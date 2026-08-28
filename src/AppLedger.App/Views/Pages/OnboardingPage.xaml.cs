using AppLedger.App.ViewModels;
using Wpf.Ui.Abstractions.Controls;

namespace AppLedger.App.Views.Pages;

/// <summary>The first-run flow (docs/08_UI.md §Onboarding).</summary>
public partial class OnboardingPage : INavigableView<OnboardingViewModel>
{
    /// <summary>Creates the page with its view-model injected.</summary>
    public OnboardingPage(OnboardingViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        ViewModel = viewModel;
        DataContext = this;

        InitializeComponent();
    }

    /// <inheritdoc />
    public OnboardingViewModel ViewModel { get; }
}
