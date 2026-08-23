using AppLedger.Core.Rollup;
using AppLedger.Core.Time;
using Shouldly;
using Xunit;

namespace AppLedger.Core.Tests.Time;

/// <summary>
/// UTC storage with local-day bucketing (docs/06_DATA_MODEL.md §Time). The awkward cases are real: users
/// think in local days, DST makes some of those days 23 or 25 hours long, and the day boundary uses the
/// zone in effect at rollup time rather than being recomputed later.
/// </summary>
public sealed class DayBucketTests
{
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
    private static readonly TimeZoneInfo Saigon = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");

    [Fact]
    public void FromDate_And_ToDate_RoundTrip()
    {
        DayBucket.FromDate(2026, 8, 23).ShouldBe(20260823);
        DayBucket.ToDate(20260823).ShouldBe(new DateOnly(2026, 8, 23));
    }

    /// <summary>
    /// Late evening in Saigon is already the next UTC day; the row must still be filed under the local
    /// day the user experienced.
    /// </summary>
    [Fact]
    public void ToLocalDay_UsesLocalCalendarNotUtc()
    {
        // 2026-08-23 23:30 in Asia/Ho_Chi_Minh (UTC+7) is 2026-08-23 16:30 UTC.
        var utc = new DateTimeOffset(2026, 8, 23, 16, 30, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        DayBucket.ToLocalDay(utc, Saigon).ShouldBe(20260823);
        DayBucket.ToLocalHour(utc, Saigon).ShouldBe(23);
    }

    [Fact]
    public void ToLocalDay_JustAfterLocalMidnight_IsTheNewDay()
    {
        // 2026-08-24 00:30 in Asia/Ho_Chi_Minh is 2026-08-23 17:30 UTC.
        var utc = new DateTimeOffset(2026, 8, 23, 17, 30, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        DayBucket.ToLocalDay(utc, Saigon).ShouldBe(20260824);
    }

    [Fact]
    public void LocalDayStartUtc_RoundTripsThroughToLocalDay()
    {
        const int Day = 20260823;

        var start = DayBucket.LocalDayStartUtc(Day, Berlin);

        DayBucket.ToLocalDay(start, Berlin).ShouldBe(Day);
        DayBucket.ToLocalDay(start - 1, Berlin).ShouldBe(20260822);
    }

    /// <summary>Europe/Berlin loses an hour on 2026-03-29 and gains one on 2026-10-25.</summary>
    [Theory]
    [InlineData(20260329, 23)]
    [InlineData(20261025, 25)]
    [InlineData(20260823, 24)]
    public void LocalDay_LengthFollowsDst(int day, int expectedHours)
    {
        var start = DayBucket.LocalDayStartUtc(day, Berlin);
        var end = DayBucket.LocalDayEndUtcExclusive(day, Berlin);

        ((end - start) / 3600).ShouldBe(expectedHours);
    }

    [Fact]
    public void NextAndPreviousDay_CrossMonthAndYearBoundaries()
    {
        DayBucket.NextDay(20260831).ShouldBe(20260901);
        DayBucket.NextDay(20261231).ShouldBe(20270101);
        DayBucket.PreviousDay(20260301).ShouldBe(20260228);
        DayBucket.NextDay(20280228).ShouldBe(20280229); // leap year
    }

    [Fact]
    public void DaysBetween_CountsCalendarDays() => DayBucket.DaysBetween(20260823, 20260902).ShouldBe(10);

    [Theory]
    [InlineData(1787443265L, 1787443260L)]
    [InlineData(1787443200L, 1787443200L)]
    public void MinuteStartUtc_FloorsToTheMinute(long input, long expected) =>
        DayBucket.MinuteStartUtc(input).ShouldBe(expected);

    [Fact]
    public void HourStartUtc_FloorsToTheHour() => DayBucket.HourStartUtc(1787446799).ShouldBe(1787443200);

    /// <summary>Negative epoch values exist in test data; flooring must step down, not toward zero.</summary>
    [Fact]
    public void MinuteStartUtc_HandlesNegativeEpoch() => DayBucket.MinuteStartUtc(-30).ShouldBe(-60);

    [Fact]
    public void Format_IsIsoForDiagnostics() => DayBucket.Format(20260823).ShouldBe("2026-08-23");
}

/// <summary>
/// Retention arithmetic. The fixed floors are the interesting part: they hold even when a user configures
/// a long retention, because minute rows and command lines are the largest and most sensitive tiers.
/// </summary>
public sealed class RetentionCalculatorTests
{
    [Theory]
    [InlineData(10, RetentionCalculator.MinimumDays)]
    [InlineData(30, 30)]
    [InlineData(180, 180)]
    [InlineData(365, 365)]
    [InlineData(999, RetentionCalculator.MaximumDays)]
    public void ClampDays_KeepsTheConfiguredRange(int requested, int expected) =>
        RetentionCalculator.ClampDays(requested).ShouldBe(expected);

    [Fact]
    public void DaysFor_MinuteRows_AreAlwaysSevenDays()
    {
        RetentionCalculator.DaysFor(RetentionTier.Metrics1M, 365).ShouldBe(7);
        RetentionCalculator.DaysFor(RetentionTier.Metrics1M, 30).ShouldBe(7);
    }

    [Fact]
    public void DaysFor_ProcessInstances_AreAlwaysThirtyDays() =>
        RetentionCalculator.DaysFor(RetentionTier.ProcessInstances, 365).ShouldBe(30);

    [Theory]
    [InlineData(RetentionTier.Metrics1H)]
    [InlineData(RetentionTier.Metrics1D)]
    [InlineData(RetentionTier.NetHostsDaily)]
    [InlineData(RetentionTier.Events)]
    public void DaysFor_OtherTiers_FollowTheSetting(RetentionTier tier) =>
        RetentionCalculator.DaysFor(tier, 90).ShouldBe(90);

    [Fact]
    public void CutoffUtc_IsTheSettingInSecondsBeforeNow()
    {
        const long Now = 1_787_443_200;

        RetentionCalculator.CutoffUtc(RetentionTier.Metrics1H, 180, Now).ShouldBe(Now - (180L * 86400));
        RetentionCalculator.CutoffUtc(RetentionTier.Metrics1M, 180, Now).ShouldBe(Now - (7L * 86400));
    }

    [Fact]
    public void NightlyPlan_CoversEveryTier() =>
        RetentionCalculator.NightlyPlan(180, 1_787_443_200)
            .Select(p => p.Tier)
            .ShouldBe(Enum.GetValues<RetentionTier>(), ignoreOrder: true);

    [Fact]
    public void CutoffDay_IsALocalCalendarDay()
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");
        const long Now = 1_787_443_200;

        var day = RetentionCalculator.CutoffDay(RetentionTier.NetHostsDaily, 30, Now, tz);

        day.ShouldBe(DayBucket.ToLocalDay(Now - (30L * 86400), tz));
    }

    [Fact]
    public void Defaults_MatchTheDocumentedValues()
    {
        RetentionCalculator.DefaultDays.ShouldBe(180);
        RetentionCalculator.MinimumDays.ShouldBe(30);
        RetentionCalculator.MaximumDays.ShouldBe(365);
    }
}
