using System.Net;
using AppLedger.Collector.Accumulators;
using AppLedger.Core.Collection;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace AppLedger.Collector.Tests.Accumulators;

/// <summary>
/// Byte attribution and the endpoint cap (docs/10_NETWORK_AND_DNS.md §Byte attribution,
/// docs/05_COLLECTOR.md §Budget controls). The cap is the part that matters: an uncapped dictionary here is
/// an unbounded allocation on the ETW thread, in a process with about 20 MB of budget left.
/// </summary>
public sealed class NetAccumulatorTests
{
    private readonly ITestOutputHelper _output;

    public NetAccumulatorTests(ITestOutputHelper output) => _output = output;

    private static NetworkEvent Event(
        string remote = "93.184.216.34",
        int port = 443,
        long size = 100,
        NetworkDirection direction = NetworkDirection.Inbound,
        NetworkProtocol protocol = NetworkProtocol.Tcp,
        long ts = 1_700_000_000) =>
        new(ProcessId: 100, size, direction, protocol, IPAddress.Parse(remote), port, ts);

    [Fact]
    public void Add_SendsAndReceives_AccumulateSeparately()
    {
        var net = new NetAccumulator();

        net.Add(Event(size: 100, direction: NetworkDirection.Inbound));
        net.Add(Event(size: 40, direction: NetworkDirection.Outbound));
        net.Add(Event(size: 60, direction: NetworkDirection.Inbound));

        net.InBytes.ShouldBe(160);
        net.OutBytes.ShouldBe(40);
    }

    /// <summary>
    /// A 4 GB transfer to a local database is not bandwidth the user paid for, so loopback is counted but
    /// kept out of the internet totals (docs/06 has separate columns for exactly this).
    /// </summary>
    [Fact]
    public void Add_LoopbackTraffic_StaysOutOfTheInternetTotals()
    {
        var net = new NetAccumulator();

        net.Add(Event(remote: "127.0.0.1", protocol: NetworkProtocol.Tcp | NetworkProtocol.Loopback, size: 5_000));
        net.Add(Event(size: 100));

        net.InBytes.ShouldBe(100);
        net.InBytesLoopback.ShouldBe(5_000);
    }

    [Fact]
    public void Add_SameEndpointTwice_MergesRatherThanDuplicating()
    {
        var net = new NetAccumulator();

        net.Add(Event(size: 100, ts: 1_700_000_000));
        net.Add(Event(size: 250, direction: NetworkDirection.Outbound, ts: 1_700_000_005));

        var endpoint = net.Endpoints.Values.ShouldHaveSingleItem();
        endpoint.InBytes.ShouldBe(100);
        endpoint.OutBytes.ShouldBe(250);
        endpoint.FirstSeenUtc.ShouldBe(1_700_000_000);
        endpoint.LastSeenUtc.ShouldBe(1_700_000_005);
    }

    [Fact]
    public void Add_DifferentPortsOrProtocols_AreDifferentEndpoints()
    {
        var net = new NetAccumulator();

        net.Add(Event(port: 443));
        net.Add(Event(port: 80));
        net.Add(Event(port: 443, protocol: NetworkProtocol.Udp));

        net.EndpointCount.ShouldBe(3);
    }

    /// <summary>
    /// The loopback flag is masked out of the key, so an app talking to its own address does not appear as
    /// two endpoints that have to be added back together later.
    /// </summary>
    [Fact]
    public void Add_LoopbackFlag_DoesNotSplitTheEndpointKey()
    {
        var net = new NetAccumulator();

        net.Add(Event(remote: "127.0.0.1", protocol: NetworkProtocol.Tcp | NetworkProtocol.Loopback));
        net.Add(Event(remote: "127.0.0.1", protocol: NetworkProtocol.Tcp | NetworkProtocol.Loopback));

        net.EndpointCount.ShouldBe(1);
    }

    /// <summary>
    /// The whole reason the cap exists. A torrent client or a port scan can touch tens of thousands of
    /// endpoints in a minute, and the accumulator must not grow with them.
    /// </summary>
    [Fact]
    public void Add_BeyondTheEndpointCap_FoldsIntoTheOverflowBucket()
    {
        var net = new NetAccumulator();

        for (var i = 0; i < NetAccumulator.MaxEndpointsPerApp + 500; i++)
        {
            net.Add(Event(remote: $"10.{i / 65536 % 256}.{i / 256 % 256}.{i % 256}", size: 10));
        }

        net.EndpointCount.ShouldBe(NetAccumulator.MaxEndpointsPerApp + 1, "the cap plus the (other) bucket");
        net.OverflowedEvents.ShouldBe(500);
        net.Endpoints.ShouldContainKey(NetAccumulator.Overflow);
    }

