# 07 — IPC

UI ↔ Agent over a local named pipe. Contracts live in `AppLedger.Ipc` (shared by both processes), serialized with
`System.Text.Json` source generators (`IpcJsonContext`). History is **not** served over the pipe — the UI reads SQLite
directly; the pipe carries live data, commands and health.

## Transport & security

- Name: `\\.\pipe\AppLedger.v1` (`v1` bumps on breaking changes; the UI tries the highest version it knows).
- Server: `NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, maxNumberOfServerInstances: 4,
  PipeTransmissionMode.Byte, PipeOptions.Asynchronous, inBufferSize, outBufferSize, pipeSecurity)` with an explicit
  `PipeSecurity`:
  - **DACL**: `FullControl` for the current user's SID and for `BUILTIN\Administrators`. Nothing else.
  - **SACL**: a mandatory label of **Medium** integrity. Without it the pipe inherits the creating process's High
    label and the Medium-IL UI cannot obtain write access — which is what connecting to a pipe requires.
- **Do not use `PipeOptions.CurrentUserOnly` here.** It looks like the simpler answer and it is the wrong one across
  this particular boundary: an elevated process's default token owner is `BUILTIN\Administrators`, so the pipe it
  creates is *owned* by that group, while the client-side `CurrentUserOnly` check compares the pipe's owner against
  `WindowsIdentity.GetCurrent().Owner` of a Medium-IL process, which is the user SID. The two do not match, and it
  sets no integrity label either. `24_ADR.md` ADR-17 records this; `19_TESTING.md` §Pipe security is the
  `Category=Admin` test that keeps it honest, because no non-elevated run can reproduce it.
- Remote clients: `\\host\pipe\…` access must be refused. .NET's own `CreateNamedPipe` call already passes
  `PIPE_REJECT_REMOTE_CLIENTS`; **verify that against the pinned runtime rather than assuming it**, and fall back to
  `SetNamedPipeHandleState` on the raw handle if it does not.
- Client: `NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous)` — no `CurrentUserOnly`,
  for the reason above. The squatting defence is peer verification instead (next bullet).
- **Peer verification, both directions.** After connect, each side resolves the other's PID
  (`GetNamedPipeClientProcessId` / `GetNamedPipeServerProcessId`), canonicalizes its image path
  (`QueryFullProcessImageName` + `PathCanonicalizer`) and requires it to sit in the same install directory as its
  own. A mismatch is `Error(PolicyDenied)` and a disconnect, logged at Warning with the path **classified**, never
  quoted (`15_LOGGING.md` §Redaction). This is what stops a same-user process from squatting the pipe name — the
  threat `CurrentUserOnly` was reached for in the first place.
- The Agent never executes anything on behalf of the UI; every request is a read, a sampling hint, a policy change
  that the Agent re-validates, or a self-management command (pause/shutdown/purge). `PolicyGuard` re-checks every path
  field regardless of what the UI already did.
- **Where this code lives.** `System.IO.Pipes.AccessControl` ships in the .NET 10 shared framework, so nothing new is
  pinned — but every member carries `[SupportedOSPlatform("windows")]`, `AppLedger.Ipc` targets `net10.0` rather than
  `net10.0-windows`, and warnings are errors here. CA1416 therefore refuses the ACL calls inside `Ipc`. `NamedPipeServerStream`
  itself is fine there; only the securing of it is not. The split:
  - `AppLedger.Ipc` defines the transport port (accept/connect returning a connected `Stream`) and knows nothing
    about how the stream was secured. Tests hand it an in-memory pair.
  - `AppLedger.Infrastructure` supplies the two Windows primitives and **no more**: the `PipeSecurity` builder, and
    the peer resolver over `GetNamedPipeClientProcessId`/`GetNamedPipeServerProcessId` +
    `QueryFullProcessImageName` + `PathCanonicalizer` (all already in `NativeMethods.txt`). It does **not**
    implement the port, because `Infrastructure → Ipc` is not an edge in `CLAUDE.md` §Solution layout and this is
    not worth adding one for.
  - The Agent and the App each implement the port over those primitives — both already reference `Ipc` and
    `Infrastructure`, so no new edge appears anywhere.

  This also gives the `Category=Admin` test a home: it exercises the Infrastructure primitives against a raw
  `NamedPipeServerStream`, so it needs no Ipc, no Agent and no protocol.

## Framing

`[u32 little-endian length][UTF-8 JSON]`. The declared length is checked **before a buffer is sized** — the length
field is the one number that comes from the peer and must not be trusted to size an allocation.

The cap is asymmetric: the server accepts at most **64 KB** from a client, the client accepts up to **4 MB** from the
server. No legitimate UI request comes close to 64 KB (the largest is `ResolvePath`, one path), while `AppsTick` and
`AppDetail` genuinely need room. The asymmetry costs nothing and cuts by 64× the memory a hostile same-user process
can make the *elevated* Agent commit.

