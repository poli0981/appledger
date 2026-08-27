using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using AppLedger.Core.Collection;
using AppLedger.Infrastructure.Network;
using Shouldly;
using Xunit;

namespace AppLedger.Infrastructure.Tests.Network;

/// <summary>
/// Adapter smoke test for <c>GetExtendedTcpTable</c> and <c>GetExtendedUdpTable</c>
/// (docs/19_TESTING.md §Layers: "GetExtendedTcpTable on a listening socket the test opens").
/// </summary>
/// <remarks>
/// It needs no elevation, which is the point: connections are one of the few things Lite mode can show in
/// full.
/// </remarks>
public sealed class ConnectionPollerTests
{
    /// <summary>
    /// The end-to-end case. A socket this test opens must come back attributed to this process, on the port
    /// it was actually bound to — which exercises the table read, the row stride and the byte-order
    /// conversion at once.
    /// </summary>
    [Fact]
    public void Sample_FindsAListenerThisTestOpened()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var rows = new ConnectionPoller().Sample();

        var mine = rows.Where(r => r.ProcessId == Environment.ProcessId && r.LocalPort == port).ToList();
        mine.ShouldNotBeEmpty($"the listener on port {port} was not found in the TCP table");
        mine[0].Protocol.ShouldBe(ConnectionProtocol.Tcp);
        mine[0].State.ShouldBe(ConnectionState.Listen);
        mine[0].IsListening.ShouldBeTrue();
    }

    /// <summary>An established connection is reported with both ends and the right state.</summary>
    [Fact]
    public async Task Sample_FindsAnEstablishedConnectionWithBothEnds()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var accepted = await listener.AcceptTcpClientAsync();

        var rows = new ConnectionPoller().Sample();

        var established = rows.Where(r =>
            r.ProcessId == Environment.ProcessId
            && r.State == ConnectionState.Established
            && (r.LocalPort == port || r.RemotePort == port)).ToList();

        established.ShouldNotBeEmpty();
        established[0].RemoteAddress.ShouldNotBeNull();
        established[0].IsLoopback.ShouldBeTrue();
    }

    [Fact]
    public void Sample_FindsAUdpSocketThisTestOpened()
    {
        using var udp = new UdpClient(0, AddressFamily.InterNetwork);
        var port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;

        var rows = new ConnectionPoller().Sample();

        var mine = rows.Where(r =>
            r.Protocol == ConnectionProtocol.Udp
            && r.ProcessId == Environment.ProcessId
            && r.LocalPort == port).ToList();

        mine.ShouldNotBeEmpty($"the UDP socket on port {port} was not found");

        // A UDP row is an endpoint, not a connection: there is no far end and no state to report.
        mine[0].State.ShouldBe(ConnectionState.None);
        mine[0].RemoteAddress.ShouldBeNull();
        mine[0].IsListening.ShouldBeTrue();
    }

    [Fact]
    public void Sample_FindsAnIpv6SocketThisTestOpened()
    {
        if (!Socket.OSSupportsIPv6)
        {
            return;
        }

        using var listener = new TcpListener(IPAddress.IPv6Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var rows = new ConnectionPoller().Sample();

        rows.ShouldContain(r =>
            r.ProcessId == Environment.ProcessId
            && r.LocalPort == port
            && r.LocalAddress.AddressFamily == AddressFamily.InterNetworkV6);
    }

    /// <summary>
    /// The conversion that would otherwise be invisible: ports arrive in network byte order, so reading
    /// them as little-endian turns 443 into 46853 — a number that still looks like a port.
    /// </summary>
    [Theory]
    [InlineData(0x0000BB01u, 443)]
    [InlineData(0x00005000u, 80)]
    [InlineData(0x00000000u, 0)]
    [InlineData(0x0000FFFFu, 65535)]
    public void ToPort_ConvertsFromNetworkByteOrder(uint networkOrder, int expected) =>
        ConnectionPoller.ToPort(networkOrder).ShouldBe(expected);

    /// <summary>Only the low 16 bits are the port; the table leaves the rest undefined.</summary>
    [Fact]
    public void ToPort_IgnoresTheHighBits() => ConnectionPoller.ToPort(0xDEAD5000u).ShouldBe(80);

    [Fact]
    public void Sample_EveryRowHasAUsableLocalAddressAndPid()
    {
        var rows = new ConnectionPoller().Sample();

        rows.ShouldNotBeEmpty("a live machine always has sockets open");
        rows.ShouldAllBe(r => r.LocalAddress != null);
        rows.ShouldAllBe(r => r.LocalPort >= 0 && r.LocalPort <= 65535);
        rows.ShouldAllBe(r => r.RemotePort >= 0 && r.RemotePort <= 65535);
    }

    /// <summary>
    /// A listener has no far end. The table reports 0.0.0.0:0, and passing that through would put
    /// "connected to 0.0.0.0" in front of the user.
    /// </summary>
    [Fact]
    public void Sample_ListeningRows_HaveNoRemoteEnd()
    {
        var rows = new ConnectionPoller().Sample();

        rows.Where(r => r.State == ConnectionState.Listen)
            .ShouldAllBe(r => r.RemoteAddress == null && r.RemotePort == 0);
    }

    /// <summary>Buffers are retained between polls, so a second call must still work rather than reuse stale bytes.</summary>
    [Fact]
    public void Sample_CalledRepeatedly_KeepsWorking()
    {
        var poller = new ConnectionPoller();

        var first = poller.Sample().Count;
        var second = poller.Sample().Count;

        first.ShouldBeGreaterThan(0);
        second.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task StartAsync_ReportsRunningWithoutElevation()
    {
        var poller = new ConnectionPoller();

        await poller.StartAsync();

        poller.Health.State.ShouldBe(SensorState.Running);
    }
}

/// <summary>
/// The four <c>MIB_*ROW_OWNER_PID</c> layouts are hand-written, so nothing but this stands between a
/// mistyped field and a table read at the wrong stride — which produces plausible garbage (PIDs that
/// exist, ports that look like ports) rather than an error.
/// </summary>
public sealed class MibRowLayoutTests
{
    [Fact]
    public void MibTcpRowOwnerPid_IsTwentyFourBytes() => Marshal.SizeOf<MibTcpRowOwnerPid>().ShouldBe(24);

    [Fact]
    public void MibTcp6RowOwnerPid_IsFiftySixBytes() => Marshal.SizeOf<MibTcp6RowOwnerPid>().ShouldBe(56);

    [Fact]
    public void MibUdpRowOwnerPid_IsTwelveBytes() => Marshal.SizeOf<MibUdpRowOwnerPid>().ShouldBe(12);

    [Fact]
    public void MibUdp6RowOwnerPid_IsTwentyEightBytes() => Marshal.SizeOf<MibUdp6RowOwnerPid>().ShouldBe(28);

    [Theory]
    [InlineData(nameof(MibTcpRowOwnerPid.State), 0)]
    [InlineData(nameof(MibTcpRowOwnerPid.LocalAddress), 4)]
    [InlineData(nameof(MibTcpRowOwnerPid.LocalPort), 8)]
    [InlineData(nameof(MibTcpRowOwnerPid.RemoteAddress), 12)]
    [InlineData(nameof(MibTcpRowOwnerPid.RemotePort), 16)]
    [InlineData(nameof(MibTcpRowOwnerPid.OwningPid), 20)]
    public void MibTcpRowOwnerPid_FieldOffsets_MatchTheNativeLayout(string field, int expected) =>
        Marshal.OffsetOf<MibTcpRowOwnerPid>(field).ToInt32().ShouldBe(expected);

    [Theory]
    [InlineData(nameof(MibTcp6RowOwnerPid.LocalAddress), 0)]
    [InlineData(nameof(MibTcp6RowOwnerPid.LocalScopeId), 16)]
    [InlineData(nameof(MibTcp6RowOwnerPid.LocalPort), 20)]
    [InlineData(nameof(MibTcp6RowOwnerPid.RemoteAddress), 24)]
    [InlineData(nameof(MibTcp6RowOwnerPid.RemoteScopeId), 40)]
    [InlineData(nameof(MibTcp6RowOwnerPid.RemotePort), 44)]
    [InlineData(nameof(MibTcp6RowOwnerPid.State), 48)]
    [InlineData(nameof(MibTcp6RowOwnerPid.OwningPid), 52)]
    public void MibTcp6RowOwnerPid_FieldOffsets_MatchTheNativeLayout(string field, int expected) =>
        Marshal.OffsetOf<MibTcp6RowOwnerPid>(field).ToInt32().ShouldBe(expected);
}
