using AppLedger.App.Services;
using AppLedger.App.ViewModels;
using AppLedger.Ipc;
using AppLedger.Ipc.Streams;
using Shouldly;
using Xunit;

namespace AppLedger.App.Tests;

/// <summary>An agent client the test drives directly, with no pipe and no collector.</summary>
internal sealed class FakeAgentClient : IAgentClient
{
    public event Action<AgentStatus>? StatusChanged;

    public event Action<IReadOnlyList<AppRow>>? AppsTick;

    public event Action<HealthPayload>? HealthTick;

    public AgentStatus Status { get; private set; } = AgentStatus.Connecting;

    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    internal void Publish(AgentStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(status);
    }

    internal void Publish(params AppRow[] rows) => AppsTick?.Invoke(rows);

    internal void Publish(HealthPayload health) => HealthTick?.Invoke(health);
}

/// <summary>
/// The two view-models that carry live data (docs/19_TESTING.md §UI — view-model tests, no XAML).
/// </summary>
public sealed class ViewModelTests
{
    private static AppRow Row(string appId, int procs = 1, double cpu = 0, long ws = 0, long netIn = 0) => new()
    {
        AppId = appId,
        Procs = procs,
        CpuPct = cpu,
        WsPrivate = ws,
        NetIn = netIn,
    };

    private static AgentStatus Status(ConnectionMode mode, params (string Name, string State)[] sensors) =>
        new(mode,
            mode == ConnectionMode.Lite ? null : "0.2.0",
            sensors.ToDictionary(s => s.Name, s => new SensorStatePayload(s.State), StringComparer.Ordinal),
            TaskInstalled: mode != ConnectionMode.Lite);

    // -- AppsViewModel -----------------------------------------------------------------------------------

    [Fact]
    public void Apply_FirstTick_CreatesARowPerApp()
    {
        var client = new FakeAgentClient();
        var viewModel = new AppsViewModel(client);

        client.Publish(Row("cat:chrome"), Row("cat:discord"));

        viewModel.Rows.Select(r => r.AppId).ShouldBe(["cat:chrome", "cat:discord"], ignoreOrder: true);
    }

    /// <summary>
    /// The reason the tick is not bound to <c>ItemsSource</c>: the same row object has to survive, or the
    /// grid rebuilds every container each second and selection, scroll and sort go with it.
    /// </summary>
    [Fact]
    public void Apply_SecondTick_UpdatesTheSameRowObject()
    {
        var client = new FakeAgentClient();
        var viewModel = new AppsViewModel(client);

        client.Publish(Row("cat:chrome", cpu: 1.0));
        var first = viewModel.Rows.ShouldHaveSingleItem();

        client.Publish(Row("cat:chrome", cpu: 42.5, ws: 900));

        viewModel.Rows.ShouldHaveSingleItem().ShouldBeSameAs(first);
        first.CpuPct.ShouldBe(42.5);
        first.WsPrivate.ShouldBe(900);
    }

    [Fact]
    public void Apply_NewApp_IsAdded()
    {
        var client = new FakeAgentClient();
        var viewModel = new AppsViewModel(client);

        client.Publish(Row("cat:chrome"));
        client.Publish(Row("cat:chrome"), Row("cat:steam"));

        viewModel.Rows.Count.ShouldBe(2);
    }

    /// <summary>
    /// One missed tick is not an exit. An app whose instances all happened to be between deltas produces no
    /// sample for that second, and removing its row would make it flicker out of the grid and back in.
    /// </summary>
    [Fact]
    public void Apply_AppMissingForOneTick_KeepsItsRow()
    {
        var client = new FakeAgentClient();
        var viewModel = new AppsViewModel(client);

        client.Publish(Row("cat:chrome"), Row("cat:steam"));
        client.Publish(Row("cat:chrome"));

        viewModel.Rows.Count.ShouldBe(2);
    }

    [Fact]
    public void Apply_AppGoneForTheGracePeriod_LosesItsRow()
    {
        var client = new FakeAgentClient();
        var viewModel = new AppsViewModel(client);

        client.Publish(Row("cat:chrome"), Row("cat:steam"));

        for (var i = 0; i < AppsViewModel.TicksBeforeRemoval; i++)
        {
            client.Publish(Row("cat:chrome"));
        }

        viewModel.Rows.ShouldHaveSingleItem().AppId.ShouldBe("cat:chrome");
    }

    [Fact]
    public void Apply_AppThatReturnsAfterAGap_GetsAFreshRow()
    {
        var client = new FakeAgentClient();
        var viewModel = new AppsViewModel(client);

        client.Publish(Row("cat:steam"));
        for (var i = 0; i < AppsViewModel.TicksBeforeRemoval; i++)
        {
            client.Publish();
        }

        viewModel.Rows.ShouldBeEmpty();

        client.Publish(Row("cat:steam", cpu: 3));
        viewModel.Rows.ShouldHaveSingleItem().CpuPct.ShouldBe(3);
    }

