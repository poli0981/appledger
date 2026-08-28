using AppLedger.Collector.Accumulators;
using AppLedger.Collector.Processes;
using AppLedger.Core.Collection;
using AppLedger.Core.Identity;

namespace AppLedger.Collector.Snapshots;

/// <summary>
/// Joins the sensors that are keyed by process instance — ETW network and device I/O, and the GPU counters —
/// onto the second the <see cref="SnapshotBuilder"/> is assembling.
/// </summary>
/// <remarks>
/// Without this class every one of <c>AppSample</c>'s network, disk and GPU fields is structurally present
/// and permanently zero: the process poller cannot see real device reads, payload bytes or engine time, and
/// nothing else was folding them in. That is the kind of gap that ships — the build is clean, the tests are
/// green, and six months of history quietly record that nothing ever touched the network.
/// <para>
/// It is driven from <c>CollectorHost</c> and must run <b>before</b> <see cref="InstanceRegistry.Apply"/>,
/// because an exiting instance's last bytes can only be charged to an app while the registry still knows
/// which app that was.
/// </para>
/// </remarks>
public sealed class SensorJoin : IInstanceMetricsSource
{
    /// <summary>How often the GPU counters are read. PDH is the expensive part, not the arithmetic.</summary>
    public static readonly TimeSpan GpuInterval = TimeSpan.FromSeconds(2);

    private readonly EtwAccumulators _accumulators;
    private readonly PidMap _pids;
    private readonly IEtwSource? _etw;
    private readonly IGpuSource? _gpu;
    private readonly TimeSpan _gpuInterval;

    private readonly Dictionary<ProcessKey, GpuReading> _gpuByKey = [];
    private readonly List<(AppId AppId, InstanceExtras Extras)> _exitResidue = [];

    private TimeSpan _gpuTakenAt = TimeSpan.MinValue;
    private long _lastEventsLost;
    private long _lastHandlerErrors;

    /// <summary>Wires the join over the accumulators the ETW handlers write into.</summary>
    /// <param name="accumulators">Where the ETW handlers deposit per-instance bytes.</param>
    /// <param name="pids">PID to instance, maintained here from the process table's lifecycle events.</param>
    /// <param name="etw">The ETW source, or null in Lite mode, which constructs no session at all.</param>
    /// <param name="gpu">The GPU source, or null when the machine has no WDDM 2.x counters.</param>
    /// <param name="gpuInterval">Overrides <see cref="GpuInterval"/>; tests drive it deterministically.</param>
    public SensorJoin(
        EtwAccumulators accumulators,
        PidMap pids,
        IEtwSource? etw = null,
        IGpuSource? gpu = null,
        TimeSpan? gpuInterval = null)
    {
        ArgumentNullException.ThrowIfNull(accumulators);
        ArgumentNullException.ThrowIfNull(pids);

        _accumulators = accumulators;
        _pids = pids;
        _etw = etw;
        _gpu = gpu;
        _gpuInterval = gpuInterval ?? GpuInterval;
    }

    /// <summary>
    /// Builds a join over freshly created accumulators <b>and subscribes the ETW handlers to them</b>.
    /// </summary>
    /// <remarks>
    /// The subscription is the reason this factory exists rather than leaving the host process to assemble
    /// the pieces. Constructing the accumulators and forgetting to attach them to the source produces no
    /// error and no warning — just an Agent that records zero bytes for everything, which is the exact
    /// failure this whole class was written to remove. One call cannot be half-done.
    /// </remarks>
    /// <param name="etw">The ETW source, or null in Lite mode.</param>
    /// <param name="gpu">The GPU source, or null when the machine has no counters.</param>
    public static SensorJoin Create(IEtwSource? etw, IGpuSource? gpu)
    {
        var pids = new PidMap();
        var accumulators = new EtwAccumulators(pids, new DnsMap());

        if (etw is not null)
        {
            // Lambdas rather than method groups: the handlers take their event by `in`, and an
            // Action<T> cannot bind to that directly.
            etw.Network += e => accumulators.OnNetwork(e);
            etw.DiskIo += e => accumulators.OnDiskIo(e);
            etw.Dns += e => accumulators.OnDns(e);
        }

        return new SensorJoin(accumulators, pids, etw, gpu);
    }

    /// <summary>The accumulators the handlers write into, for the health report and for the DNS flush.</summary>
    public EtwAccumulators Accumulators => _accumulators;

    /// <inheritdoc/>
    public bool DegradedWindow { get; private set; }

    /// <summary>How many GPU readings are currently being carried. For the health report and for tests.</summary>
    public int GpuReadings => _gpuByKey.Count;

