namespace AppLedger.Core.Collection;

/// <summary>Which privileges the collector is running with.</summary>
public enum CollectorPrivilege
{
    /// <summary>
    /// Lite mode inside the UI: no ETW, no USN, no history persistence, own user only
    /// (docs/01_ARCHITECTURE.md §Lite mode). It exists so the first run never dead-ends on a UAC prompt.
    /// </summary>
    Standard,

    /// <summary>The Agent: every sensor, and the only writer of history.</summary>
    Elevated,
}

/// <summary>
/// The collector's tunables, every one of which is a budget knob (docs/05_COLLECTOR.md §Budget controls).
/// </summary>
/// <remarks>
/// <b>Memory is the constraint, not CPU.</b> S1-lite measured a ~75 MB floor for the two ETW sessions alone
/// against a 100 MB budget, leaving roughly 20 MB for everything on this page plus the SQLite page cache.
/// Every default here was chosen against that number rather than against comfort.
/// </remarks>
public sealed record CollectorOptions
{
    /// <summary>What the host may do.</summary>
    public CollectorPrivilege Privilege { get; init; } = CollectorPrivilege.Elevated;

    /// <summary>How often the process snapshot is taken while a UI is attached.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The slower interval used once nothing is watching. Halves the idle cost.</summary>
    public TimeSpan IdlePollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How long without a connected UI before the idle profile is adopted.</summary>
    public TimeSpan IdleAfter { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How much per-second detail is kept in memory.
    /// </summary>
    /// <remarks>
    /// docs/05 originally said one hour, and gave "~2 MB for 100 apps" as its size — but a measured
    /// <c>AppSample</c> is 184 bytes, so an hour of 100 apps is <b>66 MB</b>, a third of the entire Agent
    /// budget. The 2 MB figure is the one that is right: it corresponds to about a minute, which is also
    /// exactly what the UI asks of the ring (docs/08 §Pages: 60-second sparklines; the History page's "1 h"
    /// range auto-picks the <c>metrics_1m</c> tier instead).
    /// <para>
    /// Five minutes is that minute plus headroom, and it covers the window that has not been rolled up yet,
    /// so a UI attaching mid-minute still sees continuous data. Anything longer comes from SQLite.
    /// </para>
    /// </remarks>
    public TimeSpan RingWindow { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The shorter ring kept once idle, since nothing is drawing charts.</summary>
    public TimeSpan IdleRingWindow { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How many published snapshots the live channel holds before dropping the oldest. Live streams are
    /// allowed to drop; rollup inputs never are (docs/01_ARCHITECTURE.md §Backpressure).
    /// </summary>
    public int LiveChannelCapacity { get; init; } = 10;

    /// <summary>
    /// Keep only the collector's own logon session, which is the privacy default of
    /// docs/12_PRIVACY_AND_RETENTION.md. Turning it off is a user decision, never ours.
    /// </summary>
    public bool OwnSessionOnly { get; init; } = true;

    /// <summary>Lite mode: no ETW, no history, own user only.</summary>
    public static CollectorOptions Lite { get; } = new()
    {
        Privilege = CollectorPrivilege.Standard,
        RingWindow = TimeSpan.FromMinutes(1),
    };

    /// <summary>True when this configuration persists history. Lite mode does not.</summary>
    public bool PersistsHistory => Privilege == CollectorPrivilege.Elevated;
}
