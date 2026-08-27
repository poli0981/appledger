using AppLedger.Collector.Processes;
using AppLedger.Core.Identity;
using AppLedger.Core.Metrics;

namespace AppLedger.Collector.Storage;

/// <summary>
/// Owns the <c>apps</c> table: makes sure a row exists for every app before a <c>metrics_1m</c> row
/// references it.
/// </summary>
/// <remarks>
/// <b>This exists to close a foreign key that nothing else was holding.</b> <c>metrics_1m.app_id</c>
/// references <c>apps(app_id)</c>, and <c>foreign_keys=ON</c> is set on every Agent connection
/// (docs/06_DATA_MODEL.md §Pragmas), so writing a metrics row for an unregistered app is not a
/// data-quality problem — it throws. Neither the rollup's tests nor the repository's tests could catch it:
/// one stops at the <c>MetricRow</c>, the other seeds the app itself.
/// <para>
/// So the guarantee is made twice. Apps are registered when their first instance is resolved, which is
/// normally a minute before their first row; and <see cref="EnsureForRowsAsync"/> re-checks immediately
/// before the write, so a missed registration cannot become a failed transaction.
/// </para>
/// </remarks>
public sealed class AppRegistrar
{
    private readonly IMetricsRepository _repository;
    private readonly Dictionary<AppId, AppRecord> _known = [];

    /// <summary>Creates a registrar over the repository that owns the table.</summary>
    public AppRegistrar(IMetricsRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
    }

    /// <summary>How many apps have been written so far this session.</summary>
    public int KnownApps => _known.Count;

    /// <summary>
    /// Registers apps for instances that were just resolved. An app already written this session is
    /// touched rather than rewritten, so a machine with churning short-lived processes does not turn into
    /// a write per process.
    /// </summary>
    public async Task RegisterAsync(
        IReadOnlyList<LiveInstance> resolved,
        long nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        if (resolved.Count == 0)
        {
            return;
        }

        // Deduplicate by app first. Chrome starting forty renderers resolves forty instances to one app,
        // and a write per instance would be forty writes for one row - on the very tick the machine is
        // already busiest.
        var seen = new HashSet<AppId>();
        foreach (var instance in resolved)
        {
            if (seen.Add(instance.AppId))
            {
                await UpsertAsync(instance.AppId, instance.Resolution, nowUtc, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The belt to the registration braces: every app id about to be written must have a row. Called
    /// immediately before <see cref="IMetricsRepository.WriteRowsAsync"/>.
    /// </summary>
    public async Task EnsureForRowsAsync(
        IReadOnlyList<MetricRow> rows,
        long nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);

        foreach (var row in rows)
        {
            if (!_known.ContainsKey(row.AppId))
            {
                // An app in a rollup row that was never registered: the instance resolved before this
                // registrar existed, or a registration failed. Writing a minimal row is better than
                // failing the whole minute's transaction over one unknown id.
                await UpsertAsync(row.AppId, resolution: null, nowUtc, cancellationToken).ConfigureAwait(false);
                continue;
            }

            await TouchAsync(row.AppId, nowUtc, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Forgets what has been written, so the next call re-registers. For tests and for a purge.</summary>
    public void Reset() => _known.Clear();

    /// <summary>
    /// Refreshes <c>last_seen_utc</c> for an app that is still running. Once per app per minute, driven by
    /// the rollup, rather than once per second — the column exists to answer "when did I last use this?",
    /// and a minute's precision answers it.
    /// </summary>
    private async Task TouchAsync(AppId appId, long nowUtc, CancellationToken cancellationToken)
    {
        var record = _known[appId] with { LastSeenUtc = nowUtc };
        _known[appId] = record;
        await _repository.UpsertAppAsync(record, cancellationToken).ConfigureAwait(false);
    }

    private async Task UpsertAsync(
        AppId appId,
        ResolutionResult? resolution,
        long nowUtc,
        CancellationToken cancellationToken)
    {
        if (_known.ContainsKey(appId))
        {
            await TouchAsync(appId, nowUtc, cancellationToken).ConfigureAwait(false);
            return;
        }

        // first_seen_utc is only ever set here. The repository preserves it on update, so a restart does
        // not rewrite the beginning of an app's history.
        var record = new AppRecord(
            appId,
            resolution?.DisplayName ?? appId.Suffix,
            resolution?.Source ?? appId.Source,
            resolution?.Confidence ?? 0d,
            nowUtc,
            nowUtc)
        {
            InstallRoot = resolution?.InstallRoot,
            Tier = ProcessTierValue.Normal,
        };

        _known[appId] = record;
        await _repository.UpsertAppAsync(record, cancellationToken).ConfigureAwait(false);
    }
}
