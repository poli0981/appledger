using AppLedger.Collector.Rollups;
using AppLedger.Core.Identity;
using AppLedger.Core.Metrics;
using Shouldly;
using Xunit;

namespace AppLedger.Collector.Tests.Rollups;

/// <summary>
/// The bucketing half of the minute rollup. The arithmetic is <c>Rollup.FromSamples</c> in Core and is
/// golden-tested there; what is tested here is which samples belong to which minute and when a minute is
/// finished.
/// </summary>
public sealed class MinuteRollupTests
{
    private static readonly AppId Chrome = AppId.Parse("cat:chrome");
    private static readonly AppId Discord = AppId.Parse("cat:discord");

    private static AppSample Sample(AppId appId, long tsUtc, long ioRead = 100, double cpu = 10) => new()
    {
        AppId = appId,
        TsUtc = tsUtc,
        Procs = 1,
        CpuPct = cpu,
        IoRead = ioRead,
        WsPrivate = 2_048,
    };

    [Theory]
    [InlineData(1_700_000_000, 1_699_999_980)]
    [InlineData(1_700_000_059, 1_700_000_040)]
    [InlineData(1_700_000_060, 1_700_000_040)]
    [InlineData(0, 0)]
    [InlineData(59, 0)]
    [InlineData(60, 60)]
    public void BucketOf_RoundsDownToTheMinute(long ts, long expected) =>
        MinuteRollup.BucketOf(ts).ShouldBe(expected);

    /// <summary>
    /// C#'s <c>%</c> is a remainder, not a modulo, so a negative timestamp would round *up* and produce a
    /// bucket start in the future that never closes. A clock set to 1969 during setup is the only way to
    /// get here, and it should not wedge the collector.
    /// </summary>
    [Theory]
    [InlineData(-1, -60)]
    [InlineData(-59, -60)]
    [InlineData(-60, -60)]
    [InlineData(-61, -120)]
    public void BucketOf_NegativeTimestamps_StillRoundDown(long ts, long expected) =>
        MinuteRollup.BucketOf(ts).ShouldBe(expected);

    [Fact]
    public void Add_SamplesWithinOneMinute_CompleteNothingYet()
    {
        var rollup = new MinuteRollup();

        for (var second = 0; second < 60; second++)
        {
            rollup.Add([Sample(Chrome, 1_700_000_040 + second)]).ShouldBeEmpty();
        }

        rollup.BufferedApps.ShouldBe(1);
    }

    [Fact]
    public void Add_FirstSampleOfTheNextMinute_ClosesThePreviousOne()
    {
        var rollup = new MinuteRollup();
        for (var second = 0; second < 60; second++)
        {
            rollup.Add([Sample(Chrome, 1_700_000_040 + second, ioRead: 10)]);
        }

        var rows = rollup.Add([Sample(Chrome, 1_700_000_100)]);

        var row = rows.ShouldHaveSingleItem();
        row.Ts.ShouldBe(1_700_000_040);
        row.RuntimeSeconds.ShouldBe(60);
        row.IoRead.ShouldBe(600);
        rollup.CurrentBucketStartUtc.ShouldBe(1_700_000_100);
    }

    [Fact]
    public void Add_ManyApps_ProduceOneRowEach()
    {
        var rollup = new MinuteRollup();
        rollup.Add([Sample(Chrome, 1_700_000_040), Sample(Discord, 1_700_000_040)]);
        rollup.Add([Sample(Chrome, 1_700_000_041), Sample(Discord, 1_700_000_041)]);

        var rows = rollup.Add([Sample(Chrome, 1_700_000_100)]);

        rows.Count.ShouldBe(2);
        rows.Select(r => r.AppId.Value).ShouldBe(["cat:chrome", "cat:discord"]);
        rows.ShouldAllBe(r => r.RuntimeSeconds == 2);
    }

    /// <summary>
    /// An app that ran for twenty seconds of a minute produces a twenty-second row, not a minute row with
    /// two thirds of zeros. <c>runtime_s</c> is the sample count, and averages divide by it.
    /// </summary>
    [Fact]
    public void Add_AppPresentForPartOfTheMinute_ProducesAPartialRow()
    {
        var rollup = new MinuteRollup();
        for (var second = 0; second < 20; second++)
        {
            rollup.Add([Sample(Chrome, 1_700_000_040 + second, cpu: 50)]);
        }

        var row = rollup.Add([Sample(Chrome, 1_700_000_100)]).ShouldHaveSingleItem();

        row.RuntimeSeconds.ShouldBe(20);
        row.CpuPct.ShouldBe(50, 0.01);
    }

    /// <summary>
    /// Shutdown must not throw away a partial minute. The Agent stops at some arbitrary point in a bucket,
    /// and losing up to 59 seconds of every session would show as gaps in the history.
    /// </summary>
    [Fact]
    public void Flush_PartialBucket_IsStillWritten()
    {
        var rollup = new MinuteRollup();
        rollup.Add([Sample(Chrome, 1_700_000_040)]);
        rollup.Add([Sample(Chrome, 1_700_000_041)]);

        var rows = rollup.Flush();

        rows.ShouldHaveSingleItem().RuntimeSeconds.ShouldBe(2);
        rollup.BufferedApps.ShouldBe(0);
        rollup.Flush().ShouldBeEmpty();
    }

    /// <summary>
    /// A clock stepped backwards delivers samples for a minute that has already been written. Folding them
    /// in would change a row that is on disk, so they are dropped and counted instead.
    /// </summary>
    [Fact]
    public void Add_SamplesForAnAlreadyClosedBucket_AreDroppedAndCounted()
    {
        var rollup = new MinuteRollup();
        rollup.Add([Sample(Chrome, 1_700_000_100)]);
        rollup.Add([Sample(Chrome, 1_700_000_160)]);

        rollup.Add([Sample(Chrome, 1_700_000_105), Sample(Discord, 1_700_000_105)]).ShouldBeEmpty();

        rollup.LateSamples.ShouldBe(2);
        rollup.CurrentBucketStartUtc.ShouldBe(1_700_000_160);
    }

    [Fact]
    public void Add_EmptySecond_IsIgnored()
    {
        var rollup = new MinuteRollup();

        rollup.Add([]).ShouldBeEmpty();

        rollup.CurrentBucketStartUtc.ShouldBe(-1);
        rollup.BufferedApps.ShouldBe(0);
    }

    /// <summary>
    /// A minute in which nothing ran produces no row at all, rather than a row of zeros. Rows exist only
    /// while an app is running, which is what keeps the database inside the 300 MB budget of docs/06.
    /// </summary>
    [Fact]
    public void Add_GapBetweenMinutes_ProducesNoEmptyRows()
    {
        var rollup = new MinuteRollup();
        rollup.Add([Sample(Chrome, 1_700_000_040)]);

        // Ten minutes later: one row for the minute that had a sample, nothing for the nine in between.
        var rows = rollup.Add([Sample(Chrome, 1_700_000_640)]);

        rows.ShouldHaveSingleItem().Ts.ShouldBe(1_700_000_040);
    }
}
