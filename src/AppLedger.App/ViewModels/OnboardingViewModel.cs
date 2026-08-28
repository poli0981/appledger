using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using AppLedger.App.Resources;
using AppLedger.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AppLedger.App.ViewModels;

/// <summary>
/// The three-step first run of docs/08_UI.md §Onboarding: Privacy Gate, Agent setup, defaults.
/// </summary>
/// <remarks>
/// <b>The Privacy Gate is not a consent dialog and has no toggles.</b> It says what is recorded, where it
/// stays, for how long, and who can read it, and then the user continues (docs/12 §Privacy Gate). Asking for
/// agreement would imply the defaults are negotiable at this point; they are product decisions, and the
/// screen's job is to make sure nobody discovers them later.
/// <para>
/// Agent setup can be declined. Declining is not a failure path — it is Lite mode, which exists so the first
/// run never dead-ends on a UAC prompt (docs/01 §Lite mode).
/// </para>
/// </remarks>
public sealed partial class OnboardingViewModel : ObservableObject
{
    /// <summary>How many steps there are, for "Step 1 of 3".</summary>
    public const int StepCount = 3;

    private readonly AppSettingsStore _settings;
    private readonly IAgentSetup _setup;
    private readonly Action _completed;

    /// <summary>Creates the view-model.</summary>
    /// <param name="settings">Where the answer is recorded.</param>
    /// <param name="setup">How the Agent gets installed.</param>
    /// <param name="completed">Called once, when the user finishes.</param>
    public OnboardingViewModel(AppSettingsStore settings, IAgentSetup setup, Action completed)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(completed);

        _settings = settings;
        _setup = setup;
        _completed = completed;

        RetentionDays = settings.Load().RetentionDays;
    }

    /// <summary>Zero-based step.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepLabel))]
    [NotifyPropertyChangedFor(nameof(IsPrivacyGate))]
    [NotifyPropertyChangedFor(nameof(IsAgentSetup))]
    [NotifyPropertyChangedFor(nameof(IsDefaults))]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    private int _step;

    /// <summary>"Step 1 of 3", localized.</summary>
    /// <remarks>
    /// <c>CurrentUICulture</c> chose the string; <c>CurrentCulture</c> formats the numbers inside it. They
    /// are different settings and docs/14 §Rules keeps them apart - a Vietnamese UI on a machine set to
    /// en-US should read Vietnamese with that machine's number format.
    /// </remarks>
    [SuppressMessage("Performance", "CA1863:Use CompositeFormat",
        Justification = "The format string is a localized resource; caching one would pin a single language.")]
    public string StepLabel => string.Format(
        CultureInfo.CurrentCulture, Strings.Onboarding_Step, Step + 1, StepCount);

    /// <summary>True on the Privacy Gate.</summary>
    public bool IsPrivacyGate => Step == 0;

    /// <summary>True on Agent setup.</summary>
    public bool IsAgentSetup => Step == 1;

    /// <summary>True on the defaults step.</summary>
    public bool IsDefaults => Step == 2;

    /// <summary>True anywhere but the first step.</summary>
    public bool CanGoBack => Step > 0;

    /// <summary>How long history is kept. docs/12: 180 days by default, 30-365.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RetentionLabel))]
    private int _retentionDays = 180;

    /// <summary>"Keep history for 180 days", localized.</summary>
    [SuppressMessage("Performance", "CA1863:Use CompositeFormat",
        Justification = "The format string is a localized resource; caching one would pin a single language.")]
    public string RetentionLabel => string.Format(
        CultureInfo.CurrentCulture, Strings.Defaults_Retention, RetentionDays);

    /// <summary>What the Agent-setup step is currently saying, or null before anything was tried.</summary>
    [ObservableProperty]
    private string? _agentOutcome;

    /// <summary>True while the UAC prompt is up, so the buttons can be disabled.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    private bool _isInstalling;

    /// <summary>False only while a prompt is already up, so it cannot be raised twice.</summary>
    public bool CanInstall => !IsInstalling;

    /// <summary>Moves forward one step.</summary>
    [RelayCommand]
    private void Continue()
    {
        if (Step < StepCount - 1)
        {
            Step++;
        }
    }

    /// <summary>Moves back one step.</summary>
    [RelayCommand]
    private void Back()
    {
        if (Step > 0)
        {
            Step--;
        }
    }

    /// <summary>Opens the full privacy policy.</summary>
    /// <remarks>
    /// Through the shell rather than an in-app viewer: it is a document the user should be able to keep open
    /// beside the window, and the UI has no business rendering markdown to show one file.
    /// </remarks>
    [RelayCommand]
    private static void ReadPolicy()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/poli0981/appledger/blob/main/legal/PRIVACY_POLICY.md",
                UseShellExecute = true,
            })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No browser, or a policy that blocks launching one. Not a reason to interrupt onboarding.
        }
    }

    /// <summary>Asks the Agent to install its Scheduled Task, under one UAC prompt.</summary>
    [RelayCommand]
    private async Task InstallAgentAsync(CancellationToken cancellationToken)
    {
        IsInstalling = true;
        try
        {
            var installed = await _setup.InstallAsync(cancellationToken).ConfigureAwait(true);

            // Declining the prompt is a choice, and the message says what happens next rather than what
            // went wrong - because nothing did.
            AgentOutcome = installed ? Strings.Agent_Setup_Installed : Strings.Agent_Setup_Declined;
            Step = 2;
        }
        finally
        {
            IsInstalling = false;
        }
    }

    /// <summary>Continues without the Agent.</summary>
    [RelayCommand]
    private void SkipAgent()
    {
        AgentOutcome = Strings.Agent_Setup_Declined;
        Step = 2;
    }

    /// <summary>Records the answers and leaves onboarding.</summary>
    [RelayCommand]
    private void Finish()
    {
        var current = _settings.Load();
        _settings.Save(current with
        {
            RetentionDays = Math.Clamp(RetentionDays, AppSettings.MinRetentionDays, AppSettings.MaxRetentionDays),

            // Only this line marks the Gate as shown, and only here. Setting it anywhere earlier would let a
            // half-finished first run skip the one screen the product is obliged to show.
            OnboardingCompleted = true,
        });

        _completed();
    }
}
