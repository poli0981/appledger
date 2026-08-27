# 24 — Architecture Decision Records

Format: context → decision → consequences. Status is `Accepted` unless noted. New ADRs append; superseded ones stay
with a pointer. "Findings" (a Windows API behaving differently than a doc assumed) go in the last section with a link
to the doc that was fixed.

## ADR-1 — Identity is app-level, resolved once per process instance

- **Context:** users think in apps (Chrome, Steam, a game), Windows exposes processes. Chrome is 40 PIDs; a game is a
  launcher plus children plus an anti-cheat service; `svchost`/`python.exe` are hosts, not apps.
- **Decision:** a central `IdentityResolver` maps each `(pid, createTime)` to a stable `app_id` using an ordered
  source precedence (user override › catalog › MSIX › launcher manifests › Uninstall › package managers › script ›
  install root) plus data-driven host rules; results are persisted so history never re-resolves (`03_APP_IDENTITY.md`).
- **Consequences:** every metric table keys on `app_id`; grouping quality is a measured gate (S2); rules ship in the
  signed catalog so fixes need no release; a wrong grouping is a data bug with a fixture, not a UI bug.

## ADR-2 — Two processes: standard-user UI + elevated Agent via Scheduled Task

- **Context:** live per-process network/disk/DNS need ETW kernel providers (admin); a chart-heavy WPF UI must not run
  elevated; history needs an always-on collector.
- **Decision:** `AppLedger.exe` (Medium IL) + `AppLedger.Agent.exe` elevated through a Scheduled Task (*At log on*,
  *Highest*), one UAC prompt at onboarding, named pipe + shared SQLite under the same user profile. Same pattern as
  CommandForge/FrameLedger (`01_ARCHITECTURE.md`).
- **Consequences:** UAC is the trust step and is not a security boundary — the pipe is treated as an attack surface
  (`11` §Privilege boundary); no pre-logon collection; Lite mode exists for users who decline elevation.

## ADR-3 — C# only; a failed spike cuts the feature, it never adds a toolchain

- **Context:** the user's stack is C#/.NET; FrameLedger needed C++ only because injection has no managed path;
  everything AppLedger needs is passive user-mode API reachable from C# (TraceEvent, CsWin32, WinRT projections).
- **Decision:** no C++/Rust project in this repo. If a spike (S1 budget, S6 packet) shows managed code cannot meet the
  bar, the feature is dropped or reshaped, and this ADR is cited.
- **Consequences:** one toolchain, one CI matrix, ARM64 for free; the hardest problems (attribution, data model) get the
  iteration speed; some ceilings (packet capture at line rate) are accepted.

## ADR-4 — Observer principle: no handle beyond `PROCESS_QUERY_LIMITED_INFORMATION`, ETW over handles

- **Context:** anti-cheat drivers strip or flag handles with `VM_READ`; protected processes reject them; a monitor that
  opens 200 handles a second is itself a load.
- **Decision:** system-wide snapshot (`NtQuerySystemInformation`) for counters, ETW for lifecycle/modules/I/O/network,
  query-limited handles only for image path/package/token, **none** for Tier-2 processes (`11_SAFETY_POLICY.md`).
- **Consequences:** no module list for a process without ImageLoad events (we accept), no handle tables (NG-14); the
  S7 log proves the claim.

## ADR-5 — Tiered SQLite rollups (1 s memory → 1 m / 1 h / 1 d on disk), wide rows

- **Context:** 6 months at 1 s is impossible; at 1 m it is ~1 M rows/app/month. Users read charts at 1 h/1 d granularity
  for anything older than a week.
- **Decision:** `metrics_1m` (7 days), `metrics_1h` and `metrics_1d` (retention), one wide row per app-bucket with
  avg/max/sum per metric, UTC storage with local-day bucketing at query time; `auto_vacuum=INCREMENTAL` + nightly
  retention (`06_DATA_MODEL.md`). Size/latency is a gate (S5).
