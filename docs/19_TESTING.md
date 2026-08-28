# 19 — Testing

Principle: everything that decides *what a number means* (identity, policy, rollup math, DNS parsing, eTLD+1, byte
formatting) is pure code in `AppLedger.Core` and is tested on any OS without privileges. Everything that touches
Windows is an adapter with a **smoke test** (proves the P/Invoke works on this OS/arch) and, where the data is
event-shaped, a **replay test** (same handlers, recorded input). Real ETW sessions are `Category=Admin` and run only on
an elevated developer box.

## Layers

| Layer | Project | Runs on | What |
|---|---|---|---|
| Unit (pure) | `AppLedger.Core.Tests` | CI (windows-latest), any dev box | identity resolver fixtures (S2), host rules, adoption rules, install-root heuristic, `PolicyGuard` classification table, path canonicalization rules (string level), rollup math (golden files), retention calculator, eTLD+1 (PSL) lookups, DNS `QueryResults` parser, `ByteFormatter`/culture formatting, catalog schema + strict parser, minisign parser (`tests/fixtures/minisign/`; the Ed25519 verification itself is an Infrastructure smoke test) |
| Adapter smoke | `AppLedger.Infrastructure.Tests` | CI (non-admin) | `NtQuerySystemInformation` parse finds the test process with matching PID/createTime/threads (x64 and ARM64 struct offsets); `QueryFullProcessImageName`; `GetExtendedTcpTable` on a listening socket the test opens; PDH `GPU Engine` wildcard parse (skipped if no GPU counters, e.g. hosted runner); `SHGetKnownFolderPath` roots; `GetFinalPathNameByHandle` through a junction created in `%TEMP%`; PE version + Authenticode on `%SystemRoot%\System32\notepad.exe` (`CatalogSigned`); SQLite migrations on a temp DB; Dapper repositories round-trip; USN record parsing on a recorded buffer; launcher manifest parsers on fixture files (`.acf`, `.item`, `goggame-*.info`, `receipt.json.gz`) |
| ETW replay | `AppLedger.Infrastructure.Tests` + `Collector.Tests` | CI (non-admin) | `ETWTraceEventSource(file)` over `tests/fixtures/etl/*.etl` feeds the **same** handlers as the real-time `EtwHub`; asserts per-PID byte counts, DNS map entries, process lifecycle, FileIO directory aggregation, lost-event flagging |
| Pipeline | `AppLedger.Collector.Tests` | CI | fake `ISensor`s push scripted samples → `Snapshot` → `Rollup1m` → `IMetricsRepository` (in-memory) ; idle profile switching; clock-jump handling; backpressure (drop-oldest on live, never on rollup); `EventDetector` transitions (launch/exit/crash/version) |
| IPC | `AppLedger.Ipc.Tests` | CI | framing over a plain `Stream` (length prefix, 4 MB cap, oversized rejected *before* the buffer is allocated), envelope round-trip through `IpcJsonContext`, version negotiation, `Subscribe`/`AppsTick` cadence, fan-out to several subscribers with a slow one dropping. An earlier draft put these in `AppLedger.Collector.Tests`, which cannot work: `Collector` references only `Core` (`CLAUDE.md` §Solution layout), so it cannot see `AppLedger.Ipc` |
| Pipe security | `AppLedger.Infrastructure.Tests`, **`Category=Admin`** | elevated dev box only | a High-IL server and a Medium-IL client actually connect over the ADR-7 DACL + Medium integrity label; peer-executable verification accepts the real peer and rejects a substituted path. `CurrentUserOnly` alone cannot be assumed to work across that integrity boundary (`24_ADR.md` ADR-17) |
| UI | `AppLedger.App.Tests` | CI | view-model tests (no XAML): Home/Apps/AppDetail/Settings VMs against a fake `IAgentClient` + fake `ILedgerReader`; `ChartRange` math; `ByteFormatter` per culture; every `Strings.resx` key exists in `vi`/`ja` (missing → test failure, not warning); XAML compiles; navigation smoke: every `TargetPageType` is registered in DI |
| Admin (real sessions) | `Infrastructure.Tests`, `Category=Admin` | elevated dev box only | start `AppLedger-Kernel`/`AppLedger-User` sessions, observe own process's TCP send, own DNS query, own file write; stale-session reclaim; lost-event counter wiring |
| Repository guards | `AppLedger.Core.Tests` | CI | invariants that leave no trace in a diff: no `--` inside an XML comment (it makes a project file unloadable), no invisible characters in source (a stray NBSP in a literal formats right and compares wrong), every project declares `<Platforms>x64;ARM64</Platforms>` (ADR-16), and `BannedSymbols.txt` still bans every forbidden `PROCESS_*` right |
| Manual | `Category=Manual` + checklist below | release | installs, onboarding, VM matrix |
| Budget | `spikes/S1.EtwBudget` | release build, 12 h | numbers pasted into release notes (`16` §Release checklist) |

