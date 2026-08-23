# 18 — CI/CD

Pipelines live in this repository and are **self-contained**. An earlier design made them caller stubs into the
ops repo `poli0981/.github`; that was reverted at kickoff (`24_ADR.md` §Findings) because three of the four
reusable workflows did not exist under the names used, the one that did takes a different input set, and its
C# CodeQL job runs on `ubuntu-latest`, which cannot build the four `net10.0-windows` projects. Owning the
pipelines here keeps the gates below enforceable rather than aspirational. Action versions match the ones the
ops repo has verified (`checkout@v7`, `setup-dotnet@v5`, `upload-artifact@v7`, `codeql-action@v4`).

Every workflow declares `permissions:` explicitly and narrowly.

## Workflows

| File | Trigger | Runner | Permissions | What it does |
|---|---|---|---|---|
| `ci.yml` | push `main`, PRs | `windows-latest` | `contents: read` | locked restore, format check, build, filtered test with coverage, coverage gate, vulnerability gate, artifacts; plus a build-only ARM64 job |
| `codeql.yml` | push/PR `main`, weekly | `windows-latest` | `actions: read`, `contents: read`, `security-events: write` | traced C# build, CodeQL analyze |
| `release.yml` | tag `v*` | `windows-latest` | `contents: write` | test, publish App + Agent into one folder, `vpk pack`, checksums, GitHub Release |
| `notify.yml` | release published | `ubuntu-latest` | `contents: read` | posts title + URL to Discord; skips when `DISCORD_WEBHOOK` is unset |

WPF and the WinRT projections do not build on Linux, so everything except the notifier runs on Windows.

## Gates `ci.yml` enforces

- **Locked restore** — `packages.lock.json` is committed (`RestorePackagesWithLockFile`), so a drifted pin
  fails instead of resolving silently. After changing a pin, re-run restore with the force-evaluate switch.
- **Format** — `dotnet format --verify-no-changes`. This only works because `.gitattributes` normalizes the
  tree to LF to match `.editorconfig`; without that pair the check can never pass on Windows.
- **Build** — warnings are errors through `Directory.Build.props`, so no extra flag is needed.
- **Tests** — `--filter "Category!=Admin&Category!=Manual"`. Admin tests need real ETW sessions on an
  elevated box; Manual is the release checklist.
- **Coverage** — `AppLedger.Core` line coverage ≥ 80 %, parsed from the Cobertura report. Core is the part
  that decides what every number means, and it runs anywhere without privileges (`19_TESTING.md`).
- **Vulnerabilities** — `dotnet list package --vulnerable --include-transitive`. The command exits 0 even
  when it finds something, so the step greps for the `>` rows and fails the build itself. This gate caught a
  high-severity SQLitePCLRaw advisory on the first run (`24_ADR.md`).
- **ARM64** — a build-only job. ARM64 is a supported target (NFR-6) with its own Velopack channel, and an
  x64 runner cannot execute ARM64 test binaries, so building is all CI can honestly do.

The catalog schema tests (`Category=Catalog`) and the repository guards run inside the normal test pass; they
need no separate job. XamlStyler is not wired yet — there is no XAML worth checking until the v0.2 shell.

## Release pipeline

1. Tag `vX.Y.Z` → `release.yml` derives the version from the tag, runs the tests, publishes
   `AppLedger.App` and `AppLedger.Agent` framework-dependent into **one** folder, and `vpk pack`s it as a
   single package (`16_PACKAGING_AND_UPDATES.md`).
2. SHA-256 of every asset is appended to the release body. The README tells users to verify these before
   clicking through SmartScreen, so publishing them is a promise, not a nicety.
3. `notify.yml` announces the release if a webhook is configured.

**Two gaps, both deliberate and both blocking v1.0:**

- **ARM64 channel.** `release.yml` builds `win-x64` only. The ARM64 channel of `16_PACKAGING_AND_UPDATES.md`
  needs a second `vpk pack --runtime win-arm64` on its own channel so the updater stays on its own track.
- **Catalog signing.** The Agent verifies even the bundled catalog (`13_CATALOG_RULES.md`), so a release
  today would ship rules the Agent rejects. CI must sign `catalog/appledger-catalog.json` with
  `CATALOG_MINISIGN_KEY` and attach the `.minisig` and `public_suffix_list.dat`. Open question to settle when
  this is built: whether catalog-only releases get their own `catalog-*` tag read through the GitHub API, or
  whether catalog assets are simply re-attached to every app release — the second is simpler and is the
  default unless the first proves necessary.

`release.yml` has never run. Do a dry run on a pre-release tag before trusting it.

## Dependabot

Weekly NuGet and Actions updates; Microsoft/System and Serilog grouped; `WPF-UI*` ignored on purpose — bumped
by hand after reading the release notes (`Directory.Packages.props` header). Velopack and TraceEvent bumps
require a manual S1 rerun, because both sit directly in the collector path.

## Secrets (repo settings)

| Secret | Used by | Required for |
|---|---|---|
| `CATALOG_MINISIGN_KEY`, `CATALOG_MINISIGN_PASSWORD` | `release.yml` (not yet wired) | signing the catalog at release |
| `DISCORD_WEBHOOK` | `notify.yml` | release announcements; absent = the job skips |

`GITHUB_TOKEN` is the built-in token; no PAT is needed. There is no code-signing certificate
(`16_PACKAGING_AND_UPDATES.md` §Code signing).

The catalog signature test uses a test key pair committed under `tests/fixtures/minisign/`, shared by
Core.Tests (parser) and Infrastructure.Tests (Ed25519 verification). It is never the release key, and it was
generated with NSec rather than the `minisign` CLI so development needs no extra tool.

## Branch protection

`main`: PR required, CI + CodeQL required, linear history, signed commits required.
