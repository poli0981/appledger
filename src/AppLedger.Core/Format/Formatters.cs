using System.Globalization;

namespace AppLedger.Core.Format;

/// <summary>
/// Number, duration and percentage formatting shared by every page and chart. Localized *text* lives in
/// resx (docs/14_I18N.md); what lives here is the shape of a value, which must not differ between the
/// Overview card, the table cell and the chart tick that show the same number.
/// </summary>
public static class Formatters
{
    /// <summary>A percentage with one decimal, e.g. <c>12.4 %</c>. Values are clamped to 0-100.</summary>
    public static string Percent(double value, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var clamped = Math.Clamp(value, 0d, 100d);
        return clamped.ToString("0.0", culture) + " %";
    }

    /// <summary>
    /// A duration in the compact form the UI uses for usage time: <c>1h 23m</c>, <c>4m 05s</c>, <c>38s</c>.
    /// Days appear once a duration passes 24 hours, because "48h 10m" reads worse than "2d 0h".
    /// </summary>
    public static string Duration(TimeSpan value, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var total = value < TimeSpan.Zero ? TimeSpan.Zero : value;

        if (total.TotalDays >= 1)
        {
            return string.Format(culture, "{0}d {1}h", (int)total.TotalDays, total.Hours);
        }

        if (total.TotalHours >= 1)
        {
            return string.Format(culture, "{0}h {1:00}m", (int)total.TotalHours, total.Minutes);
        }

        if (total.TotalMinutes >= 1)
        {
            return string.Format(culture, "{0}m {1:00}s", (int)total.TotalMinutes, total.Seconds);
        }

        return string.Format(culture, "{0}s", (int)total.TotalSeconds);
    }

    /// <summary>Convenience overload for the <c>runtime_s</c> columns.</summary>
    public static string DurationSeconds(long seconds, CultureInfo? culture = null) =>
        Duration(TimeSpan.FromSeconds(seconds), culture);

    /// <summary>
    /// A count with group separators, e.g. <c>1,204</c> or <c>1.204</c> depending on culture. Used for
    /// process, thread, handle and file counts.
    /// </summary>
    public static string Count(long value, CultureInfo? culture = null) =>
        value.ToString("N0", culture ?? CultureInfo.CurrentCulture);

    /// <summary>
    /// The number of whole units between two instants, for the UI's relative-time strings. Returns the
    /// unit and the amount so the caller can pick the right resx key rather than concatenating fragments,
    /// which docs/14 forbids.
    /// </summary>
    public static (RelativeUnit Unit, int Amount) Relative(long fromUtcSeconds, long nowUtcSeconds)
    {
        var seconds = Math.Max(0, nowUtcSeconds - fromUtcSeconds);

        if (seconds < 60)
        {
            return (RelativeUnit.Seconds, (int)seconds);
        }

        if (seconds < 3600)
        {
            return (RelativeUnit.Minutes, (int)(seconds / 60));
        }

        if (seconds < 86400)
        {
            return (RelativeUnit.Hours, (int)(seconds / 3600));
        }

        if (seconds < 86400 * 30)
        {
            return (RelativeUnit.Days, (int)(seconds / 86400));
        }

        return seconds < 86400L * 365
            ? (RelativeUnit.Months, (int)(seconds / (86400 * 30)))
            : (RelativeUnit.Years, (int)(seconds / (86400L * 365)));
    }
}

/// <summary>The unit chosen by <see cref="Formatters.Relative"/>; each maps to a resx key pair.</summary>
public enum RelativeUnit
{
    Seconds,
    Minutes,
    Hours,
    Days,
    Months,
    Years,
}
