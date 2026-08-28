using System.Diagnostics.CodeAnalysis;
using AppLedger.App.Resources;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AppLedger.App.ViewModels;

/// <summary>docs/08_UI.md §AlertsPage. Registered for navigation now; its content needs the event detector (v0.6).</summary>
public sealed partial class AlertsViewModel : ObservableObject
{
    /// <summary>The page heading.</summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "{Binding ViewModel.Title} resolves against an instance; a static member cannot be bound.")]
    public string Title => Strings.Page_Alerts_Title;
}
