# 18 — CI/CD

All pipelines are **caller stubs** in `.github/workflows/` that invoke reusable workflows in the ops repo
`poli0981/.github` (desktop-csharp template). Every stub declares `permissions:` explicitly — callers without a block
default to `none` when calling reusable workflows (the Phase-5 fix).

## Stubs

| File | Reusable workflow | Permissions | Trigger |
|---|---|---|---|
| `ci.yml` | `reusable-dotnet-desktop.yml@main` | `contents: read` | push `main`, PRs |
| `codeql.yml` | `reusable-codeql.yml@main` (`languages: csharp`) | `actions: read`, `contents: read`, `security-events: write` | push/PR `main`, weekly |
| `release.yml` | `reusable-dotnet-release.yml@main` (`velopack: true`) | `contents: write` | tag `v*` |
| `notify.yml` | `reusable-notify.yml@main` | `contents: read`, `actions: read` | release published |

`TODO(kickoff)` comments in the stubs mark inputs that must be aligned with the ops repo's actual input names
(`test-filter`, `pack-id`, `main-exe` are proposals; if the reusable workflow lacks them, add them there first —
the reusable file's header comment lists required permissions).

## What CI must do for this repo (inputs to the reusable workflow)

- Runner: `windows-latest` (WPF + WinRT TFMs do not build on Linux).
- `dotnet restore` with `--locked-mode` once `packages.lock.json` is committed (enable `RestorePackagesWithLockFile`).
- `dotnet build -c Release -warnaserror`, `dotnet format --verify-no-changes`, XamlStyler check.
- `dotnet test --filter "Category!=Admin&Category!=Manual" --collect:"XPlat Code Coverage"`; coverage threshold 80 % on
  `AppLedger.Core` (rollup math, identity, policy) — the parts that can run anywhere.
- Vulnerability gate: `dotnet list package --vulnerable --include-transitive` fails on Moderate+.
- Catalog schema test (`Category=Catalog`) runs on every build; catalog signature verification test uses a test key pair
  committed under `tests/fixtures/minisign/` (never the release key), shared by Core.Tests (parser) and
  Infrastructure.Tests (Ed25519 verification).
- Artifacts: test results, coverage, and `publish/` folders for manual smoke.

## Release pipeline

1. Tag `vX.Y.Z` → `release.yml` → publish App + Agent (x64, then ARM64 as a second channel), `vpk pack`, upload
   `AppLedger-win-Setup.exe`, `AppLedger-win-Portable.zip`, `RELEASES`/`releases.win.json`, delta `.nupkg`s.
2. Catalog step: `minisign -S` with `CATALOG_MINISIGN_KEY` (GitHub secret) + `CATALOG_MINISIGN_PASSWORD`; attach
   `appledger-catalog.json` + `.minisig` + `public_suffix_list.dat` to the release. The catalog can also be re-released
   alone (tag `catalog-YYYY.MM.N`) → a "catalog-only" release consumed by the weekly updater (the updater looks at
   `releases/latest/download/…`, so a catalog-only release must also re-attach the current app assets, or the updater
   must prefer the `catalog-*` tag — decide at kickoff; simplest: catalog assets are attached to every app release and
   `catalog-*` releases are marked pre-release and read via the GitHub API by tag prefix).
3. SHA-256 of every asset in the release body (SmartScreen guidance in README).
4. `notify.yml` cross-posts (Discord/Telegram/…) via the ops reusable notifier.

## Dependabot

Weekly NuGet + Actions updates; Microsoft/System and Serilog grouped; `WPF-UI*` ignored on purpose — bumped by hand
after reading release notes (`Directory.Packages.props` header). Velopack and TraceEvent bumps require a manual S1 rerun.

## Secrets (repo settings)

`CATALOG_MINISIGN_KEY`, `CATALOG_MINISIGN_PASSWORD`, notifier webhooks (managed via the ops-repo bootstrap script).
No signing certificate yet (see `16` §Code signing).

## Branch protection

`main`: PR required, CI + CodeQL required, linear history, signed commits required.
