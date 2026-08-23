using System.Globalization;
using System.Text.Json;
using AppLedger.Core.Identity;
using AppLedger.Core.Metrics;
using AppLedger.Core.Rollup;
using AppLedger.Core.Time;
using Shouldly;
using Xunit;

namespace AppLedger.Core.Tests.Rollup;

/// <summary>
/// The rollup arithmetic of docs/05_COLLECTOR.md §Rollup math. Every chart older than an hour is drawn
/// from these numbers, and the hourly SQL in docs/06 is written to agree with this implementation, so a
/// silent change here would quietly rewrite history.
/// </summary>
public sealed class RollupTests
{
    private static readonly AppId Discord = AppId.Catalog("discord");
    private const long MinuteStart = 1_787_443_200; // an arbitrary but fixed minute boundary

    private static AppSample Sample(long ts, int procs, double cpu, long wsPrivate, long netIn, bool degraded = false) => new()
    {
        AppId = Discord,
        TsUtc = ts,
        Procs = procs,
        CpuPct = cpu,
        WsPrivate = wsPrivate,
        CommitBytes = wsPrivate + 1_000,
        Ws = wsPrivate + 2_000,
        NetIn = netIn,
        NetOut = netIn / 2,
        DiskRead = 100,
        DiskWrite = 200,
        DiskOps = 3,
        IoRead = 400,
        IoWrite = 500,
        CpuUserMs = 10,
        CpuKernelMs = 5,
        Threads = 20,
        Handles = 300,
        HardFaults = 1,
        GpuPct = cpu / 2,
        VramDedicated = wsPrivate / 2,
        VramShared = wsPrivate / 4,
        Degraded = degraded,
    };

    /// <summary>The hand-checkable case: five seconds whose expected row is committed as a golden file.</summary>
    [Fact]
    public void FromSamples_MatchesGoldenRow()
    {
        var samples = new[]
        {
            Sample(MinuteStart + 0, procs: 1, cpu: 10, wsPrivate: 1_000, netIn: 100),
            Sample(MinuteStart + 1, procs: 2, cpu: 20, wsPrivate: 2_000, netIn: 200),
            Sample(MinuteStart + 2, procs: 3, cpu: 30, wsPrivate: 3_000, netIn: 300),
            Sample(MinuteStart + 3, procs: 4, cpu: 40, wsPrivate: 4_000, netIn: 400),
            Sample(MinuteStart + 4, procs: 5, cpu: 50, wsPrivate: 5_000, netIn: 500),
        };

        var row = AppLedger.Core.Rollup.Rollup.FromSamples(MinuteStart, samples);

        var golden = LoadGolden("minute-from-five-seconds.json");
        AssertMatchesGolden(row, golden);
    }

    /// <summary>
    /// Sixty seconds, asserting the shape of each aggregation kind rather than a hand-typed number: sums
    /// add, averages divide by the sample count, maxima take the peak.
    /// </summary>
    [Fact]
    public void FromSamples_SumsAveragesAndMaxima()
    {
        var samples = Enumerable.Range(0, 60)
            .Select(i => Sample(MinuteStart + i, procs: i % 5, cpu: i, wsPrivate: i * 1_000L, netIn: i * 10L))
            .ToArray();

        var row = AppLedger.Core.Rollup.Rollup.FromSamples(MinuteStart, samples);

        row.RuntimeSeconds.ShouldBe(60);
        row.NetIn.ShouldBe(samples.Sum(s => s.NetIn));
        row.DiskRead.ShouldBe(60 * 100);
        row.CpuUserMs.ShouldBe(60 * 10);
        row.CpuPct.ShouldBe(samples.Average(s => s.CpuPct), 0.05);
        row.CpuPctMax.ShouldBe(59);
        row.WsPrivateMax.ShouldBe(59_000);
        row.ProcsMax.ShouldBe(4);
        row.Degraded.ShouldBeFalse();
    }

