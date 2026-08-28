using System.Reflection;
using AppLedger.Agent.Hosting;
using AppLedger.Core.Collection;
using AppLedger.Ipc;
using AppLedger.Ipc.Framing;
using AppLedger.Ipc.Streams;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AppLedger.Agent.Ipc;

/// <summary>
/// Serves the pipe: one accept loop, one connection handler per client, and one pump that turns the
/// collector's live channel into <c>AppsTick</c> frames (docs/07_IPC.md).
/// </summary>
/// <remarks>
/// The pump is the reason this is not simply a request/response server. <c>CollectorHost.Live</c> is a queue
/// rather than a broadcast, so exactly one reader drains it, serializes each tick <b>once</b>, and hands the
/// same bytes to <see cref="StreamHub"/> for every subscriber. Four readers on the channel directly would
/// each get a disjoint subset of seconds and every one of them would draw a wrong chart.
/// </remarks>
public sealed partial class AgentPipeServer : BackgroundService
{
    private static readonly string AgentVersion =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    private readonly IServerTransport _transport;
    private readonly AgentRuntime _runtime;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<AgentPipeServer> _logger;
    private readonly StreamHub _hub = new();
    private readonly long _startedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private long _nextId;

    /// <summary>Creates the server.</summary>
    public AgentPipeServer(
        IServerTransport transport,
        AgentRuntime runtime,
        IHostApplicationLifetime lifetime,
        ILogger<AgentPipeServer> logger)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(logger);

