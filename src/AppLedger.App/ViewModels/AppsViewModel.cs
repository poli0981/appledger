using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using AppLedger.App.Resources;
using AppLedger.App.Services;
using AppLedger.Ipc.Streams;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AppLedger.App.ViewModels;

/// <summary>One row of the running-apps grid.</summary>
/// <remarks>
/// A view-model per app that <b>survives between ticks</b>. The grid binds to these once and the numbers
/// change underneath, which is what keeps selection, scroll position and sort order across a 1 Hz refresh.
/// </remarks>
public sealed partial class AppRowViewModel : ObservableObject
{
    /// <summary>Creates a row for an app.</summary>
    public AppRowViewModel(string appId)
    {
        ArgumentException.ThrowIfNullOrEmpty(appId);
        AppId = appId;
    }

    /// <summary>The app this row is, and the key it is matched on.</summary>
    public string AppId { get; }

    /// <summary>Live process instances.</summary>
    [ObservableProperty]
    private int _procs;

    /// <summary>CPU percentage, 0-100.</summary>
    [ObservableProperty]
    private double _cpuPct;

    /// <summary>Private working set, bytes.</summary>
    [ObservableProperty]
    private long _wsPrivate;

    /// <summary>GPU percentage, 0-100.</summary>
    [ObservableProperty]
    private double _gpuPct;

    /// <summary>Real device read bytes in the last second.</summary>
    [ObservableProperty]
    private long _diskRead;

    /// <summary>Real device write bytes in the last second.</summary>
    [ObservableProperty]
    private long _diskWrite;

    /// <summary>Network payload bytes received in the last second.</summary>
    [ObservableProperty]
    private long _netIn;

    /// <summary>Network payload bytes sent in the last second.</summary>
    [ObservableProperty]
    private long _netOut;

    /// <summary>Ticks since this row was last seen, so a vanished app can be removed.</summary>
    internal int MissedTicks { get; set; }

    internal void Apply(in AppRow row)
    {
        Procs = row.Procs;
        CpuPct = row.CpuPct;
        WsPrivate = row.WsPrivate;
        GpuPct = row.GpuPct;
        DiskRead = row.DiskRead;
        DiskWrite = row.DiskWrite;
        NetIn = row.NetIn;
        NetOut = row.NetOut;
        MissedTicks = 0;
    }
}

/// <summary>
/// The running-apps grid of FR-1 (docs/08_UI.md §AppsPage).
/// </summary>
/// <remarks>
/// <b>The tick is never bound to <c>ItemsSource</c> directly.</b> Replacing the collection once a second
/// resets selection, scroll position and sort on every refresh, and rebuilds every row container — a
/// documented gotcha (docs/22_WPFUI_SYNTAX.md §Gotchas) whose symptom is a grid the user cannot actually
/// interact with. Rows are matched by <c>app_id</c> and updated in place instead.
/// </remarks>
public sealed partial class AppsViewModel : ObservableObject
{
    /// <summary>
    /// How many consecutive ticks an app may be absent before its row goes.
    /// </summary>
    /// <remarks>
    /// Not one. An app that produced no samples for a single second — because every one of its instances
    /// happened to be between deltas — would otherwise flicker out of the grid and back in.
    /// </remarks>
    public const int TicksBeforeRemoval = 3;

    private readonly Dictionary<string, AppRowViewModel> _byAppId = new(StringComparer.Ordinal);

    /// <summary>Subscribes to the client for the lifetime of the application.</summary>
    /// <remarks>
    /// A singleton, unlike the page. The rows are live state that must survive navigation (docs/22
    /// §Navigation), and a transient view-model would leave every previous instance subscribed to the
    /// client - applying each tick once per visit the user has ever made to this page.
    /// </remarks>
    public AppsViewModel(IAgentClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        client.AppsTick += Apply;
        client.StatusChanged += Apply;
        Apply(client.Status);
    }

    /// <summary>The page heading.</summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "{Binding ViewModel.Title} resolves against an instance; a static member cannot be bound.")]
    public string Title => Strings.Page_Apps_Title;

    /// <summary>The rows the grid binds to, once.</summary>
    public ObservableCollection<AppRowViewModel> Rows { get; } = [];

    /// <summary>True when the numbers are missing the ETW-derived columns.</summary>
    [ObservableProperty]
    private bool _isLite;

    /// <summary>Folds one tick into the existing rows.</summary>
    public void Apply(IReadOnlyList<AppRow> tick)
    {
        ArgumentNullException.ThrowIfNull(tick);

        foreach (var row in _byAppId.Values)
        {
            row.MissedTicks++;
        }

        foreach (var row in tick)
        {
            if (string.IsNullOrEmpty(row.AppId))
            {
                continue;
            }

            if (!_byAppId.TryGetValue(row.AppId, out var existing))
            {
                existing = new AppRowViewModel(row.AppId);
                _byAppId[row.AppId] = existing;
                Rows.Add(existing);
            }

            existing.Apply(row);
        }

        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            if (Rows[i].MissedTicks < TicksBeforeRemoval)
            {
                continue;
            }

            _byAppId.Remove(Rows[i].AppId);
            Rows.RemoveAt(i);
        }
    }

    /// <summary>Reflects a change of connection state.</summary>
    public void Apply(AgentStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        IsLite = status.Mode == ConnectionMode.Lite;
    }
}
