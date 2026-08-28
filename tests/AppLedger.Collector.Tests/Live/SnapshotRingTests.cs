using System.Runtime.CompilerServices;
using AppLedger.Collector.Live;
using AppLedger.Core.Collection;
using AppLedger.Core.Identity;
using AppLedger.Core.Metrics;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace AppLedger.Collector.Tests.Live;

/// <summary>
/// The in-memory window the sparklines read from, and the budget arithmetic behind its size.
/// </summary>
public sealed class SnapshotRingTests
{
    private readonly ITestOutputHelper _output;

    public SnapshotRingTests(ITestOutputHelper output) => _output = output;

    private static readonly AppId Chrome = AppId.Parse("cat:chrome");
    private static readonly AppId Discord = AppId.Parse("cat:discord");

    private static AppSample Sample(AppId appId, long ts, long ioRead = 0) =>
        new() { AppId = appId, TsUtc = ts, Procs = 1, IoRead = ioRead };

    /// <summary>
    /// The measurement the ring window was chosen from. docs/05 said "3600 × apps; ~2 MB for 100 apps",
    /// and those two halves disagree by a factor of thirty: at this size an hour of 100 apps is 66 MB,
    /// a third of the entire Agent budget. If the struct ever grows, the window has to be revisited, and
    /// this is what forces that conversation instead of letting the budget quietly slip.
    /// </summary>
    [Fact]
    public void AppSample_Size_IsWhatTheRingBudgetWasCalculatedFrom()
    {
        var measured = Unsafe.SizeOf<AppSample>();
        _output.WriteLine($"AppSample is {measured} bytes; one hour of 100 apps would be "
            + $"{3600L * 100 * measured / (1024 * 1024)} MB.");

        measured.ShouldBe(SnapshotRing.SampleBytes,
            "the ring window in CollectorOptions was sized from this number; re-derive it before changing the struct");
    }

    /// <summary>
    /// Shrinking is what makes the idle profile a saving rather than a label: four of the collector's
    /// twenty megabytes come back when nobody is drawing sparklines.
    /// </summary>
    [Fact]
    public void Resize_Smaller_KeepsTheMostRecentSecondsAndDropsTheRest()
    {
        var ring = new SnapshotRing(TimeSpan.FromSeconds(10));
        for (var i = 0; i < 10; i++)
        {
            ring.Add([Sample(Chrome, 1_700_000_000 + i, ioRead: i)]);
        }

        ring.Resize(TimeSpan.FromSeconds(3));

        ring.Capacity.ShouldBe(3);
        ring.Count.ShouldBe(3);

        var kept = ring.Slice(Chrome);
        kept.Select(s => s.IoRead).ShouldBe([7L, 8L, 9L]);
    }

    [Fact]
    public void Resize_Larger_KeepsEverythingAndAcceptsMore()
    {
        var ring = new SnapshotRing(TimeSpan.FromSeconds(3));
        for (var i = 0; i < 3; i++)
        {
            ring.Add([Sample(Chrome, 1_700_000_000 + i, ioRead: i)]);
        }

        ring.Resize(TimeSpan.FromSeconds(5));
        ring.Add([Sample(Chrome, 1_700_000_003, ioRead: 3)]);

        ring.Capacity.ShouldBe(5);
        ring.Slice(Chrome).Select(s => s.IoRead).ShouldBe([0L, 1L, 2L, 3L]);
    }

    /// <summary>Resizing a partly-filled ring must not resurrect the empty slots as data.</summary>
    [Fact]
    public void Resize_RingNotYetFull_KeepsOnlyWhatItHeld()
    {
        var ring = new SnapshotRing(TimeSpan.FromSeconds(10));
        ring.Add([Sample(Chrome, 1_700_000_000, ioRead: 1)]);
        ring.Add([Sample(Chrome, 1_700_000_001, ioRead: 2)]);

        ring.Resize(TimeSpan.FromSeconds(5));

        ring.Count.ShouldBe(2);
        ring.Slice(Chrome).Select(s => s.IoRead).ShouldBe([1L, 2L]);
    }

    [Fact]
    public void Resize_ToTheSameWindow_IsANoOp()
    {
        var ring = new SnapshotRing(TimeSpan.FromSeconds(5));
        ring.Add([Sample(Chrome, 1_700_000_000, ioRead: 1)]);

        ring.Resize(TimeSpan.FromSeconds(5));

        ring.Capacity.ShouldBe(5);
        ring.Count.ShouldBe(1);
    }

