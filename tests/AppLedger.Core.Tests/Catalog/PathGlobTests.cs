using AppLedger.Core.Catalog;
using Shouldly;
using Xunit;

namespace AppLedger.Core.Tests.Catalog;

/// <summary>
/// The glob grammar of docs/13_CATALOG_RULES.md. A glob decides which directories a signed rules file can
/// claim, so "not rooted" has to be a parse error rather than a match-nothing pattern.
/// </summary>
public sealed class PathGlobTests
{
    [Theory]
    [InlineData(@"C:\Program Files\X")]
    [InlineData(@"c:\program files\x")]
    [InlineData(@"?:\Steam")]
    [InlineData(@"C:\Users\fixture\Documents\**\*.kdbx")]
    public void Parse_RootedPatterns_Succeed(string pattern) => Should.NotThrow(() => PathGlob.Parse(pattern));

    [Theory]
    [InlineData(@"*\Steam")]
    [InlineData(@"**\Steam")]
    [InlineData(@"Steam")]
    [InlineData(@"\Steam")]
    [InlineData(@"%PROGRAMFILES%\X")]
    public void Parse_UnrootedPatterns_Throw(string pattern) =>
        Should.Throw<FormatException>(() => PathGlob.Parse(pattern)).Message.ShouldContain("not rooted");

    [Fact]
    public void TryParse_Unrooted_ReturnsFalse()
    {
        PathGlob.TryParse(@"*\Steam", out var glob).ShouldBeFalse();

        glob.ShouldBeNull();
    }

    [Theory]
    [InlineData(@"C:\Program Files\X", @"C:\Program Files\X", true)]
    [InlineData(@"C:\Program Files\X", @"c:\program files\x", true)]
    [InlineData(@"C:\Program Files\X", @"C:\Program Files\X\bin", false)]
    [InlineData(@"C:\Program Files\X", @"D:\Program Files\X", false)]
    [InlineData(@"C:\Program Files\X", @"C:\Program Files\XY", false)]
    public void IsMatch_ExactPatterns(string pattern, string path, bool expected) =>
        PathGlob.Parse(pattern).IsMatch(path).ShouldBe(expected);

    /// <summary>The drive wildcard covers the same path on any volume, and only at the volume root.</summary>
    [Theory]
    [InlineData(@"D:\Steam", true)]
    [InlineData(@"C:\Steam", true)]
    [InlineData(@"Z:\Steam", true)]
    [InlineData(@"D:\Games\Steam", false)]
    public void IsMatch_DriveWildcard(string path, bool expected) =>
        PathGlob.Parse(@"?:\Steam").IsMatch(path).ShouldBe(expected);

    [Theory]
    [InlineData(@"C:\Users\f\AppData\Local\Google\Chrome\User Data\Default\Cache", true)]
    [InlineData(@"C:\Users\f\AppData\Local\Google\Chrome\User Data\Profile 2\Cache", true)]
    [InlineData(@"C:\Users\f\AppData\Local\Google\Chrome\User Data\Default\Code Cache", false)]
    public void IsMatch_SingleStarStaysInsideOneComponent(string path, bool expected) =>
        PathGlob.Parse(@"C:\Users\f\AppData\Local\Google\Chrome\User Data\*\Cache").IsMatch(path).ShouldBe(expected);

    [Theory]
    [InlineData(@"C:\Docs\vault.kdbx", true)]
    [InlineData(@"C:\Docs\personal\keys\vault.kdbx", true)]
    [InlineData(@"C:\Docs\vault.txt", false)]
    [InlineData(@"C:\Other\vault.kdbx", false)]
    public void IsMatch_DoubleStarSpansComponentsIncludingZero(string path, bool expected) =>
        PathGlob.Parse(@"C:\Docs\**\*.kdbx").IsMatch(path).ShouldBe(expected);

    [Fact]
    public void IsMatch_TrailingDoubleStar_MatchesSubtreeAndRoot()
    {
        var glob = PathGlob.Parse(@"C:\Vault\**");

        glob.IsMatch(@"C:\Vault").ShouldBeTrue();
        glob.IsMatch(@"C:\Vault\a\b\c").ShouldBeTrue();
        glob.IsMatch(@"C:\Vaulted").ShouldBeFalse();
    }

    /// <summary>
    /// Most catalog rules name a directory and mean everything inside it, which is a different question
    /// from whether the pattern matches the path itself.
    /// </summary>
    [Fact]
    public void MatchesOrContains_CoversTheSubtree()
    {
        var glob = PathGlob.Parse(@"C:\Users\f\AppData\Roaming\discord");

        glob.IsMatch(@"C:\Users\f\AppData\Roaming\discord\Cache\x.bin").ShouldBeFalse();
        glob.MatchesOrContains(@"C:\Users\f\AppData\Roaming\discord\Cache\x.bin").ShouldBeTrue();
        glob.MatchesOrContains(@"C:\Users\f\AppData\Roaming\discord").ShouldBeTrue();
        glob.MatchesOrContains(@"C:\Users\f\AppData\Roaming\other").ShouldBeFalse();
    }

    [Fact]
    public void IsMatch_NullOrTooShort_IsFalse()
    {
        var glob = PathGlob.Parse(@"C:\X");

        glob.IsMatch(null).ShouldBeFalse();
        glob.IsMatch("C:").ShouldBeFalse();
    }

    [Theory]
    [InlineData("*crashpad_handler*.exe", "chrome_crashpad_handler.exe", true)]
    [InlineData("*crashpad_handler*.exe", "crashpad_handler.exe", true)]
    [InlineData("*crashpad_handler*.exe", "handler.exe", false)]
    [InlineData("?.exe", "a.exe", true)]
    [InlineData("?.exe", "ab.exe", false)]
    [InlineData("*.exe", "ANYTHING.EXE", true)]
    public void WildcardMatch_HandlesStarAndQuestion(string pattern, string value, bool expected) =>
        PathGlob.WildcardMatch(pattern, value).ShouldBe(expected);
}
