# 22 — WPF-UI Syntax (lepoco `Wpf.Ui`, pinned 4.3.0)

Carried over from FrameLedger's `docs/16_WPFUI_SYNTAX.md` and adapted. Mandatory reading before any XAML or
view-model in `AppLedger.App`. NuGet ids: `WPF-UI` + `WPF-UI.DependencyInjection`, both **exactly 4.3.0**
(`[4.3.0]` in `Directory.Packages.props`). 4.0.0–4.0.3, 4.1.0 and 4.2.0 are deprecated on NuGet for critical bugs —
never "round up" to them; bump only after reading the changelog and re-running the manual UI matrix (`19_TESTING.md`).

## Bootstrap (Generic Host + DI, the WPF-UI template idiom)

```csharp
// App.xaml.cs
public partial class App : Application
{
    private static readonly IHost Host = Microsoft.Extensions.Hosting.Host
        .CreateDefaultBuilder()
        .UseSerilog((ctx, cfg) => LoggingSetup.Configure(cfg, DataRoot.Logs, "ui"))   // 15_LOGGING
        .ConfigureServices((ctx, services) =>
        {
            services.AddNavigationViewPageProvider();                 // WPF-UI.DependencyInjection
            services.AddHostedService<ApplicationHostService>();      // creates + shows MainWindow
            services.AddSingleton<IThemeService, ThemeService>();
            services.AddSingleton<ITaskBarService, TaskBarService>();
            services.AddSingleton<INavigationService, NavigationService>();
            services.AddSingleton<ISnackbarService, SnackbarService>();
            services.AddSingleton<IContentDialogService, ContentDialogService>();
            services.AddSingleton<INavigationWindow, MainWindow>();
            services.AddSingleton<MainWindowViewModel>();
            services.AddSingleton<IAgentClient, AgentClient>();       // 07_IPC
            services.AddSingleton<ILedgerReader, SqliteLedgerReader>(); // read-only DB
            // Pages + ViewModels: transient
            services.AddTransient<HomePage>();      services.AddTransient<HomeViewModel>();
            services.AddTransient<AppsPage>();      services.AddTransient<AppsViewModel>();
            services.AddTransient<AppPage>();       services.AddTransient<AppViewModel>();       // 08_UI.md names it AppPage
            services.AddTransient<SettingsPage>();  services.AddTransient<SettingsViewModel>();
        })
        .Build();

    protected override async void OnStartup(StartupEventArgs e)
    {
        VelopackApp.Build().Run();            // must be first (16_PACKAGING_AND_UPDATES)
        await Host.StartAsync();
    }
    protected override async void OnExit(ExitEventArgs e) { await Host.StopAsync(); Host.Dispose(); }
}
```

`App.xaml` — dictionary order is a hard rule (`ThemesDictionary` → `ControlsDictionary` → app styles):

```xml
<Application.Resources>
  <ResourceDictionary>
    <ResourceDictionary.MergedDictionaries>
      <ui:ThemesDictionary Theme="Dark" />
      <ui:ControlsDictionary />
      <ResourceDictionary Source="Styles/ChartPalette.xaml" />
      <ResourceDictionary Source="Styles/AppLedger.xaml" />
    </ResourceDictionary.MergedDictionaries>
  </ResourceDictionary>
</Application.Resources>
```

`xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"` on every file.

## Main window skeleton

```xml
<ui:FluentWindow x:Class="AppLedger.App.Views.MainWindow" ...
    ExtendsContentIntoTitleBar="True" WindowBackdropType="Mica" WindowCornerPreference="Round"
    Width="1280" Height="800" MinWidth="960" MinHeight="600">
  <Grid>
    <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="*"/></Grid.RowDefinitions>
    <ui:TitleBar Grid.Row="0" Title="AppLedger" Icon="{ui:ImageIcon 'pack://application:,,,/Assets/icon.ico'}" />
    <ui:NavigationView x:Name="RootNavigation" Grid.Row="1" PaneDisplayMode="Left" IsBackButtonVisible="Collapsed"
                       OpenPaneLength="200" IsPaneToggleVisible="True">
      <ui:NavigationView.MenuItems>
        <ui:NavigationViewItem Content="{x:Static res:Strings.Nav_Home}" Icon="{ui:SymbolIcon Home24}" TargetPageType="{x:Type pages:HomePage}"/>
        <ui:NavigationViewItem Content="{x:Static res:Strings.Nav_Apps}" Icon="{ui:SymbolIcon Apps24}" TargetPageType="{x:Type pages:AppsPage}"/>
        <ui:NavigationViewItem Content="{x:Static res:Strings.Nav_Installed}" Icon="{ui:SymbolIcon AppFolder24}" TargetPageType="{x:Type pages:InstalledPage}"/>
        <ui:NavigationViewItem Content="{x:Static res:Strings.Nav_Alerts}" Icon="{ui:SymbolIcon Alert24}" TargetPageType="{x:Type pages:AlertsPage}">
          <ui:NavigationViewItem.InfoBadge><ui:InfoBadge Value="{Binding ViewModel.UnreadAlerts}" Severity="Attention"/></ui:NavigationViewItem.InfoBadge>
        </ui:NavigationViewItem>
      </ui:NavigationView.MenuItems>
      <ui:NavigationView.FooterMenuItems>
        <ui:NavigationViewItem Content="{x:Static res:Strings.Nav_Settings}" Icon="{ui:SymbolIcon Settings24}" TargetPageType="{x:Type pages:SettingsPage}"/>
      </ui:NavigationView.FooterMenuItems>
    </ui:NavigationView>
    <ContentPresenter x:Name="RootContentDialog" Grid.Row="1"/>
    <ui:SnackbarPresenter x:Name="SnackbarPresenter" Grid.Row="1" VerticalAlignment="Bottom"/>
  </Grid>
</ui:FluentWindow>
```

