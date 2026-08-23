using AppLedger.Core.Identity;

namespace AppLedger.Core.Metrics;

/// <summary>
/// One wide row of a rollup tier: <c>metrics_1m</c>, <c>metrics_1h</c> or <c>metrics_1d</c>. The column
/// set is the storage contract of docs/06_DATA_MODEL.md, so the property names here and the SQL column
/// names are kept in step deliberately.
/// </summary>
public readonly record struct MetricRow
{
    /// <summary>The app this bucket belongs to.</summary>
    public required AppId AppId { get; init; }

    /// <summary>Bucket start, UTC epoch seconds. For the daily tier this is local midnight expressed in UTC.</summary>
    public required long Ts { get; init; }

    /// <summary>
    /// Seconds within the bucket during which at least one process of the app existed. Also the weight
    /// used when averages are combined into a coarser tier.
    /// </summary>
    public required int RuntimeSeconds { get; init; }

    /// <summary>Average live process count.</summary>
    public double Procs { get; init; }

    /// <summary>Highest live process count in the bucket.</summary>
    public int ProcsMax { get; init; }

    /// <summary>Average CPU percentage.</summary>
    public double CpuPct { get; init; }

    /// <summary>Highest CPU percentage in the bucket.</summary>
    public double CpuPctMax { get; init; }

    /// <summary>Total user-mode CPU milliseconds.</summary>
    public long CpuUserMs { get; init; }

    /// <summary>Total kernel-mode CPU milliseconds.</summary>
    public long CpuKernelMs { get; init; }

    /// <summary>Average private working set, bytes.</summary>
    public long WsPrivate { get; init; }

    /// <summary>Peak private working set, bytes.</summary>
    public long WsPrivateMax { get; init; }

    /// <summary>Average commit, bytes. Named to avoid the SQLite reserved word <c>COMMIT</c>.</summary>
    public long CommitBytes { get; init; }

    /// <summary>Average total working set, bytes.</summary>
    public long Ws { get; init; }

    /// <summary>Average GPU percentage.</summary>
    public double GpuPct { get; init; }

    /// <summary>Average dedicated VRAM, bytes.</summary>
    public long VramDedicated { get; init; }

    /// <summary>Peak dedicated VRAM, bytes.</summary>
    public long VramDedicatedMax { get; init; }

    /// <summary>Average shared VRAM, bytes.</summary>
    public long VramShared { get; init; }

    /// <summary>Total all-kinds I/O read, bytes.</summary>
    public long IoRead { get; init; }

    /// <summary>Total all-kinds I/O write, bytes.</summary>
    public long IoWrite { get; init; }

    /// <summary>Total real device read, bytes.</summary>
    public long DiskRead { get; init; }

    /// <summary>Total real device write, bytes.</summary>
    public long DiskWrite { get; init; }

    /// <summary>Total disk operations.</summary>
    public long DiskOps { get; init; }

    /// <summary>Total non-loopback bytes received.</summary>
    public long NetIn { get; init; }

    /// <summary>Total non-loopback bytes sent.</summary>
    public long NetOut { get; init; }

    /// <summary>Total loopback bytes received.</summary>
    public long NetInLoopback { get; init; }

    /// <summary>Total loopback bytes sent.</summary>
    public long NetOutLoopback { get; init; }

    /// <summary>Average thread count.</summary>
    public double Threads { get; init; }

    /// <summary>Average handle count.</summary>
    public double Handles { get; init; }

    /// <summary>Total hard page faults.</summary>
    public long HardFaults { get; init; }

    /// <summary>
    /// True when any input second or sub-bucket was flagged degraded. The chart hatches such buckets and
    /// the tooltip explains that events were lost (docs/01_ARCHITECTURE.md §Degraded modes).
    /// </summary>
    public bool Degraded { get; init; }
}
