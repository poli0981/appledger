# CLAUDE.md — AppLedger

AppLedger is a Windows desktop tool that shows **everything that can be known about an application** — metadata,
processes, CPU/RAM/GPU, real disk I/O, disk footprint, network connections, DNS, events — grouped per *app*
(not per process), with **up to 6 months of history**. It is read-only, local-only, GPL-3.0-only, C#-only.

This file is the entry point for any AI session working in this repo. Read it fully, then follow the reading order.

## Non-negotiables (do not "improve" these)

1. **Observer, never intruder.** No injection, no kernel driver, no window hooks, no `PROCESS_VM_READ`/`VM_WRITE`/
   `VM_OPERATION`/`CREATE_THREAD`/`DUP_HANDLE`/`TERMINATE` on any process, ever. Handles are opened with
   `PROCESS_QUERY_LIMITED_INFORMATION` only. Prefer system-wide queries (`NtQuerySystemInformation`) and ETW over
   per-process handles. Blocklisted anti-cheat processes are **zero-touch** (no `OpenProcess` at all). `docs/11_SAFETY_POLICY.md`.
2. **Read-only v1.** AppLedger displays. It never kills, uninstalls, blocks, edits registry, deletes files or changes
   any system state. The only things it writes are its own database, logs, settings and the catalog cache under
   `%LOCALAPPDATA%\AppLedgerData`. "Open folder in Explorer" runs from the non-elevated UI only. `docs/23_NON_GOALS.md`.
3. **Privacy defaults are product decisions, not settings defaults to be tuned later.** Browser-category apps store byte
   totals only — no hostnames — unless the user opts in. Other apps aggregate hosts to eTLD+1 with a per-day cap.
   No telemetry. No cloud. Purge is one click. `docs/12_PRIVACY_AND_RETENTION.md`.
4. **C# only.** No C++/Rust projects in this repo. If a spike proves managed code cannot do something, stop and record the
   finding in `docs/20_SPIKES.md` instead of adding a native project.
5. **Sensitive-path policy lives in the Agent**, not in UI dialogs. Every path that crosses the pipe is canonicalized and
   tiered by `PolicyGuard` before use. Tier 0 roots (Windows, WindowsApps, $Recycle.Bin, ...) are never scanned as apps.
6. **Budget is a feature.** Agent idle < 1 % CPU (5-min average), < 100 MB private working set after 24 h, DB < 300 MB
   for 6 months. Any collector-path change must state its cost. `docs/05_COLLECTOR.md` §Budget.
7. **Exact version pins.** Central Package Management, no floating versions. WPF-UI stays on the verified pin
   (4.0.0–4.0.3, 4.1.0, 4.2.0 are deprecated on NuGet). `Directory.Packages.props`.

## Stack

| Layer | Choice | Notes |
|---|---|---|
| Runtime | .NET 10 LTS, C# latest, `net10.0-windows10.0.19041.0` (App, Agent, Infrastructure), `net10.0` (Core, Ipc) | WinRT projection needed for `PackageManager`, `ConnectionProfile` |
| UI | WPF + **WPF-UI 4.3.0** (lepoco, Fluent), ScottPlot.WPF 5.1.59, CommunityToolkit.Mvvm, H.NotifyIcon.Wpf | `docs/22_WPFUI_SYNTAX.md` is mandatory reading before any XAML |
| Collector | TraceEvent 3.2.4 (ETW), CsWin32 0.3.298 (Win32/IP Helper/PDH), hand-written ntdll P/Invoke for `NtQuerySystemInformation` | `docs/05_COLLECTOR.md` |
| Storage | SQLite (Microsoft.Data.Sqlite) + Dapper, WAL, tiered rollups 1 m / 1 h / 1 d | `docs/06_DATA_MODEL.md` |
| IPC | Named pipe `\\.\pipe\AppLedger.v1`, length-prefixed JSON (System.Text.Json source-generated) | `docs/07_IPC.md` |
| Hosting | Generic Host + DI in both processes | Agent = Worker host; UI = WPF host per WPF-UI template |
| Logging | Serilog, rolling files, redaction by default | `docs/15_LOGGING.md` |
| i18n | `.resx` — `en` (source), `vi`, `ja` | `docs/14_I18N.md` |
| Packaging | Velopack 1.2.0, one package, two exes, per-user install | `docs/16_PACKAGING_AND_UPDATES.md` |
| Catalog | Signed JSON rules (minisign/Ed25519 via NSec) | `docs/13_CATALOG_RULES.md` |
| Tests | xUnit + NSubstitute; `Category=Admin` tests excluded on CI | `docs/19_TESTING.md` |
| CI/CD | Caller stubs → `poli0981/.github` reusable workflows, explicit `permissions:` | `docs/18_CI_CD.md` |

