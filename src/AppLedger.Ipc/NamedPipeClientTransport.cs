using System.IO.Pipes;

namespace AppLedger.Ipc;

/// <summary>
/// Connects to the Agent's pipe (docs/07_IPC.md §Transport).
/// </summary>
/// <remarks>
/// Unlike the server half, this one lives in <c>AppLedger.Ipc</c>: <see cref="NamedPipeClientStream"/> is
/// cross-platform and needs no ACL, so nothing here trips CA1416 and both the App and the Agent's own
/// <c>--status</c> can use it without either of them reimplementing it.
/// <para>
/// <c>PipeOptions.CurrentUserOnly</c> is deliberately <b>not</b> set. Its client half verifies the pipe's
/// owner is the current user, and a pipe created by the elevated Agent is owned by <c>BUILTIN\Administrators</c>
/// — so the check fails against the very server it is meant to accept (docs/24_ADR.md ADR-17). Peer identity
/// is established by verifying the server's image path instead, which the caller supplies.
/// </para>
/// </remarks>
public sealed class NamedPipeClientTransport : IClientTransport
{
    private readonly string _pipeName;
    private readonly Func<SafeHandleOwner, bool>? _verifyServer;

    /// <summary>Creates the transport.</summary>
    /// <param name="pipeName">The local name, without the <c>\\.\pipe\</c> prefix.</param>
    /// <param name="verifyServer">
    /// Called once on connect with the connected stream. Return false to refuse the server and disconnect.
    /// Null skips verification, which is for tests only — in the product a peer that cannot be verified is a
    /// peer that must be refused.
    /// </param>
    public NamedPipeClientTransport(string? pipeName = null, Func<SafeHandleOwner, bool>? verifyServer = null)
    {
        _pipeName = pipeName ?? IpcProtocol.PipeLocalName;
        _verifyServer = verifyServer;
    }

    /// <inheritdoc />
    public async ValueTask<Stream> ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var stream = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        try
        {
            await stream.ConnectAsync((int)timeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        if (_verifyServer is not null && !_verifyServer(new SafeHandleOwner(stream)))
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw new IOException("The pipe server is not the Agent installed beside this client.");
        }

        return stream;
    }

    /// <summary>
    /// Hands the connected stream to a verifier without handing it the whole transport.
    /// </summary>
    /// <remarks>
    /// The verifier needs the pipe handle, which is Windows-specific territory; wrapping it keeps this
    /// assembly from naming any of those types while still letting the caller reach the handle.
    /// </remarks>
    public readonly struct SafeHandleOwner(NamedPipeClientStream stream)
    {
        /// <summary>The connected client stream.</summary>
        public NamedPipeClientStream Stream { get; } = stream;
    }
}
