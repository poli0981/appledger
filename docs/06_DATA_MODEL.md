# 06 — Data Model

SQLite at `%LOCALAPPDATA%\AppLedgerData\appledger.db`, WAL mode, accessed through `Microsoft.Data.Sqlite` + Dapper.
One writer per table family (below). Schema version lives in `meta`; migrations are forward-only SQL scripts embedded in
Infrastructure (`Migrations/0001_initial.sql`, …) and run inside a transaction after a file copy backup
(`appledger.db.bak-<schema>`).

## Pragmas

`journal_mode=WAL`, `synchronous=NORMAL`, `foreign_keys=ON`, `auto_vacuum=INCREMENTAL`, `busy_timeout=5000`,
`temp_store=MEMORY`, `cache_size=-32000` (32 MB) in the Agent, `-8000` in the UI. **The Agent value is provisional:**
S1-lite measured a ~75 MB floor before any storage existed, so a 32 MB page cache would breach the 100 MB budget on
its own (`20_SPIKES.md` S1-lite Result). Settle it with a measurement in v0.2, not by assumption. The UI opens with `Mode=ReadOnly`
for all metric tables and a second `ReadWrite` connection only for `settings`/`app_overrides`.

## Ownership

| Tables | Writer | Reader |
|---|---|---|
| `apps`, `app_versions`, `process_instances`, `metrics_*`, `net_hosts_daily`, `dns_records`, `ip_names`, `disk_locations`, `disk_snapshots`, `events`, `usage_daily`, `health_minutes`, `catalog_state` | Agent | UI |
| `settings`, `app_overrides` | UI | Agent (polls `meta.overrides_rev` every 5 s; pipe `OverridesChanged` wakes it) |
| `meta` | both (distinct keys) | both |

## Schema (v1)