    /// <summary>
    /// A partial minute is normal: an app that starts at second 45 contributes fifteen samples, and its
    /// averages must divide by fifteen, not sixty, or the chart would show a dip that never happened.
    /// </summary>
    [Fact]
    public void FromSamples_PartialBucket_AveragesOverSamplesPresent()
    {
        var samples = Enumerable.Range(0, 15)
            .Select(i => Sample(MinuteStart + 45 + i, procs: 2, cpu: 50, wsPrivate: 1_000, netIn: 10))
            .ToArray();

        var row = AppLedger.Core.Rollup.Rollup.FromSamples(MinuteStart, samples);

        row.RuntimeSeconds.ShouldBe(15);
        row.CpuPct.ShouldBe(50);
        row.Procs.ShouldBe(2);
        row.NetIn.ShouldBe(150);
    }

    [Fact]
    public void FromSamples_AnyDegradedSecond_MarksTheRow()
    {
        var samples = new[]
        {
            Sample(MinuteStart, 1, 10, 1_000, 100),
            Sample(MinuteStart + 1, 1, 10, 1_000, 100, degraded: true),
        };

        AppLedger.Core.Rollup.Rollup.FromSamples(MinuteStart, samples).Degraded.ShouldBeTrue();
    }

    [Fact]
    public void FromSamples_EmptyOrMixedApps_Throws()
    {
        Should.Throw<ArgumentException>(() => AppLedger.Core.Rollup.Rollup.FromSamples(MinuteStart, []));

        var mixed = new[]
        {
            Sample(MinuteStart, 1, 10, 1_000, 100),
            Sample(MinuteStart + 1, 1, 10, 1_000, 100) with { AppId = AppId.Catalog("chrome") },
        };
        Should.Throw<ArgumentException>(() => AppLedger.Core.Rollup.Rollup.FromSamples(MinuteStart, mixed));
    }

    /// <summary>
    /// The weighting rule: an app that ran five minutes of an hour at 60 % must show as 60 % for those
    /// minutes, not as 5 % of the hour. Unweighted averaging is the classic bug this test exists for.
    /// </summary>
    [Fact]
    public void Combine_WeightsAveragesByRuntime()
    {
        var busy = AppLedger.Core.Rollup.Rollup.FromSamples(
            MinuteStart,
            [.. Enumerable.Range(0, 60).Select(i => Sample(MinuteStart + i, 4, 60, 8_000, 1_000))]);

        var idle = AppLedger.Core.Rollup.Rollup.FromSamples(
            MinuteStart + 60,
            [.. Enumerable.Range(0, 10).Select(i => Sample(MinuteStart + 60 + i, 1, 10, 1_000, 10))]);

        var hour = AppLedger.Core.Rollup.Rollup.Combine(MinuteStart, [busy, idle]);

        hour.RuntimeSeconds.ShouldBe(70);
        hour.CpuPct.ShouldBe(((60 * 60.0) + (10 * 10.0)) / 70, 0.05);
        hour.CpuPctMax.ShouldBe(60);
        hour.ProcsMax.ShouldBe(4);
        hour.NetIn.ShouldBe(busy.NetIn + idle.NetIn);
    }

    [Fact]
    public void Combine_IsAssociativeAcrossTiers()
    {
        var minutes = Enumerable.Range(0, 60)
            .Select(m => AppLedger.Core.Rollup.Rollup.FromSamples(
                MinuteStart + (m * 60),
                [.. Enumerable.Range(0, 60).Select(i => Sample(MinuteStart + (m * 60) + i, 2, m, 1_000L * m, 10))]))
            .ToArray();

        var directHour = AppLedger.Core.Rollup.Rollup.Combine(MinuteStart, minutes);

        var halves = new[]
        {
            AppLedger.Core.Rollup.Rollup.Combine(MinuteStart, minutes[..30]),
            AppLedger.Core.Rollup.Rollup.Combine(MinuteStart + 1800, minutes[30..]),
        };
        var viaHalves = AppLedger.Core.Rollup.Rollup.Combine(MinuteStart, halves);

        viaHalves.NetIn.ShouldBe(directHour.NetIn);
        viaHalves.RuntimeSeconds.ShouldBe(directHour.RuntimeSeconds);
        viaHalves.CpuPct.ShouldBe(directHour.CpuPct, 0.15);
        viaHalves.CpuPctMax.ShouldBe(directHour.CpuPctMax);
    }

