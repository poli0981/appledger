using AppLedger.Core.Identity;

namespace AppLedger.Collector.Snapshots;

/// <summary>
/// One process instance's contribution to one second from every sensor that is not the process poller —
/// ETW network and device I/O, and the GPU counters (docs/05_COLLECTOR.md §Accumulators, step 2).
/// </summary>
/// <remarks>
/// The process poller can only see what a process reports about itself: CPU, working set, all-kinds I/O.
/// Real device reads, network payload bytes and GPU engine time come from elsewhere and arrive keyed by the
/// same <see cref="ProcessKey"/>, which is what makes them joinable at all.
/// </remarks>
public readonly record struct InstanceExtras
{
    /// <summary>Real device read bytes this second, from ETW DiskIO.</summary>
    public long DiskRead { get; init; }

    /// <summary>Real device write bytes this second.</summary>
    public long DiskWrite { get; init; }

    /// <summary>Device operations this second, reads and writes together.</summary>
    public long DiskOps { get; init; }

    /// <summary>Non-loopback network payload bytes received this second.</summary>
    public long NetIn { get; init; }

    /// <summary>Non-loopback network payload bytes sent this second.</summary>
    public long NetOut { get; init; }

    /// <summary>Loopback payload bytes received, counted apart from the internet totals.</summary>
    public long NetInLoopback { get; init; }

    /// <summary>Loopback payload bytes sent.</summary>
    public long NetOutLoopback { get; init; }

    /// <summary>Highest GPU engine utilization, 0-100.</summary>
    public double GpuPct { get; init; }

    /// <summary>Dedicated video memory in bytes.</summary>
    public long VramDedicated { get; init; }

    /// <summary>Shared video memory in bytes.</summary>
    public long VramShared { get; init; }
}

/// <summary>
/// Supplies <see cref="InstanceExtras"/> for the second being built, keyed by process instance.
/// </summary>
/// <remarks>
/// This port exists so <see cref="SnapshotBuilder"/> can stay what it is — per-app arithmetic over plain
/// numbers — while the sensors it sums live behind ETW callbacks, a PDH query and a PID map. The builder's
/// tests need no accumulators, no threads and no clock; the join's tests need no identity resolver.
/// <para>
/// <see cref="TryTake"/> is deliberately destructive. Reading and zeroing in one step is what removes the
/// gap a separate reset would leave between the two, and the events that fall into that gap are exactly the
/// ones from the busiest processes.
/// </para>
/// </remarks>
public interface IInstanceMetricsSource
{
    /// <summary>
    /// Takes one instance's sensor totals for this second, zeroing them. <paramref name="extras"/> is always
    /// assigned; the return value says whether anything was actually there, and is advisory.
    /// </summary>
    bool TryTake(ProcessKey key, out InstanceExtras extras);

    /// <summary>
    /// The last bytes of instances that exited during this tick, already resolved to their app. They have no
    /// <c>ProcessDelta</c> to hang from, so this is the only place they can be recovered.
    /// </summary>
    IReadOnlyList<(AppId AppId, InstanceExtras Extras)> DrainExited();

    /// <summary>
    /// True when a sensor lost events or a handler threw inside this window, which makes every sample of the
    /// second <c>degraded</c> so the chart hatches the bucket instead of drawing a dip that never happened.
    /// </summary>
    bool DegradedWindow { get; }

    /// <summary>
    /// The null object, so the builder's pure path needs no null check and Lite mode — which constructs no
    /// ETW hub at all — is not a special case.
    /// </summary>
    public static IInstanceMetricsSource None { get; } = new NoInstanceMetrics();

    private sealed class NoInstanceMetrics : IInstanceMetricsSource
    {
        public bool DegradedWindow => false;

        public bool TryTake(ProcessKey key, out InstanceExtras extras)
        {
            extras = default;
            return false;
        }

        public IReadOnlyList<(AppId AppId, InstanceExtras Extras)> DrainExited() => [];
    }
}