```sql
CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);          -- schema_version, overrides_rev, agent_version, created_utc

CREATE TABLE apps (
  app_id            TEXT PRIMARY KEY,
  display_name      TEXT NOT NULL,
  publisher         TEXT,
  category          TEXT NOT NULL DEFAULT 'Unknown',
  category_source   TEXT NOT NULL DEFAULT 'none',   -- user|catalog|steam|store|none
  source            TEXT NOT NULL,                  -- msix|steam|epic|gog|itch|cat|uninst|scoop|choco|winget|script|sys|root|user
  confidence        REAL NOT NULL,
  install_root      TEXT,                           -- canonical; NULL for sys:* and script:
  aliases_json      TEXT,                           -- losing identity candidates
  icon_path         TEXT,
  current_version   TEXT,
  exe_sha256        TEXT,
  signer            TEXT,
  sig_status        TEXT,                           -- Valid|Expired|Untrusted|Unsigned|CatalogSigned|Unknown
  runtime           TEXT,                           -- DotNet|Java|Electron|Python|Unity|Unreal|CEF|Native|Unknown
  first_seen_utc    INTEGER NOT NULL,
  last_seen_utc     INTEGER NOT NULL,
  tier              INTEGER NOT NULL DEFAULT 3      -- process tier per 11_SAFETY_POLICY: 2 = zero-touch, 3 = normal
);

CREATE TABLE app_versions (
  app_id TEXT NOT NULL REFERENCES apps(app_id) ON DELETE CASCADE,
  version TEXT NOT NULL, exe_sha256 TEXT, first_seen_utc INTEGER NOT NULL, last_seen_utc INTEGER NOT NULL,
  PRIMARY KEY (app_id, version, exe_sha256)
);

CREATE TABLE process_instances (
  pid INTEGER NOT NULL, create_time INTEGER NOT NULL,          -- FILETIME ticks
  app_id TEXT NOT NULL REFERENCES apps(app_id) ON DELETE CASCADE,
  image_path TEXT, command_line TEXT,                           -- command_line NULL when policy says redact
  parent_pid INTEGER, parent_create_time INTEGER,
  user_sid TEXT, session_id INTEGER, integrity TEXT, elevated INTEGER, arch TEXT,
  start_utc INTEGER NOT NULL, exit_utc INTEGER, exit_code INTEGER,
  identity_evidence TEXT,                                       -- JSON, only when diagnostics level >= Debug
  PRIMARY KEY (pid, create_time)
);
CREATE INDEX ix_pi_app_start ON process_instances(app_id, start_utc);

-- Wide rows: one per app per bucket. ts = bucket start, UTC epoch seconds.
CREATE TABLE metrics_1m (
  app_id TEXT NOT NULL REFERENCES apps(app_id) ON DELETE CASCADE, ts INTEGER NOT NULL,
  runtime_s INTEGER NOT NULL, procs REAL, procs_max INTEGER,
  cpu_pct REAL, cpu_pct_max REAL, cpu_user_ms INTEGER, cpu_kernel_ms INTEGER,
  ws_private INTEGER, ws_private_max INTEGER, commit_bytes INTEGER, ws INTEGER,   -- COMMIT is a SQLite keyword
  gpu_pct REAL, vram_ded INTEGER, vram_ded_max INTEGER, vram_shared INTEGER,
  io_read INTEGER, io_write INTEGER, disk_read INTEGER, disk_write INTEGER, disk_ops INTEGER,
  net_in INTEGER, net_out INTEGER, net_in_loopback INTEGER, net_out_loopback INTEGER,
  threads REAL, handles REAL, hard_faults INTEGER,
  degraded INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (app_id, ts)
) WITHOUT ROWID;
CREATE INDEX ix_m1m_ts ON metrics_1m(ts);
CREATE TABLE metrics_1h (LIKE metrics_1m);   -- same columns (expand in migration script); PRIMARY KEY (app_id, ts)
CREATE TABLE metrics_1d (LIKE metrics_1m);   -- ts = local-day start expressed in UTC (see §Time); PRIMARY KEY (app_id, ts)

CREATE TABLE net_hosts_daily (
  app_id TEXT NOT NULL REFERENCES apps(app_id) ON DELETE CASCADE,
  day INTEGER NOT NULL,                -- local date as YYYYMMDD
  host TEXT NOT NULL,                  -- eTLD+1 or full name per policy; '(ip)' when unnamed; '(other)' overflow
  proto_mask INTEGER NOT NULL,         -- 1 tcp, 2 udp, 4 quic(udp/443), 8 loopback
  remote_ip_count INTEGER NOT NULL, conn_count INTEGER NOT NULL,
  in_bytes INTEGER NOT NULL, out_bytes INTEGER NOT NULL,
  first_seen_utc INTEGER NOT NULL, last_seen_utc INTEGER NOT NULL,
  iface_mask INTEGER NOT NULL DEFAULT 0, -- 1 ethernet, 2 wifi, 4 tunnel/vpn, 8 cellular, 16 other
  PRIMARY KEY (app_id, day, host)
) WITHOUT ROWID;

CREATE TABLE ip_names (               -- global reverse map learned from DNS-Client events (not per app: privacy)
  ip TEXT PRIMARY KEY, host TEXT NOT NULL, last_seen_utc INTEGER NOT NULL
);

CREATE TABLE dns_records (            -- cache of explicit lookups (DnsQueryEx on expand) — only for stored hosts
  host TEXT NOT NULL, rtype TEXT NOT NULL, value TEXT NOT NULL, ttl INTEGER, resolved_utc INTEGER NOT NULL,
  PRIMARY KEY (host, rtype, value)
) WITHOUT ROWID;

CREATE TABLE disk_locations (
  app_id TEXT NOT NULL REFERENCES apps(app_id) ON DELETE CASCADE,
  path TEXT NOT NULL,                  -- canonical
  kind TEXT NOT NULL,                  -- install|data|cache|log|shader|temp
  source TEXT NOT NULL,                -- observed|catalog|convention|user
  confidence REAL NOT NULL, tier INTEGER NOT NULL,
  write_count INTEGER NOT NULL DEFAULT 0, last_write_utc INTEGER,
  PRIMARY KEY (app_id, path)
);

CREATE TABLE disk_snapshots (
  app_id TEXT NOT NULL REFERENCES apps(app_id) ON DELETE CASCADE,
  day INTEGER NOT NULL, drive TEXT NOT NULL,          -- 'C:' ; volume GUID kept in drive_guid
  drive_guid TEXT, drive_capacity INTEGER, drive_used INTEGER,
  install_logical INTEGER, install_on_disk INTEGER, install_files INTEGER,
  data_logical INTEGER, data_on_disk INTEGER, data_files INTEGER,
  cache_on_disk INTEGER,
  scan_kind TEXT NOT NULL,             -- full|incremental|estimate
  scanned_utc INTEGER NOT NULL,
  PRIMARY KEY (app_id, day, drive)
) WITHOUT ROWID;

CREATE TABLE disk_top_files (         -- top-20 largest files per app (refreshed by scans); names subject to tier policy
  app_id TEXT NOT NULL REFERENCES apps(app_id) ON DELETE CASCADE, path TEXT NOT NULL, size INTEGER NOT NULL,
  kind TEXT NOT NULL, seen_utc INTEGER NOT NULL, PRIMARY KEY (app_id, path)
);

CREATE TABLE events (
  id INTEGER PRIMARY KEY,
  app_id TEXT REFERENCES apps(app_id) ON DELETE CASCADE,   -- NULL for system events (AgentStarted, CatalogUpdated)
  ts_utc INTEGER NOT NULL, kind TEXT NOT NULL,              -- Launch|Exit|Crash|VersionChanged|Installed|Uninstalled|NewHost|DataGrowth|ListenOpened|IdentityChanged|AgentStarted|AgentRestarted|CatalogUpdated|CatalogRejected|DatabaseReset|Purge|IdentityError
  severity INTEGER NOT NULL DEFAULT 0,                      -- 0 info, 1 notice, 2 warning
  payload_json TEXT, acknowledged INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX ix_events_app_ts ON events(app_id, ts_utc); CREATE INDEX ix_events_ts ON events(ts_utc);

CREATE TABLE usage_daily (
  app_id TEXT NOT NULL REFERENCES apps(app_id) ON DELETE CASCADE, day INTEGER NOT NULL,
  runtime_s INTEGER NOT NULL, launches INTEGER NOT NULL, crashes INTEGER NOT NULL, hour_mask INTEGER NOT NULL, -- bit per local hour
  PRIMARY KEY (app_id, day)
) WITHOUT ROWID;

CREATE TABLE health_minutes (ts INTEGER PRIMARY KEY, agent_cpu_pct REAL, agent_ws INTEGER, events_lost INTEGER, sensors_json TEXT);

CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);   -- mirrors settings.json; UI-owned

CREATE TABLE app_overrides (
  id INTEGER PRIMARY KEY, kind TEXT NOT NULL,         -- merge|split|category|display_name|hidden|exclude_history|host_logging
  match_json TEXT NOT NULL, value TEXT, previous_app_id TEXT, created_utc INTEGER NOT NULL
);

CREATE TABLE catalog_state (key TEXT PRIMARY KEY, value TEXT NOT NULL);   -- version, fetched_utc, pubkey_id, last_error
```

