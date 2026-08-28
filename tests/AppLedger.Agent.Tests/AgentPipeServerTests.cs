using System.IO.Pipes;
using AppLedger.Agent.Hosting;
using AppLedger.Agent.Ipc;
using AppLedger.Collector;
using AppLedger.Collector.Processes;
using AppLedger.Core.Collection;
using AppLedger.Core.Identity;
using AppLedger.Core.Metrics;
using AppLedger.Core.Policy;
using AppLedger.Core.Process;
using AppLedger.Core.Time;
using AppLedger.Infrastructure.Storage;
using AppLedger.Ipc;
using AppLedger.Ipc.Framing;
using AppLedger.Ipc.Streams;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AppLedger.Agent.Tests;

/// <summary>
/// The pipe server over a real pipe. Uses the shipping transport rather than an in-memory fake, because the
/// pipe is cheap to create unelevated and a fake would be testing the fake.
/// </summary>
public sealed class AgentPipeServerTests : IAsyncLifetime, IDisposable
{
    private readonly string _pipeName = $"AppLedger.agent.test.{Guid.NewGuid():N}";
    private readonly CancellationTokenSource _cts = new();
    private readonly StubLifetime _lifetime = new();

    private AgentRuntime _runtime = null!;
    private AgentPipeServer _server = null!;
    private DataRoot _dataRoot = null!;
    private string _tempRoot = null!;

