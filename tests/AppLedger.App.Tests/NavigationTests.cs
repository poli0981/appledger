using System.Text.RegularExpressions;
using AppLedger.App.ViewModels;
using AppLedger.App.Views.Pages;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace AppLedger.App.Tests;

/// <summary>
/// The navigation smoke test of docs/19_TESTING.md §UI: every <c>TargetPageType</c> in the shell is
/// registered in the container.
/// </summary>
/// <remarks>
/// This is the failure docs/22_WPFUI_SYNTAX.md §Gotchas warns about, and its shape is what makes a test
/// worth writing: a page named in XAML but missing from <c>ConfigureServices</c> compiles, links, starts and
/// renders — and then throws the first time a user clicks that item in the rail. There is no build error and
/// no warning anywhere.
/// <para>
/// The XAML is read as text rather than the pages being constructed, deliberately: constructing a WPF page
/// needs an STA thread and a running application, and the thing that actually breaks is the *registration*,
/// not the construction.
/// </para>
/// </remarks>
public sealed partial class NavigationTests
{
    /// <summary>
    /// The registrations, inspected rather than resolved.
    /// </summary>
    /// <remarks>
    /// Resolving a page would construct a WPF <c>Page</c>, which needs an STA thread and a running
    /// <c>Application</c> — and construction is not what breaks. What breaks is a page named in the rail
    /// with no registration behind it, and that is visible in the descriptors.
    /// </remarks>
    private static readonly HashSet<Type> Registered = Describe();

    private static HashSet<Type> Describe()
    {
        var services = new ServiceCollection();
        App.ConfigureServices(services);
        return services.Select(d => d.ServiceType).ToHashSet();
    }

    private static string MainWindowXaml =>
        File.ReadAllText(Path.Combine(
            TestPaths.RepoRoot, "src", "AppLedger.App", "Views", "MainWindow.xaml"));

    [GeneratedRegex(@"TargetPageType=""\{x:Type pages:(?<page>\w+)\}""")]
    private static partial Regex TargetPageTypePattern { get; }

    /// <summary>The page names the shell's rail actually points at, read from the XAML.</summary>
    public static TheoryData<string> NavigatedPages()
    {
        var data = new TheoryData<string>();
        foreach (Match match in TargetPageTypePattern.Matches(MainWindowXaml))
        {
            data.Add(match.Groups["page"].Value);
        }

        return data;
    }

    [Fact]
    public void Shell_NamesEveryPageTheNavigationDocumentsRequires()
    {
        var pages = TargetPageTypePattern.Matches(MainWindowXaml)
            .Select(m => m.Groups["page"].Value)
            .ToList();

        // docs/08_UI.md §Navigation: Home, Apps, Installed, Alerts in the rail, Settings in the footer.
        pages.ShouldBe(["HomePage", "AppsPage", "InstalledPage", "AlertsPage", "SettingsPage"], ignoreOrder: true);
    }

    [Theory]
    [MemberData(nameof(NavigatedPages))]
    public void EveryNavigatedPage_IsRegisteredInTheContainer(string pageName)
    {
        var type = typeof(HomePage).Assembly.GetType($"AppLedger.App.Views.Pages.{pageName}");

        type.ShouldNotBeNull($"{pageName} is named in MainWindow.xaml but does not exist");
        Registered.ShouldContain(
            type,
            $"{pageName} is named in MainWindow.xaml but is not registered - clicking it would throw");
    }

    /// <summary>
    /// A page resolves only if its view-model does, and the constructor takes it. Registering the page and
    /// forgetting the view-model fails in exactly the same invisible way.
    /// </summary>
    [Theory]
    [MemberData(nameof(NavigatedPages))]
    public void EveryNavigatedPage_HasItsViewModelRegistered(string pageName)
    {
        var viewModelName = pageName.Replace("Page", "ViewModel", StringComparison.Ordinal);
        var type = typeof(HomeViewModel).Assembly.GetType($"AppLedger.App.ViewModels.{viewModelName}");

        type.ShouldNotBeNull($"{viewModelName} does not exist");
        Registered.ShouldContain(type, $"{viewModelName} is not registered");
    }

    /// <summary>The services docs/22 §Bootstrap names, each of which the shell's chrome needs to exist.</summary>
    [Theory]
    [InlineData(typeof(Wpf.Ui.IThemeService))]
    [InlineData(typeof(Wpf.Ui.ITaskBarService))]
    [InlineData(typeof(Wpf.Ui.INavigationService))]
    [InlineData(typeof(Wpf.Ui.ISnackbarService))]
    [InlineData(typeof(Wpf.Ui.IContentDialogService))]
    [InlineData(typeof(Wpf.Ui.Abstractions.INavigationViewPageProvider))]
    [InlineData(typeof(MainWindowViewModel))]
    public void ShellService_IsRegistered(Type service) =>
        Registered.ShouldContain(service, $"{service.Name} is not registered");

    /// <summary>
    /// AppPage is deliberately absent until v0.3: nothing can navigate to it, and registering an unreachable
    /// page would only make this suite pass for a route that does not exist.
    /// </summary>
    [Fact]
    public void AppDetailPage_IsNotYetPartOfTheShell() =>
        MainWindowXaml.ShouldNotContain("pages:AppPage");
}
