using AppLedger.Core.Identity;
using AppLedger.Core.Process;

namespace AppLedger.Collector.Processes;

/// <summary>
/// Turns the cumulative counters of successive system snapshots into per-interval deltas, and reports
/// which instances appeared and disappeared (docs/01_ARCHITECTURE.md §Collector pipeline).
/// </summary>
/// <remarks>
/// Everything is keyed on <see cref="ProcessKey"/>, never on a bare PID. Windows reuses PIDs, so a table
/// keyed on PID alone would silently subtract a dead process's counters from a live one's and produce a
/// gigantic negative delta — or, worse, a plausible small one. Keying on <c>(pid, createTime)</c> makes
/// PID reuse look like what it is: one instance exited, another started.
/// <para>
/// Not thread-safe. One poller owns one table and calls <see cref="Update"/> from its own thread, which is
/// what the threading model of docs/05_COLLECTOR.md §Components already assumes.
/// </para>
/// </remarks>
public sealed class ProcessTable
{
    /// <summary>
    /// How far the wall clock may drift from monotonic time before the interval is thrown away. Sleep,
    /// resume, an NTP correction and a manual time change all show up here (docs/05 §Failure handling).
    /// </summary>
    public static readonly TimeSpan MaxClockDrift = TimeSpan.FromSeconds(5);

    private readonly Dictionary<ProcessKey, Counters> _previous = [];
    private readonly HashSet<ProcessKey> _seen = [];
    private readonly int _logicalCpuCount;

    private TimeSpan _lastElapsed;
    private long _lastTsUtc;
    private bool _hasBaseline;

    /// <summary>Creates a table.</summary>
    /// <param name="logicalCpuCount">
    /// The divisor for CPU percentage, so 100 % means "every core busy" the way Task Manager shows it.
    /// Injected rather than read from the environment so the arithmetic is testable.
    /// </param>
    public ProcessTable(int logicalCpuCount = 0) =>
        _logicalCpuCount = logicalCpuCount > 0 ? logicalCpuCount : Environment.ProcessorCount;

    /// <summary>How many instances the table currently believes are alive.</summary>
    public int LiveCount => _previous.Count;

    /// <summary>
    /// Folds one snapshot into the table and returns what changed since the last one.
    /// </summary>
    /// <param name="snapshot">The system-wide snapshot, already filtered to the sessions we keep.</param>
    /// <param name="tsUtc">Wall-clock sample instant, UTC epoch seconds.</param>
    /// <param name="elapsed">
    /// A monotonic reading from the same clock as the previous call. The difference between two readings is
    /// the interval; comparing it against the wall-clock difference is how a clock jump is caught.
    /// </param>
    public ProcessTick Update(ReadOnlySpan<RawProcessSample> snapshot, long tsUtc, TimeSpan elapsed)
    {
        var interval = elapsed - _lastElapsed;
        var wallDelta = TimeSpan.FromSeconds(tsUtc - _lastTsUtc);
        var trustworthy = _hasBaseline
            && interval > TimeSpan.Zero
            && (wallDelta - interval).Duration() <= MaxClockDrift;

        var deltas = trustworthy ? new List<ProcessDelta>(snapshot.Length) : [];
        var started = new List<ProcessLifecycleEvent>();
        var exited = new List<ProcessLifecycleEvent>();

        _seen.Clear();

        foreach (ref readonly var sample in snapshot)
        {
            _seen.Add(sample.Key);

            if (!_previous.TryGetValue(sample.Key, out var before))
            {
                // First sighting. There is no interval to measure against, so the instance contributes
                // nothing this tick and everything from the next one.
                started.Add(new ProcessLifecycleEvent(sample.Key, sample.ImageName, sample.ParentPid));
                _previous[sample.Key] = Counters.From(sample);
                continue;
            }

            if (trustworthy)
            {
                deltas.Add(ToDelta(sample, before, interval));
            }

            _previous[sample.Key] = Counters.From(sample);
        }

        CollectExited(exited);

        _lastElapsed = elapsed;
        _lastTsUtc = tsUtc;
        _hasBaseline = true;

        return new ProcessTick(tsUtc, deltas, started, exited, Rebaselined: !trustworthy);
    }

