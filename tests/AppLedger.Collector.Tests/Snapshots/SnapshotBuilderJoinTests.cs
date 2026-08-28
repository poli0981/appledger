using AppLedger.Collector.Processes;
using AppLedger.Collector.Snapshots;
using AppLedger.Collector.Tests.TestSupport;
using AppLedger.Core.Identity;
using AppLedger.Core.Metrics;
using AppLedger.Core.Process;
using AppLedger.Core.Rollup;
using Shouldly;
using Xunit;

namespace AppLedger.Collector.Tests.Snapshots;

/// <summary>
/// Where the sensors meet the per-app arithmetic. Without this, every network, disk and GPU field on
/// <see cref="AppSample"/> is structurally present and permanently zero — a build that is clean, tests that
/// are green, and six months of history recording that nothing ever touched the network.
/// </summary>
public sealed class SnapshotBuilderJoinTests
{
    private const string ChromeExe = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
    private const string NotepadExe = @"C:\Windows\System32\notepad.exe";

    private readonly FakePolicyGuard _policy = new();
    private readonly FakeProcessEnricher _enricher = new();
    private readonly ProcessTable _table = new(logicalCpuCount: 4);
    private readonly InstanceRegistry _registry;
    private readonly SnapshotBuilder _snapshots;
    private readonly StubMetrics _metrics = new();

    public SnapshotBuilderJoinTests()
    {
        var resolver = new FallbackIdentityResolver(_policy, new InstallRootHeuristic(FakePolicyGuard.Boundaries));
        _registry = new InstanceRegistry(_policy, _enricher, resolver);
        _snapshots = new SnapshotBuilder(_registry);
    }

    private static RawProcessSample Sample(int pid, string imageName) => new()
    {
        Key = new ProcessKey(pid, 1),
        ImageName = imageName,
        SessionId = 1,
        WorkingSetPrivate = 1024,
        WorkingSet = 1024,
        PagefileUsage = 1024,
        ThreadCount = 2,
        HandleCount = 20,
    };

    private IReadOnlyList<AppSample> Tick(RawProcessSample[] snapshot, int second, bool withSensors = true)
    {
        var tick = _table.Update(snapshot, 1_700_000_000 + second, TimeSpan.FromSeconds(second));
        _registry.Apply(tick);
        return _snapshots.Build(tick, withSensors ? _metrics : null);
    }

    private void WithChrome(params int[] pids)
    {
        foreach (var pid in pids)
        {
            _enricher.WithImagePath(new ProcessKey(pid, 1), ChromeExe);
        }
    }

    [Fact]
    public void Build_InstanceWithNetworkBytes_AddsThemToItsAppSample()
    {
        WithChrome(100);
        Tick([Sample(100, "chrome.exe")], second: 0);

        _metrics.Set(new ProcessKey(100, 1), new InstanceExtras { NetIn = 5_000, NetOut = 1_200, DiskRead = 4_096 });
        var samples = Tick([Sample(100, "chrome.exe")], second: 1);

        var chrome = samples.ShouldHaveSingleItem();
        chrome.NetIn.ShouldBe(5_000);
        chrome.NetOut.ShouldBe(1_200);
        chrome.DiskRead.ShouldBe(4_096);
    }

    /// <summary>Chrome is forty PIDs and one row, and that has to hold for bytes as much as for CPU.</summary>
    [Fact]
    public void Build_TwoInstancesOfOneApp_SumsTheirNetworkAndDiskBytes()
    {
        WithChrome(100, 101);
        Tick([Sample(100, "chrome.exe"), Sample(101, "chrome.exe")], second: 0);

        _metrics.Set(new ProcessKey(100, 1), new InstanceExtras { NetIn = 300, DiskWrite = 1_000, DiskOps = 2 });
        _metrics.Set(new ProcessKey(101, 1), new InstanceExtras { NetIn = 700, DiskWrite = 500, DiskOps = 3 });

        var chrome = Tick([Sample(100, "chrome.exe"), Sample(101, "chrome.exe")], second: 1).ShouldHaveSingleItem();

        chrome.Procs.ShouldBe(2);
        chrome.NetIn.ShouldBe(1_000);
        chrome.DiskWrite.ShouldBe(1_500);
        chrome.DiskOps.ShouldBe(5);
    }

