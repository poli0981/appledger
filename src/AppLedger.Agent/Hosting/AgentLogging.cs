using System.Globalization;
using AppLedger.Infrastructure.Storage;
using Serilog;
using Serilog.Events;

namespace AppLedger.Agent.Hosting;

/// <summary>
/// The Agent's Serilog configuration (docs/15_LOGGING.md §Sinks &amp; files).
/// </summary>
public static class AgentLogging
{
    /// <summary>The output template, verbatim from docs/15.</summary>
    public const string Template =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj} {Properties:j}{NewLine}{Exception}";

    /// <summary>
    /// Builds the file logger. Console output is not configured here.
    /// </summary>
    /// <remarks>
    /// <c>--console</c> gets its console output from the host's own logging providers rather than from a
    /// second Serilog sink, which keeps <c>Serilog.Sinks.Console</c> out of the dependency set entirely —
    /// <c>Microsoft.Extensions.Hosting</c> already brings a console provider, and a pin that exists only for
    /// a developer switch is a pin somebody has to keep current forever (CLAUDE.md §Exact version pins).
    /// </remarks>
    /// <param name="dataRoot">Logs live under the data root, never beside the binaries.</param>
    public static ILogger Create(DataRoot dataRoot)
    {
        ArgumentNullException.ThrowIfNull(dataRoot);

        var configuration = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.WithProperty("AgentVersion", typeof(AgentLogging).Assembly.GetName().Version?.ToString(3) ?? "0.0.0")
            .Enrich.WithProperty("OsBuild", Environment.OSVersion.Version.Build)
            .WriteTo.File(
                Path.Combine(dataRoot.LogsDirectory, "agent-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,

                // shared so the UI can tail this file while the Agent holds it, and unbuffered so a crash
                // leaves the last lines on disk — which are the ones worth having (docs/15).
                shared: true,
                buffered: false,
                flushToDiskInterval: TimeSpan.FromSeconds(2),
                outputTemplate: Template,
                restrictedToMinimumLevel: LogEventLevel.Information,
                formatProvider: CultureInfo.InvariantCulture);

        return configuration.CreateLogger();
    }
}
