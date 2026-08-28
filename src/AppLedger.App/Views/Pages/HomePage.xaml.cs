using AppLedger.App.ViewModels;
using Wpf.Ui.Abstractions.Controls;

namespace AppLedger.App.Views.Pages;

/// <summary>docs/08_UI.md §HomePage. Contents land with the Agent client; the page exists now so navigation is complete.</summary>
public partial class HomePage : INavigableView<HomeViewModel>
{
    /// <summary>Creates the page with its view-model injected.</summary>
    public HomePage(HomeViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        ViewModel = viewModel;
        DataContext = this;

        InitializeComponent();
    }

    /// <inheritdoc />
    public HomeViewModel ViewModel { get; }
}
