using System.Globalization;

namespace AppLedger.Core.Format;

/// <summary>Whether byte sizes are shown in binary (KiB-style, Explorer's convention) or decimal units.</summary>
public enum ByteUnits
{
    /// <summary>1 KB = 1024 B, labelled KB/MB/GB the way Explorer and Task Manager label it. The default.</summary>
    Binary,

    /// <summary>1 kB = 1000 B, as drive manufacturers and network tools count.</summary>
    SiDecimal,
}

/// <summary>
/// Formats byte counts and rates for the UI. Lives in Core because "what a number means" is testable
/// logic, and because every chart axis, table cell and tooltip must agree on it (docs/14_I18N.md).
/// </summary>
public static class ByteFormatter
{
    private static readonly string[] Suffixes = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>
    /// Formats a byte count with three significant digits, e.g. <c>412 MB</c>, <c>1.21 GB</c>, <c>948 B</c>.
    /// Negative values are formatted with a leading minus rather than rejected, because deltas are shown too.
    /// </summary>
    public static string Format(long bytes, ByteUnits units = ByteUnits.Binary, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var sign = bytes < 0 ? "-" : string.Empty;
        var magnitude = bytes == long.MinValue ? (double)long.MaxValue : Math.Abs((double)bytes);
        var step = units == ByteUnits.Binary ? 1024d : 1000d;

        var i = 0;
        while (magnitude >= step && i < Suffixes.Length - 1)
        {
            magnitude /= step;
            i++;
        }

        // Bytes are never fractional; above that keep three significant digits so columns stay narrow.
        var text = i == 0
            ? magnitude.ToString("0", culture)
            : magnitude.ToString(magnitude >= 100 ? "0" : magnitude >= 10 ? "0.0" : "0.00", culture);

        return string.Concat(sign, text, " ", Suffixes[i]);
    }

    /// <summary>Formats a throughput, e.g. <c>1.21 MB/s</c>. The unit suffix is not localized.</summary>
    public static string FormatRate(long bytesPerSecond, ByteUnits units = ByteUnits.Binary, CultureInfo? culture = null) =>
        Format(bytesPerSecond, units, culture) + "/s";

    /// <summary>
    /// The short unit label a chart axis should use for a given maximum, so every tick on one axis shares
    /// one unit instead of switching partway up.
    /// </summary>
    public static string AxisUnit(long maxBytes, ByteUnits units = ByteUnits.Binary)
    {
        var step = units == ByteUnits.Binary ? 1024d : 1000d;
        var magnitude = Math.Abs((double)maxBytes);
        var i = 0;
        while (magnitude >= step && i < Suffixes.Length - 1)
        {
            magnitude /= step;
            i++;
        }

        return Suffixes[i];
    }

    /// <summary>Scales a byte count into the unit <see cref="AxisUnit"/> chose, for tick labels.</summary>
    public static double ScaleTo(long bytes, string unit, ByteUnits units = ByteUnits.Binary)
    {
        var step = units == ByteUnits.Binary ? 1024d : 1000d;
        var index = Array.IndexOf(Suffixes, unit);
        if (index <= 0)
        {
            return bytes;
        }

        return bytes / Math.Pow(step, index);
    }
}
