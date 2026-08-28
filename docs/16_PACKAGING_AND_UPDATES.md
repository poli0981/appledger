# 16 — Packaging & Updates

Velopack 1.2.x, one package, two executables, per-user install, delta updates from GitHub Releases.

## Package

- `vpk pack --packId AppLedger --packVersion <semver> --packDir <publish> --mainExe AppLedger.exe --icon Assets/icon.ico
  --releaseNotes RELEASE_NOTES.md --framework net10.0-x64-desktop` (ARM64: separate `--runtime win-arm64` pack with
  channel `win-arm64`; the updater stays on its channel).
- Publish both exes into the same folder: `dotnet publish src/AppLedger.App -r win-x64 -c Release -o publish/win-x64`
  and `dotnet publish src/AppLedger.Agent … -o publish/win-x64`. Framework-dependent (the desktop runtime bootstraps
  via Velopack `--framework`); self-contained is allowed later if size is acceptable.
- Install root (Velopack default): `%LOCALAPPDATA%\AppLedger\` with the stable `current\` folder → the Scheduled Task
  action `%LOCALAPPDATA%\AppLedger\current\AppLedger.Agent.exe --serve` survives updates.
- **Data root is separate**: `%LOCALAPPDATA%\AppLedgerData\` — Velopack deletes its install root on uninstall and we
  want history to survive unless the user chooses otherwise.

## App startup (both exes, first lines of `Main`)

```csharp
VelopackApp.Build()
    .OnFirstRun(v => { /* UI: mark onboarding pending */ })
    .OnAfterUpdateFastCallback(v => { /* UI: schedule "restart Agent task" after window shows */ })
    .OnBeforeUninstallFastCallback(v => UninstallHooks.Run())   // stop Agent, remove task (UAC once), ask about data
    .Run();
```

Fast callbacks must finish in < 15 s; the uninstall hook shows one small dialog (keep/delete history) and performs
`--remove-task` via `runas` (`schtasks /Delete` on a Highest-level task requires elevation). If the user cancels UAC,
the task remains but its target disappears — Task Scheduler then logs a launch failure at next logon; we document this
in `README` and also leave a `cleanup.txt` in the data root with the manual command.

## Scheduled Task (`AppLedger Agent`)

Created by `AppLedger.Agent.exe --install-task` (elevated) using
`schtasks /Create /TN "AppLedger Agent" /XML "<DataRoot>\task\AppLedger Agent.xml" /F` then
`schtasks /Run /TN "AppLedger Agent"`. XML (user id and paths substituted at runtime):

```xml
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Description>AppLedger background collector (elevated). Starts at logon for this user.</Description></RegistrationInfo>
  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{{USER}}</UserId><Delay>PT20S</Delay></LogonTrigger></Triggers>
  <Principals><Principal id="Author"><UserId>{{USER_SID}}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>
    <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
    <RestartOnFailure><Interval>PT1M</Interval><Count>3</Count></RestartOnFailure>
  </Settings>
  <Actions Context="Author"><Exec><Command>{{AGENT_EXE}}</Command><Arguments>--serve</Arguments><WorkingDirectory>{{AGENT_DIR}}</WorkingDirectory></Exec></Actions>
</Task>
```

`Priority 7` = below normal (the Agent also sets its own process priority to `BelowNormal` and scanner threads lower).
`schtasks /Run` and `/End` work without elevation for the task owner; `/Create` and `/Delete` need elevation.

Three things about writing this file are load-bearing, and each fails quietly rather than loudly:

- **Encoding is UTF-16.** `schtasks /XML` rejects UTF-8 — including UTF-8 with a BOM. A UTF-8 file produces an
  unhelpful "The task XML is malformed" against XML that is, in fact, perfectly well-formed.
- **`{{AGENT_EXE}}` is `%LOCALAPPDATA%\AppLedger\current\AppLedger.Agent.exe`, computed, not observed.**
  `--install-task` runs from wherever the UI launched it, so `Environment.ProcessPath` may point at a
  version-stamped Velopack folder. Baking that into the task makes it work today and fail after the first update,
  which is exactly the failure the stable `current\` folder exists to prevent.
- **`{{USER}}` and `{{USER_SID}}` are not interchangeable.** The `LogonTrigger` takes an account name
  (`DOMAIN\user`, from `WindowsIdentity.GetCurrent().Name`) and the `Principal` takes the SID string
  (`WindowsIdentity.GetCurrent().User`). Task Scheduler accepts several spellings in each slot, which is precisely
  why the wrong one registers cleanly and then never fires.

## Agent CLI

| Argument | Elevation | Action |
|---|---|---|
| `--serve` | task | run collector + pipe server (mutex `Global\AppLedger.Agent`) |
| `--install-task` | yes (launched via `runas`) | write XML, create task, start it |
| `--remove-task` | yes | stop Agent if running (pipe `Shutdown`), delete task |
| `--status` | no | prints task state + pipe reachability (used by UI and support) |
| `--console` | yes (dev) | `--serve` with console logging, no mutex |

**Exit codes.** `--status` is read by the UI, so its code is the machine-readable half and each value is a
different decision rather than a shade of failure:

| Code | Meaning | What the caller does |
|---|---|---|
| 0 | success; for `--status`, task installed **and** an Agent answering | nothing |
| 1 | bad or missing command | print usage |
| 3 | `--serve`: another Agent already holds the mutex | exit quietly; the other one is collecting |
| 4 | the Agent could not start, or the task XML could not be written | show the error |
| 5 | `schtasks` refused the create or delete | show it, and the path of the XML that was submitted |
| 6 | the task was created but did not start | warn; it comes up at the next logon regardless |
| 7 | `--status`: no task installed | offer Agent setup |
| 8 | `--status`: task installed but nothing answering | offer to start it (`schtasks /Run`, no elevation) |

## Update flow

1. UI checks GitHub Releases (`GithubSource(repoUrl, prerelease: settings.beta)`) on start and every 24 h; shows an
   `InfoBar` with release notes (Velopack renders the markdown).
2. "Update now": `DownloadUpdatesAsync` → send `Shutdown{reason: update}` to the Agent → wait for exit (≤ 10 s) →
   `ApplyUpdatesAndRestart`.
3. Restarted UI (`OnAfterUpdateFastCallback`) runs `schtasks /Run` → Agent comes back on the new version; schema
   migration runs on the Agent's first DB open (backup first, `06`).
4. If the task is missing (user deleted it) the UI offers Agent setup again.

## Code signing

Releases are unsigned for now (SmartScreen warning documented in README with SHA-256 checksums in the release body).
Adding Azure Trusted Signing later is a `release.yml` input change plus `vpk --signParams`.

## Release checklist

- `RELEASE_NOTES.md` updated; version bumped in `Directory.Build.props`/tag.
- `legal/licenses/` complete for every row in `THIRD_PARTY_NOTICES.md` (script `tools/check-licenses.ps1` — add at kickoff).
- Catalog signed; `catalog/appledger-catalog.json.minisig` attached to the release; PSL refreshed.
- S1 budget rerun on the release build for 12 h; numbers pasted into the release notes.
- Manual smoke on Windows 10 22H2 VM and Windows 11: install, onboarding, Agent task, update from previous version, uninstall keep/delete.
