using System.Diagnostics;

namespace AppLedger.Core.Time;

/// <summary>
/// Wall-clock time and monotonic elapsed time, as two separate readings.
/// </summary>
/// <remarks>
/// They are separate on purpose. The collector compares them to detect the clock jumps of
/// docs/05_COLLECTOR.md §Failure handling: sleep and resume, a manual time change, an NTP correction. Wall
/// clock moves in all three; monotonic time does not. A collector that trusted only the wall clock would
/// compute a one-second delta over an eight-hour sleep and report a machine that used 30 GB of network in
/// a second.
/// <para>
/// It is a port so tests can drive a whole minute of collection in microseconds and step time backwards,
/// which no real clock will do on demand.
/// </para>
/// </remarks>
public interface IClock
{
    /// <summary>Wall-clock now, UTC. Subject to jumps.</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>
    /// A monotonic reading that only ever moves forward at the rate of real time. Its origin is arbitrary,
    /// so only differences between two readings mean anything.
    /// </summary>
    TimeSpan Elapsed { get; }
}

/// <summary>The real clock: the system time, and a stopwatch started when the process did.</summary>
public sealed class SystemClock : IClock
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    /// <summary>The shared instance. It is stateless apart from its stopwatch origin.</summary>
    public static SystemClock Instance { get; } = new();

    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public TimeSpan Elapsed => _stopwatch.Elapsed;
}

/// <summary>Helpers for the UTC-epoch-seconds form every stored timestamp uses.</summary>
public static class ClockExtensions
{
    /// <summary>Wall-clock now as UTC epoch seconds (docs/06_DATA_MODEL.md §Time).</summary>
    public static long UtcNowSeconds(this IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return clock.UtcNow.ToUnixTimeSeconds();
    }
}
