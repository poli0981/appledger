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
    /// Builds the samples for one second. A tick that re-baselined carries no deltas and therefore
    /// produces no samples — the second is genuinely unknown, and a zero would be a claim we cannot make.
    /// </summary>
    public IReadOnlyList<AppSample> Build(in ProcessTick tick)
    {
        _byApp.Clear();
        UnattributedInstances = 0;

        foreach (var delta in tick.Deltas)
        {
            var instance = _registry.Lookup(delta.Key);
            if (instance is null)
            {
                UnattributedInstances++;
                continue;
            }

            ref var accumulator = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(
                _byApp, instance.Value.AppId, out _);
            accumulator.Add(delta);
        }

        if (_byApp.Count == 0)
        {
            return [];
        }

        var samples = new List<AppSample>(_byApp.Count);
        foreach (var (appId, accumulator) in _byApp)
        {
            samples.Add(accumulator.ToSample(appId, tick.TsUtc, tick.Rebaselined));
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
            Degraded = degraded,
        };
    }
}
