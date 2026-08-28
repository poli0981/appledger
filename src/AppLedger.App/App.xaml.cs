using System.Windows;
using System.Windows.Threading;
using AppLedger.App.Services;
using AppLedger.App.ViewModels;
using AppLedger.App.Views;
using AppLedger.App.Views.Pages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wpf.Ui;
using Wpf.Ui.DependencyInjection;

namespace AppLedger.App;

/// <summary>
/// The UI's entry point and composition root (docs/22_WPFUI_SYNTAX.md §Bootstrap).
/// </summary>
/// <remarks>
/// The App is a <b>standard user</b> process and always will be: a chart-heavy WPF UI must not run elevated,
/// because UIPI would block drag-and-drop from Explorer and every UI bug would become an elevated bug
/// (ADR-2). Anything that needs elevation is asked of the Agent over the pipe, or done once through
/// <c>--install-task</c> under <c>runas</c>.
/// </remarks>
public partial class App : Application
{
    private readonly IHost _host = Host.CreateDefaultBuilder()
        .ConfigureServices(ConfigureServices)
        .Build();

    /// <summary>Resolves a service from the running host. Used by XAML-constructed objects only.</summary>
    public static T GetRequiredService<T>()
        where T : class =>
        ((App)Current)._host.Services.GetRequiredService<T>();

    internal static void ConfigureServices(IServiceCollection services)
    {
        // Delivers page instances to the NavigationView from the container, so pages get constructor DI.
        services.AddNavigationViewPageProvider();

        // Creates and shows the window once the host has started.
        services.AddHostedService<ApplicationHostService>();

        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<ITaskBarService, TaskBarService>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<ISnackbarService, SnackbarService>();
        services.AddSingleton<IContentDialogService, ContentDialogService>();

        services.AddSingleton<INavigationWindow, MainWindow>();
        services.AddSingleton<MainWindowViewModel>();

        // The Agent over the pipe, or the collector in this process when none answers (docs/01 §Lite mode).
        services.AddSingleton<IAgentClient>(_ => new AgentClient());
        services.AddSingleton<AppSettingsStore>();
        services.AddSingleton<IAgentSetup, AgentSetup>();

        // Every page named in MainWindow's TargetPageType must be registered here. A missing registration
        // throws at runtime the first time somebody clicks that rail item, not at build time - which is why
        // a navigation smoke test walks all of them (docs/22 §Gotchas, docs/19 §UI).
        // Pages transient, but the two view-models holding live state are singletons: they subscribe to the
        // client once, and their rows have to survive navigating away and back (docs/22 §Navigation).
        services.AddTransient<HomePage>();
        services.AddSingleton<HomeViewModel>();
        services.AddTransient<AppsPage>();
        services.AddSingleton<AppsViewModel>();
        services.AddTransient<InstalledPage>();
        services.AddTransient<InstalledViewModel>();
        services.AddTransient<AlertsPage>();
        services.AddTransient<AlertsViewModel>();
        services.AddTransient<SettingsPage>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<OnboardingPage>();

        // Built by hand because the completion callback is not a service: onboarding ends by navigating to
        // Home, and expressing that as a dependency would make the view-model know about pages.
        services.AddTransient(provider => new OnboardingViewModel(
            provider.GetRequiredService<AppSettingsStore>(),
            provider.GetRequiredService<IAgentSetup>(),
            () => provider.GetRequiredService<INavigationService>().Navigate(typeof(HomePage))));

        // AppPage - the app-detail page of docs/08 - is not registered yet. It arrives with the identity
        // resolver in v0.3, and nothing can navigate to it before then; registering a page that cannot be
        // reached would only make the smoke test pass for a route that does not exist.
    }

    /// <inheritdoc />
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        await _host.StartAsync().ConfigureAwait(true);
    }

    /// <inheritdoc />
    protected override async void OnExit(ExitEventArgs e)
    {
        await _host.StopAsync().ConfigureAwait(true);
        _host.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Last resort for an exception that reached the dispatcher.
    /// </summary>
    /// <remarks>
    /// Left unhandled so the process fails visibly rather than continuing in an unknown state. NFR-5 says
    /// the UI always renders with whatever is available, which is about <i>missing data</i> - it is not a
    /// licence to keep drawing after the code that produces the data has thrown.
    /// </remarks>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
    }
}