Code-behind (wiring only): `navigationService.SetNavigationControl(RootNavigation)`,
`contentDialogService.SetDialogHost(RootContentDialog)`, `snackbarService.SetSnackbarPresenter(SnackbarPresenter)`;
in `Loaded`: `SystemThemeWatcher.Watch(this)` (needs an HWND — never in the constructor). Agent health strip and
Lite-mode `InfoBar` live in `HomePage`, not in the window chrome.

## Navigation

- Navigate **only** via `INavigationService.Navigate(typeof(SomePage))` (or `NavigateWithHierarchy` for App → App detail
  so the back button appears) — never touch `Frame`, never `new` a Page.
- Pages implement `INavigableView<TViewModel>` with `ViewModel` injected via constructor DI; `DataContext = this` in the
  page constructor so bindings read `ViewModel.*`.
- Pages are Transient; anything that must survive navigation (selected app, chart range, filters) lives in services.
- App detail is one page with a secondary tab strip (`ui:NavigationView` `PaneDisplayMode="Top"` inside the page, or a
  segmented `RadioButton` group styled as pivots); tabs are user controls, not separate navigation pages.

## Control mapping (always prefer these)

| Need | Use | Not |
|---|---|---|
| Button | `ui:Button` with `Appearance="Primary/Secondary/Danger"`, `Icon="{ui:SymbolIcon Play24}"` | bare `Button` |
| Text | `ui:TextBlock` with `FontTypography="Caption/Body/BodyStrong/Subtitle/Title"` | hardcoded `FontSize` |
| Text input | `ui:TextBox` (`PlaceholderText`, `Icon`) | native `TextBox` |
| Numbers | `ui:NumberBox` (`Minimum/Maximum/SmallChange`, `SpinButtonPlacementMode="Compact"` — ≥ 4.3.0) | TextBox + parsing |
| Toggle | `ui:ToggleSwitch` | CheckBox for on/off settings |
| Search | `ui:AutoSuggestBox` | |
| Cards / settings rows | `ui:Card`, `ui:CardControl`, `ui:CardExpander`, `ui:CardAction` | `GroupBox` |
| Banner (Lite mode, degraded, privacy notices) | `ui:InfoBar` (`Severity`, `IsClosable`) | custom colored borders |
| Badge (confidence "?", Tier-2 "zero-touch", alert counts) | `ui:InfoBadge` / `ui:Badge` | |
| Busy | `ui:ProgressRing` | |
| Contextual popup (metric source tooltip, FR-20) | `ui:Flyout` or `ToolTip` | hand-rolled Popup |
| Hyperlink-ish | `ui:HyperlinkButton`, `ui:Anchor` | |
| Tables (processes, connections, hosts, files) | native `DataGrid` (WPF-UI restyles it) with `EnableRowVirtualization` | third-party grids |

Native `Menu`, `TabControl`, `ComboBox`, `Slider`, `ListView` are fine — `ControlsDictionary` restyles standard WPF
controls to Fluent automatically. Tables with live 1 Hz updates bind to `ObservableCollection` items that implement
`INotifyPropertyChanged` per row (update rows, never replace the collection).

## Icons

- `{ui:SymbolIcon Symbol=Home24}` / `Icon="{ui:SymbolIcon Home24}"`; filled variant `Filled="True"`. Symbols come from
  the bundled **Fluent System Icons** font — validate names against the `SymbolRegular` enum (IntelliSense), don't guess.
- **Never** use Segoe Fluent Icons glyphs / `FontIcon` with Segoe: not bundled (license) and absent on Windows 10 → empty
  squares. Fluent System Icons only.
- App icons come from `cache\icons\<app_id>.png` (03 §Enrichment) via `ui:ImageIcon`; fallback `SymbolIcon AppGeneric24`.

## Theming & brushes

- Colors **only** via `{DynamicResource …}` theme keys: `TextFillColorPrimaryBrush`, `TextFillColorSecondaryBrush`,
  `TextFillColorTertiaryBrush`, `ControlFillColorDefaultBrush`, `CardBackgroundFillColorDefaultBrush`,
  `ApplicationBackgroundBrush`, `AccentTextFillColorPrimaryBrush`, `SystemFillColorCriticalBrush`,
  `SystemFillColorCautionBrush`, `SystemFillColorSuccessBrush`. `DynamicResource` always (theme changes at runtime);
  `StaticResource` for a theme brush is a review-blocking bug.
