using System.Net;

namespace AppLedger.Collector.Accumulators;

/// <summary>
/// The address-to-hostname map learned from DNS-Client events, bounded and least-recently-used.
/// </summary>
/// <remarks>
/// <b>This map is global on purpose.</b> docs/10_NETWORK_AND_DNS.md §DNS is explicit: the mapping is stored
/// without the app that asked, because "which app resolved what" is a browsing history and the map alone
/// must not be able to reveal it. The per-app label the UI shows is built live and never persisted for a
/// Browser-category app unless the user opts in (docs/12_PRIVACY_AND_RETENTION.md).
/// <para>
/// The 10 000-entry cap is a memory decision from docs/05 §Budget controls. An address that falls out is
/// simply unlabelled — the connection still shows, as an IP. That is the honest degradation: an unnamed
/// address is a fact, a wrong name would not be.
/// </para>
/// </remarks>
public sealed class DnsMap
{
    /// <summary>The entry cap from docs/05 §Budget controls.</summary>
    public const int MaxEntries = 10_000;

    private readonly Dictionary<string, LinkedListNode<Entry>> _byAddress;
    private readonly LinkedList<Entry> _recency = new();
    private readonly int _capacity;
    private readonly Lock _gate = new();

    /// <summary>Creates a map.</summary>
    /// <param name="capacity">Entry cap; defaults to <see cref="MaxEntries"/>.</param>
    public DnsMap(int capacity = MaxEntries)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _byAddress = new Dictionary<string, LinkedListNode<Entry>>(System.Math.Min(capacity, 1024), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>How many addresses are currently known.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _byAddress.Count;
            }
        }
    }

    /// <summary>How many entries have been evicted for space, for the health report.</summary>
    public long Evicted { get; private set; }

    /// <summary>Records that an address resolved from a name. A repeat refreshes recency and the timestamp.</summary>
    public void Learn(IPAddress address, string host, long tsUtc)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (string.IsNullOrWhiteSpace(host))
        {
            return;
        }

        Learn(address.ToString(), host, tsUtc);
    }

    /// <summary>Records a mapping from an address already in string form.</summary>
    public void Learn(string address, string host, long tsUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        lock (_gate)
        {
            if (_byAddress.TryGetValue(address, out var node))
            {
                node.Value = node.Value with { Host = host, LastSeenUtc = tsUtc };
                _recency.Remove(node);
                _recency.AddLast(node);
                return;
            }

            if (_byAddress.Count >= _capacity)
            {
                var oldest = _recency.First;
                if (oldest is not null)
                {
                    _byAddress.Remove(oldest.Value.Address);
                    _recency.RemoveFirst();
                    Evicted++;
                }
            }

            _byAddress[address] = _recency.AddLast(new Entry(address, host, tsUtc));
        }
    }

    /// <summary>
    /// The hostname for an address, or null when it was never seen or has been evicted. A lookup counts as
    /// use, so an address the UI keeps asking about stays resident.
    /// </summary>
    public string? Lookup(string address)
    {
        if (string.IsNullOrEmpty(address))
        {
            return null;
        }

        lock (_gate)
        {
            if (!_byAddress.TryGetValue(address, out var node))
            {
                return null;
            }

            _recency.Remove(node);
            _recency.AddLast(node);
            return node.Value.Host;
        }
    }

    /// <summary>Every mapping currently held, for the flush into <c>ip_names</c>.</summary>
    public IReadOnlyList<(string Address, string Host, long LastSeenUtc)> Snapshot()
    {
        lock (_gate)
        {
            return [.. _recency.Select(e => (e.Address, e.Host, e.LastSeenUtc))];
        }
    }

    /// <summary>Drops everything. Used by purge, which must leave no trace of what was resolved.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _byAddress.Clear();
            _recency.Clear();
        }
    }

    private readonly record struct Entry(string Address, string Host, long LastSeenUtc);
}
