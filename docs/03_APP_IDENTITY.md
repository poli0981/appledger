# 03 — App Identity

The hardest problem in AppLedger. If grouping is wrong, every number downstream is wrong. `IdentityResolver` is a
first-class Core component with its own fixture suite (S2 is a go/no-go gate).

## Definitions

- **Process instance:** `(pid, createTime)` — the only valid process key. `createTime` is the 64-bit FILETIME from
  `SYSTEM_PROCESS_INFORMATION.CreateTime` (identical to ETW `ProcessStart` timestamps within clock resolution).
- **App:** a stable `app_id` string plus display metadata. One app has 0..n live process instances.
- **Install root:** the directory that "is" the app on disk (used for disk footprint and for parent/child grouping).

## `app_id` scheme (stable across reinstalls and versions)

| Source | Format | Example |
|---|---|---|
| MSIX/AppX | `msix:<PackageFamilyName>` | `msix:Microsoft.WindowsTerminal_8wekyb3d8bbwe` |
| Steam | `steam:<appid>` | `steam:1091500` |
| Epic | `epic:<AppName>` (manifest `AppName`, not display name) | `epic:Fortnite` |
| GOG | `gog:<gameId>` | `gog:1207658924` |
| itch.io | `itch:<gameId>` from `.itch/receipt.json.gz` | `itch:123456` |
| Uninstall key | `uninst:<normalized key name>` (lower-case, `{}` stripped, spaces→`-`) | `uninst:discord` |
| Catalog rule | `cat:<rule id>` | `cat:discord` (catalog ids win over uninstall ids when both match) |
| Scoop / Chocolatey / winget | `scoop:<name>` / `choco:<name>` / `winget:<PackageIdentifier>` | `scoop:7zip` |
| Script / runtime-hosted | `script:<sha256(lower(canonical script path))[:16]>` | `script:9f1c…` |
| Windows service host | `sys:service:<ServiceName>` · fallback `sys:services` | `sys:service:Dnscache` |
| Windows component | `sys:windows`, `sys:explorer`, `sys:system` (PID 4), `sys:idle` | |
| Fallback by install root | `root:<sha256(lower(canonical install root))[:16]>` | `root:2a7b…` |

Precedence when several sources match the same process: **user override › catalog rule › msix › steam/epic/gog/itch ›
uninstall › scoop/choco/winget › script › root**. The winning source is stored as `apps.source`, the losing candidates as
`apps.aliases` (JSON) so merges later are cheap.

## Resolution pipeline (per new process instance)

Inputs available without touching the process: image name (from ETW `ProcessStart` / `SYSTEM_PROCESS_INFORMATION.ImageName`),
PID, parent `(pid, createTime)`, session id. Inputs requiring a `PROCESS_QUERY_LIMITED_INFORMATION` handle: full image
path (`QueryFullProcessImageNameW`), command line (`NtQueryInformationProcess(ProcessCommandLineInformation)`), package
full name (`GetPackageFullName`), token user/IL/elevation. Tier-2 (anti-cheat) processes skip the handle step entirely
(`11_SAFETY_POLICY.md`) and resolve from image name + manifests only.

```
1. PolicyGuard.TierOf(imagePath)          Tier 0 → sys:windows family (see §Windows components), stop.
2. HostRule lookup (catalog host_rules)   conhost/crashpad/--type= → attach_parent; svchost → service_group;
                                          runtimes → script_from_cmdline; explorer → fixed; ... (see §Host rules)
3. Package identity                       GetPackageFullName succeeded → msix:<PFN>. UWP in ApplicationFrameHost → the
                                          frame host itself is sys:windows; the real app is the CoreWindow owner.
4. Launcher manifests (cached index)      imagePath under <steamapps>\common\<installdir>\ → steam:<appid>
                                          under Epic .item InstallLocation → epic:; GOG goggame-*.info → gog:;
                                          .itch/receipt.json.gz in an ancestor dir → itch:
5. Catalog app rules                      match on signer / exe name / install_root_glob → cat:<id>
6. Uninstall registry index (cached)      InstallLocation prefix match, else DisplayIcon path match, else UninstallString dir
7. Package managers                       path under %USERPROFILE%\scoop\apps\<n>\ → scoop:<n>; ProgramData\chocolatey\lib\<n>\ → choco:<n>
8. Parent adoption                        child image under parent's install root, or parent is Launcher-category and
                                          child under parent's root → parent's app_id (never the reverse)
9. PE version info + signer               ProductName+CompanyName → display metadata; install root = §Install root heuristic
10. Fallback                              root:<hash>, display name from FileDescription or exe name
```

