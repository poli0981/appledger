using AppLedger.Core.Collection;
using AppLedger.Core.Time;
using Shouldly;
using Xunit;

namespace AppLedger.Core.Tests.Collection;

/// <summary>
/// The two small ports the collector is built on. They are worth testing because both encode a decision
/// rather than a mechanism: what "unavailable" means, and why wall-clock and monotonic time are separate
/// readings.
/// </summary>
public sealed class SensorAndClockTests
{
    [Fact]
    public void SensorHealth_Stopped_IsNotRunning()
    {
        SensorHealth.Stopped.State.ShouldBe(SensorState.Stopped);
        SensorHealth.Stopped.IsRunning.ShouldBeFalse();
    }

    /// <summary>
    /// An unavailable sensor carries its reason, because the UI shows "N/A" plus why — a zero that means
    /// "we could not look" is a lie (docs/01_ARCHITECTURE.md §Degraded modes).
    /// </summary>
    [Fact]
    public void SensorHealth_Unavailable_KeepsTheReason()
    {
        var health = SensorHealth.Unavailable("ERROR_NO_SYSTEM_RESOURCES");

        health.State.ShouldBe(SensorState.Unavailable);
        health.IsRunning.ShouldBeFalse();
        health.Detail.ShouldBe("ERROR_NO_SYSTEM_RESOURCES");
    }

    [Fact]
    public void SensorHealth_Running_CountsLossesSeparatelyFromHandlerErrors()
    {
        var health = new SensorHealth(SensorState.Running, HandlerErrors: 2, EventsLost: 17);

        health.IsRunning.ShouldBeTrue();
        health.HandlerErrors.ShouldBe(2);
        health.EventsLost.ShouldBe(17);
    }

    /// <summary>
    /// The property that makes clock-jump detection possible: monotonic time cannot be moved by anything
    /// outside the process, so two readings always differ by real elapsed time.
    /// </summary>
    [Fact]
    public void SystemClock_Elapsed_NeverGoesBackwards()
    {
        var clock = SystemClock.Instance;

        var first = clock.Elapsed;
        var second = clock.Elapsed;

        second.ShouldBeGreaterThanOrEqualTo(first);
    }

    [Fact]
    public void SystemClock_UtcNow_IsInUtc() => SystemClock.Instance.UtcNow.Offset.ShouldBe(TimeSpan.Zero);

    [Fact]
    public void UtcNowSeconds_MatchesTheClocksOwnReading()
    {
        var clock = new FixedClock(DateTimeOffset.FromUnixTimeSeconds(1_700_000_123));

        clock.UtcNowSeconds().ShouldBe(1_700_000_123);
    }

    /// <summary>
    /// The reason <see cref="IClock"/> is a port at all: a test drives a whole minute of collection in
    /// microseconds, and steps time backwards, which no real clock will do on request.
    /// </summary>
    private sealed class FixedClock : IClock
    {
        public FixedClock(DateTimeOffset now) => UtcNow = now;

        public DateTimeOffset UtcNow { get; }

        public TimeSpan Elapsed => TimeSpan.Zero;
    }
}
