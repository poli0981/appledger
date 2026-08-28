using System.Buffers.Binary;
using System.Text;
using AppLedger.Ipc;
using AppLedger.Ipc.Framing;
using Shouldly;
using Xunit;

namespace AppLedger.Ipc.Tests;

/// <summary>
/// The length-prefixed framing of docs/07_IPC.md. Everything here runs over a <see cref="MemoryStream"/>,
/// which is the payoff of writing the layer against <see cref="Stream"/> rather than a pipe.
/// </summary>
public sealed class FramingTests
{
    private static MemoryStream Framed(params string[] frames)
    {
        var stream = new MemoryStream();
        foreach (var frame in frames)
        {
            var body = Encoding.UTF8.GetBytes(frame);
            var prefix = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(prefix, (uint)body.Length);
            stream.Write(prefix);
            stream.Write(body);
        }

        stream.Position = 0;
        return stream;
    }

    private static MemoryStream WithDeclaredLength(uint declared, int actualBodyBytes = 0)
    {
        var stream = new MemoryStream();
        var prefix = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, declared);
        stream.Write(prefix);
        stream.Write(new byte[actualBodyBytes]);
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public async Task ReadAsync_LengthPrefixedFrame_ReturnsThePayload()
    {
        using var stream = Framed("""{"t":"Ping","id":1}""");
        using var reader = new FrameReader(stream);

        (await reader.ReadAsync()).ShouldBe(FrameStatus.Frame);
        Encoding.UTF8.GetString(reader.Payload).ShouldBe("""{"t":"Ping","id":1}""");
    }

    [Fact]
    public async Task ReadAsync_SeveralFrames_AreReadInOrder()
    {
        using var stream = Framed("""{"id":1}""", """{"id":2}""", """{"id":3}""");
        using var reader = new FrameReader(stream);

        for (var i = 1; i <= 3; i++)
        {
            (await reader.ReadAsync()).ShouldBe(FrameStatus.Frame);
            Encoding.UTF8.GetString(reader.Payload).ShouldBe($$"""{"id":{{i}}}""");
        }

        (await reader.ReadAsync()).ShouldBe(FrameStatus.EndOfStream);
    }

    /// <summary>A pipe delivers what it delivers; a frame split across reads is normal, not an error.</summary>
    [Fact]
    public async Task ReadAsync_FrameArrivingOneByteAtATime_IsReassembled()
    {
        using var underlying = Framed("""{"t":"Ping","id":7}""");
        await using var trickle = new TrickleStream(underlying, bytesPerRead: 1);
        using var reader = new FrameReader(trickle);

        (await reader.ReadAsync()).ShouldBe(FrameStatus.Frame);
        Encoding.UTF8.GetString(reader.Payload).ShouldBe("""{"t":"Ping","id":7}""");
    }

    /// <summary>
    /// The declared length is the one number in a frame that comes entirely from the peer. Sizing a buffer
    /// from it before checking it is the whole vulnerability, so the refusal has to happen first.
    /// </summary>
    [Fact]
    public async Task ReadAsync_DeclaredLengthAboveTheCap_IsRefusedWithoutAllocating()
    {
        using var stream = WithDeclaredLength(declared: 3_000_000_000);
        using var reader = new FrameReader(stream, maxFrameBytes: IpcProtocol.MaxRequestFrameBytes);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var status = await reader.ReadAsync();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        status.ShouldBe(FrameStatus.TooLarge);
        reader.DeclaredLength.ShouldBe(3_000_000_000u);
        allocated.ShouldBeLessThan(3_000_000_000);
        allocated.ShouldBeLessThan(64 * 1024);
    }

    /// <summary>
    /// The server's cap is 64x smaller than the client's, which costs nothing and cuts by the same factor
    /// the memory a hostile same-user process can make an elevated Agent commit.
    /// </summary>
    [Fact]
    public async Task ReadAsync_FrameLegalForAClientButNotForTheServer_IsRefusedByTheServer()
    {
        var big = new string('x', 200_000);

        using var forServer = WithDeclaredLength(declared: (uint)big.Length);
        using var serverReader = new FrameReader(forServer, IpcProtocol.MaxRequestFrameBytes);
        (await serverReader.ReadAsync()).ShouldBe(FrameStatus.TooLarge);

        using var forClient = Framed(big);
        using var clientReader = new FrameReader(forClient, IpcProtocol.MaxInboundFrameBytes);
        (await clientReader.ReadAsync()).ShouldBe(FrameStatus.Frame);
        clientReader.Payload.Length.ShouldBe(big.Length);
    }

