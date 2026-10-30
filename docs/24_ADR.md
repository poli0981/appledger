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

## ADR-17 — The pipe is secured by an explicit DACL plus a Medium integrity label, not by `CurrentUserOnly`

- **Context:** ADR-7 specified "DACL to the owning user + Administrators, integrity label Medium, both sides verify
  the peer executable path". `07_IPC.md` and `11_SAFETY_POLICY.md` had since been rewritten around
  `PipeOptions.CurrentUserOnly`, described there as "sufficient and simpler than a hand-built DACL". The two are not
  two wordings of one decision — they are mutually exclusive, since `CurrentUserOnly` grants the current user alone
  and so cannot carry an Administrators ACE.
- **Decision:** ADR-7 stands. The Agent creates the pipe with `NamedPipeServerStreamAcl.Create` and an explicit
  `PipeSecurity`: `FullControl` for the user SID and `BUILTIN\Administrators`, plus a mandatory label of **Medium**
  integrity. The client connects without `CurrentUserOnly`. Squatting is defended against by verifying the peer's
  canonical image path in both directions (`GetNamedPipeClientProcessId` / `GetNamedPipeServerProcessId` +
  `QueryFullProcessImageName`), which `NativeMethods.txt` has listed since v0.1 under "pipes (peer verification)".
- **Why `CurrentUserOnly` is not merely redundant but wrong here.** Two independent reasons, both specific to a
  High-IL server talking to a Medium-IL client:
  1. **Owner mismatch.** An elevated token's default owner is `BUILTIN\Administrators`, so the pipe an elevated
     process creates is owned by that group. The client-side half of `CurrentUserOnly` compares the pipe's owner
     with `WindowsIdentity.GetCurrent().Owner`, which for the Medium-IL UI is the user SID. They differ, and the
     connect fails.
  2. **No mandatory label.** An object created by a High-IL process carries a High label, and the no-write-up
     policy denies a Medium-IL process the write access that connecting to a pipe requires. `CurrentUserOnly` sets
     a DACL; it does not touch the SACL.
- **Consequences:** the pipe is created by `CreateNamedPipeW` with a descriptor built from SDDL, **not** by
  `NamedPipeServerStreamAcl.Create` — the managed `PipeSecurity` model cannot carry a mandatory label at all
  (§Findings, 2026-08-28), so the API that looks like the right one produces exactly the failure this ADR exists
  to prevent. The P/Invoke and the peer check live in `AppLedger.Infrastructure`; `Ipc` targets `net10.0` and only
  ever sees a connected `Stream`.