## Solution layout (`AppLedger.slnx`)

```
src/
  AppLedger.Core/            Domain + Application. No Windows, no IO. AppIdentity model, metrics, policy model,
                             rollup math, ports (interfaces). Unit-testable on any OS.
  AppLedger.Ipc/             Pipe message contracts + framing + client/server primitives. Shared by App and Agent.
  AppLedger.Infrastructure/  Windows adapters: ETW (TraceEvent), ProcessPoller (ntdll), IP Helper, PDH GPU, PE/Authenticode,
                             registry, launcher manifests, DiskScanner + USN, SQLite repositories, catalog loader, PolicyGuard impl.
  AppLedger.Collector/       The collection pipeline as a hostable library (sensors → accumulators → rollups → repos).
                             Hosted by the Agent (elevated, full) or by the App (Lite mode, standard user).
  AppLedger.Agent/           AppLedger.Agent.exe — elevated worker host: collector + pipe server + scheduled-task installer.
  AppLedger.App/             AppLedger.exe — WPF UI (WPF-UI shell), pages, view models, charts, tray, onboarding.
catalog/                     appledger-catalog.json (+ .minisig at release time)
tests/                       AppLedger.Core.Tests, AppLedger.Infrastructure.Tests, AppLedger.Collector.Tests, AppLedger.App.Tests
spikes/                      S1..S8 console harnesses (docs/20_SPIKES.md) — never referenced by src/
docs/  legal/  .github/
```

Dependency direction: `App → Ipc, Core, Collector, Infrastructure` · `Agent → Collector, Infrastructure, Ipc, Core` ·
`Collector → Core` (+ Infrastructure adapters injected) · `Infrastructure → Core` · `Core → nothing`. A reference
from Core to anything Windows-specific is a review-blocking bug.

The App's two extra edges are not a leak and are not optional: Lite mode hosts `AppLedger.Collector` in-process
(`docs/01_ARCHITECTURE.md` §Lite mode), and the UI reads history straight out of SQLite rather than over the pipe
(`docs/07_IPC.md` §opening), which needs the Infrastructure reader. What the App must never do is *write* history —
that stays the Agent's alone (`docs/06_DATA_MODEL.md` §Ownership).

## Conventions

- Clean Architecture as in AutoClickForge/CommandForge: ports in Core (`IProcessSource`, `IEtwSource`, `IDiskScanner`,
  `IMetricsRepository`, `IPolicyGuard`, `ICatalog`), adapters in Infrastructure, wired in the hosts.
- `Task`-based async everywhere; sensors run on dedicated threads and hand off via `Channel<T>` (bounded, drop-oldest
  for live streams, never for rollup inputs).
- Process identity is always `(pid, createTime)`; app identity is always `app_id` (`docs/03_APP_IDENTITY.md`). Never key
  anything on a bare PID.
- Time: store UTC epoch seconds; day buckets are computed in local time at query time (`docs/06_DATA_MODEL.md` §Time).
- Win32: CsWin32 via `NativeMethods.txt` per project; one `SafeHandle` per resource; every P/Invoke wrapper lives in
  Infrastructure and is covered by a smoke test. No `DllImport` outside Infrastructure.
- UI: MVVM with CommunityToolkit.Mvvm source generators; pages transient, state in services; no code-behind logic beyond
  wiring; colors only via WPF-UI theme brushes; charts only via `ChartTheme.Apply`.
