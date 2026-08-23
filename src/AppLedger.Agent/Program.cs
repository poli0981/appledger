namespace AppLedger.Agent;

/// <summary>
/// Entry point for AppLedger.Agent.exe.
/// </summary>
/// <remarks>
/// TODO(kickoff): the real worker host — collector, pipe server and Scheduled Task installer — lands in v0.2
/// (docs/21_ROADMAP.md). This stub exists so the solution builds and so the CLI surface defined in
/// docs/16_PACKAGING_AND_UPDATES.md §Agent CLI is pinned before anything depends on it.
/// </remarks>
internal static class Program
{
    private const string Usage = """
        AppLedger.Agent - background collector, elevated through the "AppLedger Agent" Scheduled Task.

        Usage: AppLedger.Agent.exe <command>

          --serve         run collector + pipe server (how the task starts it)
          --install-task  write the task XML, create and start the task (needs elevation)
          --remove-task   stop the agent, delete the task (needs elevation)
          --status        print task state and pipe reachability (no elevation)
          --console       --serve with console logging and no single-instance mutex (dev only)
        """;

    private static int Main(string[] args)
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
            case "--install-task":
            case "--remove-task":
            case "--status":
            case "--console":
                Console.Error.WriteLine(
                    $"'{args[0]}' is not implemented yet - the Agent host arrives in v0.2 (docs/21_ROADMAP.md).");
                return 2;

            default:
                Console.Error.WriteLine($"Unknown command '{args[0]}'.");
                Console.Error.WriteLine(Usage);
                return 1;
        }
    }
}
