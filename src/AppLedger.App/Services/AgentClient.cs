using System.IO;
using System.Windows.Threading;
using AppLedger.Core.Metrics;
using AppLedger.Infrastructure.Ipc;
using AppLedger.Ipc;
using AppLedger.Ipc.Framing;
using AppLedger.Ipc.Streams;

// The interface names the event AppsTick and the codec type is also AppsTick; the alias keeps
// both readable rather than renaming one of them to avoid the other.
using AppsTickCodec = AppLedger.Ipc.Streams.AppsTick;

namespace AppLedger.App.Services;

/// <summary>
/// Talks to the Agent over the pipe, and runs the collector in this process when none answers
/// (docs/01_ARCHITECTURE.md §Elevation strategy step 3, docs/07_IPC.md §Threading in the UI).
/// </summary>
/// <remarks>
/// <b>Coalescing is the point of the dispatcher timer.</b> Ticks arrive at 1 Hz on a background thread and
/// the grid renders when WPF gets round to it; if two ticks land before a render, only the newest is applied.
/// Marshalling each tick individually would queue work the UI can never catch up on, and the symptom of that
/// is a grid that lags further behind the longer the window stays open.
/// </remarks>
public sealed class AgentClient : IAgentClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _drain;
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<AppRow> _decoded = [];
    private readonly Lock _gate = new();

    private byte[]? _pendingApps;
    private HealthPayload? _pendingHealth;
    private LiteCollector? _lite;
    private Task? _reader;
    private long _nextId;

    /// <summary>Creates the client on the UI dispatcher.</summary>
    public AgentClient(Dispatcher? dispatcher = null)
    {
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;

        // One drain, not one marshal per tick. 20 Hz is far above the 1 Hz the data arrives at, so a tick is
        // never held for long, and the queue can never grow.
        _drain = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };

        _drain.Tick += (_, _) => Drain();
    }

    /// <inheritdoc />
    public event Action<AgentStatus>? StatusChanged;

    /// <inheritdoc />
    public event Action<IReadOnlyList<AppRow>>? AppsTick;

    /// <inheritdoc />
    public event Action<HealthPayload>? HealthTick;

    /// <inheritdoc />
    public AgentStatus Status { get; private set; } = AgentStatus.Connecting;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _drain.Start();

        var stream = await TryConnectAsync(cancellationToken).ConfigureAwait(false);
        if (stream is null)
        {
            await StartLiteAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        _reader = Task.Run(() => ReadAsync(stream, _stopping.Token), CancellationToken.None);
    }

    private static async Task<Stream?> TryConnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            var transport = new NamedPipeClientTransport(
                IpcProtocol.PipeLocalName,

                // Any process running as this user could be holding the pipe name, so the server has to be
                // the Agent shipped beside us. A peer that cannot be verified is refused, not trusted.
                owner =>
                {
                    var pid = PipePeer.ServerProcessId(owner.Stream.SafePipeHandle);
                    return pid is not null
                        && Environment.ProcessPath is { } own
                        && PipePeer.IsSameInstallDirectory(PipePeer.TryGetImagePath(pid.Value), own);
                });

            return await transport.ConnectAsync(ConnectTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException
            or OperationCanceledException)
        {
            // Two seconds and nothing answered. docs/01 step 3: the UI then checks whether the task exists at
            // all, and either offers to start it or falls back to Lite. Either way it keeps drawing - a first
            // run that dead-ends on a spinner is what Lite mode exists to prevent.
            return null;
        }
    }

    private async Task StartLiteAsync(CancellationToken cancellationToken)
    {
        _lite = new LiteCollector();
        await _lite.StartAsync(cancellationToken).ConfigureAwait(false);

        Publish(new AgentStatus(
            ConnectionMode.Lite,
            AgentVersion: null,
            _lite.Sensors.ToDictionary(
                s => s.Name,
                s => new SensorStatePayload(s.Health.State.ToString(), s.Health.Detail),
                StringComparer.Ordinal),
            TaskInstalled: false));

        _reader = Task.Run(() => PumpLiteAsync(_lite, _stopping.Token), CancellationToken.None);
    }

    /// <summary>Turns the in-process collector's samples into the same rows the pipe would deliver.</summary>
    private async Task PumpLiteAsync(LiteCollector lite, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var samples in lite.Host.Live.Reader
                .ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                // Serialized and re-read rather than handed over directly, so the Lite path and the pipe path
                // go through the identical encoder. A second conversion would be a second place for the grid
                // to disagree with itself about what a column means.
                var ts = samples.Count > 0 ? samples[0].TsUtc : 0;
                var frame = FrameWriter.Prepare(json => AppsTickCodec.Write(json, ts, samples));

                lock (_gate)
                {
                    _pendingApps = frame;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    private async Task ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            await using var owned = stream;
            using var reader = new FrameReader(stream, IpcProtocol.MaxInboundFrameBytes);
            using var writer = new FrameWriter(stream);

            var ack = await HelloAsync(reader, writer, cancellationToken).ConfigureAwait(false);
            if (ack is null)
            {
                await StartLiteAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            Publish(new AgentStatus(
                ack.Mode == AppLedger.Ipc.AgentMode.Full ? ConnectionMode.Full : ConnectionMode.Degraded,
                ack.Agent,
                ack.Sensors,
                TaskInstalled: true));

            await SubscribeAsync(writer, StreamHub.AppsStream, cancellationToken).ConfigureAwait(false);
            await SubscribeAsync(writer, StreamHub.HealthStream, cancellationToken).ConfigureAwait(false);

            while (!cancellationToken.IsCancellationRequested)
            {
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) != FrameStatus.Frame)
                {
                    break;
                }

                if (!IpcEnvelope.TryReadHeader(reader.Payload, out var header))
                {
                    continue;
                }

                switch (header.Type)
                {
                    case MessageType.AppsTick:
                        {
                            // Copied out of the reader's buffer: the next read overwrites it, and the drain
                            // happens on the dispatcher long after this loop has moved on.
                            var frame = reader.Payload[header.PayloadStart..(header.PayloadStart + header.PayloadLength)]
                                .ToArray();

                            lock (_gate)
                            {
                                _pendingApps = frame;
                            }

                            break;
                        }

                    case MessageType.HealthTick:
                        if (IpcEnvelope.TryReadPayload(
                                reader.Payload, header, IpcJsonContext.Default.HealthPayload, out var health))
                        {
                            lock (_gate)
                            {
                                _pendingHealth = health;
                            }
                        }

                        break;

                    default:
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The Agent went away. Falling back keeps the window useful rather than freezing it on the last
            // tick it happened to receive.
            if (!cancellationToken.IsCancellationRequested)
            {
                await StartLiteAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<HelloAckPayload?> HelloAsync(
        FrameReader reader,
        FrameWriter writer,
        CancellationToken cancellationToken)
    {
        await writer.WriteAsync(json => IpcEnvelope.Write(
            json,
            MessageType.Hello,
            NextId(),
            null,
            new HelloPayload(IpcProtocol.Version, "AppLedger.App", System.Globalization.CultureInfo.CurrentUICulture.Name),
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

    private ValueTask SubscribeAsync(FrameWriter writer, string stream, CancellationToken cancellationToken) =>
        writer.WriteAsync(json => IpcEnvelope.Write(
            json, MessageType.Subscribe, NextId(), null, new SubscribePayload(stream),
            IpcJsonContext.Default.SubscribePayload), cancellationToken);

    /// <summary>Applies whatever arrived since the last render, and nothing older.</summary>
    private void Drain()
    {
        byte[]? apps;
        HealthPayload? health;

        lock (_gate)
        {
            apps = _pendingApps;
            health = _pendingHealth;
            _pendingApps = null;
            _pendingHealth = null;
        }

        if (apps is not null && AppsTick is not null && AppsTickCodec.Read(apps, _decoded) >= 0)
        {
            AppsTick(_decoded);
        }

        if (health is not null)
        {
            HealthTick?.Invoke(health);
        }
    }

    private void Publish(AgentStatus status)
    {
        Status = status;

        // Raised on the dispatcher, because every subscriber is a view-model bound to a control.
        _ = _dispatcher.BeginInvoke(() => StatusChanged?.Invoke(status));
    }

    private long NextId() => Interlocked.Increment(ref _nextId);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _drain.Stop();
        await _stopping.CancelAsync().ConfigureAwait(false);

        if (_reader is not null)
        {
            try
            {
                await _reader.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // Shutting down; a torn connection here is the expected way this ends.
            }
        }

        if (_lite is not null)
        {
            await _lite.DisposeAsync().ConfigureAwait(false);
        }

        _stopping.Dispose();
    }
}
