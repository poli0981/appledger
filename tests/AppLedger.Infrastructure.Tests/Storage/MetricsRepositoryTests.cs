using AppLedger.Core.Identity;
using AppLedger.Core.Metrics;
using AppLedger.Infrastructure.Storage;
using AppLedger.Infrastructure.Tests.TestSupport;
using Shouldly;
using Xunit;

namespace AppLedger.Infrastructure.Tests.Storage;

/// <summary>
/// Round-trips the storage contract of docs/06_DATA_MODEL.md. The point of writing this now, before the
/// collector exists to fill the tables, is that a column name that does not match a
/// <see cref="MetricRow"/> property fails here instead of silently reading back zero in the middle of v0.2.
/// </summary>
public sealed class MetricsRepositoryTests
{
    private static readonly AppId App = AppId.Parse("cat:discord");

    /// <summary>
    /// A row whose every field holds a distinct value. Distinctness is the assertion: two columns wired to
    /// each other's parameter would round-trip perfectly if they shared a value.
    /// </summary>
    private static MetricRow SampleRow(long ts) => new()
    {
        AppId = App,
        Ts = ts,
        RuntimeSeconds = 41,
        Procs = 1.5,
        ProcsMax = 3,
        CpuPct = 12.25,
        CpuPctMax = 47.5,
        CpuUserMs = 101,
        CpuKernelMs = 102,
        WsPrivate = 103,
        WsPrivateMax = 104,
        CommitBytes = 105,
        Ws = 106,
        GpuPct = 7.75,
        VramDedicated = 107,
        VramDedicatedMax = 108,
        VramShared = 109,
        IoRead = 110,
        IoWrite = 111,
        DiskRead = 112,
        DiskWrite = 113,
        DiskOps = 114,
        NetIn = 115,
        NetOut = 116,
        NetInLoopback = 117,
        NetOutLoopback = 118,
        Threads = 19.5,
        Handles = 20.25,
        HardFaults = 119,
        Degraded = true,
    };

    private static async Task<MetricsRepository> SeededRepositoryAsync(TemporaryDatabase database)
    {
        var repository = new MetricsRepository(database.Factory);

        // Every metric row references an app row, so the app has to exist first.
        await repository.UpsertAppAsync(new AppRecord(App, "Discord", AppSource.Catalog, 0.95, 100, 200));
        return repository;
    }

    [Theory]
    [InlineData(MetricTier.Minute)]
    [InlineData(MetricTier.Hour)]
    [InlineData(MetricTier.Day)]
    public async Task WriteRows_ThenReadRange_RoundTripsEveryField(MetricTier tier)
    {
        using var database = new TemporaryDatabase();
        var repository = await SeededRepositoryAsync(database);
        var written = SampleRow(1_700_000_000);

        await repository.WriteRowsAsync(tier, [written]);
        var read = await repository.ReadRangeAsync(App, tier, 0, long.MaxValue);

        read.ShouldHaveSingleItem().ShouldBe(written);
    }

    /// <summary>
    /// A rollup that runs twice for the same bucket must leave one row holding the newer values, not two
    /// rows and not an error (docs/06_DATA_MODEL.md §Rollup jobs).
    /// </summary>
    [Fact]
    public async Task WriteRows_SameBucketTwice_ReplacesRatherThanDuplicating()
    {
        using var database = new TemporaryDatabase();
        var repository = await SeededRepositoryAsync(database);
        var first = SampleRow(600);

        await repository.WriteRowsAsync(MetricTier.Minute, [first]);
        await repository.WriteRowsAsync(MetricTier.Minute, [first with { CpuPct = 99.5 }]);

        var read = await repository.ReadRangeAsync(App, MetricTier.Minute, 0, long.MaxValue);

        read.ShouldHaveSingleItem().CpuPct.ShouldBe(99.5);
    }