Every step returns `(app_id?, confidence, evidence[])`. The first step that yields an `app_id` wins unless a later
step has strictly higher precedence (table above). Evidence is stored in `process_instances.identity_evidence` (JSON,
debug level) so "why is this grouped here?" is answerable in the UI.

**What v0.2 actually ships.** `FallbackIdentityResolver` implements steps **1 and 10 only** — Tier-0 paths to the
`sys:*` family, everything else to `root:<hash of install root>` — because the resolver above is gated by spike S2 and
lands at v0.3 with its fixture suite rather than ahead of it. The visible consequence is that the root fallback groups
by whatever directory sits below the boundary, which for a vendor layout is the *vendor* folder: every product under
`%ProgramFiles%\Google` shares one `root:` id until a catalog rule claims it. Confidences are honest about this — they
are 0.30, below the prompt threshold, so the UI's "?" badge already offers "Assign to app…".

## Host rules (shipped in the catalog, `host_rules[]`)

| Rule | Matches | Result |
|---|---|---|
| `attach_parent` | `conhost.exe`; any command line containing `--type=` (Chromium/Electron children); `*crashpad_handler*.exe`; `*CrashReporter*.exe`; `werfault.exe` (attach to faulting PID from `-p`) | parent's `app_id`; if parent unknown/exited → resolve independently |
| `service_group` | `svchost.exe` | `-s <ServiceName>` present → `sys:service:<ServiceName>`; only `-k <group>` → `sys:services` |
| `system` | `ApplicationFrameHost.exe`, `RuntimeBroker.exe`, `sihost.exe`, `taskhostw.exe`, `ctfmon.exe`, `fontdrvhost.exe`, `dwm.exe`, `SearchHost.exe`, `StartMenuExperienceHost.exe`, `ShellExperienceHost.exe` | `sys:windows` |
| `dll_arg_or_system` | `rundll32.exe`, `regsvr32.exe`, `dllhost.exe` (with or without `/Processid:`) | DLL path (from command line) under a known app root → that app; else `sys:windows`. `conhost.exe` is **not** here — it is `attach_parent`, and the earlier `system` rule must not list it |
| `fixed` | `explorer.exe` → `sys:explorer`; PID 4 → `sys:system`; PID 0 → `sys:idle` | |
| `script_from_cmdline` | `python*.exe`, `pythonw.exe`, `java.exe`, `javaw.exe`, `node.exe`, `dotnet.exe`, `pwsh.exe`, `powershell.exe`, `cmd.exe`, `wscript.exe`, `cscript.exe`, `ruby.exe`, `perl.exe`, `deno.exe`, `bun.exe` | first non-option argument with a file extension → `script:<hash>`, display "Python — scraper.py"; `dotnet <x>.dll` → the dll; `-m module` → `script:` of module name; no script → the runtime's own app (uninstall/root) |
| `launcher_children` | parent app category `Launcher` (Steam, Epic, GOG Galaxy, Battle.net, EA app, Ubisoft Connect, itch) | children under `<launcher root>` (e.g. `steamwebhelper.exe`, `GameOverlayUI.exe`) → launcher; children under a **game** root → the game's own app. A game is never merged into its launcher. |
| `anticheat_helper` | `start_protected_game.exe`, `EasyAntiCheat_EOS_Setup.exe`, `BEService.exe`, `EasyAntiCheat.exe` | game's `app_id` if under the game root, else `cat:<anticheat id>`; always Tier 2 |

