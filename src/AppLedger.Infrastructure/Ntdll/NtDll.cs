using System.Runtime.InteropServices;

namespace AppLedger.Infrastructure.Ntdll;

/// <summary>
/// The two <c>ntdll</c> entry points AppLedger calls directly.
/// </summary>
/// <remarks>
/// Both are absent from the Win32 metadata CsWin32 reads, so docs/17_BUILD.md §CsWin32 sanctions writing
/// them by hand here — the single exception to "no <c>DllImport</c> outside Infrastructure"
/// (<c>CLAUDE.md</c> §Conventions). Both are pure queries: neither writes to a process, and
/// <c>NtQueryInformationProcess</c> is only ever called with
/// <see cref="ProcessCommandLineInformation"/> on a handle opened with
/// <c>PROCESS_QUERY_LIMITED_INFORMATION</c>.
/// </remarks>
internal static unsafe partial class NtDll
{
    /// <summary>The buffer was too small; <c>ReturnLength</c> holds the size actually needed.</summary>
    internal const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    /// <summary>The caller may not read this. Expected for PPL processes; never an error we log loudly.</summary>
    internal const int StatusAccessDenied = unchecked((int)0xC0000022);

    /// <summary>The information class is not implemented on this build of Windows.</summary>
    internal const int StatusNotImplemented = unchecked((int)0xC0000002);

    /// <summary>The process exited between the snapshot and the query.</summary>
    internal const int StatusInvalidHandle = unchecked((int)0xC0000008);

    /// <summary><c>SystemProcessInformation</c>: every process with its counters, in one call.</summary>
    internal const int SystemProcessInformation = 5;

    /// <summary>
    /// <c>ProcessCommandLineInformation</c>: the command line as a <see cref="UnicodeString"/> followed by
    /// its characters. Windows 8.1 and later, and readable with limited rights — which is exactly why we
    /// use it instead of reading the PEB (docs/04_DATA_SOURCES.md §B).
    /// </summary>
    internal const int ProcessCommandLineInformation = 60;

    /// <summary>True for the <c>STATUS_SUCCESS</c> family.</summary>
    internal static bool Succeeded(int status) => status >= 0;

    [LibraryImport("ntdll.dll")]
    internal static partial int NtQuerySystemInformation(
        int systemInformationClass,
        void* systemInformation,
        uint systemInformationLength,
        out uint returnLength);

    // The handle is taken as a SafeHandle rather than an nint on purpose. With a raw handle the caller has
    // to keep the owning SafeHandle alive across the call itself, and forgetting to is invisible: the JIT
    // may treat the variable as dead the moment DangerousGetHandle returns, letting the finalizer close the
    // process handle while ntdll is still using it. The generated marshalling does the AddRef/Release.
    [LibraryImport("ntdll.dll")]
    internal static partial int NtQueryInformationProcess(
        SafeHandle processHandle,
        int processInformationClass,
        void* processInformation,
        uint processInformationLength,
        out uint returnLength);
}
