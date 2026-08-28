using System.Buffers.Binary;
using System.Numerics;

namespace AppLedger.Ipc.Framing;

/// <summary>How a read ended.</summary>
public enum FrameStatus
{
    /// <summary>A complete frame is in <see cref="FrameReader.Payload"/>.</summary>
    Frame,

    /// <summary>The peer closed the connection between frames. An ordinary disconnect, not an error.</summary>
    EndOfStream,

    /// <summary>
    /// The declared length exceeded the cap. Nothing was allocated for it; the caller answers
    /// <see cref="IpcErrorCode.FrameTooLarge"/> and closes the connection.
    /// </summary>
    TooLarge,

    /// <summary>A zero length, or the stream ended part-way through a frame.</summary>
    Malformed,
}

/// <summary>
/// Reads <c>[u32 little-endian length][UTF-8 JSON]</c> frames from a stream (docs/07_IPC.md §Framing).
/// </summary>
/// <remarks>
/// Written against <see cref="Stream"/> rather than <c>PipeStream</c>, which is what lets the whole protocol
/// be tested over an in-memory pair with no pipe, no Windows and no elevation — and it costs nothing, since
/// <c>NamedPipeServerStream</c> is a <see cref="Stream"/>.
/// <para>
/// <b>The length is checked before a buffer is sized.</b> It is the one number in the frame that comes
/// entirely from the peer, and sizing an allocation from it first is the whole vulnerability.
/// </para>
/// <para>
/// <b>An oversized frame is not skipped.</b> There is no safe resynchronization point in a byte-stream
/// framing: skipping <c>length</c> bytes means trusting the same number that was just rejected. The caller
/// answers and disconnects.
/// </para>
/// </remarks>
public sealed class FrameReader : IDisposable
{
    private const int InitialBufferBytes = 8 * 1024;

    private readonly Stream _stream;
    private readonly int _maxFrameBytes;
    private readonly byte[] _lengthPrefix = new byte[4];

    private byte[] _buffer = new byte[InitialBufferBytes];
    private int _length;

    /// <summary>Creates a reader over a stream.</summary>
    /// <param name="stream">The transport. Not owned: disposing the reader does not dispose it.</param>
    /// <param name="maxFrameBytes">
    /// The cap. A server passes <see cref="IpcProtocol.MaxRequestFrameBytes"/>; a client passes
    /// <see cref="IpcProtocol.MaxInboundFrameBytes"/>.
    /// </param>
    public FrameReader(Stream stream, int maxFrameBytes = IpcProtocol.MaxInboundFrameBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFrameBytes, 1);

        _stream = stream;
        _maxFrameBytes = maxFrameBytes;
    }

    /// <summary>The frame read by the last successful <see cref="ReadAsync"/>.</summary>
    /// <remarks>Valid only until the next read: the buffer is reused.</remarks>
    public ReadOnlySpan<byte> Payload => _buffer.AsSpan(0, _length);

    /// <summary>
    /// The length the peer declared for a frame that was refused, for the error message. Never used to
    /// allocate anything.
    /// </summary>
    public uint DeclaredLength { get; private set; }

    /// <summary>Reads the next frame.</summary>
    public async ValueTask<FrameStatus> ReadAsync(CancellationToken cancellationToken = default)
    {
        _length = 0;
        DeclaredLength = 0;

        var read = await _stream
            .ReadAtLeastAsync(_lengthPrefix, minimumBytes: 4, throwOnEndOfStream: false, cancellationToken)
            .ConfigureAwait(false);

        if (read == 0)
        {
            // Nothing at all: the peer closed between frames, which is how a connection normally ends.
            return FrameStatus.EndOfStream;
        }

        if (read < 4)
        {
            return FrameStatus.Malformed;
        }

        var declared = BinaryPrimitives.ReadUInt32LittleEndian(_lengthPrefix);
        DeclaredLength = declared;

        if (declared == 0)
        {
            return FrameStatus.Malformed;
        }

        if (declared > (uint)_maxFrameBytes)
        {
            return FrameStatus.TooLarge;
        }

        // Only now, with the length known to be within the cap, is memory committed for it.
        var length = (int)declared;
        if (_buffer.Length < length)
        {
            _buffer = new byte[BitOperations.RoundUpToPowerOf2((uint)length)];
        }

        try
        {
            await _stream.ReadExactlyAsync(_buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            // A truncated frame is not a clean disconnect: the peer promised bytes it did not send.
            return FrameStatus.Malformed;
        }

        _length = length;
        return FrameStatus.Frame;
    }

    /// <summary>Releases the read buffer.</summary>
    public void Dispose()
    {
        _buffer = [];
        _length = 0;
    }
}
