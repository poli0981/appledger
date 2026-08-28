using System.Text.Json;
using AppLedger.App.Services;
using AppLedger.App.ViewModels;
using AppLedger.Infrastructure.Storage;
using AppLedger.Ipc;
using Shouldly;
using Xunit;

namespace AppLedger.App.Tests;

/// <summary>An Agent setup the test drives, with no UAC prompt and no schtasks.</summary>
internal sealed class FakeAgentSetup : IAgentSetup
{
    internal bool Installed { get; set; }

    internal bool TaskExists { get; set; }

    internal int InstallCalls { get; private set; }

    internal int StartCalls { get; private set; }

    public bool IsTaskInstalled() => TaskExists;

    public Task<bool> InstallAsync(CancellationToken cancellationToken = default)
    {
        InstallCalls++;
        return Task.FromResult(Installed);
    }

    public Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        StartCalls++;
        return Task.FromResult(true);
    }
}

/// <summary>
/// The first-run flow and the settings behind it (docs/08_UI.md §Onboarding, docs/12 §Privacy Gate).
/// </summary>
public sealed class OnboardingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"appledger-onboarding-{Guid.NewGuid():N}");

    private AppSettingsStore Store() => new(new DataRoot(_root));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory that will not delete is not worth failing a test over.
        }
    }

    // -- settings ----------------------------------------------------------------------------------------

    /// <summary>
    /// docs/12 §Defaults, and these are product decisions rather than values to be tuned: 180 days is the
    /// six-month promise the Privacy Gate makes.
    /// </summary>
    [Fact]
    public void Settings_FirstRun_HasTheDocumentedDefaults()
    {
        var settings = Store().Load();

        settings.RetentionDays.ShouldBe(180);
        settings.OnboardingCompleted.ShouldBeFalse();
        settings.Language.ShouldBe("system");
    }

    [Fact]
    public void Settings_RoundTrip()
    {
        var store = Store();
        store.Save(new AppSettings { RetentionDays = 90, OnboardingCompleted = true, Language = "vi" });

        var reloaded = Store().Load();

        reloaded.RetentionDays.ShouldBe(90);
        reloaded.OnboardingCompleted.ShouldBeTrue();
        reloaded.Language.ShouldBe("vi");
    }

    /// <summary>
    /// A corrupt settings file is a first run, not a crash — and the worst outcome of getting that wrong way
    /// round is showing the Privacy Gate again, which is never the wrong thing to show.
    /// </summary>
    [Fact]
    public void Settings_CorruptFile_ReadsAsAFirstRun()
    {
        var root = new DataRoot(_root);
        root.EnsureCreated();
        File.WriteAllText(root.SettingsPath, "{ this is not json");

        var settings = Store().Load();

        settings.OnboardingCompleted.ShouldBeFalse();
        settings.RetentionDays.ShouldBe(180);
    }

    [Fact]
    public void Settings_AreWrittenAsSnakeCaseJson()
    {
        var store = Store();
        store.Save(new AppSettings { OnboardingCompleted = true });

        var json = File.ReadAllText(new DataRoot(_root).SettingsPath);

        json.ShouldContain("onboarding_completed");
        json.ShouldContain("retention_days");
        JsonDocument.Parse(json).ShouldNotBeNull();
    }

    // -- the flow ----------------------------------------------------------------------------------------

    private OnboardingViewModel Build(FakeAgentSetup setup, Action? completed = null) =>
        new(Store(), setup, completed ?? (() => { }));

    [Fact]
    public void Onboarding_StartsOnThePrivacyGate()
    {
        var viewModel = Build(new FakeAgentSetup());

        viewModel.IsPrivacyGate.ShouldBeTrue();
        viewModel.CanGoBack.ShouldBeFalse();
        viewModel.StepLabel.ShouldBe("Step 1 of 3");
    }

    [Fact]
    public void Onboarding_Continue_ReachesAgentSetup()
    {
        var viewModel = Build(new FakeAgentSetup());

        viewModel.ContinueCommand.Execute(null);

        viewModel.IsAgentSetup.ShouldBeTrue();
        viewModel.CanGoBack.ShouldBeTrue();
    }

    [Fact]
    public void Onboarding_Continue_StopsAtTheLastStep()
    {
        var viewModel = Build(new FakeAgentSetup());

        for (var i = 0; i < 10; i++)
        {
            viewModel.ContinueCommand.Execute(null);
        }

        viewModel.IsDefaults.ShouldBeTrue();
        viewModel.StepLabel.ShouldBe("Step 3 of 3");
    }

    /// <summary>
    /// Declining the Agent is a choice, not a failure: it is Lite mode, which exists so a first run never
    /// dead-ends on a UAC prompt. The message says what happens next rather than what went wrong.
    /// </summary>
    [Fact]
    public void Onboarding_SkipAgent_ContinuesAndSaysSo()
    {
        var viewModel = Build(new FakeAgentSetup());
        viewModel.ContinueCommand.Execute(null);

        viewModel.SkipAgentCommand.Execute(null);

        viewModel.IsDefaults.ShouldBeTrue();
        viewModel.AgentOutcome.ShouldBe("Continuing without the Agent. You can install it later from Settings.");
    }

    [Fact]
    public async Task Onboarding_InstallAgent_Succeeding_SaysInstalled()
    {
        var setup = new FakeAgentSetup { Installed = true };
        var viewModel = Build(setup);

        await viewModel.InstallAgentCommand.ExecuteAsync(null);

        setup.InstallCalls.ShouldBe(1);
        viewModel.IsDefaults.ShouldBeTrue();
        viewModel.AgentOutcome.ShouldBe("The Agent is installed and running.");
    }

    /// <summary>A dismissed UAC prompt reads exactly like choosing Lite mode, because it is the same choice.</summary>
    [Fact]
    public async Task Onboarding_InstallAgent_Declined_ReadsTheSameAsSkipping()
    {
        var viewModel = Build(new FakeAgentSetup { Installed = false });

        await viewModel.InstallAgentCommand.ExecuteAsync(null);

        viewModel.IsDefaults.ShouldBeTrue();
        viewModel.AgentOutcome.ShouldBe("Continuing without the Agent. You can install it later from Settings.");
    }

    [Fact]
    public async Task Onboarding_WhileInstalling_TheButtonIsDisabled()
    {
        var viewModel = Build(new FakeAgentSetup());
        viewModel.CanInstall.ShouldBeTrue();

        await viewModel.InstallAgentCommand.ExecuteAsync(null);

        viewModel.CanInstall.ShouldBeTrue("the prompt is finished");
    }

    /// <summary>
    /// Only finishing records that the Gate was shown. A run closed half-way through has to see it again -
    /// which is the right way round for the one screen the product owes the user.
    /// </summary>
    [Fact]
    public void Onboarding_NotFinished_LeavesTheGateUnshown()
    {
        var viewModel = Build(new FakeAgentSetup());
        viewModel.ContinueCommand.Execute(null);
        viewModel.SkipAgentCommand.Execute(null);

        Store().Load().OnboardingCompleted.ShouldBeFalse();
    }

    [Fact]
    public void Onboarding_Finish_RecordsTheAnswersAndCallsBack()
    {
        var finished = false;
        var viewModel = Build(new FakeAgentSetup(), () => finished = true);
        viewModel.RetentionDays = 90;

        viewModel.FinishCommand.Execute(null);

        finished.ShouldBeTrue();

        var saved = Store().Load();
        saved.OnboardingCompleted.ShouldBeTrue();
        saved.RetentionDays.ShouldBe(90);
    }

    /// <summary>The slider cannot produce these, but a hand-edited settings file can.</summary>
    [Theory]
    [InlineData(1, 30)]
    [InlineData(4000, 365)]
    public void Onboarding_Finish_ClampsRetentionToTheDocumentedRange(int chosen, int expected)
    {
        var viewModel = Build(new FakeAgentSetup());
        viewModel.RetentionDays = chosen;

        viewModel.FinishCommand.Execute(null);

        Store().Load().RetentionDays.ShouldBe(expected);
    }

    [Fact]
    public void Onboarding_RetentionLabel_FollowsTheSlider()
    {
        var viewModel = Build(new FakeAgentSetup());
        viewModel.RetentionDays = 45;

        viewModel.RetentionLabel.ShouldBe("Keep history for 45 days");
    }

    // -- the start-agent offer ---------------------------------------------------------------------------

    private static AgentStatus Lite(bool taskInstalled) => new(
        ConnectionMode.Lite, null, new Dictionary<string, SensorStatePayload>(), taskInstalled);

    /// <summary>
    /// Two Lite cases, two different offers. With no task the answer is Agent setup and a UAC prompt; with a
    /// task that is merely not running, starting it needs no elevation at all.
    /// </summary>
    [Fact]
    public void Home_LiteWithATaskInstalled_OffersToStartIt()
    {
        var client = new FakeAgentClient();
        var viewModel = new HomeViewModel(client, new FakeAgentSetup { TaskExists = true });

        client.Publish(Lite(taskInstalled: false));

        viewModel.CanStartAgent.ShouldBeTrue("the task exists even though the status did not say so");
    }

    [Fact]
    public void Home_LiteWithNoTask_DoesNotOfferToStartOne()
    {
        var client = new FakeAgentClient();
        var viewModel = new HomeViewModel(client, new FakeAgentSetup { TaskExists = false });

        client.Publish(Lite(taskInstalled: false));

        viewModel.CanStartAgent.ShouldBeFalse();
    }

    [Fact]
    public void Home_ConnectedToAnAgent_DoesNotOfferToStartOne()
    {
        var client = new FakeAgentClient();
        var viewModel = new HomeViewModel(client, new FakeAgentSetup { TaskExists = true });

        client.Publish(new AgentStatus(
            ConnectionMode.Full, "0.2.0", new Dictionary<string, SensorStatePayload>(), TaskInstalled: true));

        viewModel.CanStartAgent.ShouldBeFalse();
    }

    [Fact]
    public async Task Home_StartAgent_AsksTheTaskScheduler()
    {
        var setup = new FakeAgentSetup { TaskExists = true };
        var client = new FakeAgentClient();
        var viewModel = new HomeViewModel(client, setup);

        await viewModel.StartAgentCommand.ExecuteAsync(null);

        setup.StartCalls.ShouldBe(1);
    }
}
