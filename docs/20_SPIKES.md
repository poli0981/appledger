# 20 — Spikes (go/no-go gates)

A spike is a throw-away console harness under `spikes/` that answers one question with a number. Spikes run **before**
the feature that depends on them; S1 and S2 gate the whole project. Each entry: question, harness, procedure, pass
criterion, and what happens on failure. Results are recorded in the status table at the bottom (date, box, numbers);
findings that change a design go to `24_ADR.md`.

| # | Question | Gates |
|---|---|---|
| S1-lite | Do the two real ETW sessions open and stay lossless inside budget, with counting-only handlers? | the Collector design (run at kickoff) |
| S1 | Can a C# Agent with the real ETW sessions run 24/7 inside the budget? | everything |
| S2 | Does the identity resolver group processes into apps correctly? | everything that displays a number |
| S3 | Are per-app network bytes from `Kernel-Network` accurate? | Network tab, `net_*` tables |
| S4 | Is the disk scanner fast enough, and correct on hard links/junctions/OneDrive/compression? | Disk tab, growth chart |
| S5 | Does the tiered SQLite design hold 6 months inside 300 MB with fast month queries? | retention promise |
| S6 | Is there a 100 %-managed packet/flow path (pktmon ETW)? | v2 packet mode — **not** a v1 gate |
| S7 | Is zero-touch monitoring invisible to anti-cheat? | Game category, Tier-2 policy |
| S8 | Can network history be back-filled from SRUM without admin? | "history before install" feature |

## S1-lite — ETW pre-flight (`spikes/S1.EtwBudget`, `--minutes` mode)

Runs **before** the Collector exists, so the premise behind ADR-3 is tested at kickoff instead of at v0.2.

- **Question:** on this box and this SDK, can TraceEvent open both real sessions, keep `EventsLost` at zero under
  normal load, and stay inside the budget with handlers that do nothing but count?
- **Harness:** the same two sessions and keywords as `05_COLLECTOR.md`, handlers that only `Interlocked.Increment`,
  no accumulators, no SQLite, no pipe. Logs own CPU time, private WS, `EventsLost` per session, per-kind event rate,
  GC counts and handler-exception count to CSV every 10 s. Also proves the stale-session reclaim path
  (`TraceEventSession.GetActiveSessionNames()`) and that TraceEvent's native bits load on this RID.
- **Procedure:** `dotnet run -c Release --project spikes/S1.EtwBudget -- --minutes 45 --out s1-lite.csv`, elevated,
  with one large file copy and one browsing session inside the window.
- **Pass:** idle < 1 % CPU (5-min average), private WS < 100 MB, `EventsLost = 0` for Network/DiskIO/Process at normal
  load, zero handler exceptions.
- **Fail ->** the reductions listed under S1 apply, and the finding is recorded before any Collector code is written.
- S1-lite does **not** replace S1: it has no accumulators, no rollups, no 48-hour run and no game/anti-cheat leg.

### Result (2026-08-23, PASS)

All four criteria met, but the two margins are very different and that is the useful part:

| Criterion | Budget | Measured | Margin |
|---|---|---|---|
| CPU, peak 5-min average | < 1 % idle, < 3 % load | **0.03 %** | ~30x |
| Private working set, peak | < 100 MB | **79.7 MB** | 1.25x |
| `EventsLost` (kernel / user) | 0 | **0 / 0** | — |
| Handler exceptions | 0 | **0** | — |

Two caveats that matter more than the numbers, and that v0.2 has to design around:

1. **RAM is the binding constraint, not CPU.** ~75 MB is the *floor*: a .NET process holding these two sessions
   with handlers that do nothing but increment a counter, before a single accumulator exists. The working set was
   flat (+0.6 MB over the last 35 minutes), so this is a baseline rather than a leak. What still has to fit in the
   remaining ~20 MB: the per-instance accumulators, the 1-hour snapshot ring (~2 MB per `05_COLLECTOR.md`), the DNS
   map (10 k entries), the endpoint maps (2 000 per app), Serilog buffers, the pipe server — and the SQLite page
   cache, which `06_DATA_MODEL.md` sets to `cache_size=-32000`, i.e. **32 MB on its own**. 75 + 32 already exceeds
   the budget, so that pragma is a v0.2 decision, not a detail.
