using System.Threading.Channels;
using AppLedger.Core.Metrics;

namespace AppLedger.Collector.Live;

/// <summary>
/// The 1 Hz stream of app snapshots the UI subscribes to over the pipe.
/// </summary>
/// <remarks>
/// <b>Bounded, drop-oldest, deliberately.</b> docs/01_ARCHITECTURE.md §Backpressure draws the line: live
/// streams may drop, rollup inputs never may. A UI that stalls — minimised, a slow pipe, a debugger
/// breakpoint — must not be able to make the collector block, grow, or fall behind on the history it is
/// there to record. Dropping the oldest second means a stalled reader resumes on *current* data instead of
/// replaying a queue of stale seconds it no longer wants.
/// <para>
/// The rollup path never touches this channel. It takes the same samples by a separate reference, so a drop
/// here can never lose a stored row.
/// </para>
/// </remarks>
public sealed class LiveChannel
{
    private readonly Channel<IReadOnlyList<AppSample>> _channel;

    /// <summary>Creates a channel that holds at most <paramref name="capacity"/> published seconds.</summary>
    public LiveChannel(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        _channel = Channel.CreateBounded<IReadOnlyList<AppSample>>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleWriter = true,
                SingleReader = false,
                AllowSynchronousContinuations = false,
            },
            _ => Interlocked.Increment(ref _dropped));
    }

    private long _dropped;

    /// <summary>
    /// How many seconds were dropped because no one read them fast enough. Surfaced in the health report:
    /// a non-zero count means the UI is behind, not that the collector missed anything.
    /// </summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>The reader side, for the pipe server.</summary>
    public ChannelReader<IReadOnlyList<AppSample>> Reader => _channel.Reader;

    /// <summary>
    /// Publishes one second. Never blocks and never fails: a full channel drops its oldest entry, which is
    /// the behaviour the bounded options were chosen for.
    /// </summary>
    public void Publish(IReadOnlyList<AppSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        // TryWrite on a DropOldest channel always succeeds while the channel is open.
        _channel.Writer.TryWrite(samples);
    }

    /// <summary>Closes the stream so readers finish cleanly on shutdown.</summary>
    public void Complete() => _channel.Writer.TryComplete();
}
