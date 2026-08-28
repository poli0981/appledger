# 21 — Roadmap

Build order follows the dependency direction (`CLAUDE.md`): pure Core → adapters with smoke tests → collector →
Agent + IPC → App shell → pages. Each milestone is shippable as a pre-release; the numbers are targets, not dates.
FR/NFR ids refer to `02_SPEC.md`.

| Milestone | Scope | Exit criteria |
|---|---|---|
| **M0 — Kickoff + pre-flight** — **done 2026-08-23** | build-green scaffold, scaffold/doc reconciliation; `spikes/S1.EtwBudget` in **S1-lite** mode; S2 fixture set authored (the resolver itself lands later) | — solution builds warning-free on x64 and ARM64, `dotnet format` clean, 393 tests green, `AppLedger.Core` coverage 92.1 % (≥ 80 %)<br>— S1-lite **PASS** (`20_SPIKES.md` Status)<br>— 14 findings recorded in `24_ADR.md`, ADR-16 added |
| **v0.1 — Skeleton** — **done 2026-08-27** | ✔ solution + CPM + analyzers + self-contained CI; ✔ Core models, `PolicyGuard` rules and tier table, rollup math with golden tests, strict catalog parser and validator, minisign **parsing**, PSL/eTLD+1, formatters and redactors; ✔ 12 S2 fixtures authored; ✔ Infrastructure adapters with smoke tests — known folders, full canonicalization (8.3 + `GetFinalPathNameByHandleW` + device-path mapper), `PolicyGuard`, data root, ntdll poller, enrichment, PE/Authenticode, SQLite migrations + `IMetricsRepository`, minisign **verification** and the catalog loader | `dotnet test` green on CI (425 Core + 170 Infrastructure); Core coverage ≥ 80 % — **met: 92.1 %**<br>**Open:** the shipped catalog is not signed yet, so catalog rules stay off until `catalog/appledger-catalog.json.minisig` is committed (`13_CATALOG_RULES.md`) |
| **v0.2 — Agent + live** — **in progress** (**full S1 runs at the end of this milestone**) | ✔ Collector pipeline: `ProcessTable`, `InstanceRegistry`, `SnapshotBuilder`, `MinuteRollup`, `CollectorHost`, 5-min ring, live channel, `AppRegistrar`; ✔ sensors: `EtwHub` (Network/DiskIO/Process/ImageLoad/DNS), `ConnectionPoller`, `GpuPoller`, `NtProcessSource`; ✔ SQLite writer; ✔ `FallbackIdentityResolver` as the deliberate stand-in until S2 gates the real one at v0.3.<br>✔ `AppLedger.Ipc` (pipe contracts + framing) and `AppLedger.Agent` (`--serve/--console/--install-task/--remove-task/--status`); ✔ minimal App shell: onboarding (Privacy Gate + Agent setup), Home (FR-12 partial), Apps list (FR-1), Lite mode (FR-17); ✔ S1 harness: `spikes/S1.EtwBudget --hours` and `tools/s1-report.py`.<br>**Remaining:** the S1 runs themselves — 48 h per leg on the dev box, per `20_SPIKES.md` §S1 and `tests/MANUAL_CHECKLIST.md` — and the `cache_size` decision they settle | S1 re-run on this build passes; Agent survives logon/sleep/update cycle |
| **v0.3 — App detail** | Identity resolver v1 with seed catalog (FR-2 installed apps index, FR-3 pickers incl. FR-19), Overview (FR-4), Processes (FR-5), Details (FR-10), History charts with ScottPlot (FR-8), tooltips on every number (FR-20) | **S2 gate runs here** (≥ 95 %, zero game-into-launcher merges); manual matrix on Win 10 VM |
| **v0.4 — Disk** | DiskScanner full + USN incremental, snapshots, data-location learning via FileIO windows, Disk tab (FR-6), growth chart, data-growth alert | S4 pass; "reclaimable" labels only (no actions) |
| **v0.5 — Network** | Hosts per policy (eTLD+1, caps, browser `none`), DNS panel with `DnsQueryEx` expansion, ESTATS on demand, per-interface/VPN/metered flags, optional GeoIP DB, Network tab (FR-7), new-host alert | S3 < 10 %; privacy defaults verified by tests (`19_TESTING.md`) |
| **v0.6 — Events, policy, retention** | EventDetector (launch/exit/crash/version/install/uninstall), Events tab (FR-9), Alerts page + toasts (FR-13), Policy tab and overrides incl. merge/split/apply-to-history (FR-11), retention job + purge UI (FR-14), pause/private window (FR-15), catalog weekly update (FR-16) | S5 pass on real data after 2 weeks of dogfooding |
| **v0.7 — Polish** | Settings complete (FR-18), i18n `vi`/`ja` complete, accessibility pass (NFR-7), icons cache, usage heatmap, diagnostics copy, Velopack delta updates verified from v0.6 | manual matrix all boxes; zero `TODO(kickoff)` left in stubs — **already met at v0.2**: the five markers in the Agent, App and packaging stubs were all closed by the shell and Agent slices |
| **v1.0** | 6-month retention proven on the dev box (≥ 180 days of continuous data from v0.2 onward), S7 evidence documented, legal docs final, release signed (minisign) with checksums | README claims each backed by a spike or test |

## v1.x (after 1.0, each behind its spike)

- S8 SRUM back-fill of the 30–60 days before install (network bytes only).
- Agent-side toasts when the UI is not running (Windows toasts from the Agent via AUMID + shortcut).
- Optional column-level encryption of high-sensitivity tables (`net_hosts_daily`, `events`) with a DPAPI-protected key.
- Opt-in online category lookups (Steam store genre cache shared with FrameLedger; MSIX Store category).
- CSV/JSON export of any range; "compare two apps" on every chart.
- Shared WPF-UI shell + elevation-broker + pipe library extracted for FrameLedger/CommandForge/AppLedger (ops repo).

## v2 (design changes; each needs its own ADR)

- Packet/flow mode via pktmon ETW if S6 passes (flows + SNI, never payload).
- Windows Service mode with a machine-wide installer (pre-logon collection, multi-user machines) — `01 §Why not a service`.
- AppLedger as an OmniDeck native mini-app (same `AppLedger.Collector` library, read-only views).
- Per-app CLI (`appledger query --app discord --since 30d`) over the pipe for scripting.

## Explicitly not planned

See `23_NON_GOALS.md`. Items there are not "later"; they are "no" unless an ADR reverses them.
