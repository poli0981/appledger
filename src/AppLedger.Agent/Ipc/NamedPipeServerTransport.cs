using System.IO.Pipes;
using AppLedger.Infrastructure.Ipc;
using AppLedger.Ipc;
using Microsoft.Extensions.Logging;

namespace AppLedger.Agent.Ipc;

/// <summary>
/// The Windows half of <see cref="IServerTransport"/>: creates the pipe with the ADR-17 descriptor and
/// refuses a peer that is not the App shipped beside us.
/// </summary>
/// <remarks>
/// It lives in the Agent rather than in Infrastructure because implementing the port would mean an
/// <c>Infrastructure → Ipc</c> reference, and that edge is not in <c>CLAUDE.md</c> §Solution layout. The
/// Agent already references both, so nothing new appears in the graph; Infrastructure keeps supplying only
/// the two Windows primitives, which is all it should know about a protocol it does not speak.
/// </remarks>
public sealed partial class NamedPipeServerTransport : IServerTransport
{
    private readonly string _pipeName;
    private readonly ILogger<NamedPipeServerTransport> _logger;
    private readonly string? _ownImagePath;
    private readonly bool _verifyPeer;

    /// <summary>Creates the transport.</summary>
    /// <param name="logger">For refusals, which are security events worth seeing.</param>
    /// <param name="pipeName">Overridable so a test can use a name of its own.</param>
    /// <param name="verifyPeer">
    /// Off only for a test harness, where both ends are the test host and "same directory" means nothing.
    /// </param>
    public NamedPipeServerTransport(
        ILogger<NamedPipeServerTransport> logger,
        string? pipeName = null,
        bool verifyPeer = true)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _pipeName = pipeName ?? IpcProtocol.PipeLocalName;
        _verifyPeer = verifyPeer;
        _ownImagePath = Environment.ProcessPath;
    }

    /// <inheritdoc />
    public int MaxConcurrentClients => IpcProtocol.MaxServerInstances;

    /// <inheritdoc />
    public async ValueTask<Stream> AcceptAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var server = NamedPipeServerFactory.Create(_pipeName, MaxConcurrentClients);
            try
            {
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await server.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            if (IsPeerTrusted(server))
            {
                return server;
            }

            // Refused, and the loop continues: one squatter must not stop the Agent from serving the real
            // App that connects a moment later.
            await server.DisposeAsync().ConfigureAwait(false);
        }
    }

    private bool IsPeerTrusted(NamedPipeServerStream server)
    {
        if (!_verifyPeer)
        {
            return true;
        }

        var pid = PipePeer.ClientProcessId(server.SafePipeHandle);
        if (pid is null)
        {
            PeerRefused(_logger, "the client's process id could not be read");
            return false;
        }

        var peerPath = PipePeer.TryGetImagePath(pid.Value);
        if (_ownImagePath is null || !PipePeer.IsSameInstallDirectory(peerPath, _ownImagePath))
        {
            // The path is deliberately not logged even at this level: it is attacker-chosen text, and
            // docs/15 §Redaction keeps paths out of Information entirely.
            PeerRefused(_logger, "the client is not installed beside this Agent");
            return false;
        }

        return true;
    }

    [LoggerMessage(
        EventId = 1520,
        Level = LogLevel.Warning,
        Message = "A pipe client was refused: {Reason}")]
    private static partial void PeerRefused(ILogger logger, string reason);
}
