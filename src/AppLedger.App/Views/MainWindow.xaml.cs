using System.Windows;
using AppLedger.App.ViewModels;
using Wpf.Ui;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace AppLedger.App.Views;

/// <summary>
/// The shell (docs/08_UI.md §Navigation).
/// </summary>
/// <remarks>
/// Code-behind is wiring only, which is the rule rather than a preference: anything with a decision in it
/// belongs in a view-model where a test can reach it without a window (docs/19_TESTING.md §UI — "view-model
/// tests, no XAML").
/// </remarks>
public partial class MainWindow : FluentWindow, INavigationWindow
{
    private readonly IServiceProvider _services;

    /// <summary>Creates the window and attaches the services that need its visual tree.</summary>
    public MainWindow(
        MainWindowViewModel viewModel,
        IServiceProvider services,
        INavigationViewPageProvider pageProvider,
        INavigationService navigationService,
        IContentDialogService contentDialogService,
        ISnackbarService snackbarService)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(pageProvider);
        ArgumentNullException.ThrowIfNull(navigationService);
        ArgumentNullException.ThrowIfNull(contentDialogService);
        ArgumentNullException.ThrowIfNull(snackbarService);

        ViewModel = viewModel;
        _services = services;
        DataContext = this;

        InitializeComponent();

        SetPageService(pageProvider);
        navigationService.SetNavigationControl(RootNavigation);
        contentDialogService.SetDialogHost(RootContentDialog);
        snackbarService.SetSnackbarPresenter(SnackbarPresenter);

        Loaded += OnLoaded;
    }

    /// <summary>State the chrome binds to.</summary>
    public MainWindowViewModel ViewModel { get; }

    /// <inheritdoc />
    public INavigationView GetNavigation() => RootNavigation;

    /// <inheritdoc />
    public bool Navigate(Type pageType) => RootNavigation.Navigate(pageType);

    /// <inheritdoc />
    public void SetPageService(INavigationViewPageProvider navigationViewPageProvider) =>
        RootNavigation.SetPageProviderService(navigationViewPageProvider);

    /// <inheritdoc />
    public void SetServiceProvider(IServiceProvider serviceProvider)
    {
        // The provider is taken through the constructor instead; this member exists because the interface
        // requires it, and a second, later-arriving provider is exactly the kind of hidden state the
        // constructor injection above is meant to avoid.
    }

    /// <inheritdoc />
    public void ShowWindow() => Show();

    /// <inheritdoc />
    public void CloseWindow() => Close();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Needs an HWND, so it cannot go in the constructor - the window has no handle until it is loaded
        // (docs/22_WPFUI_SYNTAX.md §Gotchas).
        SystemThemeWatcher.Watch(this);
    }

    private void OnExitClicked(object sender, RoutedEventArgs e) => Close();
}
