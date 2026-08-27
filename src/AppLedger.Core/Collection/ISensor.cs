namespace AppLedger.Core.Collection;

/// <summary>What a sensor is currently able to do.</summary>
public enum SensorState
{
    /// <summary>Constructed but not started.</summary>
    Stopped = 0,

    /// <summary>Starting, or retrying after a failure.</summary>
    Starting,

    /// <summary>Producing data.</summary>
    Running,

    /// <summary>
    /// Permanently unavailable on this machine or in this privilege mode — no ETW without admin, no GPU
    /// counters without a WDDM 2.x driver. The UI shows "N/A" rather than zero, because a zero that means
    /// "we could not look" is a lie (docs/01_ARCHITECTURE.md §Degraded modes).
    /// </summary>
    Unavailable,
}

/// <summary>
/// A sensor's health, polled by the host and surfaced in <c>Health</c> and the budget strip.
/// </summary>
/// <param name="State">What the sensor can currently do.</param>
/// <param name="Detail">
/// A short reason for <see cref="SensorState.Unavailable"/>, e.g. a Win32 error name. Never a path, a host
/// or a command line: this reaches the UI and the log at Information level (docs/15_LOGGING.md).
/// </param>
/// <param name="HandlerErrors">
/// How many times a handler threw and the event was dropped. A throwing handler is caught and counted,
/// never re-thrown into a provider's callback loop (docs/05_COLLECTOR.md §Failure handling).
/// </param>
/// <param name="EventsLost">
/// How many events the underlying source reported losing. Any increase within a minute flags that minute
/// <c>degraded</c>, so a chart hatches the bucket instead of drawing a dip that never happened.
/// </param>
public readonly record struct SensorHealth(
    SensorState State,
    string? Detail = null,
    long HandlerErrors = 0,
    long EventsLost = 0)
{
    /// <summary>A sensor that has not been started.</summary>
    public static SensorHealth Stopped { get; } = new(SensorState.Stopped);

    /// <summary>A sensor that cannot run here, with the reason.</summary>
    public static SensorHealth Unavailable(string detail) => new(SensorState.Unavailable, detail);

    /// <summary>True when the sensor is producing data.</summary>
    public bool IsRunning => State == SensorState.Running;
}

/// <summary>
/// One source of collection data, supervised by the collector host (docs/05_COLLECTOR.md §Components).
/// </summary>
/// <remarks>
/// Sensors are adapters: the ETW hub, the process poller, the GPU poller, the connection poller. They live
/// in Infrastructure and are injected, which is why this port is in Core — the Collector orchestrates
/// sensors it cannot construct.
/// <para>
/// A sensor never throws out of <see cref="StartAsync"/> for a condition the machine simply does not
/// support. It reports <see cref="SensorState.Unavailable"/> and the collector carries on without it,
/// because "no GPU counters on this box" is a normal Tuesday, not a fault.
/// </para>
/// </remarks>
public interface ISensor
{
    /// <summary>A stable name for logs and for the health report, e.g. <c>EtwHub.Network</c>.</summary>
    string Name { get; }

    /// <summary>The sensor's current health. Read frequently; must not block.</summary>
    SensorHealth Health { get; }

    /// <summary>Starts producing. Returns once the sensor is running or has given up.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops producing and releases what it holds. Safe to call when never started.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}
