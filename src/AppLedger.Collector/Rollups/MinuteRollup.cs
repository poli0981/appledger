using AppLedger.Core.Identity;
using AppLedger.Core.Metrics;
using AppLedger.Core.Rollup;

namespace AppLedger.Collector.Rollups;

/// <summary>
/// Buffers one minute of per-second samples and, on each minute boundary, folds them into one
/// <c>metrics_1m</c> row per app (docs/06_DATA_MODEL.md §Rollup jobs).
/// </summary>
/// <remarks>
/// The arithmetic itself is <see cref="Rollup.FromSamples"/> in Core — pure, golden-tested, and the same
/// implementation the hourly SQL is verified against. What lives here is only the bucketing: which samples
/// belong to which minute, and when a minute is finished.
/// <para>
/// <b>Memory.</b> The buffer holds at most 60 samples per live app, which is the bound that matters: S1-lite
/// left roughly 20 MB for every structure in the collector put together (`docs/05` §Where the budget
/// actually binds). At ~200 bytes per sample that is ~1.2 MB for 100 apps, and it is released every minute.
/// </para>
/// </remarks>
public sealed class MinuteRollup
{
    /// <summary>Seconds per bucket. The tier name and this constant must agree.</summary>
    public const int BucketSeconds = 60;

    private readonly Dictionary<AppId, List<AppSample>> _buffer = [];
    private long _bucketStartUtc = -1;

    /// <summary>The bucket currently being filled, or -1 before the first sample.</summary>
    public long CurrentBucketStartUtc => _bucketStartUtc;

    /// <summary>How many apps have samples in the current bucket.</summary>
    public int BufferedApps => _buffer.Count;

    /// <summary>
    /// Adds one second of samples. When they belong to a later minute than the buffer, the buffer is
    /// closed and its rows returned; the new samples then open the next bucket.
    /// </summary>
    /// <returns>The completed rows, or an empty list when the minute is still filling.</returns>
    public IReadOnlyList<MetricRow> Add(IReadOnlyList<AppSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (samples.Count == 0)
        {
            return [];
        }

        var bucket = BucketOf(samples[0].TsUtc);

        // A sample from an earlier bucket than the one we are filling arrives after a clock step backwards.
        // Folding it in would corrupt a minute that is already closed, so it is dropped and counted.
        if (bucket < _bucketStartUtc)
        {
            LateSamples += samples.Count;
            return [];
        }

        IReadOnlyList<MetricRow> completed = [];
        if (_bucketStartUtc >= 0 && bucket != _bucketStartUtc)
        {
            completed = Flush();
        }

        _bucketStartUtc = bucket;

        foreach (var sample in samples)
        {
            if (!_buffer.TryGetValue(sample.AppId, out var list))
            {
                list = new List<AppSample>(BucketSeconds);
                _buffer[sample.AppId] = list;
            }

            list.Add(sample);
        }

        return completed;
    }

    /// <summary>
    /// How many samples arrived for a bucket that had already been written. Non-zero means the clock
    /// stepped backwards; the health report shows it rather than the rows quietly disagreeing.
    /// </summary>
    public long LateSamples { get; private set; }

    /// <summary>
    /// Closes the current bucket and returns its rows, leaving the buffer empty. Called on shutdown so a
    /// partial minute is still written — <c>runtime_s</c> is the sample count, not the bucket length, so a
    /// 20-second minute is a legitimate row rather than a broken one.
    /// </summary>
    public IReadOnlyList<MetricRow> Flush()
    {
        if (_buffer.Count == 0)
        {
            return [];
        }

        var rows = new List<MetricRow>(_buffer.Count);
        foreach (var (_, samples) in _buffer)
        {
            rows.Add(Rollup.FromSamples(_bucketStartUtc, samples));
        }

        _buffer.Clear();
        rows.Sort(static (a, b) => string.CompareOrdinal(a.AppId.Value, b.AppId.Value));
        return rows;
    }

    /// <summary>The start of the minute a UTC-epoch-second timestamp falls in.</summary>
    /// <remarks>
    /// A floor division, not a remainder. C#'s <c>%</c> goes negative for negative operands, and a clock
    /// set to 1969 during setup would then produce a bucket start of <c>ts + 30</c> instead of
    /// <c>ts - 30</c> - in the future, and never closing.
    /// </remarks>
    public static long BucketOf(long tsUtc)
    {
        var remainder = tsUtc % BucketSeconds;
        if (remainder < 0)
        {
            remainder += BucketSeconds;
        }

        return tsUtc - remainder;
    }
}
