using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AppLedger.Collector.Processes;
using AppLedger.Core.Identity;
using AppLedger.Core.Metrics;

namespace AppLedger.Collector.Snapshots;

/// <summary>
/// Sums one second of per-instance deltas into one <see cref="AppSample"/> per app
/// (docs/05_COLLECTOR.md §Accumulators, step 3).
/// </summary>
/// <remarks>
/// This is where the product's central claim happens: the user thinks in apps, Windows exposes processes,
/// and the arithmetic that bridges them is here. Chrome is forty PIDs and one row.
/// <para>
/// An instance with no resolution is dropped rather than charted under a placeholder id. It can only
/// happen for a process that appeared and vanished inside one poll interval, and inventing an app for it
/// would put a row in the apps list that names nothing the user can find.
/// </para>
/// </remarks>
public sealed class SnapshotBuilder
{
    private readonly InstanceRegistry _registry;
    private readonly Dictionary<AppId, Accumulator> _byApp = [];

    /// <summary>Creates a builder over the registry that holds instance identity.</summary>
    public SnapshotBuilder(InstanceRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    /// <summary>
    /// How many instances the last call could not attribute to an app. Surfaced so the health report can
    /// show it rather than leaving the numbers quietly short.
    /// </summary>
    public int UnattributedInstances { get; private set; }

    /// <summary>
    /// How many exiting instances' last sensor bytes were dropped because their app had no surviving
    /// instance in the same second. A sample must mean "this app ran this second", so inventing one for an
    /// app that has just gone would put a row in the list naming nothing the user can find.
    /// </summary>
    public int ExitResidueDropped { get; private set; }

    /// <summary>
    /// Builds the samples for one second. A tick that re-baselined carries no deltas and therefore
    /// produces no samples — the second is genuinely unknown, and a zero would be a claim we cannot make.
    /// </summary>
    /// <param name="tick">One second of per-instance deltas from the process table.</param>
    /// <param name="sensors">
    /// Everything the process poller cannot see — ETW bytes and GPU counters, keyed by the same instance.
    /// Null in Lite mode and in the tests that only care about the per-app arithmetic.
    /// </param>
    public IReadOnlyList<AppSample> Build(in ProcessTick tick, IInstanceMetricsSource? sensors = null)
    {
        var metrics = sensors ?? IInstanceMetricsSource.None;

        _byApp.Clear();
        UnattributedInstances = 0;
        ExitResidueDropped = 0;

        foreach (var delta in tick.Deltas)
        {
            var instance = _registry.Lookup(delta.Key);
            if (instance is null)
            {
                UnattributedInstances++;
                continue;
            }

            // Taken before the ref is obtained. TryTake cannot touch _byApp today, but a ref from
            // GetValueRefOrAddDefault is invalidated by any add to the same dictionary, and "does not"
            // is a weaker guarantee than "cannot".
            metrics.TryTake(delta.Key, out var extras);

            ref var accumulator = ref CollectionsMarshal.GetValueRefOrAddDefault(
                _byApp, instance.Value.AppId, out _);
            accumulator.Add(delta);
            accumulator.Add(in extras);
        }

        // Instances that exited during this tick have no delta, so their last bytes are reachable only here.
        foreach (var (appId, extras) in metrics.DrainExited())
        {
            ref var accumulator = ref CollectionsMarshal.GetValueRefOrNullRef(_byApp, appId);
            if (Unsafe.IsNullRef(ref accumulator))
            {
                ExitResidueDropped++;
                continue;
            }

            accumulator.Add(in extras);
        }

        if (_byApp.Count == 0)
        {
            return [];
        }

        var degraded = tick.Rebaselined || metrics.DegradedWindow;
        var samples = new List<AppSample>(_byApp.Count);
        foreach (var (appId, accumulator) in _byApp)
        {
            samples.Add(accumulator.ToSample(appId, tick.TsUtc, degraded));
        }

        // Sorted by app id so the live stream and the ring are deterministic, which makes a snapshot
        // comparable to the one before it without sorting at every read.
        samples.Sort(static (a, b) => string.CompareOrdinal(a.AppId.Value, b.AppId.Value));
        return samples;
    }

    /// <summary>
    /// One app's running totals for the current second. A mutable struct held by reference in the
    /// dictionary, so summing forty Chrome processes allocates nothing.
    /// </summary>
    private struct Accumulator
    {
        private int _procs;
        private double _cpuPct;
        private long _cpuUserMs;
        private long _cpuKernelMs;
        private long _wsPrivate;
        private long _commitBytes;
        private long _ws;
        private long _ioRead;
        private long _ioWrite;
        private int _threads;
        private int _handles;
        private long _hardFaults;
        private long _diskRead;
        private long _diskWrite;
        private long _diskOps;
        private long _netIn;
        private long _netOut;
        private long _netInLoopback;
        private long _netOutLoopback;
        private double _gpuPct;
        private long _vramDedicated;
        private long _vramShared;

        internal void Add(in ProcessDelta delta)
        {
            _procs++;
            _cpuPct += delta.CpuPct;
            _cpuUserMs += delta.CpuUserMs;
            _cpuKernelMs += delta.CpuKernelMs;
            _wsPrivate += delta.WsPrivate;
            _commitBytes += delta.CommitBytes;
            _ws += delta.Ws;
            _ioRead += delta.IoRead;
            _ioWrite += delta.IoWrite;
            _threads += delta.Threads;
            _handles += delta.Handles;
            _hardFaults += delta.HardFaults;
        }

        internal void Add(in InstanceExtras extras)
        {
            _diskRead += extras.DiskRead;
            _diskWrite += extras.DiskWrite;
            _diskOps += extras.DiskOps;
            _netIn += extras.NetIn;
            _netOut += extras.NetOut;
            _netInLoopback += extras.NetInLoopback;
            _netOutLoopback += extras.NetOutLoopback;
            _gpuPct += extras.GpuPct;
            _vramDedicated += extras.VramDedicated;
            _vramShared += extras.VramShared;
        }

        internal readonly AppSample ToSample(AppId appId, long tsUtc, bool degraded) => new()
        {
            AppId = appId,
            TsUtc = tsUtc,
            Procs = _procs,

            // CPU percentages add across an app's processes and are capped once at the app level, the same
            // convention a single process follows. Forty renderers at 3 % is one browser at 100 %, not 120.
            CpuPct = Math.Min(_cpuPct, 100d),
            CpuUserMs = _cpuUserMs,
            CpuKernelMs = _cpuKernelMs,
            WsPrivate = _wsPrivate,
            CommitBytes = _commitBytes,
            Ws = _ws,
            IoRead = _ioRead,
            IoWrite = _ioWrite,
            Threads = _threads,
            Handles = _handles,
            HardFaults = _hardFaults,

            // GPU follows the same convention as CPU and for the same reason: a browser's forty processes
            // each taking a slice of one engine is one browser, not four hundred percent of a GPU.
            GpuPct = Math.Min(_gpuPct, 100d),
            VramDedicated = _vramDedicated,
            VramShared = _vramShared,
            DiskRead = _diskRead,
            DiskWrite = _diskWrite,
            DiskOps = _diskOps,
            NetIn = _netIn,
            NetOut = _netOut,
            NetInLoopback = _netInLoopback,
            NetOutLoopback = _netOutLoopback,

            Degraded = degraded,
        };
    }
}
