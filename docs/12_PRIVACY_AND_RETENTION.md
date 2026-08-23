# 12 — Privacy & Retention

AppLedger records, for up to a year, which programs ran, when, and which hosts they talked to. Stored locally, that is
still the most sensitive dataset on the machine. These rules are product decisions; changing a default is an ADR, not a tweak.

## Defaults

| Setting | Default | Range |
|---|---|---|
| Retention | 180 days | 30–365 (`metrics_1m` always 7 days; `process_instances` always 30 days) |
| Host logging — category Browser | `none` (bytes only) | `none` / `etld1` / `full` (per app override; enabling shows an explicit warning) |
| Host logging — category System (`sys:*`) | `none` | same |
| Host logging — all other categories | `etld1` | same |
| Hosts per app per day | 200 (overflow → `(other)`) | 50–1000 |
| DNS query names | stored only where host logging ≠ `none`; counts always | — |
| Command lines | stored in `process_instances` | off via `settings.store_command_lines` (then `NULL`) |
| Session scope | own logon session only | "all sessions" (admin only; shows other users' process names — warned) |
| Reverse DNS | off | on |
| GeoIP DB | not downloaded | opt-in download |
| Catalog auto-update | weekly check | off / manual |
| Diagnostics log level | Information (redacted) | Debug (unredacted, auto-reverts after 24 h) |
| Identity evidence JSON | not stored | stored at Debug |

## Data inventory (every stored field, its sensitivity, and its purge path)

| Data | Table / file | Sensitivity | Purged by |
|---|---|---|---|
| App identities, names, publishers, versions, signer, hash, install root | `apps`, `app_versions` | low (what is installed) | all / app |
| Process instances with image path, command line, user SID, parent | `process_instances` | **medium** (command lines can contain tokens/URLs) | all / app / 30-day auto |
| Per-minute/hour/day resource metrics | `metrics_*` | low | all / app / range |
| Hosts per app per day (policy-shaped) | `net_hosts_daily` | **high** (behavioral) | all / app / range |
| IP → hostname global map | `ip_names` | medium | all; app purge scrubs unreferenced names |
| Explicit DNS lookups | `dns_records` | low-medium | all |
| Data locations and largest files (policy-shaped) | `disk_locations`, `disk_top_files` | medium (file names) | all / app |
| Disk snapshots | `disk_snapshots` | low | all / app / range |
| Events (launch/exit/crash/new host/…) | `events` | **medium-high** (when/what you ran; hosts) | all / app / range |
| Usage per day | `usage_daily` | medium (habits) | all / app / range |
| Agent health | `health_minutes` | none | all / range |
| App icons | `cache\icons` | none | all / app |
| Scan caches | `cache\scan\*.bin` | medium (directory names) | all / app |
| Logs | `logs\*.log` | low at Information (redacted), **high** at Debug | "Clear logs" + 7-day rolling |
| Settings, overrides | `settings`, `app_overrides`, `settings.json` | low | "Reset settings" only (kept on purge) |

Adding a stored field without a row here fails review (`CLAUDE.md` DoD).

## Privacy Gate (first run; re-shown when a default changes in an update)

Plain language, one screen, no legalese. Content (resx keys `Privacy_Gate_*`):
- **What**: "AppLedger records which apps run, how much CPU/memory/disk/network they use, where they store files, and
  — for most apps — which websites/hosts they talk to."
- **Browsers**: "For web browsers we record only how much data they used, not which sites. You can change this per app."
- **Where**: "Everything stays on this PC in `%LOCALAPPDATA%\AppLedgerData`. Nothing is uploaded. AppLedger has no accounts."
- **How long**: "6 months by default. You can shorten it, pause, or delete everything in one click."
- **Who can see it**: "Anyone who can log in as you on this PC. Protect your account accordingly."
- Buttons: Continue · Read full policy (`legal/PRIVACY_POLICY.md`).

## Pause & private window

- Tray › Pause: the collector stops sampling and persisting; ETW sessions stay open (restarting them costs more than
  idling). Live view shows "Paused". Resume on demand or after the chosen duration.
- Private window (N minutes): same as pause, but also discards the in-memory ring and endpoint maps at the end so
  nothing from the window leaks into the next rollup.
- Both write a `Paused`/`Resumed` event (without reason).

## Purge

- **All**: every Agent-owned table, icon cache, scan caches; `VACUUM`; keeps settings/overrides; the Agent keeps running.
- **App**: `DELETE FROM apps WHERE app_id=?` cascades; then `ip_names` scrub (`DELETE FROM ip_names WHERE host NOT IN
  (SELECT host FROM net_hosts_daily)`), icon and scan cache removal. The app will be re-created on next sight (fresh).
- **Range**: `DELETE` from time-keyed tables by `ts`/`day`; `usage_daily` recomputed for the edges.
- The UI shows row counts before confirming (`PurgeDone` reports actuals after). Purge events are recorded with counts only.
- Uninstall offers "Delete my history" (deletes `DataRoot` entirely) or "Keep" (default).

## Logging redaction (`15_LOGGING.md`)

At `Information` and above, log events may contain: app ids, pids, counts, durations, Win32 error codes, sensor names.
They must not contain: hostnames, IPs, full paths (use `PathRedactor.ToClass(path)` → `"<install-root>/…/x.dll"` or
`"<userprofile>/…"`), command lines, user names/SIDs. `Debug` may contain all of it; enabling Debug shows a warning and
auto-reverts after 24 h.

## Network calls AppLedger makes (exhaustive)

| Call | When | Data sent | Opt |
|---|---|---|---|
| GitHub Releases update check (Velopack `GithubSource`) | UI start, then every 24 h | standard HTTP request; no identifiers | off via settings |
| Catalog update (`https://github.com/poli0981/appledger/releases/latest/download/appledger-catalog.json` + `.minisig`) | weekly, or manual | none | off via settings |
| GeoIP DB download (project release asset) | manual | none | opt-in |
| `DnsQueryEx` for `ResolveHost` / optional reverse DNS | user expands a host / reverse DNS enabled | the hostname/IP to the system resolver | inherent / opt-in |
| Steam store genre / MSIX Store category lookup | never by default | app ids | opt-in (roadmap) |

No telemetry, no crash reporting SDK, no analytics. Bug reports are manual (issue template asks the user to paste redacted log lines).

## Export

Settings › Data › Export: a zip with CSVs per table for a chosen range, policy-shaped (hosts as stored), plus a
`README.txt` describing columns. Export is a read; the Agent streams rows through the pipe only for live data — the UI
reads SQLite directly for export.

## Multi-user machines

The Agent runs for the user who installed it; other users' processes are excluded by default (`session scope`). Each
user installing AppLedger gets their own Agent task and data folder. "All sessions" mode shows other users' process
names and resource usage (no command lines of other users) and is labeled as such.

## Retention of this document's promises

`legal/PRIVACY_POLICY.md` is the user-facing statement of the same facts; both change together in the same PR.
