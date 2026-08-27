# 01 — Architecture

## Process model

```
┌─────────────────────────────────────┐   \\.\pipe\AppLedger.v1   ┌──────────────────────────────────────────────┐
│ AppLedger.exe (UI)                  │  length-prefixed JSON     │ AppLedger.Agent.exe                          │
│ standard user (Medium IL)           │◄─────────────────────────►│ elevated (High IL), same user, at logon      │
│ WPF · WPF-UI 4.3.0 · ScottPlot 5    │        (07_IPC)           │                                              │
│ reads SQLite (WAL, read-only)       │                           │ AppLedger.Collector (library)                │
│ writes settings + app_overrides     │                           │  ├ ProcessPoller     1 Hz, NtQuerySystemInfo │
│ tray (H.NotifyIcon) · toasts        │                           │  ├ EtwHub            Network·DiskIO·Process  │
│ window picker · onboarding          │                           │  │                   ImageLoad·Thread·DNS    │
└─────────────────────────────────────┘                           │  │                   FileIO (sampled)        │
            ▲                                                     │  ├ GpuPoller         PDH, 2 s                │
            │ Lite mode: hosts AppLedger.Collector in-proc        │  ├ ConnectionPoller  IP Helper, 1 s          │
            │ with Privilege=Standard (no ETW, no history)        │  ├ IdentityResolver  catalog + heuristics    │
            └─────────────────────────────────────────────────────│  ├ DiskScanner       USN incremental, daily   │
                                                                  │  ├ Rollup/Retention  1 s → 1 m → 1 h → 1 d   │
              %LOCALAPPDATA%\AppLedgerData\                       │  ├ EventDetector     launch/exit/crash/...   │
                appledger.db (+ -wal, -shm)                       │  └ PolicyGuard       path tiers, zero-touch  │
                logs\  catalog\  cache\icons\  settings.json      │ PipeServer · Health · TaskInstaller          │
                                                                  └──────────────────────────────────────────────┘
```

Both processes are the **same user**. The Agent is elevated through a Scheduled Task, not a service, so it shares the
user's profile, `%LOCALAPPDATA%` and the SQLite file with the UI.

## Why two processes

- Per-process network bytes, real disk I/O, DNS attribution and module loads come from ETW kernel/system providers.
  Creating those sessions requires administrator rights (or *Performance Log Users* membership). There is no clean
  non-admin path for *live* per-process network bytes.
- A chart-heavy WPF UI must not run elevated: UIPI blocks drag-and-drop from Explorer, every UI bug becomes an
  elevated bug, and the attack surface grows. So: unprivileged UI + elevated worker — the elevation-broker pattern
  from CommandForge and FrameLedger.
- History is the product, so the worker must be **always on**. A Scheduled Task *At log on* with *Highest* run level
  gives that with a single UAC prompt at setup.

## Why not a Windows Service (v1)

Velopack installs per-user into `%LOCALAPPDATA%\AppLedger`. A `LocalSystem` service whose binary lives in a user-writable
folder is a classic local-privilege-escalation smell, would need a machine-wide installer (Velopack's MSI deployment
tooling or a separate package), and would run under a different profile than the UI (separate `%LOCALAPPDATA%`, separate
SQLite). The Scheduled Task model has none of these problems. It does rely on UAC, which Microsoft does not define as a
security boundary — see `11_SAFETY_POLICY.md` §Privilege boundary. Service mode is a v2 option (`21_ROADMAP.md`).

## Elevation strategy

1. **Onboarding (one UAC prompt):** the UI launches `AppLedger.Agent.exe --install-task` via `ShellExecute` with the
   `runas` verb. The elevated instance writes a task XML to `%TEMP%` and runs `schtasks /Create /TN "AppLedger Agent" /XML … /F`,
   then starts the task. Task definition: `16_PACKAGING_AND_UPDATES.md` §Scheduled Task.
2. **Every logon:** Task Scheduler starts `AppLedger.Agent.exe --serve` as the interactive user with highest privileges.
   The Agent acquires a named mutex `Global\AppLedger.Agent` (second instance exits), opens the DB, starts the collector,
   then the pipe server.
3. **UI start:** the UI connects to the pipe. If no Agent answers within 2 s it checks whether the task exists
   (`schtasks /Query`), offers to start it (`schtasks /Run` does not need elevation for the task owner), or falls back to
   **Lite mode**.
4. **Pause/stop:** the tray "Pause collection" sends `Pause` over the pipe (collector stops sampling, sessions stay open);
   "Stop Agent" sends `Shutdown`. Both are reversible without UAC because the task is owned by the user.
