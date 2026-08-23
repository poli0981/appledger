# 02 — Spec

Product statement: *pick an application; see everything that can be known about it, now and over the last six months.*
Read-only. Local-only. App-level, not process-level.

## Users & jobs

- **Power user / gamer:** "Why is my disk at 100 %?", "What is this launcher uploading?", "How big did this game get after
  the update?", "Which app is eating RAM when I'm away?"
- **Developer:** "What does my own app really touch — files, hosts, DNS, handles?" (AppLedger as a zero-setup black-box tracer).
- **Privacy-minded user:** "Which apps phone home, to where, how often — without installing a firewall driver."

## Functional requirements

| ID | Requirement | Source |
|---|---|---|
| FR-1 | List running **apps** (grouped, not processes) with live CPU %, private WS, GPU %, disk R/W B/s, net in/out B/s, process count; refresh 1 Hz; sortable; search | 03, 04 |
| FR-2 | List **installed** apps (from Uninstall keys, MSIX, launcher manifests, Scoop/Choco) even when not running, with last-seen, footprint and history access | 03, 09 |
| FR-3 | Select an app by: list click · crosshair window picker · browse to an executable (policy-checked) · from an alert | 08, 11 |
| FR-4 | **Overview tab**: identity card (icon, name, version, publisher, signature badge, category, source, confidence), 8–10 headline metrics with sparklines, today's usage time, last events | 08 |
| FR-5 | **Processes tab**: every live process instance of the app — PID, create time, parent chain, command line (redact toggle), user, session, integrity level, elevated, arch, runtime, threads, handles, per-process CPU/RAM/IO | 04 |
| FR-6 | **Disk tab**: install footprint, observed data locations (with confidence and last write), reclaimable cache, per-drive table with % of capacity and % of used, logical vs on-disk, file count, largest files (top 20), growth chart; "Rescan now" | 09 |
| FR-7 | **Network tab**: live connections (proto, local/remote endpoint, hostname, state, direction, bytes, RTT/retransmits when enabled), hosts table (eTLD+1 or full per policy) with bytes in/out and first/last seen, DNS panel (query name, type, results/CNAME chain, TTL, status), per-interface split, loopback/VPN/metered flags, totals for today/7 d/30 d/6 mo | 10 |
| FR-8 | **History tab**: charts for every stored metric with ranges 1 h / today / 7 d / 30 d / 6 mo, compare with one other app, version-change markers, export CSV/JSON of the visible range | 06, 08 |
| FR-9 | **Events tab**: timeline of launch/exit (with usage duration), crash/exit code, version change, install/uninstall, first-seen host, data-growth spike, catalog/identity changes; filter by kind | 06 |
| FR-10 | **Details tab** (on demand): PE version info, Authenticode signer chain and status, SHA-256, autostart entries (Run keys, Startup folder, scheduled tasks, services) referencing the app, firewall rules for its executables, file associations / protocol handlers, shell/COM extensions under its install root | 04 |
| FR-11 | **Policy tab** (per app): host logging level (none / eTLD+1 / full), exclude from history, hide from lists, manual category, manual app merge/split ("these processes belong to …") — all stored locally as user overrides | 03, 12 |
| FR-12 | **Home**: today's top apps by CPU / RAM / disk / network, alerts list, calendar heatmap of usage, Agent health strip | 08 |
| FR-13 | **Alerts**: new remote host for an app (non-browser), crash, version change, data folder grew > X GB in 24 h, listening port opened — as in-app list and optional Windows toast | 08 |
| FR-14 | **Retention**: default 6 months, configurable 1–12; nightly purge; one-click purge of all / one app / a date range | 06, 12 |
| FR-15 | **Privacy Gate** on first run; "Pause collection" in tray; "Private window" (pause N minutes) | 12 |
| FR-16 | **Catalog updates**: signed rules fetched from GitHub Releases (opt-in auto, default weekly check), verified before load, rollback on failure | 13 |
| FR-17 | **Lite mode** without Agent: FR-1, FR-3, FR-4, FR-5 with user-level data only; banner explains | 01 |
| FR-18 | **Settings**: language (en/vi/ja), theme (light/dark/system — Fluent; Mica on Windows 11, solid fallback on 10), start UI with Windows, Agent budget display, retention, privacy defaults, catalog update policy, optional GeoIP DB download, diagnostics (open logs, log level), export/purge | 08 |
| FR-19 | **Window picker** works for UWP/MSIX apps (resolves through `ApplicationFrameHost` to the real process) | 08 |
| FR-20 | Every number has a tooltip naming its source and semantics ("Private working set — what Task Manager calls Memory") | 04 |