- **Amended 2026-08-28 by measurement.** Writing a label at or below your own integrity needs no privilege, so the
  descriptor is provable unelevated and most of it is a CI test rather than an `Category=Admin` one. What remains
  admin-only is that an *elevated* process applies it; and what remains manual is the connect that actually
  matters — a Medium-IL client to a High-IL server — because one test process cannot be both integrity levels
  (`19_TESTING.md` §Pipe security, `tests/MANUAL_CHECKLIST.md`).

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
| 2026-08-27 | `TraceEventSession.Source.Process()` throws whenever the session ends underneath it - `logman stop`, another instance reclaiming the name, sleep mid-buffer - and it runs on a background thread, where an exception is unhandled **by definition**. The first real run of the `Category=Admin` suite did not fail a test, it **killed the test host**. The loop is now guarded the same way handlers already were (`docs/05` Failure handling), and `Stop` calls `StopProcessing` before `Dispose` so the ordinary path produces no exception at all. | `Infrastructure/Etw/EtwHub.cs` |
| 2026-08-27 | The ETW guard above was only half a fix. `Process()` also **returns normally** when the session is stopped cleanly - `logman stop`, another instance reclaiming the name - so catching only the throw left the hub reporting `Running` forever while collecting nothing. An Agent that looks healthy and silently records no network or disk bytes is worse than one that crashes, because a crash is visible. Any exit from the loop now marks the sensor unavailable, and a generation counter tells our own shutdown apart from a session dying under us. | `Infrastructure/Etw/EtwHub.cs` |
| 2026-08-28 | `NetAccumulator` pre-sizes **two** dictionaries to `MaxEndpointsPerApp` (2 000) in its field initializers, and one accumulator exists per network-active `(pid, createTime)`. That is roughly **250 KB per instance** committed on the first packet, on a TraceEvent thread - about 75 MB for 300 active processes, against the ~20 MB S1-lite leaves for the entire collector. Nothing constructs a `NetAccumulator` in production yet, so the cost has never been paid; wiring the sensors into the snapshot is precisely what starts paying it. Capacities dropped to the default and grown on demand; the 2 000 cap stays enforced where it belongs, in the add path. | `Collector/Accumulators/NetAccumulator.cs`, `docs/05` Budget |
| 2026-08-28 | `EventsLost` can go **down**. It is the sum of two sessions' counters and a session that restarts starts again from zero, so `Degraded` cannot be derived from "value differs from last time" - only an *increase* is loss, and a decrease must re-baseline silently. Deriving it from inequality would hatch every chart after any session restart. | `docs/05` Failure handling |
| 2026-08-28 | Carrying the 2-second GPU reading forward onto the off-second is **unbiased** in the minute rollup, not double-counted: `Rollup.FromSamples` divides by `samples.Count`, so 30 readings each appearing twice give `2 x sum / 60`, which is the mean of the readings. Zeroing the off-second instead yields exactly **half**. The intuition that carry-forward inflates the average is wrong, and it is the intuition that would have picked the biased option. | `docs/05` Accumulators |
| 2026-08-28 | A JSON parser over peer bytes throws in two separate ways, and only one of them looks like a parse error. `Utf8JsonReader.Read()` raises `JsonException` for unparseable input, but the `TryGet*` family raises **`InvalidOperationException`** when the token is merely of another kind - so `TryGetInt64` on `"id":"three"` throws, despite the name promising otherwise. A `Try`-shaped wrapper that catches only `JsonException` still dies on the first frame any same-user process cares to malform. Both parsers now catch both, and a fuzz test over truncations and single-byte corruptions of a valid frame is what found it - reading the code had not. | `Ipc/IpcEnvelope.cs`, `Ipc/Streams/AppsTick.cs` |
| 2026-08-28 | **`PipeSecurity` cannot carry a mandatory integrity label, and says nothing when it drops one.** The managed `ObjectSecurity` model maps a SACL onto *audit rules*, and an `ML` ace is not an audit ace - so `SetSecurityDescriptorSddlForm("D:(...)S:(ML;;NW;;;ME)")` returns normally, keeps the DACL, and silently discards the label; reading the SACL back gives an empty `S:`. `NamedPipeServerStreamAcl.Create` with that descriptor then fails outright with "a required privilege is not held", because an empty SACL still asks for `SE_SECURITY_NAME`. Had it succeeded, the pipe would have been created unlabelled, kept the elevated Agent's High integrity, and refused the unelevated UI - the exact failure ADR-17 exists to prevent, reached through the API meant to prevent it. Building the descriptor with `ConvertStringSecurityDescriptorToSecurityDescriptorW` and passing it to `CreateNamedPipeW` keeps the label, needs no privilege, and is atomic; a probe confirmed the created pipe reads back `S:AI(ML;;NW;;;ME)` **unelevated**. | `Infrastructure/Ipc/NamedPipeServerFactory.cs`, `docs/07` Transport, ADR-17 |
| 2026-08-28 | **XAML's `{x:Static}` resolves through public reflection.** An `internal` strongly-typed resource class compiles, links, and starts - and then throws `XamlParseException` on the first window: "StaticExtension value cannot be resolved to an enumeration, static field, or static property". Nothing in the build says so; only launching the app does. `Strings` is public, and a test asserts the visibility rather than the next person rediscovering it. | `docs/14`, `tools/gen-strings.py` |
| 2026-08-28 | **The SDK's strongly-typed resource generation is not usable on a clean build.** With `Generator=MSBuild:Compile` and `StronglyTypedFileName` under `obj`, `GenerateResource` writes the file during `PrepareResources` - after the `Compile` item group was evaluated - so a clean build produces it and never compiles it, and every reference to the class fails to resolve. The *second* build succeeds, because by then the file exists when globs run. That is the "green locally, red on CI" shape exactly, since CI always builds clean; pointing the output at the project directory instead produced no file at all. The resx files and the accessor are now generated by `tools/gen-strings.py` and committed, which has no build ordering in it. | `AppLedger.App.csproj`, `docs/14` |
| 2026-10-30 | **The ARM64 CI leg restored for one platform and built for another, and a dev box cannot reproduce it.** `ci.yml` ran `dotnet restore --locked-mode` with no platform and then `dotnet build -p:Platform=ARM64 --no-restore`; the ARM64 build began failing with `MSB3030 ... apphost.exe ... was not found` for both exe projects while x64 stayed green. It does not reproduce locally, and the reason is the interesting part: this dev box carries `Microsoft.NETCore.App.Host.win-arm64/10.0.14` in the SDK's own `packs/` directory (from the full installer), so the ARM64 apphost is found there no matter what restore did, while a `setup-dotnet` runner installs only the host's own `win-x64` pack. The fix is to put `-p:Platform=ARM64` on the restore as well, so the restored graph matches the build. Lock files are not involved: no `packages.lock.json` in the repo names a `Host.win*` entry and an x64 `project.assets.json` names none either, which is why `--locked-mode` tolerates the extra property. | `ci.yml` ARM64 restore; `docs/18` ARM64 gate |
| 2026-10-30 | **CsWin32 0.3.298 to 0.3.367 is source-breaking in generated interop, and the break is an improvement.** `SHGetKnownFolderPath` now yields a `CoTaskMemFreePWSTRSafeHandle` instead of a `PWSTR`, and `GetTokenInformation` lost its four-argument overload: three call sites across `KnownFolders` and `ProcessEnricher`, the two files where a wrong P/Invoke yields a plausible number rather than an error. Worth taking on its own - the SafeHandle removes a manual `Marshal.FreeCoTaskMem` and the `unsafe` block around it, which is also one of the 23 S6640 findings - but the token reads can only be proven by the elevated `Category=Admin` round trip, so it is a migration rather than a version sweep. | pin held at 0.3.298 with the reason in `Directory.Packages.props`; own PR |
| 2026-10-30 | **The four `SQLitePCLRaw` transitive pins had become no-ops and were a standing hazard.** `Microsoft.Data.Sqlite` 10.0.14 depends on `bundle_e_sqlite3` and `core` 2.1.13 directly, so the pins that lifted GHSA-2m69-gcr7-jv3q off 10.0.9 no longer changed resolution - while still having to be kept in lockstep with the provider by hand, against a 3.x line that a lone bump would have broken. Removed, and verified rather than assumed: resolution still lands on 2.1.13 for all four and the vulnerability gate reports nothing. | `Directory.Packages.props`; `docs/18` Dependabot |
| 2026-10-30 | **A single-instance `Mutex` acquired before an `await` and released in the `finally` throws on every clean shutdown.** `Program.ServeAsync` took the mutex with `WaitOne` on the entry thread and released it in a `finally` that runs after two `ConfigureAwait(false)` awaits, so the release happened on a thread-pool thread. `Mutex` ownership is thread-affine, so `ReleaseMutex` raised `ApplicationException` out of the `finally`, replacing the `return 0` - every orderly `--serve` stop would have ended as an unhandled exception with a non-zero exit code, which Task Scheduler records as a failed task. Found by SonarCloud S2222, whose literal complaint ("unlock along all execution paths") was wrong: it *was* released on every path it was taken on, and that was the bug. Now the guard never acquires anything - it asks `CreateMutex` whether it created the named object, which is atomic, thread-agnostic and self-healing after a crash. | `Agent/Program.cs`; `SingleInstanceGuardTests` pins the thread affinity and the `createdNew` semantics |
| 2026-10-30 | **`ProcessStartInfo.FileName = "schtasks.exe"` lets the calling executable's own directory win.** With `UseShellExecute = false` the name goes to `CreateProcess`, whose search order begins with the directory of the calling executable - and the Agent runs elevated from the Velopack `current\` folder, which `docs/11` already records as user-writable. A `schtasks.exe` dropped beside `AppLedger.Agent.exe` would have been preferred over the real one. Resolved from `Environment.SpecialFolder.System` instead of `%SystemRoot%` so a poisoned environment block cannot move it either. Defence in depth rather than a privilege boundary, since UAC is not one. | `Agent/Tasks/SchTasks.cs`; `docs/11` §Privilege boundary; asserted by a test |
| 2026-10-30 | **`GpuPoller.QueryHandle` borrows a raw PDH handle across a call the owner can close from another thread — left open on purpose.** It returns `new PDH_HQUERY(_query!.DangerousGetHandle())` and three call sites pass that to PDH entry points, while `Stop()` disposes `_query`. Two of the `Stop()` callers are on the sampling path and therefore same-thread, but `StopAsync` and `Dispose` are the host's shutdown path and run on another thread, so the window is structurally reachable: the P/Invoke can be handed a closed, possibly recycled handle. The fix is `DangerousAddRef`/`DangerousRelease` around each of the three uses, or a lock shared with `Stop`. **Fixed 2026-10-30 with a lock**, not with refcounting: `DangerousAddRef` would have closed the use-after-close hole only, and would have let `Dispose` return while a PDH call was still in flight. One `Lock` now covers every use of the raw handle, and the handle accessor takes the query as a parameter instead of reading the field through `_query!` - a null-forgiving operator on a field another thread can clear. That second half was a **reachable NullReferenceException** rather than a theoretical hazard: the null check and the call that used the handle were separate statements, so a shutdown landing between them faulted the collector tick. **The fix is established by construction and review rather than by a test, and that was checked rather than assumed:** the concurrency test written for it passes five times out of five against the pre-lock code, because the window is two instructions wide against a read that takes milliseconds and there is exactly one chance per run - repetition does not help a window that narrow. The test is kept for the contract either side of the race (concurrent sample and dispose must not fault, a disposed poller must answer rather than throw) and says in its own remarks that it does not reproduce the race. Cost in `docs/05` §Budget. | `GpuPoller`; `docs/05` §Budget; the sibling in `NamedPipeServerFactory` stays suppressed |
| 2026-10-30 | **The S2 fixtures carried seven hand-typed hashes that were hashes of nothing, so the gate could not pass.** Of 55 expected ids, 45 were real and 7 were placeholder hex in the two hashed prefixes - `C:\Work\scraper.py` hashes to `745d3e9b722dbfba`, while the fixture said `1f2e3d4c5b6a7988`. Un-skipping the scoring test would have topped out at 87 % against a 95 % gate, failing for a reason that was not the resolver's. Writing the real hashes in would have been worse: the fixtures' own README says they were authored before the resolver because "a gate whose test data is authored after the implementation measures the implementation against itself". Fixed by letting a hash-bearing expectation name its **input** instead - `root:<D:\Tools>`, `script:<C:\Work\build.ps1>` - so the claim stays human-authored and human-checkable while `AppId` does the hashing it is already tested for. | fixture schema + `fixtures/README.md`; 7 expectations across 4 files |
| 2026-10-30 | **docs/03's pipeline put the Tier-0 check first and said "stop"; two fixtures say otherwise.** `conhost.exe` lives in `System32` and must still join the script its parent runs (fixture 4 says so in as many words), and fixtures 4 and 10 both expect `msix:` for images under `%ProgramFiles%\WindowsApps` - a root that is Tier 0 so that nothing *scans* it, not because a packaged app is part of Windows. Host rules and the identity sources now run first and a Tier-0 path is the last resort, which is still where plain `notepad.exe` lands. | `docs/03` §Resolution pipeline; `IdentityResolver` |
| 2026-10-30 | **A catalog rule matching only on `package_family` must not mint an id.** docs/03's precedence table puts catalog above msix, and the shipped catalog identifies Windows Terminal and the Xbox app by package family alone - so by the table, fixture 4's Windows Terminal would be `cat:windows-terminal`, while the fixture and docs/03's own fixture list both say `msix:...Terminal`. Resolved by reading such a rule as metadata rather than identity: it cannot state anything `msix:<PFN>` does not, and it is still kept as the *matched* app so its category and `helpers[]` apply - which is what groups `OpenConsole.exe` and what lets `launcher_children` recognise the Xbox app as a Launcher. | `docs/03` §Resolution pipeline; `IdentityResolver` |
| 2026-10-30 | **Tier-2 promotion of a protected game cannot be a pure function of its path, and is not built.** `docs/11` promotes "the game process itself and its children while such a driver/service is present" every 30 s, but a game's own executable carries no anti-cheat name and sits in no anti-cheat directory, and its EAC helper starts *after* it - at the moment the game is first classified there is nothing to see. `ProcessTierTable` answers the static half (helper names, anti-cheat directories) and the anti-cheat helpers are Tier 2 today. S2 fixture 3 marks the gap in the data with `tierNeedsPromotion`, and a test checks the flag is earned by a driver or service actually being in that fixture, so it cannot excuse an unrelated tier miss. | `fixtures/03_steam_game_anticheat.json`; `docs/03` §What ships; promotion loop still owed |
| 2026-10-30 | **Adoption merged a Steam game into Steam, and the S2 gate could not see it.** Adoption rule 2 reads "child image path is under the parent's install root", and a Steam game lives *inside* the Steam folder - so the rule is true of a game as well as of a helper, and `ProtectedGame.exe` was adopted into `cat:steam`. The gate scored 100 % anyway, because with the launcher manifest index populated a game has an identity of its own and never reaches adoption at all; the merge only appears at the fidelity the Agent actually ships with, where the indexes are empty. Fixed by testing the child's *own* install root against the parent's rather than containment: `steamapps\common` is already a boundary for the install-root heuristic, so the game's root is its own directory while `steamwebhelper.exe`, four levels down in `bin\cef\`, still shares Steam's. The no-game-into-launcher test now runs at both fidelities, since an invariant that only holds when an index is populated is not an invariant. | `IdentityResolver.Adopt`; `Resolve_NeverMergesAGameIntoItsLauncher`; `Resolve_WithEmptyIndexes_...` |
| 2026-10-30 | **The collector never passed the parent, so adoption and `attach_parent` could not fire at all.** `ProcessLifecycleEvent` has carried `ParentPid` since the collector was written, but `InstanceRegistry.Add` left `ProcessFacts.Parent` null - so every Chromium child, crash handler and `conhost` resolved independently however good the resolver was. Fixed by turning the pid into a `(pid, createTime)` key against the live instances, which is also where the PID-reuse guard belongs: a recycled pid is held by a process that is not older than the child, and `ProcessKey.CouldBeParentOf` rejects it there as well as in the resolver. | `InstanceRegistry.ParentKeyOf`; `InstanceRegistryTests` |
| 2026-10-30 | **Starts were resolved in snapshot order, which makes adoption fail even once the parent is passed.** Adoption reads the parent's answer out of the resolver's cache, so a child resolved first cannot be adopted - and `NtQuerySystemInformation` returns no particular order, while on the first tick and after every rebaseline *the whole process tree* arrives as starts at once. `InstanceRegistry.Apply` now orders by create time. Two bugs that each hide the other: fixing either alone would have changed nothing visible. | `InstanceRegistry.Apply`; `InstanceRegistryTests` |
| 2026-10-30 | **`InstanceRegistry.InvalidateResolutions` rebuilt its facts from the resolution, which the new resolver turns into a grouping wipe.** It passed the app's *display name* as the image file name and the *install root* as the image path - invisible with `FallbackIdentityResolver`, which reads only those two, and destructive with a resolver that matches on executable names, command lines and parents: every live instance would re-resolve to a `root:` hash. The method had no callers, so nothing was broken yet; it was a trap for its first one. Now each instance retains the `ProcessFacts` it was first resolved with and is re-resolved from those, in create-time order so the parent chain is rebuilt before any child asks for it. Cost: roughly 1 KB of retained strings per instance, a few hundred KB at a few hundred processes, against the budget in `docs/05`. | `LiveInstance.Facts`; `InstanceRegistryTests` |
| 2026-10-30 | **No signer reaches the resolver, so twelve of the seventeen catalog app rules cannot match.** `InstanceRegistry` builds its facts from the process table and one limited handle; Authenticode and PE metadata are read once per `(app_id, version)`, which is after resolution rather than before it. `docs/13` AND-s across match kinds, so a rule naming a signer cannot match without one - and only `itch`, `obs` and `xbox` name none. Chrome therefore resolves to `uninst:google-chrome` at 0.90 instead of `cat:chrome` at 0.95. Lower precedence rather than wrong, and the confidence says so. Pinned by a test (`Resolve_FromCollectorFacts_...`) rather than left as an assumption, because the previous claim this doc made about the Agent was false and only a test found out. Closing it needs a signer source the resolver can consult lazily, cached per image path. | `docs/03` §What ships; open |
| 2026-10-30 | **The identity indexes rebuild on an interval rather than on change notification, deliberately.** `docs/03` §Caching & invalidation asks for `RegNotifyChangeKeyValue` on the Uninstall keys and `ReadDirectoryChangesW` on the manifest directories. The eager build at Agent start is what actually matters: everything already installed is indexed before the first process is resolved, so a refresh only has to notice software installed *while the Agent runs*, and such an app's processes start after the refresh that finds it. Notification would shorten a window that is already rare, for a registry-notification thread and a watcher per launcher directory - both of which the handle and thread budget pay for (`docs/05` §Budget). A refresh also does **not** invalidate live resolutions: `Invalidate()` drops the parent entries adoption reads, so invalidating mid-run would lose grouping rather than improve it. | `IdentityIndexSource`; `docs/03` §What ships |
| 2026-10-30 | **SonarCloud reports 0 % coverage for files the suite plainly covers, and it is not the exclusions.** `InstanceRegistry.cs` reads 0 % with four passing tests against it; `CollectorHost.cs` reads 0 % against 167 Collector tests; `CollectorHealth.cs` and `CollectorOptions.cs` in Core read 0 % while the rest of Core is covered well enough to clear the 80 % gate in `build-test`. Meanwhile `LiteCollector.cs` reads 96.1 %, so the import is not failing wholesale. Narrowed by reproduction rather than left as a suspicion: of the six test projects, **five produce a 1-2 MB OpenCover report and `AppLedger.Collector.Tests` produces an empty 363-byte one** - `<Modules />`, every counter zero - while its 171 tests pass. It survives running that project alone and a clean rebuild of both it and `AppLedger.Collector`, its PDBs are present beside the DLLs in the test output, and no `ExcludeFromCodeCoverage` exists anywhere in the repo. Its csproj differs from a working one only in the target framework, which three other working projects share. So coverlet instruments nothing for that project, including `AppLedger.Core` - which is why Core’s `Collection/` files read 0 % too, since only Collector.Tests covers them. The 80 % gate in `build-test` is unaffected because it reads Cobertura for `AppLedger.Core`, which `Core.Tests` covers. **Left open rather than excluded**: a file that is covered and reported as uncovered is an import bug, and hiding it behind `sonar.coverage.exclusions` would make the one number the gate is built on untrustworthy. The host-wiring exclusions added alongside this are a separate, argued decision about what is worth covering. | open; `docs/18` coverage gate |
