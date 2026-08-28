using System.Diagnostics;
using System.Globalization;
using System.Text;
using AppLedger.Collector;
using AppLedger.Collector.Processes;
using AppLedger.Collector.Snapshots;
using AppLedger.Core.Collection;
using AppLedger.Core.Identity;
using AppLedger.Core.Time;
using AppLedger.Infrastructure.Etw;
using AppLedger.Infrastructure.Gpu;
using AppLedger.Infrastructure.Network;
using AppLedger.Infrastructure.Platform;
using AppLedger.Infrastructure.Policy;
using AppLedger.Infrastructure.Process;
using AppLedger.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace S1.EtwBudget;

/// <summary>
/// S1's first leg: the collector pipeline with the real sensors and a temporary database, and <b>no pipe
/// server</b> (docs/20_SPIKES.md §S1).
/// </summary>
/// <remarks>
/// The isolation run. Its number subtracted from the Agent's is the cost of everything the Agent adds — the
/// pipe server, Serilog, the catalog and the real database — which is worth knowing separately, because if
/// the pair misses the budget this says which half to look at.
/// <para>
/// Deliberately not built from <c>AgentComposition</c>: a spike cannot reference an exe project, and more to
/// the point the isolation leg is only meaningful if what it leaves out is visible in one file. What it must
/// <b>not</b> differ in is app identity, which is why the install-root boundaries come from the same
/// <see cref="InstallRootBoundaries"/> the Agent uses rather than a copy — a harness that groups processes
/// differently is not measuring the shipping pipeline.
/// </para>
/// </remarks>
internal static class FullRun
{
    private const int SampleSeconds = 10;

    internal static async Task<int> RunAsync(double hours, string outPath, CancellationToken cancellationToken)
    {
        var root = new DataRoot(Path.Combine(Path.GetTempPath(), $"appledger-s1-{Guid.NewGuid():N}"));
        root.EnsureCreated();

        Console.Out.WriteLine($"S1 (full)  hours={hours}  out={outPath}");
        Console.Out.WriteLine($"           database: {root.DatabasePath}");

        var folders = KnownFolders.Current;

        // No catalog: the loader needs an embedded signing key this build has no reason to carry, and a null
        // catalog is a working PolicyGuard with fewer rules (ADR-12). It costs the run nothing measurable and
        // it keeps the harness runnable from a clean checkout.
        var policy = PolicyGuard.Create(catalog: null, dataRoot: root, folders: folders);

        var database = new SqliteConnectionFactory(root, DatabaseOptions.Agent);
        var schemaVersion = new SchemaMigrator(database, root).Migrate();
        var repository = new MetricsRepository(database);

        var registry = new InstanceRegistry(
            policy,
            new ProcessEnricher(),
            new FallbackIdentityResolver(policy, new InstallRootHeuristic(InstallRootBoundaries.For(folders))));

        using var etw = EtwHub.CanCreateSessions ? new EtwHub(NullLogger<EtwHub>.Instance) : null;
        using var gpu = new GpuPoller();
        var connections = new ConnectionPoller();

        // Create attaches the ETW handlers to the accumulators; building them apart and forgetting to connect
        // them measures a collector that records zero bytes for everything, at a very attractive cost.
        var host = new CollectorHost(
            new NtProcessSource(),
            registry,
            SystemClock.Instance,
            new CollectorOptions(),
            repository,
            SensorJoin.Create(etw, gpu));

        if (etw is not null)
        {
            host.AddSensor(etw);
        }

        host.AddSensor(gpu);
        host.AddSensor(connections);

        await host.StartSensorsAsync(cancellationToken).ConfigureAwait(false);

        Console.Out.WriteLine($"           schema v{schemaVersion}");
        foreach (var sensor in host.Sensors)
        {
            Console.Out.WriteLine($"           sensor {sensor.Name,-16} {sensor.Health.State}");
        }

        if (host.FailedSensors.Count > 0)
        {
            foreach (var (sensor, error) in host.FailedSensors)
            {
                Console.Error.WriteLine($"           sensor {sensor} failed to start: {error}");
            }
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromHours(hours));

        Console.Out.WriteLine("           collecting; Ctrl+C stops early.\n");

        var ticking = TickAsync(host, deadline.Token);
        var summary = await MeasureAsync(host, root.DatabasePath, outPath, deadline.Token).ConfigureAwait(false);
        await ticking.ConfigureAwait(false);

        await host.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        await host.StopSensorsAsync(CancellationToken.None).ConfigureAwait(false);

        Console.Out.WriteLine($"\n           database left at {root.DatabasePath} for the S5 size model");
        Console.Out.WriteLine($"           delete it when done: it is under %TEMP%, not the real data root");

        return Report(summary, hours);
    }

