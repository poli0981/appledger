using System.Windows;

namespace AppLedger.App;

/// <summary>
/// WPF application object for AppLedger.exe (standard user, never elevated).
/// </summary>
/// <remarks>
/// TODO(kickoff): v0.2 moves startup to the Generic Host bootstrap in docs/22_WPFUI_SYNTAX.md §Bootstrap
/// (VelopackApp.Build().Run() first, then Host.StartAsync with the navigation/theme/IPC services).
/// </remarks>
public partial class App : Application
{
}
