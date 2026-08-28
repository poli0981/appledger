using System.Diagnostics;
using System.Text.Json;
using AppLedger.Core.Collection;
using AppLedger.Core.Metrics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AppLedger.Agent.Hosting;

/// <summary>
/// Owns the timer the collector deliberately does not (docs/05_COLLECTOR.md §Components).
/// </summary>
/// <remarks>
/// <c>CollectorHost.TickAsync</c> is called by whoever has a clock, which is what makes the pipeline
/// testable without one. Here that is a <see cref="PeriodicTimer"/> re-read from
/// <c>CollectorHost.CurrentInterval</c> every tick, so the idle profile takes effect the moment no UI has
/// been seen for ten minutes rather than at the next restart.
/// </remarks>
public sealed partial class CollectorWorker : BackgroundService
{
    private readonly AgentRuntime _runtime;
    private readonly ILogger<CollectorWorker> _logger;
    private readonly Process _self = Process.GetCurrentProcess();

    private TimeSpan _lastCpu;
    private DateTimeOffset _lastHealthAt = DateTimeOffset.MinValue;
    private long _lastHealthMinute = -1;

    /// <summary>Creates the worker over a built runtime.</summary>
    public CollectorWorker(AgentRuntime runtime, ILogger<CollectorWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(logger);

        _runtime = runtime;
        _logger = logger;
    }

    /// <summary>How many ticks have completed. For the smoke test and for diagnostics.</summary>
    public long Ticks { get; private set; }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Below normal, matching the Scheduled Task's own priority 7: a monitor that competes with the
        // things it is monitoring changes the numbers it reports (docs/16 §Scheduled Task).
        TrySetBelowNormalPriority();

        await _runtime.Collector.StartSensorsAsync(stoppingToken).ConfigureAwait(false);
        LogSensorState();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await TickOnceAsync(stoppingToken).ConfigureAwait(false);

                // Re-read each time: the interval doubles when the profile goes idle.
                using var timer = new PeriodicTimer(_runtime.Collector.CurrentInterval);
                await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown, which is the only way out of the loop that is not a fault.
        }
        finally
        {
            // Flush before stopping the sensors: the partial minute is history, and losing up to 59 seconds
            // of every session would read as gaps in the charts rather than as a shutdown.
            await FlushAsync().ConfigureAwait(false);
            await _runtime.Collector.StopSensorsAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>One tick plus, on a minute boundary, one health row. Exposed so a test can drive it.</summary>
    public async Task TickOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _runtime.Collector.TickAsync(cancellationToken).ConfigureAwait(false);
            Ticks++;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One bad tick must not end collection. The alternative is an Agent that dies on a transient
            // database lock and takes the rest of the day's history with it.
            TickFailed(_logger, ex.GetType().Name);
        }

        await WriteHealthIfDueAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteHealthIfDueAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var minute = now.ToUnixTimeSeconds() / 60 * 60;
        if (minute == _lastHealthMinute)
        {
            return;
        }

        var health = _runtime.Collector.ReadHealth();

        try
        {
            await _runtime.Repository.WriteHealthAsync(
                new HealthMinute(minute, CpuPercentSince(now), _self.PrivateMemorySize64, health.EventsLost, Sensors(health)),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            HealthWriteFailed(_logger, ex.GetType().Name);
        }

        _lastHealthMinute = minute;
    }

    /// <summary>
    /// The process's CPU as a percentage of one core, averaged over the interval since the last reading.
    /// </summary>
    /// <remarks>
    /// Divided by the logical CPU count, which is the Task Manager convention the rest of the product uses
    /// (docs/04_DATA_SOURCES.md) — an Agent using a whole core on a 20-thread box reads as 5 %, not 100 %,
    /// and the budget in docs/01 is written against that same convention.
    /// </remarks>
    private double CpuPercentSince(DateTimeOffset now)
    {
        _self.Refresh();
        var cpu = _self.TotalProcessorTime;

        if (_lastHealthAt == DateTimeOffset.MinValue)
        {
            _lastHealthAt = now;
            _lastCpu = cpu;
            return 0d;
        }

        var wall = now - _lastHealthAt;
        var used = cpu - _lastCpu;

        _lastHealthAt = now;
        _lastCpu = cpu;

        return wall <= TimeSpan.Zero
            ? 0d
            : Math.Round(used / wall * 100d / Environment.ProcessorCount, 2);
    }

    private static string Sensors(CollectorHealth health) =>
        JsonSerializer.Serialize(
            health.Sensors.ToDictionary(s => s.Name, s => s.Health.State.ToString(), StringComparer.Ordinal));

    private async Task FlushAsync()
    {
        try
        {
            await _runtime.Collector.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            FlushFailed(_logger, ex.GetType().Name);
        }
    }

    private void TrySetBelowNormalPriority()
    {
        try
        {
            _self.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // A machine or policy that refuses the change is not a reason to refuse to collect.
            PriorityNotSet(_logger, ex.GetType().Name);
        }
    }

    private void LogSensorState()
    {
        foreach (var sensor in _runtime.Collector.Sensors)
        {
            LogSensor(_logger, sensor.Name, sensor.Health.State, sensor.Health.Detail);
        }
    }

    [LoggerMessage(EventId = 1500, Level = LogLevel.Information, Message = "Sensor {Sensor} is {State} {Detail}")]
    private static partial void LogSensor(ILogger logger, string sensor, SensorState state, string? detail);

    [LoggerMessage(EventId = 1501, Level = LogLevel.Error, Message = "A collection tick failed with {Error}")]
    private static partial void TickFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 1502, Level = LogLevel.Error, Message = "Writing the health row failed with {Error}")]
    private static partial void HealthWriteFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 1503, Level = LogLevel.Error, Message = "The final flush failed with {Error}")]
    private static partial void FlushFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 1504, Level = LogLevel.Warning, Message = "Process priority was not lowered: {Error}")]
    private static partial void PriorityNotSet(ILogger logger, string error);
}
