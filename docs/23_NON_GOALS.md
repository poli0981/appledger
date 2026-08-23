# 23 — Non-goals

These are product decisions, not a backlog. Each needs an ADR to reverse. The purpose is to keep AppLedger a
**read-only observer** whose safety story fits in one sentence: *it opens no process beyond query-limited rights,
changes nothing on the system, and keeps everything local.*

| # | Not a goal | Why | What we do instead |
|---|---|---|---|
| NG-1 | Killing, suspending, renicing or setting affinity of processes | One action turns a viewer into a tool that can break a game or a service; the privilege boundary (`11`) is only credible while the Agent has no "do" verbs | Show the data; the user uses Task Manager |
| NG-2 | Uninstalling apps, deleting caches, "cleaning", emptying folders | Deleting inside an elevated Agent is the LPE-shaped risk we designed around; "reclaimable" is a label | Show "reclaimable" with the path; "Open in Explorer" from the non-elevated UI |
| NG-3 | Blocking, shaping or firewalling network traffic | A firewall needs WFP callouts (driver) or rule mutation — both are system changes and a support burden | Show connections/hosts; link to Windows Firewall settings |
| NG-4 | Packet payload capture | Privacy hazard; redistribution limits (Npcap); attribution races | v2 flow/SNI mode only if S6 passes, header-only |
| NG-5 | A kernel driver or any injection/hooking into other processes | Anti-cheat risk, signing cost, attack surface; FrameLedger already covers the one case that needs injection | ETW + system-wide queries (`04_DATA_SOURCES.md`) |
| NG-6 | Evasion of anti-cheat or any detection mechanism | Non-negotiable (`11_SAFETY_POLICY.md` §No evasion) | Zero-touch for Tier-2; document evidence (S7) |
| NG-7 | Cloud sync, accounts, telemetry, crash reporting SDKs | Local-only is the privacy promise; this dataset is a browsing/usage history | Manual bug reports with redacted logs |
| NG-8 | Antivirus-style verdicts ("this app is malicious") | We show facts (signature status, hosts, autostart); verdicts need threat intel we don't have and would be wrong often | Facts + links (hash → user's choice of lookup, opened in the browser) |
| NG-9 | Monitoring other users' sessions by default | Privacy; a shared PC must not turn one user into a monitor of another | Own logon session only; "all sessions" is an explicit, warned admin setting |
| NG-10 | Windows Service in v1 | Per-user Velopack install + LocalSystem binary = LPE smell; separate profile breaks the shared SQLite model | Scheduled Task at logon (`01`); service is a v2 design |
| NG-11 | x86 (32-bit) build | WOW64 path redirection makes Tier-0 path policy unreliable; no meaningful user base | x64 + ARM64 |
| NG-12 | Editing the registry, scheduled tasks, services, firewall rules of other apps | System changes; see NG-1/NG-2 | Read and display (Details tab) |
| NG-13 | Browser history / per-URL visibility | eTLD+1 at most, and browsers default to bytes-only; we are not a parental-control product | Category-based host policy (`12`) |
| NG-14 | Process-level memory inspection (heaps, modules via `VM_READ`, handles via `DuplicateHandle`) | Requires `PROCESS_VM_READ`/`DUP_HANDLE`; anti-cheat and PPL conflicts | ETW ImageLoad for modules; counters from `NtQuerySystemInformation` |
| NG-15 | Replacing Task Manager / Process Explorer / Resource Monitor for live troubleshooting | Those tools are better at that; our value is per-app history and attribution | Link to them from the Processes tab |
| NG-16 | A second implementation language | ADR-3; a spike failure cuts the feature, it does not add a toolchain | C# only |

## Consequences we accept

- Some data is unavailable or approximate (browser-internal DNS, HTTP.sys traffic attributed to `System`, short-lived
  flows unattributed, QUIC identified heuristically). The UI says so on the number (FR-20).
- Users who want actions will ask for them. The answer is a pointer to the right Windows tool, not a feature.
- Not building a service means nothing is collected before logon and nothing on a user session that did not run
  onboarding. That is the intended scope.
