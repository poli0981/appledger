# 0.2.0 — unreleased

The Agent runs, the UI connects to it, and the two of them show live per-app numbers. First milestone with
anything to look at. The one thing still outstanding is S1 itself: the harness for both legs is in, the
48-hour runs are not.

- **v0.1 closed.** `AppLedger.Infrastructure` went from zero source files to the full adapter set: known
  folders, path canonicalization, `PolicyGuard`, the data root, the ntdll process poller, enrichment,
  PE/Authenticode, SQLite storage and migrations, and catalog signature verification.
- **The collector pipeline runs**: system snapshot to per-instance deltas to per-app samples to
  `metrics_1m` rows. `CollectorHost` owns the write ordering that keeps the `metrics_1m` to `apps` foreign
  key satisfied.
- **Sensors**: `EtwHub` (two real-time sessions), `ConnectionPoller` (IP Helper, works unelevated),
  `GpuPoller` (PDH). Every one of them reports `Unavailable` with a reason rather than reporting zero when
  it cannot run — an absent GPU counter set and a missing ETW session are normal states, not faults.
- **The sensors are actually joined to the samples now.** `SnapshotBuilder` only ever summed `ProcessDelta`,
  so `AppSample`'s network, disk and GPU fields were structurally present and permanently zero. Hosting the
  Agent before fixing that would have written six months of history with empty columns and nothing to
  indicate it. The window boundary is a read-and-zero rather than a reset, so no event can fall into the gap
  between the two, and GPU readings carry forward across the 2 s / 1 s cadence mismatch — which is unbiased,
  because the rollup divides by sample count.
- **The Agent hosts it**: `--serve`, `--console`, `--install-task`, `--remove-task`, `--status`. The
  Scheduled Task points at `%LOCALAPPDATA%\AppLedger\current\AppLedger.Agent.exe` rather than a versioned
  path, so an update does not orphan it, and the XML is written UTF-16 because `schtasks /XML` silently
  rejects UTF-8 as malformed.
- **IPC**: `\\.\pipe\AppLedger.v1`, length-prefixed UTF-8 JSON with a source-generated serializer. The
  server accepts at most 64 KB from a client but sends up to 4 MB — deliberately asymmetric, because it cuts
  by 64x the memory a hostile same-user process can make an *elevated* Agent commit before a frame is even
  parsed. A subscriber that has dropped 60 consecutive ticks is disconnected: with four server instances, a
  wedged client is holding one of the user's own four slots.
- **Pipe security follows ADR-7, not the `CurrentUserOnly` that `docs/07` had drifted to** (now ADR-17). The
  pipe carries an explicit DACL for the user plus Administrators and a Medium integrity label, and both ends
  verify the other's image path. Getting there turned up the milestone's sharpest finding: `PipeSecurity`
  **cannot carry a mandatory label and says nothing when it drops one** — the managed model maps a SACL onto
  audit rules, and an `ML` ace is not an audit ace. Had that path succeeded it would have produced an
  unlabelled pipe stuck at the Agent's High integrity, refusing the UI — the exact failure ADR-17 exists to
  prevent, reached through the API meant to prevent it.
- **A minimal but real App shell**: WPF-UI host, six registered pages, three-step onboarding (Privacy Gate →
  Agent setup → defaults), the health strip, the apps list (FR-1), and **Lite mode** — the collector hosted
  in the UI as a standard user when no Agent answers, so a first run never dead-ends on a UAC prompt. What
  Lite cannot see is reported absent rather than zero. Every user-visible string exists in `en`, `vi` and
  `ja`, generated from one source by `tools/gen-strings.py`.
- **Both S1 legs are runnable.** `spikes/S1.EtwBudget --hours` measures the collector without the pipe
  server, Serilog or the catalog; the shipping Agent is measured through the `health_minutes` rows it writes
  anyway, with no measurement-only code path; `tools/s1-report.py` renders either against the criteria and
  says plainly which ones it cannot decide from the data it has.
- **The catalog is signed and loads.** Public key `6ED9A5D305231FDB` is embedded, the signature is
  committed, and a test fails the build if the two ever drift apart.
- Numbers that were assumed and are now measured: the process poller costs 2.4 ms per poll over ~330
  processes; an `AppSample` is 184 bytes — which is what shrank the live ring from the documented one hour
  to five minutes, because an hour of 100 apps would have been 66 MB against a 100 MB Agent budget; a
  `NetAccumulator` is 280 bytes rather than the ~250 KB it pre-sized itself to, which at 300 networked
  processes was ~75 MB of the same budget; and the Agent unelevated costs 0.04 % CPU and 36 MB working set.
  Added to S1-lite's ~75 MB ETW floor, that puts the elevated Agent near 111 MB against a 100 MB budget
  before SQLite's page cache is touched. **That is the number S1 exists to settle**, and `cache_size` stays
  provisional until it does.
- Twelve findings added to `docs/24_ADR.md` §Findings across the milestone, and one new ADR (ADR-17). Several were
  invisible to the build: permanently-zero metric columns, a silently discarded integrity label, JSON
  parsers that die two different ways on a malformed frame, a resource class that compiles and then throws
  at window construction, and a resource generator that only works on the second build.

# 0.1.0 — unreleased

Opening phase (M0). Nothing user-facing yet; the milestones are `docs/21_ROADMAP.md`.

- Repository builds, tests and formats cleanly on x64 and ARM64. Fifteen scaffold defects fixed, each
  recorded in `docs/24_ADR.md` §Findings — among them a project file that could not be loaded at all, a
  package pin with no stable release, a vulnerable transitive dependency, and a seed catalog that failed
  its own schema rules.
- `AppLedger.Core`: identity model, path policy, rollup math, strict catalog parser, eTLD+1 and formatters.
  393 tests, 92 % line coverage.
- `spikes/S1.EtwBudget`: the S1-lite ETW pre-flight harness. Not yet run — it needs an elevated terminal.
- Twelve S2 identity fixtures authored ahead of the resolver.
