-- AppLedger schema v1. The authority for this file is docs/06_DATA_MODEL.md; the doc writes
-- metrics_1h and metrics_1d as "LIKE metrics_1m", which is shorthand - SQLite has no such syntax,
-- so the column list is repeated in full here and a test asserts the three are identical.
--
-- Pragmas are not set here: journal_mode cannot run inside a transaction, and the page cache size
-- differs between the Agent and the UI. They belong to the connection, not to the schema.

CREATE TABLE meta (
  key   TEXT PRIMARY KEY,
  value TEXT NOT NULL
);

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
  tier              INTEGER NOT NULL DEFAULT 3      -- 2 = zero-touch, 3 = normal (docs/11_SAFETY_POLICY.md)
);

CREATE TABLE app_versions (
  app_id         TEXT NOT NULL REFERENCES apps(app_id) ON DELETE CASCADE,
  version        TEXT NOT NULL,
  exe_sha256     TEXT,
  first_seen_utc INTEGER NOT NULL,
  last_seen_utc  INTEGER NOT NULL,
  PRIMARY KEY (app_id, version, exe_sha256)
);

CREATE TABLE process_instances (
  pid                INTEGER NOT NULL,
  create_time        INTEGER NOT NULL,              -- FILETIME ticks; half of the process key
  app_id             TEXT NOT NULL REFERENCES apps(app_id) ON DELETE CASCADE,
  image_path         TEXT,
  command_line       TEXT,                          -- NULL when policy says redact
  parent_pid         INTEGER,
  parent_create_time INTEGER,
  user_sid           TEXT,
  session_id         INTEGER,
  integrity          TEXT,
  elevated           INTEGER,
  arch               TEXT,
  start_utc          INTEGER NOT NULL,
  exit_utc           INTEGER,
  exit_code          INTEGER,
  identity_evidence  TEXT,                          -- JSON, only at diagnostics level Debug or higher
  PRIMARY KEY (pid, create_time)
);
CREATE INDEX ix_pi_app_start ON process_instances(app_id, start_utc);

-- One wide row per app per bucket. ts is the bucket start, UTC epoch seconds.
-- commit_bytes, not commit: COMMIT is a SQLite keyword and will not parse unquoted (docs/24_ADR.md).
CREATE TABLE metrics_1m (
  app_id           TEXT NOT NULL REFERENCES apps(app_id) ON DELETE CASCADE,
  ts               INTEGER NOT NULL,
  runtime_s        INTEGER NOT NULL,
  procs            REAL,
  procs_max        INTEGER,
  cpu_pct          REAL,
  cpu_pct_max      REAL,
  cpu_user_ms      INTEGER,
  cpu_kernel_ms    INTEGER,
  ws_private       INTEGER,
  ws_private_max   INTEGER,
  commit_bytes     INTEGER,
  ws               INTEGER,
  gpu_pct          REAL,
  vram_ded         INTEGER,
  vram_ded_max     INTEGER,
  vram_shared      INTEGER,
  io_read          INTEGER,
  io_write         INTEGER,
  disk_read        INTEGER,
  disk_write       INTEGER,
  disk_ops         INTEGER,
  net_in           INTEGER,
  net_out          INTEGER,
  net_in_loopback  INTEGER,
  net_out_loopback INTEGER,
  threads          REAL,
  handles          REAL,
  hard_faults      INTEGER,
  degraded         INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (app_id, ts)
) WITHOUT ROWID;
CREATE INDEX ix_m1m_ts ON metrics_1m(ts);

CREATE TABLE metrics_1h (
  app_id           TEXT NOT NULL REFERENCES apps(app_id) ON DELETE CASCADE,
  ts               INTEGER NOT NULL,
  runtime_s        INTEGER NOT NULL,
  procs            REAL,
  procs_max        INTEGER,
  cpu_pct          REAL,
  cpu_pct_max      REAL,
  cpu_user_ms      INTEGER,
  cpu_kernel_ms    INTEGER,
  ws_private       INTEGER,
  ws_private_max   INTEGER,
  commit_bytes     INTEGER,
  ws               INTEGER,
  gpu_pct          REAL,
  vram_ded         INTEGER,
  vram_ded_max     INTEGER,
  vram_shared      INTEGER,
  io_read          INTEGER,
  io_write         INTEGER,
  disk_read        INTEGER,
  disk_write       INTEGER,
  disk_ops         INTEGER,
  net_in           INTEGER,
  net_out          INTEGER,
  net_in_loopback  INTEGER,
  net_out_loopback INTEGER,
  threads          REAL,
  handles          REAL,
  hard_faults      INTEGER,
  degraded         INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (app_id, ts)
) WITHOUT ROWID;
CREATE INDEX ix_m1h_ts ON metrics_1h(ts);

-- ts is the UTC epoch of local midnight for the day, so a chart axis needs no conversion.
CREATE TABLE metrics_1d (
  app_id           TEXT NOT NULL REFERENCES apps(app_id) ON DELETE CASCADE,
  ts               INTEGER NOT NULL,
  runtime_s        INTEGER NOT NULL,
  procs            REAL,
  procs_max        INTEGER,
  cpu_pct          REAL,
  cpu_pct_max      REAL,
  cpu_user_ms      INTEGER,
  cpu_kernel_ms    INTEGER,
  ws_private       INTEGER,
  ws_private_max   INTEGER,
  commit_bytes     INTEGER,
  ws               INTEGER,
  gpu_pct          REAL,
  vram_ded         INTEGER,
  vram_ded_max     INTEGER,
  vram_shared      INTEGER,
  io_read          INTEGER,
  io_write         INTEGER,
  disk_read        INTEGER,
  disk_write       INTEGER,
  disk_ops         INTEGER,
  net_in           INTEGER,
  net_out          INTEGER,
  net_in_loopback  INTEGER,
  net_out_loopback INTEGER,
  threads          REAL,
  handles          REAL,
  hard_faults      INTEGER,
  degraded         INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (app_id, ts)
) WITHOUT ROWID;
CREATE INDEX ix_m1d_ts ON metrics_1d(ts);

