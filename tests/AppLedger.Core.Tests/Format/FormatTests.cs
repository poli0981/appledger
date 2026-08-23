using System.Globalization;
using AppLedger.Core.Format;
using Shouldly;
using Xunit;

namespace AppLedger.Core.Tests.Format;

/// <summary>
/// Byte formatting. Explorer-style binary units are the default because that is what the numbers next to
/// ours on the same screen use; the decimal option exists for network and drive figures (docs/14_I18N.md).
/// </summary>
public sealed class ByteFormatterTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(948, "948 B")]
    [InlineData(1024, "1.00 KB")]
    [InlineData(1536, "1.50 KB")]
    [InlineData(10 * 1024, "10.0 KB")]
    [InlineData(100 * 1024, "100 KB")]
    [InlineData(1024 * 1024, "1.00 MB")]
    [InlineData(432013312, "412 MB")]
    public void Format_Binary_UsesThreeSignificantDigits(long bytes, string expected) =>
        ByteFormatter.Format(bytes, ByteUnits.Binary, Invariant).ShouldBe(expected);

    [Theory]
    [InlineData(1000, "1.00 KB")]
    [InlineData(1_000_000, "1.00 MB")]
    public void Format_Decimal_UsesPowersOfTen(long bytes, string expected) =>
        ByteFormatter.Format(bytes, ByteUnits.SiDecimal, Invariant).ShouldBe(expected);

    /// <summary>Deltas are shown too, so a negative value formats rather than throwing.</summary>
    [Fact]
    public void Format_Negative_KeepsTheSign() =>
        ByteFormatter.Format(-1536, ByteUnits.Binary, Invariant).ShouldBe("-1.50 KB");

    [Fact]
    public void Format_LongMinValue_DoesNotOverflow() =>
        Should.NotThrow(() => ByteFormatter.Format(long.MinValue, ByteUnits.Binary, Invariant));

    [Fact]
    public void Format_UsesTheCultureDecimalSeparator()
    {
        var vietnamese = CultureInfo.GetCultureInfo("vi-VN");

        ByteFormatter.Format(1536, ByteUnits.Binary, vietnamese).ShouldBe("1,50 KB");
    }

    [Fact]
    public void FormatRate_AppendsPerSecond() =>
        ByteFormatter.FormatRate(1024 * 1024, ByteUnits.Binary, Invariant).ShouldBe("1.00 MB/s");

    /// <summary>One axis, one unit: every tick on a chart must share the unit chosen for its maximum.</summary>
    [Theory]
    [InlineData(500L, "B")]
    [InlineData(5L * 1024, "KB")]
    [InlineData(5L * 1024 * 1024, "MB")]
    [InlineData(5L * 1024 * 1024 * 1024, "GB")]
    public void AxisUnit_FollowsTheMaximum(long max, string expected) =>
        ByteFormatter.AxisUnit(max).ShouldBe(expected);

    [Fact]
    public void ScaleTo_ConvertsIntoTheAxisUnit()
    {
        ByteFormatter.ScaleTo(1024 * 1024, "MB").ShouldBe(1.0, 0.0001);
        ByteFormatter.ScaleTo(512, "B").ShouldBe(512);
    }
}

