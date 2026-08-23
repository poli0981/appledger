using AppLedger.Core.Policy;
using Shouldly;
using Xunit;

namespace AppLedger.Core.Tests.Policy;

/// <summary>
/// The lexical half of docs/11_SAFETY_POLICY.md §Canonicalization. Every case here exists because getting
/// it wrong would let a path that is really inside Windows look like an ordinary folder.
/// </summary>
public sealed class PathRulesTests
{
    [Theory]
    [InlineData(@"C:\Windows\System32", @"C:\Windows\System32")]
    [InlineData(@"\\?\C:\Windows\System32", @"C:\Windows\System32")]
    [InlineData(@"\\?\C:\Windows\System32\", @"C:\Windows\System32")]
    [InlineData(@"c:\windows\system32", @"c:\windows\system32")]
    [InlineData(@"C:/Windows/System32", @"C:\Windows\System32")]
    [InlineData(@"C:\Windows\\System32", @"C:\Windows\System32")]
    [InlineData(@"C:\Windows\.\System32", @"C:\Windows\System32")]
    [InlineData(@"C:\Windows\System32\..\..\Users\x", @"C:\Users\x")]
    [InlineData(@"C:\..\..\Windows", @"C:\Windows")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"C:\Temp. ", @"C:\Temp")]
    [InlineData(@"C:\Temp.\sub", @"C:\Temp\sub")]
    public void TryNormalize_RootedPaths_AreCanonicalized(string input, string expected)
    {
        PathRules.TryNormalize(input, out var normalized, out var reason).ShouldBeTrue();

        reason.ShouldBe(PathDenyReason.None);
        normalized.ShouldBe(expected, StringCompareShould.IgnoreCase);
    }

    [Fact]
    public void TryNormalize_DriveLetterIsUpperCased()
    {
        PathRules.TryNormalize(@"d:\games", out var normalized, out _).ShouldBeTrue();

        normalized.ShouldStartWith(@"D:\");
    }

    /// <summary>An alternate data stream is dropped: streams are never enumerated, so the file is the unit.</summary>
    [Theory]
    [InlineData(@"C:\Windows\System32\drivers\etc\hosts:stream", @"C:\Windows\System32\drivers\etc\hosts")]
    [InlineData(@"C:\Temp\file.txt:$DATA", @"C:\Temp\file.txt")]
    public void TryNormalize_AlternateDataStream_IsStripped(string input, string expected)
    {
        PathRules.TryNormalize(input, out var normalized, out _).ShouldBeTrue();

        normalized.ShouldBe(expected, StringCompareShould.IgnoreCase);
    }

    [Theory]
    [InlineData("", PathDenyReason.Empty)]
    [InlineData("   ", PathDenyReason.Empty)]
    [InlineData(null, PathDenyReason.Empty)]
    [InlineData(@"..\foo", PathDenyReason.NotRooted)]
    [InlineData(@"foo\bar", PathDenyReason.NotRooted)]
    [InlineData(@"\Windows", PathDenyReason.NotRooted)]
    [InlineData(@"C:", PathDenyReason.NotRooted)]
    [InlineData(@"\\server\share\x", PathDenyReason.NetworkPath)]
    [InlineData(@"\\?\UNC\server\share\x", PathDenyReason.NetworkPath)]
    [InlineData(@"\\.\PhysicalDrive0", PathDenyReason.DevicePath)]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume2\x", PathDenyReason.DevicePath)]
    [InlineData("C:\\Temp\\bad\u0001name", PathDenyReason.InvalidCharacters)]
    [InlineData(@"C:\Temp\wild*card", PathDenyReason.InvalidCharacters)]
    public void TryNormalize_UnusableShapes_AreRejected(string? input, PathDenyReason expected)
    {
        PathRules.TryNormalize(input, out var normalized, out var reason).ShouldBeFalse();

        normalized.ShouldBeNull();
        reason.ShouldBe(expected);
    }

    [Fact]
    public void TryNormalize_OverlongPath_IsRejected()
    {
        var tooLong = @"C:\" + new string('a', PathRules.MaxPathLength);

        PathRules.TryNormalize(tooLong, out _, out var reason).ShouldBeFalse();

        reason.ShouldBe(PathDenyReason.TooLong);
    }

    /// <summary>
    /// The trailing-separator rule: without it, <c>C:\WindowsFoo</c> would be treated as living inside
    /// <c>C:\Windows</c> and would silently become unscannable.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Windows\System32", @"C:\Windows", true)]
    [InlineData(@"C:\Windows", @"C:\Windows", true)]
    [InlineData(@"C:\Windows\", @"C:\Windows", true)]
    [InlineData(@"c:\windows\system32", @"C:\WINDOWS", true)]
    [InlineData(@"C:\WindowsFoo", @"C:\Windows", false)]
    [InlineData(@"C:\WindowsFoo\x", @"C:\Windows", false)]
    [InlineData(@"D:\Windows\System32", @"C:\Windows", false)]
    [InlineData(@"C:\Win", @"C:\Windows", false)]
    public void IsUnder_UsesTrailingSeparatorComparison(string candidate, string root, bool expected) =>
        PathRules.IsUnder(candidate, root).ShouldBe(expected);

    [Fact]
    public void IsUnder_NullOrEmpty_IsFalse()
    {
        PathRules.IsUnder(null, @"C:\Windows").ShouldBeFalse();
        PathRules.IsUnder(@"C:\Windows", null).ShouldBeFalse();
        PathRules.IsUnder(string.Empty, string.Empty).ShouldBeFalse();
    }

    [Theory]
    [InlineData(@"C:\Program Files\Git", "Git")]
    [InlineData(@"C:\Program Files\Git\", "Git")]
    [InlineData(@"C:\", null)]
    public void LeafName_ReturnsLastComponent(string path, string? expected) =>
        PathRules.LeafName(path).ShouldBe(expected);

    [Theory]
    [InlineData(@"C:\Program Files\Git\bin", @"C:\Program Files\Git")]
    [InlineData(@"C:\Program Files", @"C:\")]
    [InlineData(@"C:\", null)]
    public void Parent_WalksUpOneLevel(string path, string? expected) =>
        PathRules.Parent(path).ShouldBe(expected);

    [Fact]
    public void SamePath_IgnoresCaseAndTrailingSeparator() =>
        PathRules.SamePath(@"C:\Windows\", @"c:\windows").ShouldBeTrue();

    [Fact]
    public void ToExtendedLength_IsIdempotent()
    {
        var once = PathRules.ToExtendedLength(@"C:\Windows");

        PathRules.ToExtendedLength(once).ShouldBe(once);
        once.ShouldBe(@"\\?\C:\Windows");
    }

    [Fact]
    public void ToComparisonKey_LowerCasesAndDropsTrailingSeparator() =>
        PathRules.ToComparisonKey(@"C:\Program Files\Git\").ShouldBe(@"c:\program files\git");

    [Theory]
    [InlineData(@"C:\Windows", @"C:\")]
    [InlineData(@"d:\x\y", @"D:\")]
    public void VolumeRoot_ReturnsUpperCasedDrive(string path, string expected) =>
        PathRules.VolumeRoot(path).ShouldBe(expected);
}