    /// <summary>
    /// GPU follows CPU's convention: a browser's processes each taking a slice of one engine is one browser,
    /// not four hundred percent of a GPU.
    /// </summary>
    [Fact]
    public void Build_GpuAcrossManyProcessesOfOneApp_SumsAndCapsAtOneHundred()
    {
        WithChrome(100, 101, 102);
        var snapshot = new[] { Sample(100, "chrome.exe"), Sample(101, "chrome.exe"), Sample(102, "chrome.exe") };
        Tick(snapshot, second: 0);

        foreach (var pid in (int[])[100, 101, 102])
        {
            _metrics.Set(new ProcessKey(pid, 1), new InstanceExtras { GpuPct = 45d, VramDedicated = 1_000 });
        }

        var chrome = Tick(snapshot, second: 1).ShouldHaveSingleItem();

        chrome.GpuPct.ShouldBe(100d);
        chrome.VramDedicated.ShouldBe(3_000);
    }

    /// <summary>Two apps must not share a byte, however close together their PIDs are.</summary>
    [Fact]
    public void Build_TwoApps_KeepTheirBytesApart()
    {
        WithChrome(100);
        _enricher.WithImagePath(new ProcessKey(200, 1), NotepadExe);
        var snapshot = new[] { Sample(100, "chrome.exe"), Sample(200, "notepad.exe") };
        Tick(snapshot, second: 0);

        _metrics.Set(new ProcessKey(100, 1), new InstanceExtras { NetIn = 900 });
        _metrics.Set(new ProcessKey(200, 1), new InstanceExtras { NetIn = 11 });

        var samples = Tick(snapshot, second: 1);

        samples.Count.ShouldBe(2);
        samples.Sum(s => s.NetIn).ShouldBe(911);
        samples.ShouldContain(s => s.NetIn == 900);
        samples.ShouldContain(s => s.NetIn == 11);
    }

    [Fact]
    public void Build_ExitResidue_IsChargedToASurvivingInstanceOfTheSameApp()
    {
        WithChrome(100, 101);
        var both = new[] { Sample(100, "chrome.exe"), Sample(101, "chrome.exe") };
        Tick(both, second: 0);

        var appId = _registry.Lookup(new ProcessKey(100, 1))!.Value.AppId;
        _metrics.Exited.Add((appId, new InstanceExtras { NetOut = 640 }));

        var chrome = Tick([Sample(100, "chrome.exe")], second: 1).ShouldHaveSingleItem();

        chrome.NetOut.ShouldBe(640);
        _snapshots.ExitResidueDropped.ShouldBe(0);
    }

    /// <summary>
    /// When the last instance of an app goes, its final bytes have nowhere honest to go: a sample means
    /// "this app ran this second", and inventing one would put a row in the list naming nothing the user can
    /// find. Dropped — but counted, because a silent loss is indistinguishable from a quiet machine.
    /// </summary>
    [Fact]
    public void Build_ExitResidueForAnAppWithNoSurvivors_IsDroppedAndCounted()
    {
        WithChrome(100);
        _enricher.WithImagePath(new ProcessKey(200, 1), NotepadExe);
        Tick([Sample(100, "chrome.exe"), Sample(200, "notepad.exe")], second: 0);

        var chromeId = _registry.Lookup(new ProcessKey(100, 1))!.Value.AppId;
        _metrics.Exited.Add((chromeId, new InstanceExtras { NetOut = 4_000 }));

        var samples = Tick([Sample(200, "notepad.exe")], second: 1);

        samples.ShouldHaveSingleItem().NetOut.ShouldBe(0);
        _snapshots.ExitResidueDropped.ShouldBe(1);
    }

    [Fact]
    public void Build_DegradedWindow_MarksEverySampleDegraded()
    {
        WithChrome(100);
        _enricher.WithImagePath(new ProcessKey(200, 1), NotepadExe);
        var snapshot = new[] { Sample(100, "chrome.exe"), Sample(200, "notepad.exe") };
        Tick(snapshot, second: 0);

        _metrics.DegradedWindow = true;
        var samples = Tick(snapshot, second: 1);

        samples.Count.ShouldBe(2);
        samples.ShouldAllBe(s => s.Degraded);
    }

    /// <summary>
    /// Lite mode passes no sensor source at all, and must be indistinguishable from the pipeline as it
    /// behaved before the join existed — zeros there mean "not collected", not "collected as zero".
    /// </summary>
    [Fact]
    public void Build_WithNoSensorSource_LeavesEverySensorFieldAtZero()
    {
        WithChrome(100);
        Tick([Sample(100, "chrome.exe")], second: 0, withSensors: false);

        _metrics.Set(new ProcessKey(100, 1), new InstanceExtras { NetIn = 5_000, GpuPct = 80d });
        var chrome = Tick([Sample(100, "chrome.exe")], second: 1, withSensors: false).ShouldHaveSingleItem();

        chrome.NetIn.ShouldBe(0);
        chrome.GpuPct.ShouldBe(0d);
        chrome.Procs.ShouldBe(1);
    }

