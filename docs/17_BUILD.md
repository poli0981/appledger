# 17 — Build

## Prerequisites

- Windows 11 (dev), .NET SDK 10.0.1xx (`global.json`), Visual Studio 2022/2026 or Rider with the WPF workload,
  `vpk` (`dotnet tool install -g vpk`), XamlStyler extension/CLI. The `minisign` CLI is **not** needed for
  development: the test corpus in `tests/fixtures/minisign/` was generated with NSec and regenerates the same way;
  `minisign` is only used by CI to sign a release catalog.
- An elevated terminal for running the Agent locally and the `Category=Admin` tests.

## Projects and TFMs

| Project | TFM | Output | Key packages |
|---|---|---|---|
| `AppLedger.Core` | `net10.0` | library | — |
| `AppLedger.Ipc` | `net10.0` | library | System.Text.Json (source-gen) |
| `AppLedger.Infrastructure` | `net10.0-windows10.0.19041.0` | library | TraceEvent, CsWin32, Microsoft.Data.Sqlite, Dapper, NSec, Serilog |
| `AppLedger.Collector` | `net10.0-windows10.0.19041.0` | library | Microsoft.Extensions.Hosting.Abstractions |
| `AppLedger.Agent` | `net10.0-windows10.0.19041.0` | `AppLedger.Agent.exe` (console, `OutputType=Exe`, `DisableWinExeOutputInference`) | Microsoft.Extensions.Hosting, Serilog.Extensions.Hosting, Velopack |
| `AppLedger.App` | `net10.0-windows10.0.19041.0` | `AppLedger.exe` (`UseWPF`) | WPF-UI, WPF-UI.DependencyInjection, ScottPlot.WPF, CommunityToolkit.Mvvm, H.NotifyIcon.Wpf, Microsoft.Toolkit.Uwp.Notifications, Velopack |
| `tests/*` | matching TFMs | xunit | NSubstitute, coverlet |
| `spikes/*` | `net10.0-windows10.0.19041.0` | console | reference `Collector`/`Infrastructure` |

`10.0.19041.0` (Windows 10 2004) is the minimum for the WinRT projections we use (`Windows.Management.Deployment`,
`Windows.Networking.Connectivity`); runtime checks guard APIs newer than 22H2.

Agent `app.manifest`: `requestedExecutionLevel level="asInvoker"` (the task elevates; `runas` is used explicitly for
`--install-task`), `longPathAware=true`, Per-Monitor V2 not needed. App `app.manifest`: `asInvoker`, `longPathAware`,
`dpiAwareness=PerMonitorV2`, Windows 10/11 compatibility GUIDs.

## CsWin32

Each Windows project has a `NativeMethods.txt` listing exact APIs (no wildcards in release): e.g. Infrastructure
includes `QueryFullProcessImageName`, `OpenProcess`, `OpenProcessToken`, `GetTokenInformation`, `LookupAccountSid`,
`IsWow64Process2`, `GetPackageFullName`, `PackageFamilyNameFromFullName`, `GetExtendedTcpTable`, `GetExtendedUdpTable`,
`SetPerTcpConnectionEStats`, `GetPerTcpConnectionEStats`, `SetPerTcp6ConnectionEStats`, `GetPerTcp6ConnectionEStats`,
`GetUnicastIpAddressTable`, `GetIfTable2`, `FreeMibTable`, `NotifyUnicastIpAddressChange`, `PdhOpenQuery`,
`PdhAddEnglishCounter`, `PdhCollectQueryData`, `PdhGetFormattedCounterArray`, `PdhCloseQuery`, `FindFirstFileEx`,
`FindNextFile`, `FindClose`, `GetCompressedFileSize`, `GetDiskFreeSpace`, `GetDiskFreeSpaceEx`, `GetFinalPathNameByHandle`,
`GetLongPathName`, `GetFileInformationByHandleEx`, `DeviceIoControl`, `OpenFileById`, `QueryDosDevice`, `SHGetKnownFolderPath`,
`SHGetFileInfo`, `WinVerifyTrust`, `CryptQueryObject`, `WindowFromPoint`, `GetAncestor`, `GetWindowThreadProcessId`,
`EnumChildWindows`, `GetClassName`, `IsHungAppWindow`, `DnsQueryEx`, `DnsFree`, `GetInterfaceDnsSettings`.
`NtQuerySystemInformation` / `NtQueryInformationProcess` are hand-written in `Infrastructure/Ntdll/` with explicit
struct layouts (tested for both x64 and ARM64 offsets) — simpler than pulling WDK metadata.

