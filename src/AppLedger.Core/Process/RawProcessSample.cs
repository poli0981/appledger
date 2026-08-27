using AppLedger.Core.Identity;

namespace AppLedger.Core.Process;

/// <summary>
/// One process as the system-wide snapshot reports it: everything
/// <c>NtQuerySystemInformation(SystemProcessInformation)</c> gives us without opening a single handle
/// (docs/04_DATA_SOURCES.md §A).
/// </summary>
/// <remarks>
/// Counters here are cumulative totals, not deltas. Turning them into per-second rates is the collector's
/// job, because only it knows the interval and can detect the clock jumps of docs/05_COLLECTOR.md
/// §Failure handling. The field names deliberately echo the struct so a reader can check them against
/// the documented semantics one for one.
/// </remarks>
public readonly record struct RawProcessSample
{
    /// <summary>The process instance. Never a bare PID (docs/03_APP_IDENTITY.md §Definitions).</summary>
    public required ProcessKey Key { get; init; }

    /// <summary>
    /// The image file name with no path, e.g. <c>chrome.exe</c>. The snapshot never carries a full path;
    /// that needs a handle. Empty for the idle process.
    /// </summary>
    public required string ImageName { get; init; }

    /// <summary>
    /// The parent's PID as reported. It is only a PID, so it must be validated against a known instance
    /// before use — Windows reuses PIDs and the parent may already have exited.
    /// </summary>
    public int ParentPid { get; init; }

    /// <summary>The logon session. The default filter keeps only the Agent's own session.</summary>
    public int SessionId { get; init; }

    /// <summary>Cumulative user-mode CPU time, in 100 ns ticks.</summary>
    public long UserTime { get; init; }

    /// <summary>Cumulative kernel-mode CPU time, in 100 ns ticks.</summary>
    public long KernelTime { get; init; }

    /// <summary>Cumulative CPU cycles. More precise than time on a CPU that changes frequency.</summary>
    public ulong CycleTime { get; init; }

    /// <summary>Private working set in bytes — what Task Manager shows as "Memory".</summary>
    public long WorkingSetPrivate { get; init; }

    /// <summary>Total working set in bytes, shared pages included.</summary>
    public long WorkingSet { get; init; }

    /// <summary>Peak total working set in bytes.</summary>
    public long PeakWorkingSet { get; init; }

    /// <summary>Commit charge in bytes — the reservation, which is what actually constrains the machine.</summary>
    public long PagefileUsage { get; init; }

    /// <summary>Peak commit charge in bytes.</summary>
    public long PeakPagefileUsage { get; init; }

    /// <summary>Open handle count.</summary>
    public int HandleCount { get; init; }

    /// <summary>Thread count.</summary>
    public int ThreadCount { get; init; }

    /// <summary>Cumulative hard page faults — the memory-pressure signal of the Processes tab.</summary>
    public long HardFaultCount { get; init; }

    /// <summary>Cumulative bytes read through any I/O: files, pipes, devices and sockets alike.</summary>
    public long ReadTransferCount { get; init; }

    /// <summary>Cumulative bytes written through any I/O.</summary>
    public long WriteTransferCount { get; init; }

    /// <summary>Cumulative bytes transferred by I/O that is neither a read nor a write.</summary>
    public long OtherTransferCount { get; init; }

    /// <summary>Cumulative read operations.</summary>
    public long ReadOperationCount { get; init; }

    /// <summary>Cumulative write operations.</summary>
    public long WriteOperationCount { get; init; }

    /// <summary>Base scheduling priority.</summary>
    public int BasePriority { get; init; }
}
