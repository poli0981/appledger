using System.Text.Json;

namespace AppLedger.Ipc;

/// <summary>
/// Every message name in protocol v1 (docs/07_IPC.md §Message catalog).
/// </summary>
/// <remarks>
/// The whole catalog is listed even though v0.2 implements a third of it, and that is a deliberate choice
/// rather than optimism. Because <c>t</c> is a string on the wire, a name is never a compatibility concern —
/// but the distinction between "your build is too old" and "this Agent does not do that yet" is, and it is
/// one the UI has to draw: the first shows *Update required*, the second greys out a button. A known name
/// with no handler answers <see cref="IpcErrorCode.BadRequest"/>; an unknown one means the peer is a build
/// we do not recognise at all.
/// <para>
/// Payload types, by contrast, exist only for what is implemented — every extra <c>[JsonSerializable]</c>
/// is generated code for something nobody can construct.
/// </para>
/// </remarks>
public enum MessageType
{
    /// <summary>Not a name this build knows.</summary>
    Unknown = 0,

    // Handshake and keep-alive.
    Hello,
    HelloAck,
    Ping,
    Pong,

    // Streams.
    Subscribe,
    Unsubscribe,
    AppsTick,
    AppTick,
    ConnectionsTick,
    HealthTick,
    Event,

    // Requests and their answers.
    GetAppDetail,
    AppDetail,
    GetInstalledApps,
    InstalledApps,
    ResolvePath,
    ResolvedPath,
    ResolveWindow,
    ResolvedWindow,
    ResolveHost,
    HostRecords,
    ScanNow,
    ScanAccepted,
    ScanProgress,
    ScanDone,
    SamplingHint,
    OverridesChanged,
    ApplyOverrideToHistory,
    Pause,
    Resume,
    Purge,
    PurgeDone,
    UpdateCatalog,
    CatalogResult,
    GetHealth,
    Health,
    Shutdown,

    // Generic answers.
    Ack,
    Error,
}

/// <summary>
/// Converts <see cref="MessageType"/> to and from its wire spelling without allocating or reflecting.
/// </summary>
/// <remarks>
/// A dictionary keyed by <c>string</c> would mean materialising the token as a string on every frame, at
/// 1 Hz per client, for a value that is thrown away immediately. <c>ValueTextEquals</c> compares against a
/// UTF-8 literal in place — escapes handled, no allocation — and the literals are compile-time constants,
/// so the whole table is a chain of vectorized span comparisons.
/// </remarks>
public static class MessageTypes
{
    /// <summary>Reads the value of a JSON string token as a message type.</summary>
    /// <returns>False when the name is not one this build knows.</returns>
    public static bool TryRead(ref Utf8JsonReader reader, out MessageType type)
    {
        // Ordered by how often each is seen on the wire: ticks and keep-alive dominate by orders of
        // magnitude, and everything else happens when a human clicks something.
        if (reader.ValueTextEquals("AppsTick"u8)) { type = MessageType.AppsTick; return true; }
        if (reader.ValueTextEquals("Ping"u8)) { type = MessageType.Ping; return true; }
        if (reader.ValueTextEquals("Pong"u8)) { type = MessageType.Pong; return true; }
        if (reader.ValueTextEquals("AppTick"u8)) { type = MessageType.AppTick; return true; }
        if (reader.ValueTextEquals("HealthTick"u8)) { type = MessageType.HealthTick; return true; }
        if (reader.ValueTextEquals("ConnectionsTick"u8)) { type = MessageType.ConnectionsTick; return true; }
        if (reader.ValueTextEquals("Event"u8)) { type = MessageType.Event; return true; }

        if (reader.ValueTextEquals("Hello"u8)) { type = MessageType.Hello; return true; }
        if (reader.ValueTextEquals("HelloAck"u8)) { type = MessageType.HelloAck; return true; }
        if (reader.ValueTextEquals("Subscribe"u8)) { type = MessageType.Subscribe; return true; }
        if (reader.ValueTextEquals("Unsubscribe"u8)) { type = MessageType.Unsubscribe; return true; }
        if (reader.ValueTextEquals("Ack"u8)) { type = MessageType.Ack; return true; }
        if (reader.ValueTextEquals("Error"u8)) { type = MessageType.Error; return true; }

        if (reader.ValueTextEquals("GetHealth"u8)) { type = MessageType.GetHealth; return true; }
        if (reader.ValueTextEquals("Health"u8)) { type = MessageType.Health; return true; }
        if (reader.ValueTextEquals("Pause"u8)) { type = MessageType.Pause; return true; }
        if (reader.ValueTextEquals("Resume"u8)) { type = MessageType.Resume; return true; }
        if (reader.ValueTextEquals("Shutdown"u8)) { type = MessageType.Shutdown; return true; }

        if (reader.ValueTextEquals("GetAppDetail"u8)) { type = MessageType.GetAppDetail; return true; }
        if (reader.ValueTextEquals("AppDetail"u8)) { type = MessageType.AppDetail; return true; }
        if (reader.ValueTextEquals("GetInstalledApps"u8)) { type = MessageType.GetInstalledApps; return true; }
        if (reader.ValueTextEquals("InstalledApps"u8)) { type = MessageType.InstalledApps; return true; }
        if (reader.ValueTextEquals("ResolvePath"u8)) { type = MessageType.ResolvePath; return true; }
        if (reader.ValueTextEquals("ResolvedPath"u8)) { type = MessageType.ResolvedPath; return true; }
        if (reader.ValueTextEquals("ResolveWindow"u8)) { type = MessageType.ResolveWindow; return true; }
        if (reader.ValueTextEquals("ResolvedWindow"u8)) { type = MessageType.ResolvedWindow; return true; }
        if (reader.ValueTextEquals("ResolveHost"u8)) { type = MessageType.ResolveHost; return true; }
        if (reader.ValueTextEquals("HostRecords"u8)) { type = MessageType.HostRecords; return true; }
        if (reader.ValueTextEquals("ScanNow"u8)) { type = MessageType.ScanNow; return true; }
        if (reader.ValueTextEquals("ScanAccepted"u8)) { type = MessageType.ScanAccepted; return true; }
        if (reader.ValueTextEquals("ScanProgress"u8)) { type = MessageType.ScanProgress; return true; }
        if (reader.ValueTextEquals("ScanDone"u8)) { type = MessageType.ScanDone; return true; }
        if (reader.ValueTextEquals("SamplingHint"u8)) { type = MessageType.SamplingHint; return true; }
        if (reader.ValueTextEquals("OverridesChanged"u8)) { type = MessageType.OverridesChanged; return true; }
        if (reader.ValueTextEquals("ApplyOverrideToHistory"u8)) { type = MessageType.ApplyOverrideToHistory; return true; }
        if (reader.ValueTextEquals("Purge"u8)) { type = MessageType.Purge; return true; }
        if (reader.ValueTextEquals("PurgeDone"u8)) { type = MessageType.PurgeDone; return true; }
        if (reader.ValueTextEquals("UpdateCatalog"u8)) { type = MessageType.UpdateCatalog; return true; }
        if (reader.ValueTextEquals("CatalogResult"u8)) { type = MessageType.CatalogResult; return true; }

        type = MessageType.Unknown;
        return false;
    }