- Do **not** set `Background` on `FluentWindow` (kills Mica). Page backgrounds transparent by default.
- Exception to the no-hex rule: the chart palette — defined once per theme in `Styles/ChartPalette.xaml` (two
  dictionaries), never inline. Series colors: CPU, RAM, GPU, Disk read/write, Net in/out each have a fixed key so the same
  metric has the same color on every page.
- No `SystemColors.*` anywhere.

## Custom controls (`KpiTile`, `Sparkline`, `TierBadge`, `ConfidenceDot`, `UsageHeatmap` legend)

- Derive from `Control` with a `ControlTemplate` in `Styles/AppLedger.xaml`; templates use only theme brushes above →
  they re-theme for free.
- `KpiTile`: `ui:Card` look — caption (`Caption` typography), value (`Title`), unit, 60-point `Sparkline`, source tooltip
  (FR-20). `TierBadge`: Tier 0 "Windows" (`TextFillColorTertiaryBrush` outline), Tier 2 "zero-touch" (`SystemFillColorCautionBrush`).
  `ConfidenceDot`: filled accent ≥ 0.9, half ≥ 0.6, hollow below (with "?" and the assign action).
- `Sparkline` is a lightweight `DrawingVisual` (not ScottPlot) — 60–300 points, redrawn at 1 Hz, no allocations per frame.

## Dialogs & notifications

- Confirmations (purge, apply override to history): `Wpf.Ui.Controls.MessageBox` — **async**, instance-based:

```csharp
var box = new Wpf.Ui.Controls.MessageBox
{
    Title = Strings.Purge_Title,
    Content = Strings.Purge_Body,
    PrimaryButtonText = Strings.Common_Purge,
    PrimaryButtonAppearance = ControlAppearance.Danger,
    CloseButtonText = Strings.Common_Cancel,
};
var result = await box.ShowDialogAsync(); // Wpf.Ui.Controls.MessageBoxResult.Primary
```

  ⚠ Name clash with `System.Windows.MessageBox` — `using MessageBox = Wpf.Ui.Controls.MessageBox;` in App code; the
  System one is banned via `BannedSymbols.txt`.
- Rich in-flow dialogs (Privacy Gate, Agent setup, assign-to-app): `IContentDialogService` (host wired in MainWindow).
- Transient in-app: `ISnackbarService.Show(title, message, ControlAppearance.Success, new SymbolIcon(SymbolRegular.Checkmark24), TimeSpan.FromSeconds(4))`.
- System/tray: tray icon + context menu via **H.NotifyIcon** only; Windows toasts via `Microsoft.Toolkit.Uwp.Notifications`
  (`ToastContentBuilder`) — policy in `08_UI.md` §Notifications (toasts are opt-in, never for browser hosts).

## ScottPlot 5 (`ScottPlot.WPF` 5.1.59) theme sync & usage

- One `WpfPlot` per chart; data via `Plot.Add.SignalXY` (history), `DataLogger`/`DataStreamer` (live 1 Hz),
  `Plot.Add.Bars` (top-N), `Plot.Add.Heatmap` (usage calendar), `Plot.Add.VerticalLine` (version markers, `08_UI.md`).
- On startup and on `ApplicationThemeManager.Changed`: for every live plot set figure/data background, axis/grid/tick
  colors and the series palette from `ChartPalette.xaml`, then `Refresh()`. Centralize in `ChartTheme.Apply(Plot plot)` —
  pages never color plots ad hoc.
- Byte axes use `ByteFormatter` tick labels (`14_I18N.md`); time axes use `DateTimeAutomatic` with local time.
- Never call `Refresh()` more than once per second per plot; batch live updates on a single `DispatcherTimer`.

## Gotchas checklist

- [ ] Dictionaries order: `ThemesDictionary` → `ControlsDictionary` → app styles. Wrong order = default-looking controls.
- [ ] `SystemThemeWatcher.Watch` after HWND exists (`Loaded`), not in the constructor.
- [ ] Menu row lives **outside** `ui:TitleBar` (its area is the drag region; a Menu inside becomes undraggable/unclickable territory).
- [ ] Don't set `AllowsTransparency`/`WindowStyle` on `FluentWindow` — it manages its own chrome.
- [ ] Mica is Win 11-only: leave `WindowBackdropType="Mica"`; the library falls back on Win 10 — verify visuals in the Win 10 VM pass (`19_TESTING.md` matrix).
- [ ] VS Designer sometimes renders WPF-UI controls unstyled at design time — judge by running, not the previewer.
- [ ] `TargetPageType` navigation requires the page registered in DI; a missing registration throws at runtime — smoke-test every nav item (App.Tests §Navigation).
- [ ] The crosshair window picker is a separate borderless top-most `Window` (not `FluentWindow`), click-through off, `Cursor="Cross"`; it closes on `MouseUp`/`Esc` — see `08_UI.md` §Picker.
- [ ] Do not bind the 1 Hz `AppsTick` directly to a `DataGrid` `ItemsSource`; update existing row VMs by `(app_id)` key.
