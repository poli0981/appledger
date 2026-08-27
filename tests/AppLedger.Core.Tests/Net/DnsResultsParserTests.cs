using AppLedger.Core.Net;
using Shouldly;
using Xunit;

namespace AppLedger.Core.Tests.Net;

/// <summary>
/// docs/10_NETWORK_AND_DNS.md §DNS. The <c>QueryResults</c> field has no schema worth the name, so the
/// parser's job is to take what it recognises and never to throw — it runs on an ETW callback thread, where
/// an exception would cost the whole session, not one label.
/// </summary>
public sealed class DnsResultsParserTests
{
    [Fact]
    public void ParseAddresses_BareIpv4List_ReturnsThemAll() =>
        DnsResultsParser.ParseAddresses("93.184.216.34;93.184.216.35;")
            .Select(a => a.ToString()).ShouldBe(["93.184.216.34", "93.184.216.35"]);

    [Fact]
    public void ParseAddresses_TypePrefixedEntries_AreUnderstood() =>
        DnsResultsParser.ParseAddresses("type: 1 93.184.216.34;type: 28 2606:2800:220:1:248:1893:25c8:1946;")
            .Select(a => a.ToString())
            .ShouldBe(["93.184.216.34", "2606:2800:220:1:248:1893:25c8:1946"]);

    /// <summary>
    /// A CNAME's value is a name, not an address. Treating it as one would either throw or silently produce
    /// nothing; skipping it keeps the addresses that came alongside.
    /// </summary>
    [Fact]
    public void ParseAddresses_CnameEntries_AreSkippedButTheirNeighboursSurvive() =>
        DnsResultsParser.ParseAddresses("type: 5 cname.target.net;type: 1 93.184.216.34;")
            .ShouldHaveSingleItem().ToString().ShouldBe("93.184.216.34");

    /// <summary>
    /// Windows reports IPv4 answers as IPv4-mapped IPv6 on some builds. Storing both forms would split one
    /// host's traffic across two rows that never add back up.
    /// </summary>
    [Fact]
    public void ParseAddresses_Ipv4MappedIpv6_IsNormalisedToIpv4() =>
        DnsResultsParser.ParseAddresses("::ffff:93.184.216.34;")
            .ShouldHaveSingleItem().ToString().ShouldBe("93.184.216.34");

    /// <summary>An unfamiliar token must not cost us the addresses that came with it.</summary>
    [Fact]
    public void ParseAddresses_UnknownTokens_AreIgnoredWithoutLosingTheRest() =>
        DnsResultsParser.ParseAddresses("type: 65 alpn=h3;garbage;;type: 1 1.2.3.4;not-an-address")
            .ShouldHaveSingleItem().ToString().ShouldBe("1.2.3.4");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(";;;")]
    [InlineData("type:")]
    [InlineData("type: 5")]
    [InlineData("type: notanumber")]
    public void ParseAddresses_DegenerateInput_IsEmptyRatherThanAnException(string? input) =>
        Should.NotThrow(() => DnsResultsParser.ParseAddresses(input)).ShouldBeEmpty();

    [Fact]
    public void ParseAddresses_TypeWithUnparseableNumber_StillReadsTheValue() =>
        DnsResultsParser.ParseAddresses("type: x 1.2.3.4")
            .ShouldHaveSingleItem().ToString().ShouldBe("1.2.3.4");

    [Fact]
    public void ParseCnames_ReturnsTheChainInOrderWithoutTrailingDots() =>
        DnsResultsParser.ParseCnames("type: 5 a.example.net.;type: 5 b.example.net;type: 1 1.2.3.4")
            .ShouldBe(["a.example.net", "b.example.net"]);

    [Fact]
    public void ParseCnames_NoCnames_IsEmpty() =>
        DnsResultsParser.ParseCnames("93.184.216.34;").ShouldBeEmpty();

    /// <summary>
    /// A real answer from a browsing session: a CNAME chain ending in two A records and one AAAA. This is
    /// the shape that actually arrives, which is why it is asserted whole rather than in pieces.
    /// </summary>
    [Fact]
    public void ParseAddresses_RealisticChainedAnswer_YieldsOnlyTheAddresses()
    {
        const string Results =
            "type: 5 e4567.dscb.akamaiedge.net;type: 5 dualstack.example.map.fastly.net;"
            + "type: 1 151.101.1.140;type: 1 151.101.65.140;type: 28 2a04:4e42::396;";

        DnsResultsParser.ParseAddresses(Results).Select(a => a.ToString())
            .ShouldBe(["151.101.1.140", "151.101.65.140", "2a04:4e42::396"]);

        DnsResultsParser.ParseCnames(Results)
            .ShouldBe(["e4567.dscb.akamaiedge.net", "dualstack.example.map.fastly.net"]);
    }
}