`NativeMethods.json`: `{ "allowMarshaling": false, "useSafeHandles": true, "public": false }`.

## Platforms

There is no AnyCPU build (ADR-16). Every project declares `<Platforms>x64;ARM64</Platforms>`, the solution maps its
"Any CPU" configuration to project **x64** (so the default `dotnet build`/`dotnet test` and CI produce x64), and
ARM64 is an explicit `-p:Platform=ARM64`. Build outputs therefore carry a platform segment:
`bin/x64/Release/net10.0-windows10.0.19041.0/`. Adding a project means adding both its `<Platforms>` and its
`<Platform Solution=… Project=… />` mappings in `AppLedger.slnx`; forgetting either one drops it back to AnyCPU and
CsWin32 fails with `PInvoke005`.

## Analyzers & style

- `Directory.Build.props`: warnings as errors, `AnalysisLevel latest-recommended`, `EnforceCodeStyleInBuild`.
- `BannedSymbols.txt` (Microsoft.CodeAnalysis.BannedApiAnalyzers): `System.Windows.MessageBox`,
  `System.Diagnostics.Process.Kill`, `System.Diagnostics.Process.GetProcesses` (use the poller),
  `File.Delete`/`Directory.Delete` (go through `Infrastructure/Storage/DataRootFiles.cs`), and every member of
  `PROCESS_ACCESS_RIGHTS` except `PROCESS_QUERY_LIMITED_INFORMATION`.
- `.editorconfig`: 4 spaces, file-scoped namespaces, `var` when apparent, private fields `_camelCase`, async suffix
  `Async`. It sets `end_of_line = lf`, so `.gitattributes` normalizes the working tree to LF — without that pair,
  `dotnet format --verify-no-changes` can never pass on Windows.
- XAML: XamlStyler config from FrameLedger.

## Commands

```powershell
dotnet restore                                                   # add --locked-mode to honour packages.lock.json
dotnet build AppLedger.slnx -c Release                            # x64; ARM64: -p:Platform=ARM64
dotnet format --verify-no-changes
dotnet test --filter "Category!=Admin&Category!=Manual"        # CI-equivalent
dotnet test                                                      # full, elevated terminal on a dev box
dotnet run --project src/AppLedger.Agent -- --console            # elevated terminal
dotnet run --project src/AppLedger.App                           # standard terminal; connects to the console Agent
dotnet run --project spikes/S1.EtwBudget -- --minutes 45 --out s1-lite.csv   # ETW pre-flight, elevated
dotnet run --project spikes/S1.EtwBudget -- --hours 48 --out s1.csv          # S1 leg A, elevated
python tools/s1-report.py --csv s1.csv --db "$env:LOCALAPPDATA\AppLedgerData\appledger.db"   # the S1 pass/fail table
dotnet publish src/AppLedger.App -c Release -r win-x64 -o publish/win-x64
dotnet publish src/AppLedger.Agent -c Release -r win-x64 -o publish/win-x64
vpk pack --packId AppLedger --packVersion 1.0.0 --packDir publish/win-x64 --mainExe AppLedger.exe --icon src/AppLedger.App/Assets/icon.ico
```

## Debugging tips

- Run the Agent with `--console` in an elevated terminal and the UI normally; the UI finds the pipe.
- `logman query -ets` lists live ETW sessions; `logman stop AppLedger-Kernel -ets` clears a stuck session.
- PDH counter names are English via `PdhAddEnglishCounter` regardless of OS language — do not use localized paths.
- SQLite: `DataRoot\appledger.db` can be opened read-only with any viewer while the Agent runs (WAL).
