using System.Net;
using AppLedger.Collector.Accumulators;
using AppLedger.Collector.Processes;
using AppLedger.Collector.Snapshots;
using AppLedger.Collector.Tests.TestSupport;
using AppLedger.Core.Collection;
using AppLedger.Core.Identity;
using Shouldly;
using Xunit;

namespace AppLedger.Collector.Tests.Snapshots;

/// <summary>
/// The join that folds ETW bytes and GPU counters onto each second. Everything here is scripted input
/// through the handler seam, so no session and no elevation is involved (docs/19_TESTING.md §Layers).
/// </summary>
public sealed class SensorJoinTests
{
    private const string ChromeExe = @"C:\Program Files\Google\Chrome\Application\chrome.exe";

    private static readonly ProcessKey Chrome = new(100, 1);
    private static readonly ProcessKey Other = new(200, 1);

    private readonly PidMap _pids = new();
    private readonly EtwAccumulators _accumulators;
    private readonly FakeEtwSource _etw = new();
    private readonly FakeGpuSource _gpu = new();
    private readonly InstanceRegistry _registry;
    private readonly SensorJoin _join;

    public SensorJoinTests()
    {
        _accumulators = new EtwAccumulators(_pids, new DnsMap());

        var policy = new FakePolicyGuard();
        var enricher = new FakeProcessEnricher().WithImagePath(Chrome, ChromeExe);
        var resolver = new FallbackIdentityResolver(policy, new InstallRootHeuristic(FakePolicyGuard.Boundaries));
        _registry = new InstanceRegistry(policy, enricher, resolver);

        _join = new SensorJoin(_accumulators, _pids, _etw, _gpu, gpuInterval: TimeSpan.FromSeconds(2));
    }

    private static ProcessTick Tick(
        long second,
        ProcessLifecycleEvent[]? started = null,
        ProcessLifecycleEvent[]? exited = null,
        bool rebaselined = false) =>
        new(1_700_000_000 + second, [], started ?? [], exited ?? [], rebaselined);

    private static ProcessLifecycleEvent Start(ProcessKey key, string image = "chrome.exe") =>
        new(key, image, ParentPid: 4);

    private static NetworkEvent Net(int pid, long size, NetworkDirection direction = NetworkDirection.Inbound) =>
        new(pid, size, direction, NetworkProtocol.Tcp, IPAddress.Parse("93.184.216.34"), 443, 1_700_000_000);

    private static DiskIoEvent Disk(int pid, long size, bool isWrite = false) =>
        new(pid, size, isWrite, DiskNumber: 0, 1_700_000_000);

    private void Apply(ProcessTick tick, long elapsedSeconds)
    {
        _join.Apply(tick, _registry, TimeSpan.FromSeconds(elapsedSeconds));
        _registry.Apply(tick);
    }

    // -- taking ------------------------------------------------------------------------------------------

    [Fact]
    public void TryTake_InstanceWithNetworkBytes_ReturnsThemAndZeroesTheAccumulator()
    {
        Apply(Tick(0, started: [Start(Chrome)]), 0);
        _accumulators.OnNetwork(Net(Chrome.Pid, 1_000));
        _accumulators.OnNetwork(Net(Chrome.Pid, 400, NetworkDirection.Outbound));

        _join.TryTake(Chrome, out var first).ShouldBeTrue();
        first.NetIn.ShouldBe(1_000);
        first.NetOut.ShouldBe(400);

        _join.TryTake(Chrome, out var second).ShouldBeFalse();
        second.NetIn.ShouldBe(0);
        second.NetOut.ShouldBe(0);
    }

    /// <summary>
    /// The reason the boundary is a take rather than a read followed by a reset: an event arriving between
    /// the two would be dropped, and the events that land in that gap come from the busiest processes.
    /// </summary>
    [Fact]
    public void TryTake_EventsArrivingBetweenTwoTakes_AreChargedToTheSecondOne()
    {
        Apply(Tick(0, started: [Start(Chrome)]), 0);

        _accumulators.OnNetwork(Net(Chrome.Pid, 100));
        _join.TryTake(Chrome, out var first);

        _accumulators.OnNetwork(Net(Chrome.Pid, 250));
        _join.TryTake(Chrome, out var second);

        first.NetIn.ShouldBe(100);
        second.NetIn.ShouldBe(250);
    }

