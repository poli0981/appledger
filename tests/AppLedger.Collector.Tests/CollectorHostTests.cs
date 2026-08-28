using AppLedger.Collector.Accumulators;
using AppLedger.Collector.Processes;
using AppLedger.Collector.Snapshots;
using AppLedger.Collector.Tests.TestSupport;
using AppLedger.Core.Collection;
using AppLedger.Core.Identity;
using AppLedger.Core.Metrics;
using AppLedger.Core.Process;
using Shouldly;
using Xunit;

namespace AppLedger.Collector.Tests;

/// <summary>
/// The host wires the pipeline and owns the ordering. These are the cases no test below the host can
/// catch, because each half looks correct on its own.
/// </summary>
public sealed class CollectorHostTests
{
    private const string ChromeExe = @"C:\Program Files\Google\Chrome\Application\chrome.exe";

    private readonly FakePolicyGuard _policy = new();
    private readonly FakeProcessEnricher _enricher = new();
    private readonly FakeMetricsRepository _repository = new();
    private readonly ManualClock _clock = new();
    private readonly ScriptedProcessSource _source = new();

    private CollectorHost Build(CollectorOptions? options = null, SensorJoin? sensors = null)
    {
        var resolver = new FallbackIdentityResolver(_policy, new InstallRootHeuristic(FakePolicyGuard.Boundaries));
        var registry = new InstanceRegistry(_policy, _enricher, resolver);
        return new CollectorHost(_source, registry, _clock, options, _repository, sensors);
    }

    private static RawProcessSample Sample(int pid, string imageName, long readBytes = 0) => new()
    {
        Key = new ProcessKey(pid, 1),
        ImageName = imageName,
        SessionId = 1,
        ReadTransferCount = readBytes,
        WorkingSetPrivate = 4_096,
        WorkingSet = 8_192,
        PagefileUsage = 12_288,
        ThreadCount = 3,
        HandleCount = 30,
    };

    /// <summary>Runs <paramref name="seconds"/> ticks, advancing the clock by one second each time.</summary>
    private async Task RunAsync(CollectorHost host, int seconds, Func<int, RawProcessSample[]> snapshotFor)
    {
        for (var i = 0; i < seconds; i++)
        {
            _source.Then(snapshotFor(i));
            await host.TickAsync();
            _clock.Advance();
        }
    }

    /// <summary>
    /// The reason this class exists. The foreign key is real and enforced, so a metrics row for an app with
    /// no <c>apps</c> row throws — and the ordering that prevents it lives only in the host.
    /// </summary>
    [Fact]
    public async Task TickAsync_WritesTheAppRowBeforeTheMetricsRowThatReferencesIt()
    {
        _enricher.WithImagePath(new ProcessKey(100, 1), ChromeExe);
        var host = Build();

        // Two full minutes so a bucket actually closes.
        await RunAsync(host, 130, _ => [Sample(100, "chrome.exe", readBytes: 1_000)]);

        _repository.WriteCalls.ShouldBeGreaterThan(0);
        _repository.Rows.ShouldNotBeEmpty();
        _repository.Apps.Keys.ShouldContain(_repository.Rows.First().AppId);
    }

    /// <summary>
    /// The belt to the registration braces. If an app somehow reaches a rollup row without having been
    /// registered, the host must still write its row rather than fail the whole minute's transaction.
    /// </summary>
    [Fact]
    public async Task TickAsync_AppThatSlippedRegistration_StillGetsARowBeforeTheWrite()
    {
        _enricher.WithImagePath(new ProcessKey(100, 1), ChromeExe);
        var host = Build();

        await RunAsync(host, 130, _ => [Sample(100, "chrome.exe")]);

        // Every row that was written had its app present at write time, or the fake would have thrown.
        _repository.Rows.ShouldAllBe(r => _repository.Apps.ContainsKey(r.AppId));
    }

