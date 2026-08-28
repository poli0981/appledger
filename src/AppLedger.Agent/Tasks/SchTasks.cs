using System.Diagnostics;
using System.Globalization;

namespace AppLedger.Agent.Tasks;

/// <summary>What a <c>schtasks</c> invocation did.</summary>
/// <param name="ExitCode">Its exit code; 0 is success.</param>
/// <param name="Output">Standard output, trimmed.</param>
/// <param name="Error">Standard error, trimmed.</param>
public readonly record struct SchTasksResult(int ExitCode, string Output, string Error)
{
    /// <summary>True when the command succeeded.</summary>
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// The three <c>schtasks</c> verbs AppLedger uses, and no others
/// (docs/16_PACKAGING_AND_UPDATES.md §Scheduled Task, docs/11_SAFETY_POLICY.md).
/// </summary>
/// <remarks>
/// Every method here names <see cref="AgentTaskDefinition.TaskName"/> and takes no task name from a caller.
/// That is deliberate: "create, modify or delete any Scheduled Task other than <c>AppLedger Agent</c>" is on
/// the list of things this product does not do, and a parameter would make that a matter of discipline
/// rather than of shape.
/// <para>
/// <c>/Create</c> and <c>/Delete</c> need elevation for a <c>HighestAvailable</c> task; <c>/Run</c>,
/// <c>/End</c> and <c>/Query</c> do not, for the task's owner.
/// </para>
/// </remarks>
public static class SchTasks
{
    /// <summary>Registers the task from an XML file, replacing any existing one of the same name.</summary>
    public static SchTasksResult Create(string xmlPath) =>
        Run("/Create", "/TN", AgentTaskDefinition.TaskName, "/XML", xmlPath, "/F");

    /// <summary>Starts the task now.</summary>
    public static SchTasksResult Run() => Run("/Run", "/TN", AgentTaskDefinition.TaskName);

    /// <summary>Deletes the task.</summary>
    public static SchTasksResult Delete() => Run("/Delete", "/TN", AgentTaskDefinition.TaskName, "/F");

    /// <summary>Queries the task, in CSV so the state can be read rather than scraped.</summary>
    public static SchTasksResult Query() =>
        Run("/Query", "/TN", AgentTaskDefinition.TaskName, "/FO", "CSV", "/NH");

    /// <summary>
    /// The task's state — <c>Ready</c>, <c>Running</c>, <c>Disabled</c> — or null when it does not exist.
    /// </summary>
    public static string? QueryState()
    {
        var result = Query();
        if (!result.Succeeded)
        {
            return null;
        }

        // CSV without headers: "TaskName","Next Run Time","Status". Quoted fields, and the task name is the
        // one field that could contain a comma, so the state is read from the end rather than by splitting.
        var line = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
        if (string.IsNullOrEmpty(line))
        {
            return null;
        }

        var lastQuote = line.LastIndexOf('"');
        var openQuote = lastQuote <= 0 ? -1 : line.LastIndexOf('"', lastQuote - 1);

        return lastQuote > 0 && openQuote >= 0
            ? line[(openQuote + 1)..lastQuote]
            : null;
    }

    private static SchTasksResult Run(params string[] arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,

            // Invariant so a localized Windows does not change the CSV the state is read out of.
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException("schtasks.exe could not be started.");

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(milliseconds: 30_000);

        return new SchTasksResult(
            process.HasExited ? process.ExitCode : -1,
            output.Trim(),
            error.Trim());
    }

    /// <summary>Formats a failed result for a console message, without leaking a path.</summary>
    public static string Describe(in SchTasksResult result) =>
        string.Create(CultureInfo.InvariantCulture, $"schtasks exited {result.ExitCode}: {result.Error}");
}
