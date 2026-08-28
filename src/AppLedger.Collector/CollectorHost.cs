using AppLedger.Collector.Live;
using AppLedger.Collector.Processes;
using AppLedger.Collector.Rollups;
using AppLedger.Collector.Snapshots;
using AppLedger.Collector.Storage;
using AppLedger.Core.Collection;
using AppLedger.Core.Metrics;
using AppLedger.Core.Process;
using AppLedger.Core.Time;

namespace AppLedger.Collector;

/// <summary>
/// Drives one collection tick end to end and supervises the sensors that feed it
/// (docs/05_COLLECTOR.md §Components).
/// </summary>
/// <remarks>
/// The host owns the ordering, and one part of that ordering is load-bearing: the <c>apps</c> row is
/// written before the <c>metrics_1m</c> row that references it. <c>foreign_keys=ON</c> means getting that
/// backwards is a thrown exception rather than a stray row, and no test below the host can catch it,
/// because each half looks correct on its own.
/// <para>
/// The loop itself is deliberately not here. <see cref="TickAsync"/> is called by whoever owns the timer —
/// the Agent's worker, the UI in Lite mode, or a test driving a thousand seconds in a millisecond — which
/// is what makes the whole pipeline testable without a clock.
/// </para>
/// </remarks>
public sealed class CollectorHost
{
    private readonly IProcessSource _source;
    private readonly IClock _clock;
    private readonly CollectorOptions _options;
    private readonly ProcessTable _table;
    private readonly InstanceRegistry _registry;
    private readonly SnapshotBuilder _snapshots;
    private readonly MinuteRollup _rollup;
    private readonly AppRegistrar? _registrar;
    private readonly IMetricsRepository? _repository;
    private readonly SensorJoin? _sensorJoin;
    private readonly List<ISensor> _sensors = [];

    private TimeSpan _lastUiActivity = TimeSpan.MinValue;
    private int _liveApps;

    /// <summary>Wires the pipeline.</summary>
    /// <param name="source">The system-wide process snapshot.</param>
    /// <param name="registry">Instance identity, resolved once per instance.</param>
    /// <param name="clock">Wall-clock and monotonic time, as separate readings.</param>
    /// <param name="options">Budget knobs.</param>
    /// <param name="repository">
    /// Where history goes, or null in Lite mode — which persists nothing at all
    /// (docs/01_ARCHITECTURE.md §Lite mode).
    /// </param>
    /// <param name="sensors">
    /// The join that folds ETW bytes and GPU counters into each second, built by the host process with
    /// <see cref="SensorJoin.Create"/>. Null in Lite mode and in tests that only exercise the process path —
    /// in which case every network, disk and GPU field is legitimately zero rather than accidentally so.
    /// </param>
    public CollectorHost(
        IProcessSource source,
        InstanceRegistry registry,
        IClock clock,
        CollectorOptions? options = null,
        IMetricsRepository? repository = null,
        SensorJoin? sensors = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(clock);

        _source = source;
        _registry = registry;
        _clock = clock;
        _options = options ?? new CollectorOptions();
        _sensorJoin = sensors;

        _table = new ProcessTable();
        _snapshots = new SnapshotBuilder(registry);
        _rollup = new MinuteRollup();

        Ring = new SnapshotRing(_options.RingWindow);
        Live = new LiveChannel(_options.LiveChannelCapacity);

        // Lite mode has no repository and therefore no registrar: there is nothing to key a foreign key on.
        _repository = _options.PersistsHistory ? repository : null;
        _registrar = _repository is null ? null : new AppRegistrar(_repository);
    }

    /// <summary>The last few minutes of per-second samples, for sparklines and for a UI attaching mid-minute.</summary>
    public SnapshotRing Ring { get; }

    /// <summary>The 1 Hz stream the UI subscribes to. Bounded and drop-oldest.</summary>
    public LiveChannel Live { get; }

    /// <summary>True once no UI has been seen for <see cref="CollectorOptions.IdleAfter"/>.</summary>
    public bool IsIdle { get; private set; }

    /// <summary>How many rows have been written this session, for the health report.</summary>
    public long RowsWritten { get; private set; }

    /// <summary>The interval the caller should wait before the next tick, which halves once idle.</summary>
    public TimeSpan CurrentInterval => IsIdle ? _options.IdlePollInterval : _options.PollInterval;

    /// <summary>Health of every supervised sensor, by name, for <c>HelloAck</c> and the budget strip.</summary>
    public IReadOnlyList<SensorReport> Sensors => [.. _sensors.Select(s => new SensorReport(s.Name, s.Health))];

    /// <summary>
    /// Everything the pipe server's <c>Health</c> message needs, in one call.
    /// </summary>
    /// <remarks>
    /// Allocates, and is meant to be called at the 10-second <c>HealthTick</c> cadence — never per tick.
    /// It exists because the alternative is a pipe server reaching through the host into six private
    /// collaborators for seven counters, each of which measures a place where the collector knows it missed
    /// something and which is otherwise indistinguishable from a quiet machine.
    /// </remarks>
    public CollectorHealth ReadHealth()
    {
        var accumulators = _sensorJoin?.Accumulators;

        return new CollectorHealth
        {
            TsUtc = _clock.UtcNowSeconds(),
            IsIdle = IsIdle,
            CurrentInterval = CurrentInterval,
            LiveApps = _liveApps,
            LiveInstances = _registry.Count,
            RowsWritten = RowsWritten,
            RingSeconds = Ring.Count,
            LiveDropped = Live.Dropped,
            LateSamples = _rollup.LateSamples,
            UnattributedInstances = _snapshots.UnattributedInstances,
            ExitResidueDropped = _snapshots.ExitResidueDropped,
            UnattributedEvents = accumulators?.UnattributedEvents ?? 0,
            HandlerErrors = accumulators?.HandlerErrors ?? 0,
            EventsLost = _sensors.Sum(s => s.Health.EventsLost),
            DnsEntries = accumulators?.Dns.Count ?? 0,
            DnsEvicted = accumulators?.Dns.Evicted ?? 0,
            Sensors = Sensors,
            FailedSensors = [.. FailedSensors.Select(f => $"{f.Sensor}: {f.Error}")],
        };
    }