    [Fact]
    public async Task ReadAsync_ZeroLength_IsMalformed()
    {
        using var stream = WithDeclaredLength(declared: 0);
        using var reader = new FrameReader(stream);

        (await reader.ReadAsync()).ShouldBe(FrameStatus.Malformed);
    }

    /// <summary>Closing between frames is how a connection normally ends, not a fault to log.</summary>
    [Fact]
    public async Task ReadAsync_CleanEndOfStream_IsNotAnError()
    {
        using var stream = new MemoryStream();
        using var reader = new FrameReader(stream);

        (await reader.ReadAsync()).ShouldBe(FrameStatus.EndOfStream);
    }

    [Fact]
    public async Task ReadAsync_EndOfStreamPartWayThroughTheHeader_IsMalformed()
    {
        using var stream = new MemoryStream([1, 2]);
        using var reader = new FrameReader(stream);

        (await reader.ReadAsync()).ShouldBe(FrameStatus.Malformed);
    }

    /// <summary>A peer that promises bytes and then closes is not the same as a peer that closes.</summary>
    [Fact]
    public async Task ReadAsync_TruncatedBody_IsMalformed()
    {
        using var stream = WithDeclaredLength(declared: 100, actualBodyBytes: 10);
        using var reader = new FrameReader(stream);

        (await reader.ReadAsync()).ShouldBe(FrameStatus.Malformed);
    }

    // -- writing -----------------------------------------------------------------------------------------

    [Fact]
    public async Task WriteAsync_Frame_RoundTripsThroughTheReader()
    {
        using var stream = new MemoryStream();
        using (var writer = new FrameWriter(stream))
        {
            await writer.WriteAsync(json => IpcEnvelope.Write(
                json, MessageType.Pong, id: 42, replyTo: 41, new PongPayload(1_700_000_000),
                IpcJsonContext.Default.PongPayload));
        }

        stream.Position = 0;
        using var reader = new FrameReader(stream);
        (await reader.ReadAsync()).ShouldBe(FrameStatus.Frame);

        IpcEnvelope.TryReadHeader(reader.Payload, out var header).ShouldBeTrue();
        header.Type.ShouldBe(MessageType.Pong);
        header.Id.ShouldBe(42);
        header.ReplyTo.ShouldBe(41);

        IpcEnvelope.TryReadPayload(reader.Payload, header, IpcJsonContext.Default.PongPayload, out var payload)
            .ShouldBeTrue();
        payload!.ServerTimeUtc.ShouldBe(1_700_000_000);
    }

    /// <summary>
    /// A length and its body emitted as two writes can have another writer's frame land between them, and
    /// the peer never recovers: every subsequent length is read out of the middle of somebody else's JSON.
    /// </summary>
    [Fact]
    public async Task WriteAsync_ConcurrentWriters_DoNotInterleaveFrames()
    {
        using var stream = new MemoryStream();
        using var writer = new FrameWriter(stream);

        var senders = Enumerable.Range(0, 50).Select(i => Task.Run(async () =>
            await writer.WriteAsync(json => IpcEnvelope.Write(
                json, MessageType.Ping, id: i, replyTo: null, new PongPayload(i),
                IpcJsonContext.Default.PongPayload))));

        await Task.WhenAll(senders);

        stream.Position = 0;
        using var reader = new FrameReader(stream);

        var ids = new List<long>();
        while (await reader.ReadAsync() == FrameStatus.Frame)
        {
            IpcEnvelope.TryReadHeader(reader.Payload, out var header).ShouldBeTrue();
            ids.Add(header.Id);
        }

        ids.Count.ShouldBe(50);
        ids.Order().ShouldBe(Enumerable.Range(0, 50).Select(i => (long)i));
    }

    [Fact]
    public async Task Prepare_ProducesTheSameBytesAsTheInstanceWriter()
    {
        var prepared = FrameWriter.Prepare(json => IpcEnvelope.Write(
            json, MessageType.Ping, id: 5, replyTo: null, new PongPayload(9), IpcJsonContext.Default.PongPayload));

        using var stream = new MemoryStream();
        using (var writer = new FrameWriter(stream))
        {
            await writer.WriteAsync(json => IpcEnvelope.Write(
                json, MessageType.Ping, id: 5, replyTo: null, new PongPayload(9),
                IpcJsonContext.Default.PongPayload));
        }

        prepared.ShouldBe(stream.ToArray());
    }

    /// <summary>Returns a few bytes at a time, the way a real transport does.</summary>
    private sealed class TrickleStream(Stream inner, int bytesPerRead) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, Math.Min(count, bytesPerRead));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer[..Math.Min(buffer.Length, bytesPerRead)], cancellationToken);

        public override void Flush() => inner.Flush();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
