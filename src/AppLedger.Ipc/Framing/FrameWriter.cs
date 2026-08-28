using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AppLedger.Ipc.Framing;

/// <summary>
/// Writes length-prefixed frames to a stream, one at a time (docs/07_IPC.md §Framing).
/// </summary>
/// <remarks>
/// <b>One write per frame, and one writer at a time.</b> Emitting the prefix and the body as two writes lets
/// another writer's frame interleave between them, which desynchronizes the peer permanently — every
/// subsequent length is read out of the middle of somebody else's JSON. The body is serialized into a reused
/// buffer with four bytes reserved at the front, the prefix is patched in afterwards, and the whole thing
/// goes out in a single call under a semaphore.
/// <para>
/// The semaphore is not theoretical: the fan-out pump pushing <c>AppsTick</c> and the request/response path
/// answering a <c>Ping</c> are different tasks writing to the same connection.
/// </para>
/// </remarks>
public sealed class FrameWriter : IDisposable
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ArrayBufferWriter<byte> _buffer = new(8 * 1024);
    private readonly Utf8JsonWriter _json;

    /// <summary>Creates a writer over a stream.</summary>
    /// <param name="stream">The transport. Not owned.</param>
    public FrameWriter(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        _stream = stream;
        _json = new Utf8JsonWriter(_buffer, new JsonWriterOptions { SkipValidation = false });
    }

    /// <summary>
    /// Serializes one frame with <paramref name="compose"/> and sends it.
    /// </summary>
    /// <remarks>
    /// The callback shape is what keeps the hot path allocation-free: the caller writes straight into the
    /// shared buffer instead of handing over a byte array somebody had to build first.
    /// </remarks>
    public async ValueTask WriteAsync(Action<Utf8JsonWriter> compose, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compose);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var frame = Compose(compose);
            await _stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Sends a frame whose bytes are already serialized, prefix included.</summary>
    /// <remarks>
    /// This is how one tick reaches four subscribers having been serialized once (see
    /// <c>StreamHub</c>): the bytes are shared, only the write is per-connection.
    /// </remarks>
    public async ValueTask WritePreparedAsync(
        ReadOnlyMemory<byte> prefixedFrame,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(prefixedFrame, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Serializes one frame into a fresh array, prefix included, for broadcasting to several connections.
    /// </summary>
    public static byte[] Prepare(Action<Utf8JsonWriter> compose)
    {
        ArgumentNullException.ThrowIfNull(compose);

        var buffer = new ArrayBufferWriter<byte>(8 * 1024);
        buffer.Write(stackalloc byte[4]);

        using (var json = new Utf8JsonWriter(buffer))
        {
            compose(json);
            json.Flush();
        }

        var frame = buffer.WrittenSpan.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(0, 4), (uint)(frame.Length - 4));
        return frame;
    }

    private ReadOnlyMemory<byte> Compose(Action<Utf8JsonWriter> compose)
    {
        _buffer.ResetWrittenCount();
        _buffer.Write(stackalloc byte[4]);   // reserved for the prefix, patched once the length is known

        _json.Reset(_buffer);
        compose(_json);
        _json.Flush();

        var written = _buffer.WrittenMemory;

        // Writing through WrittenMemory is sound here and only here: this instance owns the buffer, nothing
        // else holds a reference to it, and the four bytes being patched are the ones reserved above.
        var patchable = MemoryMarshal.AsMemory(written)[..4];
        BinaryPrimitives.WriteUInt32LittleEndian(patchable.Span, (uint)(written.Length - 4));
        return written;
    }

    /// <summary>Releases the JSON writer and the send gate.</summary>
    public void Dispose()
    {
        _json.Dispose();
        _gate.Dispose();
    }
}
