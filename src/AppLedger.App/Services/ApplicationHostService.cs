using System.Windows;
using AppLedger.App.Views;
using AppLedger.App.Views.Pages;
using Microsoft.Extensions.Hosting;
using Wpf.Ui;

namespace AppLedger.App.Services;

/// <summary>
/// Creates and shows the main window once the host is running (docs/22_WPFUI_SYNTAX.md §Bootstrap).
/// </summary>
/// <remarks>
/// The window is a hosted service rather than a StartupUri so it is constructed <i>through the container</i>,
/// which is what lets pages and view-models take their dependencies as constructor parameters instead of
/// reaching for a static locator.
/// </remarks>
public sealed class ApplicationHostService : IHostedService
{
    private readonly IServiceProvider _services;

    /// <summary>Creates the service.</summary>
    public ApplicationHostService(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Resolved before the window so the two data view-models exist and are subscribed before the first
        // tick arrives; a tick raised with nobody listening is a second of data nothing ever draws.
        _ = _services.GetService(typeof(ViewModels.HomeViewModel));
        _ = _services.GetService(typeof(ViewModels.AppsViewModel));

        if (Application.Current.Windows.OfType<MainWindow>().Any())
        {
            return;
        }

        var window = _services.GetService(typeof(INavigationWindow)) as INavigationWindow
            ?? throw new InvalidOperationException("The navigation window is not registered.");

        window.ShowWindow();

        // Home is the landing page of docs/08_UI.md §Navigation. Navigating explicitly rather than relying
        // on the first rail item keeps that a decision rather than an accident of ordering.
        window.Navigate(typeof(HomePage));

        if (_services.GetService(typeof(IAgentClient)) is IAgentClient client)
        {
            await client.StartAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_services.GetService(typeof(IAgentClient)) is IAgentClient client)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }
}
