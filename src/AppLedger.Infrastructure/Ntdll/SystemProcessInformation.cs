using System.Runtime.InteropServices;

namespace AppLedger.Infrastructure.Ntdll;

/// <summary>
/// A counted UTF-16 string as the native API returns it. Sixteen bytes on both 64-bit architectures:
/// two <c>USHORT</c>s, four bytes of alignment padding, then a pointer.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct UnicodeString
{
    /// <summary>Length in bytes, not characters, and not including a terminator.</summary>
    internal ushort Length;

    /// <summary>Capacity in bytes.</summary>
    internal ushort MaximumLength;

    /// <summary>Pointer to the characters. Null for the idle process.</summary>
    internal nint Buffer;
}

/// <summary>
/// <c>SYSTEM_PROCESS_INFORMATION</c> as returned by
/// <c>NtQuerySystemInformation(SystemProcessInformation)</c>.
/// </summary>
/// <remarks>
/// Hand-written rather than generated: the structure is not in the Win32 metadata CsWin32 reads, and
/// pulling in WDK metadata for one struct would be a heavier dependency than writing it out
/// (docs/17_BUILD.md §CsWin32). The layout is identical on x64 and ARM64 — both are LP64 with 8-byte
/// alignment — and <c>SystemProcessInformationLayoutTests</c> asserts every offset rather than trusting
/// that sentence.
/// <para>
/// Thread entries follow each process entry in the buffer. We never parse them: skipping straight to
/// <see cref="NextEntryOffset"/> is what keeps the 1 Hz poll cheap (docs/04_DATA_SOURCES.md §A).
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct SystemProcessInformation
{
    /// <summary>Bytes from this entry to the next, or zero at the end of the list.</summary>
    internal uint NextEntryOffset;

    /// <summary>Number of thread entries following this one.</summary>
    internal uint NumberOfThreads;

    /// <summary>Private working set in bytes. Occupies the field older headers call <c>SpareLi1</c>.</summary>
    internal long WorkingSetPrivateSize;

    /// <summary>Cumulative hard page faults.</summary>
    internal uint HardFaultCount;

    /// <summary>Highest thread count reached.</summary>
    internal uint NumberOfThreadsHighWatermark;

    /// <summary>Cumulative CPU cycles.</summary>
    internal ulong CycleTime;

    /// <summary>Creation time as a FILETIME. Half of the process key.</summary>
    internal long CreateTime;

    /// <summary>Cumulative user-mode time in 100 ns ticks.</summary>
    internal long UserTime;

    /// <summary>Cumulative kernel-mode time in 100 ns ticks.</summary>
    internal long KernelTime;

    /// <summary>The image file name, with no path.</summary>
    internal UnicodeString ImageName;

    /// <summary>Base scheduling priority.</summary>
    internal int BasePriority;

    /// <summary>The PID, widened to pointer size by the native API.</summary>
    internal nint UniqueProcessId;

    /// <summary>The parent's PID. Only a PID: it may already have been reused.</summary>
    internal nint InheritedFromUniqueProcessId;

    /// <summary>Open handle count.</summary>
    internal uint HandleCount;

    /// <summary>Logon session id.</summary>
    internal uint SessionId;

    /// <summary>Opaque process key. Unused.</summary>
    internal nuint UniqueProcessKey;

    /// <summary>Peak virtual size in bytes.</summary>
    internal nuint PeakVirtualSize;

    /// <summary>Virtual size in bytes.</summary>
    internal nuint VirtualSize;

    /// <summary>Cumulative page faults of every kind.</summary>
    internal uint PageFaultCount;

    /// <summary>Peak working set in bytes.</summary>
    internal nuint PeakWorkingSetSize;

    /// <summary>Working set in bytes, shared pages included.</summary>
    internal nuint WorkingSetSize;

    /// <summary>Peak paged pool quota.</summary>
    internal nuint QuotaPeakPagedPoolUsage;

    /// <summary>Paged pool quota.</summary>
    internal nuint QuotaPagedPoolUsage;

    /// <summary>Peak non-paged pool quota.</summary>
    internal nuint QuotaPeakNonPagedPoolUsage;

    /// <summary>Non-paged pool quota.</summary>
    internal nuint QuotaNonPagedPoolUsage;

    /// <summary>Commit charge in bytes.</summary>
    internal nuint PagefileUsage;

    /// <summary>Peak commit charge in bytes.</summary>
    internal nuint PeakPagefileUsage;

    /// <summary>Private page count.</summary>
    internal nuint PrivatePageCount;

    /// <summary>Cumulative read operations.</summary>
    internal long ReadOperationCount;

    /// <summary>Cumulative write operations.</summary>
    internal long WriteOperationCount;

    /// <summary>Cumulative operations that are neither reads nor writes.</summary>
    internal long OtherOperationCount;

    /// <summary>Cumulative bytes read, across files, pipes, devices and sockets.</summary>
    internal long ReadTransferCount;

    /// <summary>Cumulative bytes written.</summary>
    internal long WriteTransferCount;

    /// <summary>Cumulative bytes transferred by other operations.</summary>
    internal long OtherTransferCount;
}