CREATE TABLE net_hosts_daily (
  app_id          TEXT NOT NULL REFERENCES apps(app_id) ON DELETE CASCADE,
  day             INTEGER NOT NULL,                -- local calendar date as YYYYMMDD
  host            TEXT NOT NULL,                   -- eTLD+1 or full name per policy; '(ip)' / '(other)'
  proto_mask      INTEGER NOT NULL,                -- 1 tcp, 2 udp, 4 quic, 8 loopback
  remote_ip_count INTEGER NOT NULL,
  conn_count      INTEGER NOT NULL,
  in_bytes        INTEGER NOT NULL,
  out_bytes       INTEGER NOT NULL,
  first_seen_utc  INTEGER NOT NULL,
  last_seen_utc   INTEGER NOT NULL,
  iface_mask      INTEGER NOT NULL DEFAULT 0,      -- 1 ethernet, 2 wifi, 4 tunnel, 8 cellular, 16 other
  PRIMARY KEY (app_id, day, host)
) WITHOUT ROWID;

-- Global, not per app: a per-app reverse map would be a browsing history by another name.
CREATE TABLE ip_names (
  ip            TEXT PRIMARY KEY,
  host          TEXT NOT NULL,
  last_seen_utc INTEGER NOT NULL
);

CREATE TABLE dns_records (
  host         TEXT NOT NULL,
  rtype        TEXT NOT NULL,
  value        TEXT NOT NULL,
  ttl          INTEGER,
  resolved_utc INTEGER NOT NULL,
  PRIMARY KEY (host, rtype, value)
) WITHOUT ROWID;

CREATE TABLE disk_locations (
  app_id         TEXT NOT NULL REFERENCES apps(app_id) ON DELETE CASCADE,
  path           TEXT NOT NULL,                    -- canonical
  kind           TEXT NOT NULL,                    -- install|data|cache|log|shader|temp
  source         TEXT NOT NULL,                    -- observed|catalog|convention|user
  confidence     REAL NOT NULL,
  tier           INTEGER NOT NULL,
  write_count    INTEGER NOT NULL DEFAULT 0,
  last_write_utc INTEGER,
  PRIMARY KEY (app_id, path)
);

CREATE TABLE disk_snapshots (
  app_id          TEXT NOT NULL REFERENCES apps(app_id) ON DELETE CASCADE,
  day             INTEGER NOT NULL,
  drive           TEXT NOT NULL,
  drive_guid      TEXT,
  drive_capacity  INTEGER,
  drive_used      INTEGER,
  install_logical INTEGER,
  install_on_disk INTEGER,
  install_files   INTEGER,
  data_logical    INTEGER,
  data_on_disk    INTEGER,
  data_files      INTEGER,
  cache_on_disk   INTEGER,
  scan_kind       TEXT NOT NULL,                   -- full|incremental|estimate
  scanned_utc     INTEGER NOT NULL,
  PRIMARY KEY (app_id, day, drive)
) WITHOUT ROWID;

CREATE TABLE disk_top_files (
  app_id   TEXT NOT NULL REFERENCES apps(app_id) ON DELETE CASCADE,
  path     TEXT NOT NULL,
  size     INTEGER NOT NULL,
  kind     TEXT NOT NULL,
  seen_utc INTEGER NOT NULL,
  PRIMARY KEY (app_id, path)
);

CREATE TABLE events (
  id           INTEGER PRIMARY KEY,
  app_id       TEXT REFERENCES apps(app_id) ON DELETE CASCADE,   -- NULL for system events
  ts_utc       INTEGER NOT NULL,
  kind         TEXT NOT NULL,
  severity     INTEGER NOT NULL DEFAULT 0,                       -- 0 info, 1 notice, 2 warning
  payload_json TEXT,
  acknowledged INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX ix_events_app_ts ON events(app_id, ts_utc);
CREATE INDEX ix_events_ts ON events(ts_utc);

CREATE TABLE usage_daily (
  app_id    TEXT NOT NULL REFERENCES apps(app_id) ON DELETE CASCADE,
  day       INTEGER NOT NULL,
  runtime_s INTEGER NOT NULL,
  launches  INTEGER NOT NULL,
  crashes   INTEGER NOT NULL,
  hour_mask INTEGER NOT NULL,                      -- one bit per local hour
  PRIMARY KEY (app_id, day)
) WITHOUT ROWID;

CREATE TABLE health_minutes (
  ts            INTEGER PRIMARY KEY,
  agent_cpu_pct REAL,
  agent_ws      INTEGER,
  events_lost   INTEGER,
  sensors_json  TEXT
);

-- UI-owned. The Agent reads it and never writes it (docs/06_DATA_MODEL.md §Ownership).
CREATE TABLE settings (
  key   TEXT PRIMARY KEY,
  value TEXT NOT NULL
);

CREATE TABLE app_overrides (
  id              INTEGER PRIMARY KEY,
  kind            TEXT NOT NULL,                   -- merge|split|category|display_name|hidden|exclude_history|host_logging
  match_json      TEXT NOT NULL,
  value           TEXT,
  previous_app_id TEXT,
  created_utc     INTEGER NOT NULL
);

CREATE TABLE catalog_state (
  key   TEXT PRIMARY KEY,
  value TEXT NOT NULL
);
