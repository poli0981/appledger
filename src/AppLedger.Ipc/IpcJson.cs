using System.Text.Json;
using System.Text.Json.Serialization;

namespace AppLedger.Ipc;

/// <summary>
/// Source-generated metadata for every payload this build can construct (docs/07_IPC.md §opening).
/// </summary>
/// <remarks>
/// Only the implemented subset is listed, deliberately. Every extra <c>[JsonSerializable]</c> is generated
/// code, assembly size and trim surface for a type nobody can build — and the v1 catalog is three times what
/// v0.2 answers. <see cref="MessageType"/> carries all the names, because a name costs nothing and telling
/// "your build is too old" apart from "this Agent does not do that yet" is worth something; a payload type
/// costs more than nothing.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(HelloPayload))]
[JsonSerializable(typeof(HelloAckPayload))]
[JsonSerializable(typeof(PongPayload))]
[JsonSerializable(typeof(SubscribePayload))]
[JsonSerializable(typeof(HealthPayload))]
[JsonSerializable(typeof(PausePayload))]
[JsonSerializable(typeof(AckPayload))]
[JsonSerializable(typeof(ShutdownPayload))]
[JsonSerializable(typeof(ErrorPayload))]
public sealed partial class IpcJsonContext : JsonSerializerContext;

/// <summary>Shared JSON settings, so the two processes cannot drift.</summary>
public static class IpcJson
{
    /// <summary>
    /// Reader options. Comments and trailing commas are rejected: this is a machine protocol, and every
    /// leniency is a place where two implementations can disagree about what a frame meant.
    /// </summary>
    public static JsonReaderOptions ReaderOptions { get; } = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 32,
    };
}
