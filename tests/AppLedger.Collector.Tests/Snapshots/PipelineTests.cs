using AppLedger.Collector.Processes;
using AppLedger.Collector.Rollups;
using AppLedger.Collector.Snapshots;
using AppLedger.Collector.Tests.TestSupport;
using AppLedger.Core.Identity;
using AppLedger.Core.Metrics;
using AppLedger.Core.Policy;
using AppLedger.Core.Process;
using Shouldly;
using Xunit;

namespace AppLedger.Collector.Tests.Snapshots;

/// <summary>
/// The pipeline of docs/19_TESTING.md §Layers: scripted samples through the process table, the registry,
/// the snapshot builder and the minute rollup. No Windows APIs, so it runs anywhere.
/// </summary>
public sealed class PipelineTests
{
    // The heuristic returns the last directory before the boundary, which for a vendor layout is the
    // vendor folder rather than the product folder - literally what docs/03 specifies. It is also why the
    // root fallback is step 10 of ten: every Google product under Program Files\Google shares this id
    // until a catalog rule claims it at v0.3.
    private const string ChromeRoot = @"C:\Program Files\Google";
    private const string ChromeExe = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
    private const string NotepadExe = @"C:\Windows\System32\notepad.exe";

    private readonly FakePolicyGuard _policy = new();
    private readonly FakeProcessEnricher _enricher = new();
    private readonly ProcessTable _table = new(logicalCpuCount: 4);
    private readonly InstanceRegistry _registry;
    private readonly SnapshotBuilder _snapshots;

    public PipelineTests()
    {
        var resolver = new FallbackIdentityResolver(_policy, new InstallRootHeuristic(FakePolicyGuard.Boundaries));
        _registry = new InstanceRegistry(_policy, _enricher, resolver);
        _snapshots = new SnapshotBuilder(_registry);
    }

    private static RawProcessSample Sample(int pid, string imageName, long userTime = 0, long readBytes = 0, long ws = 1024)
        => new()
        {
            Key = new ProcessKey(pid, 1),
            ImageName = imageName,
            SessionId = 1,
            UserTime = userTime,
            ReadTransferCount = readBytes,
            WorkingSetPrivate = ws,
            WorkingSet = ws,
            PagefileUsage = ws,
            ThreadCount = 2,
            HandleCount = 20,
        };

    private IReadOnlyList<AppSample> Tick(RawProcessSample[] snapshot, int second)
    {
        var tick = _table.Update(snapshot, 1_700_000_000 + second, TimeSpan.FromSeconds(second));
        _registry.Apply(tick);
        return _snapshots.Build(tick);
    }

    /// <summary>
    /// The product's central claim, in one test. Chrome is many PIDs and one row; the user thinks in apps
    /// and Windows exposes processes, and this is the arithmetic that bridges them.
    /// </summary>
    [Fact]
    public void Build_ManyProcessesOfOneApp_ProduceASingleSample()
    {
        for (var pid = 100; pid < 106; pid++)
        {
            _enricher.WithImagePath(new ProcessKey(pid, 1), ChromeExe);
        }

        RawProcessSample[] first = [.. Enumerable.Range(100, 6).Select(p => Sample(p, "chrome.exe"))];
        RawProcessSample[] second = [.. Enumerable.Range(100, 6).Select(p => Sample(p, "chrome.exe", readBytes: 1_000))];

        Tick(first, 0);
        var samples = Tick(second, 1);

        var chrome = samples.ShouldHaveSingleItem();
        chrome.AppId.ShouldBe(AppId.Root(ChromeRoot));
        chrome.Procs.ShouldBe(6);
        chrome.IoRead.ShouldBe(6_000);
        chrome.Threads.ShouldBe(12);
    }

    [Fact]
    public void Build_ProcessesOfDifferentApps_AreKeptApart()
    {
        _enricher.WithImagePath(new ProcessKey(100, 1), ChromeExe);
        _enricher.WithImagePath(new ProcessKey(200, 1), NotepadExe);

        Tick([Sample(100, "chrome.exe"), Sample(200, "notepad.exe")], 0);
        var samples = Tick([Sample(100, "chrome.exe", readBytes: 500), Sample(200, "notepad.exe", readBytes: 90)], 1);

        samples.Count.ShouldBe(2);
        samples.Single(s => s.AppId == AppId.Windows).IoRead.ShouldBe(90);
        samples.Single(s => s.AppId == AppId.Root(ChromeRoot)).IoRead.ShouldBe(500);
    }

    /// <summary>
    /// A Tier-0 image is a Windows component decided from the path alone, so notepad and the print spooler
    /// are one <c>sys:windows</c> row rather than two anonymous root hashes.
    /// </summary>
    [Fact]
    public void Build_ProcessesUnderTheWindowsRoot_AllBecomeSysWindows()
    {
        _enricher.WithImagePath(new ProcessKey(100, 1), NotepadExe);
        _enricher.WithImagePath(new ProcessKey(200, 1), @"C:\Windows\System32\spoolsv.exe");

        Tick([Sample(100, "notepad.exe"), Sample(200, "spoolsv.exe")], 0);
        var samples = Tick([Sample(100, "notepad.exe"), Sample(200, "spoolsv.exe")], 1);

        samples.ShouldHaveSingleItem().AppId.ShouldBe(AppId.Windows);
        samples[0].Procs.ShouldBe(2);
    }

