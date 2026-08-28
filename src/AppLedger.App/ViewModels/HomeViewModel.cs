using System.Diagnostics.CodeAnalysis;
using AppLedger.App.Resources;
using AppLedger.App.Services;
using AppLedger.Ipc;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AppLedger.App.ViewModels;

/// <summary>
/// The Agent health strip of docs/08_UI.md §HomePage — mode, cost, and what each sensor can do.
/// </summary>
/// <remarks>
/// The strip is the one place the product admits what it cannot see. A sensor that is <c>Unavailable</c>
/// makes its columns read "N/A" rather than zero, because a zero would claim we looked and found none
/// (docs/01_ARCHITECTURE.md §Degraded modes).
/// </remarks>
public sealed partial class HomeViewModel : ObservableObject
{
    /// <summary>Subscribes to the client for the lifetime of the application.</summary>
    /// <remarks>
    /// Registered as a singleton rather than transient, unlike the page that shows it. docs/22 §Navigation
    /// says anything that must survive navigation lives in a service, and live state is exactly that: a
    /// transient view-model would resubscribe on every visit to Home and leave the previous one attached to
    /// the client, so the tick would be applied once per visit ever made.
    /// </remarks>
    public HomeViewModel(IAgentClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        client.StatusChanged += Apply;
        client.HealthTick += Apply;
        Apply(client.Status);
    }

    /// <summary>The page heading.</summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "{Binding ViewModel.Title} resolves against an instance; a static member cannot be bound.")]
    public string Title => Strings.Page_Home_Title;

    /// <summary>Full, Degraded, Lite, or connecting — already localized.</summary>
    [ObservableProperty]
    private string _mode = Strings.Health_Mode_Connecting;

    /// <summary>True in Lite mode, which is what shows the banner.</summary>
    [ObservableProperty]
    private bool _isLite;

    /// <summary>The Agent's version, or null in Lite mode.</summary>
    [ObservableProperty]
    private string? _agentVersion;

    /// <summary>The Agent's CPU as a share of one core, or null when nothing has reported yet.</summary>
    [ObservableProperty]
    private double? _agentCpuPct;

    /// <summary>The Agent's private working set in bytes, or null when nothing has reported yet.</summary>
    [ObservableProperty]
    private long? _agentWorkingSet;

    /// <summary>Events the sensors have reported losing.</summary>
    [ObservableProperty]
    private long _eventsLost;

    /// <summary>One chip per sensor, in the order the Agent listed them.</summary>
    public System.Collections.ObjectModel.ObservableCollection<SensorChip> Sensors { get; } = [];

    /// <summary>Reflects a change of connection state.</summary>
    public void Apply(AgentStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        Mode = status.Mode switch
        {
            ConnectionMode.Full => Strings.Health_Mode_Full,
            ConnectionMode.Degraded => Strings.Health_Mode_Degraded,
            ConnectionMode.Lite => Strings.Health_Mode_Lite,
            _ => Strings.Health_Mode_Connecting,
        };

        IsLite = status.Mode == ConnectionMode.Lite;
        AgentVersion = status.AgentVersion;

        // Lite mode has no Agent, so its cost is this process's - which is not the same number and must not
        // be shown as though it were (docs/07 §Streams: agentCpuPct describes the hosting process).
        if (IsLite)
        {
            AgentCpuPct = null;
            AgentWorkingSet = null;
        }

        ApplySensors(status.Sensors);
    }

    /// <summary>Reflects one health tick.</summary>
    public void Apply(HealthPayload health)
    {
        ArgumentNullException.ThrowIfNull(health);

        AgentCpuPct = health.AgentCpuPct;
        AgentWorkingSet = health.AgentWs;
        EventsLost = health.EventsLost;

        ApplySensors(health.Sensors);
    }

    private void ApplySensors(IReadOnlyDictionary<string, SensorStatePayload> sensors)
    {
        Sensors.Clear();
        foreach (var (name, state) in sensors)
        {
            Sensors.Add(new SensorChip(name, state.State, state.Detail));
        }
    }
}

/// <summary>One sensor, as the strip shows it.</summary>
/// <param name="Name">The sensor's name, verbatim from <c>ISensor.Name</c> — never localized.</param>
/// <param name="State">Stopped, Starting, Running or Unavailable.</param>
/// <param name="Detail">A short reason when unavailable, such as a Win32 error number.</param>
public sealed record SensorChip(string Name, string State, string? Detail)
{
    /// <summary>True when this sensor is producing data.</summary>
    public bool IsRunning => State == "Running";
}
