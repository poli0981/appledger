namespace AppLedger.Ipc;

/// <summary>
/// What the Agent is able to do right now (docs/07_IPC.md §Handshake).
/// </summary>
/// <remarks>
/// Two values, not three. <c>Lite</c> is the state in which <b>no Agent answered at all</b>, so there is no
/// <c>HelloAck</c> to carry it; the UI synthesizes that from its own connection state. A mode the Agent could
/// report would be a mode the Agent was alive to report.
/// </remarks>
public enum AgentMode
{
    /// <summary>Every sensor is running.</summary>
    Full,

    /// <summary>At least one sensor cannot run here. The numbers it would have produced are absent, not zero.</summary>
    Degraded,
}

/// <summary>UI → Agent, first frame on every connection.</summary>
/// <param name="Protocol">The highest protocol the client knows.</param>
/// <param name="Client">Product and version, for the Agent's log.</param>
/// <param name="Lang">The UI's culture, so Agent-side strings match what the user sees.</param>
public sealed record HelloPayload(int Protocol, string Client, string Lang);

/// <summary>One sensor's state on the wire.</summary>
/// <param name="State">
/// <c>Stopped</c>, <c>Starting</c>, <c>Running</c> or <c>Unavailable</c> — mirroring
/// <c>AppLedger.Core.Collection.SensorState</c> exactly, so a new sensor needs no protocol change.
/// </param>
/// <param name="Detail">
/// A short reason code for <c>Unavailable</c>, such as a Win32 error number. Never a path, a hostname or an
/// exception message: this reaches the UI and the log at Information level (docs/15_LOGGING.md §Redaction).
/// </param>
public sealed record SensorStatePayload(string State, string? Detail = null);

/// <summary>Which catalog the Agent loaded, and whether its signature verified.</summary>
public sealed record CatalogInfoPayload(string? Version, bool Verified);

/// <summary>Agent → UI, answering <c>Hello</c>.</summary>
/// <param name="Protocol">The protocol the Agent speaks. A mismatch is <c>Error(ProtocolUnsupported)</c>.</param>
/// <param name="Agent">The Agent's version.</param>
/// <param name="Mode">Full or Degraded.</param>
/// <param name="DbPath">Where history lives, so the UI can open it read-only itself.</param>
/// <param name="Schema">The database schema version the UI should expect.</param>
/// <param name="Capabilities">
/// Optional features this build implements. Absent means absent — never present-but-false — which is what
/// lets the UI grey a control out at connect time instead of sending a request certain to fail.
/// </param>
/// <param name="Sensors">Keyed by <c>ISensor.Name</c>, verbatim.</param>
/// <param name="Catalog">Catalog version and signature state.</param>
/// <param name="StartedUtc">When the Agent started, UTC epoch seconds.</param>
public sealed record HelloAckPayload(
    int Protocol,
    string Agent,
    AgentMode Mode,
    string DbPath,
    int Schema,
    IReadOnlyList<string> Capabilities,
    IReadOnlyDictionary<string, SensorStatePayload> Sensors,
    CatalogInfoPayload Catalog,
    long StartedUtc);

/// <summary>Agent → UI, answering <c>Ping</c>.</summary>
public sealed record PongPayload(long ServerTimeUtc);

/// <summary>UI → Agent. <c>AppId</c> is required for the per-app streams and ignored for the rest.</summary>
public sealed record SubscribePayload(string Stream, string? AppId = null);

/// <summary>
/// The Agent's own cost and the collector's quiet losses (docs/07_IPC.md §Streams, docs/15 §Agent self-watch).
/// </summary>
/// <remarks>
/// Everything after <see cref="BudgetOk"/> measures a place where the collector <i>knows</i> it missed
/// something. None of it is visible in the data — what each produces is a number that is merely a little too
/// small — so unreported they are indistinguishable from a quiet machine.
/// <para>
/// <see cref="AgentCpuPct"/> and <see cref="AgentWs"/> are facts about the hosting process rather than about
/// the collector, and the Agent merges its own reading in: in Lite mode the same library runs inside a WPF
/// UI whose working set says nothing about the cost of collecting.
/// </para>
/// </remarks>
public sealed record HealthPayload
{
    /// <summary>When this was measured, UTC epoch seconds.</summary>
    public required long Ts { get; init; }