    /// <summary>
    /// <b>Dropping detail must never drop bytes.</b> The endpoint breakdown is allowed to become incomplete;
    /// the app's totals are not, because they are what the charts and the six months of history are made of.
    /// </summary>
    [Fact]
    public void Add_BeyondTheCap_KeepsTheTotalsExact()
    {
        var net = new NetAccumulator();
        const int Events = NetAccumulator.MaxEndpointsPerApp + 1_000;

        for (var i = 0; i < Events; i++)
        {
            net.Add(Event(remote: $"10.{i / 65536 % 256}.{i / 256 % 256}.{i % 256}", size: 7));
        }

        net.InBytes.ShouldBe(Events * 7L);
    }

    /// <summary>Bytes that overflowed are still in the breakdown, just not attributed to a named endpoint.</summary>
    [Fact]
    public void Add_OverflowBucket_CarriesTheBytesItAbsorbed()
    {
        var net = new NetAccumulator();

        for (var i = 0; i < NetAccumulator.MaxEndpointsPerApp; i++)
        {
            net.Add(Event(remote: $"10.{i / 65536 % 256}.{i / 256 % 256}.{i % 256}", size: 1));
        }

        net.Add(Event(remote: "203.0.113.7", size: 999));

        net.Endpoints[NetAccumulator.Overflow].InBytes.ShouldBe(999);
    }

    /// <summary>An event with no remote address still counts; it just cannot be attributed to an endpoint.</summary>
    [Fact]
    public void Add_EventWithNoRemoteAddress_CountsInTotalsAndLandsInOverflow()
    {
        var net = new NetAccumulator();

        net.Add(new NetworkEvent(100, 42, NetworkDirection.Outbound, NetworkProtocol.Udp, null, 0, 1_700_000_000));

        net.OutBytes.ShouldBe(42);
        net.Endpoints[NetAccumulator.Overflow].OutBytes.ShouldBe(42);
    }

    [Fact]
    public void Reset_EmptiesEverythingForTheNextWindow()
    {
        var net = new NetAccumulator();
        net.Add(Event(size: 500));

        net.Reset();

        net.InBytes.ShouldBe(0);
        net.EndpointCount.ShouldBe(0);
        net.OverflowedEvents.ShouldBe(0);
    }

    [Theory]
    [InlineData(true, "8.8.8.8", 443, NetworkProtocol.Tcp)]
    [InlineData(false, "8.8.8.8", 443, NetworkProtocol.Udp | NetworkProtocol.Quic)]
    [InlineData(false, "8.8.8.8", 53, NetworkProtocol.Udp)]
    [InlineData(true, "127.0.0.1", 8080, NetworkProtocol.Tcp | NetworkProtocol.Loopback)]
    [InlineData(true, "::1", 8080, NetworkProtocol.Tcp | NetworkProtocol.Loopback)]
    public void Classify_MatchesTheDocumentedRules(bool isTcp, string remote, int port, NetworkProtocol expected) =>
        NetworkEvent.Classify(isTcp, IPAddress.Parse("192.168.1.10"), IPAddress.Parse(remote), port)
            .ShouldBe(expected);

    /// <summary>A machine talking to its own routable address is still talking to itself.</summary>
    [Fact]
    public void Classify_RemoteEqualsLocal_IsLoopback() =>
        NetworkEvent.Classify(isTcp: true, IPAddress.Parse("192.168.1.10"), IPAddress.Parse("192.168.1.10"), 445)
            .ShouldHaveFlag(NetworkProtocol.Loopback);

    [Fact]
    public void Classify_NoRemoteAddress_IsNotLoopback() =>
        NetworkEvent.Classify(isTcp: true, IPAddress.Parse("192.168.1.10"), null, 443)
            .ShouldBe(NetworkProtocol.Tcp);

    /// <summary>
    /// One of these exists per network-active instance, constructed on a TraceEvent thread at the first
    /// packet. Pre-sizing its two dictionaries to <see cref="NetAccumulator.MaxEndpointsPerApp"/> cost about
    /// 250 KB each — ~75 MB for three hundred talkative processes, against the ~20 MB S1-lite leaves for the
    /// whole collector (docs/24_ADR.md §Findings, 2026-08-28). The cap is a ceiling, not a forecast.
    /// </summary>
    [Fact]
    public void Ctor_FreshInstance_AllocatesFarLessThanItsEndpointCap()
    {
        _ = new NetAccumulator();   // warm the type so its statics are not charged to the measurement

        var before = GC.GetAllocatedBytesForCurrentThread();
        var net = new NetAccumulator();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        _output.WriteLine($"a fresh NetAccumulator costs {allocated} bytes; "
            + $"300 network-active instances would be {300 * allocated / 1024} KB.");

        GC.KeepAlive(net);
        allocated.ShouldBeLessThan(1024,
            "the endpoint dictionaries must grow on demand, not be sized for the 2 000-entry cap");
    }
}

internal static class ProtocolAssertions
{
    internal static void ShouldHaveFlag(this NetworkProtocol actual, NetworkProtocol expected) =>
        actual.HasFlag(expected).ShouldBeTrue($"expected {expected} in {actual}");
}
