using AppLedger.Agent.Hosting;
using AppLedger.Agent.Ipc;
using AppLedger.Agent.Tasks;
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
                return InstallTask();

            case "--remove-task":
                return await RemoveTaskAsync().ConfigureAwait(false);

            case "--status":
                return await StatusAsync().ConfigureAwait(false);

            default:
                Console.Error.WriteLine($"Unknown command '{args[0]}'.");
                Console.Error.WriteLine(Usage);
                return 1;
        }
    }

    /// <summary>
    /// Writes the task XML under <c>DataRoot</c>, registers it, and starts it. Needs elevation, which the UI
    /// arranges by launching this with the <c>runas</c> verb (docs/01_ARCHITECTURE.md §Elevation strategy).
    /// </summary>
    private static int InstallTask()
    {
        var dataRoot = DataRoot.Default;
        dataRoot.EnsureCreated();

        string xmlPath;
        try
        {
            xmlPath = AgentTaskDefinition.Write(
                Path.Combine(dataRoot.Root, "task"),
                AgentTaskDefinition.BuildXmlForCurrentUser());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine($"The task definition could not be written: {ex.GetType().Name}");
            return 4;
        }

        var created = SchTasks.Create(xmlPath);
        if (!created.Succeeded)
        {
            // The XML stays on disk deliberately: it is the first thing worth looking at when Task Scheduler
            // refuses a registration, and it is under DataRoot rather than %TEMP% so it survives.
            Console.Error.WriteLine(SchTasks.Describe(created));
            Console.Error.WriteLine($"The submitted definition is at {xmlPath}");
            return 5;
        }

        var started = SchTasks.Run();
        if (!started.Succeeded)
        {
            // Registered but not started is a real state, and a different one: the task will come up at the
            // next logon regardless, so this is a warning rather than a failure.
            Console.Out.WriteLine($"The task was created but did not start: {SchTasks.Describe(started)}");
            return 6;
        }

        Console.Out.WriteLine($"The '{AgentTaskDefinition.TaskName}' task is installed and running.");
        return 0;
    }

    /// <summary>
    /// Asks a running Agent to stop, then deletes the task. Needs elevation for the delete.
    /// </summary>
    private static async Task<int> RemoveTaskAsync()
    {
        // Stopping first means the Agent flushes its partial minute and closes the database cleanly, rather
        // than being torn down with the task underneath it.
        var stopped = await AgentControlClient
            .ShutdownAsync("user", TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);

        Console.Out.WriteLine(stopped
            ? "A running Agent acknowledged the shutdown."
            : "No running Agent answered; deleting the task anyway.");

        var deleted = SchTasks.Delete();
        if (!deleted.Succeeded)
        {
            Console.Error.WriteLine(SchTasks.Describe(deleted));
            return 5;
        }

        Console.Out.WriteLine($"The '{AgentTaskDefinition.TaskName}' task is removed.");
        return 0;
    }

    /// <summary>
    /// Prints the task's state and whether an Agent is answering the pipe. Needs no elevation, which is what
    /// makes it usable from the UI and from a support conversation.
    /// </summary>
    /// <remarks>
    /// The exit code is the machine-readable part, and each value is a different decision for the caller:
    /// start the task, offer setup, or fall back to Lite mode.
    /// </remarks>
    private static async Task<int> StatusAsync()
    {
        var state = SchTasks.QueryState();
        var status = await AgentControlClient.QueryAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

        Console.Out.WriteLine($"task:  {state ?? "not installed"}");
        Console.Out.WriteLine(status.Reachable
            ? $"agent: reachable, version {status.Version}, mode {status.Mode}"
            : "agent: not reachable");

        return (state, status.Reachable) switch
        {
            (null, _) => 7,          // no task: the UI offers Agent setup
            (_, true) => 0,          // installed and answering
            _ => 8,                  // installed but silent: the UI offers to start it
        };
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
