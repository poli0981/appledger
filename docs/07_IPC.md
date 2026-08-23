# 07 — IPC

UI ↔ Agent over a local named pipe. Contracts live in `AppLedger.Ipc` (shared by both processes), serialized with
`System.Text.Json` source generators (`IpcJsonContext`). History is **not** served over the pipe — the UI reads SQLite
directly; the pipe carries live data, commands and health.

## Transport & security

- Name: `\\.\pipe\AppLedger.v1` (`v1` bumps on breaking changes; the UI tries the highest version it knows).
- Server: `NamedPipeServerStream(name, PipeDirection.InOut, maxNumberOfServerInstances: 4, PipeTransmissionMode.Byte,
  PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly)`. `CurrentUserOnly` restricts clients to the same user SID —
  the UI (Medium IL) and Agent (High IL) are the same user, so this is sufficient and simpler than a hand-built DACL.
  Additionally `PIPE_REJECT_REMOTE_CLIENTS` is set (via the `PipeOptions` equivalent / `SetNamedPipeHandleState`) so
  `\\host\pipe\…` access is refused.
- Client: `NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly)`;
  `CurrentUserOnly` on the client also verifies the server's owner is the current user (defends against a rogue process
  squatting the pipe name).
- The Agent never executes anything on behalf of the UI; every request is a read, a sampling hint, a policy change
  that the Agent re-validates, or a self-management command (pause/shutdown/purge). `PolicyGuard` re-checks every path
  field regardless of what the UI already did.

## Framing

`[u32 little-endian length][UTF-8 JSON]`, max 4 MB per frame (larger → `Error(FrameTooLarge)` and disconnect).
Every frame is one envelope:

```json
{ "t": "MessageType", "id": 123, "re": 120, "p": { ...payload... } }
```

`id` = sender-assigned sequence; `re` = id of the request being answered (responses/streams); `p` = typed payload.
Keep-alive: the UI sends `Ping` every 5 s; the Agent answers `Pong` with `serverTimeUtc`; 3 missed pongs → reconnect
with exponential backoff (1 s → 30 s).

## Handshake

```
UI  → Hello        { "protocol": 1, "client": "AppLedger.App 1.0.0", "lang": "vi" }
Agent → HelloAck   { "protocol": 1, "agent": "1.0.0", "mode": "Full|Degraded", "dbPath": "...", "schema": 1,
                     "sensors": { "ProcessPoller": "Ok", "EtwNetwork": "Ok", "EtwDiskIO": "Ok", "EtwFileIO": "Sampled",
                                  "EtwProcess": "Ok", "DnsClient": "Ok", "Gpu": "Ok", "Connections": "Ok", "Usn": "Unavailable:1179" },
                     "catalog": { "version": "2026.08.0", "verified": true }, "startedUtc": 1755820800 }
```

Protocol mismatch → `Error(ProtocolUnsupported)` and the UI shows "Update required".

## Message catalog

### Streams (UI subscribes; Agent pushes until `Unsubscribe`)

| Type | Payload | Cadence |
|---|---|---|
| `Subscribe` | `{ "stream": "apps" }` · `{ "stream": "app", "appId": "…" }` · `{ "stream": "connections", "appId": "…", "estats": true }` · `{ "stream": "health" }` · `{ "stream": "events" }` | — |
| `AppsTick` | compact columns: `{ "ts": 1755820801, "cols": ["appId","procs","cpu","wsPrivate","gpu","diskR","diskW","netIn","netOut"], "rows": [["cat:discord",4,1.2,412345678,0,0,8192,1200,340],…] }` | 1 s |
| `AppTick` | `{ "appId", "ts", "app": { full AppSnapshot }, "procs": [ per-instance snapshot with pid, createTime, cpu, wsPrivate, commit, ioR, ioW, diskR, diskW, netIn, netOut, gpu, threads, handles, hardFaults ] }` | 1 s |
| `ConnectionsTick` | `{ "appId", "ts", "conns": [ { "proto":"tcp","v":4,"lip":"…","lport":…,"rip":"…","rport":443,"state":"Established","dir":"out","pid":1234,"host":"discord.gg","in":…, "out":…, "rttMs":23, "retrans":0, "iface":"wifi","loopback":false } ] }` | 1 s |
| `HealthTick` | `{ "ts", "agentCpuPct", "agentWs", "eventsLost", "ringSeconds", "sensors": {…}, "budgetOk": true }` | 10 s |
| `Event` | `{ "id", "appId", "tsUtc", "kind", "severity", "payload": {…} }` | on occurrence |
| `Unsubscribe` | `{ "stream": "…", "appId": "…" }` | — |