    // -- the GPU carry-forward arithmetic, proved rather than argued --------------------------------------

    private static AppSample GpuSample(long second, double gpuPct) => new()
    {
        AppId = AppId.Parse("cat:chrome"),
        TsUtc = 1_700_000_000 + second,
        Procs = 1,
        GpuPct = gpuPct,
    };

    /// <summary>
    /// The reading is taken every 2 s and used on both seconds. <c>Rollup.FromSamples</c> divides by the
    /// sample count, so thirty readings appearing twice across sixty samples average to the mean of the
    /// readings — carrying forward is unbiased, not double-counted.
    /// </summary>
    [Fact]
    public void FromSamples_GpuCarriedForward_RollsUpToTheMeanOfTheReadings()
    {
        // Readings alternate 20 and 40 every two seconds: mean of the readings is 30.
        var samples = Enumerable.Range(0, 60)
            .Select(i => GpuSample(i, (i / 2 % 2) == 0 ? 20d : 40d))
            .ToList();

        Rollup.FromSamples(1_700_000_000, samples).GpuPct.ShouldBe(30d);
    }

    /// <summary>The rejected alternative, kept executable so the rationale cannot rot into a comment.</summary>
    [Fact]
    public void FromSamples_GpuZeroedOnTheOffSecond_WouldReportExactlyHalf()
    {
        var samples = Enumerable.Range(0, 60)
            .Select(i => GpuSample(i, i % 2 == 1 ? 0d : ((i / 2 % 2) == 0 ? 20d : 40d)))
            .ToList();

        Rollup.FromSamples(1_700_000_000, samples).GpuPct.ShouldBe(15d);
    }

    // -- budget ------------------------------------------------------------------------------------------

    /// <summary>
    /// Passed by <c>in</c>/<c>out</c> and never boxed, so this never reaches the heap — but it is on the
    /// 1 Hz path for every live instance, and a struct that quietly grows is how a budget slips.
    /// </summary>
    [Fact]
    public void InstanceExtras_Size_IsWhatTheJoinBudgetWasCalculatedFrom() =>
        System.Runtime.CompilerServices.Unsafe.SizeOf<InstanceExtras>().ShouldBe(80);

    /// <summary>
    /// The join must not turn the tick path into an allocating one. S1-lite left roughly 20 MB for every
    /// structure in the collector combined, and per-tick garbage at 1 Hz is a GC pressure problem long
    /// before it is a memory one.
    /// </summary>
    [Fact]
    public void Build_WithASensorSource_AllocatesNoMoreThanWithoutOne()
    {
        WithChrome(100, 101);
        var snapshot = new[] { Sample(100, "chrome.exe"), Sample(101, "chrome.exe") };

        var tick = _table.Update(snapshot, 1_700_000_000, TimeSpan.Zero);
        _registry.Apply(tick);
        tick = _table.Update(snapshot, 1_700_000_001, TimeSpan.FromSeconds(1));
        _registry.Apply(tick);

        // Warm both paths so first-call JIT and dictionary growth are not charged to the measurement.
        for (var i = 0; i < 50; i++)
        {
            _snapshots.Build(tick);
            _snapshots.Build(tick, IInstanceMetricsSource.None);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            _snapshots.Build(tick);
        }

        var withoutSensors = GC.GetAllocatedBytesForCurrentThread() - before;

        before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            _snapshots.Build(tick, IInstanceMetricsSource.None);
        }

        var withSensors = GC.GetAllocatedBytesForCurrentThread() - before;

        withSensors.ShouldBe(withoutSensors);
    }

    /// <summary>Scripted per-instance sensor totals, so the builder's tests need no accumulators.</summary>
    private sealed class StubMetrics : IInstanceMetricsSource
    {
        private readonly Dictionary<ProcessKey, InstanceExtras> _byKey = [];

        public bool DegradedWindow { get; set; }

        internal List<(AppId AppId, InstanceExtras Extras)> Exited { get; } = [];

        internal void Set(ProcessKey key, InstanceExtras extras) => _byKey[key] = extras;

        public bool TryTake(ProcessKey key, out InstanceExtras extras)
        {
            if (!_byKey.Remove(key, out extras))
            {
                return false;
            }

            return true;
        }

        public IReadOnlyList<(AppId AppId, InstanceExtras Extras)> DrainExited()
        {
            var drained = Exited.ToList();
            Exited.Clear();
            return drained;
        }
    }
}