- **Consequences:** sub-minute detail is only live; purge is a delete by key range; schema migrations are forward-only
  with a backup.

## ADR-6 — WPF + WPF-UI 4.3.0 (pinned) + ScottPlot 5

- **Context:** Fluent look on Windows 10/11, Velopack-friendly, same shell as FrameLedger/CommandForge; LiveCharts2 is
  prettier but weaker with large series; several WPF-UI 4.x builds are deprecated on NuGet.
- **Decision:** `WPF-UI` exactly 4.3.0 (`[4.3.0]`), `ScottPlot.WPF` 5.x, CommunityToolkit.Mvvm, H.NotifyIcon. Rules in
  `22_WPFUI_SYNTAX.md`.
- **Consequences:** Dependabot ignores `WPF-UI*`; bumps are manual with the UI matrix; charts follow one palette.

## ADR-7 — IPC is length-prefixed JSON over a named pipe with peer verification

- **Context:** FrameLedger used newline JSON; AppLedger streams larger 1 Hz ticks (all apps) and must reject oversized
  or malformed frames from any same-user process.
- **Decision:** `\\.\pipe\AppLedger.v1`, 4-byte length prefix, 4 MB cap, System.Text.Json source-generated, version
  negotiation in `Hello`, DACL to the owning user + Administrators, integrity label Medium, both sides verify the peer
  executable path (`07_IPC.md`).
- **Consequences:** a protocol break bumps the pipe name; no gRPC dependency; the Agent never executes a path it
  receives — it only classifies and reads.

## ADR-8 — TraceEvent as the only ETW consumer