    /// <summary>
    /// Opens the window for one tick: keeps the PID map in step with the process table, recovers the last
    /// bytes of instances that exited, refreshes the GPU readings when they are due, and decides whether the
    /// second is degraded.
    /// </summary>
    /// <param name="tick">The tick the process table just produced.</param>
    /// <param name="registry">Consulted for exiting instances, so it must not have been updated yet.</param>
    /// <param name="elapsed">Monotonic time, for the GPU cadence. Never wall-clock: it may step.</param>
    public void Apply(in ProcessTick tick, InstanceRegistry registry, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(registry);

        UpdateDegraded();
        _exitResidue.Clear();

        if (tick.Rebaselined)
        {
            // The interval is not trustworthy, so nothing accumulated across it is either: eight hours of
            // sleep arriving as one second of traffic is a worse answer than a hole in the chart.
            //
            // What is *not* discarded is the PID map. A clock step does not change which processes are
            // alive, and the very first tick of every session is re-baselined by definition - clearing the
            // map here (or returning before the lifecycle events below) would leave every running process
            // unattributed until it happened to restart, which is a silent, permanent zero.
            _accumulators.ResetWindow();
            _gpuByKey.Clear();
            _gpuTakenAt = TimeSpan.MinValue;
        }

        // Exits before starts, and the take before the forget. A PID reused inside one tick would otherwise
        // have its new slot cleared by the old instance's Forget.
        foreach (var exit in tick.Exited)
        {
            var instance = registry.Lookup(exit.Key);
            if (instance is not null && TryTake(exit.Key, out var extras))
            {
                _exitResidue.Add((instance.Value.AppId, extras));
            }

            _accumulators.Forget(exit.Key);
            _gpuByKey.Remove(exit.Key);
        }

        foreach (var start in tick.Started)
        {
            _pids.Set(start.Key);
        }

        RefreshGpu(elapsed);
    }

    /// <inheritdoc/>
    public bool TryTake(ProcessKey key, out InstanceExtras extras)
    {
        var net = _accumulators.TakeNetwork(key);
        var disk = _accumulators.TakeDisk(key);
        _gpuByKey.TryGetValue(key, out var gpu);

        extras = new InstanceExtras
        {
            NetIn = net.InBytes,
            NetOut = net.OutBytes,
            NetInLoopback = net.InBytesLoopback,
            NetOutLoopback = net.OutBytesLoopback,
            DiskRead = disk.ReadBytes,
            DiskWrite = disk.WriteBytes,
            DiskOps = disk.Operations,
            GpuPct = gpu.Percent,
            VramDedicated = gpu.Dedicated,
            VramShared = gpu.Shared,
        };

        return extras != default;
    }

    /// <inheritdoc/>
    public IReadOnlyList<(AppId AppId, InstanceExtras Extras)> DrainExited() => _exitResidue;

    private void UpdateDegraded()
    {
        var lost = _etw?.EventsLost ?? 0;
        var errors = _accumulators.HandlerErrors;

        // EventsLost is the sum of two sessions' counters, and a session that restarts starts again from
        // zero - so the value can go *down*. Only an increase is loss; a decrease re-baselines silently.
        // Deriving this from inequality would hatch every chart after any session restart.
        DegradedWindow = lost > _lastEventsLost || errors > _lastHandlerErrors;

        _lastEventsLost = lost;
        _lastHandlerErrors = errors;
    }

    private void RefreshGpu(TimeSpan elapsed)
    {
        if (_gpu is null || !_gpu.Health.IsRunning)
        {
            _gpuByKey.Clear();
            _gpuTakenAt = TimeSpan.MinValue;
            return;
        }

        if (_gpuTakenAt != TimeSpan.MinValue && elapsed - _gpuTakenAt < _gpuInterval)
        {
            // Between readings. The last one is carried forward rather than zeroed, which is what keeps the
            // minute average honest: Rollup divides by the sample count, so 30 readings appearing twice
            // across 60 samples average to the mean of the readings. Zeroing the off-second would report
            // exactly half (docs/24_ADR.md Findings, 2026-08-28).
            return;
        }

        // Due for a reading. Cleared first, so a process that has stopped using the GPU stops being charted
        // rather than keeping whatever it last had.
        _gpuByKey.Clear();
        foreach (var sample in _gpu.Sample())
        {
            if (_pids.Lookup(sample.ProcessId) is { } key)
            {
                _gpuByKey[key] = new GpuReading(
                    sample.UtilizationPercent,
                    sample.DedicatedBytes,
                    sample.SharedBytes);
            }
        }

        _gpuTakenAt = elapsed;
    }

    private readonly record struct GpuReading(double Percent, long Dedicated, long Shared);
}
