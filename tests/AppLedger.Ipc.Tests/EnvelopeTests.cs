using System.Text;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace AppLedger.Ipc.Tests;

/// <summary>
/// The two-pass envelope read. The first pass locates <c>p</c> without materialising it; the second
/// deserializes it out of the frame's own bytes through source-generated metadata.
/// </summary>
public sealed class EnvelopeTests
{
    private static byte[] Utf8(string json) => Encoding.UTF8.GetBytes(json);

    [Fact]
    public void TryReadHeader_WellFormedEnvelope_ReadsTheScalarsAndLocatesThePayload()
    {
        var frame = Utf8("""{"t":"Hello","id":3,"re":2,"p":{"protocol":1,"client":"x","lang":"vi"}}""");

        IpcEnvelope.TryReadHeader(frame, out var header).ShouldBeTrue();

        header.Type.ShouldBe(MessageType.Hello);
        header.Id.ShouldBe(3);
        header.ReplyTo.ShouldBe(2);
        header.HasPayload.ShouldBeTrue();

        Encoding.UTF8.GetString(frame.AsSpan(header.PayloadStart, header.PayloadLength))
            .ShouldBe("""{"protocol":1,"client":"x","lang":"vi"}""");
    }

    [Fact]
    public void TryReadHeader_NoPayload_HasNone()
    {
        IpcEnvelope.TryReadHeader(Utf8("""{"t":"Ping","id":9}"""), out var header).ShouldBeTrue();

        header.Type.ShouldBe(MessageType.Ping);
        header.HasPayload.ShouldBeFalse();
        header.ReplyTo.ShouldBe(0);
    }

    /// <summary>
    /// docs/07 §Versioning makes additive fields legal within v1, so skipping an unknown one is the
    /// specified behaviour rather than leniency — and the payload after it must still be found.
    /// </summary>
    [Fact]
    public void TryReadHeader_UnknownFieldBeforeThePayload_IsSkippedAndThePayloadIsStillLocated()
    {
        var frame = Utf8("""{"t":"Ping","future":{"a":[1,2,{"b":3}]},"id":4,"p":{"serverTimeUtc":7}}""");

        IpcEnvelope.TryReadHeader(frame, out var header).ShouldBeTrue();
        header.Id.ShouldBe(4);

        IpcEnvelope.TryReadPayload(frame, header, IpcJsonContext.Default.PongPayload, out var payload).ShouldBeTrue();
        payload!.ServerTimeUtc.ShouldBe(7);
    }

    /// <summary>A nested payload must be skipped to its own end, not to the first closing brace.</summary>
    [Fact]
    public void TryReadHeader_DeeplyNestedPayload_SpansExactlyTheWholeSubtree()
    {
        var frame = Utf8("""{"t":"Ping","id":1,"p":{"a":{"b":{"c":[1,{"d":2}]}}},"re":5}""");

        IpcEnvelope.TryReadHeader(frame, out var header).ShouldBeTrue();

        header.ReplyTo.ShouldBe(5);
        Encoding.UTF8.GetString(frame.AsSpan(header.PayloadStart, header.PayloadLength))
            .ShouldBe("""{"a":{"b":{"c":[1,{"d":2}]}}}""");
    }