    [Fact]
    public void TryTake_DiskAccumulator_ZeroesReadsWritesAndOperationsTogether()
    {
        Apply(Tick(0, started: [Start(Chrome)]), 0);
        _accumulators.OnDiskIo(Disk(Chrome.Pid, 4_096));
        _accumulators.OnDiskIo(Disk(Chrome.Pid, 8_192, isWrite: true));

        _join.TryTake(Chrome, out var taken);
        taken.DiskRead.ShouldBe(4_096);
        taken.DiskWrite.ShouldBe(8_192);
        taken.DiskOps.ShouldBe(2);

        _join.TryTake(Chrome, out var afterwards);
        afterwards.DiskRead.ShouldBe(0);
        afterwards.DiskWrite.ShouldBe(0);
        afterwards.DiskOps.ShouldBe(0);
    }

    [Fact]
    public void TryTake_InstanceWithNoSensorActivity_YieldsNothing()
    {
        Apply(Tick(0, started: [Start(Chrome)]), 0);

        _join.TryTake(Chrome, out var extras).ShouldBeFalse();
        extras.ShouldBe(default(InstanceExtras));
    }

    // -- lifecycle ---------------------------------------------------------------------------------------

    /// <summary>An event whose PID the poller has not reported yet cannot be attributed, and is counted.</summary>
    [Fact]
    public void OnNetwork_PidTheMapDoesNotKnow_IsCountedAsUnattributed()
    {
        _accumulators.OnNetwork(Net(Chrome.Pid, 500));

        _accumulators.UnattributedEvents.ShouldBe(1);
        _join.TryTake(Chrome, out var extras);
        extras.NetIn.ShouldBe(0);
    }

    [Fact]
    public void Apply_StartedInstance_IsRegisteredInThePidMap()
    {
        Apply(Tick(0, started: [Start(Chrome)]), 0);

        _pids.Lookup(Chrome.Pid).ShouldBe(Chrome);
    }

    /// <summary>
    /// An exiting instance has no delta this tick, so this is the only place its last bytes can be recovered.
    /// </summary>
    [Fact]
    public void Apply_ExitedInstance_LastBytesAreDrainedAgainstItsApp()
    {
        Apply(Tick(0, started: [Start(Chrome)]), 0);
        _accumulators.OnNetwork(Net(Chrome.Pid, 700));

        var appId = _registry.Lookup(Chrome)!.Value.AppId;
        Apply(Tick(1, exited: [Start(Chrome)]), 1);

        var residue = _join.DrainExited().ShouldHaveSingleItem();
        residue.AppId.ShouldBe(appId);
        residue.Extras.NetIn.ShouldBe(700);
    }

    [Fact]
    public void Apply_ExitedInstance_IsForgottenFromThePidMap()
    {
        Apply(Tick(0, started: [Start(Chrome)]), 0);
        Apply(Tick(1, exited: [Start(Chrome)]), 1);

        _pids.Lookup(Chrome.Pid).ShouldBeNull();
    }

    /// <summary>
    /// Exits are processed before starts so a PID reused inside one tick keeps the slot the new instance
    /// just claimed, rather than having it cleared by the old instance's Forget.
    /// </summary>
    [Fact]
    public void Apply_PidReusedInTheSameTick_KeepsTheNewInstance()
    {
        Apply(Tick(0, started: [Start(Chrome)]), 0);

        var reused = new ProcessKey(Chrome.Pid, CreateTime: 2);
        Apply(Tick(1, started: [Start(reused)], exited: [Start(Chrome)]), 1);

        _pids.Lookup(Chrome.Pid).ShouldBe(reused);
    }