    /// <summary>
    /// Drives the collector at whatever interval its own profile asks for.
    /// </summary>
    /// <remarks>
    /// No <c>NoteUiActivity</c> call anywhere in this harness, which is the point: S1's headline number is the
    /// <i>idle</i> budget, and the idle profile is what the Agent runs under when nobody is watching. A
    /// harness that kept the collector in its 1 Hz profile for 48 hours would measure a case that only occurs
    /// while a window is open.
    /// </remarks>
    private static async Task TickAsync(CollectorHost host, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await host.TickAsync(cancellationToken).ConfigureAwait(false);

                using var timer = new PeriodicTimer(host.CurrentInterval);
                await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The --hours budget elapsed, or Ctrl+C.
        }
    }

    private static async Task<Budget> MeasureAsync(
        CollectorHost host,
        string databasePath,
        string outPath,
        CancellationToken cancellationToken)
    {
        var self = Process.GetCurrentProcess();
        var cpuCount = Environment.ProcessorCount;
        var started = Stopwatch.StartNew();

        await using var csv = new StreamWriter(outPath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        await csv.WriteLineAsync(
            "elapsed_s,cpu_pct,cpu_5min_pct,private_ws_mb,gc0,gc1,gc2,idle,live_apps,live_instances," +
            "rows_written,ring_seconds,events_lost,live_dropped,late_samples,unattributed_instances," +
            "exit_residue_dropped,unattributed_events,handler_errors,dns_entries,dns_evicted,db_mb")
            .ConfigureAwait(false);

        var lastCpu = self.TotalProcessorTime;
        var lastElapsed = TimeSpan.Zero;
        var budget = new Budget();
        var window = new Queue<double>();

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(SampleSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                self.Refresh();
                var elapsed = started.Elapsed;
                var cpu = self.TotalProcessorTime;
                var cpuPct = (cpu - lastCpu).TotalSeconds / ((elapsed - lastElapsed).TotalSeconds * cpuCount) * 100.0;
                lastCpu = cpu;
                lastElapsed = elapsed;

                var wsMb = self.PrivateMemorySize64 / (1024.0 * 1024.0);

                // The budget in docs/01 is stated as a 5-minute average, so that is the number compared with
                // it. A single 10-second sample would fail the run on any file copy that happened to land.
                window.Enqueue(cpuPct);
                while (window.Count > 300 / SampleSeconds)
                {
                    window.Dequeue();
                }

                var rolling = window.Average();
                var health = host.ReadHealth();
                var dbMb = DatabaseMb(databasePath);

                budget.Observe(elapsed, rolling, wsMb, health);

                await csv.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                    $"{elapsed.TotalSeconds:F0},{cpuPct:F3},{rolling:F3},{wsMb:F1}," +
                    $"{GC.CollectionCount(0)},{GC.CollectionCount(1)},{GC.CollectionCount(2)}," +
                    $"{(health.IsIdle ? 1 : 0)},{health.LiveApps},{health.LiveInstances}," +
                    $"{health.RowsWritten},{health.RingSeconds},{health.EventsLost},{health.LiveDropped}," +
                    $"{health.LateSamples},{health.UnattributedInstances},{health.ExitResidueDropped}," +
                    $"{health.UnattributedEvents},{health.HandlerErrors},{health.DnsEntries}," +
                    $"{health.DnsEvicted},{dbMb:F2}")).ConfigureAwait(false);

                Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  {elapsed.TotalMinutes,6:F1} min  cpu {cpuPct,6:F2}% (5-min {rolling,5:F2}%)  " +
                    $"ws {wsMb,6:F1} MB  db {dbMb,6:F1} MB  apps {health.LiveApps,4}  rows {health.RowsWritten,8}  " +
                    $"lost {health.EventsLost,6}{(health.IsIdle ? "  idle" : string.Empty)}"));
            }
        }
        catch (OperationCanceledException)
        {
            // Normal end.
        }

        return budget;
    }

    /// <summary>
    /// The database file only. The -wal beside it is checkpointed on close, so counting it would report
    /// a size the six-month model in docs/06 does not mean.
    /// </summary>
    private static double DatabaseMb(string path) =>
        File.Exists(path) ? new FileInfo(path).Length / (1024.0 * 1024.0) : 0;

    private static int Report(Budget b, double hours)
    {
        Console.Out.WriteLine("\n--- S1 leg A: collector, no pipe server ---------------------------");
        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"peak 5-min CPU, idle   : {b.PeakIdleCpu:F2} %      (criterion < 1 %)"));
        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"peak 5-min CPU, any    : {b.PeakCpu:F2} %      (criterion < 3 % under load)"));
        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"private WS at 24 h     : {Fmt(b.WsAt24h)}   (criterion < 100 MB)"));
        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"private WS at end      : {b.LastWsMb:F1} MB   (criterion < 100 MB)"));
        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"peak private WS        : {b.PeakWsMb:F1} MB"));
        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"events lost            : {b.EventsLost}          (criterion 0 at normal load)"));
        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"handler errors         : {b.HandlerErrors}          (criterion 0)"));
        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"late samples           : {b.LateSamples}          (non-zero means the clock stepped back)"));
        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"rows written           : {b.RowsWritten}"));
        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"unattributed events    : {b.UnattributedEvents}    (the window before the poller sees a new PID)"));

        var pass = b.PeakIdleCpu < 1.0
            && b.PeakCpu < 3.0
            && b.PeakWsMb < 100.0
            && b.EventsLost == 0
            && b.HandlerErrors == 0;

        Console.Out.WriteLine(pass
            ? $"\nPASS over {hours} h. Record as leg A in docs/20_SPIKES.md §Status."
            : $"\nFAIL over {hours} h. docs/20 §S1 lists the reductions to try, in order, before re-running.");

        return pass ? 0 : 1;

        static string Fmt(double? value) => value is { } v
            ? string.Create(CultureInfo.InvariantCulture, $"{v:F1} MB")
            : "n/a    ";
    }

    /// <summary>
    /// The worst reading of each criterion, kept as the run goes so a 48-hour run needs no post-processing.
    /// </summary>
    /// <remarks>
    /// Idle CPU is tracked apart from CPU generally because the two criteria in docs/20 are different numbers
    /// for different states, and a run that spends an hour under load would otherwise report that hour as its
    /// idle figure. <c>IsIdle</c> is the collector's own answer, so the split matches the profile it was
    /// actually running under rather than a guess about what the machine was doing.
    /// </remarks>
    private sealed class Budget
    {
        private static readonly TimeSpan Day = TimeSpan.FromHours(24);

        public double PeakCpu { get; private set; }

        public double PeakIdleCpu { get; private set; }

        public double PeakWsMb { get; private set; }

        public double LastWsMb { get; private set; }

        public double? WsAt24h { get; private set; }

        public long EventsLost { get; private set; }

        public long HandlerErrors { get; private set; }

        public long LateSamples { get; private set; }

        public long RowsWritten { get; private set; }

        public long UnattributedEvents { get; private set; }

        public void Observe(TimeSpan elapsed, double rollingCpu, double wsMb, CollectorHealth health)
        {
            PeakCpu = Math.Max(PeakCpu, rollingCpu);

            if (health.IsIdle)
            {
                PeakIdleCpu = Math.Max(PeakIdleCpu, rollingCpu);
            }

            PeakWsMb = Math.Max(PeakWsMb, wsMb);
            LastWsMb = wsMb;

            if (WsAt24h is null && elapsed >= Day)
            {
                WsAt24h = wsMb;
            }

            // Cumulative counters, so the last reading is the total - but Max, not assignment: EventsLost is
            // read per sensor and a sensor that restarts brings its own counter back to zero (docs/24 Finding).
            EventsLost = Math.Max(EventsLost, health.EventsLost);
            HandlerErrors = Math.Max(HandlerErrors, health.HandlerErrors);
            LateSamples = Math.Max(LateSamples, health.LateSamples);
            RowsWritten = Math.Max(RowsWritten, health.RowsWritten);
            UnattributedEvents = Math.Max(UnattributedEvents, health.UnattributedEvents);
        }
    }
}
