using AppLedger.Core.Metrics;

namespace AppLedger.Core.Rollup;

/// <summary>
/// The rollup arithmetic of docs/05_COLLECTOR.md §Rollup math: counters sum, gauges average, peaks max.
/// Pure and golden-tested, because every number the UI shows for anything older than an hour comes out
/// of here, and the hourly SQL in docs/06 is verified against this implementation.
/// </summary>
public static class Rollup
{
    /// <summary>
    /// Folds the 1-second samples of one app inside one bucket into a single row. <paramref name="samples"/>
    /// may be shorter than the bucket when the app started or exited mid-bucket: <c>RuntimeSeconds</c> is
    /// the sample count, not the bucket length, and averages divide by it.
    /// </summary>
    /// <param name="bucketStartUtc">Bucket start in UTC epoch seconds; becomes the row's <c>Ts</c>.</param>
    /// <param name="samples">Samples belonging to this bucket, all for the same app.</param>
    public static MetricRow FromSamples(long bucketStartUtc, IReadOnlyList<AppSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
        {
            throw new ArgumentException("A rollup bucket needs at least one sample.", nameof(samples));
        }

        var n = samples.Count;
        var first = samples[0];

        double procs = 0, cpu = 0, gpu = 0, threads = 0, handles = 0;
        double wsPrivate = 0, commit = 0, ws = 0, vramDed = 0, vramShared = 0;
        var procsMax = 0;
        double cpuMax = 0;
        long wsPrivateMax = 0, vramDedMax = 0;
        long cpuUserMs = 0, cpuKernelMs = 0;
        long ioRead = 0, ioWrite = 0, diskRead = 0, diskWrite = 0, diskOps = 0;
        long netIn = 0, netOut = 0, netInLb = 0, netOutLb = 0, hardFaults = 0;
        var degraded = false;

        foreach (var s in samples)
        {
            if (!s.AppId.Equals(first.AppId))
            {
                throw new ArgumentException("All samples in a bucket must belong to the same app.", nameof(samples));
            }

            procs += s.Procs;
            cpu += s.CpuPct;
            gpu += s.GpuPct;
            threads += s.Threads;
            handles += s.Handles;
            wsPrivate += s.WsPrivate;
            commit += s.CommitBytes;
            ws += s.Ws;
            vramDed += s.VramDedicated;
            vramShared += s.VramShared;

            procsMax = Math.Max(procsMax, s.Procs);
            cpuMax = Math.Max(cpuMax, s.CpuPct);
            wsPrivateMax = Math.Max(wsPrivateMax, s.WsPrivate);
            vramDedMax = Math.Max(vramDedMax, s.VramDedicated);

            cpuUserMs += s.CpuUserMs;
            cpuKernelMs += s.CpuKernelMs;
            ioRead += s.IoRead;
            ioWrite += s.IoWrite;
            diskRead += s.DiskRead;
            diskWrite += s.DiskWrite;
            diskOps += s.DiskOps;
            netIn += s.NetIn;
            netOut += s.NetOut;
            netInLb += s.NetInLoopback;
            netOutLb += s.NetOutLoopback;
            hardFaults += s.HardFaults;
            degraded |= s.Degraded;
        }

        return new MetricRow
        {
            AppId = first.AppId,
            Ts = bucketStartUtc,
            RuntimeSeconds = n,
            Procs = Round1(procs / n),
            ProcsMax = procsMax,
            CpuPct = Round1(cpu / n),
            CpuPctMax = Round1(cpuMax),
            CpuUserMs = cpuUserMs,
            CpuKernelMs = cpuKernelMs,
            WsPrivate = ToBytes(wsPrivate / n),
            WsPrivateMax = wsPrivateMax,
            CommitBytes = ToBytes(commit / n),
            Ws = ToBytes(ws / n),
            GpuPct = Round1(gpu / n),
            VramDedicated = ToBytes(vramDed / n),
            VramDedicatedMax = vramDedMax,
            VramShared = ToBytes(vramShared / n),
            IoRead = ioRead,
            IoWrite = ioWrite,
            DiskRead = diskRead,
            DiskWrite = diskWrite,
            DiskOps = diskOps,
            NetIn = netIn,
            NetOut = netOut,
            NetInLoopback = netInLb,
            NetOutLoopback = netOutLb,
            Threads = Round1(threads / n),
            Handles = Round1(handles / n),
            HardFaults = hardFaults,
            Degraded = degraded,
        };
    }