    /// <summary>
    /// A re-baselined tick means the interval cannot be trusted, and neither can anything accumulated
    /// across it. Eight hours of sleep arriving as one second of traffic is worse than a hole in the chart.
    /// </summary>
    [Fact]
    public void Apply_RebaselinedTick_DiscardsTheAccumulatedWindow()
    {
        Apply(Tick(0, started: [Start(Chrome)]), 0);
        _accumulators.OnNetwork(Net(Chrome.Pid, 9_000_000));

        _join.Apply(Tick(1, rebaselined: true), _registry, TimeSpan.FromSeconds(1));

        _join.TryTake(Chrome, out var extras);
        extras.NetIn.ShouldBe(0);
    }

    /// <summary>
    /// A clock step does not change which processes are alive, so the PID map must survive it. It also
    /// cannot be skipped on a re-baselined tick: <b>the first tick of every session is re-baselined by
    /// definition</b> — there is no previous snapshot to measure an interval against — and that is exactly
    /// the tick on which every running process is reported as started. Dropping it once leaves every one of
    /// them unattributed for as long as it keeps running, with no error anywhere.
    /// </summary>
    [Fact]
    public void Apply_FirstTickOfTheSession_StillRegistersTheProcessesItReportsAsStarted()
    {
        _join.Apply(Tick(0, started: [Start(Chrome)], rebaselined: true), _registry, TimeSpan.Zero);

        _pids.Lookup(Chrome.Pid).ShouldBe(Chrome);

        _accumulators.OnNetwork(Net(Chrome.Pid, 1_234));
        _accumulators.UnattributedEvents.ShouldBe(0);
        _join.TryTake(Chrome, out var extras);
        extras.NetIn.ShouldBe(1_234);
    }

    [Fact]
    public void Apply_RebaselinedMidSession_KeepsAlreadyKnownInstancesAttributable()
    {
        Apply(Tick(0, started: [Start(Chrome)]), 0);

        _join.Apply(Tick(1, rebaselined: true), _registry, TimeSpan.FromSeconds(1));

        _pids.Lookup(Chrome.Pid).ShouldBe(Chrome);
    }

    // -- GPU ---------------------------------------------------------------------------------------------

    [Fact]
    public void Apply_OnTheOffSecond_CarriesTheLastGpuReadingForward()
    {
        _gpu.Returning(new GpuSample(Chrome.Pid, 40d, 1_024, 512));
        Apply(Tick(0, started: [Start(Chrome)]), 0);

        // One second later: inside the 2 s cadence, so the poller must not be asked again.
        Apply(Tick(1), 1);

        _gpu.SampleCalls.ShouldBe(1);
        _join.TryTake(Chrome, out var extras);
        extras.GpuPct.ShouldBe(40d);
        extras.VramDedicated.ShouldBe(1_024);
    }

    [Fact]
    public void Apply_AfterTheGpuInterval_TakesAFreshReading()
    {
        _gpu.Returning(new GpuSample(Chrome.Pid, 40d, 1_024, 512));
        Apply(Tick(0, started: [Start(Chrome)]), 0);

        _gpu.Returning(new GpuSample(Chrome.Pid, 10d, 2_048, 512));
        Apply(Tick(2), 2);

        _gpu.SampleCalls.ShouldBe(2);
        _join.TryTake(Chrome, out var extras);
        extras.GpuPct.ShouldBe(10d);
    }

    /// <summary>A process that stopped using the GPU must stop being charted, not keep its last value.</summary>
    [Fact]
    public void Apply_ProcessNoLongerReported_StopsBeingCharted()
    {
        _gpu.Returning(new GpuSample(Chrome.Pid, 40d, 1_024, 512));
        Apply(Tick(0, started: [Start(Chrome)]), 0);

        _gpu.Returning();
        Apply(Tick(2), 2);

        _join.GpuReadings.ShouldBe(0);
        _join.TryTake(Chrome, out var extras);
        extras.GpuPct.ShouldBe(0d);
    }