2. **The CPU figure is a floor, not a prediction.** TraceEvent decodes event payloads lazily, and these handlers
   read no fields at all, so almost no parsing ran. The real Collector reads `ProcessID`, `size`, `daddr` and
   `dport` on every network event — at 12 k events/s sustained, that is where the cost will actually appear.
   S1 (full, after v0.2) is what measures it.

Two smaller notes: the kernel session's 64 MB buffer absorbed a 19.6 k events/s peak with zero loss on this box, so
buffer sizing is not the first thing to reduce if RAM gets tight; and the harness enabled the DNS session through
`Dynamic.All` rather than filtering to event IDs 3006/3008 as this document specifies, which makes the user-session
cost an upper bound.

## S1 — Agent budget (`spikes/S1.EtwBudget`)

S1 is measured **twice**, on purpose. The two runs answer different questions and their difference is itself a
number worth having — it is the cost of the pipe server, the identity resolver and the real database, which the
spike deliberately does not carry.

- **Leg A — the pipeline alone (`spikes/S1.EtwBudget --hours`).** Hosts `AppLedger.Collector` with
  `Privilege=Elevated`, the real `AppLedger-Kernel` + `AppLedger-User` sessions and rollups
  into a temp SQLite under `%TEMP%`, **no pipe server and no UI**. Logs its own CPU time, private WS, GC counts and
  every counter in `CollectorHealth` every **10 s** to CSV — `EventsLost` as the summed figure the collector
  actually exposes, not per session, because nothing above `EtwHub` sees the split. It never calls
  `NoteUiActivity`, so the run stays on the idle profile the budget is written against. This is the isolation
  run: if it fails, the fault is in the collector, not in anything the Agent adds.
- **Leg B — the Agent that actually ships (`AppLedger.Agent.exe --console`).** The real composition root, the pipe
  server, Serilog, the catalog and the real database. No extra instrumentation and **no measurement-only CLI flag**:
  the Agent already self-measures and writes one `health_minutes` row per minute (`15_LOGGING.md` §Agent
  self-watch), so leg B is read back with a SQL query. Measuring through the mechanism that ships is the point —
  a separate measuring path can be right about a build that is wrong.
  S1's pass criteria are 5-minute averages, so per-minute resolution is sufficient.
- **Procedure:** both legs 48 h on the dev box, elevated. During each run: ≥ 2 h of a game (one with EAC), a 1 GB
  file copy + a Steam download, a browser session with ~200 tabs opened over time, one sleep/resume, one 10-min idle
  window. Repeat the first 6 h of leg B on the Windows 10 VM. Build for ARM64 once to confirm TraceEvent's native
  bits load (`KernelTraceControl.dll`) — if they don't, ARM64 ships Lite-only and ADR records it.
- **Readback:** `python tools/s1-report.py --csv s1.csv --db <data root>/appledger.db` renders both legs against
  the pass criteria below. Stdlib-only and read-only, so it can be run against a live Agent mid-run. Note what
  it can and cannot decide: `health_minutes` has no column for the idle profile, so the idle criterion is read
  against the quietest contiguous ten minutes of the run — the procedure's idle window, found rather than
  declared — and the report says so on the line. `--idle HH:MM-HH:MM` overrides it for leg B when the window
  was noted at the time. Handler errors and late samples have no column either; leg A is what reports them.
- **What this run cannot settle: FileIO.** The sampling windows of `05_COLLECTOR.md` §FileIO are a **v0.4** item
  (`21_ROADMAP.md`) and `EtwHub` does not enable the keyword at all yet, so neither leg exercises the noisiest
  provider AppLedger will ever enable. Every number below is therefore an underestimate of the shipping v1
  collector by exactly that amount, and the "lost events during the 1 GB copy only in FileIO" clause of the pass
  criteria is vacuous until v0.4. S1 is re-run when the windows land; that re-run is the one that settles the
  criterion, and this one is what says whether everything else fits without it.
- **What leg B settles that leg A cannot:** the Agent's SQLite `cache_size`. `06_DATA_MODEL.md` sets it to
  `-32000` (32 MB) and marks the value provisional, because S1-lite measured a ~75 MB floor before any storage
  existed. Leg B is where that pragma stops being an assumption.
- **Pass:** idle (no UI, idle profile) < 1 % CPU 5-min average and < 100 MB private WS at hour 24 and hour 48; under
  load < 3 % CPU; `EventsLost = 0` for Network/DiskIO/Process at normal load; lost events during the 1 GB copy only in
  FileIO (allowed, flagged). No handler exceptions. Temp DB growth consistent with S5's model.
