using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using AppLedger.Core.Identity;
using AppLedger.Core.Policy;
using AppLedger.Infrastructure.Ntdll;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.SystemInformation;
using Windows.Win32.System.Threading;

namespace AppLedger.Infrastructure.Process;

/// <summary>
/// Fills in the half of a process's identity that needs a handle — image path, command line, package,
/// token and architecture — with exactly one <c>OpenProcess</c> per instance, closed immediately
/// (docs/04_DATA_SOURCES.md §B).
/// </summary>
/// <remarks>
/// <b>The rights mask is the whole safety story.</b> <c>PROCESS_QUERY_LIMITED_INFORMATION</c> is the only
/// right AppLedger ever requests, anywhere; every other member of the enum is banned in
/// <c>BannedSymbols.txt</c>, and a repository guard asserts the constant at this call site. A Tier-2
/// process is refused before the call is even reached (docs/11_SAFETY_POLICY.md §Process access tiers).
/// </remarks>
public sealed class ProcessEnricher : IProcessEnricher
{
    private const int MaxCommandLineBytes = 64 * 1024;

    /// <summary>
    /// <c>APPMODEL_ERROR_NO_PACKAGE</c>. Not an error: it is how Windows says "this process is not MSIX".
    /// </summary>
    private const WIN32_ERROR NoPackage = (WIN32_ERROR)15700;

    private readonly IProcessCounter? _counter;

    /// <summary>Creates an enricher.</summary>
    /// <param name="counter">
    /// Optional observer notified of every handle open. It exists so the zero-touch test can assert that a
    /// Tier-2 process produces no opens at all, rather than inferring it from null fields.
    /// </param>
    public ProcessEnricher(IProcessCounter? counter = null) => _counter = counter;

    /// <inheritdoc />
    public ProcessEnrichment Enrich(ProcessKey key, ProcessTier tier)
    {
        // Zero-touch means zero touch. Not reduced rights, not one careful call: nothing.
        if (tier == ProcessTier.ZeroTouch)
        {
            return ProcessEnrichment.Unavailable;
        }

        using var handle = Open(key.Pid);
        if (handle is null)
        {
            return ProcessEnrichment.Unavailable;
        }

        // PID reuse guard: the snapshot named an instance, and Windows may have handed the PID to a
        // different process since. Enriching the wrong one would attach a stranger's command line to an app.
        if (!CreateTimeMatches(handle, key.CreateTime))
        {
            return ProcessEnrichment.Unavailable;
        }

        var (sid, userName) = ReadTokenUser(handle);

        return new ProcessEnrichment
        {
            Attempted = true,
            ImagePath = ReadImagePath(handle),
            CommandLine = ReadCommandLine(handle),
            PackageFamilyName = ReadPackageFamilyName(handle),
            UserSid = sid,
            UserName = userName,
            Integrity = ReadIntegrity(handle),
            Elevated = ReadElevation(handle),
            Architecture = ReadArchitecture(handle),
        };
    }

    private SafeFileHandle? Open(int pid)
    {
        _counter?.OnOpenProcess(pid);

        var handle = PInvoke.OpenProcess(
            PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION,
            bInheritHandle: false,
            (uint)pid);

        return handle.IsNull ? null : new SafeFileHandle(handle, ownsHandle: true);
    }

    private static bool CreateTimeMatches(SafeFileHandle handle, long expected)
    {
        if (!PInvoke.GetProcessTimes(handle, out var creation, out _, out _, out _))
        {
            return false;
        }

        var actual = ((long)creation.dwHighDateTime << 32) | (uint)creation.dwLowDateTime;
        return actual == expected;
    }

    private static unsafe string? ReadImagePath(SafeFileHandle handle)
    {
        Span<char> buffer = stackalloc char[1024];
        var size = (uint)buffer.Length;

        // PROCESS_NAME_WIN32 asks for the DOS form directly, so no device-path mapping is needed here.
        if (!PInvoke.QueryFullProcessImageName(handle, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buffer, ref size))
        {
            return null;
        }

        return size == 0 ? null : new string(buffer[..(int)size]);
    }

    /// <summary>
    /// Reads the command line through <c>ProcessCommandLineInformation</c>, which works with limited
    /// rights. A PPL process answers <c>STATUS_ACCESS_DENIED</c>, which is a null result, not an error.
    /// </summary>
    private static unsafe string? ReadCommandLine(SafeFileHandle handle)
    {
        var size = 4096u;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var buffer = new byte[size];
            int status;
            uint returned;

            fixed (byte* p = buffer)
            {
                status = NtDll.NtQueryInformationProcess(
                    handle.DangerousGetHandle(), NtDll.ProcessCommandLineInformation, p, size, out returned);

                if (NtDll.Succeeded(status))
                {
                    var text = *(UnicodeString*)p;
                    return text.Buffer == 0 || text.Length == 0
                        ? null
                        : new string((char*)text.Buffer, 0, text.Length / sizeof(char));
                }
            }

            if (status != NtDll.StatusInfoLengthMismatch || returned == 0 || returned > MaxCommandLineBytes)
            {
                return null;
            }

            size = returned;
        }