CI filter: `dotnet test --filter "Category!=Admin&Category!=Manual"`; coverage gate 80 % on `AppLedger.Core`
(`18_CI_CD.md`). Test names: `Method_Scenario_Expectation`. One assertion concept per test; table-driven with
`[Theory]` + `[MemberData]` for policy/identity cases.

## Fixtures

Fixture *locations* are resolved by `tests/Shared/TestPaths.cs`, linked into every test project rather than packaged:
it walks up to `AppLedger.slnx`, so nothing depends on the output layout (which carries a platform segment, ADR-16),
and both test assemblies read the shared corpora under `tests/fixtures/` through identical rules.

- **Identity** (`tests/AppLedger.Core.Tests/Identity/fixtures/*.json`): the 12 mandatory scenarios in
  `03_APP_IDENTITY.md` §Test fixtures, format in the folder README. Pass = ≥ 95 % expected `app_id` matches and **zero**
  game-into-launcher merges. Every grouping bug fixed adds a fixture first (red → green).
- **ETL** (`tests/fixtures/etl/`): recorded on a dev box with `tools/record-etl.ps1`, which enables the exact keyword
  set `EtwHub` uses, 60 s each. Scenarios: `idle`, `chrome-browsing` (bytes only; hostnames never asserted from a
  browser fixture), `file-copy-1gb`, `game-launch-steam`, `dns-burst`, `lost-events`. Keep each under 20 MB.
  **None are committed yet.** Recording needs an elevated terminal, and scrubbing is not implemented: rewriting
  paths inside an `.etl` needs a relogger pass (`ETWReloggerTraceEventSource`), not a text substitution. Until it
  exists, only record on a machine with nothing personal open. The handler seam (`EtwAccumulators`, which takes
  plain event records rather than TraceEvent types) is what makes replay possible at all, and it is already
  covered by scripted-input tests that need no fixture.
- **Catalog**: the shipped `catalog/appledger-catalog.json` is itself a fixture — the schema test parses it strictly,
  checks every `apps[].category` ∈ `categories`, every `host_rules[].rule` is known, globs expand, ids are unique and
  kebab-case. A second copy with a typo'd field must be **rejected** (strict parsing is a feature).
- **Policy** (`Core.Tests/Policy/cases.json`): table of `(input path, expected tier, reason)` including: `\\?\C:\Windows\System32`,
  `C:\WINDOW~1\SYSTEM~1`, `C:\Windows\System32\..\..\Users\x`, trailing-dot/space names, ADS `file.txt:stream`,
  a junction into `System32` (string-level expectation; the real junction case is in Infrastructure smoke),
  `D:\Windows` (not a Tier-0 root unless `%SystemRoot%` is on D:), `%LOCALAPPDATA%\Microsoft\Credentials` (Tier 1),
  password-vault globs from the catalog (Tier 1).
- **Rollups** (`Core.Tests/Rollup/golden/*.json`): 60 synthetic 1 s snapshots → expected 1 m row (avg/max/sum per
  metric), 60 × 1 m → 1 h, 24 × 1 h across a DST change → 1 d in local time (`06_DATA_MODEL.md` §Time).

## What is deliberately not unit-tested

- Pixel output of charts and pages (manual matrix + screenshots in PRs).
- Real anti-cheat behavior (S7 is a spike with a written procedure, not an automated test).
- Timing of Task Scheduler / logon (manual).

## Manual matrix (per release, `Category=Manual` checklist in `tests/MANUAL_CHECKLIST.md` — add at kickoff)

| Box | Checks |
|---|---|
| Windows 11 (x64, dGPU) — dev box | full onboarding, Agent task created, budget strip, all tabs with a game, Discord, Chrome, VS Code, a Steam game with EAC (S7 procedure), update from previous release, uninstall keep/delete |
| Windows 10 22H2 VM (x64, no GPU counters) | Lite mode first, then Agent; Mica fallback (solid); GPU column shows "N/A" not 0; DNS-Client provider present; window picker on a classic app |
| Windows 11 ARM64 (if available) | x64 + ARM64 apps mixed; `NtQuerySystemInformation` offsets; TraceEvent native bits load; picker through `ApplicationFrameHost` (FR-19) |
| Laptop on battery, metered Wi-Fi | task starts on battery, metered flag shown, idle profile after 10 min, sleep/resume re-baseline |
| Non-English Windows (ja-JP display language) | `PdhAddEnglishCounter` still resolves; `Strings.ja` rendering; byte/date formats |

## Regression rules

- A bug in identity → fixture. A bug in policy → `cases.json` row. A bug in a parser → minimal recorded input under
  `tests/fixtures/`. A budget regression → S1 rerun before merge. Docs updated in the same PR (`CLAUDE.md` §DoD).