    public async Task InitializeAsync()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"appledger-agent-{Guid.NewGuid():N}");
        _dataRoot = new DataRoot(_tempRoot);
        _dataRoot.EnsureCreated();

        var database = new SqliteConnectionFactory(_dataRoot, DatabaseOptions.Agent);
        new SchemaMigrator(database, _dataRoot).Migrate();
        var repository = new MetricsRepository(database);

        var policy = AppLedger.Infrastructure.Policy.PolicyGuard.Create();
        var registry = new InstanceRegistry(
            policy,
            new AppLedger.Infrastructure.Process.ProcessEnricher(),
            new FallbackIdentityResolver(policy, new InstallRootHeuristic([@"C:\Program Files"])));

        var collector = new CollectorHost(
            new AppLedger.Infrastructure.Process.NtProcessSource(),
            registry,
            SystemClock.Instance,
            new CollectorOptions(),
            repository);

        _runtime = new AgentRuntime(collector, database, repository, _dataRoot, [], "test-catalog", true, 1);

        var transport = new NamedPipeServerTransport(
            NullLogger<NamedPipeServerTransport>.Instance,
            _pipeName,

            // Both ends are the test host here, so "installed beside us" means nothing. The verification
            // itself is covered where it can be: NamedPipeServerFactoryTests, over a real pipe.
            verifyPeer: false);

        _server = new AgentPipeServer(transport, _runtime, _lifetime, NullLogger<AgentPipeServer>.Instance);
        await _server.StartAsync(_cts.Token);
    }

    public async Task DisposeAsync()
    {
        await _cts.CancelAsync();
        await _server.StopAsync(CancellationToken.None);
        _server.Dispose();
        _runtime.Dispose();
        _cts.Dispose();

        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException)
        {
            // A SQLite handle that has not closed yet is not worth failing a test over.
        }
    }

    /// <summary>
    /// Present only because the analyzer requires it of a type holding disposables; xUnit's own teardown is
    /// <see cref="DisposeAsync"/>, which does the real work and runs first.
    /// </summary>
    public void Dispose()
    {
        _cts.Dispose();
        _server?.Dispose();
        _runtime?.Dispose();
    }

    private async Task<Client> ConnectAsync()
    {
        var stream = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await stream.ConnectAsync(timeout: 5_000);
        return new Client(stream);
    }

    [Fact]
    public async Task Hello_WithTheCurrentProtocol_IsAcknowledged()
    {
        using var client = await ConnectAsync();

        var ack = await client.HelloAsync();

        ack.Protocol.ShouldBe(IpcProtocol.Version);
        ack.Schema.ShouldBe(1);
        ack.Catalog.Version.ShouldBe("test-catalog");
        ack.StartedUtc.ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// Two values on the wire. Lite is the state where no Agent answered, so it can never appear in a
    /// HelloAck — there would be nobody to send one.
    /// </summary>
    [Fact]
    public async Task HelloAck_Mode_IsFullOrDegradedButNeverLite()
    {
        using var client = await ConnectAsync();

        var ack = await client.HelloAsync();

        ack.Mode.ShouldBeOneOf(AgentMode.Full, AgentMode.Degraded);
    }

    [Fact]
    public async Task Hello_WithAnotherProtocol_IsRefusedAsProtocolUnsupported()
    {
        using var client = await ConnectAsync();

        await client.SendAsync(MessageType.Hello, new HelloPayload(999, "test", "en"),
            IpcJsonContext.Default.HelloPayload);

        var (header, frame) = await client.ReadAsync();
        header.Type.ShouldBe(MessageType.Error);

        IpcEnvelope.TryReadPayload(frame, header, IpcJsonContext.Default.ErrorPayload, out var error).ShouldBeTrue();
        error!.Code.ShouldBe(IpcErrorCode.ProtocolUnsupported);
    }

    [Fact]
    public async Task Ping_IsAnsweredWithPongCarryingTheServerTime()
    {
        using var client = await ConnectAsync();
        await client.HelloAsync();

        await client.SendAsync(MessageType.Ping, new AckPayload(), IpcJsonContext.Default.AckPayload);

        var (header, frame) = await client.ReadAsync();
        header.Type.ShouldBe(MessageType.Pong);

        IpcEnvelope.TryReadPayload(frame, header, IpcJsonContext.Default.PongPayload, out var pong).ShouldBeTrue();
        pong!.ServerTimeUtc.ShouldBeGreaterThan(1_700_000_000);
    }

    [Fact]
    public async Task GetHealth_ReportsTheCollectorsCounters()
    {
        using var client = await ConnectAsync();
        await client.HelloAsync();

        await client.SendAsync(MessageType.GetHealth, new AckPayload(), IpcJsonContext.Default.AckPayload);

        var (header, frame) = await client.ReadAsync();
        header.Type.ShouldBe(MessageType.Health);

        IpcEnvelope.TryReadPayload(frame, header, IpcJsonContext.Default.HealthPayload, out var health).ShouldBeTrue();
        health!.Ts.ShouldBeGreaterThan(0);
        health.AgentWs.ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// A stream this build does not serve answers BadRequest rather than accepting a subscription that will
    /// never produce anything — which is the failure mode that looks like a hung UI.
    /// </summary>
    [Fact]
    public async Task Subscribe_ToAStreamThisBuildDoesNotServe_IsRefused()
    {
        using var client = await ConnectAsync();
        await client.HelloAsync();

        await client.SendAsync(MessageType.Subscribe, new SubscribePayload("connections", "cat:chrome"),
            IpcJsonContext.Default.SubscribePayload);

        var (header, frame) = await client.ReadAsync();
        header.Type.ShouldBe(MessageType.Error);

        IpcEnvelope.TryReadPayload(frame, header, IpcJsonContext.Default.ErrorPayload, out var error).ShouldBeTrue();
        error!.Code.ShouldBe(IpcErrorCode.BadRequest);
    }

    [Fact]
    public async Task Subscribe_ToApps_IsAcknowledged()
    {
        using var client = await ConnectAsync();
        await client.HelloAsync();

        await client.SendAsync(MessageType.Subscribe, new SubscribePayload(StreamHub.AppsStream),
            IpcJsonContext.Default.SubscribePayload);

        var (header, _) = await client.ReadAsync();
        header.Type.ShouldBe(MessageType.Ack);
    }

    /// <summary>
    /// A known name with no handler is BadRequest — a build too old to know the name at all never reaches
    /// the handler, because the envelope reader rejects it, and the UI reads that as "update required".
    /// </summary>
    [Fact]
    public async Task KnownButUnimplementedRequest_AnswersBadRequest()
    {
        using var client = await ConnectAsync();
        await client.HelloAsync();

        await client.SendAsync(MessageType.UpdateCatalog, new AckPayload(), IpcJsonContext.Default.AckPayload);

        var (header, frame) = await client.ReadAsync();
        header.Type.ShouldBe(MessageType.Error);

        IpcEnvelope.TryReadPayload(frame, header, IpcJsonContext.Default.ErrorPayload, out var error).ShouldBeTrue();
        error!.Code.ShouldBe(IpcErrorCode.BadRequest);
    }

    /// <summary>
    /// Any inbound frame counts as a UI watching. Missing this leaves the collector in the 2-second idle
    /// profile while a chart is being drawn, which reads as a choppy chart rather than as a pipe bug.
    /// </summary>
    [Fact]
    public async Task AnyRequest_TakesTheCollectorOutOfTheIdleProfile()
    {
        await _runtime.Collector.TickAsync();
        _runtime.Collector.IsIdle.ShouldBeTrue("nothing has connected yet");

        using var client = await ConnectAsync();
        await client.HelloAsync();

        await _runtime.Collector.TickAsync();
        _runtime.Collector.IsIdle.ShouldBeFalse();
    }

    [Fact]
    public async Task Shutdown_IsAcknowledgedAndStopsTheApplication()
    {
        using var client = await ConnectAsync();
        await client.HelloAsync();

        await client.SendAsync(MessageType.Shutdown, new ShutdownPayload("user"),
            IpcJsonContext.Default.ShutdownPayload);

        var (header, _) = await client.ReadAsync();
        header.Type.ShouldBe(MessageType.Ack);

        // The acknowledgement goes out before the host is told to stop - deliberately, so the client learns
        // the shutdown was accepted rather than seeing the pipe vanish. That ordering means the client can
        // observe the ack a moment before the server reaches the next statement, so this waits rather than
        // asserting instantly: a race in the test, not in the server.
        await WaitForAsync(() => _lifetime.Stopped);
        _lifetime.Stopped.ShouldBeTrue();
    }

    [Fact]
    public async Task TwoClients_AreBothServed()
    {
        using var first = await ConnectAsync();
        using var second = await ConnectAsync();

        (await first.HelloAsync()).Protocol.ShouldBe(IpcProtocol.Version);
        (await second.HelloAsync()).Protocol.ShouldBe(IpcProtocol.Version);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition() && !timeout.IsCancellationRequested)
        {
            await Task.Delay(10, CancellationToken.None);
        }
    }

    /// <summary>A one-frame protocol client, so the tests read like the conversation they describe.</summary>
    private sealed class Client(NamedPipeClientStream stream) : IDisposable
    {
        private readonly FrameReader _reader = new(stream, IpcProtocol.MaxInboundFrameBytes);
        private readonly FrameWriter _writer = new(stream);
        private long _id;

        internal async Task<HelloAckPayload> HelloAsync()
        {
            await SendAsync(
                MessageType.Hello,
                new HelloPayload(IpcProtocol.Version, "AppLedger.Agent.Tests", "en"),
                IpcJsonContext.Default.HelloPayload);

            var (header, frame) = await ReadAsync();
            header.Type.ShouldBe(MessageType.HelloAck);

            IpcEnvelope.TryReadPayload(frame, header, IpcJsonContext.Default.HelloAckPayload, out var ack)
                .ShouldBeTrue();
            return ack!;
        }

        internal ValueTask SendAsync<T>(
            MessageType type,
            T payload,
            System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
            _writer.WriteAsync(json => IpcEnvelope.Write(json, type, ++_id, null, payload, info));

        internal async Task<(IpcHeader Header, byte[] Frame)> ReadAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            (await _reader.ReadAsync(timeout.Token)).ShouldBe(FrameStatus.Frame);

            // Copied out of the reader's buffer: the caller deserializes after the next read may have run.
            var frame = _reader.Payload.ToArray();
            IpcEnvelope.TryReadHeader(frame, out var header).ShouldBeTrue();
            return (header, frame);
        }

        public void Dispose()
        {
            _reader.Dispose();
            _writer.Dispose();
            stream.Dispose();
        }
    }

    private sealed class StubLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        internal bool Stopped { get; private set; }

        public void StopApplication() => Stopped = true;
    }
}
