# 11 — Safety Policy

AppLedger is read-only, but it runs an elevated Agent that accepts paths from a non-elevated UI and opens handles to
other people's processes. This document is the contract that keeps that safe. `PolicyGuard` (Core interface,
Infrastructure implementation) enforces it; no other code may make tier or access decisions.

## Principle: observer, not intruder

- No injection, no kernel driver, no window/keyboard hooks, no debugger APIs.
- `OpenProcess` is called with exactly `PROCESS_QUERY_LIMITED_INFORMATION` (plus `SYNCHRONIZE` never — we do not wait
  on processes). `BannedSymbols.txt` bans **every other member** of the CsWin32 `PROCESS_ACCESS_RIGHTS` enum, so
  widening the access mask means a deliberate, reviewable edit to that file: `PROCESS_VM_READ`, `PROCESS_VM_WRITE`,
  `PROCESS_VM_OPERATION`, `PROCESS_CREATE_THREAD`, `PROCESS_CREATE_PROCESS`, `PROCESS_DUP_HANDLE`,
  `PROCESS_SET_INFORMATION`, `PROCESS_SET_LIMITED_INFORMATION`, `PROCESS_SET_QUOTA`, `PROCESS_SET_SESSIONID`,
  `PROCESS_TERMINATE`, `PROCESS_SUSPEND_RESUME`, `PROCESS_QUERY_INFORMATION`, `PROCESS_ALL_ACCESS`,
  `PROCESS_SYNCHRONIZE`, `PROCESS_DELETE`, `PROCESS_WRITE_DAC`, `PROCESS_WRITE_OWNER`. A unit test additionally
  asserts the rights constant at every `OpenProcess` call site.
- Module lists come from ETW ImageLoad, command lines from `ProcessCommandLineInformation`, exit codes from ETW.
- No evasion techniques of any kind, ever: no handle-stripping workarounds, no driver-signing games, no renaming to
  dodge anti-cheat heuristics, no hiding our own process. If something refuses to be observed, we show "(unavailable)".

## Process access tiers