/// <summary>
/// Percentages, durations and relative times. <c>Relative</c> returns a unit and an amount rather than a
/// string because docs/14 forbids concatenating localized fragments.
/// </summary>
public sealed class FormattersTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    [Theory]
    [InlineData(12.44, "12.4 %")]
    [InlineData(0, "0.0 %")]
    [InlineData(100, "100.0 %")]
    [InlineData(150, "100.0 %")]
    [InlineData(-5, "0.0 %")]
    public void Percent_ClampsAndKeepsOneDecimal(double value, string expected) =>
        Formatters.Percent(value, Invariant).ShouldBe(expected);

    [Theory]
    [InlineData(38, "38s")]
    [InlineData(65, "1m 05s")]
    [InlineData(3600, "1h 00m")]
    [InlineData(4980, "1h 23m")]
    [InlineData(90000, "1d 1h")]
    public void DurationSeconds_UsesTheCompactForm(long seconds, string expected) =>
        Formatters.DurationSeconds(seconds, Invariant).ShouldBe(expected);

    [Fact]
    public void Duration_Negative_ShowsZero() =>
        Formatters.Duration(TimeSpan.FromSeconds(-10), Invariant).ShouldBe("0s");

    [Fact]
    public void Count_UsesGroupSeparators()
    {
        Formatters.Count(1204, Invariant).ShouldBe("1,204");
        Formatters.Count(1204, CultureInfo.GetCultureInfo("de-DE")).ShouldBe("1.204");
    }

    [Theory]
    [InlineData(30, RelativeUnit.Seconds, 30)]
    [InlineData(240, RelativeUnit.Minutes, 4)]
    [InlineData(7200, RelativeUnit.Hours, 2)]
    [InlineData(86400 * 3, RelativeUnit.Days, 3)]
    [InlineData(86400 * 60, RelativeUnit.Months, 2)]
    [InlineData(86400 * 400, RelativeUnit.Years, 1)]
    public void Relative_PicksTheUnitAndAmount(long ago, RelativeUnit expectedUnit, int expectedAmount)
    {
        const long Now = 1_787_443_200;

        var (unit, amount) = Formatters.Relative(Now - ago, Now);

        unit.ShouldBe(expectedUnit);
        amount.ShouldBe(expectedAmount);
    }

    [Fact]
    public void Relative_FutureTimestamp_ClampsToZeroSeconds() =>
        Formatters.Relative(2_000_000_000, 1_000_000_000).ShouldBe((RelativeUnit.Seconds, 0));
}

/// <summary>
/// Redaction. A log line has to be useful in a bug report and useless as a record of what the user did,
/// so these tests assert the *absence* of the original value as much as the presence of the class.
/// </summary>
public sealed class RedactorTests
{
    [Fact]
    public void PathRedactor_KnownRoot_ReplacesTheRootAndDropsNames()
    {
        var roots = new[] { new KeyValuePair<string, string>("install-root", @"C:\Program Files\Discord") };

        var result = PathRedactor.ToClass(@"C:\Program Files\Discord\resources\app\index.js", roots);

        result.ShouldStartWith("<install-root>");
        result.ShouldEndWith(".js");
        result.ShouldNotContain("resources");
        result.ShouldNotContain("index");
    }

    [Fact]
    public void PathRedactor_UnknownRoot_StillDropsNames()
    {
        var result = PathRedactor.ToClass(@"D:\Private\Secret Project\notes.txt");

        result.ShouldStartWith("<drive>");
        result.ShouldNotContain("Secret");
        result.ShouldNotContain("notes");
        result.ShouldEndWith(".txt");
    }

    [Fact]
    public void PathRedactor_RootItself_HasNoDepthSuffix()
    {
        var roots = new[] { new KeyValuePair<string, string>("userprofile", @"C:\Users\fixture") };

        PathRedactor.ToClass(@"C:\Users\fixture", roots).ShouldBe("<userprofile>");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void PathRedactor_Empty_IsNone(string? path) => PathRedactor.ToClass(path).ShouldBe("<none>");

    [Theory]
    [InlineData("cdn.discordapp.com", "<etld1>")]
    [InlineData("localhost", "<host>")]
    [InlineData("203.0.113.42", "<ip-v4>")]
    [InlineData("2001:db8::1", "<ip-v6>")]
    [InlineData(null, "<none>")]
    public void HostRedactor_ClassifiesWithoutRevealing(string? host, string expected) =>
        HostRedactor.ToClass(host).ShouldBe(expected);

    /// <summary>
    /// The point of the host classifier: the registrable domain must not appear in the output, or an
    /// Information-level log would still record which sites an app talked to.
    /// </summary>
    [Fact]
    public void HostRedactor_NeverEchoesTheValue() =>
        HostRedactor.ToClass("cdn.discordapp.com").ShouldNotContain("discord");
}
