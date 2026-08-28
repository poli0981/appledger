using AppLedger.App.ViewModels;
using Wpf.Ui.Abstractions.Controls;

namespace AppLedger.App.Views.Pages;

/// <summary>docs/08_UI.md §AlertsPage. Registered for navigation now; its content needs the event detector (v0.6).</summary>
public partial class AlertsPage : INavigableView<AlertsViewModel>
{
    /// <summary>Creates the page with its view-model injected.</summary>
    public AlertsPage(AlertsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        ViewModel = viewModel;
        DataContext = this;

        InitializeComponent();
    }

    /// <inheritdoc />
    public AlertsViewModel ViewModel { get; }
}
