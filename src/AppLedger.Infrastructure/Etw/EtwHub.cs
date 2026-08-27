using System.Net;
using AppLedger.Core.Collection;
using AppLedger.Core.Net;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Logging;

namespace AppLedger.Infrastructure.Etw;

/// <summary>
/// The two real-time ETW sessions AppLedger consumes, and the translation from TraceEvent's payload types
/// into the plain event records the Collector understands (docs/05_COLLECTOR.md §ETW sessions).
/// </summary>
/// <remarks>
/// <b>Observation only.</b> Every provider here is a read of what the kernel already reports; nothing is
/// injected, no stack walks are enabled, and no handle is opened to any process (ADR-4). Enabling a kernel
/// session needs administrator rights, so the whole class degrades to
/// <see cref="SensorState.Unavailable"/> rather than throwing when the Agent is not elevated — Lite mode
/// runs exactly this way and must not see an exception.
/// <para>
/// <b>Handlers are the hot path.</b> They run on TraceEvent's own <c>Process()</c> threads at the ~12 k
/// events/second docs/05 anticipates, so they do the minimum: read a few fields, translate, hand off. They
/// never throw back into TraceEvent's loop, never allocate in steady state beyond the address objects the
/// payload forces, and never touch a lock the snapshot thread holds for long.
/// </para>
/// </remarks>
public sealed partial class EtwHub : IEtwSource, IDisposable
{
    /// <summary>The kernel session name. Fixed so a crashed Agent's session can be reclaimed by name.</summary>
    public const string KernelSessionName = "AppLedger-Kernel";

    /// <summary>The user-provider session name.</summary>
    public const string UserSessionName = "AppLedger-User";

    /// <summary>The DNS-Client provider (docs/04_DATA_SOURCES.md §D).</summary>
    public const string DnsProviderName = "Microsoft-Windows-DNS-Client";

    /// <summary>Event 3008: a query completed and carries its answers.</summary>
    private const int DnsQueryCompletedEventId = 3008;

    /// <summary>Event 3020: the answer came from the cache. Same payload shape.</summary>
    private const int DnsCachedEventId = 3020;

    private const int StartAttempts = 3;

    private readonly ILogger<EtwHub> _logger;
    private readonly TimeSpan _retryDelay;

    private TraceEventSession? _kernel;
    private TraceEventSession? _user;
    private Thread? _kernelThread;
    private Thread? _userThread;

    /// <summary>
    /// Bumped by every <c>Stop</c>. A processing thread captures it at creation and compares on exit, which
    /// is how "we asked for this" is told apart from "the session died under us" without a flag that has to
    /// be cleared at exactly the right moment.
    /// </summary>
    private int _generation;

    /// <summary>Creates the hub.</summary>
    /// <param name="logger">Structured sink. Nothing logged here carries a host, a path or a command line.</param>
    /// <param name="retryDelay">
    /// Backoff between the three start attempts docs/05 specifies. Injected so a test does not wait fifteen
    /// seconds to observe a failure.
    /// </param>
    public EtwHub(ILogger<EtwHub> logger, TimeSpan? retryDelay = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(5);
    }

    /// <inheritdoc />
    public string Name => "EtwHub";

    /// <inheritdoc />
    public SensorHealth Health { get; private set; } = SensorHealth.Stopped;

    /// <inheritdoc />
    public event Action<NetworkEvent>? Network;

    /// <inheritdoc />
    public event Action<DiskIoEvent>? DiskIo;

    /// <inheritdoc />
    public event Action<DnsEvent>? Dns;

    /// <inheritdoc />
    public event Action<ImageLoadEvent>? ImageLoad;

    /// <inheritdoc />
    public long EventsLost =>
        (_kernel?.Source?.EventsLost ?? 0) + (_user?.Source?.EventsLost ?? 0);

