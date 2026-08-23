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