    /// <summary>
    /// Combines finer rows into one coarser row: 60 minutes into an hour, 24 hours into a day. Sums add,
    /// maxima take the maximum, and averages are weighted by <see cref="MetricRow.RuntimeSeconds"/> so an
    /// app that ran for five minutes of an hour does not drag the hour's average toward zero.
    /// </summary>
    /// <param name="bucketStartUtc">Start of the coarser bucket; becomes the row's <c>Ts</c>.</param>
    /// <param name="rows">Rows to combine, all for the same app.</param>
    public static MetricRow Combine(long bucketStartUtc, IReadOnlyList<MetricRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            throw new ArgumentException("A rollup bucket needs at least one row.", nameof(rows));
        }

        var first = rows[0];
        long runtime = 0;

        double procs = 0, cpu = 0, gpu = 0, threads = 0, handles = 0;
        double wsPrivate = 0, commit = 0, ws = 0, vramDed = 0, vramShared = 0;
        var procsMax = 0;
        double cpuMax = 0;
        long wsPrivateMax = 0, vramDedMax = 0;
        long cpuUserMs = 0, cpuKernelMs = 0;
        long ioRead = 0, ioWrite = 0, diskRead = 0, diskWrite = 0, diskOps = 0;
        long netIn = 0, netOut = 0, netInLb = 0, netOutLb = 0, hardFaults = 0;
        var degraded = false;

        foreach (var r in rows)
        {
            if (!r.AppId.Equals(first.AppId))
            {
                throw new ArgumentException("All rows in a bucket must belong to the same app.", nameof(rows));
            }

            var w = r.RuntimeSeconds;
            runtime += w;

            procs += r.Procs * w;
            cpu += r.CpuPct * w;
            gpu += r.GpuPct * w;
            threads += r.Threads * w;
            handles += r.Handles * w;
            wsPrivate += (double)r.WsPrivate * w;
            commit += (double)r.CommitBytes * w;
            ws += (double)r.Ws * w;
            vramDed += (double)r.VramDedicated * w;
            vramShared += (double)r.VramShared * w;

            procsMax = Math.Max(procsMax, r.ProcsMax);
            cpuMax = Math.Max(cpuMax, r.CpuPctMax);
            wsPrivateMax = Math.Max(wsPrivateMax, r.WsPrivateMax);
            vramDedMax = Math.Max(vramDedMax, r.VramDedicatedMax);

            cpuUserMs += r.CpuUserMs;
            cpuKernelMs += r.CpuKernelMs;
            ioRead += r.IoRead;
            ioWrite += r.IoWrite;
            diskRead += r.DiskRead;
            diskWrite += r.DiskWrite;
            diskOps += r.DiskOps;
            netIn += r.NetIn;
            netOut += r.NetOut;
            netInLb += r.NetInLoopback;
            netOutLb += r.NetOutLoopback;
            hardFaults += r.HardFaults;
            degraded |= r.Degraded;
        }

        // A bucket whose rows all have zero runtime carries only sums; averaging by zero would be NaN.
        double d = runtime == 0 ? 1 : runtime;

        return new MetricRow
        {
            AppId = first.AppId,
            Ts = bucketStartUtc,
            RuntimeSeconds = checked((int)runtime),
            Procs = Round1(procs / d),
            ProcsMax = procsMax,
            CpuPct = Round1(cpu / d),
            CpuPctMax = Round1(cpuMax),
            CpuUserMs = cpuUserMs,
            CpuKernelMs = cpuKernelMs,
            WsPrivate = ToBytes(wsPrivate / d),
            WsPrivateMax = wsPrivateMax,
            CommitBytes = ToBytes(commit / d),
            Ws = ToBytes(ws / d),
            GpuPct = Round1(gpu / d),
            VramDedicated = ToBytes(vramDed / d),
            VramDedicatedMax = vramDedMax,
            VramShared = ToBytes(vramShared / d),
            IoRead = ioRead,
            IoWrite = ioWrite,
            DiskRead = diskRead,
            DiskWrite = diskWrite,
            DiskOps = diskOps,
            NetIn = netIn,
            NetOut = netOut,
            NetInLoopback = netInLb,
            NetOutLoopback = netOutLb,
            Threads = Round1(threads / d),
            Handles = Round1(handles / d),
            HardFaults = hardFaults,
            Degraded = degraded,
        };
    }

    /// <summary>
    /// Percentages and averaged counts are stored as REAL with one decimal (docs/05 §Rollup math), which
    /// keeps a 6-month table small without changing anything a chart can show.
    /// </summary>
    internal static double Round1(double value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);

    /// <summary>Byte gauges are stored as INTEGER; averaging produces a fraction that we round to a byte.</summary>
    internal static long ToBytes(double value) => (long)Math.Round(value, MidpointRounding.AwayFromZero);
}
