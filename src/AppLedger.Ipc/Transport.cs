namespace AppLedger.Ipc;

/// <summary>
/// Accepts one client at a time and hands back a connected duplex stream.
/// </summary>
/// <remarks>
/// This port is the reason <c>AppLedger.Ipc</c> can target <c>net10.0</c> at all. Securing the pipe needs
/// <c>PipeSecurity</c> and a mandatory integrity label, and every member of those APIs carries
/// <c>[SupportedOSPlatform("windows")]</c> — from an unattributed assembly the platform-compatibility
/// analyzer raises CA1416, which this repo turns into a build error. The API is not missing; the analyzer
/// refuses it, and that refusal is what keeps the protocol honest about being platform-neutral.
/// <para>
/// So the Agent implements this over <c>NamedPipeServerStreamAcl.Create</c> and the Windows primitives in
/// <c>AppLedger.Infrastructure</c> (docs/07_IPC.md §Transport), and a test implements it over an in-memory
/// pair. Neither needs the other.
/// </para>
/// <para>
/// Accept is part of the port rather than a bare stream factory because the server has to
/// <c>WaitForConnectionAsync</c>, which is not something a <see cref="Stream"/> can do.
/// </para>
/// </remarks>
public interface IServerTransport
{
    /// <summary>How many clients may be connected at once.</summary>
    int MaxConcurrentClients { get; }

    /// <summary>
    /// Waits for the next client. The returned stream is owned by the caller, which disposes it when the
    /// connection ends.
    /// </summary>
    ValueTask<Stream> AcceptAsync(CancellationToken cancellationToken);
}

/// <summary>Connects to the server and hands back a connected duplex stream.</summary>
public interface IClientTransport
{
    /// <summary>
    /// Connects, or throws <see cref="TimeoutException"/> when nothing answers within
    /// <paramref name="timeout"/>.
    /// </summary>
    /// <remarks>
    /// The UI gives this two seconds before it checks whether the Scheduled Task exists at all and offers
    /// Lite mode (docs/01_ARCHITECTURE.md §Elevation strategy). A first run that dead-ends on a spinner is
    /// exactly what Lite mode exists to prevent.
    /// </remarks>
    ValueTask<Stream> ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken);
}
