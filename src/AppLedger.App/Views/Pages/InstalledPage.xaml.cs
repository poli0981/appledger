using AppLedger.App.ViewModels;
using Wpf.Ui.Abstractions.Controls;

namespace AppLedger.App.Views.Pages;

/// <summary>docs/08_UI.md §InstalledPage. Registered for navigation now; its content needs the installed-apps index (v0.3).</summary>
public partial class InstalledPage : INavigableView<InstalledViewModel>
{
    /// <summary>Creates the page with its view-model injected.</summary>
    public InstalledPage(InstalledViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        ViewModel = viewModel;
        DataContext = this;

        InitializeComponent();
    }

    /// <inheritdoc />
    public InstalledViewModel ViewModel { get; }
}
