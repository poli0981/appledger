# 15 — Logging

Serilog in both processes. Logs are diagnostics for the user and for bug reports — never a second copy of the history DB.

## Sinks & files

- `DataRoot\logs\agent-.log` and `DataRoot\logs\ui-.log`, `RollingInterval.Day`, `retainedFileCountLimit: 7`,
  `fileSizeLimitBytes: 10 MB` with `rollOnFileSizeLimit`, `shared: true` (the UI may tail the Agent log), `buffered: false`
  for the Agent (crash-safe), `flushToDiskInterval: 2 s`.
- Format: `{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj} {Properties:j}{NewLine}{Exception}`.
- Debug builds also write to the debugger/console. No event-log sink, no network sinks.

## Levels

| Level | Use |
|---|---|
| Verbose | per-event ETW traces — never enabled in release builds |
| Debug | unredacted diagnostics; user-enabled for 24 h (`12`) |
| Information | lifecycle, sensor state changes, rollup/retention summaries, catalog updates, scan summaries — **redacted** |
| Warning | lost events, budget exceeded, sensor retries, identity fallbacks, USN reset |
| Error | sensor failure, DB errors, pipe faults, migration issues |
| Fatal | unrecoverable (DB open failure after reset, host crash) |

## Redaction

`ILogger` extensions accept only redacted types at ≥ Information:
- `PathRedactor.ToClass(path)` → `<install-root>\…\<ext>` / `<userprofile>\…` / `<windows>` / `<drive>\…` (keeps depth and extension, drops names).
- `HostRedactor.ToClass(host)` → `<etld1>` or `<ip-v4>`/`<ip-v6>` (never the value) at Information; the value at Debug.
- Command lines, SIDs, user names: never at ≥ Information (log `{HasCommandLine: true, Length: n}` instead).
- A unit test scans log templates in Infrastructure/Collector for forbidden property names (`Path`, `Host`, `CommandLine`,
  `User`) outside `Debug` calls.

## Structured properties (conventions)

`AppId`, `Pid`, `CreateTime`, `Sensor`, `Session`, `EventsLost`, `Rows`, `DurationMs`, `Win32Error`, `Tier`.
Source contexts per component (`AppLedger.Collector.EtwHub`, `…ProcessPoller`, `…DiskScanner`, `AppLedger.Agent.PipeServer`,
`AppLedger.App.Ipc`, …). Enrich with `AgentVersion`, `OsBuild`, `MachineHash` (SHA-256 of machine GUID, first 8 chars —
lets a user correlate logs from two PCs without exposing the GUID).

## Health report

Settings › Diagnostics › "Copy health report": a redacted text block (versions, OS build, sensors + errors, budget
numbers, DB size and row counts per table, catalog version, last 20 Warning+ lines) the user can paste into an issue.

## Agent self-watch

The Agent logs its own CPU/WS every 10 min at Information and writes `health_minutes`; exceeding budget for 10 minutes
logs a Warning once per hour (no log spam).
