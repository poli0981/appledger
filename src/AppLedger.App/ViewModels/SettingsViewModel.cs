using System.Diagnostics.CodeAnalysis;
using AppLedger.App.Resources;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AppLedger.App.ViewModels;

/// <summary>docs/08_UI.md §SettingsPage. Registered for navigation now; completed in v0.7 (FR-18).</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    /// <summary>The page heading.</summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "{Binding ViewModel.Title} resolves against an instance; a static member cannot be bound.")]
    public string Title => Strings.Page_Settings_Title;
}
