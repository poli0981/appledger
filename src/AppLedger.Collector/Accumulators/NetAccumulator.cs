using System.Net;
using AppLedger.Core.Collection;

namespace AppLedger.Collector.Accumulators;

/// <summary>One remote endpoint's traffic within the current window.</summary>
/// <param name="Protocol">Transport plus the QUIC and loopback flags, OR-ed across events.</param>
/// <param name="InBytes">Payload bytes received.</param>
/// <param name="OutBytes">Payload bytes sent.</param>
/// <param name="FirstSeenUtc">First event for this endpoint.</param>
/// <param name="LastSeenUtc">Most recent event.</param>
public readonly record struct EndpointTotals(
    NetworkProtocol Protocol,
    long InBytes,
    long OutBytes,
    long FirstSeenUtc,
    long LastSeenUtc);

/// <summary>
/// Per-process network byte totals, split by direction and by loopback, plus a capped per-endpoint
/// breakdown (docs/05_COLLECTOR.md §Accumulators, docs/10_NETWORK_AND_DNS.md §Byte attribution).
/// </summary>
/// <remarks>
/// <b>The cap is the point.</b> A port scanner, a torrent client or a busy CDN-fronted app can touch tens of
/// thousands of endpoints in a minute, and an uncapped dictionary is an unbounded allocation on the ETW
/// thread — in a process whose entire remaining memory budget is about 20 MB. Beyond
/// <see cref="MaxEndpointsPerApp"/> everything folds into a single <c>(other)</c> bucket, so the app's
/// totals stay exact even when its breakdown stops being complete.
/// <para>
/// Totals and the breakdown are separate for exactly that reason: dropping detail must never drop bytes.
/// </para>
/// </remarks>
public sealed class NetAccumulator
{
    /// <summary>The per-app endpoint cap from docs/05 §Budget controls.</summary>
    public const int MaxEndpointsPerApp = 2_000;

    /// <summary>The key used for everything that overflowed the cap.</summary>
    public static EndpointKey Overflow { get; } = new(NetworkProtocol.None, "(other)", 0);

    // Deliberately not pre-sized to MaxEndpointsPerApp. The cap is a ceiling on what may be tracked, not a
    // prediction of what will be: one accumulator exists per network-active instance, and two dictionaries
    // sized for 2 000 entries cost about 250 KB each time - committed on a TraceEvent thread at the first
    // packet, in a process with roughly 20 MB of headroom for every collector structure combined. Three
    // hundred talkative processes would have been ~75 MB for capacity nearly all of them never use.
    // The cap itself lives in AddEndpoint, where it belongs, and is unaffected by this.
    private readonly Dictionary<EndpointKey, EndpointTotals> _endpoints = [];
    private readonly LinkedList<EndpointKey> _recency = new();
    private readonly Dictionary<EndpointKey, LinkedListNode<EndpointKey>> _nodes = [];

    /// <summary>Non-loopback payload bytes received.</summary>
    public long InBytes { get; private set; }

    /// <summary>Non-loopback payload bytes sent.</summary>
    public long OutBytes { get; private set; }

    /// <summary>Loopback payload bytes received, kept apart from the internet totals.</summary>
    public long InBytesLoopback { get; private set; }

    /// <summary>Loopback payload bytes sent.</summary>
    public long OutBytesLoopback { get; private set; }

    /// <summary>How many events folded into <c>(other)</c> because the cap was reached.</summary>
    public long OverflowedEvents { get; private set; }

    /// <summary>The endpoint breakdown, including the <c>(other)</c> bucket when it exists.</summary>
    public IReadOnlyDictionary<EndpointKey, EndpointTotals> Endpoints => _endpoints;

    /// <summary>How many distinct endpoints are currently tracked.</summary>
    public int EndpointCount => _endpoints.Count;