- **Fail →** first reduce: poller 2 s, DiskIO without file-name resolution, DNS map cap, FileIO window 5 s / 10 min.
  Re-run. If still failing on RAM: trim TraceEvent (`TraceEventSession` without `TraceLog`, no symbol loading). If still
  failing: the always-on premise is wrong for a managed Agent → record in ADR, consider "collect only while UI open"
  (history becomes best-effort) — a product decision, not a language switch (ADR-3).

## S2 — Identity accuracy (`tests/AppLedger.Core.Tests/Identity` + `spikes/S2.Identity`)

- **Harness:** the fixture runner is the unit test. The spike additionally dumps the **live** process table of the dev
  box (`--dump live.json`, image paths redacted to `%PROFILE%`) so real-world cases become fixtures.
- **Procedure:** run the 12 mandatory fixtures; run the live dump with Chrome, Discord, Steam + one game (+ EAC), Epic +
  one game, Windows Terminal, VS Code + Git, a `python script.py`, OBS, and note every instance whose `app_id` a human
  would disagree with.
- **Pass:** ≥ 95 % expected matches on fixtures, **0** game-into-launcher merges, ≤ 3 disagreements on the live dump,
  all `root:` fallbacks have confidence ≤ 0.6 and a sensible display name.
- **Fail →** the resolver is redesigned before any page that groups by app is built. Typical fixes: more host rules
  (data, not code), adoption rule tightening, better install-root boundaries.

## S3 — Network attribution accuracy (`spikes/S3.NetBytes`)

- **Harness:** consumes `Kernel-Network` events for 1 h, sums bytes per PID and in total; samples adapter counters
  (`GetIfEntry2.InOctets/OutOctets` per interface) at start and end; also records loopback separately.
- **Procedure:** 1 h of mixed use: browser streaming, a game with online play, a Steam download (≥ 2 GB), an OS update
  check, a VPN on for 15 min, a `curl` to `127.0.0.1`. Compare `Σ(per-PID TCP+UDP payload)` with `Σ(adapter octets)`
  minus loopback.
- **Pass:** |difference| < 10 % over the hour (adapter counts include L2/L3/L4 headers; ETW counts payload, so a
  5–8 % gap is expected); QUIC (UDP/443) attributed to the browser; VPN traffic attributed to the app (pre-encryption)
  **and** the VPN tunnel interface labelled; loopback excluded from "internet" totals but shown in the app's breakdown;
  `System` (PID 4) traffic exists and is labelled `sys:system`.
- **Fail →** document the error band in the UI tooltip (FR-20) if 10–20 %; above 20 % investigate event drops (S1) or
  missing IPv6 handlers before shipping the Network tab.

## S4 — Disk scanner (`spikes/S4.DiskScan`)

- **Harness:** `IDiskScanner` over a given root with timing, file count, logical/on-disk totals, hard-link dedupe
  count, reparse points skipped, cloud placeholders counted as logical-only; then an incremental pass driven by the USN
  journal after a known set of changes.
- **Procedure:** full scan of a Steam library ≥ 300 GB / ≥ 500 k files on SATA SSD at background priority (`THREAD_MODE_BACKGROUND_BEGIN`),
  compare with `dir /s` (logical) and Explorer "Size on disk" (allocation). Create 1 000 files + delete 500 + modify
  200 under the root; run incremental. Special cases: a folder with 2 GB of hard-linked files (WinSxS-style), a junction
  to `System32` placed inside the root (must be skipped and reported), a OneDrive Files-On-Demand folder, an NTFS-
  compressed folder, a Dev Drive (ReFS) volume.
- **Pass:** full scan < 2 min; incremental < 5 s; logical within 1 % of `dir /s`; on-disk within 1 % of Explorer;
  hard links counted once; junction skipped with a `PolicyDenied` note; placeholders not counted as on-disk; no
  foreground stutter in a game during the scan (subjective, note it).
- **Fail →** scan in chunks by directory with yield points; or full scans only nightly and no incremental (USN off) on
  that volume type.

## S5 — Storage tiers (`spikes/S5.Storage`)

- **Harness:** generates 6 months of synthetic data for 100 apps (realistic duty cycles: 5 apps 24/7, 40 apps 4 h/day,
  55 apps sporadic), 200 hosts/day for 10 apps, daily disk snapshots, events; runs the retention job; times the
  month-chart query (`metrics_1d` for one app), the 7-day hourly query, the "top apps today" query, and a purge of one app.
