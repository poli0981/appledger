using AppLedger.Core.Identity;

namespace AppLedger.Core.Metrics;

/// <summary>
/// The three stored rollup tiers of docs/06_DATA_MODEL.md §Tiers and retention. The one-second ring is
/// memory-only and deliberately absent.
/// </summary>
public enum MetricTier
{
    /// <summary>`metrics_1m`, kept 7 days.</summary>
    Minute,

    /// <summary>`metrics_1h`, kept for the retention window.</summary>
    Hour,

    /// <summary>`metrics_1d`, kept for the retention window. `ts` is local midnight expressed in UTC.</summary>
    Day,
}

/// <summary>An app row as the collector knows it, before storage adds anything of its own.</summary>
/// <param name="AppId">The stable identity (docs/03_APP_IDENTITY.md).</param>
/// <param name="DisplayName">What the user sees.</param>
/// <param name="Source">Which resolution source won.</param>
/// <param name="Confidence">How sure the resolver was.</param>
/// <param name="FirstSeenUtc">First sighting, UTC epoch seconds.</param>
/// <param name="LastSeenUtc">Most recent sighting, UTC epoch seconds.</param>
public readonly record struct AppRecord(
    AppId AppId,
    string DisplayName,
    AppSource Source,
    double Confidence,
    long FirstSeenUtc,
    long LastSeenUtc)
{
    /// <summary>Publisher, when the PE or the catalog names one.</summary>
    public string? Publisher { get; init; }

    /// <summary>Category from the taxonomy, or `Unknown`.</summary>
    public string Category { get; init; } = "Unknown";

    /// <summary>Where the category came from: user, catalog, steam, store or none.</summary>
    public string CategorySource { get; init; } = "none";

    /// <summary>Canonical install root. Null for `sys:*` and `script:` identities.</summary>
    public string? InstallRoot { get; init; }

    /// <summary>The version currently observed.</summary>
    public string? CurrentVersion { get; init; }

    /// <summary>Authenticode signer subject, when there is one.</summary>
    public string? Signer { get; init; }

    /// <summary>The signature status, stored as its enum name.</summary>
    public SignatureStatus SignatureStatus { get; init; } = SignatureStatus.Unknown;

    /// <summary>Process tier: 2 for zero-touch, 3 for normal.</summary>
    public ProcessTierValue Tier { get; init; } = ProcessTierValue.Normal;
}

/// <summary>
/// The numeric form of the process tier as stored in <c>apps.tier</c>. Kept separate from
/// <c>Policy.ProcessTier</c> so the storage contract does not move when the policy enum does.
/// </summary>
public enum ProcessTierValue
{
    /// <summary>Zero-touch: no handle was ever opened for this app.</summary>
    ZeroTouch = 2,

    /// <summary>Normal.</summary>
    Normal = 3,
}

/// <summary>
/// Reads and writes the metric tables. Implemented over SQLite in Infrastructure; a port here so the
/// collector pipeline can be tested against an in-memory double (docs/19_TESTING.md §Layers).
/// </summary>
/// <remarks>
/// There is exactly one writer per table family (docs/06_DATA_MODEL.md §Ownership), and it is the Agent.
/// The UI opens the same file read-only. Nothing in this interface can delete a row that is not keyed by
/// app or by time range: purge and retention are separate, deliberate operations.
/// </remarks>
public interface IMetricsRepository
{
    /// <summary>
    /// Inserts or updates an app row. <c>first_seen_utc</c> is preserved on update: an app is first seen
    /// once, and overwriting it would silently rewrite history.
    /// </summary>
    Task UpsertAppAsync(AppRecord app, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a batch of rows into one tier, replacing any row with the same <c>(app_id, ts)</c>. A rollup
    /// that runs twice for the same bucket must produce the same table, not two rows.
    /// </summary>
    Task WriteRowsAsync(MetricTier tier, IReadOnlyList<MetricRow> rows, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one app's rows in a half-open time range, ordered by <c>ts</c>. This is the query every chart
    /// in the UI is built on (docs/06_DATA_MODEL.md §Query patterns).
    /// </summary>
    Task<IReadOnlyList<MetricRow>> ReadRangeAsync(
        AppId appId,
        MetricTier tier,
        long fromTsUtc,
        long toTsUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes one minute of the Agent's own cost and the collector's quiet losses, replacing any row for the
    /// same minute (docs/06_DATA_MODEL.md <c>health_minutes</c>, docs/15_LOGGING.md §Agent self-watch).
    /// </summary>
    /// <remarks>
    /// This is the durable half of the three health cadences, and the one S1 reads back after a 48-hour run.
    /// Measuring the Agent through the mechanism it ships with is the point: a separate measuring path can
    /// be right about a build that is wrong.
    /// </remarks>
    Task WriteHealthAsync(HealthMinute minute, CancellationToken cancellationToken = default);
}

/// <summary>One minute of Agent health, as stored (docs/06_DATA_MODEL.md <c>health_minutes</c>).</summary>
/// <param name="TsUtc">The minute this describes, UTC epoch seconds.</param>
/// <param name="AgentCpuPct">The hosting process's CPU over that minute, 0-100.</param>
/// <param name="AgentWs">The hosting process's private working set, bytes.</param>
/// <param name="EventsLost">Events the sensors reported losing, cumulative.</param>
/// <param name="SensorsJson">
/// Sensor states as JSON, so a new sensor needs no migration. Never a path or a hostname: this row is
/// readable by anything that can open the database.
/// </param>
public readonly record struct HealthMinute(
    long TsUtc,
    double AgentCpuPct,
    long AgentWs,
    long EventsLost,
    string? SensorsJson);