    [Fact]
    public async Task TickAsync_FirstSeen_IsNotRewrittenAsTheAppKeepsRunning()
    {
        _enricher.WithImagePath(new ProcessKey(100, 1), ChromeExe);
        var host = Build();

        await RunAsync(host, 200, _ => [Sample(100, "chrome.exe")]);

        var app = _repository.Apps.Values.ShouldHaveSingleItem();
        app.FirstSeenUtc.ShouldBe(1_700_000_000);
        app.LastSeenUtc.ShouldBeGreaterThan(app.FirstSeenUtc);
    }

    /// <summary>
    /// An app already written this session is not rewritten per process. A machine with churning
    /// short-lived processes would otherwise turn into one database write per process start.
    /// </summary>
    [Fact]
    public async Task TickAsync_ManyProcessesOfOneApp_DoNotBecomeOneWriteEach()
    {
        for (var pid = 100; pid < 130; pid++)
        {
            _enricher.WithImagePath(new ProcessKey(pid, 1), ChromeExe);
        }

        var host = Build();
        await RunAsync(host, 5, i => [.. Enumerable.Range(100, 10 + (i * 4)).Select(p => Sample(p, "chrome.exe"))]);

        _repository.Apps.Count.ShouldBe(1);

        // One upsert per tick that saw new instances, not one per instance.
        _repository.UpsertCalls.ShouldBeLessThan(10);
    }