    /// <summary>The Agent process's CPU, 0-100.</summary>
    public required double AgentCpuPct { get; init; }

    /// <summary>The Agent process's private working set, bytes.</summary>
    public required long AgentWs { get; init; }

    /// <summary>Events the ETW sessions have reported losing.</summary>
    public required long EventsLost { get; init; }

    /// <summary>Seconds of per-second detail currently in the ring.</summary>
    public required int RingSeconds { get; init; }

    /// <summary>Sensor states, keyed by <c>ISensor.Name</c>.</summary>
    public required IReadOnlyDictionary<string, SensorStatePayload> Sensors { get; init; }

    /// <summary>False once the Agent has been over budget for ten consecutive minutes.</summary>
    public required bool BudgetOk { get; init; }

    /// <summary>Apps that produced a sample in the most recent tick.</summary>
    public int LiveApps { get; init; }

    /// <summary>Process instances the registry knows.</summary>
    public int LiveInstances { get; init; }

    /// <summary>Rows written to history this session.</summary>
    public long RowsWritten { get; init; }

    /// <summary>Live ticks dropped because a subscriber fell behind.</summary>
    public long LiveDropped { get; init; }

    /// <summary>Samples that arrived for an already-written minute. Non-zero means the clock stepped back.</summary>
    public long LateSamples { get; init; }

    /// <summary>Instances in the last tick that resolved to no app.</summary>
    public int UnattributedInstances { get; init; }

    /// <summary>ETW events whose PID matched no known instance.</summary>
    public long UnattributedEvents { get; init; }

    /// <summary>Handlers that threw. The event was dropped rather than re-thrown into a provider's loop.</summary>
    public long HandlerErrors { get; init; }

    /// <summary>True when the Agent is paused, and until when.</summary>
    public long? PausedUntilUtc { get; init; }
}

/// <summary>UI → Agent. Absent minutes means "until resumed".</summary>
public sealed record PausePayload(int? Minutes = null);

/// <summary>
/// The generic acknowledgement. Its optional fields accrete as more commands are implemented, which is a
/// mild smell in the spec — but docs/07 §Versioning sanctions additive optional fields explicitly, and
/// diverging from the documented shape to avoid it would be the worse trade.
/// </summary>
public sealed record AckPayload
{
    /// <summary>Set by <c>Pause</c> and <c>Resume</c>.</summary>
    public long? PausedUntilUtc { get; init; }

    /// <summary>Set by <c>ApplyOverrideToHistory</c>.</summary>
    public long? RowsRekeyed { get; init; }
}

/// <summary>UI → Agent. The reason distinguishes an update restart from a user stopping collection.</summary>
public sealed record ShutdownPayload(string Reason);

/// <summary>Either side. The message is for a log; the UI renders its own text from the code.</summary>
/// <param name="Code">One of <see cref="IpcErrorCode"/>.</param>
/// <param name="Message">A short diagnostic. Never an exception message, a path or a hostname.</param>
/// <param name="Tier">Set for <c>PolicyDenied</c>: the tier that refused.</param>
/// <param name="Reason">
/// Set for <c>PolicyDenied</c>: a generic code such as <c>ProtectedOs</c>. Never the matched rule or the
/// canonical path — that would make the Agent an oracle for what it treats as sensitive
/// (docs/11_SAFETY_POLICY.md §Path tiers).
/// </param>
public sealed record ErrorPayload(
    IpcErrorCode Code,
    string Message,
    int? Tier = null,
    string? Reason = null);
