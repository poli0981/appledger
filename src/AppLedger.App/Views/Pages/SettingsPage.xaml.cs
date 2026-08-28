using AppLedger.App.ViewModels;
using Wpf.Ui.Abstractions.Controls;

namespace AppLedger.App.Views.Pages;

/// <summary>docs/08_UI.md §SettingsPage. Registered for navigation now; completed in v0.7 (FR-18).</summary>
public partial class SettingsPage : INavigableView<SettingsViewModel>
{
    /// <summary>Creates the page with its view-model injected.</summary>
    public SettingsPage(SettingsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        ViewModel = viewModel;
        DataContext = this;

        InitializeComponent();
    }

    /// <inheritdoc />
    public SettingsViewModel ViewModel { get; }
}