        return null;
    }

    private static string? ReadPackageFamilyName(SafeFileHandle handle)
    {
        uint length = 0;
        var error = PInvoke.GetPackageFullName(handle, ref length);

        if (error == NoPackage || length == 0)
        {
            return null;
        }

        Span<char> fullName = stackalloc char[(int)length];
        if (PInvoke.GetPackageFullName(handle, ref length, fullName) != WIN32_ERROR.ERROR_SUCCESS)
        {
            return null;
        }

        var full = new string(fullName[..NullTerminated(fullName)]);

        uint familyLength = 0;
        if (PInvoke.PackageFamilyNameFromFullName(full, ref familyLength) == NoPackage || familyLength == 0)
        {
            return null;
        }

        Span<char> family = stackalloc char[(int)familyLength];
        return PInvoke.PackageFamilyNameFromFullName(full, ref familyLength, family) == WIN32_ERROR.ERROR_SUCCESS
            ? new string(family[..NullTerminated(family)])
            : null;
    }

    private static (string? Sid, string? Name) ReadTokenUser(SafeFileHandle process)
    {
        using var token = OpenToken(process);
        if (token is null)
        {
            return (null, null);
        }

        var buffer = QueryToken(token, TOKEN_INFORMATION_CLASS.TokenUser);
        if (buffer is null)
        {
            return (null, null);
        }

        try
        {
            // TOKEN_USER is a SID_AND_ATTRIBUTES: a pointer to the SID, then its attributes.
            var sidPointer = MemoryMarshal.Read<nint>(buffer);
            if (sidPointer == 0)
            {
                return (null, null);
            }

            var identifier = new SecurityIdentifier(sidPointer);
            string? name = null;
            try
            {
                name = ((NTAccount)identifier.Translate(typeof(NTAccount))).Value;
            }
            catch (IdentityNotMappedException)
            {
                // A deleted or remote account. The SID is still useful; the name is not available.
            }
            catch (SystemException)
            {
            }

            return (identifier.Value, name);
        }
        catch (ArgumentException)
        {
            return (null, null);
        }
    }

    private static IntegrityLevel ReadIntegrity(SafeFileHandle process)
    {
        using var token = OpenToken(process);
        if (token is null)
        {
            return IntegrityLevel.Unknown;
        }

        var buffer = QueryToken(token, TOKEN_INFORMATION_CLASS.TokenIntegrityLevel);
        if (buffer is null)
        {
            return IntegrityLevel.Unknown;
        }

        try
        {
            var sidPointer = MemoryMarshal.Read<nint>(buffer);
            if (sidPointer == 0)
            {
                return IntegrityLevel.Unknown;
            }

            // The level is the last sub-authority of the mandatory label SID (S-1-16-<rid>).
            var value = new SecurityIdentifier(sidPointer).Value;
            var lastDash = value.LastIndexOf('-');
            if (lastDash < 0 || !int.TryParse(value[(lastDash + 1)..], CultureInfo.InvariantCulture, out var rid))
            {
                return IntegrityLevel.Unknown;
            }

            return rid switch
            {
                >= 0x4000 => IntegrityLevel.System,
                >= 0x3000 => IntegrityLevel.High,
                >= 0x2000 => IntegrityLevel.Medium,
                >= 0x1000 => IntegrityLevel.Low,
                _ => IntegrityLevel.Untrusted,
            };
        }
        catch (ArgumentException)
        {
            return IntegrityLevel.Unknown;
        }
    }

    private static bool? ReadElevation(SafeFileHandle process)
    {
        using var token = OpenToken(process);
        if (token is null)
        {
            return null;
        }

        var buffer = QueryToken(token, TOKEN_INFORMATION_CLASS.TokenElevation);
        return buffer is null || buffer.Length < sizeof(uint) ? null : MemoryMarshal.Read<uint>(buffer) != 0;
    }

    private static string? ReadArchitecture(SafeFileHandle handle)
    {
        if (!PInvoke.IsWow64Process2(handle, out var processMachine, out var nativeMachine))
        {
            return null;
        }

        // UNKNOWN means "not running under emulation", so the process's architecture is the machine's.
        var machine = processMachine == IMAGE_FILE_MACHINE.IMAGE_FILE_MACHINE_UNKNOWN ? nativeMachine : processMachine;

        return machine switch
        {
            IMAGE_FILE_MACHINE.IMAGE_FILE_MACHINE_AMD64 => "x64",
            IMAGE_FILE_MACHINE.IMAGE_FILE_MACHINE_I386 => "x86",
            IMAGE_FILE_MACHINE.IMAGE_FILE_MACHINE_ARM64 => "ARM64",
            IMAGE_FILE_MACHINE.IMAGE_FILE_MACHINE_ARMNT => "ARM",
            _ => null,
        };
    }

    private static SafeFileHandle? OpenToken(SafeFileHandle process) =>
        PInvoke.OpenProcessToken(process, TOKEN_ACCESS_MASK.TOKEN_QUERY, out var token) ? token : null;

    private static byte[]? QueryToken(SafeFileHandle token, TOKEN_INFORMATION_CLASS informationClass)
    {
        PInvoke.GetTokenInformation(token, informationClass, Span<byte>.Empty, out var required);
        if (required == 0 || required > 64 * 1024)
        {
            return null;
        }

        var buffer = new byte[required];
        return PInvoke.GetTokenInformation(token, informationClass, buffer, out _) ? buffer : null;
    }

    private static int NullTerminated(ReadOnlySpan<char> text)
    {
        var end = text.IndexOf('\0');
        return end < 0 ? text.Length : end;
    }
}

/// <summary>
/// Observes handle opens so a test can prove the zero-touch rule rather than infer it.
/// </summary>
/// <remarks>
/// docs/11_SAFETY_POLICY.md §Tests asks for exactly this: "the enrichment adapter must record zero
/// <c>OpenProcess</c> calls for it (counting mock)". A property that important deserves a seam, not a
/// comment.
/// </remarks>
public interface IProcessCounter
{
    /// <summary>Called immediately before every <c>OpenProcess</c>, successful or not.</summary>
    void OnOpenProcess(int pid);
}
