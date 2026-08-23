# AppLedger

**The "About this app" page Windows never had.**

Pick any running or installed application and see everything about it in one place — what it is, who signed it,
which processes belong to it, how much CPU / RAM / GPU it uses, how hard it hits the disk, how much space it takes on
every drive, which hosts it talks to and what DNS it resolves — with **up to 6 months of history**, per app, not per process.

Task Manager shows you the last 60 seconds per process. AppLedger shows you the last six months per *application*.

## Features

- **App-level view.** Chrome is one app, not forty processes; a Steam game is its own app, not "Steam". AppLedger groups
  processes by package, store manifest, installer record, signer and install folder — with confidence shown, and a way to
  fix it when it guesses wrong.
- **Metadata that matters.** Name, version, publisher, digital signature status, category, install folder, *observed* data
  folders, command line, parent chain, runtime (.NET / Electron / Java / Python / Unity / Unreal), architecture, integrity level.
- **Live resources.** CPU (user/kernel), private working set & commit, GPU engine utilization & VRAM, threads, handles,
  total I/O and *real* disk read/write, network in/out — per app, 1-second refresh.
- **Disk footprint you can trust.** Logical vs on-disk size, per-drive breakdown with % of capacity, install vs data vs
  reclaimable cache, hard links and OneDrive placeholders handled — and a **growth chart** over time.
- **Network detail.** Live connections with state and direction, bytes per remote host, TCP/UDP/QUIC split, per-connection
  RTT and retransmits, DNS queries with A/AAAA/CNAME chains, interface (Wi-Fi / Ethernet / VPN) and metered flags.
- **Events timeline.** Launches, exits and usage time per day, crashes and exit codes, version changes, first contact with a
  new host, sudden data-folder growth.
- **History & charts.** Last hour (1 s), today (1 min), 7 days (hourly), 30 days and 6 months (daily); per app, compare two
  apps, or top-N stacked; version-change markers on every timeline.
- **Privacy by default.** Everything stays on your PC. Browsers get byte totals only — no hostnames — unless you opt in.
  Other apps aggregate hosts to their registrable domain. One-click purge. No telemetry, ever.
- **Fluent, native Windows 11 look** (Mica, Snap Layouts, dark/light/system) via the MIT-licensed
  [WPF UI](https://github.com/lepoco/wpfui) library, with graceful Windows 10 fallback.
- Multilingual UI: **English / Tiếng Việt / 日本語**.

## How it works

AppLedger never touches the processes it observes. It consumes Event Tracing for Windows (the same passive mechanism
Task Manager uses), system-wide process queries and the IP Helper API. There is no injection, no driver, no memory reading;
the only handle right it ever requests is *query limited information*. Anti-cheat–protected games are treated as
"zero-touch" and are observed through ETW alone. See `legal/DISCLAIMER.md`.

The app is split into two processes:

| Process | Privilege | Role |
|---|---|---|
| `AppLedger.exe` | Standard user | Fluent desktop UI, charts, settings |
| `AppLedger.Agent.exe` | Elevated, runs at logon | ETW sessions, process polling, disk scanning, history database |

During onboarding a one-time UAC prompt registers a Scheduled Task so the Agent can start with Windows without prompting
again. Without the Agent, AppLedger runs in **Lite mode**: live view of your own processes, no network bytes, no real disk
I/O, no history.

## Requirements

- Windows 10 (22H2) or Windows 11, x64 or ARM64
- Administrator rights once, for Agent setup (optional — Lite mode works without)
- ~300 MB of disk for six months of history (typical; browsers excluded from host logging by default)

## Install

1. Download `AppLedger-win-Setup.exe` from [Releases](https://github.com/poli0981/appledger/releases).
2. Windows SmartScreen may warn because releases are not code-signed (free, open-source project). Choose
   **More info → Run anyway** after verifying the SHA-256 checksum published with each release.
3. Follow the first-run Privacy Gate and Agent setup.

Uninstall from Windows Settings as usual; AppLedger asks whether to keep or delete your history database.

## Privacy

AppLedger stores everything locally in `%LOCALAPPDATA%\AppLedgerData`. The only network calls it ever makes are:
(1) update checks against GitHub Releases, (2) signed catalog-rules updates from this repository, and
(3) *optional, opt-in* download of an offline GeoIP database. Bug reports are always manual and user-reviewed.
Full policy: `legal/PRIVACY_POLICY.md`.

## License

GPL-3.0-only. See `LICENSE`. Third-party components and their licenses are listed in `legal/THIRD_PARTY_NOTICES.md`.

## Documentation

Developer/AI-facing documentation lives in [`docs/`](docs/) — start with `CLAUDE.md` and `docs/01_ARCHITECTURE.md`.
Want to fix a wrong app grouping or category? See `docs/13_CATALOG_RULES.md` — it's a data change, not a code change.

---

**Status:** pre-alpha, under active development. Roadmap: `docs/21_ROADMAP.md`.