    /// <summary>
    /// CPU percentages add across an app's processes and are capped once at the app level. Forty renderers
    /// at 3 % is one browser at 100 %, not 120 - a number over 100 on a percentage axis reads as a bug.
    /// </summary>
    [Fact]
    public void Build_AppCpuAcrossManyProcesses_IsCappedAtOneHundred()
    {
        for (var pid = 100; pid < 140; pid++)
        {
            _enricher.WithImagePath(new ProcessKey(pid, 1), ChromeExe);
        }

        RawProcessSample[] first = [.. Enumerable.Range(100, 40).Select(p => Sample(p, "chrome.exe"))];

        // 400 ms of CPU each on a 4-core box is 10 % per process.
        RawProcessSample[] busy = [.. Enumerable.Range(100, 40).Select(p => Sample(p, "chrome.exe", userTime: 400 * 10_000))];

        Tick(first, 0);
        Tick(busy, 1).ShouldHaveSingleItem().CpuPct.ShouldBe(100d);
    }

    /// <summary>
    /// Zero-touch means zero touch, all the way through the pipeline. The registry decides the tier from
    /// the image name before enrichment, so a protected process is never even offered to the enricher.
    /// </summary>
    [Fact]
    public void Apply_ZeroTouchProcess_IsNeverEnriched()
    {
        Tick([Sample(100, "lsass.exe"), Sample(200, "chrome.exe")], 0);

        _enricher.Enriched.ShouldNotContain(new ProcessKey(100, 1));
        _enricher.Enriched.ShouldContain(new ProcessKey(200, 1));
        _registry.Lookup(new ProcessKey(100, 1))!.Value.Tier.ShouldBe(ProcessTier.ZeroTouch);
    }

    /// <summary>
    /// A Tier-2 process still counts. Its identity comes from the image name alone, which is exactly what
    /// docs/11 promises: counters from the system-wide snapshot, no handle.
    /// </summary>
    [Fact]
    public void Build_ZeroTouchProcess_StillContributesItsCounters()
    {
        Tick([Sample(100, "lsass.exe")], 0);

        var sample = Tick([Sample(100, "lsass.exe", readBytes: 4_096)], 1).ShouldHaveSingleItem();

        sample.IoRead.ShouldBe(4_096);
        sample.AppId.Value.ShouldStartWith("root:");
    }

    /// <summary>Resolution happens once per instance, not once per second (ADR-4).</summary>
    [Fact]
    public void Apply_AcrossManyTicks_EnrichesEachInstanceExactlyOnce()
    {
        _enricher.WithImagePath(new ProcessKey(100, 1), ChromeExe);

        for (var second = 0; second < 10; second++)
        {
            Tick([Sample(100, "chrome.exe")], second);
        }

        _enricher.Enriched.Count(k => k.Pid == 100).ShouldBe(1);
    }

    [Fact]
    public void Apply_InstanceExits_IsForgotten()
    {
        _enricher.WithImagePath(new ProcessKey(100, 1), ChromeExe);

        Tick([Sample(100, "chrome.exe"), Sample(200, "notepad.exe")], 0);
        _registry.Count.ShouldBe(2);

        Tick([Sample(200, "notepad.exe")], 1);

        _registry.Count.ShouldBe(1);
        _registry.Lookup(new ProcessKey(100, 1)).ShouldBeNull();
    }

    /// <summary>
    /// A re-baselined tick carries no deltas, so it produces no samples. A zero would be a claim we cannot
    /// make about a second we did not measure.
    /// </summary>
    [Fact]
    public void Build_RebaselinedTick_ProducesNoSamples() =>
        Tick([Sample(100, "chrome.exe")], 0).ShouldBeEmpty();

    /// <summary>
    /// A process that appeared and vanished inside one interval has no resolution. Charting it under an
    /// invented id would put a row in the apps list naming nothing the user can find.
    /// </summary>
    [Fact]
    public void Build_DeltaWithNoResolution_IsCountedRatherThanInvented()
    {
        var orphan = new ProcessDelta { Key = new ProcessKey(999, 9), ImageName = "ghost.exe", IoRead = 10 };
        var tick = new ProcessTick(1_700_000_060, [orphan], [], [], Rebaselined: false);

        _snapshots.Build(tick).ShouldBeEmpty();
        _snapshots.UnattributedInstances.ShouldBe(1);
    }

    [Fact]
    public void Build_SamplesAreOrderedByAppId()
    {
        _enricher.WithImagePath(new ProcessKey(100, 1), ChromeExe);
        _enricher.WithImagePath(new ProcessKey(200, 1), NotepadExe);
        _enricher.WithImagePath(new ProcessKey(300, 1), @"C:\Program Files\Zed\zed.exe");

        Tick([Sample(100, "chrome.exe"), Sample(200, "notepad.exe"), Sample(300, "zed.exe")], 0);
        var samples = Tick([Sample(100, "chrome.exe"), Sample(200, "notepad.exe"), Sample(300, "zed.exe")], 1);

        samples.Select(s => s.AppId.Value).ShouldBe(samples.Select(s => s.AppId.Value).Order(StringComparer.Ordinal));
    }
}