- Strings: every user-visible string is a `Strings.resx` key (`Page_App_Tab_Disk`), never a literal.
- Logging: Serilog structured events; `Information` level never contains hostnames, full paths, user names or command
  lines — use the redaction helpers (`docs/15_LOGGING.md`).
- Analyzers on, warnings are errors. `dotnet format` and XamlStyler clean before commit.
- Commits: Conventional Commits (`feat(collector): ...`), GPG-signed.
- Docs are the spec. Any deviation from a doc updates that doc in the same PR.

## Reading order

1. `CLAUDE.md` (this) → `docs/23_NON_GOALS.md` → `docs/11_SAFETY_POLICY.md` → `docs/12_PRIVACY_AND_RETENTION.md`
2. `docs/01_ARCHITECTURE.md` → `docs/02_SPEC.md` → `docs/03_APP_IDENTITY.md` → `docs/04_DATA_SOURCES.md`
3. `docs/05_COLLECTOR.md` → `docs/06_DATA_MODEL.md` → `docs/07_IPC.md` → `docs/09_DISK_SCANNER.md` → `docs/10_NETWORK_AND_DNS.md`
4. `docs/08_UI.md` + `docs/22_WPFUI_SYNTAX.md` (before any XAML)
5. `docs/13_CATALOG_RULES.md` → `docs/14_I18N.md` → `docs/15_LOGGING.md` → `docs/16_PACKAGING_AND_UPDATES.md` → `docs/17_BUILD.md` → `docs/18_CI_CD.md` → `docs/19_TESTING.md`
6. `docs/20_SPIKES.md` → `docs/21_ROADMAP.md` → `docs/24_ADR.md`

## Workflow

- **Spikes before features.** S1 (Agent budget) and S2 (identity accuracy) are go/no-go gates for the whole project.
  Do not build pages that depend on unproven sources. `docs/20_SPIKES.md`.
- Build order per `docs/21_ROADMAP.md`: Core models + PolicyGuard + rollup math (pure, tested) → Infrastructure adapters
  (each with a smoke test) → Collector pipeline → Agent host + IPC → App shell → pages in roadmap order.
- When a Windows API behaves differently than documented here, record it in `docs/24_ADR.md` (short "Finding" entry) and
  fix the doc — do not paper over it in code comments.

## Definition of done (per PR)

- Builds warning-free; tests green (`Category!=Admin` on CI, full suite locally on an elevated dev box); `dotnet format` clean.
- New user-visible strings exist in `en`, `vi`, `ja` resx (machine-draft `ja` acceptable, marked `<!-- review -->`).
- Collector-path changes include a budget note (measured with the S1 harness, or reasoned).
- Any new stored field is classified in `docs/12_PRIVACY_AND_RETENTION.md` §Data inventory and covered by purge.
- Any new `OpenProcess`, file-system or registry access goes through `PolicyGuard` and is listed in `docs/11_SAFETY_POLICY.md`.
- Docs updated in the same PR when behavior deviates.

## Never

- Never add `PROCESS_VM_READ` "just for module enumeration" — use ETW ImageLoad.
- Never add a `FloatingVersion`/wildcard package version.
- Never store a browser hostname without the per-app opt-in flag being true.
- Never call `Process.Kill`, `TerminateProcess`, uninstall strings, `schtasks /Delete` on foreign tasks, or write outside
  `%LOCALAPPDATA%\AppLedgerData` (except the Velopack-managed install folder and our own Scheduled Task).
- Never add analytics, crash reporting SDKs, or any network call not listed in `docs/12_PRIVACY_AND_RETENTION.md` §Network calls.
- Never weaken Tier 0/1/2 policy to make a demo look better.

Placeholders still unresolved: `{{RELEASE_DATE}}`, filled at the first release, plus `{{USER}}`/`{{USER_SID}}`/`{{AGENT_EXE}}`/`{{AGENT_DIR}}` in `docs/16`, which are substituted at
runtime when the Scheduled Task XML is written. Repo URL, author, contact addresses and the Discord invite were
filled in at kickoff (2026-08-23).
