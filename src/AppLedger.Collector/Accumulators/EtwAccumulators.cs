using System.Collections.Concurrent;
using AppLedger.Core.Collection;
using AppLedger.Core.Identity;

namespace AppLedger.Collector.Accumulators;

/// <summary>One window's real device I/O.</summary>
/// <param name="ReadBytes">Bytes read from the device.</param>
/// <param name="WriteBytes">Bytes written to the device.</param>
/// <param name="Operations">Device operations, read and write together.</param>
public readonly record struct DiskTotals(long ReadBytes, long WriteBytes, long Operations);

/// <summary>Real device I/O for one process instance within the current window.</summary>
public sealed class DiskAccumulator
{
    private long _readBytes;
    private long _writeBytes;
    private long _operations;

    /// <summary>Bytes read from the device.</summary>
    public long ReadBytes => Interlocked.Read(ref _readBytes);

    /// <summary>Bytes written to the device.</summary>
    public long WriteBytes => Interlocked.Read(ref _writeBytes);

    /// <summary>Device operations, read and write together.</summary>
    public long Operations => Interlocked.Read(ref _operations);

    /// <summary>Folds one event in. Called from the ETW thread, so it only ever does interlocked adds.</summary>
    public void Add(in DiskIoEvent e)
    {
        if (e.IsWrite)
        {
            Interlocked.Add(ref _writeBytes, e.TransferSize);
        }
        else
        {
            Interlocked.Add(ref _readBytes, e.TransferSize);
        }

        Interlocked.Increment(ref _operations);
    }

    /// <summary>Reads the three totals and zeroes them, one field at a time.</summary>
    /// <remarks>
    /// Three exchanges are not one atomic operation, so an event landing between the first and the third can
    /// have its bytes counted in this second and its operation count in the next. That is **one event, one
    /// field, one second late** — never lost, never doubled. The alternative is a lock on a path that carries
    /// thousands of events a second during a file copy, which is a poor trade for a one-event skew in a
    /// counter the UI renders as "disk ops per second".
    /// </remarks>
    public DiskTotals Take() => new(
        Interlocked.Exchange(ref _readBytes, 0),
        Interlocked.Exchange(ref _writeBytes, 0),
        Interlocked.Exchange(ref _operations, 0));

    /// <summary>Empties for the next window without reading.</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _readBytes, 0);
        Interlocked.Exchange(ref _writeBytes, 0);
        Interlocked.Exchange(ref _operations, 0);
    }
}

/// <summary>
/// The per-instance accumulators the ETW handlers write into, and the one place events become attributable.
/// </summary>
/// <remarks>
/// <b>This is the seam that makes ETW testable.</b> The handlers take plain event records rather than
/// TraceEvent types, so the identical code path runs against a live session, against a recorded
/// <c>.etl</c>, and against a unit test's scripted input — which is exactly what docs/19_TESTING.md §Layers
/// means by "the same handlers, recorded input". Without this split, the only way to test byte attribution
/// would be to run an elevated session and generate real traffic.
/// <para>
/// <b>Threading.</b> Handlers run on TraceEvent's own callback threads and must never throw back into them:
/// a throwing handler is caught, counted, and the event dropped (docs/05 §Failure handling). Accumulators
/// are looked up in a <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed on the instance, and an event
/// for an unknown PID is counted rather than guessed at.
/// </para>
/// </remarks>
public sealed class EtwAccumulators
{
    private readonly ConcurrentDictionary<ProcessKey, NetAccumulator> _net = new();
    private readonly ConcurrentDictionary<ProcessKey, DiskAccumulator> _disk = new();
    private readonly PidMap _pids;
    private readonly DnsMap _dns;

    private long _unattributedEvents;
    private long _handlerErrors;

    /// <summary>Creates the accumulator set over a PID map and a DNS map.</summary>
    public EtwAccumulators(PidMap pids, DnsMap dns)
    {
        ArgumentNullException.ThrowIfNull(pids);
        ArgumentNullException.ThrowIfNull(dns);
        _pids = pids;
        _dns = dns;
    }

    /// <summary>
    /// Events whose PID matched no known instance. Expected and small — a process that started and exited
    /// between polls — but a large number means the poller is falling behind, so it is counted, not ignored.
    /// </summary>
    public long UnattributedEvents => Interlocked.Read(ref _unattributedEvents);