    /// <summary>Folds one event in. Totals always move; the breakdown may fold into <c>(other)</c>.</summary>
    public void Add(in NetworkEvent e)
    {
        AddTotals(e);
        AddEndpoint(e);
    }

    /// <summary>
    /// Empties the accumulator for the next window. Called after the per-second snapshot has taken what it
    /// needs, so the structures stay bounded by one window rather than by uptime.
    /// </summary>
    public void Reset()
    {
        InBytes = 0;
        OutBytes = 0;
        InBytesLoopback = 0;
        OutBytesLoopback = 0;
        OverflowedEvents = 0;
        _endpoints.Clear();
        _recency.Clear();
        _nodes.Clear();
    }

    private void AddTotals(in NetworkEvent e)
    {
        if (e.IsLoopback)
        {
            if (e.Direction == NetworkDirection.Inbound)
            {
                InBytesLoopback += e.Size;
            }
            else
            {
                OutBytesLoopback += e.Size;
            }

            return;
        }

        if (e.Direction == NetworkDirection.Inbound)
        {
            InBytes += e.Size;
        }
        else
        {
            OutBytes += e.Size;
        }
    }

    private void AddEndpoint(in NetworkEvent e)
    {
        var key = e.RemoteAddress is null
            ? Overflow
            : new EndpointKey(e.Protocol & ~NetworkProtocol.Loopback, e.RemoteAddress.ToString(), e.RemotePort);

        if (_endpoints.TryGetValue(key, out var existing))
        {
            _endpoints[key] = Merge(existing, e);
            Touch(key);
            return;
        }

        if (_endpoints.Count >= MaxEndpointsPerApp)
        {
            // The cap is reached. Rather than evicting a live endpoint to make room for a one-off - which
            // would churn on exactly the workload that hit the cap - everything new folds into (other).
            OverflowedEvents++;
            _endpoints[Overflow] = _endpoints.TryGetValue(Overflow, out var other)
                ? Merge(other, e)
                : Create(e);
            return;
        }

        _endpoints[key] = Create(e);
        _nodes[key] = _recency.AddLast(key);
    }

    private void Touch(EndpointKey key)
    {
        if (_nodes.TryGetValue(key, out var node))
        {
            _recency.Remove(node);
            _recency.AddLast(node);
        }
    }

    private static EndpointTotals Create(in NetworkEvent e) => new(
        e.Protocol,
        e.Direction == NetworkDirection.Inbound ? e.Size : 0,
        e.Direction == NetworkDirection.Outbound ? e.Size : 0,
        e.TsUtc,
        e.TsUtc);

    private static EndpointTotals Merge(in EndpointTotals existing, in NetworkEvent e) => existing with
    {
        Protocol = existing.Protocol | e.Protocol,
        InBytes = existing.InBytes + (e.Direction == NetworkDirection.Inbound ? e.Size : 0),
        OutBytes = existing.OutBytes + (e.Direction == NetworkDirection.Outbound ? e.Size : 0),
        FirstSeenUtc = System.Math.Min(existing.FirstSeenUtc, e.TsUtc),
        LastSeenUtc = System.Math.Max(existing.LastSeenUtc, e.TsUtc),
    };
}

/// <summary>
/// The per-endpoint accumulation key of docs/10 §Byte attribution: <c>(proto, remoteIp, remotePort)</c>.
/// </summary>
/// <remarks>
/// The address is a string rather than an <see cref="IPAddress"/> so the key is a value with cheap equality
/// and no reference chasing on the ETW thread. The loopback flag is deliberately masked out of the protocol
/// before keying, so an app's own address does not split into two endpoints.
/// </remarks>
/// <param name="Protocol">Transport, without the loopback flag.</param>
/// <param name="RemoteAddress">The far end, or <c>(other)</c> for the overflow bucket.</param>
/// <param name="RemotePort">The far end's port.</param>
public readonly record struct EndpointKey(NetworkProtocol Protocol, string RemoteAddress, int RemotePort);
