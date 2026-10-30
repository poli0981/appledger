using AppLedger.Collector;
using AppLedger.Collector.Processes;
using AppLedger.Collector.Snapshots;
using AppLedger.Core.Collection;
using AppLedger.Core.Identity;
using AppLedger.Core.Metrics;
using AppLedger.Core.Time;
using AppLedger.Infrastructure.Gpu;
using AppLedger.Infrastructure.Network;
using AppLedger.Infrastructure.Platform;
using AppLedger.Infrastructure.Policy;
using AppLedger.Infrastructure.Process;

namespace AppLedger.App.Services;

/// <summary>
/// The collector hosted inside the UI as a standard user (docs/01_ARCHITECTURE.md §Lite mode).
/// </summary>
/// <remarks>
/// Lite mode exists so the first run never dead-ends on a UAC prompt. What it can see is what a standard user
/// can see: the process table for this session, GPU counters and the connection table. It constructs no ETW
/// hub — those sessions need elevation — so network bytes, real device I/O and per-process DNS are
/// <b>absent</b>, and absent is not zero. The sensor states carry that distinction to the UI, which shows
/// "N/A" rather than a zero that would claim we looked.
/// <para>
/// Nothing is persisted. History is the Agent's alone (docs/06_DATA_MODEL.md §Ownership), and a UI writing
/// rows would put two writers on one database.
/// </para>
/// </remarks>
public sealed class LiteCollector : IAsyncDisposable
{
    private readonly CollectorHost _host;
    private readonly List<ISensor> _sensors;
    private readonly CancellationTokenSource _stopping = new();

    private Task? _loop;

    /// <summary>Builds the Lite pipeline.</summary>
    public LiteCollector()
    {
        var folders = KnownFolders.Current;
        var policy = PolicyGuard.Create(catalog: null, dataRoot: null, folders: folders);

        // Still the stand-in, and deliberately: the real resolver needs the catalog, and Lite mode does not
        // load one - PolicyGuard above is built with `catalog: null` too. Verifying a signed catalog inside
        // the unelevated UI process is its own decision (what a rejected catalog should do to a UI that is
        // already the fallback), so it is not taken here. The visible consequence is that Lite mode groups
        // by install root where the Agent groups by catalog rule; nothing is written either way, because
        // Lite passes no repository.
        var resolver = new FallbackIdentityResolver(policy, new InstallRootHeuristic(InstallRootBoundaries.For(folders)));
        var registry = new InstanceRegistry(policy, new ProcessEnricher(), resolver);

        var gpu = new GpuPoller();
        var connections = new ConnectionPoller();
        _sensors = [gpu, connections];

        // No IEtwSource: Lite mode does not construct one, so every ETW-derived field stays zero and the
        // sensor list simply has no entry for it - which is what tells the UI to show N/A.
        _host = new CollectorHost(
            new NtProcessSource(),
            registry,
            SystemClock.Instance,
            CollectorOptions.Lite,
            repository: null,
            SensorJoin.Create(etw: null, gpu));

        foreach (var sensor in _sensors)
        {
            _host.AddSensor(sensor);
        }
    }

    /// <summary>The per-second samples, for whoever is drawing.</summary>
    public CollectorHost Host => _host;

    /// <summary>Starts the sensors and the tick loop.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _host.StartSensorsAsync(cancellationToken).ConfigureAwait(false);

        // A UI is by definition watching, so Lite mode never adopts the idle profile: the thing the idle
        // profile saves for is a UI that is not there.
        _host.NoteUiActivity();

        _loop = Task.Run(() => RunAsync(_stopping.Token), CancellationToken.None);
    }

    /// <summary>Sensor states in the shape the health strip reads.</summary>
    public IReadOnlyList<SensorReport> Sensors => _host.Sensors;

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                _host.NoteUiActivity();
                await _host.TickAsync(cancellationToken).ConfigureAwait(false);

                using var timer = new PeriodicTimer(_host.CurrentInterval);
                await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    /// <summary>Stops the loop and the sensors.</summary>
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);

        if (_loop is not null)
        {
            await _loop.ConfigureAwait(false);
        }

        await _host.StopSensorsAsync(CancellationToken.None).ConfigureAwait(false);

        foreach (var sensor in _sensors.OfType<IDisposable>())
        {
            sensor.Dispose();
        }

        _stopping.Dispose();
    }
}
