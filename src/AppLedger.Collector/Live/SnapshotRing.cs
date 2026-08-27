using AppLedger.Core.Identity;
using AppLedger.Core.Metrics;

namespace AppLedger.Collector.Live;

/// <summary>
/// The last few minutes of per-second samples, in memory, for the UI's sparklines and for whatever has not
/// been rolled up into <c>metrics_1m</c> yet.
/// </summary>
/// <remarks>
/// <b>Why five minutes and not an hour.</b> docs/05_COLLECTOR.md said "3600 × apps; ~2 MB for 100 apps",
/// but those two halves disagree: a measured <see cref="AppSample"/> is 184 bytes, so an hour of 100 apps
/// is 66 MB — a third of the whole Agent budget, against the ~20 MB S1-lite left for every structure in the
/// collector put together. The 2 MB figure is the accurate half; it corresponds to roughly a minute.
/// <para>
/// A minute is also all the UI asks for: docs/08 §Pages wants 60-second sparklines, and the History page's
/// "1 h" range auto-picks the <c>metrics_1m</c> tier rather than reading 3600 ring points. Five minutes is
/// that minute with headroom, and it covers the not-yet-rolled-up window so a UI attaching mid-minute sees
/// continuous data.
/// </para>
/// <para>
/// Not thread-safe for writes; one snapshot thread appends and readers take a snapshot copy. That matches
/// the threading model of docs/05 §Components, where only <c>SnapshotBuilder</c> publishes.
/// </para>
/// </remarks>
public sealed class SnapshotRing
{
    private readonly IReadOnlyList<AppSample>[] _seconds;
    private readonly Lock _gate = new();
    private int _next;
    private int _count;

    /// <summary>Creates a ring covering <paramref name="window"/> at one sample per second.</summary>
    public SnapshotRing(TimeSpan window)
    {
        var capacity = (int)window.TotalSeconds;
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(window), window, "The ring must cover at least one second.");
        }

        _seconds = new IReadOnlyList<AppSample>[capacity];
    }

    /// <summary>How many seconds the ring can hold.</summary>
    public int Capacity => _seconds.Length;

    /// <summary>How many seconds it currently holds.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>
    /// Appends one second. When the ring is full the oldest second is overwritten, which is the whole point:
    /// the ring has a fixed memory cost regardless of how long the Agent has been running.
    /// </summary>
    public void Add(IReadOnlyList<AppSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        lock (_gate)
        {
            _seconds[_next] = samples;
            _next = (_next + 1) % _seconds.Length;
            _count = System.Math.Min(_count + 1, _seconds.Length);
        }
    }

    /// <summary>
    /// The samples of one app, oldest first, over the whole ring. This is what a sparkline draws.
    /// Seconds in which the app was not running are absent rather than zero — it was not idle, it was gone.
    /// </summary>
    public IReadOnlyList<AppSample> Slice(AppId appId, int maxSeconds = int.MaxValue)
    {
        var window = Snapshot(maxSeconds);
        var slice = new List<AppSample>(window.Count);

        foreach (var second in window)
        {
            foreach (var sample in second)
            {
                if (sample.AppId.Equals(appId))
                {
                    slice.Add(sample);
                    break;
                }
            }
        }

        return slice;
    }

    /// <summary>Every second currently held, oldest first.</summary>
    /// <param name="maxSeconds">At most this many of the most recent seconds.</param>
    public IReadOnlyList<IReadOnlyList<AppSample>> Snapshot(int maxSeconds = int.MaxValue)
    {
        lock (_gate)
        {
            var take = System.Math.Min(_count, maxSeconds);
            var result = new List<IReadOnlyList<AppSample>>(take);

            // Walk back from the newest slot, then reverse, so callers always see oldest-first.
            for (var i = 0; i < take; i++)
            {
                var index = (_next - 1 - i + _seconds.Length * 2) % _seconds.Length;
                result.Add(_seconds[index]);
            }

            result.Reverse();
            return result;
        }
    }

    /// <summary>Forgets everything. Used when the collector re-baselines after a clock jump.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_seconds);
            _next = 0;
            _count = 0;
        }
    }

    /// <summary>
    /// The memory this ring can occupy at most, for the health report and the budget strip. Reported rather
    /// than assumed, because the estimate in the doc was wrong by a factor of thirty.
    /// </summary>
    public static long EstimateBytes(TimeSpan window, int liveApps) =>
        (long)window.TotalSeconds * liveApps * SampleBytes;

    /// <summary>Measured size of one <see cref="AppSample"/>, asserted by a test so it cannot drift.</summary>
    public const int SampleBytes = 184;
}
