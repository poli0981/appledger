using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace S1.EtwBudget;

/// <summary>
/// S1-lite: the ETW pre-flight from docs/20_SPIKES.md. Opens the two real sessions with the keyword set of
/// docs/05_COLLECTOR.md, counts events without accumulating anything, and samples its own cost every 10 s.
/// It answers one question before any Collector code exists: can a managed process hold these sessions at all,
/// losslessly, inside the budget of docs/01_ARCHITECTURE.md?
/// </summary>
internal static class Program
{
    private const string KernelSessionName = "AppLedger-Kernel";
    private const string UserSessionName = "AppLedger-User";
    private const string DnsProvider = "Microsoft-Windows-DNS-Client";
    private const int SampleSeconds = 10;

    // Handlers run on TraceEvent's own threads and must not allocate: counters only.
    private static long _netEvents;
    private static long _diskEvents;
    private static long _procEvents;
    private static long _imageEvents;
    private static long _dnsEvents;
    private static long _handlerErrors;

    private static async Task<int> Main(string[] args)
    {
        if (!IsElevated())
        {
            Console.Error.WriteLine("S1 needs an elevated terminal: creating a system logger requires administrator rights.");
            return 2;
        }

        Console.Out.WriteLine($"S1       runtime={RuntimeInformation.FrameworkDescription}  arch={RuntimeInformation.ProcessArchitecture}");

        using var cancelled = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancelled.Cancel(); };

        // Two legs, one exe. --hours runs the shipping pipeline against a temporary database; --minutes runs
        // the ETW pre-flight that counts events without accumulating them. The pre-flight stays because it is
        // the thing to re-run first when the full leg misses the budget: it says whether the cost is ETW's
        // floor or ours.
        if (ArgValue(args, "--hours") is { } h)
        {
            if (!double.TryParse(h, CultureInfo.InvariantCulture, out var hours) || hours <= 0)
            {
                Console.Error.WriteLine("--hours needs a positive number of hours.");
                return 2;
            }

            ReclaimStaleSessions();

            return await FullRun.RunAsync(hours, ArgValue(args, "--out") ?? "s1.csv", cancelled.Token)
                .ConfigureAwait(false);
        }