    /// <summary>
    /// Drops every instance and the baseline. The next <see cref="Update"/> reports everything as started
    /// and produces no deltas — which is what a resumed machine or a restarted sensor needs.
    /// </summary>
    public void Reset()
    {
        _previous.Clear();
        _hasBaseline = false;
        _lastElapsed = TimeSpan.Zero;
        _lastTsUtc = 0;
    }

    private void CollectExited(List<ProcessLifecycleEvent> exited)
    {
        if (_previous.Count == _seen.Count)
        {
            return;
        }

        // Materialise the keys before removing: mutating while enumerating a dictionary is not allowed, and
        // exits are rare enough that the allocation only happens when something actually exited.
        foreach (var key in _previous.Keys.Where(k => !_seen.Contains(k)).ToList())
        {
            exited.Add(new ProcessLifecycleEvent(key, _previous[key].ImageName, _previous[key].ParentPid));
            _previous.Remove(key);
        }
    }

    private ProcessDelta ToDelta(in RawProcessSample now, in Counters before, TimeSpan interval)
    {
        var userTicks = Advance(now.UserTime, before.UserTime);
        var kernelTicks = Advance(now.KernelTime, before.KernelTime);

        return new ProcessDelta
        {
            Key = now.Key,
            ImageName = now.ImageName,
            SessionId = now.SessionId,
            CpuPct = CpuPercent(userTicks + kernelTicks, interval),
            CpuUserMs = userTicks / TicksPerMillisecond,
            CpuKernelMs = kernelTicks / TicksPerMillisecond,
            WsPrivate = now.WorkingSetPrivate,
            Ws = now.WorkingSet,
            CommitBytes = now.PagefileUsage,
            IoRead = Advance(now.ReadTransferCount, before.ReadTransferCount),
            IoWrite = Advance(now.WriteTransferCount, before.WriteTransferCount),
            Threads = now.ThreadCount,
            Handles = now.HandleCount,
            HardFaults = Advance(now.HardFaultCount, before.HardFaultCount),
        };
    }

    /// <summary>100 ns ticks per millisecond, the unit the kernel reports CPU time in.</summary>
    private const long TicksPerMillisecond = 10_000;

    /// <summary>
    /// The difference between two readings of a counter that only ever climbs. A negative result means the
    /// counter did not climb — a driver resetting it, or a value we misread — and zero is the only honest
    /// answer: charting a negative would draw a spike downward, and charting the raw value would draw one
    /// the size of the process's whole lifetime.
    /// </summary>
    private static long Advance(long now, long before) => now > before ? now - before : 0;

    private double CpuPercent(long cpuTicks, TimeSpan interval)
    {
        var available = interval.TotalMilliseconds * _logicalCpuCount;
        if (available <= 0)
        {
            return 0;
        }

        var used = (double)cpuTicks / TicksPerMillisecond;

        // Capped at 100 the way Task Manager caps it. A process can genuinely exceed its share for one
        // sample when the scheduler catches up, and a 340 % CPU reading on a chart reads as a bug.
        return Math.Clamp(used / available * 100d, 0d, 100d);
    }

    /// <summary>
    /// The cumulative readings kept between polls, plus the two identity fields an exit event needs after
    /// the instance is gone from the snapshot.
    /// </summary>
    private readonly record struct Counters(
        string ImageName,
        int ParentPid,
        long UserTime,
        long KernelTime,
        long ReadTransferCount,
        long WriteTransferCount,
        long HardFaultCount)
    {
        internal static Counters From(in RawProcessSample sample) => new(
            sample.ImageName,
            sample.ParentPid,
            sample.UserTime,
            sample.KernelTime,
            sample.ReadTransferCount,
            sample.WriteTransferCount,
            sample.HardFaultCount);
    }
}