Rule order: `fixed` → `system` → `service_group` → `dll_arg_or_system` → `attach_parent` → `script_from_cmdline` →
`anticheat_helper` → `launcher_children`. Rules are data (`13_CATALOG_RULES.md`) so fixes never need a release.

## Parent adoption rules (step 8)

A child joins the parent's app only if **all** of:
1. `parent.createTime < child.createTime` and the parent instance is known (PID reuse guard).
2. Child image path is under the parent's install root, **or** a host rule says `attach_parent`, **or** the parent is
   Launcher-category and the child is under the launcher root (not under a separate game root).
3. The child has no stronger identity of its own (msix/steam/epic/gog/itch/catalog/uninstall). A child with its own
   identity is always its own app (Steam → game, VS Code → `git.exe` under Git's root → Git).

Adoption is one-directional (child → parent). Grandchildren inherit through their parent's resolved `app_id`.

## Install root heuristic (step 9)

Walk up from the image directory until the parent would be one of: `%ProgramFiles%`, `%ProgramFiles(x86)%`,
`%LOCALAPPDATA%\Programs`, `%LOCALAPPDATA%`, `%APPDATA%`, `%USERPROFILE%`, `%ProgramData%`, a drive root, a `steamapps\common`,
or any Tier-0 root. The last directory before that boundary is the install root. Special cases: `app-<version>`
(Squirrel), `current` (Velopack), `bin`/`x64`/`win-x64`/`Release` leaf folders are skipped one level up.

## Windows components

Processes whose canonical image path is under a Tier-0 root resolve to a `sys:*` app without opening a handle for
enrichment beyond image path (already known). Display name from a small shipped table (`Dnscache` → "DNS Client"),
category `System`, footprint N/A, host logging off by default (Windows telemetry endpoints are noisy and not actionable),
but bytes and connections are still counted so "Windows Update pulled 4 GB" is visible.

## Metadata enrichment (once per `(app_id, version)`)

- PE `VS_VERSIONINFO`: `ProductName`, `FileDescription`, `CompanyName`, `ProductVersion`, `FileVersion`, `LegalCopyright`.
- Authenticode: signer subject, issuer, thumbprint, timestamp, status (`Valid`/`Expired`/`Untrusted`/`Unsigned`/`CatalogSigned`)
  via `WinVerifyTrust` with `WTD_CACHE_ONLY_URL_RETRIEVAL | WTD_REVOCATION_CHECK_NONE` (no network). Tier-0 files are
  reported `CatalogSigned` without computing catalog hashes.
  **Known limitation (v0.1):** verification reads *embedded* signatures only. A file **outside** Tier 0 that is signed
  through a Windows security catalog and carries no embedded signature therefore reports `Unsigned`, because
  `CatalogSigned` is currently reachable only through the Tier-0 short-circuit. Closing it means the `CryptCATAdmin*`
  hash lookup and belongs to v0.3, where the status is first displayed (`docs/24_ADR.md` §Findings).
- SHA-256 of the main executable (streamed, background priority). Changes → `VersionChanged` event even if version strings
  are identical (silent updates).
- Icon: `SHGetFileInfo`/`ExtractIconEx` at 32 and 256 px → PNG in `cache\icons\<app_id>.png`; MSIX logo from the manifest.
- Category: user override › catalog › Steam store genre cache (FrameLedger pipeline, opt-in online) › MSIX Store category
  (opt-in online) › `Unknown`. Taxonomy in `13_CATALOG_RULES.md`.
- Runtime detection from ETW ImageLoad of the process: `coreclr.dll`/`clr.dll` → .NET; `jvm.dll` → Java; `node.dll` or
  `--type=` children + `electron` resources → Electron; `python3*.dll` → Python; `UnityPlayer.dll` → Unity;
  `*-Win64-Shipping.exe` + `UE*` → Unreal; `libcef.dll` → CEF. Informational only.

## Confidence

| Source | Confidence |
|---|---|
| user override, msix, steam/epic/gog/itch | 1.00 |
| catalog rule | 0.95 |
| uninstall (InstallLocation match) | 0.90 · (DisplayIcon/UninstallString match) 0.80 |
| scoop/choco/winget | 0.90 |
| script | 0.85 |
| parent adoption | parent's confidence × 0.9 |
| PE product+signer | 0.60 |
| root fallback | 0.30 |

The UI shows a "?" badge below 0.60 with a one-click "Assign to app…" (writes a user override, `11`/`12` compliant:
overrides are local metadata, not system changes).

## User overrides (`app_overrides` table, owned by the UI)

- `merge`: map a `(match: exe path | install root | script hash)` to an existing `app_id`.
- `split`: force processes matching a pattern out of an app into a new `app_id` (`user:<guid>`).
- `category`, `display_name`, `hidden`, `exclude_from_history`, `host_logging`.
- Overrides re-run resolution for live instances immediately; history rows are re-keyed only on explicit "apply to history"
  (single UPDATE per table inside a transaction; reversible for 7 days via `app_overrides.previous_app_id`).

## Caching & invalidation

- Uninstall index, launcher manifests, catalog rules, package-manager dirs: loaded at Agent start, refreshed on
  `RegNotifyChangeKeyValue`(Uninstall keys), `ReadDirectoryChangesW` on `steamapps`/Epic manifests dirs, and catalog update.
- Resolution result cached per `(pid, createTime)` for the instance lifetime; persisted in `process_instances.app_id`.
- `apps.last_seen_utc`, `current_version`, `signer` updated on every new instance; version change triggers re-enrichment.

## Test fixtures (S2, `tests/AppLedger.Core.Tests/Identity/fixtures/*.json`)

Each fixture is a synthetic process table (image path, command line, parent, package name, signer, manifest index,
uninstall index) plus the expected `app_id` per process. Mandatory cases:

1. Chrome: 1 browser + 12 `--type=` children + `chrome_crashpad_handler.exe` → one app.
2. Discord: `Discord.exe` in `%LOCALAPPDATA%\Discord\app-1.0.9xxx\` + Update.exe + renderer children → `cat:discord`.
3. Steam + `steamwebhelper.exe` ×6 + `GameOverlayUI.exe` + game under `steamapps\common\X\` + `start_protected_game.exe`
   + EAC service → `cat:steam` (5 instances), `steam:<appid>` (2 instances, Tier 2), `cat:eac` (service).
4. Windows Terminal (MSIX) + `OpenConsole.exe` + `pwsh.exe -File build.ps1` → `msix:…Terminal` (2), `script:<build.ps1>` (1).
5. `python.exe -m http.server` and `python.exe scraper.py` from the same install → two `script:` apps.
6. `svchost.exe -k netsvcs -p -s Schedule` → `sys:service:Schedule`; `svchost.exe -k LocalServiceNetworkRestricted` → `sys:services`.
7. Portable 7-Zip from `D:\Tools\7z\7zFM.exe` (no uninstall key, signed) → `root:` with PE metadata, confidence 0.60.
8. VS Code + `git.exe` under `C:\Program Files\Git\` + `node.exe` extension host (`--type=`) → Code (2), Git (1).
9. Epic game with `EpicGamesLauncher.exe` → `cat:epic` + `epic:<AppName>`.
10. UWP app behind `ApplicationFrameHost.exe` → frame host `sys:windows`, app `msix:`.
11. PID reuse: parent exited, new process got the same PID with later createTime → child not adopted.
12. User override: `split` a renderer out of Chrome → honored for live, history untouched.

Pass criterion: ≥ 95 % of expected `app_id` matches across all fixtures and **zero** game-into-launcher merges.
