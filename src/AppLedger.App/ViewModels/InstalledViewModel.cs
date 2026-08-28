using System.Diagnostics.CodeAnalysis;
using AppLedger.App.Resources;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AppLedger.App.ViewModels;

/// <summary>docs/08_UI.md §InstalledPage. Registered for navigation now; its content needs the installed-apps index (v0.3).</summary>
public sealed partial class InstalledViewModel : ObservableObject
{
    /// <summary>The page heading.</summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "{Binding ViewModel.Title} resolves against an instance; a static member cannot be bound.")]
    public string Title => Strings.Page_Installed_Title;
}