| Tier | Members | Policy |
|---|---|---|
| **2 — zero-touch** | processes matched by catalog `anticheat[]` (by game root containing `EasyAntiCheat\`, `BattlEye\`, `GameGuard\`; by service names `EasyAntiCheat`, `EasyAntiCheat_EOS`, `BEService`, `vgc`, `vgk`, `PnkBstrA/B`; by drivers seen via ImageLoad `EasyAntiCheat*.sys`, `BEDaisy.sys`, `vgk.sys`, `mhyprot2.sys`, `faceit.sys`, `xhunter1.sys`); the game process itself and its children while such a driver/service is present; PPL processes (`lsass.exe`, `csrss.exe`, `wininit.exe`, `winlogon.exe`, `services.exe`, `smss.exe`, `MsMpEng.exe`, `NisSrv.exe`, `SecurityHealthService.exe`, `MsSense.exe`) | **No `OpenProcess` at all.** Identity from ETW image name + manifests; counters from `SystemProcessInformation`; network/disk from ETW. Command line, token, package: "(zero-touch)". |
| **3 — normal** | everything else | `PROCESS_QUERY_LIMITED_INFORMATION` once per instance for enrichment; handle closed immediately. |

Re-evaluation: every 30 s the resolver re-checks whether an anti-cheat driver/service appeared (ImageLoad/SCM) and
promotes affected apps to Tier 2 for the rest of their lifetime. The blocklist is the same family as FrameLedger's
(EAC, BattlEye, Vanguard, Denuvo AC, Ricochet, GameGuard, Xigncode3, mhyprot, VAC, FACEIT, PunkBuster); entries
without a known driver/service name match by directory name only and carry `"match_confidence": "dir"` in the catalog.

Why this is safe for users: anti-cheat drivers commonly strip or deny non-limited rights on protected games;
`QUERY_LIMITED_INFORMATION` is the right Task Manager uses. AppLedger never even requests it for Tier 2, so there is
nothing to strip. We still state in `legal/DISCLAIMER.md` that anti-cheat vendors change heuristics and we cannot guarantee
outcomes; we can only guarantee that we do nothing beyond what Task Manager does.

## Path tiers

| Tier | Roots (resolved via `SHGetKnownFolderPath`, never hard-coded `C:\`) | Policy |
|---|---|---|
| **0 — protected OS** | `FOLDERID_Windows` (incl. `System32`, `SysWOW64`, `WinSxS`, `servicing`, `Temp`), `FOLDERID_ProgramFiles\WindowsApps`, `FOLDERID_ProgramFilesX86\WindowsApps`, `<drive>\$Recycle.Bin`, `<drive>\System Volume Information`, `<drive>\Recovery`, `<drive>\Config.Msi`, `\Device\*`/`\\?\GLOBALROOT\*` volumes not mapped to a drive | Never a pickable/scannable app root; never enumerated; never listed by name in file lists (single "(Windows)" bucket); processes from here are `sys:*` |
| **1 — sensitive user data** | `%LOCALAPPDATA%\Microsoft\Credentials`, `%APPDATA%\Microsoft\Credentials`, `%APPDATA%\Microsoft\Protect`, `%APPDATA%\Microsoft\Crypto`, `%LOCALAPPDATA%\Microsoft\Vault`, `%USERPROFILE%\.ssh`, `%USERPROFILE%\.gnupg`, `%APPDATA%\gnupg`, `%LOCALAPPDATA%\Microsoft\TokenBroker`, browser profile secret files (`Login Data*`, `Cookies*`, `Web Data*`, `key4.db`, `logins.json`, `cert9.db`), password-manager vaults (catalog `sensitive_paths[]`: 1Password, Bitwarden, KeePass `*.kdbx` in Documents) | Sizes counted; **names never stored or sent** (`path = null`, `kind` describes the class); never opened; "Open folder" hidden |
| **2 — write-protected for us** | anything not under `%LOCALAPPDATA%\AppLedgerData` | Read-only by construction: Infrastructure has no write adapter for arbitrary paths; the single write root is `DataRoot` |
| **3 — normal** | everything else | readable, scannable |

Tier 0/1 lists are data (`catalog.protected_paths`, `catalog.sensitive_paths`) on top of a built-in minimum that the
catalog cannot remove (only extend).

## Canonicalization (before any tier decision)

`PolicyGuard.Evaluate(rawPath)`:
1. Reject empty, control characters, `..` after normalization, UNC (`\\server\…`) and `\\?\UNC\…` (no network paths in v1),
   device paths (`\\.\`, `\\?\GLOBALROOT`) unless produced internally by the ETW device-path mapper.
2. `GetFullPathName` → absolute; strip trailing dots/spaces; expand 8.3 segments via `GetLongPathNameW`.
3. Resolve reparse points with `CreateFileW(FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT)` on each ancestor?
   No — simpler and complete: open the final path with `FILE_FLAG_BACKUP_SEMANTICS` (directory or file, read attributes
   only) and call `GetFinalPathNameByHandleW(VOLUME_NAME_DOS)`. This collapses junctions, symlinks and mount points
   anywhere in the path. If the open fails — access denied, or the path does not exist yet — fall back to the lexical
   path and mark `unresolved=true`. The lexical form is then run through the **full** tier table rather than the
   Tier-0-or-Tier-3 shortcut this document first described: failing to open something is never a reason to treat it as
   ordinary, so a credential store we could not open stays Tier 1 and keeps its name out of every output.
4. Drop alternate data stream suffixes (`file.txt:stream`) → the file path; ADS are never enumerated.
5. Compare case-insensitively (ordinal, upper-invariant) with a trailing separator so `C:\WindowsFoo` is not under `C:\Windows`.
6. Result `PathDecision { Canonical, Tier, Allowed, Reason, Unresolved }`. Tier-0/1 reasons are generic codes
   (`ProtectedOs`, `SensitiveUserData`), never the matched rule text (no oracle for what we consider sensitive).

Build is x64/ARM64 only, so `System32` never silently redirects to `SysWOW64`.

## Privilege boundary

- The Agent runs elevated **as the same user** via a Scheduled Task. UAC is not a security boundary (Microsoft's
  position); any process already running as the user could start the task. What we guarantee: the Agent exposes no
  capability to the UI that the UI could not obtain itself with one UAC prompt, and it never acts on the system.
  Concretely, over the pipe the Agent only reads, samples, purges *its own data*, pauses, or exits.
- Every pipe request with a path field goes through `PolicyGuard` inside the Agent. The UI's own checks are UX, not security.
- The Agent binary lives in the Velopack `current\` folder (user-writable). Anyone who can replace it already runs as the
  user; the task does not grant cross-user access. A machine-wide install (v2) would close this by moving the binary to
  `Program Files` and the task to a `LocalService`/service model.
- The pipe is `CurrentUserOnly` and rejects remote clients. No TCP listener exists anywhere in AppLedger.
- The Agent never loads plugins, scripts, or code from the data folder. The catalog is data (JSON), verified by signature,
  parsed with a strict schema (unknown fields rejected), size-capped at 4 MB.

## Things the Agent explicitly does not do (enforced by absence of code + tests)

Write to any path outside `DataRoot`; delete, move, or modify user files (deletion **inside** `DataRoot` — purge, icon
cache, scan cache, migration backups — goes through the single `Infrastructure/Storage/DataRootFiles.cs` helper, the
only place allowed to suppress `RS0030`); change registry (except reading); create,
modify, or delete any Scheduled Task other than `AppLedger Agent`; call `TerminateProcess`; enumerate handles
(`SystemHandleInformation`) or objects; enable network blocking (WFP); enable ETW stack walks; run with `SeDebugPrivilege`
enabled (we explicitly do **not** enable it — limited rights suffice).

## Lite mode note

Without the Agent the UI itself opens `PROCESS_QUERY_LIMITED_INFORMATION` handles to own-user processes through the same
`PolicyGuard` rules; Tier 2 still means zero-touch.

## Tests (`tests/AppLedger.Infrastructure.Tests/Policy`)

- Canonicalization fixtures: junction `%TEMP%\al-junc → %SystemRoot%\System32` ⇒ Tier 0; `C:\WINDOW~1\SYSTEM~1` ⇒ Tier 0;
  `\\?\C:\Windows\System32\` ⇒ Tier 0; `C:\Windows\System32\drivers\etc\hosts:stream` ⇒ Tier 0 file; `C:\WindowsFoo\x` ⇒ Tier 3;
  `%USERPROFILE%\.ssh\id_ed25519` ⇒ Tier 1 with `path = null` in outputs; mixed case and trailing `. ` variants; UNC ⇒ rejected;
  relative path with `..` ⇒ rejected; a path whose final component does not exist ⇒ lexical fallback with `Unresolved`.
Two of those cases depend on the machine rather than on the policy: creating a junction, and the existence of an 8.3
name for the Windows directory (many installs have short-name generation disabled). Both are probed once and turned
into a *skip* by a conditional `FactAttribute` — xUnit 2.9 has no dynamic skip — so a locked-down machine and a broken
policy never produce the same CI result.

- Access-rights test: reflection scan of Infrastructure for `OpenProcess` call sites asserting the rights constant;
  a banned-symbols analyzer config for the forbidden constants.
- Tier-2 test: given a fixture with `EasyAntiCheat.sys` in ImageLoad for PID X, the resolver must produce an instance
  with `tier = 2` and the enrichment adapter must record zero `OpenProcess` calls for it (counting mock).
