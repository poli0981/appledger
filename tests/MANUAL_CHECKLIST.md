# Manual checklist

The checks that no automated test can make, referenced by `docs/19_TESTING.md` §Manual matrix. Run before a
release; run the boxed items whenever the code they cover changes.

A step belongs here only if it is *genuinely* unautomatable — a second integrity level, a real logon, another
machine, a human's eyes. "Hard to automate" is not the bar; "cannot be automated without the harness becoming
the thing under test" is.

---

## Pipe security — the cross-integrity connect

**Covers:** `docs/24_ADR.md` ADR-17, `docs/07_IPC.md` §Transport.
**Run whenever:** `Infrastructure/Ipc/*` changes.

The automated tests prove the descriptor is built correctly, that a real pipe carries the Medium mandatory
label, and that an *elevated* process can still apply it (`Category=Admin`). They cannot prove the case that
actually matters — a **Medium-integrity client connecting to a High-integrity server** — because one test
process cannot be both, and building a restricted token by hand would make that P/Invoke the thing under test
rather than the pipe.

- [ ] From an **elevated** terminal: `dotnet run --project src/AppLedger.Agent -- --console`
- [ ] From a **normal** terminal: `dotnet run --project src/AppLedger.App`
- [ ] The App reaches the Agent — the health strip shows **Full** or **Degraded**, never **Lite**
- [ ] Kill the Agent; the App falls back to Lite with an `InfoBar` rather than hanging
- [ ] Restart the Agent; the App reconnects within the backoff window (1 s → 30 s)

If the connect fails with access denied, the label is the first suspect. `NamedPipeServerFactory.ReadAppliedSddl`
exists precisely so this is diagnosable: a pipe carrying `(ML;;NW;;;ME)` is labelled correctly, and one with no
`S:` section at all inherited the Agent's High integrity.

---

## The Scheduled Task

**Covers:** `docs/16_PACKAGING_AND_UPDATES.md` §Scheduled Task, §Agent CLI.
**Run whenever:** `Agent/Tasks/*` changes.

