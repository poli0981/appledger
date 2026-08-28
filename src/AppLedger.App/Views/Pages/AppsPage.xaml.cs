using AppLedger.App.ViewModels;
using Wpf.Ui.Abstractions.Controls;

namespace AppLedger.App.Views.Pages;

/// <summary>docs/08_UI.md §AppsPage — the running-apps grid of FR-1.</summary>
public partial class AppsPage : INavigableView<AppsViewModel>
{
    /// <summary>Creates the page with its view-model injected.</summary>
    public AppsPage(AppsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        ViewModel = viewModel;
        DataContext = this;

        InitializeComponent();
    }

    /// <inheritdoc />
    public AppsViewModel ViewModel { get; }
}
