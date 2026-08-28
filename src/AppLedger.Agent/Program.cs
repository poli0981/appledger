using AppLedger.Agent.Hosting;
using AppLedger.Agent.Ipc;
using AppLedger.Infrastructure.Storage;
using AppLedger.Ipc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

namespace AppLedger.Agent;

/// <summary>
/// Entry point for AppLedger.Agent.exe (docs/16_PACKAGING_AND_UPDATES.md §Agent CLI).
/// </summary>
internal static class Program
{
    /// <summary>
    /// Only one Agent may collect at a time: two would fight over the ETW session names and write the same
    /// minute twice. <c>--console</c> deliberately skips it so a developer can run one beside the task.
    /// </summary>
    private const string SingleInstanceMutex = @"Global\AppLedger.Agent";

    private const string Usage = """
        AppLedger.Agent - background collector, elevated through the "AppLedger Agent" Scheduled Task.

        Usage: AppLedger.Agent.exe <command>

          --serve         run collector + pipe server (how the task starts it)
          --install-task  write the task XML, create and start the task (needs elevation)
          --remove-task   stop the agent, delete the task (needs elevation)
          --status        print task state and pipe reachability (no elevation)
          --console       --serve with console logging and no single-instance mutex (dev only)
        """;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Out.WriteLine(Usage);
            return 1;
        }

        switch (args[0])
        {
            case "--help":
            case "-h":
            case "/?":
                Console.Out.WriteLine(Usage);
                return 0;

            case "--serve":
                return await ServeAsync(withConsole: false, useMutex: true).ConfigureAwait(false);

            case "--console":
                return await ServeAsync(withConsole: true, useMutex: false).ConfigureAwait(false);

            case "--install-task":
            case "--remove-task":
            case "--status":
                Console.Error.WriteLine(
                    $"'{args[0]}' is not implemented yet - the Scheduled Task CLI is the next slice (docs/21_ROADMAP.md).");
                return 2;

            default:
                Console.Error.WriteLine($"Unknown command '{args[0]}'.");
                Console.Error.WriteLine(Usage);
                return 1;
        }
    }

    private static async Task<int> ServeAsync(bool withConsole, bool useMutex)
    {
        using var single = useMutex ? new Mutex(initiallyOwned: false, SingleInstanceMutex, out _) : null;
        if (single is not null && !single.WaitOne(TimeSpan.Zero))
        {
            // Task Scheduler's MultipleInstancesPolicy is IgnoreNew, but a hand-started Agent can still
            // race the task. Exiting quietly is the right answer: the other instance is already collecting.
            Console.Error.WriteLine("Another AppLedger Agent is already running.");
            return 3;
        }

        var dataRoot = DataRoot.Default;
        dataRoot.EnsureCreated();

        Log.Logger = AgentLogging.Create(dataRoot);

        try
        {
            using var loggerFactory = new SerilogLoggerFactory(Log.Logger);

            // Built before the host so a failure here - a corrupt database, a rejected catalog - is reported
            // as itself rather than as a dependency-injection error three frames deep.
            using var runtime = AgentComposition.Build(loggerFactory);

            var builder = Host.CreateApplicationBuilder();
            builder.Logging.ClearProviders();
            if (withConsole)
            {
                builder.Logging.AddConsole();
            }

            builder.Logging.AddSerilog(Log.Logger);

            builder.Services.AddSingleton(runtime);
            builder.Services.AddSingleton<IServerTransport>(sp => new NamedPipeServerTransport(
                sp.GetRequiredService<ILogger<NamedPipeServerTransport>>()));

            // Order matters at startup: the collector is producing before the pipe server offers to stream
            // it, so a UI that connects in the first second gets data rather than an empty channel
            // (docs/01_ARCHITECTURE.md §Elevation strategy, step 2).
            builder.Services.AddHostedService<CollectorWorker>();
            builder.Services.AddHostedService<AgentPipeServer>();

            await builder.Build().RunAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "The Agent could not start");
            Console.Error.WriteLine($"The Agent could not start: {ex.GetType().Name}");
            return 4;
        }
        finally
        {
            await Log.CloseAndFlushAsync().ConfigureAwait(false);
            single?.ReleaseMutex();
        }
    }
}
