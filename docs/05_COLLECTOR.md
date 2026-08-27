# 05 — Collector

`AppLedger.Collector` is the library that turns sensors into per-app snapshots and rollups. It is hosted by the Agent
(full) or by the UI (Lite). Everything here is C#; ETW is consumed through `Microsoft.Diagnostics.Tracing.TraceEvent`.

## Layering (pinned at kickoff)

The Collector project references **only** `AppLedger.Core` and carries no `PackageReference` to TraceEvent, CsWin32 or
IP Helper. Every sensor below is a **port** in Core (`IEtwSource`, `IProcessSource`, `IGpuSource`,
`IConnectionSource`, `IDiskScanner`) whose adapter lives in `AppLedger.Infrastructure` and is injected by the host.
The `EtwHub` code shown in the ETW sessions section is therefore Infrastructure code; the Collector starts,
supervises and aggregates. `CLAUDE.md` Solution layout is the authority for this direction.

## Components and threads

| Component | Thread | Cadence | Output |
|---|---|---|---|
| `ProcessPoller` | dedicated, `BelowNormal` | 1 s (`PeriodicTimer`) | `ProcessTable` deltas, new/exited instances |
| `EtwHub` | one thread per session inside TraceEvent's `Process()` loop + our handlers | event-driven | per-PID accumulators |
| `GpuPoller` | dedicated | 2 s | `GpuAccumulator` |
| `ConnectionPoller` | dedicated | 1 s | `ConnTable` |
| `SnapshotBuilder` | timer | 1 s, aligned to wall-clock seconds | `AppSnapshot[]` → live channel + 5-min ring |
| `Rollup1m` | timer | on minute boundary | `metrics_1m` rows |
| `RollupJobs` | background | hourly / daily (first idle after 03:00 local) | `metrics_1h`, `metrics_1d`, `disk_snapshots` |
| `DiskScanner` | dedicated, `Lowest`, I/O priority Low | event/scheduled | `disk_locations`, `disk_snapshots` |
| `RetentionJob` | background | nightly | deletes + incremental vacuum |

Handlers on ETW threads must be allocation-free in steady state: update `long` fields with `Interlocked.Add`, look up
accumulators in a `ConcurrentDictionary<ProcessKey, Accumulator>` keyed by `(pid, createTime)`; PID→key is a
`volatile` array indexed by PID (max 65536 entries × 16 B) maintained by the poller and `ProcessStart/Stop` events.

## ETW sessions

Two real-time sessions with fixed names so a crashed Agent can reclaim them (`TraceEventSession.GetActiveSessionNames()`
→ stop stale ones named ours before starting):

```csharp
// Session 1 — kernel providers (system logger). Name must not be "NT Kernel Logger" so we coexist with other tools.
var kernel = new TraceEventSession("AppLedger-Kernel") { StopOnDispose = true, BufferSizeMB = 64, BufferQuantumKB = 1024 };
kernel.EnableKernelProvider(
    KernelTraceEventParser.Keywords.Process
  | KernelTraceEventParser.Keywords.Thread          // needed so DiskIO's IssuingThreadId resolves to a PID
  | KernelTraceEventParser.Keywords.ImageLoad
  | KernelTraceEventParser.Keywords.NetworkTCPIP
  | KernelTraceEventParser.Keywords.DiskIO);
// FileIO | FileIOInit are added/removed dynamically for sampling windows (see below).

// Session 2 — user providers.
var user = new TraceEventSession("AppLedger-User") { StopOnDispose = true, BufferSizeMB = 16 };
user.EnableProvider("Microsoft-Windows-DNS-Client", TraceEventLevel.Informational);
```

- `kernel.Source.Kernel.TcpIpSend += OnTcpSend` etc. (strongly typed); DNS via `user.Source.Dynamic.All` filtered on
  provider GUID and event IDs 3006/3008.
- Each session's `Source.Process()` runs on its own thread; `Dispose` on shutdown stops the session (`StopOnDispose`).
- On Windows, at most 8 system logger sessions can exist; creation failure (`ERROR_NO_SYSTEM_RESOURCES`, 1450) or
  `ERROR_ALREADY_EXISTS` after reclaim → retry ×3 with 5 s backoff → sensor `Unavailable`.
- **Lost events:** read `TraceEventSource.EventsLost` once per minute; if it increased, flag the minute `degraded`,
  log at Warning with the delta, and double `BufferSizeMB` once (cap 128 for kernel, 32 for user). Persistent loss is a
  Health warning, never a crash.
- Rundown: at session start, `ProcessDCStart`/`ImageDCStart` events seed the process table and runtime detection.

## FileIO sampling windows

FileIO is the only noisy keyword (thousands of events/s during large copies). Policy:
- Default: 10 s window every 5 min (`kernel.EnableKernelProvider(current | FileIO | FileIOInit)` then revert). The
  keyword change is an `EnableTrace` call on the live session — no session restart.
- Disk tab open for an app: continuous, max 60 s, then back to periodic (UI sends `SamplingHint{disk:true}` every 30 s
  while the tab is visible; absence of hints ends the window).
