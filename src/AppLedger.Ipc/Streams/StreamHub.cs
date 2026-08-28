using System.Threading.Channels;

namespace AppLedger.Ipc.Streams;

/// <summary>
/// One subscriber's mailbox for one stream. Disposing it unsubscribes.
/// </summary>
public sealed class StreamSubscription : IDisposable
{
    private readonly StreamHub _hub;
    private readonly Channel<ReadOnlyMemory<byte>> _channel;
    private long _dropped;
    private long _consecutiveDrops;

    internal StreamSubscription(StreamHub hub, string stream)
    {
        _hub = hub;
        Stream = stream;

        _channel = Channel.CreateBounded<ReadOnlyMemory<byte>>(
            new BoundedChannelOptions(StreamHub.MailboxCapacity)
            {
                // docs/01_ARCHITECTURE.md §Backpressure: live streams drop oldest. Rollup inputs never do,
                // and nothing on this path can reach them.
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            },
            itemDropped: _ =>
            {
                Interlocked.Increment(ref _dropped);
                Interlocked.Increment(ref _consecutiveDrops);
            });
    }

    /// <summary>Which stream this is subscribed to.</summary>
    public string Stream { get; }

    /// <summary>Frames waiting for this subscriber, already serialized.</summary>
    public ChannelReader<ReadOnlyMemory<byte>> Reader => _channel.Reader;

    /// <summary>How many frames this subscriber has missed.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>
    /// True once this subscriber has missed <see cref="StreamHub.DisconnectAfterDroppedTicks"/> frames in a
    /// row, which the server treats as reason to close the connection.
    /// </summary>
    /// <remarks>
    /// Not tidiness. The server accepts four clients (<see cref="IpcProtocol.MaxServerInstances"/>), and a
    /// client that has stopped reading holds one of them for the lifetime of the Agent — a denial of service
    /// against the user's own other window.
    /// </remarks>
    public bool IsWedged => Interlocked.Read(ref _consecutiveDrops) >= StreamHub.DisconnectAfterDroppedTicks;

    internal void Post(ReadOnlyMemory<byte> frame)
    {
        var before = Interlocked.Read(ref _dropped);
        _channel.Writer.TryWrite(frame);

        // Nothing was evicted, so this subscriber is keeping up again.
        if (Interlocked.Read(ref _dropped) == before)
        {
            Interlocked.Exchange(ref _consecutiveDrops, 0);
        }
    }

    internal void Complete() => _channel.Writer.TryComplete();

    /// <summary>Unsubscribes and closes the mailbox.</summary>
    public void Dispose()
    {
        _hub.Remove(this);
        Complete();
    }
}

/// <summary>
/// Broadcasts one serialized frame to every subscriber of a stream (docs/07_IPC.md §Streams).
/// </summary>
/// <remarks>
/// <b>Why this exists at all.</b> <c>CollectorHost.Live</c> is a <i>queue</i>, not a broadcast: four readers
/// would each receive a disjoint subset of ticks and every one of them would draw a wrong chart. So the
/// Agent runs exactly one reader, serializes each tick <b>once</b>, and posts the same bytes to one bounded
/// mailbox per subscriber.
/// <para>
/// <b>Mailboxes hold two frames.</b> A subscriber one tick behind wants the newest tick, not a replay of ten
/// seconds it no longer cares about — and four clients times ten frames times 7 KB is 280 KB held against a
/// budget of about twenty megabytes for the entire collector. At two it is 56 KB.
/// </para>
/// <para>
/// <b>The frame is a fresh array per tick, not a pooled buffer.</b> Four writers finishing at different
/// times would each have to release a shared rental, and a use-after-return there is the kind of bug that
/// reproduces once a week in production. Seven kilobytes a second of gen-0 is a price worth paying to not
/// have that bug; it is named here rather than optimised away silently.
/// </para>
/// </remarks>
public sealed class StreamHub
{
    /// <summary>Frames a subscriber may fall behind before the oldest is dropped.</summary>
    public const int MailboxCapacity = 2;

    /// <summary>Consecutive dropped frames after which a subscriber is considered wedged.</summary>
    public const long DisconnectAfterDroppedTicks = 60;

    /// <summary>The <c>apps</c> stream: every running app, once a second.</summary>
    public const string AppsStream = "apps";

    /// <summary>The <c>health</c> stream: the Agent's own cost, every ten seconds.</summary>
    public const string HealthStream = "health";

    private readonly Lock _gate = new();
    private readonly Dictionary<string, StreamSubscription[]> _byStream = [];
    private long _dropped;

    /// <summary>Frames dropped across every subscriber.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Adds a subscriber to a stream.</summary>
    public StreamSubscription Subscribe(string stream)
    {
        ArgumentException.ThrowIfNullOrEmpty(stream);

        var subscription = new StreamSubscription(this, stream);

        lock (_gate)
        {
            _byStream[stream] = _byStream.TryGetValue(stream, out var existing)
                ? [.. existing, subscription]
                : [subscription];
        }

        return subscription;
    }

    /// <summary>How many subscribers a stream currently has.</summary>
    public int SubscriberCount(string stream)
    {
        lock (_gate)
        {
            return _byStream.TryGetValue(stream, out var subscribers) ? subscribers.Length : 0;
        }
    }

    /// <summary>
    /// Posts one already-serialized frame to every subscriber of a stream.
    /// </summary>
    /// <returns>How many subscribers it reached.</returns>
    public int Publish(string stream, ReadOnlyMemory<byte> frame)
    {
        ArgumentException.ThrowIfNullOrEmpty(stream);

        // The array is swapped whole on subscribe and unsubscribe, so the publish path reads one reference
        // and never takes a lock. A pump that blocks is a pump that makes the collector drop ticks.
        StreamSubscription[] subscribers;
        lock (_gate)
        {
            if (!_byStream.TryGetValue(stream, out subscribers!))
            {
                return 0;
            }
        }

        foreach (var subscriber in subscribers)
        {
            var before = subscriber.Dropped;
            subscriber.Post(frame);
            if (subscriber.Dropped != before)
            {
                Interlocked.Increment(ref _dropped);
            }
        }

        return subscribers.Length;
    }

    /// <summary>Closes every mailbox, so each reader's loop ends.</summary>
    public void CompleteAll()
    {
        lock (_gate)
        {
            foreach (var subscribers in _byStream.Values)
            {
                foreach (var subscriber in subscribers)
                {
                    subscriber.Complete();
                }
            }

            _byStream.Clear();
        }
    }

    internal void Remove(StreamSubscription subscription)
    {
        lock (_gate)
        {
            if (!_byStream.TryGetValue(subscription.Stream, out var existing))
            {
                return;
            }

            var remaining = existing.Where(s => !ReferenceEquals(s, subscription)).ToArray();
            if (remaining.Length == 0)
            {
                _byStream.Remove(subscription.Stream);
            }
            else
            {
                _byStream[subscription.Stream] = remaining;
            }
        }
    }
}
