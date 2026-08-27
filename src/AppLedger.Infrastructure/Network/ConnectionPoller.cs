using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using AppLedger.Core.Collection;
using Windows.Win32;
using Windows.Win32.NetworkManagement.IpHelper;

namespace AppLedger.Infrastructure.Network;

/// <summary>
/// Reads the TCP and UDP tables with their owning PIDs (docs/10_NETWORK_AND_DNS.md §Connections).
/// </summary>
/// <remarks>
/// Unlike ETW, this needs no elevation at all, which is why Lite mode still shows connections while it
/// shows no bytes. Nothing here opens a socket, connects to anything, or touches a process: it reads the
/// same table <c>netstat</c> reads.
/// <para>
/// Buffers are retained between polls. At 1 Hz on a machine with a few thousand sockets, reallocating four
/// tables per second would be the poller's whole cost.
/// </para>
/// </remarks>
public sealed class ConnectionPoller : IConnectionSource
{
    private const uint AfInet = 2;
    private const uint AfInet6 = 23;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint NoError = 0;

    /// <summary>The ceiling on a single table read. Beyond this the machine is doing something pathological.</summary>
    internal const int MaxTableBytes = 16 * 1024 * 1024;

    private byte[] _tcp4 = new byte[8 * 1024];
    private byte[] _tcp6 = new byte[8 * 1024];
    private byte[] _udp4 = new byte[8 * 1024];
    private byte[] _udp6 = new byte[8 * 1024];

    private readonly List<ConnectionRow> _rows = new(512);

    /// <inheritdoc />
    public string Name => "ConnectionPoller";

    /// <inheritdoc />
    public SensorHealth Health { get; private set; } = SensorHealth.Stopped;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        Health = new SensorHealth(SensorState.Running);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Health = SensorHealth.Stopped;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IReadOnlyList<ConnectionRow> Sample()
    {
        _rows.Clear();

        ReadTcp4();
        ReadTcp6();
        ReadUdp4();
        ReadUdp6();

        return _rows;
    }

    private unsafe void ReadTcp4()
    {
        if (!Read(ref _tcp4, AfInet, isTcp: true, out var span))
        {
            return;
        }

        fixed (byte* p = span)
        {
            var count = *(uint*)p;
            var rows = (MibTcpRowOwnerPid*)(p + sizeof(uint));

            for (var i = 0u; i < count; i++)
            {
                ref var row = ref rows[i];
                _rows.Add(new ConnectionRow(
                    (int)row.OwningPid,
                    ConnectionProtocol.Tcp,
                    ToState(row.State),
                    new IPAddress(row.LocalAddress),
                    ToPort(row.LocalPort),

                    // A listener has no far end; the table reports 0.0.0.0:0 and reporting that as a peer
                    // would put "connected to 0.0.0.0" in front of the user.
                    row.State == MibTcpState.Listen ? null : new IPAddress(row.RemoteAddress),
                    row.State == MibTcpState.Listen ? 0 : ToPort(row.RemotePort)));
            }
        }
    }

    private unsafe void ReadTcp6()
    {
        if (!Read(ref _tcp6, AfInet6, isTcp: true, out var span))
        {
            return;
        }

        fixed (byte* p = span)
        {
            var count = *(uint*)p;
            var rows = (MibTcp6RowOwnerPid*)(p + sizeof(uint));

            for (var i = 0u; i < count; i++)
            {
                // A pointer rather than a ref: the address is a fixed-size buffer, and C# only allows
                // those to be read through a pinned expression.
                var row = &rows[i];
                var isListen = row->State == MibTcpState.Listen;

                _rows.Add(new ConnectionRow(
                    (int)row->OwningPid,
                    ConnectionProtocol.Tcp,
                    ToState(row->State),
                    ToIpv6(row->LocalAddress, row->LocalScopeId),
                    ToPort(row->LocalPort),
                    isListen ? null : ToIpv6(row->RemoteAddress, row->RemoteScopeId),
                    isListen ? 0 : ToPort(row->RemotePort)));
            }
        }
    }

    private unsafe void ReadUdp4()
    {
        if (!Read(ref _udp4, AfInet, isTcp: false, out var span))
        {
            return;
        }

        fixed (byte* p = span)
        {
            var count = *(uint*)p;
            var rows = (MibUdpRowOwnerPid*)(p + sizeof(uint));

            for (var i = 0u; i < count; i++)
            {
                ref var row = ref rows[i];
                _rows.Add(new ConnectionRow(
                    (int)row.OwningPid,
                    ConnectionProtocol.Udp,
                    ConnectionState.None,
                    new IPAddress(row.LocalAddress),
                    ToPort(row.LocalPort),
                    RemoteAddress: null,
                    RemotePort: 0));
            }
        }
    }

    private unsafe void ReadUdp6()
    {
        if (!Read(ref _udp6, AfInet6, isTcp: false, out var span))
        {
            return;
        }

        fixed (byte* p = span)
        {
            var count = *(uint*)p;
            var rows = (MibUdp6RowOwnerPid*)(p + sizeof(uint));

            for (var i = 0u; i < count; i++)
            {
                var row = &rows[i];
                _rows.Add(new ConnectionRow(
                    (int)row->OwningPid,
                    ConnectionProtocol.Udp,
                    ConnectionState.None,
                    ToIpv6(row->LocalAddress, row->LocalScopeId),
                    ToPort(row->LocalPort),
                    RemoteAddress: null,
                    RemotePort: 0));
            }
        }
    }

    /// <summary>
    /// Fills a retained buffer, growing it when the table has outgrown it. Returns false when the table
    /// could not be read at all, which is not worth an exception: one poll of one address family is missing
    /// and the next second will try again.
    /// </summary>
    private static bool Read(ref byte[] buffer, uint family, bool isTcp, out Span<byte> span)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var size = (uint)buffer.Length;
            var result = isTcp
                ? PInvoke.GetExtendedTcpTable(buffer, ref size, bOrder: false, family, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL, Reserved: 0)
                : PInvoke.GetExtendedUdpTable(buffer, ref size, bOrder: false, family, UDP_TABLE_CLASS.UDP_TABLE_OWNER_PID, Reserved: 0);

            if (result == NoError)
            {
                span = buffer.AsSpan(0, (int)System.Math.Min(size, (uint)buffer.Length));
                return true;
            }

            if (result != ErrorInsufficientBuffer || size > MaxTableBytes)
            {
                span = default;
                return false;
            }

            // The table is a moving target, so growing to exactly the reported size often misses again.
            // A little headroom converges in one step and then stays.
            buffer = new byte[System.Math.Min((int)size + 4096, MaxTableBytes)];
        }

        span = default;
        return false;
    }

    /// <summary>
    /// Ports arrive in network byte order in the low 16 bits. Reading them as little-endian turns 443 into
    /// 46853 — a number that still looks like a port, which is why this is worth its own function and its
    /// own test.
    /// </summary>
    internal static int ToPort(uint networkOrder) =>
        (int)(((networkOrder & 0xFF) << 8) | ((networkOrder >> 8) & 0xFF));

    private static ConnectionState ToState(uint state) => state switch
    {
        MibTcpState.Listen => ConnectionState.Listen,
        MibTcpState.Established => ConnectionState.Established,
        _ => ConnectionState.Transient,
    };

    private static unsafe IPAddress ToIpv6(byte* address, uint scopeId)
    {
        var bytes = new ReadOnlySpan<byte>(address, 16);
        return new IPAddress(bytes, scopeId);
    }
}