    /// <summary>True when the current process can create a kernel session at all.</summary>
    public static bool CanCreateSessions => TraceEventSession.IsElevated() == true;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!CanCreateSessions)
        {
            // Not a failure. Lite mode runs here by design, and the UI says which numbers are missing
            // rather than showing zeros (docs/01 §Degraded modes).
            Health = SensorHealth.Unavailable("not elevated");
            LogNotElevated(_logger);
            return;
        }

        Health = new SensorHealth(SensorState.Starting);

        for (var attempt = 1; attempt <= StartAttempts; attempt++)
        {
            try
            {
                ReclaimStaleSessions();
                StartKernelSession();
                StartUserSession();

                Health = new SensorHealth(SensorState.Running);
                LogStarted(_logger);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // ERROR_NO_SYSTEM_RESOURCES (1450) means the eight-system-logger limit is reached, and
                // ERROR_ALREADY_EXISTS means a session survived a crash. Both are worth retrying once the
                // reclaim has had a moment to take effect.
                Stop();
                LogStartAttemptFailed(_logger, attempt, ex.GetType().Name);

                if (attempt == StartAttempts)
                {
                    Health = SensorHealth.Unavailable(ex.GetType().Name);
                    return;
                }

                await Task.Delay(_retryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Stop();
        Health = SensorHealth.Stopped;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() => Stop();

    /// <summary>
    /// Stops any session left behind by a previous run. A crashed Agent leaves its session running, and
    /// creating one with a name already in use fails — so the reclaim is what makes a restart work
    /// (docs/05 §ETW sessions).
    /// </summary>
    private void ReclaimStaleSessions()
    {
        foreach (var name in TraceEventSession.GetActiveSessionNames())
        {
            if (name is not (KernelSessionName or UserSessionName))
            {
                continue;
            }

            LogReclaimingSession(_logger, name);
            using var stale = new TraceEventSession(name, TraceEventSessionOptions.Attach);
            stale.Stop(noThrow: true);
        }
    }

    private void StartKernelSession()
    {
        _kernel = new TraceEventSession(KernelSessionName)
        {
            StopOnDispose = true,
            BufferSizeMB = 64,
            BufferQuantumKB = 1024,
        };

        // Thread is enabled because DiskIO's issuing thread is what resolves an event to a process; without
        // it every disk event would be unattributable (docs/04 §D).
        // FileIO and FileIOInit are deliberately absent: they are the noisy keywords, toggled only during a
        // sampling window (docs/05 §FileIO sampling windows).
        _kernel.EnableKernelProvider(
            KernelTraceEventParser.Keywords.Process
            | KernelTraceEventParser.Keywords.Thread
            | KernelTraceEventParser.Keywords.ImageLoad
            | KernelTraceEventParser.Keywords.NetworkTCPIP
            | KernelTraceEventParser.Keywords.DiskIO);

        var kernelParser = _kernel.Source.Kernel;

        kernelParser.TcpIpSend += data => OnTcp(data.ProcessID, data.size, NetworkDirection.Outbound, data.saddr, data.daddr, data.dport, data.TimeStamp);
        kernelParser.TcpIpRecv += data => OnTcp(data.ProcessID, data.size, NetworkDirection.Inbound, data.saddr, data.daddr, data.dport, data.TimeStamp);
        kernelParser.TcpIpSendIPV6 += data => OnTcp(data.ProcessID, data.size, NetworkDirection.Outbound, data.saddr, data.daddr, data.dport, data.TimeStamp);
        kernelParser.TcpIpRecvIPV6 += data => OnTcp(data.ProcessID, data.size, NetworkDirection.Inbound, data.saddr, data.daddr, data.dport, data.TimeStamp);

        kernelParser.UdpIpSend += data => OnUdp(data.ProcessID, data.size, NetworkDirection.Outbound, data.saddr, data.daddr, data.dport, data.TimeStamp);
        kernelParser.UdpIpRecv += data => OnUdp(data.ProcessID, data.size, NetworkDirection.Inbound, data.saddr, data.daddr, data.dport, data.TimeStamp);

        // TraceEvent spells the IPv6 UDP payload type "UpdIpV6TraceData" - a typo in the library, not here.
        kernelParser.UdpIpSendIPV6 += data => OnUdp(data.ProcessID, data.size, NetworkDirection.Outbound, data.saddr, data.daddr, data.dport, data.TimeStamp);
        kernelParser.UdpIpRecvIPV6 += data => OnUdp(data.ProcessID, data.size, NetworkDirection.Inbound, data.saddr, data.daddr, data.dport, data.TimeStamp);

        kernelParser.DiskIORead += data => OnDisk(data, isWrite: false);
        kernelParser.DiskIOWrite += data => OnDisk(data, isWrite: true);

        kernelParser.ImageLoad += OnImageLoad;

        _kernelThread = StartProcessingThread(_kernel, "AppLedger.Etw.Kernel");
    }

    private void StartUserSession()
    {
        _user = new TraceEventSession(UserSessionName)
        {
            StopOnDispose = true,
            BufferSizeMB = 16,
        };

        _user.EnableProvider(DnsProviderName, TraceEventLevel.Informational);

        // The DNS-Client provider has no strongly typed parser in TraceEvent, so the dynamic parser is
        // filtered by event id rather than by shape.
        _user.Source.Dynamic.All += OnDynamic;

        _userThread = StartProcessingThread(_user, "AppLedger.Etw.User");
    }

    /// <summary>
    /// Runs one session's <c>Process()</c> loop on its own thread, and treats <b>any</b> exit from it as
    /// the sensor going down.
    /// </summary>
    /// <remarks>
    /// Two ways out, and both had to be handled. An unguarded <c>Process()</c> that <i>throws</i> takes the
    /// whole process with it, because an exception on a background thread is unhandled by definition — that
    /// killed the test host the first time these sessions ran for real.
    /// <para>
    /// But <c>Process()</c> also <i>returns normally</i> when the session is stopped cleanly: someone runs
    /// <c>logman stop</c>, another instance reclaims the name, a policy tears it down. Catching only the
    /// throw left the hub reporting <see cref="SensorState.Running"/> forever while collecting nothing —
    /// an Agent that looks healthy and silently records no network or disk bytes for the rest of the
    /// session. That is the worse of the two failures, because a crash is at least visible.
    /// </para>
    /// <para>
    /// The generation counter is what separates "our own shutdown" from "the session died". A boolean flag
    /// cannot: <c>Stop</c> would have to clear it while a slow thread might still be inside its own
    /// <c>finally</c>, and the thread would then report a fault that was really a clean stop.
    /// </para>
    /// </remarks>
    private Thread StartProcessingThread(TraceEventSession session, string name)
    {
        var generation = Volatile.Read(ref _generation);

        var thread = new Thread(() =>
        {
            string? failure = null;

            try
            {
                session.Source.Process();
            }
            catch (Exception ex)
            {
                failure = ex.GetType().Name;
            }

            // Still the current run? Then nobody asked for this, and the sensor is genuinely down.
            if (Volatile.Read(ref _generation) != generation)
            {
                return;
            }

            Health = SensorHealth.Unavailable(failure ?? "session ended");
            LogProcessingStopped(_logger, name, failure ?? "the session was stopped externally");
        })
        {
            IsBackground = true,
            Name = name,
        };

        thread.Start();
        return thread;
    }

    private void OnTcp(
        int processId,
        int size,
        NetworkDirection direction,
        IPAddress local,
        IPAddress remote,
        int remotePort,
        DateTime timestamp) =>
        Raise(processId, size, direction, isTcp: true, local, remote, remotePort, timestamp);

    private void OnUdp(
        int processId,
        int size,
        NetworkDirection direction,
        IPAddress local,
        IPAddress remote,
        int remotePort,
        DateTime timestamp) =>
        Raise(processId, size, direction, isTcp: false, local, remote, remotePort, timestamp);

    private void Raise(
        int processId,
        int size,
        NetworkDirection direction,
        bool isTcp,
        IPAddress local,
        IPAddress remote,
        int remotePort,
        DateTime timestamp)
    {
        var handler = Network;
        if (handler is null)
        {
            return;
        }

        handler(new NetworkEvent(
            processId,
            size,
            direction,
            NetworkEvent.Classify(isTcp, local, remote, remotePort),
            remote,
            remotePort,
            ToEpochSeconds(timestamp)));
    }

    private void OnDisk(DiskIOTraceData data, bool isWrite) =>
        DiskIo?.Invoke(new DiskIoEvent(
            data.ProcessID,
            data.TransferSize,
            isWrite,
            data.DiskNumber,
            ToEpochSeconds(data.TimeStamp)));

    private void OnImageLoad(ImageLoadTraceData data) =>
        ImageLoad?.Invoke(new ImageLoadEvent(data.ProcessID, data.FileName, ToEpochSeconds(data.TimeStamp)));

    private void OnDynamic(TraceEvent data)
    {
        var handler = Dns;
        if (handler is null)
        {
            return;
        }

        var id = (int)data.ID;
        if (id is not (DnsQueryCompletedEventId or DnsCachedEventId))
        {
            return;
        }

        var name = data.PayloadByName("QueryName") as string;
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        var addresses = DnsResultsParser.ParseAddresses(data.PayloadByName("QueryResults") as string);
        if (addresses.Count == 0)
        {
            return;
        }

        handler(new DnsEvent(data.ProcessID, name, addresses, ToEpochSeconds(data.TimeStamp)));
    }

    /// <summary>
    /// ETW timestamps arrive as local <see cref="DateTime"/>. Everything stored is UTC epoch seconds
    /// (docs/06_DATA_MODEL.md §Time), and converting here keeps the assumption in one place.
    /// </summary>
    private static long ToEpochSeconds(DateTime timestamp) =>
        new DateTimeOffset(timestamp.ToUniversalTime(), TimeSpan.Zero).ToUnixTimeSeconds();

    private void Stop()
    {
        // Tells every thread from the current run that whatever it is about to see was asked for.
        Interlocked.Increment(ref _generation);

        // StopProcessing before Dispose: it asks the loop to return, where Dispose pulls the session out
        // from under a thread that is still inside it. Both end up in the same place, but only one of them
        // does so without an exception.
        StopProcessing(_user);
        StopProcessing(_kernel);

        _user?.Dispose();
        _user = null;
        _kernel?.Dispose();
        _kernel = null;

        _userThread?.Join(TimeSpan.FromSeconds(2));
        _kernelThread?.Join(TimeSpan.FromSeconds(2));
        _userThread = null;
        _kernelThread = null;
    }

    private static void StopProcessing(TraceEventSession? session)
    {
        try
        {
            session?.Source?.StopProcessing();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The loop had already finished. Nothing to stop, and nothing worth reporting.
        }
    }

    [LoggerMessage(
        EventId = 1400,
        Level = LogLevel.Information,
        Message = "ETW is unavailable because this process is not elevated; live network, disk and DNS attribution are off.")]
    private static partial void LogNotElevated(ILogger logger);

    [LoggerMessage(EventId = 1401, Level = LogLevel.Information, Message = "ETW sessions started.")]
    private static partial void LogStarted(ILogger logger);

    [LoggerMessage(EventId = 1402, Level = LogLevel.Warning, Message = "ETW start attempt {Attempt} failed: {Error}.")]
    private static partial void LogStartAttemptFailed(ILogger logger, int attempt, string error);

    [LoggerMessage(EventId = 1403, Level = LogLevel.Information, Message = "Reclaiming a stale ETW session named {Session}.")]
    private static partial void LogReclaimingSession(ILogger logger, string session);

    [LoggerMessage(
        EventId = 1404,
        Level = LogLevel.Warning,
        Message = "The {Session} ETW processing loop ended unexpectedly: {Error}. That sensor is now unavailable.")]
    private static partial void LogProcessingStopped(ILogger logger, string session, string error);
}