    /// <summary>Adds a sensor to supervise. Sensors are adapters and are injected by the host process.</summary>
    public void AddSensor(ISensor sensor)
    {
        ArgumentNullException.ThrowIfNull(sensor);
        _sensors.Add(sensor);
    }

    /// <summary>
    /// Starts every sensor. A sensor that cannot run here reports <see cref="SensorState.Unavailable"/> and
    /// the collector carries on without it; one that throws is caught and left stopped, because a GPU
    /// counter that is missing must not take the process table down with it.
    /// </summary>
    public async Task StartSensorsAsync(CancellationToken cancellationToken = default)
    {
        foreach (var sensor in _sensors)
        {
            try
            {
                await sensor.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                FailedSensors.Add((sensor.Name, ex.GetType().Name));
            }
        }
    }

    /// <summary>Sensors that threw while starting, by name and exception type. Never carries a message.</summary>
    public List<(string Sensor, string Error)> FailedSensors { get; } = [];

    /// <summary>Stops every sensor, swallowing failures so one bad stop cannot block shutdown.</summary>
    public async Task StopSensorsAsync(CancellationToken cancellationToken = default)
    {
        foreach (var sensor in _sensors)
        {
            try
            {
                await sensor.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                FailedSensors.Add((sensor.Name, ex.GetType().Name));
            }
        }
    }

    /// <summary>Tells the host a UI is connected, which keeps it out of the idle profile.</summary>
    public void NoteUiActivity() => _lastUiActivity = _clock.Elapsed;

    /// <summary>
    /// Runs one tick: snapshot, deltas, identity, per-app samples, ring, live stream, and — on a minute
    /// boundary — the rollup and the write.
    /// </summary>
    /// <returns>The samples published for this second, which is what the caller may log or assert on.</returns>
    public async Task<IReadOnlyList<AppSample>> TickAsync(CancellationToken cancellationToken = default)
    {
        var nowUtc = _clock.UtcNowSeconds();
        UpdateIdleState();

        var sessionFilter = _options.OwnSessionOnly ? _source.CurrentSessionId : (int?)null;
        var tick = _table.Update(_source.Snapshot(sessionFilter), nowUtc, _clock.Elapsed);

        // Before the registry is updated: an exiting instance's last bytes can only be charged to an app
        // while the registry still knows which app that was, and Apply forgets exited instances first.
        _sensorJoin?.Apply(tick, _registry, _clock.Elapsed);

        var resolved = _registry.Apply(tick);
        if (_registrar is not null && resolved.Count > 0)
        {
            await _registrar.RegisterAsync(resolved, nowUtc, cancellationToken).ConfigureAwait(false);
        }

        var samples = _snapshots.Build(tick, _sensorJoin);
        _liveApps = samples.Count;
        if (samples.Count == 0)
        {
            return samples;
        }

        Ring.Add(samples);
        Live.Publish(samples);

        var rows = _rollup.Add(samples);
        await WriteAsync(rows, nowUtc, cancellationToken).ConfigureAwait(false);

        return samples;
    }

    /// <summary>
    /// Writes whatever is still buffered. Called on shutdown so a partial minute survives — losing up to
    /// 59 seconds of every session would read as gaps in the history.
    /// </summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await WriteAsync(_rollup.Flush(), _clock.UtcNowSeconds(), cancellationToken).ConfigureAwait(false);
        Live.Complete();
    }

    private async Task WriteAsync(IReadOnlyList<MetricRow> rows, long nowUtc, CancellationToken cancellationToken)
    {
        if (rows.Count == 0 || _repository is null || _registrar is null)
        {
            return;
        }

        // The load-bearing order. Every app id in these rows gets its apps row first, or the write throws
        // on the foreign key rather than silently dropping a minute.
        await _registrar.EnsureForRowsAsync(rows, nowUtc, cancellationToken).ConfigureAwait(false);
        await _repository.WriteRowsAsync(MetricTier.Minute, rows, cancellationToken).ConfigureAwait(false);

        RowsWritten += rows.Count;
    }

    private void UpdateIdleState()
    {
        // Nothing has ever connected is the Agent's normal state at logon, and it should be idle.
        var idle = _lastUiActivity == TimeSpan.MinValue
            || _clock.Elapsed - _lastUiActivity >= _options.IdleAfter;

        if (idle == IsIdle)
        {
            return;
        }

        IsIdle = idle;

        // The idle profile is only a saving if something is actually released. Nobody draws sparklines when
        // no UI has been connected for ten minutes, and the four megabytes this returns are four of the
        // twenty the whole collector has (docs/05 §Budget controls).
        Ring.Resize(idle ? _options.IdleRingWindow : _options.RingWindow);
    }
}