`LIKE` above is shorthand for the doc; the migration script repeats the full column list for `metrics_1h`/`metrics_1d`.

## Tiers and retention

| Tier | Source | Resolution | Kept | Rows / 100 apps (upper bound) |
|---|---|---|---|---|
| ring | snapshots | 1 s | 1 h, memory only | — |
| `metrics_1m` | Rollup1m | 1 min | 7 days | 1.0 M if every app ran 24/7 — typical < 150 k |
| `metrics_1h` | from 1m | 1 h | retention (default 180 d, 30–365) | 438 k upper, typical < 80 k |
| `metrics_1d` | from 1h | 1 day | retention | 18 k |
| `net_hosts_daily` | from endpoints | day × host | retention | capped 200 hosts/app/day → ≤ 3.6 M upper, typical < 200 k |
| `disk_snapshots` | scanner | day × drive | retention | 36 k |
| `events`, `usage_daily` | detectors | — | retention | small |
| `process_instances` | poller | — | 30 days (then only counts survive via usage_daily) | depends on churn; Chromium churn ~5 k/day |

Size estimate: 1-h wide row ≈ 150 B + index; 100 apps × 180 d × 24 h ≈ 65 MB upper bound; 1-m × 7 d ≈ 150 MB upper bound;
typical machines are far below because rows exist only while an app runs. Target < 300 MB total (S5 verifies).