    /// <summary>Handlers that threw. The event is dropped; the exception never reaches TraceEvent's loop.</summary>
    public long HandlerErrors => Interlocked.Read(ref _handlerErrors);

    /// <summary>The address-to-hostname map these handlers feed.</summary>
    public DnsMap Dns => _dns;

    /// <summary>Network totals for an instance, or null when it has produced no traffic.</summary>
    public NetAccumulator? NetworkFor(ProcessKey key) => _net.GetValueOrDefault(key);

    /// <summary>Device I/O totals for an instance, or null when it has produced none.</summary>
    public DiskAccumulator? DiskFor(ProcessKey key) => _disk.GetValueOrDefault(key);

    /// <summary>
    /// Reads and zeroes one instance's network totals under the same lock the handler takes, so the window
    /// boundary has no gap for an event to fall through. Zero for an instance that has produced no traffic.
    /// </summary>
    public NetTotals TakeNetwork(ProcessKey key)
    {
        if (!_net.TryGetValue(key, out var accumulator))
        {
            return default;
        }

        lock (accumulator)
        {
            return accumulator.TakeTotals();
        }
    }

    /// <summary>Reads and zeroes one instance's device I/O. Zero for an instance that has produced none.</summary>
    public DiskTotals TakeDisk(ProcessKey key) =>
        _disk.TryGetValue(key, out var accumulator) ? accumulator.Take() : default;

    /// <summary>Handles one network event. Never throws.</summary>
    public void OnNetwork(in NetworkEvent e)
    {
        var key = _pids.Lookup(e.ProcessId);
        if (key is null)
        {
            Interlocked.Increment(ref _unattributedEvents);
            return;
        }

        try
        {
            var accumulator = _net.GetOrAdd(key.Value, static _ => new NetAccumulator());
            lock (accumulator)
            {
                accumulator.Add(e);
            }
        }
#pragma warning disable CA1031 // A handler on TraceEvent's callback thread must not let anything escape.
        catch (Exception)
        {
            Interlocked.Increment(ref _handlerErrors);
        }
#pragma warning restore CA1031
    }

    /// <summary>Handles one device-I/O event. Never throws.</summary>
    public void OnDiskIo(in DiskIoEvent e)
    {
        var key = _pids.Lookup(e.ProcessId);
        if (key is null)
        {
            Interlocked.Increment(ref _unattributedEvents);
            return;
        }

        try
        {
            _disk.GetOrAdd(key.Value, static _ => new DiskAccumulator()).Add(e);
        }
#pragma warning disable CA1031
        catch (Exception)
        {
            Interlocked.Increment(ref _handlerErrors);
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Handles one DNS answer. The mapping is learned globally and the asking process is deliberately not
    /// recorded with it (docs/10 §DNS).
    /// </summary>
    public void OnDns(in DnsEvent e)
    {
        try
        {
            foreach (var address in e.Addresses)
            {
                _dns.Learn(address, e.QueryName, e.TsUtc);
            }
        }
#pragma warning disable CA1031
        catch (Exception)
        {
            Interlocked.Increment(ref _handlerErrors);
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Discards every accumulator's contents without reading them, endpoint breakdown included.
    /// </summary>
    /// <remarks>
    /// This is not the per-second path — that is <see cref="TakeNetwork"/> and <see cref="TakeDisk"/>, which
    /// read and zero together. This is the *discard* path, for a tick whose interval cannot be trusted: after
    /// a sleep or a clock step, whatever accumulated across the gap belongs to no second we can name, and
    /// eight hours of traffic arriving as one second is worse than a hole (docs/05 §Failure handling).
    /// </remarks>
    public void ResetWindow()
    {
        foreach (var (_, accumulator) in _net)
        {
            lock (accumulator)
            {
                accumulator.Reset();
            }
        }

        foreach (var (_, accumulator) in _disk)
        {
            accumulator.Reset();
        }
    }

    /// <summary>Forgets an instance entirely. Called when the process table reports it exited.</summary>
    public void Forget(ProcessKey key)
    {
        _net.TryRemove(key, out _);
        _disk.TryRemove(key, out _);
        _pids.Clear(key.Pid);
    }
}
