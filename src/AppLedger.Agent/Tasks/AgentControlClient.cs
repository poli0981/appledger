using AppLedger.Infrastructure.Ipc;
using AppLedger.Ipc;
using AppLedger.Ipc.Framing;

namespace AppLedger.Agent.Tasks;

/// <summary>What a running Agent answered, or why it did not.</summary>
/// <param name="Reachable">True when a <c>HelloAck</c> came back.</param>
/// <param name="Version">The Agent's version, when it answered.</param>
/// <param name="Mode">Full or Degraded, when it answered.</param>
public readonly record struct AgentStatus(bool Reachable, string? Version, AgentMode? Mode);

/// <summary>
/// The small client the Agent's own CLI needs: <c>--status</c> has to ask whether an Agent is answering,
/// and <c>--remove-task</c> has to ask a running one to stop before deleting its task.
/// </summary>
/// <remarks>
/// Deliberately a handful of round trips rather than the App's full <c>IpcClient</c>. This one talks to a
/// peer it is shipped beside, does one thing and exits; the UI's client keeps a read loop, a heartbeat and a
/// reconnect policy, none of which a command-line invocation has any use for.
/// </remarks>
public static class AgentControlClient
{
    /// <summary>Asks a running Agent who it is.</summary>
    public static async Task<AgentStatus> QueryAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = await ConnectAsync(timeout, cancellationToken).ConfigureAwait(false);
            using var reader = new FrameReader(stream, IpcProtocol.MaxInboundFrameBytes);
            using var writer = new FrameWriter(stream);

            var ack = await HelloAsync(reader, writer, cancellationToken).ConfigureAwait(false);
            return ack is null
                ? new AgentStatus(false, null, null)
                : new AgentStatus(true, ack.Agent, ack.Mode);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException
            or OperationCanceledException)
        {
            // Nothing is listening, or what is listening is not ours. Both are "not reachable" to a caller
            // deciding whether to start the task.
            return new AgentStatus(false, null, null);
        }
    }

    /// <summary>Asks a running Agent to shut down, and waits for it to let go of the pipe.</summary>
    /// <returns>True when an Agent acknowledged; false when none was running.</returns>
    public static async Task<bool> ShutdownAsync(
        string reason,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = await ConnectAsync(timeout, cancellationToken).ConfigureAwait(false);
            using var reader = new FrameReader(stream, IpcProtocol.MaxInboundFrameBytes);
            using var writer = new FrameWriter(stream);

            if (await HelloAsync(reader, writer, cancellationToken).ConfigureAwait(false) is null)
            {
                return false;
            }

            await writer.WriteAsync(json => IpcEnvelope.Write(
                json, MessageType.Shutdown, 2, null, new ShutdownPayload(reason),
                IpcJsonContext.Default.ShutdownPayload), cancellationToken).ConfigureAwait(false);

            var status = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            return status == FrameStatus.Frame
                && IpcEnvelope.TryReadHeader(reader.Payload, out var header)
                && header.Type == MessageType.Ack;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException
            or OperationCanceledException)
        {
            return false;
        }
    }

    private static ValueTask<Stream> ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var transport = new NamedPipeClientTransport(
            IpcProtocol.PipeLocalName,

            // The server has to be the Agent shipped beside us. Any process running as this user could be
            // holding the pipe name, and refusing an unverifiable peer is the only reading that fails safe.
            owner =>
            {
                var pid = PipePeer.ServerProcessId(owner.Stream.SafePipeHandle);
                return pid is not null
                    && Environment.ProcessPath is { } own
                    && PipePeer.IsSameInstallDirectory(PipePeer.TryGetImagePath(pid.Value), own);
            });

        return transport.ConnectAsync(timeout, cancellationToken);
    }

    private static async Task<HelloAckPayload?> HelloAsync(
        FrameReader reader,
        FrameWriter writer,
        CancellationToken cancellationToken)
    {
        await writer.WriteAsync(json => IpcEnvelope.Write(
            json,
            MessageType.Hello,
            1,
            null,
            new HelloPayload(IpcProtocol.Version, "AppLedger.Agent CLI", "en"),
            IpcJsonContext.Default.HelloPayload), cancellationToken).ConfigureAwait(false);

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) != FrameStatus.Frame
            || !IpcEnvelope.TryReadHeader(reader.Payload, out var header)
            || header.Type != MessageType.HelloAck)
        {
            return null;
        }

        IpcEnvelope.TryReadPayload(reader.Payload, header, IpcJsonContext.Default.HelloAckPayload, out var ack);
        return ack;
    }
}