`RetentionJob` (nightly, first idle after 03:00 local): `DELETE … WHERE ts < cutoff` per tier in 10 k-row batches with
`PRAGMA incremental_vacuum(200)` between batches; `metrics_1m` cutoff 7 d; everything else `settings.retention_days`.
A full `VACUUM` only after an explicit purge.

## Rollup jobs

- `Rollup1m`: in-memory minute buffer → one `INSERT OR REPLACE` per app per minute inside one transaction (batched).
- `RollupHourly`: at `hh:02`, for the previous UTC hour: `INSERT OR REPLACE INTO metrics_1h SELECT … FROM metrics_1m GROUP BY app_id`
  with the weighted-average formulas from `05` §Rollup math (implemented in SQL; verified against the Core implementation
  by a golden test).
- `RollupDaily`: at 00:05 local for the previous local day (see §Time).
- `NetHostsRollup`: every 5 min flush endpoint maps into `net_hosts_daily` (upsert, add bytes, bump counts); policy applied here.
- `UsageDaily`: derived from `process_instances` at day rollover and on demand for today.

## Time

- All `ts`/`*_utc` columns are UTC epoch seconds. `day` columns are local calendar dates (`YYYYMMDD`) because users think
  in local days; the day boundary uses the time zone **in effect at rollup time**. A TZ change mid-history makes one day
  shorter/longer; we accept that (documented in the UI tooltip). DST transitions likewise.
- `metrics_1d.ts` = UTC epoch of local midnight of `day` (for chart axes); `day` itself is stored in `usage_daily`,
  `net_hosts_daily`, `disk_snapshots`.

## Purge semantics (`12`)

`Purge(all)`: `DELETE` every Agent-owned table, drop `cache\icons`, `VACUUM`, keep `settings`/`app_overrides`, event `Purge`.
`Purge(app)`: cascade via `apps` row delete; also scrub `ip_names` entries not referenced by any remaining `net_hosts_daily`
row (hostnames only, so the global map cannot reveal a purged app's hosts). `Purge(range)`: `DELETE` per tier by `ts`/`day`.
All purges run in the Agent (single writer) on request from the UI and report row counts back.

## Query patterns the UI relies on (must stay < 100 ms on S5 data)

- App page header: `apps` by id + latest `app_versions` + today's `usage_daily`.
- History chart 6 months: `SELECT ts, col FROM metrics_1d WHERE app_id=? AND ts BETWEEN ? AND ? ORDER BY ts` (≤ 366 rows).
- Top-N today: `SELECT app_id, SUM(col) FROM metrics_1h WHERE ts >= today_utc GROUP BY app_id ORDER BY 2 DESC LIMIT 10`.
- Hosts: `SELECT host, SUM(in_bytes), SUM(out_bytes), MIN(first_seen_utc) FROM net_hosts_daily WHERE app_id=? AND day BETWEEN ? AND ? GROUP BY host`.
- Calendar heatmap: `usage_daily` for 365 days.
