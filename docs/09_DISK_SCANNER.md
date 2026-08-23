# 09 — Disk Scanner

Answers "how much of my disk is this app?" correctly and cheaply, and records it daily so growth is visible.

## What is measured

| Bucket | Definition | Source |
|---|---|---|
| **Install** | everything under the app's install root(s) | resolver (`03`) |
| **Data** | observed write locations (FileIO sampling, ≥ 3 writes or ≥ 1 MB written) + catalog `data_dirs` + convention candidates that exist (`%APPDATA%\<Company>\<Product>`, `%LOCALAPPDATA%\<Company>\<Product>`, `LocalLow`, `%ProgramData%\<Company>`, `Documents\My Games\<Product>`, `Saved Games\<Product>`, `%LOCALAPPDATA%\Packages\<PFN>` for MSIX) | `disk_locations` |
| **Cache (reclaimable, estimate)** | catalog `cache_dirs` + name heuristics within Install/Data roots: `Cache`, `Code Cache`, `GPUCache`, `DawnCache`, `ShaderCache`, `DXCache`, `GLCache`, `logs`, `Logs`, `crashes`, `Crashpad`, `Temp`, `tmp`; plus shader caches keyed by exe under `%LOCALAPPDATA%\NVIDIA\DXCache` / `%LOCALAPPDATA%\D3DSCache` / `%LOCALAPPDATA%\AMD\DxCache` when attributable by name | heuristics |
| **Per drive** | each bucket split by volume; drive capacity/used from `GetDiskFreeSpaceExW` | |
| **Totals** | logical (sum of file sizes) and on-disk (allocation) | |

Never deleted by us. "Reclaimable" is a label for the user, not an action (`23_NON_GOALS.md`).

## Size semantics

- **Logical** = `nFileSizeHigh:nFileSizeLow` from enumeration.
- **On-disk**: if `FILE_ATTRIBUTE_COMPRESSED` or `FILE_ATTRIBUTE_SPARSE_FILE` → `GetCompressedFileSizeW`; if
  `FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS` or `FILE_ATTRIBUTE_OFFLINE` (OneDrive Files On-Demand, cloud placeholders) → 0 on-disk
  and counted in a separate `cloud_placeholder_logical` total; otherwise logical rounded up to the volume cluster size
  (`GetDiskFreeSpaceW` bytes/cluster, cached per volume). Resident files (< ~700 B on NTFS) are counted as 0 on-disk.
- **Reparse points** (`FILE_ATTRIBUTE_REPARSE_POINT`): recorded in `disk_locations` as kind `link` with the target
  (`DeviceIoControl(FSCTL_GET_REPARSE_POINT)`), **not descended** by default. `settings.scan_follow_links = true`
  descends only when the target canonicalizes inside the same app root and is not Tier 0/1. Cloud placeholders are
  reparse points too — distinguished by the `RECALL_ON_DATA_ACCESS` attribute, enumerated normally.
- **Hard links**: enumeration cannot see link counts without opening each file. Default: not deduplicated (app folders
  rarely hard-link). `settings.scan_dedupe_hardlinks = true` opens files with `FILE_FLAG_BACKUP_SEMANTICS` and uses
  `GetFileInformationByHandleEx(FileIdInfo)` → dedupe by `(VolumeSerial, FileId)`; cost ≈ one open per file, documented in the UI.
- **Alternate data streams**: ignored (not enumerated); noted in the tooltip.
- Dev Drive / ReFS: same enumeration; USN available on ReFS; cluster size from the API as usual.

## Enumeration