    [Fact]
    public void Resize_ToNothing_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() => new SnapshotRing(TimeSpan.FromSeconds(5)).Resize(TimeSpan.Zero));

    /// <summary>The default window must fit the ~20 MB S1-lite left for everything in the collector.</summary>
    [Fact]
    public void EstimateBytes_DefaultWindowWithAHundredApps_FitsTheBudget()
    {
        var bytes = SnapshotRing.EstimateBytes(new CollectorOptions().RingWindow, liveApps: 100);

        (bytes / (1024 * 1024)).ShouldBeLessThan(8);
    }

    /// <summary>And the documented one hour would not, which is why it is not the default.</summary>
    [Fact]
    public void EstimateBytes_OneHourWithAHundredApps_WouldBreachTheBudget() =>
        (SnapshotRing.EstimateBytes(TimeSpan.FromHours(1), liveApps: 100) / (1024 * 1024))
            .ShouldBeGreaterThan(60);

    [Fact]
    public void Add_BeyondCapacity_OverwritesTheOldestSecond()
    {
        var ring = new SnapshotRing(TimeSpan.FromSeconds(3));

        for (var i = 0; i < 10; i++)
        {
            ring.Add([Sample(Chrome, 1_700_000_000 + i, ioRead: i)]);
        }

        ring.Count.ShouldBe(3);
        ring.Slice(Chrome).Select(s => s.IoRead).ShouldBe([7L, 8L, 9L]);
    }

    [Fact]
    public void Snapshot_ReturnsOldestFirst()
    {
        var ring = new SnapshotRing(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 4; i++)
        {
            ring.Add([Sample(Chrome, 1_700_000_000 + i)]);
        }

        ring.Snapshot().Select(second => second[0].TsUtc)
            .ShouldBe([1_700_000_000L, 1_700_000_001L, 1_700_000_002L, 1_700_000_003L]);
    }

    [Fact]
    public void Snapshot_WithALimit_ReturnsTheMostRecentSeconds()
    {
        var ring = new SnapshotRing(TimeSpan.FromSeconds(10));
        for (var i = 0; i < 8; i++)
        {
            ring.Add([Sample(Chrome, 1_700_000_000 + i)]);
        }

        ring.Snapshot(maxSeconds: 3).Select(second => second[0].TsUtc)
            .ShouldBe([1_700_000_005L, 1_700_000_006L, 1_700_000_007L]);
    }

    /// <summary>
    /// A second in which the app was not running is absent, not zero. It was not idle, it was gone — and a
    /// sparkline that draws a zero says something untrue about a process that did not exist.
    /// </summary>
    [Fact]
    public void Slice_SecondsWhereTheAppWasNotRunning_AreAbsentRatherThanZero()
    {
        var ring = new SnapshotRing(TimeSpan.FromSeconds(10));
        ring.Add([Sample(Chrome, 1_700_000_000, ioRead: 5)]);
        ring.Add([Sample(Discord, 1_700_000_001, ioRead: 7)]);
        ring.Add([Sample(Chrome, 1_700_000_002, ioRead: 9)]);

        var slice = ring.Slice(Chrome);

        slice.Count.ShouldBe(2);
        slice.Select(s => s.IoRead).ShouldBe([5L, 9L]);
    }

    [Fact]
    public void Slice_AppThatWasNeverSeen_IsEmpty()
    {
        var ring = new SnapshotRing(TimeSpan.FromSeconds(5));
        ring.Add([Sample(Chrome, 1_700_000_000)]);

        ring.Slice(Discord).ShouldBeEmpty();
    }

    [Fact]
    public void Clear_ForgetsEverything()
    {
        var ring = new SnapshotRing(TimeSpan.FromSeconds(5));
        ring.Add([Sample(Chrome, 1_700_000_000)]);

        ring.Clear();

        ring.Count.ShouldBe(0);
        ring.Slice(Chrome).ShouldBeEmpty();
    }

    [Fact]
    public void Constructor_WindowShorterThanASecond_IsRefused() =>
        Should.Throw<ArgumentOutOfRangeException>(() => new SnapshotRing(TimeSpan.Zero));

    [Fact]
    public void Capacity_MatchesTheWindowInSeconds() =>
        new SnapshotRing(TimeSpan.FromMinutes(5)).Capacity.ShouldBe(300);
}
