# Third-party notices

AppLedger is GPL-3.0-only. The components below are redistributed with the application (unless marked *dev only* or
*optional download*) under their own licenses, all compatible with GPL-3.0. Verbatim license texts live in
`legal/licenses/<package>-LICENSE.txt`; the release checklist fails if a row here has no matching file.

| Component | Version | License | Role |
|---|---|---|---|
| .NET 10 runtime and WPF | 10.0.x | MIT | runtime |
| WPF-UI (lepoco `Wpf.Ui`) + WPF-UI.DependencyInjection | 4.3.0 | MIT | Fluent UI toolkit |
| Fluent UI System Icons (bundled in WPF-UI) | — | MIT | icon font (Segoe Fluent Icons is **not** bundled; its EULA forbids redistribution) |
| ScottPlot.WPF | 5.1.59 | MIT | charts |
| CommunityToolkit.Mvvm | 8.4.0 | MIT | MVVM |
| H.NotifyIcon.Wpf | 2.3.0 | MIT | tray icon |
| Microsoft.Toolkit.Uwp.Notifications | 7.1.3 | MIT | Windows toasts |
| Microsoft.Extensions.Hosting / Logging | 10.0.x | MIT | Generic Host, DI |
| Serilog, Serilog.Extensions.Hosting, Serilog.Sinks.File, Serilog.Settings.Configuration | 4.x / 9.x / 7.x / 9.x | Apache-2.0 | logging |
| Microsoft.Data.Sqlite | 10.0.x | MIT | SQLite ADO.NET |
| SQLitePCLRaw (bundle_e_sqlite3) | via Microsoft.Data.Sqlite | Apache-2.0 | SQLite native binding |
| SQLite | bundled | Public domain | database engine |
| Dapper | 2.1.x | Apache-2.0 | data access |
| Microsoft.Diagnostics.Tracing.TraceEvent | 3.2.x | MIT | ETW sessions and parsing (ships `KernelTraceControl.dll`, `msdia140.dll` per architecture) |
| Microsoft.Windows.CsWin32 | 0.3.x | MIT | *build time only* — generates P/Invoke source |
| NSec.Cryptography (+ libsodium) | 25.x | MIT (libsodium: ISC) | minisign (Ed25519/BLAKE2b) signature verification |
| MaxMind.Db | 4.x | Apache-2.0 | mmdb reader for the optional GeoIP database |
| DB-IP Lite (IP to Country / ASN) | monthly | CC BY 4.0 | *optional download*; attribution "IP geolocation by DB-IP" shown in Settings and the Network tab |
| Public Suffix List | snapshot 2026-08-23, `sha256:14ef61b1c212f701...` | MPL-2.0 | eTLD+1 aggregation of host names; `catalog/public_suffix_list.dat`, refreshed with the catalog |
| Velopack | 1.2.x | MIT | installer and updates |
| Intel/other hardware SDKs | — | — | **none used** |
| xUnit, NSubstitute, Shouldly, coverlet, Microsoft.NET.Test.Sdk | — | Apache-2.0 / BSD-3 / BSD-3 / MIT / MIT | *dev only* |
| minisign (tool) | — | ISC | *dev/CI only* — signs the catalog; not redistributed |
| XamlStyler (tool) | — | MIT | *dev only* |

Catalog contributions (`catalog/appledger-catalog.json`) are part of this repository and licensed GPL-3.0-only.
Game, store, publisher and product names appearing in the catalog are trademarks of their owners and are used for
identification only.

Generated {{RELEASE_DATE}}. Exact versions at any release: `Directory.Packages.props` and the release notes.
