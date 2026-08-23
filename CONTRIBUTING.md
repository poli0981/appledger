# Contributing

Thanks for helping. AppLedger is a solo-maintained project with strict invariants; please read `CLAUDE.md` (it is the
entry point for humans too) and `docs/23_NON_GOALS.md` before proposing features.

## Good first contributions

- **Catalog entries** (`catalog/appledger-catalog.json`): an app grouped wrongly, a missing launcher helper, a data/cache
  folder, an anti-cheat service/driver name. Follow the checklist in `docs/13_CATALOG_RULES.md` and add an identity
  fixture when grouping changes (`tests/AppLedger.Core.Tests/Identity/fixtures`).
- **Translations** (`vi`, `ja`) in `src/AppLedger.App/Resources/Strings.*.resx` — `docs/14_I18N.md`.
- **Recorded fixtures** for parsers (launcher manifests, USN buffers, DNS result strings) with personal data scrubbed.

## Rules that block a PR

- Any `OpenProcess` with more than `PROCESS_QUERY_LIMITED_INFORMATION`, any injection, any driver, any evasion.
- Any verb that changes system state (kill, delete, uninstall, block) — see NG-1…NG-3.
- Any new network call, telemetry, or stored field without a row in the privacy docs.
- Floating package versions; bumping `WPF-UI` without the manual UI matrix.
- Behavior that deviates from `docs/` without updating the doc in the same PR.

## Workflow

1. Open an issue first for anything beyond a catalog entry or a typo.
2. Branch from `main`; Conventional Commits (`feat(collector): …`, `fix(identity): …`); GPG-signed commits.
3. `dotnet build -warnaserror`, `dotnet test`, `dotnet format --verify-no-changes`, XamlStyler clean.
4. Fill the PR template checklist, including the budget note for collector-path changes.
5. One concern per PR; screenshots for UI changes.

By contributing you agree that your contribution is licensed under GPL-3.0-only.