    /// <summary>A row with no app id would be a row the user cannot act on; it is dropped rather than shown.</summary>
    [Fact]
    public void Apply_RowWithNoAppId_IsIgnored()
    {
        var client = new FakeAgentClient();
        var viewModel = new AppsViewModel(client);

        client.Publish(Row(string.Empty), Row("cat:chrome"));

        viewModel.Rows.ShouldHaveSingleItem().AppId.ShouldBe("cat:chrome");
    }

    [Fact]
    public void Apply_LiteStatus_RaisesTheBanner()
    {
        var client = new FakeAgentClient();
        var viewModel = new AppsViewModel(client);

        client.Publish(Status(ConnectionMode.Lite));
        viewModel.IsLite.ShouldBeTrue();

        client.Publish(Status(ConnectionMode.Full));
        viewModel.IsLite.ShouldBeFalse();
    }

    /// <summary>A view-model built after the client already had a state must not start out blank.</summary>
    [Fact]
    public void Constructor_ClientAlreadyInLiteMode_PicksItUp()
    {
        var client = new FakeAgentClient();
        client.Publish(Status(ConnectionMode.Lite));

        new AppsViewModel(client).IsLite.ShouldBeTrue();
    }

    // -- HomeViewModel -----------------------------------------------------------------------------------

    [Theory]
    [InlineData(ConnectionMode.Full, "Full")]
    [InlineData(ConnectionMode.Degraded, "Degraded")]
    [InlineData(ConnectionMode.Lite, "Lite")]
    [InlineData(ConnectionMode.Connecting, "Connecting")]
    public void Home_Mode_IsShownAsItsLocalizedName(ConnectionMode mode, string expected)
    {
        var client = new FakeAgentClient();
        var viewModel = new HomeViewModel(client);

        client.Publish(Status(mode));

        viewModel.Mode.ShouldBe(expected);
    }

    [Fact]
    public void Home_Health_ShowsTheAgentsOwnCost()
    {
        var client = new FakeAgentClient();
        var viewModel = new HomeViewModel(client);

        client.Publish(new HealthPayload
        {
            Ts = 1_700_000_000,
            AgentCpuPct = 0.04,
            AgentWs = 36 * 1024 * 1024,
            EventsLost = 7,
            RingSeconds = 300,
            Sensors = new Dictionary<string, SensorStatePayload> { ["EtwHub"] = new("Running") },
            BudgetOk = true,
        });

        viewModel.AgentCpuPct.ShouldBe(0.04);
        viewModel.AgentWorkingSet.ShouldBe(36 * 1024 * 1024);
        viewModel.EventsLost.ShouldBe(7);
        viewModel.Sensors.ShouldHaveSingleItem().Name.ShouldBe("EtwHub");
    }

    /// <summary>
    /// Lite mode has no Agent, so it has no Agent cost. Showing this process's numbers under that label
    /// would be a different measurement wearing the same name.
    /// </summary>
    [Fact]
    public void Home_LiteMode_ReportsNoAgentCostRatherThanZero()
    {
        var client = new FakeAgentClient();
        var viewModel = new HomeViewModel(client);

        client.Publish(new HealthPayload
        {
            Ts = 1,
            AgentCpuPct = 1.5,
            AgentWs = 100,
            EventsLost = 0,
            RingSeconds = 60,
            Sensors = new Dictionary<string, SensorStatePayload>(),
            BudgetOk = true,
        });

        client.Publish(Status(ConnectionMode.Lite));

        viewModel.AgentCpuPct.ShouldBeNull();
        viewModel.AgentWorkingSet.ShouldBeNull();
        viewModel.AgentVersion.ShouldBeNull();
    }

    /// <summary>
    /// A sensor that cannot run here is shown as such. The chip is what turns "this column is zero" into
    /// "we could not look", which is the distinction docs/01 §Degraded modes is built on.
    /// </summary>
    [Fact]
    public void Home_UnavailableSensor_IsShownAsNotRunning()
    {
        var client = new FakeAgentClient();
        var viewModel = new HomeViewModel(client);

        client.Publish(Status(ConnectionMode.Degraded, ("EtwHub", "Running"), ("GpuPoller", "Unavailable")));

        viewModel.Sensors.Count.ShouldBe(2);
        viewModel.Sensors.Single(s => s.Name == "GpuPoller").IsRunning.ShouldBeFalse();
        viewModel.Sensors.Single(s => s.Name == "EtwHub").IsRunning.ShouldBeTrue();
    }

    [Fact]
    public void Home_SensorNames_AreNeverLocalized()
    {
        var client = new FakeAgentClient();
        var viewModel = new HomeViewModel(client);

        client.Publish(Status(ConnectionMode.Full, ("EtwHub", "Running")));

        // docs/14 §Rules: app ids, sensor names and log event names stay as they are in every language.
        viewModel.Sensors.ShouldHaveSingleItem().Name.ShouldBe("EtwHub");
    }
}
