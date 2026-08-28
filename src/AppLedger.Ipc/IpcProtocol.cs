namespace AppLedger.Ipc;

/// <summary>
/// The constants both processes have to agree on before anything else can be said (docs/07_IPC.md).
/// </summary>
public static class IpcProtocol
{
    /// <summary>
    /// The wire protocol version carried in <c>Hello</c>/<c>HelloAck</c>.
    /// </summary>
    /// <remarks>
    /// Additive fields inside a payload, and additive members of <see cref="MessageType"/>, are compatible
    /// within a version — <c>t</c> is a string on the wire, so a name an older build has never heard of is
    /// simply a name it rejects. Removing or repurposing a field is what bumps this, and a bump also changes
    /// <see cref="PipeName"/>, so an old client cannot even connect to a new Agent by accident.
    /// </remarks>
    public const int Version = 1;

    /// <summary>The pipe both sides use. The version is part of the name, deliberately.</summary>
    public const string PipeName = @"\\.\pipe\AppLedger.v1";

    /// <summary>The name without the <c>\\.\pipe\</c> prefix, which is what the BCL pipe types take.</summary>
    public const string PipeLocalName = "AppLedger.v1";

    /// <summary>
    /// How many clients the server accepts at once. Four is the documented figure, and it is a resource the
    /// user shares with themselves: a wedged client holds one of these until it is disconnected.
    /// </summary>
    public const int MaxServerInstances = 4;

    /// <summary>
    /// The largest frame a <b>client</b> will accept from the server. Ticks for every app on a busy machine
    /// are the only thing that approaches it.
    /// </summary>
    public const int MaxInboundFrameBytes = 4 * 1024 * 1024;

    /// <summary>
    /// The largest frame the <b>server</b> will accept from a client.
    /// </summary>
    /// <remarks>
    /// Deliberately 64x smaller than the outbound cap. No legitimate UI request comes close — the largest is
    /// <c>ResolvePath</c>, carrying one path — and the asymmetry cuts by the same factor the memory a hostile
    /// same-user process can make an <i>elevated</i> Agent commit before the frame is even parsed.
    /// </remarks>
    public const int MaxRequestFrameBytes = 64 * 1024;

    /// <summary>How often the UI sends <c>Ping</c>.</summary>
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(5);

    /// <summary>Missed <c>Pong</c>s before the UI reconnects.</summary>
    public const int MissedPongsBeforeReconnect = 3;

    /// <summary>Cadence of the <c>health</c> stream.</summary>
    public static readonly TimeSpan HealthTickInterval = TimeSpan.FromSeconds(10);

    /// <summary>Optional features this build implements, sent in <c>HelloAck.capabilities</c>.</summary>
    /// <remarks>
    /// A client must treat an absent capability as absent, never as present-but-false. This is how the UI
    /// greys a feature out at connect time rather than by sending a request that is certain to fail.
    /// </remarks>
    public static class Capabilities
    {
        /// <summary>Per-connection RTT and retransmit counts (`SetPerTcpConnectionEStats`).</summary>
        public const string EStats = "estats";

        /// <summary>Incremental disk scanning driven by the USN journal.</summary>
        public const string Usn = "usn";

        /// <summary>An offline GeoIP database is present.</summary>
        public const string GeoIp = "geoip";
    }
}

/// <summary>
/// Why a frame was refused (docs/07_IPC.md §Errors). The set is closed: a code the peer does not know is
/// itself a protocol mismatch.
/// </summary>
public enum IpcErrorCode
{
    /// <summary>The peer speaks a protocol version this build cannot.</summary>
    ProtocolUnsupported,

    /// <summary>The declared frame length exceeded the cap. The connection is closed afterwards.</summary>
    FrameTooLarge,

    /// <summary>Malformed, unparseable, or a request this build does not implement.</summary>
    BadRequest,

    /// <summary>
    /// <c>PolicyGuard</c> refused a path. The detail carries the tier and a generic reason, never the
    /// canonical path of a Tier-0 or Tier-1 target — that would make the Agent an oracle for what it
    /// considers sensitive.
    /// </summary>
    PolicyDenied,

    /// <summary>The app, host or record asked for does not exist.</summary>
    NotFound,

    /// <summary>The sensor that would answer this is not running here.</summary>
    SensorUnavailable,

    /// <summary>The Agent is at capacity, or already doing this.</summary>
    Busy,

    /// <summary>Anything unexpected. Never carries an exception message to the UI.</summary>
    Internal,
}
