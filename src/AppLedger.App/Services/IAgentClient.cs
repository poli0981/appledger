using AppLedger.Ipc;
using AppLedger.Ipc.Streams;

namespace AppLedger.App.Services;

/// <summary>
/// Where the UI's numbers come from, and how completely.
/// </summary>
/// <remarks>
/// Three values, and the third is the one the wire cannot carry. <c>HelloAck.mode</c> is
/// <c>Full | Degraded</c> because <b>Lite means no Agent answered</b> — there is nobody to send a mode
/// (docs/07_IPC.md §Handshake). The UI synthesizes it from its own connection state, which is why this enum
/// lives here rather than in <c>AppLedger.Ipc</c>, and is named for the connection rather than for the
/// Agent - <c>AgentMode</c> is already the two-valued thing on the wire.
/// </remarks>
public enum ConnectionMode
{
    /// <summary>Connecting, or reconnecting after a drop.</summary>
    Connecting,

    /// <summary>An Agent answered and every sensor is running.</summary>
    Full,

    /// <summary>An Agent answered and at least one sensor cannot run here.</summary>
    Degraded,

    /// <summary>
    /// No Agent answered. The collector runs in this process with what a standard user can see: no ETW, so
    /// no network bytes, no real device I/O and no per-process DNS, and nothing is persisted
    /// (docs/01_ARCHITECTURE.md §Lite mode).
    /// </summary>
    Lite,
}

/// <summary>What the UI knows about the Agent right now.</summary>
/// <param name="Mode">Full, Degraded, Lite, or still connecting.</param>
/// <param name="AgentVersion">The Agent's version when one answered.</param>
/// <param name="Sensors">Sensor states by <c>ISensor.Name</c>, empty in Lite mode until the first tick.</param>
/// <param name="TaskInstalled">
/// Whether the Scheduled Task exists. Distinguishes "offer Agent setup" from "offer to start the task", which
/// are different buttons (docs/16 §Agent CLI exit codes).
/// </param>
public sealed record AgentStatus(
    ConnectionMode Mode,
    string? AgentVersion,
    IReadOnlyDictionary<string, SensorStatePayload> Sensors,
    bool TaskInstalled)
{
    /// <summary>The state before anything has been tried.</summary>
    public static AgentStatus Connecting { get; } =
        new(ConnectionMode.Connecting, null, new Dictionary<string, SensorStatePayload>(), TaskInstalled: false);
}

/// <summary>
/// The UI's single source of live data, whether it comes over the pipe or from the collector hosted in this
/// process (docs/22_WPFUI_SYNTAX.md §Bootstrap registers this as <c>IAgentClient</c>).
/// </summary>
/// <remarks>
/// One seam for both, deliberately: a view-model that has to know whether it is in Lite mode in order to read
/// a number would grow that branch in every page. What differs between the two is <i>which</i> numbers are
/// real, and that is carried by <see cref="AgentStatus"/> and by the sensor states — not by a different API.
/// </remarks>
public interface IAgentClient : IAsyncDisposable
{
    /// <summary>Raised on the UI thread when the connection state changes.</summary>
    event Action<AgentStatus>? StatusChanged;

    /// <summary>Raised on the UI thread with the most recent tick, coalesced.</summary>
    event Action<IReadOnlyList<AppRow>>? AppsTick;

    /// <summary>Raised on the UI thread with the Agent's health, every ten seconds.</summary>
    event Action<HealthPayload>? HealthTick;

    /// <summary>The current state.</summary>
    AgentStatus Status { get; }

    /// <summary>Connects, or falls back to Lite mode, and starts producing ticks.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);
}