    /// <summary>
    /// An unknown name means the peer is a build we do not recognise. That is a different thing from a known
    /// name with no handler, which answers BadRequest — the UI shows "Update required" for one and greys a
    /// button out for the other.
    /// </summary>
    [Fact]
    public void TryReadHeader_UnknownMessageName_IsRejected() =>
        IpcEnvelope.TryReadHeader(Utf8("""{"t":"Teleport","id":1}"""), out _).ShouldBeFalse();

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("""{"id":1}""")]
    [InlineData("null")]
    public void TryReadHeader_NotAnEnvelope_IsRejected(string json) =>
        IpcEnvelope.TryReadHeader(Utf8(json), out _).ShouldBeFalse();

    [Fact]
    public void TryReadPayload_HeaderWithNoPayload_IsFalse()
    {
        var frame = Utf8("""{"t":"Ping","id":1}""");
        IpcEnvelope.TryReadHeader(frame, out var header).ShouldBeTrue();

        IpcEnvelope.TryReadPayload(frame, header, IpcJsonContext.Default.PongPayload, out var payload).ShouldBeFalse();
        payload.ShouldBeNull();
    }

    /// <summary>Every name has to survive the round trip, or a message becomes unroutable in one direction.</summary>
    [Fact]
    public void MessageTypes_EveryName_RoundTripsThroughItsWireSpelling()
    {
        foreach (var type in Enum.GetValues<MessageType>())
        {
            if (type == MessageType.Unknown)
            {
                continue;
            }

            var frame = Utf8($$"""{"t":"{{Encoding.UTF8.GetString(MessageTypes.Utf8(type))}}","id":1}""");

            IpcEnvelope.TryReadHeader(frame, out var header).ShouldBeTrue($"{type} should be readable");
            header.Type.ShouldBe(type);
        }
    }

    [Fact]
    public void MessageTypes_Utf8OfUnknown_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() => MessageTypes.Utf8(MessageType.Unknown).ToArray());

    /// <summary>
    /// The names are the wire contract, so they are pinned rather than derived from the enum's spelling: a
    /// rename that looks like a refactor would otherwise be a silent protocol break.
    /// </summary>
    [Theory]
    [InlineData(MessageType.Hello, "Hello")]
    [InlineData(MessageType.HelloAck, "HelloAck")]
    [InlineData(MessageType.AppsTick, "AppsTick")]
    [InlineData(MessageType.HealthTick, "HealthTick")]
    [InlineData(MessageType.GetHealth, "GetHealth")]
    [InlineData(MessageType.Ack, "Ack")]
    [InlineData(MessageType.Error, "Error")]
    public void MessageTypes_WireSpelling_IsTheDocumentedOne(MessageType type, string expected) =>
        Encoding.UTF8.GetString(MessageTypes.Utf8(type)).ShouldBe(expected);

    // -- payload serialization ---------------------------------------------------------------------------

    private static string Serialize<T>(MessageType type, T payload, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
    {
        var frame = Framing.FrameWriter.Prepare(json => IpcEnvelope.Write(json, type, 1, null, payload, info));
        return Encoding.UTF8.GetString(frame.AsSpan(4));
    }

    [Fact]
    public void Write_Payload_UsesCamelCaseAndOmitsNulls()
    {
        var json = Serialize(MessageType.Ack, new AckPayload { PausedUntilUtc = 1_700_000_060 },
            IpcJsonContext.Default.AckPayload);

        json.ShouldBe("""{"t":"Ack","id":1,"p":{"pausedUntilUtc":1700000060}}""");
        json.ShouldNotContain("rowsRekeyed");
    }

    /// <summary>
    /// The mode is two-valued on the wire and spelled, not numbered. Lite is absent by design: it means no
    /// Agent answered, so there is no HelloAck to carry it.
    /// </summary>
    [Fact]
    public void Write_HelloAck_SpellsTheModeAndKeepsSensorNamesVerbatim()
    {
        var payload = new HelloAckPayload(
            Protocol: 1,
            Agent: "0.2.0",
            Mode: AgentMode.Degraded,
            DbPath: "db",
            Schema: 1,
            Capabilities: ["estats"],
            Sensors: new Dictionary<string, SensorStatePayload>
            {
                ["EtwHub"] = new("Running"),
                ["GpuPoller"] = new("Unavailable", "NoCounters"),
            },
            Catalog: new CatalogInfoPayload("2026.08.0", Verified: true),
            StartedUtc: 1_700_000_000);

        var json = Serialize(MessageType.HelloAck, payload, IpcJsonContext.Default.HelloAckPayload);

        json.ShouldContain(""""mode":"Degraded"""");
        json.ShouldContain(""""EtwHub":{"state":"Running"}"""");
        json.ShouldContain("NoCounters");
        json.ShouldNotContain("Lite");
    }

    [Fact]
    public void Write_ErrorWithoutPolicyDetail_OmitsTierAndReason()
    {
        var json = Serialize(MessageType.Error, new ErrorPayload(IpcErrorCode.BadRequest, "unsupported"),
            IpcJsonContext.Default.ErrorPayload);

        json.ShouldContain("BadRequest");
        json.ShouldNotContain("tier");
        json.ShouldNotContain("reason");
    }

    [Fact]
    public void TryReadPayload_RoundTripsAnErrorThroughTheEnvelope()
    {
        var frame = Framing.FrameWriter.Prepare(json => IpcEnvelope.Write(
            json, MessageType.Error, 8, 7,
            new ErrorPayload(IpcErrorCode.PolicyDenied, "protected location", Tier: 0, Reason: "ProtectedOs"),
            IpcJsonContext.Default.ErrorPayload));

        var body = frame.AsSpan(4);
        IpcEnvelope.TryReadHeader(body, out var header).ShouldBeTrue();

        IpcEnvelope.TryReadPayload(body, header, IpcJsonContext.Default.ErrorPayload, out var payload).ShouldBeTrue();
        payload!.Code.ShouldBe(IpcErrorCode.PolicyDenied);
        payload.Tier.ShouldBe(0);
        payload.Reason.ShouldBe("ProtectedOs");
    }

    /// <summary>
    /// The parsers run on bytes chosen by whoever is on the other end of the pipe. Both
    /// <c>Utf8JsonReader.Read</c> and the <c>TryGet*</c> family throw on input that does not fit — and
    /// <c>TryGetInt64</c> raises <c>InvalidOperationException</c> for a string token, which its name does
    /// not suggest. Anything that escapes here takes the connection loop with it, the same way an unguarded
    /// ETW processing loop took its host (docs/24_ADR.md §Findings).
    /// </summary>
    [Fact]
    public void TryReadHeader_ArbitraryBytes_NeverThrows()
    {
        var valid = Utf8("""{"t":"Hello","id":3,"re":2,"p":{"protocol":1,"client":"x","lang":"vi"}}""");
        var random = new Random(20260828);

        for (var i = 0; i < 2_000; i++)
        {
            var frame = valid.ToArray();

            // Truncations, then single-byte corruptions: the two things a bad frame actually looks like.
            var length = random.Next(0, frame.Length + 1);
            var mutated = frame.AsSpan(0, length).ToArray();
            if (mutated.Length > 0 && random.Next(2) == 0)
            {
                mutated[random.Next(mutated.Length)] = (byte)random.Next(256);
            }

            // The assertion is that this line returns rather than throwing; the value is irrelevant.
            IpcEnvelope.TryReadHeader(mutated, out _);
        }
    }

    [Theory]
    [InlineData("""{"t":"Hello","id":"three"}""")]
    [InlineData("""{"t":"Hello","id":[1]}""")]
    [InlineData("""{"t":123,"id":1}""")]
    [InlineData("""{"t":"Hello","re":{}}""")]
    [InlineData("""{"t":"Hello","id":1,"p":}""")]
    public void TryReadHeader_WrongTypeForAKnownField_IsRejectedRatherThanThrown(string json) =>
        IpcEnvelope.TryReadHeader(Utf8(json), out _).ShouldBeFalse();

    [Fact]
    public void TryReadPayload_PayloadThatDoesNotFitTheType_IsRejectedRatherThanThrown()
    {
        var frame = Utf8("""{"t":"Pong","id":1,"p":{"serverTimeUtc":"tomorrow"}}""");
        IpcEnvelope.TryReadHeader(frame, out var header).ShouldBeTrue();

        IpcEnvelope.TryReadPayload(frame, header, IpcJsonContext.Default.PongPayload, out var payload)
            .ShouldBeFalse();
        payload.ShouldBeNull();
    }

    /// <summary>A machine protocol has no use for comments, and every leniency is a place to disagree.</summary>
    [Fact]
    public void ReaderOptions_RejectCommentsAndTrailingCommas()
    {
        IpcJson.ReaderOptions.CommentHandling.ShouldBe(JsonCommentHandling.Disallow);
        IpcJson.ReaderOptions.AllowTrailingCommas.ShouldBeFalse();
    }
}
