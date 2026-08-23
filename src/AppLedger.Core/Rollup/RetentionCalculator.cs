namespace AppLedger.Core.Rollup;

/// <summary>The storage tiers the nightly retention job prunes (docs/06_DATA_MODEL.md §Tiers and retention).</summary>
public enum RetentionTier
{
    /// <summary>Minute rows. Always seven days, regardless of the configured retention.</summary>
    Metrics1M,

    /// <summary>Hourly rows. Kept for the configured retention.</summary>
    Metrics1H,

    /// <summary>Daily rows. Kept for the configured retention.</summary>
    Metrics1D,

    /// <summary>Per-app per-day host rows. Kept for the configured retention.</summary>
    NetHostsDaily,

    /// <summary>Disk footprint snapshots. Kept for the configured retention.</summary>
    DiskSnapshots,

    /// <summary>Events. Kept for the configured retention.</summary>
    Events,

    /// <summary>Daily usage. Kept for the configured retention.</summary>
    UsageDaily,

    /// <summary>Agent health samples. Kept for the configured retention.</summary>
    HealthMinutes,

    /// <summary>Process instances. Always thirty days; only counts survive, through <c>usage_daily</c>.</summary>
    ProcessInstances,
}

/// <summary>
/// Works out what the nightly purge deletes. The fixed floors matter: a user who sets retention to a year
/// still does not accumulate a year of minute rows or of command lines, because those are the two most
/// sensitive and largest tiers (docs/12_PRIVACY_AND_RETENTION.md §Defaults).
/// </summary>
public static class RetentionCalculator
{
    /// <summary>Default retention in days.</summary>
    public const int DefaultDays = 180;

    /// <summary>Shortest retention a user may configure.</summary>
    public const int MinimumDays = 30;

    /// <summary>Longest retention a user may configure.</summary>
    public const int MaximumDays = 365;

    /// <summary>Minute rows never survive longer than this, whatever the retention setting says.</summary>
    public const int Metrics1MDays = 7;

    /// <summary>Process instances, and therefore command lines, never survive longer than this.</summary>
    public const int ProcessInstanceDays = 30;

    /// <summary>Clamps a configured value into the supported range.</summary>
    public static int ClampDays(int days) => Math.Clamp(days, MinimumDays, MaximumDays);

    /// <summary>How many days of data a tier keeps under the given retention setting.</summary>
    public static int DaysFor(RetentionTier tier, int retentionDays) => tier switch
    {
        RetentionTier.Metrics1M => Metrics1MDays,
        RetentionTier.ProcessInstances => ProcessInstanceDays,
        _ => ClampDays(retentionDays),
    };

    /// <summary>
    /// The exclusive cutoff for a tier: rows whose timestamp is strictly older are deleted. Expressed in
    /// UTC epoch seconds, matching every <c>ts</c> and <c>*_utc</c> column.
    /// </summary>
    public static long CutoffUtc(RetentionTier tier, int retentionDays, long nowUtcSeconds) =>
        nowUtcSeconds - ((long)DaysFor(tier, retentionDays) * 86400);

    /// <summary>
    /// The cutoff for the day-keyed tables, as the <c>yyyyMMdd</c> value below which rows are deleted.
    /// Computed in local time because those columns are local calendar days.
    /// </summary>
    public static int CutoffDay(RetentionTier tier, int retentionDays, long nowUtcSeconds, TimeZoneInfo timeZone) =>
        Time.DayBucket.ToLocalDay(CutoffUtc(tier, retentionDays, nowUtcSeconds), timeZone);

    /// <summary>Every tier a full nightly pass touches, with its cutoff.</summary>
    public static IReadOnlyList<(RetentionTier Tier, long CutoffUtc)> NightlyPlan(int retentionDays, long nowUtcSeconds)
    {
        var tiers = Enum.GetValues<RetentionTier>();
        var plan = new List<(RetentionTier, long)>(tiers.Length);
        foreach (var tier in tiers)
        {
            plan.Add((tier, CutoffUtc(tier, retentionDays, nowUtcSeconds)));
        }

        return plan;
    }
}
