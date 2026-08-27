using AppLedger.Core.Identity;
using AppLedger.Core.Process;

namespace AppLedger.Collector.Processes;

/// <summary>
/// One process instance's contribution to one second: counters as deltas, gauges as values
/// (docs/05_COLLECTOR.md §Accumulators).
/// </summary>
/// <remarks>
/// The snapshot reports cumulative totals, which are useless to a chart on their own — "this process has
/// read 40 GB since it started" says nothing about now. Turning them into per-interval deltas is the one
/// job that needs state between polls, which is why <see cref="ProcessTable"/> exists at all.
/// </remarks>
public readonly record struct ProcessDelta
{
    /// <summary>The instance this covers.</summary>
    public required ProcessKey Key { get; init; }

    /// <summary>Image file name, carried through so the snapshot step can resolve identity.</summary>
    public required string ImageName { get; init; }

    /// <summary>The logon session, for the own-session privacy filter.</summary>
    public int SessionId { get; init; }

    /// <summary>CPU percentage over the interval, 0-100, divided by logical CPU count and capped.</summary>
    public double CpuPct { get; init; }

    /// <summary>User-mode CPU milliseconds consumed during the interval.</summary>
    public long CpuUserMs { get; init; }

    /// <summary>Kernel-mode CPU milliseconds consumed during the interval.</summary>
    public long CpuKernelMs { get; init; }

    /// <summary>Private working set at the sample instant, bytes.</summary>
    public long WsPrivate { get; init; }

    /// <summary>Total working set at the sample instant, bytes.</summary>
    public long Ws { get; init; }

    /// <summary>Commit charge at the sample instant, bytes.</summary>
    public long CommitBytes { get; init; }

    /// <summary>All-kinds I/O bytes read during the interval.</summary>
    public long IoRead { get; init; }

    /// <summary>All-kinds I/O bytes written during the interval.</summary>
    public long IoWrite { get; init; }

    /// <summary>Thread count at the sample instant.</summary>
    public int Threads { get; init; }

    /// <summary>Handle count at the sample instant.</summary>
    public int Handles { get; init; }

    /// <summary>Hard page faults during the interval.</summary>
    public long HardFaults { get; init; }
}

/// <summary>A process instance appearing or disappearing between two polls.</summary>
/// <param name="Key">The instance.</param>
/// <param name="ImageName">Its image file name, which is all the snapshot knows about it.</param>
/// <param name="ParentPid">
/// The parent's PID as reported. Only a PID: it has not been matched to a live instance, and the
/// <c>createTime</c> ordering guard of docs/03_APP_IDENTITY.md §Parent adoption has not been applied.
/// </param>
public readonly record struct ProcessLifecycleEvent(ProcessKey Key, string ImageName, int ParentPid);

/// <summary>What one poll produced.</summary>
/// <param name="TsUtc">The sample instant, UTC epoch seconds.</param>
/// <param name="Deltas">One entry per instance that was alive for the whole interval.</param>
/// <param name="Started">Instances seen for the first time. They contribute no delta this tick.</param>
/// <param name="Exited">Instances that were alive last poll and are not in this snapshot.</param>
/// <param name="Rebaselined">
/// True when the interval was not trustworthy — a clock jump, or the first poll — so counters were
/// re-baselined and <paramref name="Deltas"/> is empty. The caller drops the affected bucket rather than
/// charting a spike that never happened (docs/05_COLLECTOR.md §Failure handling).
/// </param>
public readonly record struct ProcessTick(
    long TsUtc,
    IReadOnlyList<ProcessDelta> Deltas,
    IReadOnlyList<ProcessLifecycleEvent> Started,
    IReadOnlyList<ProcessLifecycleEvent> Exited,
    bool Rebaselined);
