namespace AppLedger.Core.Collection;

/// <summary>One sensor's health together with the name it is reported under (docs/07_IPC.md §Handshake).</summary>
/// <param name="Name">
/// <see cref="ISensor.Name"/>, verbatim. The wire uses these keys rather than a parallel vocabulary, so a new
/// sensor needs no protocol change and a reported value cannot disagree with what the host observed.
/// </param>
/// <param name="Health">What that sensor can currently do.</param>
public readonly record struct SensorReport(string Name, SensorHealth Health);

/// <summary>
/// Everything <c>Health</c>, <c>HealthTick</c> and the budget strip need, read in one call
/// (docs/07_IPC.md §Streams, docs/15_LOGGING.md §Agent self-watch).
/// </summary>
/// <remarks>
/// The counters below share a property that makes them worth collecting in one type: every one of them is a
/// place where the collector <b>knows</b> it missed something. A dropped live tick, a late sample, an
/// unattributed event — each is invisible in the data itself, because what they produce is a number that is
/// merely a little too small. Left unreported they are indistinguishable from a quiet machine.
/// <para>
/// Built on demand, at the <c>HealthTick</c> cadence — never per tick. It allocates, and the tick path is
/// measured in microseconds.
/// </para>
/// <para>
/// Process CPU and working set are deliberately absent. They are facts about whichever process is hosting
/// the collector, not about the collector: in Lite mode the same library runs inside a WPF UI whose working
/// set says nothing about the cost of collecting. The host merges its own reading in.
/// </para>
/// </remarks>
public sealed record CollectorHealth
{
    /// <summary>When this snapshot was taken, UTC epoch seconds.</summary>
    public required long TsUtc { get; init; }

    /// <summary>True when no UI has been seen for the idle threshold, so the cheaper profile is in force.</summary>
    public required bool IsIdle { get; init; }

    /// <summary>The interval between ticks right now.</summary>
    public required TimeSpan CurrentInterval { get; init; }

    /// <summary>Apps that produced a sample in the most recent tick.</summary>
    public required int LiveApps { get; init; }

    /// <summary>Process instances the registry currently knows.</summary>
    public required int LiveInstances { get; init; }

    /// <summary>Rows written to history this session.</summary>
    public required long RowsWritten { get; init; }

    /// <summary>Seconds of per-second detail currently held in the ring.</summary>
    public required int RingSeconds { get; init; }

    /// <summary>Live ticks dropped because a reader fell behind. Allowed by design, but never silently.</summary>
    public required long LiveDropped { get; init; }

    /// <summary>Samples that arrived for an already-written minute. Non-zero means the clock stepped back.</summary>
    public required long LateSamples { get; init; }

    /// <summary>Instances in the last tick that resolved to no app, and whose numbers were therefore dropped.</summary>
    public required int UnattributedInstances { get; init; }

    /// <summary>
    /// Exiting instances whose last sensor bytes had no surviving instance of the same app to be charged to.
    /// </summary>
    public required int ExitResidueDropped { get; init; }

    /// <summary>ETW events whose PID matched no known instance — the window before the poller catches up.</summary>
    public required long UnattributedEvents { get; init; }

    /// <summary>Handlers that threw. The event was dropped rather than re-thrown into a provider's loop.</summary>
    public required long HandlerErrors { get; init; }

    /// <summary>Events the ETW sessions reported losing, cumulative since the session started.</summary>
    public required long EventsLost { get; init; }

    /// <summary>Address-to-hostname mappings currently held.</summary>
    public required int DnsEntries { get; init; }

    /// <summary>Mappings evicted by the DNS map's LRU cap.</summary>
    public required long DnsEvicted { get; init; }

    /// <summary>Every supervised sensor, by name.</summary>
    public required IReadOnlyList<SensorReport> Sensors { get; init; }

    /// <summary>Sensors that threw while starting or stopping, as "name: ExceptionType". Never a message.</summary>
    public required IReadOnlyList<string> FailedSensors { get; init; }

    /// <summary>
    /// True when at least one supervised sensor cannot run here. Drives <c>HelloAck.mode</c>, which is
    /// <c>Full</c> or <c>Degraded</c> — never <c>Lite</c>, because Lite means no Agent answered at all.
    /// </summary>
    public bool Degraded => Sensors.Any(s => s.Health.State == SensorState.Unavailable) || FailedSensors.Count > 0;
}
