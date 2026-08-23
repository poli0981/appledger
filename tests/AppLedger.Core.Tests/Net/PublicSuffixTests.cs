using AppLedger.Core.Catalog;
using AppLedger.Core.Net;
using Shouldly;
using Xunit;

namespace AppLedger.Core.Tests.Net;

/// <summary>
/// eTLD+1 reduction against the vendored Public Suffix List. This is the mechanism that turns "six months
/// of hostnames" into "six months of registrable domains", so an over-broad reduction would store less
/// than promised and an under-broad one would store more than the user agreed to (docs/12 §Defaults).
/// </summary>
public sealed class PublicSuffixTests
{
    private static readonly Lazy<PublicSuffixList> ListLazy =
        new(() => PublicSuffixList.Parse(File.ReadAllText(TestPaths.PublicSuffixList)));

    private static PublicSuffixList List => ListLazy.Value;

    [Fact]
    public void Parse_VendoredList_HasManyRules() => List.RuleCount.ShouldBeGreaterThan(5_000);

    [Theory]
    [InlineData("cdn.discordapp.com", "discordapp.com")]
    [InlineData("discordapp.com", "discordapp.com")]
    [InlineData("a.b.c.example.com", "example.com")]
    [InlineData("bbc.co.uk", "bbc.co.uk")]
    [InlineData("www.bbc.co.uk", "bbc.co.uk")]
    [InlineData("shop.example.com.vn", "example.com.vn")]
    [InlineData("EXAMPLE.COM", "example.com")]
    [InlineData("example.com.", "example.com")]
    public void GetRegistrableDomain_ReducesToEtldPlusOne(string host, string expected) =>
        List.GetRegistrableDomain(host).ShouldBe(expected);

    /// <summary>
    /// A private-suffix entry such as github.io means each user's pages site is its own registrable
    /// domain — reducing it to "github.io" would merge unrelated sites into one row.
    /// </summary>
    [Theory]
    [InlineData("foo.github.io", "foo.github.io")]
    [InlineData("pages.foo.github.io", "foo.github.io")]
    public void GetRegistrableDomain_HonoursPrivateSuffixes(string host, string expected) =>
        List.GetRegistrableDomain(host).ShouldBe(expected);

    [Theory]
    [InlineData("co.uk")]
    [InlineData("com")]
    [InlineData("github.io")]
    public void GetRegistrableDomain_HostThatIsItselfASuffix_ReturnsItself(string host) =>
        List.GetRegistrableDomain(host).ShouldBe(host);

    [Theory]
    [InlineData("bbc.co.uk", "co.uk")]
    [InlineData("cdn.discordapp.com", "com")]
    [InlineData("foo.github.io", "github.io")]
    public void GetPublicSuffix_ReturnsTheEtld(string host, string expected) =>
        List.GetPublicSuffix(host).ShouldBe(expected);

    /// <summary>An unlisted TLD is still a public suffix, per the list's implicit `*` rule.</summary>
    [Fact]
    public void GetRegistrableDomain_UnlistedTld_UsesTheImplicitRule() =>
        List.GetRegistrableDomain("host.invalidtldthatdoesnotexist").ShouldBe("host.invalidtldthatdoesnotexist");

    [Theory]
    [InlineData("1.2.3.4")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("2001:db8::1")]
    public void GetRegistrableDomain_IpLiterals_AreNotNames(string host)
    {
        List.GetRegistrableDomain(host).ShouldBeNull();
        PublicSuffixList.IsIpLiteral(host).ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("a..b")]
    public void GetRegistrableDomain_UnusableInput_IsNull(string? host) =>
        List.GetRegistrableDomain(host).ShouldBeNull();