    /// <summary>
    /// A machine with no WDDM 2.x counters is a normal Tuesday. It must produce nothing rather than zeros,
    /// so the UI can show "N/A" — a zero here would be a claim we looked and found none.
    /// </summary>
    [Fact]
    public void Apply_GpuSensorUnavailable_ProducesNoReadings()
    {
        _gpu.Returning(new GpuSample(Chrome.Pid, 40d, 1_024, 512));
        _gpu.Health = SensorHealth.Unavailable("NoCounters");

        Apply(Tick(0, started: [Start(Chrome)]), 0);

        _gpu.SampleCalls.ShouldBe(0);
        _join.GpuReadings.ShouldBe(0);
    }

    /// <summary>A GPU sample for a PID the poller has not reported cannot be keyed to an instance.</summary>
    [Fact]
    public void Apply_GpuSampleForAnUnknownPid_IsIgnored()
    {
        _gpu.Returning(new GpuSample(Other.Pid, 90d, 1_024, 512));
        Apply(Tick(0, started: [Start(Chrome)]), 0);

        _join.GpuReadings.ShouldBe(0);
    }

    // -- degraded ----------------------------------------------------------------------------------------

    [Fact]
    public void Apply_EventsLostIncreased_MarksTheWindowDegraded()
    {
        Apply(Tick(0), 0);
        _join.DegradedWindow.ShouldBeFalse();

        _etw.EventsLost = 12;
        Apply(Tick(1), 1);

        _join.DegradedWindow.ShouldBeTrue();
    }

    [Fact]
    public void Apply_EventsLostUnchanged_LeavesTheWindowClean()
    {
        _etw.EventsLost = 12;
        Apply(Tick(0), 0);
        Apply(Tick(1), 1);

        _join.DegradedWindow.ShouldBeFalse();
    }

    /// <summary>
    /// EventsLost is the sum of two sessions' counters and a restarted session starts again from zero, so
    /// the value can go down. Deriving degraded from inequality would hatch every chart after any restart.
    /// </summary>
    [Fact]
    public void Apply_EventsLostDecreasedAfterASessionRestart_DoesNotMarkTheWindowDegraded()
    {
        _etw.EventsLost = 500;
        Apply(Tick(0), 0);

        _etw.EventsLost = 0;
        Apply(Tick(1), 1);

        _join.DegradedWindow.ShouldBeFalse();
    }

    [Fact]
    public void Apply_HandlerThrew_MarksTheWindowDegraded()
    {
        Apply(Tick(0), 0);

        // A DNS answer with no address list makes the handler throw, which it catches and counts rather
        // than letting it escape into a provider's callback loop.
        _accumulators.OnDns(new DnsEvent(Chrome.Pid, "example.com", null!, 1_700_000_000));
        _accumulators.HandlerErrors.ShouldBe(1);

        Apply(Tick(1), 1);
        _join.DegradedWindow.ShouldBeTrue();
    }

    // -- the factory -------------------------------------------------------------------------------------

    /// <summary>
    /// Create must subscribe the handlers, because building the accumulators and forgetting to attach them
    /// produces no error at all — just an Agent that records zero bytes for everything.
    /// </summary>
    [Fact]
    public void Create_SubscribesTheHandlersToTheSource()
    {
        var source = new FakeEtwSource();
        var join = SensorJoin.Create(source, gpu: null);

        join.Apply(Tick(0, started: [Start(Chrome)]), _registry, TimeSpan.Zero);
        source.Raise(Net(Chrome.Pid, 1_500));
        source.Raise(Disk(Chrome.Pid, 4_096, isWrite: true));

        join.TryTake(Chrome, out var extras).ShouldBeTrue();
        extras.NetIn.ShouldBe(1_500);
        extras.DiskWrite.ShouldBe(4_096);
    }

    [Fact]
    public void Create_WithNoSources_IsUsableAndYieldsNothing()
    {
        var join = SensorJoin.Create(etw: null, gpu: null);

        join.Apply(Tick(0, started: [Start(Chrome)]), _registry, TimeSpan.Zero);

        join.DegradedWindow.ShouldBeFalse();
        join.TryTake(Chrome, out var extras).ShouldBeFalse();
        extras.ShouldBe(default(InstanceExtras));
    }
}