- **Context:** alternatives are raw `ProcessTrace` P/Invoke (weeks of work) or third-party wrappers with unclear
  maintenance. TraceEvent (PerfView's library, MIT) handles kernel sessions, rundown, real-time loss counters.
- **Decision:** `Microsoft.Diagnostics.Tracing.TraceEvent` 3.2.x; two named sessions (`AppLedger-Kernel`,
  `AppLedger-User`), FileIO keyword toggled for sampling windows (`05_COLLECTOR.md`).
- **Consequences:** native helper DLLs ship with the app (x64/ARM64 verified in S1); no `TraceLog`/symbol features used.

## ADR-9 — Privacy defaults are product decisions

- **Context:** six months of per-app hostnames is a browsing history; local-only storage is necessary but not sufficient.
- **Decision:** Browser and System categories store bytes only; other apps store eTLD+1 with a per-day cap; command
  lines redacted in logs; own-session only; a Privacy Gate on first run; one-click purge; exhaustive list of network calls
  (`12_PRIVACY_AND_RETENTION.md`). Defaults can be relaxed per app by the user, never by us.
- **Consequences:** some questions ("which site did Chrome talk to?") are intentionally unanswerable by default.

## ADR-10 — No Windows Service in v1

- **Context:** see `01 §Why not a Windows Service`.
- **Decision:** Scheduled Task at logon; service is a v2 design with a machine-wide installer.
- **Consequences:** nothing collected pre-logon; task deletion degrades to Lite mode, not to a broken app.

## ADR-11 — Read-only product

- **Context:** the user's decision (session of 2026-08-22): "the app only displays data".
- **Decision:** no kill/uninstall/clean/block/edit verbs anywhere; `BannedSymbols.txt` enforces the obvious ones;
  `23_NON_GOALS.md` lists the rest.
- **Consequences:** the privilege boundary stays auditable; feature requests for actions are answered with links to
  the right Windows tool.

## ADR-12 — Catalog rules are signed data (minisign/Ed25519), verified with NSec, strict-parsed

- **Context:** identity and policy rules must update without releases, but an elevated Agent must never load tampered
  or typo'd rules.
- **Decision:** `appledger-catalog.json` + detached `.minisig`; public key embedded; unknown fields reject the file;
  never downgrade; the bundled copy is verified too (`13_CATALOG_RULES.md`).
- **Consequences:** a release secret (`CATALOG_MINISIGN_KEY`) exists in CI; key rotation needs an app update first.

## ADR-13 — Packet/flow capture deferred; pktmon ETW is the only candidate

- **Context:** no public pktmon API; Npcap OEM redistribution terms; payload = privacy hazard; 5-tuple → PID races.
- **Decision:** v1 has no packet mode (NG-4). v2 may add header-only flows + SNI via `Microsoft-Windows-PktMon` if S6
  passes; payload is never retained.
- **Consequences:** hostnames for apps with private resolvers are IP-only in v1 (documented on the number).

## ADR-14 — Offline GeoIP (DB-IP Lite), opt-in download, display-time only

- **Context:** country/ASN per remote IP is useful context; MaxMind GeoLite2 needs an account/EULA; online lookups would
  leak the user's hosts.
- **Decision:** DB-IP Lite (CC BY 4.0) mmdb as a release asset the user can download; `MaxMind.Db` reader; attribution
  shown in the UI; nothing geo-related is stored (`10_NETWORK_AND_DNS.md`).
- **Consequences:** monthly DB refresh is a release chore; no geo data without the opt-in.

## ADR-15 — Data root separate from the install root

- **Context:** Velopack deletes its install folder on uninstall; users expect to choose whether history survives.
- **Decision:** install `%LOCALAPPDATA%\AppLedger\` (Velopack), data `%LOCALAPPDATA%\AppLedgerData\` (ours); the
  uninstall dialog offers keep/delete (`16_PACKAGING_AND_UPDATES.md`).
- **Consequences:** two folders to document in Privacy; purge covers the data root only.

## ADR-16 — Every project builds x64/ARM64; AnyCPU is not a supported configuration

- **Context:** `docs/23_NON_GOALS.md` NG-11 rules out x86 because WOW64 path redirection makes the Tier-0 path policy
  unreliable. At kickoff the solution still built as AnyCPU, and CsWin32 refused to generate `SHGetFileInfo` and
  `DnsQueryEx` (`PInvoke005`) because they have architecture-specific shapes.
- **Decision:** every project in `AppLedger.slnx` declares `<Platforms>x64;ARM64</Platforms>`, and each `<Project>`
  carries explicit `<Platform Solution="*|…" Project="…" />` mappings — without them the solution pins every project
  to AnyCPU regardless of its own `<Platforms>`. The solution keeps an **"Any CPU" solution platform that maps every
  project to x64**: MSBuild chooses the default solution platform as "Any CPU" when present and otherwise the
  alphabetically first one, which would be ARM64 — so a bare `dotnet build AppLedger.slnx` on an x64 box (and on CI,
  which passes no platform) would build ARM64 and abort the test run with "Could not find 'dotnet.exe' host for the
  'ARM64' architecture". `Directory.Build.props` additionally defaults `Platform` to x64 so a single-project
  `dotnet build` works. Core and Ipc are architecture-tagged too even though they contain no Windows code — one
  platform matrix, no split output layout.
- **Consequences:** output paths gain a platform segment (`bin/x64/Release/…`); the default build and CI produce x64;
  ARM64 is `dotnet build AppLedger.slnx -p:Platform=ARM64` and the separate `win-arm64` Velopack channel
  (`16_PACKAGING_AND_UPDATES.md`); the "Any CPU" solution platform is a naming alias only — no project ever targets
  AnyCPU. A new project that forgets its `Platforms` or its solution mapping falls back to AnyCPU and breaks the
  Infrastructure build loudly, which is the point.

## Findings (append as discovered)

| Date | Finding | Doc fixed |
|---|---|---|
| 2026-08-23 | MSBuild/XML forbid `--` inside a comment. The Agent `.csproj` and `app.manifest` documented CLI switches (`--status`, `--install-task`) inside comments, so neither file could be loaded at all. | csproj + manifest reworded; guard test in `Core.Tests` |
| 2026-08-23 | `.slnx` pins every project to `AnyCPU` unless each `<Project>` carries an explicit `<Platform Solution="*|x64" Project="x64" />` mapping — a project-level `<Platforms>` is not enough. With AnyCPU, CsWin32 fails `SHGetFileInfo` and `DnsQueryEx` with `PInvoke005`. | `AppLedger.slnx`, `Directory.Build.props`, ADR-16 |
| 2026-08-23 | With no "Any CPU" solution platform, MSBuild defaults to the alphabetically first one — ARM64 — so the plain `dotnet build`/`dotnet test` on an x64 box built ARM64 and aborted the test run. Fixed by keeping an "Any CPU" solution platform mapped to project x64. | `AppLedger.slnx`, ADR-16 |
| 2026-08-23 | `Microsoft.CodeAnalysis.BannedApiAnalyzers 3.11.0` has no stable release on nuget.org (prerelease only); restore silently resolved 4.14.0 and `TreatWarningsAsErrors` turned NU1603 into a build failure. Every other pin was re-verified and does exist. | `Directory.Packages.props` pinned to 4.14.0 |
| 2026-08-23 | `Microsoft.Data.Sqlite` 10.0.9 resolves `SQLitePCLRaw.lib.e_sqlite3` 2.1.11, which carries GHSA-2m69-gcr7-jv3q (high). The `docs/18` vulnerability gate would have failed on day one. | transitive pins to 2.1.13 in `Directory.Packages.props` |
| 2026-08-23 | `COMMIT` is a SQLite reserved keyword, so `metrics_1m.commit` would not parse unquoted. | `docs/06` column renamed `commit_bytes` |
| 2026-08-23 | Catalog `host_rules` fields are OR-ed, so `{exe: dllhost.exe, cmdline_contains: /Processid:}` matched every process with that switch; and `conhost.exe` appeared in both `system` and `attach_parent`, with `system` running first — which would have broken S2 fixture 4. | catalog seed, `docs/03`, `docs/13` |
| 2026-08-23 | `apps[].match` semantics were ambiguous. AND-across-kinds is the only reading consistent with S2 fixture 7 (portable 7-Zip must fall through to `root:`). | `docs/13` matching-semantics section |
| 2026-08-23 | Catalog glob `"*\\Steam"` is not rooted, violating `docs/13`'s own strict-parse rule, so the shipped seed would fail its schema test. | `?:\` drive-wildcard token defined in `docs/13`; catalog updated |
| 2026-08-23 | `.github/ISSUE_TEMPLATE/config.yml` used `url: {{REPO_URL}}/...`; an unquoted `{` opens a YAML flow mapping, making the whole file invalid for GitHub. | `config.yml` rewritten with quoted URLs |
| 2026-08-23 | `.editorconfig` sets `end_of_line = lf` but the repo had no `.gitattributes`, so a Windows checkout produced CRLF and `dotnet format --verify-no-changes` could never pass in CI. | `.gitattributes` added |
| 2026-08-23 | TraceEvent 3.2.4 has no `TraceEventSession.BufferQuantumSize`; the property is `BufferQuantumKB`. Found by compiling the S1-lite harness against the code sample in `docs/05`, which is exactly what a pre-flight spike is for. | `docs/05` session snippet |
| 2026-08-23 | Dependabot does not regenerate every `packages.lock.json` under Central Package Management: it refreshed 3 of the 9 lock files touching the analyzer bump and 2 of the 4 touching coverlet, so `--locked-mode` restore fails with NU1004 on the rest and no Dependabot NuGet PR can pass CI unaided. A force-evaluate restore on the branch is now part of the documented flow. | `docs/18` Dependabot |
| 2026-08-23 | The CI stubs pointed at reusable workflows in `poli0981/.github` that mostly do not exist: `reusable-dotnet-desktop.yml`, `reusable-dotnet-release.yml` and `reusable-notify.yml` all 404 (the real one is `reusable-desktop-csharp.yml`), our `ci.yml` passed four inputs it does not declare, `codeql.yml` passed `languages: csharp` where a JSON array string is required, and that CodeQL job runs on `ubuntu-latest`, which cannot build the `net10.0-windows` projects. All four workflows are now self-contained in this repo. | `.github/workflows/*`, `docs/18` rewritten |
| 2026-08-23 | S1-lite PASSED, but the budget that binds is **RAM, not CPU**: ~75 MB is the floor for two ETW sessions with counting-only handlers, against a 100 MB budget, while CPU came in ~30x under. `06_DATA_MODEL.md` sets the Agent SQLite `cache_size` to 32 MB, which alone would breach it. The CPU figure is also a floor: TraceEvent parses payloads lazily and the harness reads no fields. | `docs/20` S1-lite Result; `docs/05` Budget controls; `docs/06` Pragmas |
| 2026-08-23 | M0 as written cannot run first: S1 hosts `AppLedger.Collector` (v0.2) and S2 needs `IdentityResolver` (listed at v0.3). | `docs/20` split into S1-lite and S1; `docs/21` M0 row |
| 2026-08-27 | `NativeMethods.txt` listed `GetFinalPathNameByHandle` but not `CreateFile`, and no BCL API opens a **directory** handle: `File.OpenHandle` refuses one and `FILE_FLAG_BACKUP_SEMANTICS` is exposed nowhere in the framework. Canonicalization step 3 is unimplementable without it. | `NativeMethods.txt`, with a comment naming the single reason |
| 2026-08-27 | The Tier-1 root list needs `%USERPROFILE%` (`.ssh`, `.gnupg`) and the catalog glob grammar allows `%PUBLIC%`, but `FOLDERID_Profile` and `FOLDERID_Public` were missing from the generation list, leaving no non-hard-coded way to resolve either. | `NativeMethods.txt` |
| 2026-08-27 | CsWin32 0.3.298 does not mark `QueryDosDeviceW` with `SetLastError`, so `ERROR_INSUFFICIENT_BUFFER` cannot be told apart from "that drive letter is not in use". The device-path mapper uses one generous buffer and treats zero as absent, instead of a retry loop reading an error that was never captured. | `Infrastructure/Platform/DevicePathMapper.cs` comment |
| 2026-08-27 | xUnit 2.9.3 has no `Assert.Skip` - dynamic skipping arrived in v3 - so a smoke test whose precondition is a machine capability has to become a conditional `FactAttribute` subclass that sets `Skip` at construction. | `docs/11` Tests; `Infrastructure.Tests/TestSupport/Capabilities.cs` |
| 2026-08-27 | `C:\WINDOW~1\SYSTEM~1` from `docs/11` Tests **cannot exist on any machine**: `Windows` (7 chars) and `System32` (8 chars) are already 8.3-legal, so neither gets a `~1` form. `Directory.Exists` on it answers for whatever unrelated directory happens to own that alias, which is why the case passed as skipped on one box and failed on CI. The test now asks `GetShortPathNameW` for a real short/long pair under `%SystemRoot%` instead of inventing one. | `docs/11` Tests; `Infrastructure.Tests/TestSupport/Capabilities.cs` |
| 2026-08-27 | `docs/11` step 3 said an unresolvable path is "Tier 0 if lexically under a Tier-0 root, Tier 3 otherwise", which would **downgrade a Tier-1 path we merely failed to open** and let its name into outputs. Classifying the lexical form through the full table agrees for Tier 0 and is strictly safer for Tier 1. | `docs/11` Canonicalization step 3 |
| 2026-08-27 | Authenticode verification through `WinVerifyTrust(WTD_CHOICE_FILE)` sees **embedded** signatures only, so a non-Tier-0 file signed through a Windows security catalog reports `Unsigned`. `CatalogSigned` is reachable only via the Tier-0 short-circuit in v0.1; the `CryptCATAdmin*` hash lookup that would close it is deferred to v0.3, where the status is first displayed. | `docs/03` Metadata enrichment |
| 2026-08-27 | `X509Certificate.CreateFromSignedFile` is obsolete (SYSLIB0057) and `X509CertificateLoader`, its named replacement, reads certificate *files* rather than signed PEs - there is no non-obsolete managed way to reach a PE's embedded certificate. It is used as an extractor only, with a scoped suppression, and the bytes are parsed through the modern loader. | `Infrastructure/Metadata/AuthenticodeReader.cs` |
| 2026-08-27 | The new `AppLedger.Infrastructure.Process` namespace shadows the `System.Diagnostics.Process` **type** inside `AppLedger.Infrastructure.Tests.*`: C# resolves a namespace from an enclosing scope ahead of a type imported by a using directive, so `Process.Start` stops compiling. Test helpers name the type in full. | `Infrastructure.Tests/TestSupport/Junctions.cs` comment |
| 2026-08-27 | Pragma order is load-bearing: SQLite only accepts a change out of `auto_vacuum=none` while the database is still new, and setting `journal_mode=WAL` first writes the header. With the documented order the pragma silently stayed at `none`, so the nightly `incremental_vacuum` of `docs/06` would have reclaimed nothing for six months. `auto_vacuum` now runs first. | `Infrastructure/Storage/SqliteConnectionFactory.cs` comment |
| 2026-08-27 | `{{CATALOG_PUBKEY}}` being unresolved is not a cosmetic gap: with no embedded key there is nothing to verify against, so `CatalogLoader.TryCreateFromEmbeddedKey` returns null and the Agent runs on the built-in policy minimum with no catalog rules at all until a release key exists. Failing closed is the only safe reading of `docs/01` Degraded modes. | `docs/13` Signing and verification |
| 2026-08-27 | Passing a process handle to `NtQueryInformationProcess` as a raw `nint` from `SafeHandle.DangerousGetHandle` is a use-after-free waiting to happen: once that expression returns, the JIT may treat the owning variable as dead, and the finalizer can close the handle while ntdll is still reading through it. Nothing observable fails until it does, rarely. The P/Invoke now takes a `SafeHandle` so the generated marshalling holds the reference. | `Infrastructure/Ntdll/NtDll.cs` comment |
| 2026-08-27 | `docs/05` sized the live ring as "3600 x apps; ~2 MB for 100 apps", and those two halves disagree by a factor of thirty: a measured `AppSample` is **184 bytes**, so an hour of 100 apps is **66 MB** - a third of the whole Agent budget, against the ~20 MB S1-lite left for every collector structure combined. The 2 MB figure is the accurate half and corresponds to about a minute, which is also all the UI asks for (`docs/08`: 60-second sparklines; the History page's 1 h range auto-picks the `metrics_1m` tier). Ring set to 5 minutes, and a test now pins the struct size so the budget cannot slip silently. | `docs/05` Accumulators and Budget, `docs/01` pipeline diagram |
| 2026-08-27 | TraceEvent 3.2.4 spells the IPv6 UDP payload type **`UpdIpV6TraceData`** - a transposition in the library itself, not in our code. Writing the obvious `UdpIpV6TraceData` fails to compile, which is the harmless outcome; the point of recording it is that the API was read by reflecting over the assembly rather than from memory, the same way S1-lite found `BufferQuantumKB`. | `Infrastructure/Etw/EtwHub.cs` comment |
| 2026-08-27 | Kernel `TcpIp*`/`UdpIp*` payloads expose their fields in **lower case** (`size`, `saddr`, `daddr`, `sport`, `dport`, `connid`) while `DiskIOTraceData` uses PascalCase (`TransferSize`, `DiskNumber`). Neither appears in the package's XML documentation, so both were confirmed by reflection before any handler was written. | `Infrastructure/Etw/EtwHub.cs` |
