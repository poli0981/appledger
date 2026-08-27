using System.Runtime.InteropServices;

namespace AppLedger.Infrastructure.Network;

/// <summary>
/// The four <c>MIB_*ROW_OWNER_PID</c> shapes returned by <c>GetExtendedTcpTable</c> and
/// <c>GetExtendedUdpTable</c>.
/// </summary>
/// <remarks>
/// Hand-written for the same reason <c>WINTRUST_DATA</c> is: the APIs take the table as <c>void*</c>, so
/// CsWin32 has no dependency to follow and generates none of them. Offsets are asserted by
/// <c>MibRowLayoutTests</c>, because reading a table at the wrong stride produces plausible garbage — PIDs
/// that exist, ports that look like ports — rather than an error.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct MibTcpRowOwnerPid
{
    /// <summary>Connection state, from the <c>MIB_TCP_STATE</c> enumeration.</summary>
    internal uint State;

    /// <summary>Local IPv4 address, in network byte order.</summary>
    internal uint LocalAddress;

    /// <summary>Local port in the low 16 bits, network byte order.</summary>
    internal uint LocalPort;

    /// <summary>Remote IPv4 address, in network byte order.</summary>
    internal uint RemoteAddress;

    /// <summary>Remote port in the low 16 bits, network byte order.</summary>
    internal uint RemotePort;

    /// <summary>The process that owns the socket.</summary>
    internal uint OwningPid;
}

/// <summary>The IPv6 TCP row. Addresses are raw 16-byte arrays rather than a managed type.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MibTcp6RowOwnerPid
{
    /// <summary>Local IPv6 address.</summary>
    internal fixed byte LocalAddress[16];

    /// <summary>Local scope id, needed to disambiguate a link-local address.</summary>
    internal uint LocalScopeId;

    /// <summary>Local port in the low 16 bits, network byte order.</summary>
    internal uint LocalPort;

    /// <summary>Remote IPv6 address.</summary>
    internal fixed byte RemoteAddress[16];

    /// <summary>Remote scope id.</summary>
    internal uint RemoteScopeId;

    /// <summary>Remote port in the low 16 bits, network byte order.</summary>
    internal uint RemotePort;

    /// <summary>Connection state.</summary>
    internal uint State;

    /// <summary>The process that owns the socket.</summary>
    internal uint OwningPid;
}

/// <summary>The IPv4 UDP row. A UDP row is an endpoint: there is no remote end and no state.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MibUdpRowOwnerPid
{
    /// <summary>Local IPv4 address, in network byte order.</summary>
    internal uint LocalAddress;

    /// <summary>Local port in the low 16 bits, network byte order.</summary>
    internal uint LocalPort;

    /// <summary>The process that owns the socket.</summary>
    internal uint OwningPid;
}

/// <summary>The IPv6 UDP row.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MibUdp6RowOwnerPid
{
    /// <summary>Local IPv6 address.</summary>
    internal fixed byte LocalAddress[16];

    /// <summary>Local scope id.</summary>
    internal uint LocalScopeId;

    /// <summary>Local port in the low 16 bits, network byte order.</summary>
    internal uint LocalPort;

    /// <summary>The process that owns the socket.</summary>
    internal uint OwningPid;
}

/// <summary>The <c>MIB_TCP_STATE</c> values AppLedger distinguishes.</summary>
internal static class MibTcpState
{
    /// <summary>Accepting connections.</summary>
    internal const uint Listen = 2;

    /// <summary>Established.</summary>
    internal const uint Established = 5;
}