    /// <summary>
    /// A daily bucket spans a DST transition, so it can hold 23 or 25 hourly rows. The rollup must not
    /// assume 24, and the day's timestamp is local midnight expressed in UTC (docs/06 §Time).
    /// </summary>
    [Fact]
    public void Combine_AcrossDstTransition_UsesLocalMidnightAndAllHours()
    {
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        const int SpringForwardDay = 20260329; // Europe/Berlin loses an hour: the local day has 23 hours

        var dayStart = DayBucket.LocalDayStartUtc(SpringForwardDay, berlin);
        var dayEnd = DayBucket.LocalDayEndUtcExclusive(SpringForwardDay, berlin);
        var hours = (int)((dayEnd - dayStart) / 3600);

        hours.ShouldBe(23);

        var hourly = Enumerable.Range(0, hours)
            .Select(h => AppLedger.Core.Rollup.Rollup.FromSamples(
                dayStart + (h * 3600),
                [.. Enumerable.Range(0, 60).Select(i => Sample(dayStart + (h * 3600) + i, 1, 25, 2_000, 60))]))
            .ToArray();

        var day = AppLedger.Core.Rollup.Rollup.Combine(dayStart, hourly);

        day.Ts.ShouldBe(dayStart);
        day.RuntimeSeconds.ShouldBe(hours * 60);
        day.NetIn.ShouldBe(hours * 60 * 60);
        day.CpuPct.ShouldBe(25);
    }

    private static JsonElement LoadGolden(string name) =>
        JsonDocument.Parse(File.ReadAllText(TestPaths.Fixture("Rollup", "golden", name))).RootElement;

    private static void AssertMatchesGolden(MetricRow row, JsonElement golden)
    {
        var expected = golden.GetProperty("expected");

        row.RuntimeSeconds.ShouldBe(expected.GetProperty("runtime_s").GetInt32());
        row.Procs.ShouldBe(expected.GetProperty("procs").GetDouble());
        row.ProcsMax.ShouldBe(expected.GetProperty("procs_max").GetInt32());
        row.CpuPct.ShouldBe(expected.GetProperty("cpu_pct").GetDouble());
        row.CpuPctMax.ShouldBe(expected.GetProperty("cpu_pct_max").GetDouble());
        row.CpuUserMs.ShouldBe(expected.GetProperty("cpu_user_ms").GetInt64());
        row.CpuKernelMs.ShouldBe(expected.GetProperty("cpu_kernel_ms").GetInt64());
        row.WsPrivate.ShouldBe(expected.GetProperty("ws_private").GetInt64());
        row.WsPrivateMax.ShouldBe(expected.GetProperty("ws_private_max").GetInt64());
        row.CommitBytes.ShouldBe(expected.GetProperty("commit_bytes").GetInt64());
        row.NetIn.ShouldBe(expected.GetProperty("net_in").GetInt64());
        row.NetOut.ShouldBe(expected.GetProperty("net_out").GetInt64());
        row.DiskRead.ShouldBe(expected.GetProperty("disk_read").GetInt64());
        row.DiskWrite.ShouldBe(expected.GetProperty("disk_write").GetInt64());
        row.Threads.ShouldBe(expected.GetProperty("threads").GetDouble());
        row.Handles.ShouldBe(expected.GetProperty("handles").GetDouble());
        row.Degraded.ShouldBe(expected.GetProperty("degraded").GetBoolean());
        row.Ts.ShouldBe(long.Parse(golden.GetProperty("bucket_start_utc").GetString()!, CultureInfo.InvariantCulture));
    }
}
