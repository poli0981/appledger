using System.Globalization;

namespace AppLedger.Core.Time;

/// <summary>
/// Conversions between UTC epoch seconds and the local calendar days the UI buckets by
/// (docs/06_DATA_MODEL.md §Time). Storage is always UTC; only presentation is local, and the day boundary
/// uses the time zone in effect at rollup time.
/// </summary>
/// <remarks>
/// A time-zone or DST change mid-history makes one day shorter or longer. That is accepted and stated in
/// the UI tooltip rather than corrected, because rewriting historical buckets would be worse.
/// </remarks>
public static class DayBucket
{
    /// <summary>The local calendar day of an instant, as the <c>yyyyMMdd</c> integer stored in `day` columns.</summary>
    public static int ToLocalDay(long utcEpochSeconds, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeSeconds(utcEpochSeconds).UtcDateTime, timeZone);
        return FromDate(local.Year, local.Month, local.Day);
    }

    /// <summary>Packs a calendar date into the <c>yyyyMMdd</c> form.</summary>
    public static int FromDate(int year, int month, int day) => (year * 10000) + (month * 100) + day;

    /// <summary>Unpacks a <c>yyyyMMdd</c> value.</summary>
    public static DateOnly ToDate(int day) => new(day / 10000, day / 100 % 100, day % 100);

    /// <summary>
    /// UTC epoch seconds of local midnight starting <paramref name="day"/> — the value stored in
    /// <c>metrics_1d.ts</c> so a chart axis can place the bucket.
    /// </summary>
    /// <remarks>
    /// In zones where DST begins at midnight the local time 00:00 does not exist on that date; we take the
    /// first instant that does, which is what a user means by "that day started".
    /// </remarks>
    public static long LocalDayStartUtc(int day, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        var date = ToDate(day);
        var midnight = new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Unspecified);

        if (timeZone.IsInvalidTime(midnight))
        {
            // Walk forward a minute at a time out of the spring-forward gap (never longer than two hours).
            for (var minutes = 1; minutes <= 180; minutes++)
            {
                var candidate = midnight.AddMinutes(minutes);
                if (!timeZone.IsInvalidTime(candidate))
                {
                    return ToEpochSeconds(candidate, timeZone);
                }
            }
        }

        return ToEpochSeconds(midnight, timeZone);
    }

    /// <summary>
    /// UTC epoch seconds one second past the end of the local day, i.e. the exclusive upper bound for a
    /// range query over that day.
    /// </summary>
    public static long LocalDayEndUtcExclusive(int day, TimeZoneInfo timeZone) =>
        LocalDayStartUtc(NextDay(day), timeZone);

    /// <summary>The day after <paramref name="day"/>, in <c>yyyyMMdd</c> form.</summary>
    public static int NextDay(int day) => FromDate(ToDate(day).AddDays(1));

    /// <summary>The day before <paramref name="day"/>, in <c>yyyyMMdd</c> form.</summary>
    public static int PreviousDay(int day) => FromDate(ToDate(day).AddDays(-1));

    /// <summary>Packs a <see cref="DateOnly"/> into the <c>yyyyMMdd</c> form.</summary>
    public static int FromDate(DateOnly date) => FromDate(date.Year, date.Month, date.Day);

    /// <summary>Inclusive count of days between two <c>yyyyMMdd</c> values.</summary>
    public static int DaysBetween(int fromDay, int toDay) => ToDate(toDay).DayNumber - ToDate(fromDay).DayNumber;

    /// <summary>The local hour of an instant, 0-23 — the bit index used by <c>usage_daily.hour_mask</c>.</summary>
    public static int ToLocalHour(long utcEpochSeconds, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeSeconds(utcEpochSeconds).UtcDateTime, timeZone);
        return local.Hour;
    }

    /// <summary>Start of the UTC minute containing an instant — the <c>metrics_1m</c> bucket key.</summary>
    public static long MinuteStartUtc(long utcEpochSeconds) => FloorTo(utcEpochSeconds, 60);

    /// <summary>Start of the UTC hour containing an instant — the <c>metrics_1h</c> bucket key.</summary>
    public static long HourStartUtc(long utcEpochSeconds) => FloorTo(utcEpochSeconds, 3600);

    /// <summary>Renders a <c>yyyyMMdd</c> value for logs and diagnostics. Never used for UI text.</summary>
    public static string Format(int day) => ToDate(day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static long FloorTo(long value, long unit)
    {
        var remainder = value % unit;
        // Epoch seconds before 1970 are negative; C# remainder keeps the sign, so step down explicitly.
        return remainder < 0 ? value - remainder - unit : value - remainder;
    }

    private static long ToEpochSeconds(DateTime unspecifiedLocal, TimeZoneInfo timeZone)
    {
        var offset = timeZone.GetUtcOffset(unspecifiedLocal);
        return new DateTimeOffset(unspecifiedLocal, offset).ToUnixTimeSeconds();
    }
}
