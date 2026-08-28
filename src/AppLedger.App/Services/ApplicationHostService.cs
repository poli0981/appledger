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
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Application.Current.Windows.OfType<MainWindow>().Any())
        {
            return Task.CompletedTask;
        }

        var window = _services.GetService(typeof(INavigationWindow)) as INavigationWindow
            ?? throw new InvalidOperationException("The navigation window is not registered.");

        window.ShowWindow();

        // Home is the landing page of docs/08_UI.md §Navigation. Navigating explicitly rather than relying
        // on the first rail item keeps that a decision rather than an accident of ordering.
        window.Navigate(typeof(HomePage));

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
