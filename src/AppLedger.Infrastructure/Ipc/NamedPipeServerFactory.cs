using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.Security.Authorization;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Pipes;

namespace AppLedger.Infrastructure.Ipc;

/// <summary>
/// Creates the Agent's named pipe with the descriptor ADR-17 requires: a DACL for the user and
/// Administrators, and a <b>Medium mandatory integrity label</b> so the unelevated UI can connect at all.
/// </summary>
/// <remarks>
/// <b>Why this is P/Invoke and not <c>NamedPipeServerStreamAcl.Create</c>.</b> The managed
/// <c>PipeSecurity</c> model cannot express a mandatory label. It maps the SACL onto <i>audit rules</i>, and
/// an <c>ML</c> ace is not an audit ace — so <c>SetSecurityDescriptorSddlForm</c> accepts the string, drops
/// the label, and hands back a descriptor whose SACL reads as an empty <c>S:</c>. Nothing throws. The pipe
/// would then be created with no label at all, keep the creating process's High integrity by default, and
/// deny the Medium-IL UI the write access that connecting requires — the exact failure ADR-17 exists to
/// prevent, arrived at through the API that was supposed to prevent it (docs/24_ADR.md §Findings,
/// 2026-08-28).
/// <para>
/// Building the descriptor from SDDL and passing it to <c>CreateNamedPipeW</c> keeps the label, needs no
/// privilege — writing a label at or below your own integrity never does — and is atomic: there is no window
/// in which the pipe exists with the wrong label.
/// </para>
/// <para>
/// It also settles a question docs/07 left hedged. <c>PIPE_REJECT_REMOTE_CLIENTS</c> is passed explicitly
/// here rather than hoped for from the BCL's own flags.
/// </para>
/// </remarks>
public static class NamedPipeServerFactory
{
    /// <summary>
    /// SDDL for the pipe: full control for this user and for Administrators, plus a Medium mandatory label
    /// with the no-write-up policy.
    /// </summary>
    /// <remarks>
    /// <c>D:</c> is the DACL and <c>S:</c> the SACL; <c>FA</c> is full access and <c>BA</c> the
    /// Administrators alias. <c>(ML;;NW;;;ME)</c> is the label — the piece <c>PipeOptions.CurrentUserOnly</c>
    /// has no way to express, and the reason it cannot be used across this boundary.
    /// </remarks>
    public static string SddlForCurrentUser(SecurityIdentifier user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return $"D:(A;;FA;;;{user.Value})(A;;FA;;;BA)S:(ML;;NW;;;ME)";
    }

    /// <summary>The SDDL for the account this process is running as.</summary>
    public static string SddlForCurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User
            ?? throw new InvalidOperationException("The current token carries no user SID.");

        return SddlForCurrentUser(user);
    }

    /// <summary>
    /// Creates one server instance of the pipe, ready to await a connection.
    /// </summary>
    /// <param name="pipeName">The local name, without the <c>\\.\pipe\</c> prefix.</param>
    /// <param name="maxInstances">How many concurrent server instances the name allows.</param>
    /// <param name="sddl">The descriptor; defaults to <see cref="SddlForCurrentUser()"/>.</param>
    public static NamedPipeServerStream Create(string pipeName, int maxInstances, string? sddl = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(pipeName);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxInstances, 1);

        var descriptor = BuildDescriptor(sddl ?? SddlForCurrentUser());
        try
        {
            var handle = CreateHandle($@"\\.\pipe\{pipeName}", maxInstances, descriptor);

            // isConnected: false — the caller awaits WaitForConnectionAsync, exactly as it would for a
            // stream the BCL had created itself.
            return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, handle);
        }
        finally
        {
            _ = PInvoke.LocalFree(new HLOCAL(descriptor));
        }
    }

    /// <summary>
    /// Reads back the descriptor Windows actually applied to a pipe, as SDDL.
    /// </summary>
    /// <remarks>
    /// This exists because the failure it detects is silent. A pipe created without its label keeps the
    /// creating process's High integrity, an unelevated UI then cannot connect, and nothing anywhere says
    /// why — the connect simply fails with access denied. Being able to ask the pipe what it ended up with
    /// turns that into a diagnosable fact, and it is what the tests assert against.
    /// </remarks>
    public static unsafe string? ReadAppliedSddl(SafePipeHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);

        const OBJECT_SECURITY_INFORMATION DaclAndLabel =
            OBJECT_SECURITY_INFORMATION.DACL_SECURITY_INFORMATION
            | OBJECT_SECURITY_INFORMATION.LABEL_SECURITY_INFORMATION;

        if (PInvoke.GetSecurityInfo(
                handle,
                SE_OBJECT_TYPE.SE_KERNEL_OBJECT,
                DaclAndLabel,
                out _,
                out _,
                out _,
                out _,
                out var descriptor) != WIN32_ERROR.ERROR_SUCCESS)
        {
            return null;
        }

        try
        {
            if (!PInvoke.ConvertSecurityDescriptorToStringSecurityDescriptor(
                    descriptor,
                    1,
                    DaclAndLabel,
                    out var text,
                    out _))
            {
                return null;
            }

            try
            {
                return text.ToString();
            }
            finally
            {
                _ = PInvoke.LocalFree(new HLOCAL(text.Value));
            }
        }
        finally
        {
            _ = PInvoke.LocalFree(new HLOCAL(descriptor.Value));
        }
    }

    private static unsafe nint BuildDescriptor(string sddl)
    {
        if (!PInvoke.ConvertStringSecurityDescriptorToSecurityDescriptor(
                sddl,
                1,
                out var descriptor,
                out _))
        {
            throw new InvalidOperationException(
                $"The pipe security descriptor was rejected (Win32 {Marshal.GetLastWin32Error()}).");
        }

        return (nint)descriptor.Value;
    }

    private static unsafe SafePipeHandle CreateHandle(string fullName, int maxInstances, nint descriptor)
    {
        var attributes = new SECURITY_ATTRIBUTES
        {
            nLength = (uint)sizeof(SECURITY_ATTRIBUTES),
            lpSecurityDescriptor = (void*)descriptor,
            bInheritHandle = false,
        };

        var handle = PInvoke.CreateNamedPipe(
            fullName,
            FILE_FLAGS_AND_ATTRIBUTES.PIPE_ACCESS_DUPLEX | FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OVERLAPPED,
            NAMED_PIPE_MODE.PIPE_TYPE_BYTE
                | NAMED_PIPE_MODE.PIPE_READMODE_BYTE
                | NAMED_PIPE_MODE.PIPE_WAIT
                | NAMED_PIPE_MODE.PIPE_REJECT_REMOTE_CLIENTS,
            (uint)maxInstances,
            nOutBufferSize: 0,
            nInBufferSize: 0,
            nDefaultTimeOut: 0,
            attributes);

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException($"The pipe could not be created (Win32 {error}).");
        }

        // CsWin32 hands back a SafeFileHandle and NamedPipeServerStream wants a SafePipeHandle, so ownership
        // is transferred rather than the raw value shared: SetHandleAsInvalid retires the original without
        // closing it, which is what keeps this from becoming a double close. The raw value is consumed in the
        // same expression and the original stays referenced by the line after it, so there is no window in
        // which the JIT could consider it dead — the failure mode ADR-7's Finding of 2026-08-27 describes.
        var pipe = new SafePipeHandle(handle.DangerousGetHandle(), ownsHandle: true);
        handle.SetHandleAsInvalid();
        return pipe;
    }
}
