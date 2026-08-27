# 0.2.0 — unreleased

The Agent's collection path, end to end but not yet hosted. Still nothing user-facing.

- **v0.1 closed.** `AppLedger.Infrastructure` went from zero source files to the full adapter set: known
  folders, path canonicalization, `PolicyGuard`, the data root, the ntdll process poller, enrichment,
  PE/Authenticode, SQLite storage and migrations, and catalog signature verification.
- **The collector pipeline runs**: system snapshot to per-instance deltas to per-app samples to
  `metrics_1m` rows. `CollectorHost` owns the write ordering that keeps the `metrics_1m` to `apps` foreign
  key satisfied.
- **Sensors**: `EtwHub` (two real-time sessions), `ConnectionPoller` (IP Helper, works unelevated),
  `GpuPoller` (PDH). Every one of them reports `Unavailable` with a reason rather than reporting zero when
  it cannot run — an absent GPU counter set and a missing ETW session are normal states, not faults.
- **The catalog is signed and loads.** Public key `6ED9A5D305231FDB` is embedded, the signature is
  committed, and a test fails the build if the two ever drift apart.
- Numbers that were assumed and are now measured: the process poller costs 2.4 ms per poll over ~330
  processes, and an `AppSample` is 184 bytes — which is what shrank the live ring from the documented one
  hour to five minutes, because an hour of 100 apps would have been 66 MB against a 100 MB Agent budget.
- Twelve findings added to `docs/24_ADR.md` §Findings, including two that only an elevated test run could
  surface: an unguarded ETW processing loop that killed its host, and — more quietly — the same loop
  returning cleanly and leaving the sensor claiming to be healthy while collecting nothing.

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