### Requests (request/response)

| Type | Payload | Response |
|---|---|---|
| `GetAppDetail` | `{ "appId" }` | `AppDetail` — identity, evidence summary, enrichment, live instance list, disk summary, sensors applicable |
| `GetInstalledApps` | `{}` | `InstalledApps` — from the resolver's indexes (uninstall, msix, launchers, package managers), not from SQLite |
| `ResolvePath` | `{ "path": "C:\\…\\app.exe" }` | `ResolvedPath` — `PolicyGuard` decision `{ canonical, tier, allowed, reason }` + the resolved identity preview (no scan, no persistence) |
| `ResolveWindow` | `{ "hwnd": 123456 }` | `ResolvedWindow` — `(pid, createTime, appId)` after UWP `CoreWindow` handling |
| `ResolveHost` | `{ "host": "cdn.discordapp.com" }` | `HostRecords` — A/AAAA/CNAME chain/TTL/status/server from `DnsQueryEx` (cached 10 min) |
| `ScanNow` | `{ "appId", "kind": "full|incremental" }` | `ScanAccepted` then `ScanProgress` stream `{ "appId", "phase", "files", "bytes", "pct" }` then `ScanDone` |
| `SamplingHint` | `{ "appId", "disk": true }` | `Ack` (keeps the FileIO window open for 30 s) |
| `OverridesChanged` | `{ "rev": 42 }` | `Ack` (Agent reloads `app_overrides`, re-resolves live instances) |
| `ApplyOverrideToHistory` | `{ "overrideId": 7 }` | `Ack` with `{ "rowsRekeyed": n }` |
| `Pause` / `Resume` | `{ "minutes": 30 }` (optional) | `Ack` with `{ "pausedUntilUtc" }` |
| `Purge` | `{ "scope": "all|app|range", "appId"?, "fromUtc"?, "toUtc"? }` | `PurgeDone` with row counts per table |
| `UpdateCatalog` | `{}` | `CatalogResult` `{ "version", "verified", "error"? }` |
| `GetHealth` | `{}` | `Health` (same shape as `HealthTick`) |
| `Shutdown` | `{ "reason": "update|user" }` | `Ack` then the Agent exits after flushing the current minute |
| `Ping` | `{}` | `Pong` `{ "serverTimeUtc" }` |

### Errors

`Error` `{ "code": "ProtocolUnsupported|FrameTooLarge|BadRequest|PolicyDenied|NotFound|SensorUnavailable|Busy|Internal",
"message": "…", "detail"?: {…} }`. `PolicyDenied.detail` carries `{ tier, reason }` but never the canonical path of a
Tier-0/1 target (the UI shows a generic "protected location" message).

## Payload rules

- Byte counts are `long`; percentages are `double` 0–100; timestamps are UTC epoch seconds (`ts`) or ticks for
  `createTime` (FILETIME).
- Hostnames in `ConnectionsTick` follow the app's host-logging policy **even for live data**: `none` → `host` omitted;
  `etld1` → registrable domain; `full` → full name. The UI never gets data the policy says it should not display.
- Command lines are sent only in `AppTick.procs` when `settings.show_command_lines = true`; otherwise `"(hidden)"`.
- Paths of Tier-1 locations are sent as `{ "path": null, "kind": "credential-store", "size": n }`.

## Versioning

- Additive fields are fine within `v1`; removing/renaming fields or changing semantics bumps `protocol` and the pipe name.
- `AppLedger.Ipc` exposes `IpcProtocol.Version` and a `Capabilities` list in `HelloAck` for optional features
  (`"estats"`, `"usn"`, `"geoip"`).

## Threading in the UI

`IpcClient` runs the read loop on a background thread and marshals ticks to the dispatcher via a single
`DispatcherTimer`-driven drain (coalescing: if two `AppsTick`s arrive before a render, only the latest is applied).
History queries run on a worker (`Task.Run`) with a read-only connection per query.
