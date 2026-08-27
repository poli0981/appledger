using System.Net;

namespace AppLedger.Core.Collection;

/// <summary>Transport of a network event, as classified by docs/10_NETWORK_AND_DNS.md §Byte attribution.</summary>
[Flags]
public enum NetworkProtocol
{
    /// <summary>Unclassified.</summary>
    None = 0,

    /// <summary>TCP.</summary>
    Tcp = 1,

    /// <summary>UDP.</summary>
    Udp = 2,

    /// <summary>UDP on remote port 443. QUIC is UDP, and separating it makes "the browser used QUIC" visible.</summary>
    Quic = 4,

    /// <summary>
    /// Local traffic. Counted, but kept apart from the "internet" totals — a 4 GB local database transfer is
    /// not bandwidth the user paid for.
    /// </summary>
    Loopback = 8,
}

/// <summary>Which way bytes moved.</summary>
public enum NetworkDirection
{
    /// <summary>Received.</summary>
    Inbound,

    /// <summary>Sent.</summary>
    Outbound,
}

/// <summary>
/// One <c>TcpIpSend/Recv</c> or <c>UdpIpSend/Recv</c>, reduced to the fields AppLedger uses
/// (docs/04_DATA_SOURCES.md §D).
/// </summary>
/// <remarks>
/// Deliberately a plain record over primitives rather than a TraceEvent type. That is what lets the same
/// handler run against a live session, against a recorded <c>.etl</c>, and against a unit test's scripted
/// input — the replay requirement of docs/19_TESTING.md §Layers — without the Collector ever referencing
/// TraceEvent.
/// </remarks>
/// <param name="ProcessId">The socket-owning process. HTTP.sys traffic attributes to <c>System</c>.</param>
/// <param name="Size">Payload bytes, no headers. Totals therefore sit a few percent under adapter counters.</param>
/// <param name="Direction">Sent or received.</param>
/// <param name="Protocol">Transport plus the QUIC and loopback flags.</param>
/// <param name="RemoteAddress">The far end, or null when the event carried none.</param>
/// <param name="RemotePort">The far end's port.</param>
/// <param name="TsUtc">Event time, UTC epoch seconds.</param>
public readonly record struct NetworkEvent(
    int ProcessId,
    long Size,
    NetworkDirection Direction,
    NetworkProtocol Protocol,
    IPAddress? RemoteAddress,
    int RemotePort,
    long TsUtc)
{
    /// <summary>True when this is local traffic and must stay out of the internet totals.</summary>
    public bool IsLoopback => Protocol.HasFlag(NetworkProtocol.Loopback);

    /// <summary>
    /// Classifies an event from its addresses, per docs/10 §Byte attribution: loopback when the far end is
    /// in <c>127/8</c> or <c>::1</c> or equals the near end; QUIC when UDP reaches remote port 443.
    /// </summary>
    public static NetworkProtocol Classify(
        bool isTcp,
        IPAddress? localAddress,
        IPAddress? remoteAddress,
        int remotePort)
    {
        var protocol = isTcp ? NetworkProtocol.Tcp : NetworkProtocol.Udp;

        if (!isTcp && remotePort == 443)
        {
            protocol |= NetworkProtocol.Quic;
        }

        if (IsLocal(localAddress, remoteAddress))
        {
            protocol |= NetworkProtocol.Loopback;
        }

        return protocol;
    }

    private static bool IsLocal(IPAddress? localAddress, IPAddress? remoteAddress)
    {
        if (remoteAddress is null)
        {
            return false;
        }

        if (IPAddress.IsLoopback(remoteAddress))
        {
            return true;
        }

        // A machine talking to its own routable address is still talking to itself.
        return localAddress is not null && remoteAddress.Equals(localAddress);
    }
}

/// <summary>One <c>DiskIORead</c> or <c>DiskIOWrite</c>: real device traffic, not the file-system cache.</summary>
/// <param name="ProcessId">Resolved from the issuing thread, which is why the Thread keyword is enabled.</param>
/// <param name="TransferSize">Bytes moved.</param>
/// <param name="IsWrite">Write when true, read when false.</param>
/// <param name="DiskNumber">Which physical disk, for the per-drive breakdown.</param>
/// <param name="TsUtc">Event time, UTC epoch seconds.</param>
public readonly record struct DiskIoEvent(int ProcessId, long TransferSize, bool IsWrite, int DiskNumber, long TsUtc);

/// <summary>
/// A DNS answer learned from <c>Microsoft-Windows-DNS-Client</c> event 3008 or 3020.
/// </summary>
/// <remarks>
/// The address-to-name mapping this produces is stored **globally**, never per app: a per-app reverse map is
/// a browsing history by another name (docs/10 §DNS, docs/06 <c>ip_names</c>).
/// </remarks>
/// <param name="ProcessId">Which process asked. Used for the live per-app label, not for what is stored.</param>
/// <param name="QueryName">The name that was resolved.</param>
/// <param name="Addresses">Every address the answer carried.</param>
/// <param name="TsUtc">Event time, UTC epoch seconds.</param>
public readonly record struct DnsEvent(
    int ProcessId,
    string QueryName,
    IReadOnlyList<IPAddress> Addresses,
    long TsUtc);

/// <summary>An <c>ImageLoad</c>, used for runtime detection and for spotting anti-cheat drivers.</summary>
/// <param name="ProcessId">The loading process.</param>
/// <param name="FileName">The image that was loaded.</param>
/// <param name="TsUtc">Event time, UTC epoch seconds.</param>
public readonly record struct ImageLoadEvent(int ProcessId, string FileName, long TsUtc);

/// <summary>
/// The live ETW feed. Adapters live in Infrastructure; the Collector consumes this port and never
/// references TraceEvent (docs/05_COLLECTOR.md §Layering).
/// </summary>
public interface IEtwSource : ISensor
{
    /// <summary>Raised for every network send and receive. Handlers must be allocation-free and must not throw.</summary>
    event Action<NetworkEvent>? Network;

    /// <summary>Raised for every real device read and write.</summary>
    event Action<DiskIoEvent>? DiskIo;

    /// <summary>Raised when a DNS answer is observed.</summary>
    event Action<DnsEvent>? Dns;

    /// <summary>Raised for every image load.</summary>
    event Action<ImageLoadEvent>? ImageLoad;

    /// <summary>
    /// Events the session reported losing since the last read. Any increase within a minute flags that
    /// minute <c>degraded</c>, so the chart hatches the bucket instead of drawing a dip that never happened.
    /// </summary>
    long EventsLost { get; }
}