5. **Update:** the UI asks the Agent to `Shutdown` before `UpdateManager.ApplyUpdatesAndRestart`; Velopack's stable
   `current\` folder keeps the task's action path valid across versions. The restarted UI re-runs `schtasks /Run`.
6. **Uninstall:** Velopack hook `--veloapp-uninstall` stops the Agent and deletes the task (`schtasks /Delete` needs
   elevation for a Highest-level task; the hook prompts once via `--remove-task` under `runas`). The data folder is kept
   unless the user chose "delete my history" in the uninstall dialog.

## Collector pipeline

```
sensors (own threads)          accumulators (lock-free per (pid,createTime))        every 1 s                 every 1 m
ProcessPoller ──────────────►  ProcessTable: cpu/mem/io deltas, new/exited         ─┐
EtwHub.Network ─────────────►  NetAccumulator[pid]: in/out bytes, per-endpoint      ├─► Snapshot (app-level) ─► LiveStream (pipe)
EtwHub.DiskIO ──────────────►  DiskAccumulator[pid]: read/write bytes, IOPS         │        │                    RingBuffer 5 min (memory)
EtwHub.Process/ImageLoad ───►  lifecycle events, runtime detection                  │        └──────────────────► Rollup1m ─► SQLite
EtwHub.DNS ─────────────────►  DnsMap: pid → queries, ip → name                     │
GpuPoller (2 s) ────────────►  GpuAccumulator[pid]                                  │
ConnectionPoller (1 s) ─────►  ConnTable: (proto, 5-tuple, state, pid)             ─┘
```

- Sensors never block each other; they write to per-PID accumulators guarded by `Interlocked`/striped locks.
- The `Snapshot` step maps PIDs to `app_id` through `IdentityResolver` (cached per `(pid,createTime)`), sums per app and
  publishes one immutable `AppSnapshot[]` per second.
- `Rollup1m` computes avg/max/sum per metric per app from the 60 snapshots and writes one wide row per app-minute.
  Hourly and daily rollups are derived from stored rows (`06_DATA_MODEL.md`).
- **Backpressure:** live streams to the UI use a bounded channel with drop-oldest; rollup inputs are never dropped. If
  ETW reports lost events, the Agent flags the minute as `degraded` in the row and in `Health`.

## Identity, disk and events

- `IdentityResolver` runs on the new-process path (`03_APP_IDENTITY.md`) and is re-run when the catalog updates or the
  user overrides an app. Resolution results are persisted in `process_instances.app_id` so history never re-resolves.
- `DiskScanner` is a background job with its own budget (`09_DISK_SCANNER.md`): full scan on first sight of an app's
  install root, incremental via USN afterwards, one `disk_snapshots` row per app per drive per day.
- `EventDetector` turns lifecycle transitions into rows in `events`: launch, exit (with exit code), crash, version change,
  first-seen host, data growth, install/uninstall (`02_SPEC.md` FR-12).

## Lite mode (no Agent)

`AppLedger.Collector` is a library with a `CollectorOptions.Privilege` switch. Hosted inside the UI as a standard user it
runs `ProcessPoller` (own-user processes, no command lines of other users), `ConnectionPoller` and `GpuPoller`, and skips
all ETW sensors, USN and history persistence. The UI shows an `InfoBar` explaining what is missing and offers Agent setup.
Lite mode exists so the first-run experience never dead-ends on UAC.

## Resource budget (enforced, not aspirational)

| Component | Budget | Measured by |
|---|---|---|
| Agent idle (no UI connected) | < 1 % CPU 5-min avg on an 8-core box; < 100 MB private WS after 24 h | S1 harness, `Health` message |
| Agent under load (game + 1 GB/s file copy) | < 3 % CPU; zero lost ETW events in Network/DiskIO sessions | S1 |
| FileIO sampling window | ≤ 10 s per 5 min, or while the Disk tab is open (max 60 s continuous) | `05_COLLECTOR.md` |
| Disk scan | background priority, ≤ 1 core, I/O priority Low; full scan of 300 GB / 500 k files < 2 min | S4 |
| SQLite | < 300 MB for 100 apps × 6 months; month chart query < 100 ms | S5 |
| UI | 1 Hz chart refresh without dropped frames on an iGPU; < 250 MB private WS | manual |

The Agent self-measures (its own CPU time and private WS) and exposes them in `Health`; the UI shows a warning banner if
the Agent exceeds its budget for 10 consecutive minutes.

## Degraded modes

| Condition | Behavior |
|---|---|
| ETW session creation fails (too many system loggers, or another tool owns the name) | Retry 3× with backoff; then run without that sensor, mark `Health.Sensors[x] = Unavailable` with the Win32 error; UI banner |
| Events lost (`EventsLost > 0` in a minute) | Row flagged `degraded=1`; chart renders the minute hatched; Agent raises buffer size once (max 256 MB total) |
| No admin (task deleted by user/policy) | Lite mode |
| USN journal missing/reset (exFAT, journal disabled) | Full rescans on the daily schedule only |
| Catalog signature invalid | Keep last good catalog; never load unsigned data; event `CatalogRejected` |
| DB corrupted | Move aside as `appledger.db.corrupt-<date>`, start fresh, event `DatabaseReset`, toast |

## Lifecycle edge cases

- **Sleep/resume:** clock jumps are detected (`Stopwatch` vs wall clock drift > 5 s); the affected minute is dropped from
  rollups and the next snapshot re-baselines counters. ETW sessions survive sleep; the Agent re-validates them on resume.
- **Time zone / DST change:** rollups are UTC; only day-bucket presentation changes (`06_DATA_MODEL.md` §Time).
- **User switch / lock:** the Agent keeps running (task runs for the logged-in user). Only the owning user's session is
  observed by default (`12_PRIVACY_AND_RETENTION.md`).
- **PID reuse:** everything keyed on `(pid, createTime)`; parent links validated by `parent.createTime < child.createTime`.
- **Agent crash:** Task Scheduler restarts it (3 retries, 1 min). The 1-h memory ring is lost; rollups up to the last full
  minute are safe (WAL). Next start logs a `AgentRestarted` event with the previous exit reason.

## ADRs

Decisions behind this document are recorded in `24_ADR.md` (ADR-1 app-level identity, ADR-2 two-process broker,
ADR-3 C#-only, ADR-4 observer principle, ADR-5 tiered SQLite, ADR-10 no service in v1).
