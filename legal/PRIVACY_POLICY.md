# Privacy Policy

**Short version:** AppLedger keeps everything on your computer, sends nothing about you anywhere, and lets you delete
all of it with one click. The long version follows; the technical specification is `docs/12_PRIVACY_AND_RETENTION.md`.

## What AppLedger records (locally, in `%LOCALAPPDATA%\AppLedgerData`)

| Data | Why | Default retention |
|---|---|---|
| Which apps are installed and ran (names, versions, publishers, signatures, install folders) | identity of what you see | 6 months (configurable 1–12) |
| Per-app resource use per minute/hour/day (CPU, memory, GPU, disk I/O, network bytes) | the charts | 7 days at minute level, then hourly/daily for the retention period |
| Process details (command lines, parent, user, session) | Processes tab | 30 days; command lines can be disabled |
| Remote hosts per app per day, shaped by policy | Network tab | retention period; **browsers and Windows components store byte totals only, no host names**, unless you opt in per app; other apps store the registrable domain (eTLD+1) only, capped per day |
| DNS lookups you expand in the UI | DNS panel | retention period |
| Disk footprint snapshots and observed data folders | Disk tab, growth chart | retention period |
| Events: launches, exits, crashes, version changes, new hosts, data growth | Events tab, alerts | retention period |
| Agent health numbers | budget display | retention period |
| Diagnostic logs (redacted: no host names, paths under your profile, user names or command lines at the default level) | troubleshooting | 7 days, rolling |

AppLedger observes **your own logon session only** unless you explicitly enable "all sessions" (shown with a warning).

## What AppLedger never does

- No telemetry, analytics, crash-reporting service, account, or cloud sync.
- No packet payload capture. No keystrokes, screenshots, clipboard or browser history.
- No upload of any stored data. Bug reports are manual: you choose what to paste.

## Network connections AppLedger makes (complete list)

| Connection | When | What is sent | Control |
|---|---|---|---|
| GitHub Releases update check (https://github.com/poli0981/appledger) | at UI start, then every 24 h | a standard HTTP request; no identifiers | Settings › Updates |
| Catalog rules update (https://github.com/poli0981/appledger/releases) | weekly, or manual | none | Settings › Catalog |
| GeoIP database download (https://github.com/poli0981/appledger/releases) | only when you click "Download" | none | opt-in |
| Store category lookups (Steam / Microsoft Store) | never by default; future opt-in | app ids only | opt-in |

GitHub sees your IP address like any website you visit. Nothing else leaves your machine.

## Your controls

- **Privacy Gate** on first run explains the above before any collection starts.
- **Pause collection** (tray) and **Private window** (pause for N minutes).
- **Per-app policy**: host logging level (none / domain / full), exclude from history, hide from lists.
- **Purge**: everything, one app, or a date range — removes rows from every table, caches and icons.
- **Uninstall** asks whether to keep or delete the data folder.

Windows can encrypt the disk where this data lives (BitLocker); AppLedger does not add its own encryption in v1
(`docs/21_ROADMAP.md` lists optional column encryption).

## Children, legal basis, jurisdictions

AppLedger is a local utility with no online service; there is no account and no data processing by
poli0981. If your jurisdiction regards locally stored usage history as personal data, you are the sole
controller of it.

Changes to this policy ship with the app and re-open the Privacy Gate when a default changes.

Contact: contact@poli0981.dev · Last updated: {{RELEASE_DATE}}
