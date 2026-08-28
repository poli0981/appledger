using System.Diagnostics;
using System.IO;

namespace AppLedger.App.Services;

/// <summary>How the Agent's Scheduled Task gets installed and started from the UI.</summary>
public interface IAgentSetup
{
    /// <summary>Whether the <c>AppLedger Agent</c> task exists.</summary>
    bool IsTaskInstalled();

    /// <summary>
    /// Runs <c>AppLedger.Agent.exe --install-task</c> elevated. This is the one UAC prompt the product asks
    /// for (docs/01_ARCHITECTURE.md §Elevation strategy step 1).
    /// </summary>
    /// <returns>False when the user dismissed the prompt, which is a choice rather than a failure.</returns>
    Task<bool> InstallAsync(CancellationToken cancellationToken = default);

    /// <summary>Starts an installed task. Needs no elevation for the task's owner.</summary>
    Task<bool> StartAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The real implementation, over <c>schtasks</c> and <c>ShellExecute</c>.
/// </summary>
/// <remarks>
/// The UI never creates or deletes a task itself. It launches the Agent's own <c>--install-task</c> under the
/// <c>runas</c> verb and lets the elevated process do it, which is what keeps the whole
/// scheduled-task surface inside the one binary that docs/11_SAFETY_POLICY.md constrains — and keeps this
/// process, which draws charts, from ever running elevated (ADR-2).
/// </remarks>
public sealed class AgentSetup : IAgentSetup
{
    /// <summary>The task's name. The UI only ever names this one, like the Agent.</summary>
    public const string TaskName = "AppLedger Agent";

    /// <inheritdoc />
    public bool IsTaskInstalled() => Run("schtasks.exe", ["/Query", "/TN", TaskName]) == 0;

    /// <inheritdoc />
    public async Task<bool> InstallAsync(CancellationToken cancellationToken = default)
    {
        var agent = AgentExecutablePath();
        if (!File.Exists(agent))
        {
            // Running from a build output rather than an install, which is the developer's case. Saying so
            // is better than a UAC prompt for a file that is not there.
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = agent,
                Arguments = "--install-task",

                // UseShellExecute is required for the runas verb; it is what raises the UAC prompt.
                UseShellExecute = true,
                Verb = "runas",
            });

            if (process is null)
            {
                return false;
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // ERROR_CANCELLED: the user dismissed the UAC prompt. Declining elevation is exactly the case
            // Lite mode exists for, so it is not an error and must not be reported as one.
            return false;
        }
    }

    /// <inheritdoc />
    public Task<bool> StartAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Run("schtasks.exe", ["/Run", "/TN", TaskName]) == 0);

    /// <summary>
    /// Where the Agent lives: <c>%LOCALAPPDATA%\AppLedger\current\AppLedger.Agent.exe</c>.
    /// </summary>
    /// <remarks>
    /// The stable <c>current\</c> folder, not this process's own directory — the two are the same only in a
    /// development build, and the installed layout is the one that has to work.
    /// </remarks>
    public static string AgentExecutablePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AppLedger",
        "current",
        "AppLedger.Agent.exe");

    private static int Run(string fileName, string[] arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(info);
            if (process is null)
            {
                return -1;
            }

            process.WaitForExit(milliseconds: 15_000);
            return process.HasExited ? process.ExitCode : -1;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return -1;
        }
    }
}