- During a window, events are aggregated per `(pid, directory)` into `DirectoryActivity{reads, writes, bytes, lastWrite}`;
  file-level top lists keep at most 50 entries per app (LRU). Nothing per-file is persisted except top-20 "largest
  recently written" per app per day.
- Paths pass through `PolicyGuard` before aggregation: Tier-0 paths are aggregated into a single "(Windows)" bucket;
  Tier-1 paths keep sizes but drop names.

## Accumulators → snapshot

`SnapshotBuilder` every second:
1. Reads `ProcessTable` (immutable array produced by the poller).
2. For each live instance, collects `{cpuUser, cpuKernel, wsPrivate, commit, ws, ioRead, ioWrite, diskRead, diskWrite,
   netIn, netOut, gpuPct, vramDed, vramShared, threads, handles, hardFaults}` — deltas for counters, values for gauges.
3. Maps to `app_id` via `IdentityResolver` cache; sums per app; computes `procs`.
4. Publishes `AppSnapshot[]` (sorted by `app_id`) to: the live channel (bounded 10, drop-oldest), the **5-min ring**
   (300 × apps ≈ 5.5 MB for 100 apps), and `Rollup1m`'s minute buffer.
5. Self-measures: own CPU time and private WS → `Health`.

Per-endpoint network accumulation (`NetAccumulator.Endpoints: Dictionary<(proto, daddr, dport), (in, out, first, last)>`)
is capped at 2 000 endpoints per app (LRU); overflow aggregates into `(other)`. Hostnames are attached at rollup time
via the DNS map, and policy (`12`) decides what is persisted.

## Rollup math (Core, pure, golden-tested)

For each app over a window of N 1-second snapshots (N may be < 60 if the app started/stopped mid-minute):
- `sum`: `io_*`, `disk_*`, `net_*`, `runtime_s = N`
- `avg`: `cpu_pct`, `gpu_pct`, `ws_private`, `commit_bytes`, `threads`, `handles`, `procs` (weighted by presence)
- `max`: `cpu_pct_max`, `ws_private_max`, `vram_ded_max`, `procs_max`
Hourly rows from 1-m rows: sums add, avgs are `runtime_s`-weighted, maxes max. Daily from hourly likewise.
Percent values are stored as `REAL` 0–100 with one decimal; bytes as `INTEGER`.

## Budget controls

| Knob | Default | Why |
|---|---|---|
| Poller interval | 1 s (2 s when no UI connected and no app is "watched") | halves idle cost |
| GPU wildcard expansion | every 10 s | PDH cost |
| FileIO window | 10 s / 5 min | noisiest keyword |
| ESTATS | only for the app in view | per-connection cost, admin |
| DNS map size | 10 000 ip→name entries, LRU | memory |
| Endpoint map | 2 000 per app | memory |
| Ring window | 300 s × live apps (1 min when idle) | measured: `AppSample` is 184 B, so 5.5 MB / 100 apps |
| Idle detection | no UI for 10 min → `Idle` profile (poller 2 s, no GPU polling, ring keeps 15 min) | budget |

The S1 harness (`spikes/S1.EtwBudget`) hosts this library with the real sessions and logs its own CPU/RSS every 10 s.
Budget violations in S1 block feature work (`20_SPIKES.md`).

**Measured so far.** `ProcessPoller`'s source (`NtProcessSource`, 2026-08-27, Release, i7-14700KF) costs **2.4 ms per
poll over ~330 processes** — one `NtQuerySystemInformation` call plus one linear pass, with the buffer retained between
calls so the steady state allocates only the image-name strings. At the default 1 Hz that is ~0.24 % of one core, well
inside the < 1 % budget, and the figure is reproduced by
`NtProcessSourceTests.Snapshot_PollCost_IsReportedForTheBudgetNote` rather than being a claim.

**Where the budget actually binds.** S1-lite (2026-08-23) measured ~75 MB private working set for the two sessions
alone, with handlers that only increment counters, and 0.03 % CPU. Against a 100 MB budget that leaves roughly
20 MB for every structure on this page plus the SQLite page cache (`06_DATA_MODEL.md` sets it to 32 MB in the
Agent). Memory, not CPU, is therefore the number to hold a v0.2 design against: size the accumulators, the ring
and the DB cache together, and re-measure with S1 rather than assuming the CPU headroom transfers.

## Failure handling

- Every sensor implements `ISensor { Task StartAsync; Task StopAsync; SensorHealth Health; }`; `CollectorHost`
  supervises them; a throwing handler is caught, counted (`Health.HandlerErrors`), and the event dropped — never
  re-thrown into TraceEvent's loop.
- `ProcessPoller` buffer growth is bounded at 64 MB (pathological process counts) → beyond that, sample every 2 s.
- If `IdentityResolver` throws for an instance, the instance resolves to `root:` fallback with confidence 0.1 and an
  `IdentityError` event; the exception is logged once per image path.
- Clock jump (sleep/resume, manual time change): detected by comparing `Stopwatch` elapsed with wall-clock delta > 5 s;
  the current minute is discarded; counters re-baseline.

## Lite profile

`CollectorOptions.Privilege = Standard`: `EtwHub` not constructed; `ProcessPoller` filters to own user; enrichment
skips token queries on foreign processes; `Rollup*` disabled; ring kept (15 min) so the live charts still work.