        _transport = transport;
        _runtime = runtime;
        _lifetime = lifetime;
        _logger = logger;
    }

    /// <summary>How many clients are connected right now.</summary>
    public int ConnectedClients { get; private set; }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pump = Task.Run(() => PumpAsync(stoppingToken), CancellationToken.None);
        var health = Task.Run(() => HealthTicksAsync(stoppingToken), CancellationToken.None);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var stream = await _transport.AcceptAsync(stoppingToken).ConfigureAwait(false);

                // Deliberately not awaited: the accept loop must be free for the next client immediately.
                _ = Task.Run(() => ServeAsync(stream, stoppingToken), CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        finally
        {
            _hub.CompleteAll();
            await Task.WhenAll(pump, health).ConfigureAwait(false);
        }
    }

    /// <summary>Drains the live channel and broadcasts each second, serialized exactly once.</summary>
    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var samples in _runtime.Collector.Live.Reader
                .ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_hub.SubscriberCount(StreamHub.AppsStream) == 0)
                {
                    continue;
                }

                var ts = samples.Count > 0 ? samples[0].TsUtc : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var frame = FrameWriter.Prepare(json =>
                {
                    IpcEnvelope.WriteStart(json, MessageType.AppsTick, NextId(), replyTo: null);
                    json.WritePropertyName("p"u8);
                    AppsTick.Write(json, ts, samples);
                    IpcEnvelope.WriteEnd(json);
                });

                _hub.Publish(StreamHub.AppsStream, frame);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    private async Task HealthTicksAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(IpcProtocol.HealthTickInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_hub.SubscriberCount(StreamHub.HealthStream) == 0)
                {
                    continue;
                }

                var payload = BuildHealth();
                var frame = FrameWriter.Prepare(json => IpcEnvelope.Write(
                    json, MessageType.HealthTick, NextId(), null, payload, IpcJsonContext.Default.HealthPayload));

                _hub.Publish(StreamHub.HealthStream, frame);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    private async Task ServeAsync(Stream stream, CancellationToken cancellationToken)
    {
        ConnectedClients++;

        var subscriptions = new List<StreamSubscription>();
        using var reader = new FrameReader(stream, IpcProtocol.MaxRequestFrameBytes);
        using var writer = new FrameWriter(stream);
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            var draining = Task.Run(() => DrainAsync(subscriptions, writer, connection.Token), CancellationToken.None);

            while (!connection.IsCancellationRequested)
            {
                var status = await reader.ReadAsync(connection.Token).ConfigureAwait(false);
                if (status == FrameStatus.EndOfStream)
                {
                    break;
                }

                if (status == FrameStatus.TooLarge)
                {
                    await SendErrorAsync(writer, 0, IpcErrorCode.FrameTooLarge, "frame exceeds the cap", connection.Token)
                        .ConfigureAwait(false);
                    break;
                }

                if (status == FrameStatus.Malformed || !IpcEnvelope.TryReadHeader(reader.Payload, out var header))
                {
                    await SendErrorAsync(writer, 0, IpcErrorCode.BadRequest, "unreadable frame", connection.Token)
                        .ConfigureAwait(false);
                    break;
                }

                // Any inbound frame means somebody is watching, which keeps the collector out of the idle
                // profile. Missing this leaves the Agent sampling at 2 s while a UI draws charts, and it
                // looks like a choppy chart rather than like a bug in the pipe server.
                _runtime.Collector.NoteUiActivity();

                // Decoded before the first await: a ReadOnlySpan cannot cross one, and the span points into
                // the reader's buffer, which the next read overwrites.
                var request = Decode(header, reader.Payload);

                if (!await HandleAsync(request, writer, subscriptions, connection.Token).ConfigureAwait(false))
                {
                    break;
                }
            }

            await connection.CancelAsync().ConfigureAwait(false);
            await draining.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ConnectionFaulted(_logger, ex.GetType().Name);
        }
        finally
        {
            foreach (var subscription in subscriptions)
            {
                subscription.Dispose();
            }

            await stream.DisposeAsync().ConfigureAwait(false);
            ConnectedClients--;
        }
    }

    /// <summary>Writes whatever the subscriptions have queued for this client.</summary>
    private static async Task DrainAsync(
        List<StreamSubscription> subscriptions,
        FrameWriter writer,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var wrote = false;

                // A snapshot, because Subscribe adds to the list from the read loop while this runs.
                foreach (var subscription in subscriptions.ToArray())
                {
                    while (subscription.Reader.TryRead(out var frame))
                    {
                        await writer.WritePreparedAsync(frame, cancellationToken).ConfigureAwait(false);
                        wrote = true;
                    }
                }

                if (!wrote)
                {
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The client went away, which the read loop notices too.
        }
    }

    /// <summary>The two payloads v0.2 reads, lifted out of the frame before anything is awaited.</summary>
    private readonly record struct Request(IpcHeader Header, HelloPayload? Hello, SubscribePayload? Subscribe);

    private static Request Decode(in IpcHeader header, ReadOnlySpan<byte> frame)
    {
        HelloPayload? hello = null;
        SubscribePayload? subscribe = null;

        switch (header.Type)
        {
            case MessageType.Hello:
                IpcEnvelope.TryReadPayload(frame, header, IpcJsonContext.Default.HelloPayload, out hello);
                break;

            case MessageType.Subscribe:
            case MessageType.Unsubscribe:
                IpcEnvelope.TryReadPayload(frame, header, IpcJsonContext.Default.SubscribePayload, out subscribe);
                break;

            default:
                break;
        }

        return new Request(header, hello, subscribe);
    }

    private async Task<bool> HandleAsync(
        Request request,
        FrameWriter writer,
        List<StreamSubscription> subscriptions,
        CancellationToken cancellationToken)
    {
        var header = request.Header;

        switch (header.Type)
        {
            case MessageType.Hello:
                {
                    var hello = request.Hello;
                    if (hello is null || hello.Protocol != IpcProtocol.Version)
                    {
                        await SendErrorAsync(writer, header.Id, IpcErrorCode.ProtocolUnsupported,
                            $"this Agent speaks protocol {IpcProtocol.Version}", cancellationToken).ConfigureAwait(false);
                        return false;
                    }

                    var ack = BuildHelloAck();
                    await writer.WriteAsync(json => IpcEnvelope.Write(
                        json, MessageType.HelloAck, NextId(), header.Id, ack, IpcJsonContext.Default.HelloAckPayload),
                        cancellationToken).ConfigureAwait(false);
                    return true;
                }

            case MessageType.Ping:
                await writer.WriteAsync(json => IpcEnvelope.Write(
                    json, MessageType.Pong, NextId(), header.Id,
                    new PongPayload(DateTimeOffset.UtcNow.ToUnixTimeSeconds()), IpcJsonContext.Default.PongPayload),
                    cancellationToken).ConfigureAwait(false);
                return true;

            case MessageType.GetHealth:
                await writer.WriteAsync(json => IpcEnvelope.Write(
                    json, MessageType.Health, NextId(), header.Id, BuildHealth(), IpcJsonContext.Default.HealthPayload),
                    cancellationToken).ConfigureAwait(false);
                return true;

            case MessageType.Subscribe:
                {
                    var subscribe = request.Subscribe;
                    if (subscribe is null || !IsServedStream(subscribe.Stream))
                    {
                        await SendErrorAsync(writer, header.Id, IpcErrorCode.BadRequest,
                            "unsupported stream in this build", cancellationToken).ConfigureAwait(false);
                        return true;
                    }

                    subscriptions.Add(_hub.Subscribe(subscribe.Stream));
                    await SendAckAsync(writer, header.Id, cancellationToken).ConfigureAwait(false);
                    return true;
                }

            case MessageType.Unsubscribe:
                {
                    var unsubscribe = request.Subscribe;
                    if (unsubscribe is not null)
                    {
                        var match = subscriptions.Find(s => s.Stream == unsubscribe.Stream);
                        if (match is not null)
                        {
                            subscriptions.Remove(match);
                            match.Dispose();
                        }
                    }

                    await SendAckAsync(writer, header.Id, cancellationToken).ConfigureAwait(false);
                    return true;
                }

            case MessageType.Shutdown:
                await SendAckAsync(writer, header.Id, cancellationToken).ConfigureAwait(false);
                ShutdownRequested(_logger);

                // The worker's finally block flushes the partial minute on the way out, so the history has
                // no hole where the shutdown was.
                _lifetime.StopApplication();
                return false;

            default:
                // A name we know but do not answer yet. Distinct from an unknown name, which never reaches
                // here: TryReadHeader rejects those, and the UI reads that as "update required".
                await SendErrorAsync(writer, header.Id, IpcErrorCode.BadRequest,
                    "not implemented in this build", cancellationToken).ConfigureAwait(false);
                return true;
        }
    }

    private static bool IsServedStream(string stream) =>
        stream is StreamHub.AppsStream or StreamHub.HealthStream;

    private HelloAckPayload BuildHelloAck()
    {
        var health = _runtime.Collector.ReadHealth();

        return new HelloAckPayload(
            IpcProtocol.Version,
            AgentVersion,
            health.Degraded ? AgentMode.Degraded : AgentMode.Full,
            _runtime.DataRoot.DatabasePath,
            _runtime.SchemaVersion,
            Capabilities: [],
            SensorMap(health),
            new CatalogInfoPayload(_runtime.CatalogVersion, _runtime.CatalogVerified),
            _startedUtc);
    }

    private HealthPayload BuildHealth()
    {
        var health = _runtime.Collector.ReadHealth();

        return new HealthPayload
        {
            Ts = health.TsUtc,
            AgentCpuPct = 0d,
            AgentWs = Environment.WorkingSet,
            EventsLost = health.EventsLost,
            RingSeconds = health.RingSeconds,
            Sensors = SensorMap(health),
            BudgetOk = true,
            LiveApps = health.LiveApps,
            LiveInstances = health.LiveInstances,
            RowsWritten = health.RowsWritten,
            LiveDropped = health.LiveDropped,
            LateSamples = health.LateSamples,
            UnattributedInstances = health.UnattributedInstances,
            UnattributedEvents = health.UnattributedEvents,
            HandlerErrors = health.HandlerErrors,
        };
    }

    private static Dictionary<string, SensorStatePayload> SensorMap(CollectorHealth health) =>
        health.Sensors.ToDictionary(
            s => s.Name,
            s => new SensorStatePayload(s.Health.State.ToString(), s.Health.Detail),
            StringComparer.Ordinal);

    private ValueTask SendAckAsync(FrameWriter writer, long replyTo, CancellationToken cancellationToken) =>
        writer.WriteAsync(json => IpcEnvelope.Write(
            json, MessageType.Ack, NextId(), replyTo, new AckPayload(), IpcJsonContext.Default.AckPayload),
            cancellationToken);

    private ValueTask SendErrorAsync(
        FrameWriter writer,
        long replyTo,
        IpcErrorCode code,
        string message,
        CancellationToken cancellationToken) =>
        writer.WriteAsync(json => IpcEnvelope.Write(
            json, MessageType.Error, NextId(), replyTo, new ErrorPayload(code, message),
            IpcJsonContext.Default.ErrorPayload),
            cancellationToken);

    private long NextId() => Interlocked.Increment(ref _nextId);

    [LoggerMessage(EventId = 1530, Level = LogLevel.Error, Message = "A pipe connection faulted with {Error}")]
    private static partial void ConnectionFaulted(ILogger logger, string error);

    [LoggerMessage(EventId = 1531, Level = LogLevel.Information, Message = "A client asked the Agent to shut down")]
    private static partial void ShutdownRequested(ILogger logger);
}