    [Theory]
    [InlineData("1.2.3")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1.2.3.999x")]
    [InlineData("example.com")]
    public void IsIpLiteral_RejectsNonAddresses(string value) => PublicSuffixList.IsIpLiteral(value).ShouldBeFalse();

    [Fact]
    public void Parse_ListWithoutRules_Throws() =>
        Should.Throw<FormatException>(() => PublicSuffixList.Parse("// only a comment\n\n"));

    /// <summary>Wildcard and exception rules, checked on a small hand-written list rather than the real one.</summary>
    [Fact]
    public void Parse_WildcardAndExceptionRules_AreApplied()
    {
        var list = PublicSuffixList.Parse("// test\nck\n*.ck\n!www.ck\n");

        list.GetPublicSuffix("foo.ck").ShouldBe("foo.ck");
        list.GetRegistrableDomain("bar.foo.ck").ShouldBe("bar.foo.ck");
        list.GetRegistrableDomain("www.ck").ShouldBe("www.ck");
        list.GetPublicSuffix("www.ck").ShouldBe("ck");
    }
}

/// <summary>
/// The policy that decides what a stored host row may contain. The live path and the rollup path share
/// this code on purpose: the UI must never receive data the policy says it should not display.
/// </summary>
public sealed class HostPolicyTests
{
    private static HostPolicy Policy() =>
        new(PublicSuffixList.Parse(File.ReadAllText(TestPaths.PublicSuffixList)));

    [Theory]
    [InlineData("Browser", HostLogging.None)]
    [InlineData("System", HostLogging.None)]
    [InlineData("Game", HostLogging.Etld1)]
    [InlineData("Communication", HostLogging.Etld1)]
    [InlineData(null, HostLogging.Etld1)]
    public void DefaultForCategory_MatchesThePrivacyDefaults(string? category, HostLogging expected) =>
        HostPolicy.DefaultForCategory(category).ShouldBe(expected);

    /// <summary>
    /// The promise in the README and the Privacy Gate: for a browser, no host name is stored under any
    /// circumstance until the user opts in per app.
    /// </summary>
    [Theory]
    [InlineData("cdn.discordapp.com")]
    [InlineData("some.very.specific.host.example.co.uk")]
    [InlineData("1.2.3.4")]
    [InlineData(null)]
    public void Shape_NoneLevel_NeverYieldsAHostName(string? host) =>
        Policy().Shape(host, HostLogging.None).ShouldBe(HostPolicy.HiddenBucket);

    [Theory]
    [InlineData("cdn.discordapp.com", "discordapp.com")]
    [InlineData("CDN.DiscordApp.COM", "discordapp.com")]
    [InlineData("www.bbc.co.uk", "bbc.co.uk")]
    public void Shape_Etld1Level_ReducesToRegistrableDomain(string host, string expected) =>
        Policy().Shape(host, HostLogging.Etld1).ShouldBe(expected);

    [Theory]
    [InlineData("cdn.discordapp.com", "cdn.discordapp.com")]
    [InlineData("CDN.DiscordApp.COM.", "cdn.discordapp.com")]
    public void Shape_FullLevel_KeepsTheWholeName(string host, string expected) =>
        Policy().Shape(host, HostLogging.Full).ShouldBe(expected);

    [Theory]
    [InlineData("1.2.3.4")]
    [InlineData("")]
    [InlineData(null)]
    public void Shape_AddressOrMissingName_UsesTheUnnamedBucket(string? host) =>
        Policy().Shape(host, HostLogging.Etld1).ShouldBe(HostPolicy.UnnamedBucket);

    [Fact]
    public void IsBucket_RecognisesTheAggregates()
    {
        HostPolicy.IsBucket(HostPolicy.HiddenBucket).ShouldBeTrue();
        HostPolicy.IsBucket(HostPolicy.UnnamedBucket).ShouldBeTrue();
        HostPolicy.IsBucket(HostPolicy.OverflowBucket).ShouldBeTrue();
        HostPolicy.IsBucket("discordapp.com").ShouldBeFalse();
    }

    /// <summary>
    /// Unnamed addresses are grouped by prefix so a host that never resolves cannot blow past the per-day
    /// cap one address at a time (docs/10 §Host policy).
    /// </summary>
    [Theory]
    [InlineData("203.0.113.42", "203.0.113.0/24")]
    [InlineData("10.1.2.3", "10.1.2.0/24")]
    [InlineData("2001:db8:1234:5678::1", "2001:db8:1234::/48")]
    public void ToAddressPrefix_GroupsByNetwork(string address, string expected) =>
        HostPolicy.ToAddressPrefix(address).ShouldBe(expected);

    [Theory]
    [InlineData("example.com")]
    [InlineData("")]
    [InlineData(null)]
    public void ToAddressPrefix_NonAddress_IsNull(string? value) => HostPolicy.ToAddressPrefix(value).ShouldBeNull();

    [Fact]
    public void DefaultHostsPerDay_MatchesTheDocumentedCap() => HostPolicy.DefaultHostsPerAppPerDay.ShouldBe(200);
}