    [Fact]
    public async Task TickAsync_LiteMode_PersistsNothing()
    {
        _enricher.WithImagePath(new ProcessKey(100, 1), ChromeExe);
        var host = Build(CollectorOptions.Lite);

        await RunAsync(host, 130, _ => [Sample(100, "chrome.exe")]);

        _repository.WriteCalls.ShouldBe(0);
        _repository.UpsertCalls.ShouldBe(0);
        host.RowsWritten.ShouldBe(0);

        // The live path still works, which is the whole point of Lite mode.
        host.Ring.Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task TickAsync_PublishesToTheRingAndTheLiveStream()
    {
        _enricher.WithImagePath(new ProcessKey(100, 1), ChromeExe);
        var host = Build();

        await RunAsync(host, 5, _ => [Sample(100, "chrome.exe")]);

        // Four seconds of deltas: the first tick only establishes the baseline.
        host.Ring.Count.ShouldBe(4);
        host.Live.Reader.TryRead(out var published).ShouldBeTrue();
        published.ShouldNotBeEmpty();
    }

    /// <summary>
    /// A stalled UI must not be able to make the collector block or grow. The oldest second goes, and the
    /// reader resumes on current data rather than replaying a queue it no longer wants.
    /// </summary>
    [Fact]
    public async Task TickAsync_NobodyReadingTheLiveStream_DropsOldestAndKeepsGoing()
    {
        _enricher.WithImagePath(new ProcessKey(100, 1), ChromeExe);
        var host = Build(new CollectorOptions { LiveChannelCapacity = 3 });

        await RunAsync(host, 20, _ => [Sample(100, "chrome.exe")]);

        host.Live.Dropped.ShouldBeGreaterThan(0);

        var buffered = 0;
        while (host.Live.Reader.TryRead(out _))
        {
            buffered++;
        }

        buffered.ShouldBe(3);
    }

    /// <summary>Dropping a live second must never lose a stored row.</summary>
    [Fact]
    public async Task TickAsync_LiveDrops_DoNotAffectWhatIsWritten()
    {
        _enricher.WithImagePath(new ProcessKey(100, 1), ChromeExe);
        var host = Build(new CollectorOptions { LiveChannelCapacity = 1 });

        await RunAsync(host, 130, _ => [Sample(100, "chrome.exe", readBytes: 500)]);

        host.Live.Dropped.ShouldBeGreaterThan(0);
        _repository.Rows.ShouldNotBeEmpty();
        _repository.Rows.First().RuntimeSeconds.ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// The Agent starts at logon with nothing connected, and that is exactly when it should be cheap.
    /// </summary>
    [Fact]
    public async Task TickAsync_NoUiHasEverConnected_StartsIdle()
    {
        var host = Build();

        await RunAsync(host, 2, _ => [Sample(100, "chrome.exe")]);

        host.IsIdle.ShouldBeTrue();
        host.CurrentInterval.ShouldBe(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task TickAsync_WhileAUiIsConnected_UsesTheFastInterval()
    {
        var host = Build();
        host.NoteUiActivity();

        await RunAsync(host, 2, _ => [Sample(100, "chrome.exe")]);

        host.IsIdle.ShouldBeFalse();
        host.CurrentInterval.ShouldBe(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task TickAsync_TenMinutesAfterTheUiWentAway_AdoptsTheIdleProfile()
    {
        var host = Build();
        host.NoteUiActivity();

        await RunAsync(host, 601, _ => [Sample(100, "chrome.exe")]);

        host.IsIdle.ShouldBeTrue();
    }

    /// <summary>Shutdown must not throw away a partial minute; that would gap every session.</summary>
    [Fact]
    public async Task FlushAsync_PartialMinute_IsStillWritten()
    {
        _enricher.WithImagePath(new ProcessKey(100, 1), ChromeExe);
        var host = Build();

        await RunAsync(host, 20, _ => [Sample(100, "chrome.exe", readBytes: 100)]);
        _repository.Rows.ShouldBeEmpty();

        await host.FlushAsync();

        var row = _repository.Rows.ShouldHaveSingleItem();
        row.RuntimeSeconds.ShouldBe(19);
    }

    [Fact]
    public async Task StartSensorsAsync_SensorThatThrows_IsRecordedAndDoesNotStopTheOthers()
    {
        var host = Build();
        var good = new FakeSensor("Good");
        var bad = new FakeSensor("Bad", throwOnStart: true);
        host.AddSensor(bad);
        host.AddSensor(good);

        await host.StartSensorsAsync();

        good.Started.ShouldBeTrue();
        host.FailedSensors.ShouldHaveSingleItem().Sensor.ShouldBe("Bad");
    }

    /// <summary>
    /// A sensor that cannot run here is not a failure. "No GPU counters on this box" is a normal Tuesday,
    /// and the collector carries on without it (docs/01 §Degraded modes).
    /// </summary>
    [Fact]
    public async Task StartSensorsAsync_UnavailableSensor_IsNotAFailure()
    {
        var host = Build();
        host.AddSensor(new FakeSensor("Gpu", state: SensorState.Unavailable));

        await host.StartSensorsAsync();

        host.FailedSensors.ShouldBeEmpty();

        var report = host.Sensors.ShouldHaveSingleItem();
        report.Name.ShouldBe("Gpu");
        report.Health.State.ShouldBe(SensorState.Unavailable);
    }

    [Fact]
    public async Task StopSensorsAsync_StopsEveryone()
    {
        var host = Build();
        var a = new FakeSensor("A");
        var b = new FakeSensor("B");
        host.AddSensor(a);
        host.AddSensor(b);

        await host.StopSensorsAsync();

        a.Stopped.ShouldBeTrue();
        b.Stopped.ShouldBeTrue();
    }

    /// <summary>
    /// Sleep and resume in the middle of a session. The tick is dropped, nothing absurd is written, and the
    /// collector keeps going afterwards.
    /// </summary>
    [Fact]
    public async Task TickAsync_ClockJumpMidSession_DropsTheTickAndRecovers()
    {
        _enricher.WithImagePath(new ProcessKey(100, 1), ChromeExe);
        var host = Build();

        await RunAsync(host, 10, _ => [Sample(100, "chrome.exe", readBytes: 100)]);

        _clock.JumpWallClock(8 * 3600);
        _source.Then([Sample(100, "chrome.exe", readBytes: 40_000_000_000)]);
        (await host.TickAsync()).ShouldBeEmpty();
        _clock.Advance();

        _source.Then([Sample(100, "chrome.exe", readBytes: 40_000_000_500)]);
        var after = await host.TickAsync();

        after.ShouldHaveSingleItem().IoRead.ShouldBe(500);
    }

    // -- sensors -----------------------------------------------------------------------------------------

    /// <summary>
    /// The end-to-end claim of the whole join, at the level where it can actually be wrong: ETW bytes
    /// raised on the source reach the row that is written to history. Every layer below this passed while
    /// those columns were permanently zero.
    /// </summary>
    [Fact]
    public async Task TickAsync_EtwBytesForALiveInstance_ReachTheWrittenRow()
    {
        _enricher.WithImagePath(new ProcessKey(100, 1), ChromeExe);
        var etw = new FakeEtwSource();
        var host = Build(sensors: SensorJoin.Create(etw, gpu: null));

        for (var i = 0; i < 130; i++)
        {
            _source.Then([Sample(100, "chrome.exe")]);
            await host.TickAsync();
            etw.Raise(new NetworkEvent(100, 1_000, NetworkDirection.Inbound, NetworkProtocol.Tcp, null, 443, 0));
            etw.Raise(new DiskIoEvent(100, 4_096, IsWrite: true, DiskNumber: 0, 0));
            _clock.Advance();
        }

        _repository.Rows.ShouldNotBeEmpty();

        var row = _repository.Rows.First();
        row.NetIn.ShouldBeGreaterThan(0);
        row.DiskWrite.ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// Eight hours of sleep must not arrive as one second of traffic. The bytes that accumulated across the
    /// gap belong to no second we can name, so the window is discarded with the tick.
    /// </summary>
    [Fact]
    public async Task TickAsync_ClockJump_DoesNotChargeTheWholeGapToTheNextSecond()
    {
        _enricher.WithImagePath(new ProcessKey(100, 1), ChromeExe);
        var etw = new FakeEtwSource();
        var host = Build(sensors: SensorJoin.Create(etw, gpu: null));

        await RunAsync(host, 5, _ => [Sample(100, "chrome.exe")]);

        // Traffic that piled up while the machine was asleep.
        etw.Raise(new NetworkEvent(100, 40_000_000_000, NetworkDirection.Inbound, NetworkProtocol.Tcp, null, 443, 0));

        _clock.JumpWallClock(8 * 3600);
        _source.Then([Sample(100, "chrome.exe")]);
        (await host.TickAsync()).ShouldBeEmpty();
        _clock.Advance();

        _source.Then([Sample(100, "chrome.exe")]);
        var after = await host.TickAsync();

        after.ShouldHaveSingleItem().NetIn.ShouldBe(0);
    }

    /// <summary>
    /// The connection table is not part of <c>AppSample</c> and has no reader until the Network tab exists,
    /// so sampling it at 1 Hz would be four table reads a second for nobody (docs/05 §Budget controls).
    /// </summary>
    [Fact]
    public async Task TickAsync_ConnectionSource_IsNeverSampledOnTheTickPath()
    {
        _enricher.WithImagePath(new ProcessKey(100, 1), ChromeExe);
        var connections = new CountingConnectionSource();
        var host = Build();
        host.AddSensor(connections);

        await host.StartSensorsAsync();
        await RunAsync(host, 10, _ => [Sample(100, "chrome.exe")]);

        connections.SampleCalls.ShouldBe(0);
        host.Sensors.ShouldContain(s => s.Name == "ConnectionPoller");
    }

    /// <summary>
    /// The idle profile has to release something to be worth having. Five minutes of a hundred apps is
    /// 5.5 MB of the roughly 20 the collector gets in total.
    /// </summary>
    [Fact]
    public async Task TickAsync_GoingIdle_ShrinksTheRingToTheIdleWindow()
    {
        _enricher.WithImagePath(new ProcessKey(100, 1), ChromeExe);
        var options = new CollectorOptions
        {
            RingWindow = TimeSpan.FromMinutes(5),
            IdleRingWindow = TimeSpan.FromMinutes(1),
            IdleAfter = TimeSpan.FromSeconds(3),
        };

        var host = Build(options);
        host.Ring.Capacity.ShouldBe(300);

        host.NoteUiActivity();
        await RunAsync(host, 2, _ => [Sample(100, "chrome.exe")]);
        host.IsIdle.ShouldBeFalse();
        host.Ring.Capacity.ShouldBe(300);

        // Nothing notes activity again, so the idle threshold passes.
        await RunAsync(host, 5, _ => [Sample(100, "chrome.exe")]);

        host.IsIdle.ShouldBeTrue();
        host.Ring.Capacity.ShouldBe(60);
    }

    [Fact]
    public async Task TickAsync_UiReturning_RestoresTheFullRing()
    {
        _enricher.WithImagePath(new ProcessKey(100, 1), ChromeExe);
        var options = new CollectorOptions { IdleAfter = TimeSpan.FromSeconds(2) };
        var host = Build(options);

        await RunAsync(host, 3, _ => [Sample(100, "chrome.exe")]);
        host.Ring.Capacity.ShouldBe((int)options.IdleRingWindow.TotalSeconds);

        host.NoteUiActivity();
        await RunAsync(host, 1, _ => [Sample(100, "chrome.exe")]);

        host.IsIdle.ShouldBeFalse();
        host.Ring.Capacity.ShouldBe((int)options.RingWindow.TotalSeconds);
    }

    /// <summary>
    /// Every counter here measures a place where the collector knows it missed something, and each was
    /// unreachable from outside the host until now — indistinguishable from a quiet machine.
    /// </summary>
    [Fact]
    public async Task ReadHealth_ExposesEveryCounterThePipeServerNeeds()
    {
        _enricher.WithImagePath(new ProcessKey(100, 1), ChromeExe);
        var etw = new FakeEtwSource();
        var host = Build(new CollectorOptions { LiveChannelCapacity = 1 }, SensorJoin.Create(etw, gpu: null));
        host.AddSensor(etw);
        await host.StartSensorsAsync();

        // An event for a PID the poller has never reported cannot be attributed, and is counted.
        etw.Raise(new NetworkEvent(999, 10, NetworkDirection.Inbound, NetworkProtocol.Tcp, null, 443, 0));
        await RunAsync(host, 20, _ => [Sample(100, "chrome.exe")]);

        var health = host.ReadHealth();

        health.LiveApps.ShouldBe(1);
        health.LiveInstances.ShouldBe(1);
        health.RingSeconds.ShouldBeGreaterThan(0);
        health.LiveDropped.ShouldBeGreaterThan(0);
        health.UnattributedEvents.ShouldBe(1);
        health.CurrentInterval.ShouldBe(host.CurrentInterval);
        health.Sensors.ShouldContain(s => s.Name == "EtwHub");
        health.Degraded.ShouldBeFalse();
    }

    [Fact]
    public async Task ReadHealth_UnavailableSensor_ReportsDegraded()
    {
        var host = Build();
        host.AddSensor(new FakeSensor("Gpu", state: SensorState.Unavailable));
        await host.StartSensorsAsync();

        var health = host.ReadHealth();

        health.Degraded.ShouldBeTrue();
        health.Sensors.ShouldHaveSingleItem().Name.ShouldBe("Gpu");
    }

    /// <summary>Lite mode has no ETW at all, so the counters that come from it are zero, not missing.</summary>
    [Fact]
    public void ReadHealth_WithNoSensorJoin_StillReports()
    {
        var health = Build(CollectorOptions.Lite).ReadHealth();

        health.UnattributedEvents.ShouldBe(0);
        health.HandlerErrors.ShouldBe(0);
        health.DnsEntries.ShouldBe(0);
        health.Sensors.ShouldBeEmpty();
    }

    private sealed class CountingConnectionSource : IConnectionSource
    {
        public string Name => "ConnectionPoller";

        public SensorHealth Health { get; private set; } = SensorHealth.Stopped;

        internal int SampleCalls { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            Health = new SensorHealth(SensorState.Running);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public IReadOnlyList<ConnectionRow> Sample()
        {
            SampleCalls++;
            return [];
        }
    }
}