    /// <summary>The range is half-open, so consecutive queries neither overlap nor leave a gap.</summary>
    [Fact]
    public async Task ReadRange_IsHalfOpenAndOrderedByTimestamp()
    {
        using var database = new TemporaryDatabase();
        var repository = await SeededRepositoryAsync(database);

        await repository.WriteRowsAsync(
            MetricTier.Minute,
            [SampleRow(300), SampleRow(120), SampleRow(180), SampleRow(240)]);

        var read = await repository.ReadRangeAsync(App, MetricTier.Minute, 120, 300);

        read.Select(r => r.Ts).ShouldBe([120L, 180L, 240L]);
    }

    [Fact]
    public async Task WriteRows_EmptyBatch_TouchesNothing()
    {
        using var database = new TemporaryDatabase();
        var repository = await SeededRepositoryAsync(database);

        await repository.WriteRowsAsync(MetricTier.Minute, []);

        (await repository.ReadRangeAsync(App, MetricTier.Minute, 0, long.MaxValue)).ShouldBeEmpty();
    }

    /// <summary>
    /// An app is first seen once. Letting an update move <c>first_seen_utc</c> would quietly rewrite the
    /// beginning of its history, and nothing downstream would ever notice.
    /// </summary>
    [Fact]
    public async Task UpsertApp_SecondCall_UpdatesLastSeenButNotFirstSeen()
    {
        using var database = new TemporaryDatabase();
        var repository = new MetricsRepository(database.Factory);

        await repository.UpsertAppAsync(new AppRecord(App, "Discord", AppSource.Catalog, 0.95, 100, 200));
        await repository.UpsertAppAsync(new AppRecord(App, "Discord Canary", AppSource.Catalog, 0.95, 999, 300));

        database.Scalar($"SELECT first_seen_utc FROM apps WHERE app_id = '{App.Value}';").ShouldBe("100");
        database.Scalar($"SELECT last_seen_utc FROM apps WHERE app_id = '{App.Value}';").ShouldBe("300");
        database.Scalar($"SELECT display_name FROM apps WHERE app_id = '{App.Value}';").ShouldBe("Discord Canary");
    }

    [Fact]
    public async Task UpsertApp_StoresTheSourcePrefixAndTierTheDataModelExpects()
    {
        using var database = new TemporaryDatabase();
        var repository = new MetricsRepository(database.Factory);

        await repository.UpsertAppAsync(new AppRecord(App, "Discord", AppSource.Catalog, 0.95, 1, 2)
        {
            Tier = ProcessTierValue.ZeroTouch,
            SignatureStatus = SignatureStatus.CatalogSigned,
        });

        database.Scalar($"SELECT source FROM apps WHERE app_id = '{App.Value}';").ShouldBe("cat");
        database.Scalar($"SELECT tier FROM apps WHERE app_id = '{App.Value}';").ShouldBe("2");
        database.Scalar($"SELECT sig_status FROM apps WHERE app_id = '{App.Value}';").ShouldBe("CatalogSigned");
    }

    /// <summary>
    /// Adding a property to <see cref="MetricRow"/> without adding its column is the failure this catches:
    /// the round-trip above would still pass, because the missing field would simply come back at its
    /// default and compare equal to a sample row that also left it at default.
    /// </summary>
    [Fact]
    public void MetricRow_HasExactlyAsManyPropertiesAsTheTableHasColumns()
    {
        using var database = new TemporaryDatabase();

        var properties = typeof(MetricRow).GetProperties().Length;

        database.ColumnsOf("metrics_1m").Count.ShouldBe(properties);
    }

    /// <summary>
    /// The write path names every column explicitly. A column left out of the insert would take its
    /// default forever, which reads as "this app used no network" rather than as a bug.
    /// </summary>
    [Fact]
    public async Task WriteRows_LeavesNoColumnUnwritten()
    {
        using var database = new TemporaryDatabase();
        var repository = await SeededRepositoryAsync(database);

        await repository.WriteRowsAsync(MetricTier.Minute, [SampleRow(60)]);

        foreach (var column in database.ColumnsOf("metrics_1m"))
        {
            database.Scalar($"SELECT COUNT(*) FROM metrics_1m WHERE {column} IS NULL;")
                .ShouldBe("0", $"{column} was never written");
        }
    }
}