## Non-functional requirements

- NFR-1 **Budget:** Agent idle < 1 % CPU, < 100 MB private WS; DB < 300 MB / 6 months (01 §Budget). Violations surface in the UI.
- NFR-2 **Safety:** no process access beyond `PROCESS_QUERY_LIMITED_INFORMATION`; zero-touch for Tier-2 processes; Tier-0 paths never scanned; all UI-supplied paths canonicalized by `PolicyGuard` (11).
- NFR-3 **Privacy:** local-only; browser hosts off by default; redacted logs; purge covers every table and cache (12).
- NFR-4 **Correctness:** per-app network bytes within 10 % of adapter counters over 1 h (S3); identity ≥ 95 % on the S2 fixture set; disk sizes match `dir /s` logical totals and Explorer "size on disk" within 1 %.
- NFR-5 **Resilience:** every sensor can fail independently; the UI always renders with whatever is available; no sensor failure crashes the Agent.
- NFR-6 **Platform:** Windows 10 22H2+ and Windows 11; x64 and ARM64; no x86 build (avoids WOW64 path redirection).
- NFR-7 **Accessibility:** full keyboard navigation, adequate contrast in both Fluent themes (theme brushes only), Per-Monitor V2 DPI awareness, screen-reader names on every chart and metric card.
- NFR-8 **i18n:** en/vi/ja; byte/number/date formatting per culture; no string concatenation of localized fragments.
- NFR-9 **Startup:** UI shows the Home page within 1.5 s on a warm start; Agent reaches "collecting" within 5 s of logon.
- NFR-10 **Updates:** Velopack delta updates; Agent restarts transparently; history schema migrates forward only with backup.

## Core flows

1. **First run:** Privacy Gate (what/where/how long/how to purge) → choose retention and privacy defaults → "Install Agent"
   (one UAC) or "Continue in Lite mode" → Home.
2. **Investigate an app:** Home → top list or picker → App page (Overview) → tabs. Every tab loads independently with
   skeleton placeholders; history tabs read SQLite directly, live tabs subscribe over the pipe.
3. **Fix a grouping:** App page → Policy tab → "Merge into…" / "Split process X" → IdentityResolver re-runs for live
   instances; history rows keep their original `app_id` unless the user chooses "apply to history".
4. **Investigate an alert:** Toast → Alerts page → App page with the relevant tab preselected and the event highlighted.
5. **Purge:** Settings → Privacy → Purge (all / app / range) → confirmation with row counts → `VACUUM` → event log entry.

## Metrics semantics (summary — full table in 04)

- **CPU %** = Δ(user+kernel time) / (interval × logical CPUs) × 100, capped at 100 (Task Manager convention).
- **Memory** = private working set (Task Manager "Memory"); commit and total working set available in tooltips/charts.
- **Disk** = ETW DiskIO bytes (real device I/O); **I/O** = all I/O counters (files, pipes, devices) — both shown, labeled.
- **Network** = ETW Kernel-Network TCP/UDP payload bytes attributed to the owning process; adapter totals include headers, so sums differ by a few percent — documented in the UI tooltip.
- **Usage time** = seconds in which at least one process of the app existed (not foreground time; foreground tracking is a roadmap item).

## Out of scope

See `23_NON_GOALS.md`. In short: no blocking/firewall, no kill/uninstall/cleanup actions, no payload capture in v1, no
cloud, no kernel driver, no verdicts about "safe"/"malicious".
