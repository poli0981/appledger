using AppLedger.Core.Collection;
using AppLedger.Infrastructure.Gpu;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace AppLedger.Infrastructure.Tests.Gpu;

/// <summary>
/// Adapter smoke test for the PDH GPU counters (docs/19_TESTING.md §Layers: "PDH GPU Engine wildcard parse,
/// skipped if no GPU counters, e.g. hosted runner").
/// </summary>
/// <remarks>
/// The counter set is genuinely absent on a VM, a server SKU or an old driver, so most assertions here are
/// about the poller behaving sensibly in <b>both</b> worlds rather than about a GPU being present. A test
/// that demanded a GPU would fail on the CI runner for a reason that has nothing to do with the code.
/// </remarks>
public sealed class GpuPollerTests
{
    private readonly ITestOutputHelper _output;

    public GpuPollerTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Whichever world this machine is in, starting must reach a defined state and never throw. "No GPU
    /// counters on this box" is a normal Tuesday (docs/01 §Degraded modes).
    /// </summary>
    [Fact]
    public async Task StartAsync_ReachesRunningOrUnavailable_AndNeverThrows()
    {
        using var poller = new GpuPoller();

        await Should.NotThrowAsync(() => poller.StartAsync());

        _output.WriteLine($"GPU counters: {poller.Health.State} ({poller.Health.Detail ?? "-"})");
        poller.Health.State.ShouldBeOneOf(SensorState.Running, SensorState.Unavailable);
    }

    /// <summary>
    /// The distinction the whole sensor exists to preserve: an unavailable counter set yields **no samples**,
    /// not samples of zero. A zero would claim we looked and found no GPU work, which is a different and
    /// false statement — and the UI would draw a flat line instead of "N/A".
    /// </summary>
    [Fact]
    public async Task Sample_WhenCountersAreUnavailable_IsEmptyRatherThanZeroes()
    {
        using var poller = new GpuPoller();
        await poller.StartAsync();

        if (poller.Health.State != SensorState.Unavailable)
        {
            return;
        }

        poller.Sample().ShouldBeEmpty();
    }

    [Fact]
    public async Task Sample_WhenCountersExist_ReturnsPlausibleValues()
    {
        using var poller = new GpuPoller();
        await poller.StartAsync();

        if (!poller.Health.IsRunning)
        {
            _output.WriteLine("No GPU counters on this machine; the parse path is covered by TryParsePid.");
            return;
        }

        var samples = poller.Sample();
        _output.WriteLine($"{samples.Count} processes with GPU counters");

        samples.ShouldAllBe(s => s.ProcessId > 0);
        samples.ShouldAllBe(s => s.UtilizationPercent >= 0);
        samples.ShouldAllBe(s => s.DedicatedBytes >= 0 && s.SharedBytes >= 0);

        // One row per process, not one per engine: the poller folds engine instances down by taking the
        // maximum, the way Task Manager does.
        samples.Select(s => s.ProcessId).ShouldBeUnique();
    }

    [Fact]
    public void Sample_BeforeStarting_IsEmpty() => new GpuPoller().Sample().ShouldBeEmpty();

    [Fact]
    public async Task StopAsync_ThenSample_IsEmpty()
    {
        using var poller = new GpuPoller();
        await poller.StartAsync();

        await poller.StopAsync();

        poller.Health.State.ShouldBe(SensorState.Stopped);
        poller.Sample().ShouldBeEmpty();
    }

    [Fact]
    public void Dispose_WithoutStarting_IsSafe() => Should.NotThrow(() => new GpuPoller().Dispose());

    /// <summary>
    /// There is no per-process GPU API: the PID lives in the counter instance name and nowhere else. This
    /// is the one piece of the sensor that is testable on every machine, which is why it is a separate,
    /// exhaustively covered function rather than three lines inside the read loop.
    /// </summary>
    [Theory]
    [InlineData("pid_1234_luid_0x00000000_0x0000C24E_phys_0_eng_0_engtype_3D", 1234)]
    [InlineData("pid_4_luid_0x00000000_0x00009DBA_phys_0", 4)]
    [InlineData("pid_65535_luid_0x0_0x0_phys_0_eng_3_engtype_VideoDecode", 65535)]
    public void TryParsePid_RealInstanceNames_YieldThePid(string instance, int expected)
    {
        GpuPoller.TryParsePid(instance, out var pid).ShouldBeTrue();

        pid.ShouldBe(expected);
    }

    /// <summary>
    /// Anything that is not the documented shape must yield nothing rather than a wrong PID. Attributing
    /// GPU usage to the wrong process is worse than attributing it to none.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("luid_0x0_0x0_phys_0")]
    [InlineData("pid_")]
    [InlineData("pid_notanumber_luid_0x0")]
    [InlineData("PID_1234_luid_0x0")]
    [InlineData("_pid_1234")]
    [InlineData("pid_-5_luid_0x0")]
    public void TryParsePid_AnythingElse_YieldsNothing(string? instance)
    {
        GpuPoller.TryParsePid(instance, out var pid).ShouldBeFalse();

        pid.ShouldBe(0);
    }
}
