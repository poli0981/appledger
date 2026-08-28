using System.Diagnostics.CodeAnalysis;
using AppLedger.App.Resources;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AppLedger.App.ViewModels;

/// <summary>State the window chrome binds to.</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    /// <summary>The window and title-bar caption.</summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "{Binding ViewModel.Title} resolves against an instance; a static member cannot be bound.")]
    public string Title => Strings.App_Title;

    /// <summary>
    /// Unacknowledged alerts, shown as an InfoBadge on the Alerts rail item.
    /// </summary>
    /// <remarks>
    /// Zero until the Events tab exists (v0.6). The badge is bound now because the rail item is, and a
    /// binding to a property that does not exist fails silently in WPF - it would be found much later.
    /// </remarks>
    [ObservableProperty]
    private int _unreadAlerts;
}
