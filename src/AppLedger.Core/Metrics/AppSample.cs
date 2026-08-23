using AppLedger.Core.Identity;

namespace AppLedger.Core.Metrics;

/// <summary>
/// One second of one app: what <c>SnapshotBuilder</c> publishes at 1 Hz after summing every live process
/// instance of the app (docs/05_COLLECTOR.md §Accumulators). Counters are already deltas for the second;
/// gauges are the value at the sample instant.
/// </summary>
/// <remarks>
/// The semantics of each field are the contract of docs/04_DATA_SOURCES.md and are surfaced to the user
/// in a tooltip (FR-20), so renaming or repurposing one is a user-visible change.
/// </remarks>
public readonly record struct AppSample
{
    /// <summary>The app this second belongs to.</summary>
    public required AppId AppId { get; init; }

    /// <summary>Sample instant, UTC epoch seconds.</summary>
    public required long TsUtc { get; init; }

    /// <summary>Live process instances of this app at the sample instant.</summary>
    public int Procs { get; init; }

    /// <summary>CPU percentage, 0-100, Task Manager convention (capped, divided by logical CPU count).</summary>
    public double CpuPct { get; init; }

    /// <summary>User-mode CPU milliseconds consumed during this second.</summary>
    public long CpuUserMs { get; init; }

    /// <summary>Kernel-mode CPU milliseconds consumed during this second.</summary>
    public long CpuKernelMs { get; init; }

    /// <summary>Private working set in bytes — what Task Manager calls "Memory".</summary>
    public long WsPrivate { get; init; }

    /// <summary>Commit (private bytes) in bytes.</summary>
    public long CommitBytes { get; init; }

    /// <summary>Total working set in bytes, including shared pages.</summary>
    public long Ws { get; init; }

    /// <summary>Highest GPU engine utilization across engines, 0-100 (Task Manager convention).</summary>
    public double GpuPct { get; init; }

    /// <summary>Dedicated video memory in bytes.</summary>
    public long VramDedicated { get; init; }

    /// <summary>Shared video memory in bytes.</summary>
    public long VramShared { get; init; }

    /// <summary>All-kinds I/O read bytes this second (files, pipes, devices, sockets).</summary>
    public long IoRead { get; init; }

    /// <summary>All-kinds I/O write bytes this second.</summary>
    public long IoWrite { get; init; }

    /// <summary>Real device read bytes this second, from ETW DiskIO.</summary>
    public long DiskRead { get; init; }

    /// <summary>Real device write bytes this second, from ETW DiskIO.</summary>
    public long DiskWrite { get; init; }

    /// <summary>Disk operations this second.</summary>
    public long DiskOps { get; init; }

    /// <summary>Non-loopback network payload bytes received this second.</summary>
    public long NetIn { get; init; }

    /// <summary>Non-loopback network payload bytes sent this second.</summary>
    public long NetOut { get; init; }

    /// <summary>Loopback payload bytes received this second, counted separately from "internet" totals.</summary>
    public long NetInLoopback { get; init; }

    /// <summary>Loopback payload bytes sent this second.</summary>
    public long NetOutLoopback { get; init; }

    /// <summary>Thread count across the app's instances.</summary>
    public int Threads { get; init; }

    /// <summary>Handle count across the app's instances.</summary>
    public int Handles { get; init; }

    /// <summary>Hard page faults this second.</summary>
    public long HardFaults { get; init; }

    /// <summary>
    /// True when a sensor reported lost events for this second. It propagates into the rollup row so the
    /// chart can hatch the affected bucket instead of drawing a dip that never happened.
    /// </summary>
    public bool Degraded { get; init; }
}
