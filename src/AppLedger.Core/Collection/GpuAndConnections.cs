using System.Net;

namespace AppLedger.Core.Collection;

/// <summary>One process's GPU usage at a sample instant (docs/04_DATA_SOURCES.md §C).</summary>
/// <param name="ProcessId">The process the counter instance names.</param>
/// <param name="UtilizationPercent">
/// The highest engine's utilization, which is the Task Manager convention: a process pinning the 3D engine
/// and idling the others is at 100 %, not at 25 %.
/// </param>
/// <param name="DedicatedBytes">Dedicated video memory.</param>
/// <param name="SharedBytes">Shared video memory.</param>
public readonly record struct GpuSample(int ProcessId, double UtilizationPercent, long DedicatedBytes, long SharedBytes);

/// <summary>
/// Reads per-process GPU counters. Absent on machines without WDDM 2.x counters, which is a normal state
/// and not a fault.
/// </summary>
public interface IGpuSource : ISensor
{
    /// <summary>
    /// One sample per process that currently has GPU counters. A process with no GPU work has no counter
    /// instance at all, so it is absent rather than zero — the UI shows "N/A", because a zero would claim
    /// we looked and found nothing.
    /// </summary>
    IReadOnlyList<GpuSample> Sample();
}

/// <summary>Transport of a connection table row.</summary>
public enum ConnectionProtocol
{
    /// <summary>TCP.</summary>
    Tcp,

    /// <summary>UDP. Every UDP row is an endpoint rather than a connection.</summary>
    Udp,
}

/// <summary>TCP connection states, as IP Helper reports them.</summary>
public enum ConnectionState
{
    /// <summary>Not applicable — every UDP row.</summary>
    None = 0,

    /// <summary>Accepting connections.</summary>
    Listen,

    /// <summary>Established.</summary>
    Established,

    /// <summary>Any of the handshake or teardown states.</summary>
    Transient,
}

/// <summary>
/// One row of the TCP or UDP table, joined to the process that owns the socket
/// (docs/10_NETWORK_AND_DNS.md §Connections).
/// </summary>
/// <param name="ProcessId">The owning process.</param>
/// <param name="Protocol">TCP or UDP.</param>
/// <param name="State">Connection state, or <see cref="ConnectionState.None"/> for UDP.</param>
/// <param name="LocalAddress">Near end.</param>
/// <param name="LocalPort">Near port.</param>
/// <param name="RemoteAddress">Far end, or null for a listener and for UDP.</param>
/// <param name="RemotePort">Far port.</param>
public readonly record struct ConnectionRow(
    int ProcessId,
    ConnectionProtocol Protocol,
    ConnectionState State,
    IPAddress LocalAddress,
    int LocalPort,
    IPAddress? RemoteAddress,
    int RemotePort)
{
    /// <summary>
    /// True when this row is a listening socket. Used for the <c>ListenOpened</c> event, which is the one
    /// network fact worth telling a user about unprompted (docs/02_SPEC.md FR-9).
    /// </summary>
    public bool IsListening => State == ConnectionState.Listen
        || (Protocol == ConnectionProtocol.Udp && RemoteAddress is null);

    /// <summary>True when both ends are on this machine.</summary>
    public bool IsLoopback =>
        IPAddress.IsLoopback(LocalAddress) || (RemoteAddress is not null && IPAddress.IsLoopback(RemoteAddress));
}

/// <summary>Reads the TCP and UDP tables with their owning PIDs. Works without elevation.</summary>
public interface IConnectionSource : ISensor
{
    /// <summary>Every current row across TCP v4/v6 and UDP v4/v6.</summary>
    IReadOnlyList<ConnectionRow> Sample();
}