        return await LiteRunAsync(args, cancelled.Token).ConfigureAwait(false);
    }

    private static async Task<int> LiteRunAsync(string[] args, CancellationToken cancellationToken)
    {
        var minutes = ArgValue(args, "--minutes") is { } m && int.TryParse(m, CultureInfo.InvariantCulture, out var mm) ? mm : 45;
        var outPath = ArgValue(args, "--out") ?? "s1-lite.csv";

        Console.Out.WriteLine($"S1-lite  minutes={minutes}  out={outPath}");

        ReclaimStaleSessions();

        using var kernel = new TraceEventSession(KernelSessionName) { StopOnDispose = true, BufferSizeMB = 64, BufferQuantumKB = 1024 };
        using var user = new TraceEventSession(UserSessionName) { StopOnDispose = true, BufferSizeMB = 16 };

        try
        {
            // Thread is required so DiskIO's IssuingThreadId can be resolved to a process (docs/04 §D).
            kernel.EnableKernelProvider(
                KernelTraceEventParser.Keywords.Process
                | KernelTraceEventParser.Keywords.Thread
                | KernelTraceEventParser.Keywords.ImageLoad
                | KernelTraceEventParser.Keywords.NetworkTCPIP
                | KernelTraceEventParser.Keywords.DiskIO);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: could not enable the kernel provider ({ex.GetType().Name}: {ex.Message}).");
            Console.Error.WriteLine("      This is the ADR-3 question. Record it in docs/20_SPIKES.md before writing Collector code.");
            return 3;
        }

        user.EnableProvider(DnsProvider, TraceEventLevel.Informational);

        WireKernelHandlers(kernel.Source.Kernel);
        user.Source.Dynamic.All += _ => Count(ref _dnsEvents);

        var kernelPump = RunPump(kernel, "kernel");
        var userPump = RunPump(user, "user");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(minutes));
        Console.Out.WriteLine("         sessions live; Ctrl+C stops early.\n");

        var summary = await SampleLoopAsync(kernel, user, outPath, cts.Token).ConfigureAwait(false);

        kernel.Stop();
        user.Stop();
        await Task.WhenAny(Task.WhenAll(kernelPump, userPump), Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None))
            .ConfigureAwait(false);

        return Report(summary, minutes);
    }

    private static void WireKernelHandlers(KernelTraceEventParser k)
    {
        k.TcpIpSend += _ => Count(ref _netEvents);
        k.TcpIpRecv += _ => Count(ref _netEvents);
        k.TcpIpSendIPV6 += _ => Count(ref _netEvents);
        k.TcpIpRecvIPV6 += _ => Count(ref _netEvents);
        k.UdpIpSend += _ => Count(ref _netEvents);
        k.UdpIpRecv += _ => Count(ref _netEvents);
        k.UdpIpSendIPV6 += _ => Count(ref _netEvents);
        k.UdpIpRecvIPV6 += _ => Count(ref _netEvents);

        k.DiskIORead += _ => Count(ref _diskEvents);
        k.DiskIOWrite += _ => Count(ref _diskEvents);
        k.DiskIOFlushBuffers += _ => Count(ref _diskEvents);

        k.ProcessStart += _ => Count(ref _procEvents);
        k.ProcessStop += _ => Count(ref _procEvents);

        k.ImageLoad += _ => Count(ref _imageEvents);
    }

    private static void Count(ref long counter) => Interlocked.Increment(ref counter);

    private static Task RunPump(TraceEventSession session, string label) => Task.Factory.StartNew(
        () =>
        {
            try
            {
                session.Source.Process();
            }
#pragma warning disable CA1031 // a spike must survive a bad handler and report it, not crash the run
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Interlocked.Increment(ref _handlerErrors);
                Console.Error.WriteLine($"[{label}] pump stopped: {ex.GetType().Name}: {ex.Message}");
            }
        },
        CancellationToken.None,
        TaskCreationOptions.LongRunning,
        TaskScheduler.Default);

    private static async Task<Summary> SampleLoopAsync(
        TraceEventSession kernel, TraceEventSession user, string outPath, CancellationToken token)
    {
        var self = Process.GetCurrentProcess();
        var cpuCount = Environment.ProcessorCount;
        var started = Stopwatch.StartNew();

        await using var csv = new StreamWriter(outPath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        await csv.WriteLineAsync(
            "elapsed_s,cpu_pct,cpu_ms_total,private_ws_mb,gc0,gc1,gc2," +
            "kernel_events_lost,user_events_lost,net_events,disk_events,proc_events,image_events,dns_events,handler_errors")
            .ConfigureAwait(false);

        var lastCpu = self.TotalProcessorTime;
        var lastElapsed = TimeSpan.Zero;
        var summary = new Summary();
        var window = new Queue<double>();

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(SampleSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                self.Refresh();
                var elapsed = started.Elapsed;
                var cpu = self.TotalProcessorTime;
                var cpuPct = (cpu - lastCpu).TotalSeconds / ((elapsed - lastElapsed).TotalSeconds * cpuCount) * 100.0;
                lastCpu = cpu;
                lastElapsed = elapsed;

                var wsMb = self.PrivateMemorySize64 / (1024.0 * 1024.0);

                // 5-minute rolling average is the shape the budget in docs/01 is stated in.
                window.Enqueue(cpuPct);
                while (window.Count > 300 / SampleSeconds)
                {
                    window.Dequeue();
                }

                var rolling = window.Average();

                summary.Observe(rolling, wsMb, kernel.EventsLost, user.EventsLost);

                await csv.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                    $"{elapsed.TotalSeconds:F0},{cpuPct:F3},{cpu.TotalMilliseconds:F0},{wsMb:F1}," +
                    $"{GC.CollectionCount(0)},{GC.CollectionCount(1)},{GC.CollectionCount(2)}," +
                    $"{kernel.EventsLost},{user.EventsLost},{Volatile.Read(ref _netEvents)},{Volatile.Read(ref _diskEvents)}," +
                    $"{Volatile.Read(ref _procEvents)},{Volatile.Read(ref _imageEvents)},{Volatile.Read(ref _dnsEvents)}," +
                    $"{Volatile.Read(ref _handlerErrors)}")).ConfigureAwait(false);

                Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  {elapsed.TotalMinutes,5:F1} min  cpu {cpuPct,6:F2}% (5-min avg {rolling,5:F2}%)  ws {wsMb,6:F1} MB  " +
                    $"lost {kernel.EventsLost}/{user.EventsLost}  net {Volatile.Read(ref _netEvents)}  disk {Volatile.Read(ref _diskEvents)}  dns {Volatile.Read(ref _dnsEvents)}"));
            }
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C or the --minutes budget elapsed: both are normal ends.
        }

        return summary;
    }

    private static int Report(Summary s, int minutes)
    {
        Console.Out.WriteLine("\n--- S1-lite result -----------------------------------------------");
        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"peak 5-min CPU average : {s.PeakRollingCpu:F2} %   (budget < 1 % idle, < 3 % under load)"));
        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"peak private WS        : {s.PeakWsMb:F1} MB  (budget < 100 MB)"));
        Console.Out.WriteLine($"events lost            : kernel {s.KernelLost}, user {s.UserLost}  (budget 0 at normal load)");
        Console.Out.WriteLine($"handler errors         : {Summary.HandlerErrors}  (budget 0)");
        Console.Out.WriteLine($"events seen            : net {_netEvents}, disk {_diskEvents}, proc {_procEvents}, image {_imageEvents}, dns {_dnsEvents}");

        var pass = s.PeakRollingCpu < 3.0 && s.PeakWsMb < 100.0 && s.KernelLost == 0 && s.UserLost == 0 && Summary.HandlerErrors == 0;
        var sawTraffic = _netEvents > 0 && _diskEvents > 0 && _procEvents > 0;
        if (!sawTraffic)
        {
            Console.Out.WriteLine("\nINCONCLUSIVE: some sensors saw no events at all - re-run with real activity in the window.");
            return 4;
        }

        Console.Out.WriteLine(pass
            ? $"\nPASS over {minutes} min. Paste these numbers into docs/20_SPIKES.md §Status (S1-lite row)."
            : "\nFAIL. Apply the reductions listed under S1 in docs/20_SPIKES.md and record the finding in docs/24_ADR.md.");
        return pass ? 0 : 1;
    }

    private static void ReclaimStaleSessions()
    {
        foreach (var name in TraceEventSession.GetActiveSessionNames())
        {
            if (!name.StartsWith("AppLedger-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Console.Out.WriteLine($"         reclaiming stale session '{name}'");
            TraceEventSession.GetActiveSession(name)?.Stop();
        }
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string? ArgValue(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private sealed class Summary
    {
        public double PeakRollingCpu { get; private set; }
        public double PeakWsMb { get; private set; }
        public int KernelLost { get; private set; }
        public int UserLost { get; private set; }
        public static long HandlerErrors => Volatile.Read(ref _handlerErrors);

        public void Observe(double rollingCpu, double wsMb, int kernelLost, int userLost)
        {
            PeakRollingCpu = Math.Max(PeakRollingCpu, rollingCpu);
            PeakWsMb = Math.Max(PeakWsMb, wsMb);
            KernelLost = Math.Max(KernelLost, kernelLost);
            UserLost = Math.Max(UserLost, userLost);
        }
    }
}
