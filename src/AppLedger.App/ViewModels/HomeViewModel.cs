using System.Diagnostics.CodeAnalysis;
using AppLedger.App.Resources;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AppLedger.App.ViewModels;

/// <summary>docs/08_UI.md §HomePage. Contents land with the Agent client; the page exists now so navigation is complete.</summary>
public sealed partial class HomeViewModel : ObservableObject
{
    /// <summary>The page heading.</summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "{Binding ViewModel.Title} resolves against an instance; a static member cannot be bound.")]
    public string Title => Strings.Page_Home_Title;
}
