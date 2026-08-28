using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace AppLedger.Ipc;

/// <summary>
/// One frame's scalars, plus where its payload sits inside the frame's bytes.
/// </summary>
/// <param name="Type">The <c>t</c> field.</param>
/// <param name="Id">Sender-assigned sequence number.</param>
/// <param name="ReplyTo">The <c>re</c> field: which request this answers, or 0.</param>
/// <param name="PayloadStart">Offset of <c>p</c> within the frame.</param>
/// <param name="PayloadLength">Length of <c>p</c>, or 0 when the frame carried none.</param>
public readonly record struct IpcHeader(
    MessageType Type,
    long Id,
    long ReplyTo,
    int PayloadStart,
    int PayloadLength)
{
    /// <summary>True when there is a <c>p</c> to deserialize.</summary>
    public bool HasPayload => PayloadLength > 0;
}

/// <summary>
/// Reads and writes the envelope <c>{ "t", "id", "re", "p" }</c> of docs/07_IPC.md §Framing.
/// </summary>
/// <remarks>
/// <b>Why two passes rather than one polymorphic type.</b> <c>System.Text.Json</c>'s polymorphism requires the
/// discriminator to be the first property <i>inside</i> the polymorphic object; here <c>t</c> is a sibling of
/// <c>p</c>. Using it would change the wire format, which is not on the table.
/// <para>
/// <b>Why not buffer <c>p</c> as a <see cref="JsonElement"/>.</b> That needs either a <c>JsonDocument</c>
/// whose disposal has to be threaded through dispatch, or a <c>Clone()</c> that deep-copies the payload. At
/// 1 Hz across four clients, a 7 KB <c>AppsTick</c> makes that roughly 30 KB/s of garbage for information the
/// reader below gets for nothing: it records where <c>p</c> starts and how long it is, and the second pass
/// deserializes straight out of the frame's own bytes through source-generated metadata.
/// </para>
/// </remarks>
public static class IpcEnvelope
{
    /// <summary>
    /// Reads the envelope's scalars and locates its payload, without deserializing it.
    /// </summary>
    /// <param name="frame">The frame's bytes, exactly — no length prefix.</param>
    /// <param name="header">The scalars and the payload's extent.</param>
    /// <returns>False when the frame is not a well-formed envelope, or names a type we do not know.</returns>
    /// <remarks>
    /// <b>Malformed input is a return value here, not an exception.</b> These bytes arrive unbidden from a
    /// peer, and the reader throws on two separate classes of input that a peer can trivially produce:
    /// <see cref="Utf8JsonReader.Read"/> raises <see cref="JsonException"/> for anything unparseable — an
    /// empty frame, an unclosed brace, a stray byte — while the <c>TryGet*</c> family raises
    /// <see cref="InvalidOperationException"/> when the token is simply of another kind, so
    /// <c>TryGetInt64</c> on <c>"id":"three"</c> throws despite its name.
    /// <para>
    /// A <c>Try</c> method that let either escape would kill the connection loop on the first bad frame any
    /// same-user process cared to send — the same shape of fault as an unguarded ETW processing loop
    /// (docs/24_ADR.md §Findings, 2026-08-27), and found here the same way: by a test rather than by
    /// reading the code.
    /// </para>
    /// </remarks>
    public static bool TryReadHeader(ReadOnlySpan<byte> frame, out IpcHeader header)
    {
        try
        {
            return TryReadHeaderCore(frame, out header);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            header = default;
            return false;
        }
    }

    private static bool TryReadHeaderCore(ReadOnlySpan<byte> frame, out IpcHeader header)
    {
        header = default;

        var reader = new Utf8JsonReader(frame, isFinalBlock: true, state: default);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return false;
        }

        var type = MessageType.Unknown;
        long id = 0;
        long replyTo = 0;
        var payloadStart = 0;
        var payloadLength = 0;

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("t"u8))
            {
                if (!reader.Read() || reader.TokenType != JsonTokenType.String || !MessageTypes.TryRead(ref reader, out type))
                {
                    return false;
                }
            }
            else if (reader.ValueTextEquals("id"u8))
            {
                if (!reader.Read() || !reader.TryGetInt64(out id))
                {
                    return false;
                }
            }
            else if (reader.ValueTextEquals("re"u8))
            {
                if (!reader.Read() || !reader.TryGetInt64(out replyTo))
                {
                    return false;
                }
            }
            else if (reader.ValueTextEquals("p"u8))
            {
                if (!reader.Read())
                {
                    return false;
                }

                payloadStart = (int)reader.TokenStartIndex;
                reader.Skip();
                payloadLength = (int)reader.BytesConsumed - payloadStart;
            }
            else
            {
                // An additive field from a newer build. docs/07 §Versioning makes these legal within v1, so
                // skipping is the specified behaviour rather than leniency.
                if (!reader.Read())
                {
                    return false;
                }

                reader.Skip();
            }
        }

        header = new IpcHeader(type, id, replyTo, payloadStart, payloadLength);
        return type != MessageType.Unknown;
    }

    /// <summary>
    /// Deserializes the payload a header located, out of the frame's own bytes.
    /// </summary>
    /// <returns>
    /// False when there is no payload or it does not fit the type. A malformed payload is a
    /// <see cref="IpcErrorCode.BadRequest"/> to answer, never an exception to let out — see the remarks on
    /// <see cref="TryReadHeader"/>.
    /// </returns>
    public static bool TryReadPayload<T>(
        ReadOnlySpan<byte> frame,
        in IpcHeader header,
        JsonTypeInfo<T> typeInfo,
        out T? payload)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);

        payload = default;
        if (!header.HasPayload)
        {
            return false;
        }

        try
        {
            var reader = new Utf8JsonReader(
                frame.Slice(header.PayloadStart, header.PayloadLength),
                IpcJson.ReaderOptions);

            payload = JsonSerializer.Deserialize(ref reader, typeInfo);
            return payload is not null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            payload = default;
            return false;
        }
    }

    /// <summary>Writes one envelope with a typed payload.</summary>
    /// <param name="writer">A writer positioned at the start of the frame.</param>
    /// <param name="type">The message name.</param>
    /// <param name="id">This sender's sequence number.</param>
    /// <param name="replyTo">The request being answered, or null for an unsolicited frame.</param>
    /// <param name="payload">The payload.</param>
    /// <param name="typeInfo">Source-generated metadata for the payload's type.</param>
    public static void Write<T>(
        Utf8JsonWriter writer,
        MessageType type,
        long id,
        long? replyTo,
        T payload,
        JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(typeInfo);

        WriteStart(writer, type, id, replyTo);
        writer.WritePropertyName("p"u8);
        JsonSerializer.Serialize(writer, payload, typeInfo);
        writer.WriteEndObject();
        writer.Flush();
    }

    /// <summary>Writes an envelope whose payload the caller emits itself, for the hand-written codecs.</summary>
    /// <remarks>
    /// <c>AppsTick</c> is the reason this exists: it is a table of heterogeneous cells on the 1 Hz path, and
    /// a DTO for it would box every number in every row.
    /// </remarks>
    public static void WriteStart(Utf8JsonWriter writer, MessageType type, long id, long? replyTo)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartObject();
        writer.WriteString("t"u8, MessageTypes.Utf8(type));
        writer.WriteNumber("id"u8, id);

        if (replyTo is { } value)
        {
            writer.WriteNumber("re"u8, value);
        }
    }

    /// <summary>Closes an envelope opened with <see cref="WriteStart"/> and flushes it.</summary>
    public static void WriteEnd(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteEndObject();
        writer.Flush();
    }
}