    /// <summary>The wire spelling, as UTF-8 bytes the writer can emit directly.</summary>
    public static ReadOnlySpan<byte> Utf8(MessageType type) => type switch
    {
        MessageType.Hello => "Hello"u8,
        MessageType.HelloAck => "HelloAck"u8,
        MessageType.Ping => "Ping"u8,
        MessageType.Pong => "Pong"u8,
        MessageType.Subscribe => "Subscribe"u8,
        MessageType.Unsubscribe => "Unsubscribe"u8,
        MessageType.AppsTick => "AppsTick"u8,
        MessageType.AppTick => "AppTick"u8,
        MessageType.ConnectionsTick => "ConnectionsTick"u8,
        MessageType.HealthTick => "HealthTick"u8,
        MessageType.Event => "Event"u8,
        MessageType.GetAppDetail => "GetAppDetail"u8,
        MessageType.AppDetail => "AppDetail"u8,
        MessageType.GetInstalledApps => "GetInstalledApps"u8,
        MessageType.InstalledApps => "InstalledApps"u8,
        MessageType.ResolvePath => "ResolvePath"u8,
        MessageType.ResolvedPath => "ResolvedPath"u8,
        MessageType.ResolveWindow => "ResolveWindow"u8,
        MessageType.ResolvedWindow => "ResolvedWindow"u8,
        MessageType.ResolveHost => "ResolveHost"u8,
        MessageType.HostRecords => "HostRecords"u8,
        MessageType.ScanNow => "ScanNow"u8,
        MessageType.ScanAccepted => "ScanAccepted"u8,
        MessageType.ScanProgress => "ScanProgress"u8,
        MessageType.ScanDone => "ScanDone"u8,
        MessageType.SamplingHint => "SamplingHint"u8,
        MessageType.OverridesChanged => "OverridesChanged"u8,
        MessageType.ApplyOverrideToHistory => "ApplyOverrideToHistory"u8,
        MessageType.Pause => "Pause"u8,
        MessageType.Resume => "Resume"u8,
        MessageType.Purge => "Purge"u8,
        MessageType.PurgeDone => "PurgeDone"u8,
        MessageType.UpdateCatalog => "UpdateCatalog"u8,
        MessageType.CatalogResult => "CatalogResult"u8,
        MessageType.GetHealth => "GetHealth"u8,
        MessageType.Health => "Health"u8,
        MessageType.Shutdown => "Shutdown"u8,
        MessageType.Ack => "Ack"u8,
        MessageType.Error => "Error"u8,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown is not a wire value."),
    };
}