`docs/19_TESTING.md` puts "timing of Task Scheduler / logon" in *What is deliberately not unit-tested*, and
this is that. The XML is asserted by tests — every element, the account-name-versus-SID split, the UTF-16
encoding, the `current\` path — but whether Task Scheduler *accepts* it, and whether the task actually fires
at logon, only a real machine can say.

- [ ] Elevated: `AppLedger.Agent.exe --install-task` → exits 0, and the task appears in Task Scheduler
- [ ] Its action reads `…\AppLedger\current\AppLedger.Agent.exe --serve` — **not** a version-stamped folder
- [ ] Unelevated: `AppLedger.Agent.exe --status` → exits 0, prints `task: Running` and a reachable agent
- [ ] Log off and back on; the Agent is running again within a minute of the desktop appearing
- [ ] Unelevated: `schtasks /Run /TN "AppLedger Agent"` and `/End` both work — the owner needs no elevation
- [ ] Elevated: `AppLedger.Agent.exe --remove-task` → the running Agent acknowledges, then the task is gone
- [ ] `--status` now exits 7

If `/Create` is refused, the exact document that was submitted is at `%LOCALAPPDATA%\AppLedgerData\task\`.
Encoding is the first thing to check: `schtasks` reports a UTF-8 file as malformed XML.

---

## S1 — the 48-hour budget run

`docs/20_SPIKES.md` §S1. Two legs, run separately, each 48 h elevated. This is the gate for the always-on
premise: if it fails, `docs/24_ADR.md` ADR-3 is reopened and "collect only while the UI is open" becomes a
product question.

Neither leg can be automated for the obvious reason — it takes two days of a real machine being used by a
real person — and for a less obvious one: the load it measures is *your* load. A synthetic driver would
measure the driver.

**Leg A — the collector alone.** Elevated terminal:

```
dotnet run -c Release --project spikes/S1.EtwBudget -- --hours 48 --out s1.csv
```

**Leg B — the Agent that ships.** A second elevated terminal, ideally on a different day so the two legs are
not competing for the same ETW buffers and the same disk:

```
dotnet run -c Release --project src/AppLedger.Agent -- --console
```

During each run, per `docs/20` §S1:

- [ ] ≥ 2 h of a game, one of them with EasyAntiCheat running
- [ ] a 1 GB file copy and a Steam download
- [ ] a browser session, ~200 tabs opened over the run
- [ ] one sleep/resume
- [ ] one 10-minute window with the machine left completely alone — **note the wall-clock time**, it is what
      `--idle` takes
- [ ] no AppLedger UI running, so the collector stays on the idle profile the budget is written against

Then read both legs back:

```
python tools/s1-report.py --csv s1.csv --db "%LOCALAPPDATA%\AppLedgerData\appledger.db" --idle 03:00-03:10
```

- [ ] every row of both tables is PASS, or the failures are understood and recorded
- [ ] leg B's private WS at hour 24 and hour 48 are both under 100 MB — the two checkpoints exist because a
      leak shows up as the difference between them, not as either number alone
- [ ] the gap list contains the sleep/resume and nothing else. A gap nobody performed is an Agent that stopped
      writing, which is a bug in the thing being measured rather than a measurement
- [ ] leg A minus leg B is recorded: that difference is the cost of the pipe server, Serilog, the catalog and
      the real database, and it is the number that says where to cut if the budget is missed

Then settle what only this run can settle:

- [ ] `cache_size` in `docs/06_DATA_MODEL.md` §Pragmas — provisional at `-32000` (32 MB) since S1-lite measured
      a ~75 MB floor. Leg B's working set is what decides it
- [ ] temp database growth against S5's model
- [ ] results into `docs/20_SPIKES.md` §Status, `RELEASE_NOTES.md` and `docs/21_ROADMAP.md`

One ARM64 check, which needs no 48 hours:

- [ ] `dotnet run -c Release -p:Platform=ARM64 --project spikes/S1.EtwBudget -- --minutes 5` on an ARM64 box —
      TraceEvent's native `KernelTraceControl.dll` either loads or it does not. If it does not, ARM64 ships
      Lite-only and an ADR records why

---

## Release matrix

From `docs/19_TESTING.md`. Every row is one machine.

### Windows 11 (x64, dGPU) — dev box

- [ ] Full onboarding: Privacy Gate → Agent setup (one UAC) → defaults → Home
- [ ] The `AppLedger Agent` scheduled task exists, and starts at logon
- [ ] Budget strip shows plausible CPU and RAM
- [ ] All tabs with a game, Discord, Chrome, VS Code running
- [ ] A Steam game with EAC, following the S7 procedure (`docs/20_SPIKES.md`)
- [ ] Update from the previous release
- [ ] Uninstall, both "keep history" and "delete history"

### Windows 10 22H2 VM (x64, no GPU counters)

- [ ] Lite mode first, then Agent setup
- [ ] Mica falls back to a solid background without looking broken
- [ ] The GPU column reads **N/A**, not `0` — a zero here would claim we looked and found none
- [ ] The `Microsoft-Windows-DNS-Client` provider is present
- [ ] The window picker works on a classic (non-UWP) app

### Windows 11 ARM64 (if available)

- [ ] x64 and ARM64 apps mixed in one list
- [ ] `NtQuerySystemInformation` offsets are right — process list is not garbage
- [ ] TraceEvent's native bits load (`KernelTraceControl.dll`)
- [ ] The picker resolves through `ApplicationFrameHost` (FR-19)

### Laptop on battery, metered Wi-Fi

- [ ] The task starts on battery
- [ ] The metered flag is shown
- [ ] The idle profile engages after 10 min with no UI — poller drops to 2 s, ring shrinks to 60 s
- [ ] Sleep/resume re-baselines: no eight-hour spike in the first second after waking

### Non-English Windows (ja-JP display language)

- [ ] `PdhAddEnglishCounter` still resolves the GPU counters
- [ ] `Strings.ja` renders without clipping
- [ ] Byte and date formats follow the culture
