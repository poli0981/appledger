using AppLedger.Core.Identity;
using AppLedger.Core.Metrics;

namespace AppLedger.Collector.Tests.TestSupport;

/// <summary>
/// An in-memory repository that <b>enforces the foreign key</b> the real schema declares.
/// </summary>
/// <remarks>
/// <c>metrics_1m.app_id</c> references <c>apps(app_id)</c> and <c>foreign_keys=ON</c> is set on every Agent
/// connection, so writing a metrics row for an unregistered app throws in production. This fake throws too.
/// <para>
/// That is the whole reason it is not a bare list: a permissive double would let the ordering bug through
/// and the pipeline tests would stay green while the Agent failed on its first minute. The real constraint
/// is proven separately — <c>SchemaMigratorTests</c> shows the FK is on and cascades — so what is left to
/// prove here is the ordering, and that is what this enforces.
/// </para>
/// </remarks>
internal sealed class FakeMetricsRepository : IMetricsRepository
{
    private readonly Dictionary<AppId, AppRecord> _apps = [];
    private readonly Dictionary<(MetricTier Tier, AppId AppId, long Ts), MetricRow> _rows = [];

    internal IReadOnlyDictionary<AppId, AppRecord> Apps => _apps;

    internal int UpsertCalls { get; private set; }

    internal int WriteCalls { get; private set; }

    internal IReadOnlyCollection<MetricRow> Rows => _rows.Values;

    public Task UpsertAppAsync(AppRecord app, CancellationToken cancellationToken = default)
    {
        UpsertCalls++;

        // The repository preserves first_seen_utc on update; a fake that did not would hide a real bug.
        _apps[app.AppId] = _apps.TryGetValue(app.AppId, out var existing)
            ? app with { FirstSeenUtc = existing.FirstSeenUtc }
            : app;

        return Task.CompletedTask;
    }

    public Task WriteRowsAsync(
        MetricTier tier,
        IReadOnlyList<MetricRow> rows,
        CancellationToken cancellationToken = default)
    {
        WriteCalls++;

        foreach (var row in rows)
        {
            if (!_apps.ContainsKey(row.AppId))
            {
                throw new InvalidOperationException(
                    $"FOREIGN KEY constraint failed: no apps row for '{row.AppId.Value}'.");
            }

            // INSERT OR REPLACE: the same bucket written twice leaves one row.
            _rows[(tier, row.AppId, row.Ts)] = row;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MetricRow>> ReadRangeAsync(
        AppId appId,
        MetricTier tier,
        long fromTsUtc,
        long toTsUtc,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<MetricRow> result =
        [
            .. _rows
                .Where(kv => kv.Key.Tier == tier && kv.Key.AppId.Equals(appId)
                    && kv.Key.Ts >= fromTsUtc && kv.Key.Ts < toTsUtc)
                .Select(kv => kv.Value)
                .OrderBy(r => r.Ts),
        ];

        return Task.FromResult(result);
    }

    /// <summary>Health minutes written, newest last.</summary>
    internal List<HealthMinute> Health { get; } = [];

    public Task WriteHealthAsync(HealthMinute minute, CancellationToken cancellationToken = default)
    {
        // Replace-by-minute, matching the primary key on the real table: a restart inside the same minute
        // leaves one row for it, not two.
        Health.RemoveAll(existing => existing.TsUtc == minute.TsUtc);
        Health.Add(minute);
        return Task.CompletedTask;
    }
}

/// <summary>A process source that replays scripted snapshots, one per tick.</summary>
internal sealed class ScriptedProcessSource : Core.Process.IProcessSource
{
    private readonly Queue<Core.Process.RawProcessSample[]> _script = [];
    private Core.Process.RawProcessSample[] _current = [];

    public int CurrentSessionId => 1;

    internal ScriptedProcessSource Then(params Core.Process.RawProcessSample[] snapshot)
    {
        _script.Enqueue(snapshot);
        return this;
    }

    public ReadOnlySpan<Core.Process.RawProcessSample> Snapshot(int? sessionId = null)
    {
        if (_script.Count > 0)
        {
            _current = _script.Dequeue();
        }

        return sessionId is null
            ? _current
            : Array.FindAll(_current, s => s.SessionId == sessionId.Value);
    }
}

/// <summary>A clock a test drives by hand, so a thousand seconds pass in a microsecond.</summary>
internal sealed class ManualClock : Core.Time.IClock
{
    private long _seconds;

    internal ManualClock(long startUtcSeconds = 1_700_000_000) => _seconds = startUtcSeconds;

    public DateTimeOffset UtcNow => DateTimeOffset.FromUnixTimeSeconds(_seconds);

    public TimeSpan Elapsed { get; private set; }

    /// <summary>Advances both readings together, which is what an untroubled clock does.</summary>
    internal void Advance(int seconds = 1)
    {
        _seconds += seconds;
        Elapsed += TimeSpan.FromSeconds(seconds);
    }

    /// <summary>Moves the wall clock only, which is what sleep, resume and an NTP correction look like.</summary>
    internal void JumpWallClock(long seconds) => _seconds += seconds;
}

/// <summary>A sensor that records what the host asked it to do, and can be told to fail.</summary>
internal sealed class FakeSensor : Core.Collection.ISensor
{
    private readonly bool _throwOnStart;

    internal FakeSensor(string name, bool throwOnStart = false, Core.Collection.SensorState state = Core.Collection.SensorState.Running)
    {
        Name = name;
        _throwOnStart = throwOnStart;
        Health = new Core.Collection.SensorHealth(state);
    }

    public string Name { get; }

    public Core.Collection.SensorHealth Health { get; private set; }

    internal bool Started { get; private set; }

    internal bool Stopped { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_throwOnStart)
        {
            throw new InvalidOperationException("sensor refused to start");
        }

        Started = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Stopped = true;
        return Task.CompletedTask;
    }
}