Oversized → `Error(FrameTooLarge)` and **disconnect**. The reader must never skip the declared length to
resynchronize: that is the attacker-controlled number, and there is no safe resync point in a byte-stream framing.

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
Agent → HelloAck   { "protocol": 1, "agent": "1.0.0", "mode": "Full", "dbPath": "...", "schema": 1,
                     "capabilities": ["estats", "usn"],
                     "sensors": { "ProcessPoller":     { "state": "Running" },
                                  "EtwHub":            { "state": "Running" },
                                  "GpuPoller":         { "state": "Unavailable", "detail": "NoGpuCounters" },
                                  "ConnectionPoller":  { "state": "Running" } },
                     "catalog": { "version": "2026.08.0", "verified": true }, "startedUtc": 1755820800 }
```

Protocol mismatch → `Error(ProtocolUnsupported)` and the UI shows "Update required".

**Sensor keys are `ISensor.Name`, verbatim.** An earlier draft of this document invented a parallel vocabulary
(`EtwNetwork`, `EtwDiskIO`, `DnsClient`, `Gpu`, `Connections`) and a parallel set of states (`Ok`, `Sampled`,
`Unavailable:1179`). Neither survives contact with the code: `AppLedger.Core.Collection.ISensor` exposes
`Name` and a `SensorHealth(SensorState, Detail, HandlerErrors, EventsLost)` where `SensorState` is
`Stopped | Starting | Running | Unavailable`. The wire mirrors that one-to-one, so a new sensor needs no wire
change and a wire value can never disagree with what the host actually observed. `ProcessPoller` is the one key
with no `ISensor` behind it — `IProcessSource` is not a sensor because it cannot be absent — and the host
synthesizes its entry.

`detail` is a short reason code, never a path, a host or a Win32 message (`15_LOGGING.md` §Redaction); a Win32
error appears as its number (`"1179"`), which is why the old packed `"Unavailable:1179"` form is not needed.

`mode` is **`Full | Degraded`** — two values. `Degraded` means the Agent is running with at least one sensor
`Unavailable`. There is deliberately no `Lite`: Lite mode is the state where *no Agent answered at all*, so no
`HelloAck` exists to carry it. The UI's own three-state display (`08_UI.md` §HomePage `AgentHealthStrip`)
synthesizes `Lite` from a failed connection, and that asymmetry is the point — a mode the Agent could report
would be a mode the Agent was alive to report.

`capabilities` is the optional-feature list of §Versioning: `"estats"`, `"usn"`, `"geoip"`. A client must treat an
absent capability as absent, never as false-but-present.

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

**Fan-out and slow clients.** The collector's live channel is a *queue*, not a broadcast: several readers would each
get a disjoint subset of ticks. The Agent therefore runs exactly one reader, serializes each tick **once**, and posts
the same bytes to one bounded mailbox per subscriber. Mailboxes are capacity 2, drop-oldest — a client one tick
behind wants the newest tick, not a replay of seconds it no longer cares about (`01_ARCHITECTURE.md` §Backpressure;
rollup inputs still never drop). A subscriber that has dropped **60 consecutive ticks** is disconnected: with
`maxNumberOfServerInstances: 4`, a permanently wedged client otherwise holds one of the user's own four slots for
the lifetime of the Agent.

### Requests (request/response)

| Type | Payload | Response |
|---|---|---|
| `GetAppDetail` | `{ "appId" }` | `AppDetail` — identity, evidence summary, enrichment, live instance list, disk summary, sensors applicable |
| `GetInstalledApps` | `{}` | `InstalledApps` — from the resolver's indexes (uninstall, msix, launchers, package managers), not from SQLite |
| `ResolvePath` | `{ "path": "C:\\…\\app.exe" }` | `ResolvedPath` — `PolicyGuard` decision `{ canonical, tier, allowed, reason }` + the resolved identity preview (no scan, no persistence) |
| `ResolveWindow` | `{ "hwnd": 123456 }` | `ResolvedWindow` — `(pid, createTime, appId)` after UWP `CoreWindow` handling |
| `ResolveHost` | `{ "host": "cdn.discordapp.com" }` | `HostRecords` — A/AAAA/CNAME chain/TTL/status/server from `DnsQueryEx` (cached 10 min) |
| `ScanNow` | `{ "appId", "kind": "full|incremental" }` | `ScanAccepted` then `ScanProgress` stream `{ "appId", "phase", "files", "bytes", "pct" }` then `ScanDone` |
| `SamplingHint` | `{ "appId", "disk": true }` | `Ack` (holds the FileIO window open for 45 s) |
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
- Command lines are sent only in `AppTick.procs` when **`settings.store_command_lines`** is true; otherwise
  `"(hidden)"`. One key gates both storing and sending — a user who turned command lines off did not ask for them
  to keep flowing over the pipe. (`12_PRIVACY_AND_RETENTION.md` §Defaults owns the key; an earlier draft of this
  document called it `show_command_lines`.)
- Paths of Tier-1 locations are sent as `{ "path": null, "kind": "credential-store", "size": n }`.

## Versioning

- Additive fields are fine within `v1`; removing/renaming fields or changing semantics bumps `protocol` and the pipe name.
- `AppLedger.Ipc` exposes `IpcProtocol.Version` and a `Capabilities` list in `HelloAck` for optional features
  (`"estats"`, `"usn"`, `"geoip"`).

## Threading in the UI

`IpcClient` runs the read loop on a background thread and marshals ticks to the dispatcher via a single
`DispatcherTimer`-driven drain (coalescing: if two `AppsTick`s arrive before a render, only the latest is applied).
History queries run on a worker (`Task.Run`) with a read-only connection per query.