- `FindFirstFileExW(path, FindExInfoBasic, FindExSearchNameMatch, FIND_FIRST_EX_LARGE_FETCH)` via CsWin32; `\\?\` prefix
  for long paths; breadth-first with a bounded `Channel<DirectoryJob>` and **4 workers** (I/O bound; more workers do
  not help on a single SSD and hurt on HDD — configurable 1–8).
- Worker threads: `ThreadPriority.Lowest`; process-wide I/O priority stays normal (the Agent does other work), but
  scanner threads set `SetThreadInformation(ThreadMemoryPriority…)`/`THREAD_MODE_BACKGROUND_BEGIN` around scan bursts.
- Every directory passes `PolicyGuard.Evaluate` before descent: Tier 0 → skip (count as one "(Windows)" entry if the
  root itself is Tier 0, which only happens for `sys:*` apps — they are never scanned); Tier 1 → sizes only, names dropped.
- Progress: files/bytes counted, reported every 500 ms via `ScanProgress`; cancellable (`ScanNow` again, app exit, shutdown).
- Errors (`ERROR_ACCESS_DENIED`, `ERROR_PATH_NOT_FOUND` mid-scan): counted, directory skipped, scan still completes with
  `partial=true`; surfaced as "N folders could not be read".

## Incremental scans via USN journal (Agent only, NTFS/ReFS)

1. On first full scan of a root, record `(volume, UsnJournalID, NextUsn)` in `meta` (per app root).
2. Incremental run: `FSCTL_READ_USN_JOURNAL` from the stored USN with `ReasonMask = DATA_OVERWRITE | DATA_EXTEND |
   DATA_TRUNCATION | FILE_CREATE | FILE_DELETE | RENAME_OLD_NAME | RENAME_NEW_NAME | HARD_LINK_CHANGE | REPARSE_POINT_CHANGE`
   (+ `RETURN_ONLY_ON_CLOSE`). For each record, resolve the parent FRN to a path (`OpenFileById` + `GetFinalPathNameByHandleW`,
   cached FRN→path map bounded at 100 k entries) and test against tracked roots (prefix match on canonical paths).
3. Mark matched directories dirty; rescan only dirty directories (non-recursive) and their deleted children; recompute
   totals from the per-directory cache (`Dictionary<path, DirTotals>` persisted as a compact binary in `cache\scan\<app_id>.bin`).
4. If `ERROR_JOURNAL_ENTRY_DELETED` (journal wrapped) or the journal ID changed → full rescan.
5. Non-NTFS/ReFS volumes, or USN unavailable (error stored in `Health`): scheduled full rescans only.

Expected cost (S4): full 300 GB / 500 k files < 2 min on SATA SSD at background priority; incremental < 5 s.

## Scheduling

| Trigger | Action |
|---|---|
| App first seen (install root resolved) | full scan, low priority, after 60 s of app runtime (avoid scanning during game startup) |
| Daily, first idle period after 03:00 local (no UI, CPU < 20 %) | incremental for all apps seen in the last 7 days; full for roots older than 30 days since last full |
| App exit, if last snapshot > 6 h | incremental |
| `ScanNow` from UI | full or incremental as requested (one at a time; others queued) |
| Disk tab open | FileIO window + incremental if last scan > 1 h |

Snapshots: one `disk_snapshots` row per app per drive per local day (`INSERT OR REPLACE`, so a rescan later the same day
updates the row). `scan_kind = estimate` when only catalog/convention candidates were summed without enumeration
(e.g., app never ran).

## Data folder discovery (links to `03` and `05`)

- During FileIO windows, writes are aggregated per directory (`DirectoryActivity`). A directory becomes an **observed**
  data location when it has ≥ 3 distinct write events or ≥ 1 MB written across windows, is outside the install root,
  is not Tier 0, and canonicalizes to a path under the user profile, `%ProgramData%`, or a drive the app has written to.
- Confidence: observed ≥ 0.9; catalog 0.95; convention candidate that exists 0.6; user 1.0. Kind from catalog or
  heuristics (`cache`/`log`/`shader`/`temp` by name; else `data`).
- Observed locations are pruned if not written to for 90 days and their size is < 1 MB.

## Output to the UI

`DiskSummary { install: {logical,onDisk,files}, data: {...}, cache: {...}, placeholders: logical, perDrive: [{drive,
capacity, used, install, data, cache, pctOfCapacity, pctOfUsed}], locations: [{path|null, kind, source, confidence,
lastWrite, size, tier}], topFiles: [{path|null, size, kind}], lastScan: {kind, utc, partial, unreadableDirs} }`.
Tier-1 entries carry `path = null` + `kind = "credential-store"|"browser-profile-secrets"|…` (11 §Tier 1).

## Tests

- Golden directory fixture built at test time (`tests/.../Disk/FixtureBuilder`): compressed file, sparse file, junction
  into the fixture, junction out of the fixture, symlink, hard-linked pair, 0-byte file, long path (> 260), unicode names.
  Asserts logical/on-disk totals and that out-of-root junctions are not followed.
- USN tests are `Category=Admin` (create journal records by writing files, assert dirty-set and incremental totals).
- `PolicyGuard` integration: a junction from the fixture into `%SystemRoot%` must be skipped and reported as Tier 0.
