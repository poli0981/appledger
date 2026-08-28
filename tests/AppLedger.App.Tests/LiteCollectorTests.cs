using AppLedger.App.Services;
using AppLedger.Core.Collection;
using AppLedger.Core.Metrics;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace AppLedger.App.Tests;

/// <summary>
/// Lite mode actually collecting (docs/01_ARCHITECTURE.md §Lite mode).
/// </summary>
/// <remarks>
/// An integration test rather than a view-model one, and needed as such: everything above it can be green
/// while the grid stays empty, because the view-model is perfectly capable of applying zero ticks correctly.
/// It runs unelevated — which is the whole claim Lite mode makes.
/// </remarks>
public sealed class LiteCollectorTests
{
    private readonly ITestOutputHelper _output;

    public LiteCollectorTests(ITestOutputHelper output) => _output = output;

    private static async Task<IReadOnlyList<AppSample>> CollectAsync(LiteCollector collector)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // The first tick is only a baseline: counters have no interval to be measured against yet, so it
        // produces no samples by design. The second tick onwards is real data.
        await foreach (var samples in collector.Host.Live.Reader.ReadAllAsync(timeout.Token))
        {
            if (samples.Count > 0)
            {
                return samples;
            }
        }

        return [];
    }

    [Fact]
    public async Task Lite_ProducesSamplesForRunningApps()
    {
        await using var collector = new LiteCollector();
        await collector.StartAsync();

        var samples = await CollectAsync(collector);

        _output.WriteLine($"{samples.Count} apps in the first non-empty second");
        samples.ShouldNotBeEmpty("Lite mode produced no samples, so the grid would be empty");
    }

    /// <summary>
    /// What a standard user <i>can</i> see has to be there, or Lite mode is a blank window with a banner.
    /// </summary>
    [Fact]
    public async Task Lite_SamplesCarryTheNumbersAStandardUserCanRead()
    {
        await using var collector = new LiteCollector();
        await collector.StartAsync();

        var samples = await CollectAsync(collector);

        samples.Sum(s => s.WsPrivate).ShouldBeGreaterThan(0, "no working set was read");
        samples.Sum(s => s.Procs).ShouldBeGreaterThan(0, "no process instances were counted");
    }

    /// <summary>
    /// And what it cannot see must be <b>absent rather than zero</b>. ETW needs elevation, so Lite mode
    /// constructs no hub at all — the sensor simply has no entry, which is what tells the UI to show "N/A"
    /// instead of a zero that would claim we looked (docs/01 §Degraded modes).
    /// </summary>
    [Fact]
    public async Task Lite_HasNoEtwSensorAtAll()
    {
        await using var collector = new LiteCollector();
        await collector.StartAsync();

        var names = collector.Sensors.Select(s => s.Name).ToList();

        _output.WriteLine(string.Join(", ", collector.Sensors.Select(s => $"{s.Name}={s.Health.State}")));
        names.ShouldNotContain("EtwHub");
        names.ShouldContain("ConnectionPoller");
    }

    /// <summary>
    /// The connection poller works without elevation, which is why Lite mode has one at all
    /// (docs/04_DATA_SOURCES.md §Privilege matrix).
    /// </summary>
    [Fact]
    public async Task Lite_ConnectionPollerRunsWithoutElevation()
    {
        await using var collector = new LiteCollector();
        await collector.StartAsync();

        collector.Sensors
            .Single(s => s.Name == "ConnectionPoller")
            .Health.State.ShouldBe(SensorState.Running);
    }

    /// <summary>
    /// A UI is by definition watching, so Lite mode must never adopt the idle profile — the thing the idle
    /// profile saves for is a UI that is not there.
    /// </summary>
    [Fact]
    public async Task Lite_NeverGoesIdleWhileTheWindowIsOpen()
    {
        await using var collector = new LiteCollector();
        await collector.StartAsync();

        await CollectAsync(collector);

        collector.Host.IsIdle.ShouldBeFalse();
    }

    /// <summary>
    /// History is the Agent's alone (docs/06_DATA_MODEL.md §Ownership). A UI writing rows would put two
    /// writers on one database, and `CollectorOptions.Lite` is what makes that structurally impossible.
    /// </summary>
    [Fact]
    public async Task Lite_PersistsNothing()
    {
        await using var collector = new LiteCollector();
        await collector.StartAsync();

        await CollectAsync(collector);

        collector.Host.RowsWritten.ShouldBe(0);
        CollectorOptions.Lite.PersistsHistory.ShouldBeFalse();
    }
}