- **Pass:** DB < 300 MB after `VACUUM`-free `auto_vacuum=INCREMENTAL` maintenance; month query < 100 ms, 7-day
  hourly < 100 ms, top-apps < 50 ms on a laptop SSD; purge of one app < 2 s; WAL checkpoint keeps `-wal` < 64 MB.
- **Fail →** shrink rows (store deltas as `INTEGER` not `REAL`, drop `max` columns from `metrics_1d`), cap hosts/day
  lower, or shorten `metrics_1m` retention to 3 days.

## S6 — Managed packet/flow path (`spikes/S6.PktMon`) — v2 gate

- **Question:** can flows with SNI be captured **without** Npcap and without a native project, via the
  `Microsoft-Windows-PktMon` ETW provider consumed by TraceEvent?
- **Procedure:** start `pktmon start --capture --pkt-size 160` (admin), enable the provider in an `AppLedger-Packet`
  session, decode packet payload from event data (Ethernet/IP/TCP header + TLS ClientHello SNI), correlate the 5-tuple
  with `ConnectionPoller` snapshots to find the PID, measure CPU at 500 Mbps, measure the fraction of flows attributed.
- **Pass:** ≥ 90 % of flows attributed, < 5 % CPU at 500 Mbps, SNI extracted for ≥ 95 % of TLS flows, payload never
  retained beyond the ClientHello bytes needed.
- **Fail →** packet mode stays out (`23_NON_GOALS.md`). Per ADR-3 this does **not** open a native project; the feature is
  cut, not re-platformed.

## S7 — Zero-touch with anti-cheat (`spikes/S7.ZeroTouch`)

- **Harness:** the real Agent plus a checklist. The harness logs every `OpenProcess` call (access mask, PID, result) so
  the claim "no handle on Tier-2 processes" is verifiable from the log, not assumed.
- **Procedure:** with the Agent running, launch a game protected by EAC and one by BattlEye (owned copies), play online
  for 30 min each. Before that, confirm the game's `app_id` is Tier 2 (catalog `anticheat` match). Observe: Agent log
  has zero `OpenProcess` for the game PIDs; metrics still arrive (poller + ETW); game and anti-cheat show no warning,
  kick or error; afterwards, check the anti-cheat's own log folder for our process name.
- **Pass:** all of the above. **Fail →** if the game still complains with zero handles, the cause is elsewhere (e.g. our
  ETW session name?) — investigate, never add evasion (`11_SAFETY_POLICY.md` §No evasion).
- Note: the policy is safe by construction (no handles, no injection); S7 exists to keep us honest and to document the
  evidence for the README claim.

## S8 — SRUM back-fill without admin (`spikes/S8.Srum`)

- **Question:** does `Windows.Networking.Connectivity.ConnectionProfile.GetAttributedNetworkUsageAsync` (WinRT) return
  per-executable hourly usage for **desktop** (non-packaged) processes when called from a non-packaged app, and how far
  back?
- **Procedure:** call for each connection profile with 1-hour granularity over the last 60 days; inspect
  `AttributionId`/`AttributionName`; match ids to image paths; compare one day's total with our own `net_*` rows.
- **Pass:** ≥ 30 days of hourly rows, desktop processes attributed by executable path (or a stable id we can map),
  totals within 15 % of ours for an overlapping day.
- **Fail →** drop the back-fill feature (no ESE/`SRUDB.dat` parsing — admin-only, locked file, fragile).

## Status

| Spike | Date | Box | Result | Numbers / link |
|---|---|---|---|---|
| S1-lite | 2026-08-23 | i7-14700KF (20C/28T), 32 GB, Win 11 Insider 29648, x64 | **PASS** | 45 min. Peak 5-min CPU **0.03 %** (2.4 s CPU total); private WS **79.7 MB** peak, +0.6 MB drift after the first 10 min; `EventsLost` **0/0**; 0 handler errors. 7.72 M network, 167 k disk, 40.6 k image, 11.7 k DNS, 1 649 process events; sustained 12.2 k network ev/s in the busiest 5 min, 19.6 k ev/s peak. See the Result note above. |
| S1 | — | — | not run | |
| S2 | — | — | not run | |
| S3 | — | — | not run | |
| S4 | — | — | not run | |
| S5 | — | — | not run | |
| S6 | — | — | not run (v2) | |
| S7 | — | — | not run | |
| S8 | — | — | not run | |
