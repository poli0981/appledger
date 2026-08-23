# 04 — Data Sources

One row per displayed fact: where it comes from, what it costs, what it means, and what happens when it is unavailable.
"Privilege" is the minimum needed; the Agent has admin, the UI in Lite mode does not.
Every metric shown in the UI must link (tooltip) to its semantics line here (FR-20).

## A. Process table (ProcessPoller, 1 Hz, no handles)

Single call `NtQuerySystemInformation(SystemProcessInformation)` returns every process with counters. Buffer starts at
1 MB, doubles on `STATUS_INFO_LENGTH_MISMATCH`, retained between calls. Thread entries are skipped (not parsed) to keep
the loop cheap. Privilege: user (all processes are listed; only own-session processes are kept by default).

| Field (struct) | Metric | Semantics |
|---|---|---|
| `UniqueProcessId`, `CreateTime` | process key | `(pid, createTime)` |
| `InheritedFromUniqueProcessId` | parent pid | validated against known instances (03) |
| `ImageName` | image name | file name only; full path needs a handle |
| `SessionId` | session | default filter = Agent's own session |
| `UserTime`, `KernelTime` | CPU % user/kernel | Δ / (interval × logical CPUs) × 100, cap 100 |
| `CycleTime` | CPU cycles | shown in Processes tab; more precise than time on modern CPUs |
| `WorkingSetPrivateSize` | **Memory** (private WS) | Task Manager "Memory" column |
| `PagefileUsage` | commit (private bytes) | the real reservation; `PeakPagefileUsage` peak |
| `WorkingSetSize`, `PeakWorkingSetSize` | working set | includes shared pages |
| `HandleCount`, `NumberOfThreads` | handles, threads | |
| `ReadTransferCount`, `WriteTransferCount`, `OtherTransferCount` | **I/O** bytes (all kinds) | files, pipes, devices, sockets; Δ per second |
| `ReadOperationCount`, `WriteOperationCount` | I/O ops | |
| `HardFaultCount` | hard faults/s | memory pressure indicator (Processes tab) |
| `BasePriority` | priority | |

Struct is declared by hand in Infrastructure (`Ntdll/SystemProcessInformation.cs`), field offsets asserted by a unit
test against `Marshal.OffsetOf` for x64/ARM64. Enabling `Microsoft.Windows.WDK.Win32Metadata` for CsWin32 is optional.

## B. Per-process enrichment (once per instance, `PROCESS_QUERY_LIMITED_INFORMATION`)

| Fact | API | Notes / failure |
|---|---|---|
| Full image path | `QueryFullProcessImageNameW(PROCESS_NAME_NATIVE→Win32)` | Tier-2: skipped; path from ETW `ProcessStart.ImageFileName` (device path → DOS via `QueryDosDeviceW` map) |
| Command line | `NtQueryInformationProcess(ProcessCommandLineInformation=60)` | Win 8.1+; works with limited rights; PPL → `STATUS_ACCESS_DENIED` → "(unavailable)" |
| Package identity | `GetPackageFullName(hProcess)` → `PackageFullNameFromId`/`PackageFamilyNameFromFullName` | `APPMODEL_ERROR_NO_PACKAGE` = not packaged |
| User SID / name | `OpenProcessToken(TOKEN_QUERY)` + `GetTokenInformation(TokenUser)` → `LookupAccountSidW` | may fail on PPL → "(protected)" |
| Integrity level | `GetTokenInformation(TokenIntegrityLevel)` | Low/Medium/High/System |
| Elevated | `GetTokenInformation(TokenElevation)` | |
| Architecture | `IsWow64Process2` (x86-on-x64, ARM64, x64-on-ARM64 emulated) | |
| Start time / uptime | `CreateTime` from A | |
| Runtime | ETW ImageLoad (see D) | never via `EnumProcessModules` (needs VM_READ) |
| Windows (main window titles, hung state) | `EnumWindows` + `GetWindowThreadProcessId` + `IsHungAppWindow` | user; UWP via `CoreWindow` child |
| Exit code | ETW `ProcessStop.ExitCode` | fallback "(unknown)" — we never hold a handle open to read it |

## C. GPU (GpuPoller, PDH, every 2 s)

| Metric | Counter path | Notes |
|---|---|---|
| GPU % per engine | `\GPU Engine(pid_<pid>_luid_*_phys_*_eng_*_engtype_<3D|Copy|VideoDecode|VideoEncode|Compute…>)\Utilization Percentage` | sum per `(pid, engtype)`; "GPU %" headline = max engine (Task Manager convention) |
| VRAM dedicated / shared | `\GPU Process Memory(pid_<pid>_luid_*_phys_*)\Dedicated Usage`, `\Shared Usage` | bytes |

Implementation: `PdhOpenQuery` → `PdhAddEnglishCounter` with wildcard paths → `PdhCollectQueryData` twice (rate
counters) → `PdhGetFormattedCounterArray`. Instance names are re-expanded every 10 s or when the process set changes.
Counters exist on Windows 10 1709+ with WDDM 2.x drivers; absence → metric "N/A", sensor `Unavailable`. Privilege: user.
Cost: the wildcard expansion is the expensive part (~5 ms per 100 instances); never more often than 10 s.

## D. ETW (EtwHub, admin) — see `05_COLLECTOR.md` for session design

| Metric | Provider / keyword | Events | Fields used |
|---|---|---|---|
| Network in/out per process, per endpoint, proto | Kernel `NetworkTCPIP` | `TcpIpSend/Recv(+IPV6)`, `UdpIpSend/Recv(+IPV6)`, `TcpIpConnect/Accept/Disconnect/Retransmit/Fail` | `ProcessID`, `size`, `saddr`, `daddr`, `sport`, `dport`, `connid` |
| Real disk read/write per process | Kernel `DiskIO` (+ `Thread` so `IssuingThreadId` resolves to a process) | `DiskIORead/Write`, `DiskIOFlush` | `TransferSize`, `DiskNumber`, `IrpFlags`, `ProcessID` (resolved) |
| File-level hot spots, observed data folders | Kernel `FileIO` + `FileIOInit` (**sampled**) | `FileIORead/Write/Create/Delete/Rename/SetInfo` | `FileName`, `IoSize`, `ProcessID` |
| Process lifecycle | Kernel `Process` | `ProcessStart/Stop/DCStart/DCEnd` | `ImageFileName`, `ParentID`, `ExitCode`, `SessionID`, `CommandLine` (when present) |
| Runtime detection, driver loads (anti-cheat detection) | Kernel `ImageLoad` | `ImageLoad/ImageDCStart` | `FileName`, `ProcessID` |
| DNS per process | `Microsoft-Windows-DNS-Client` (`{1C95126E-7EEA-49A9-A3FE-A378B03DDB4D}`) | 3006 (query sent), 3008 (query completed), 3020 (cache) | `QueryName`, `QueryType`, `QueryOptions`, `QueryStatus`, `QueryResults`, header `ProcessID` |

Notes:
- Kernel-Network attributes receives to the **socket-owning** process. HTTP.sys-served traffic attributes to `System`.
  QUIC is UDP 443. Loopback is counted and flagged (`daddr` in `127/8`, `::1`). VPN/tunnel traffic is counted once at
  the TCP/IP layer (pre-encryption); the tunnel adapter's own process is not double-counted because the kernel does not
  emit a second TCP event for the encapsulated flow.
- `QueryResults` (3008) is a `;`-separated list of entries like `type: 5 cname.target.net` or bare addresses (IPv4 may
  appear as `::ffff:a.b.c.d`). Parser must be tolerant and tested against captured samples (`tests/.../Dns/samples.txt`).
- `ProcessStart` is used to create instances *before* the poller sees them (short-lived processes) and `ProcessStop`
  for exit codes; `DCStart` rundown at session start seeds the table.

## E. Network (ConnectionPoller, 1 Hz, user)

| Fact | API |
|---|---|
| TCP v4/v6 connections with PID and state | `GetExtendedTcpTable(TCP_TABLE_OWNER_PID_ALL)` / `…TCP6…` |
| UDP v4/v6 endpoints with PID | `GetExtendedUdpTable(UDP_TABLE_OWNER_PID)` / `…UDP6…` |
| Listening ports | TCP rows in `LISTEN`; UDP rows (all are "listening") |
| Direction | outbound if the connection's first `TcpIpConnect` came from this PID, inbound on `TcpIpAccept`; fallback: remote port < local ephemeral range → outbound |
| Per-connection RTT, retransmits, bandwidth (**on demand, admin**) | `SetPerTcpConnectionEStats`(`TcpConnectionEstatsPath`, `…Data`, `…Bandwidth`, enable) then `GetPerTcpConnectionEStats` ROD structs: `SmoothedRtt`, `MinRtt`, `MaxRtt`, `PktsRetrans`, `DataBytesIn/Out`, `OutboundBandwidth/InboundBandwidth`; IPv6 via `SetPerTcp6ConnectionEStats` |
| Interface per local address, type (Ethernet/Wi-Fi/tunnel) | `GetUnicastIpAddressTable` + `GetIfTable2` (`Type`, `Description`, `MediaType`); refreshed on `NotifyUnicastIpAddressChange` |
| Metered / cost | WinRT `NetworkInformation.GetInternetConnectionProfile().GetConnectionCost()` |
| Hostname for remote IP | per-app DNS map (D) › global DNS map › reverse DNS (`DnsQueryEx` PTR, **opt-in**) › IP literal |
| A/AAAA/CNAME chain, TTL, DoH status for a hostname (on expand) | `DnsQueryEx` with `DNS_QUERY_STANDARD`; chain from `DNS_RECORD` list; DoH from `GetInterfaceDnsSettings` |
| Geo / ASN (**opt-in offline DB**) | DB-IP Lite mmdb via `MaxMind.Db` reader; monthly update from GitHub Releases asset |
| History backfill before install (**spike S8**) | WinRT `ConnectionProfile.GetAttributedNetworkUsageAsync` (SRUM, hourly, ~30–60 days) |

ESTATS is enabled only for connections of the app currently shown in the Network tab and disabled on leave; enabling
costs a few µs per connection and is admin-only, so Lite mode shows RTT as "N/A".

## F. Disk (DiskScanner, background) — full spec in `09_DISK_SCANNER.md`

| Fact | Source |
|---|---|
| Install footprint (logical, on-disk, files) | recursive enumeration `FindFirstFileExW(FindExInfoBasic, FIND_FIRST_EX_LARGE_FETCH)` |
| On-disk size | cluster-rounded logical size; `GetCompressedFileSize` for `COMPRESSED`/`SPARSE`; 0 for `RECALL_ON_DATA_ACCESS`/`OFFLINE` placeholders |
| Data locations | FileIO sampling (D) clustered by directory + catalog `data_dirs` + convention candidates (03 §enrichment) |
| Reclaimable cache | catalog `cache_dirs` + directory-name heuristics (`Cache`, `Code Cache`, `GPUCache`, `ShaderCache`, `DXCache`, `logs`, `crashes`, `Temp`) — labeled "estimate", never deleted by us |
| Per-drive capacity/used | `GetDiskFreeSpaceExW` per volume |
| Change tracking | USN journal (`FSCTL_QUERY_USN_JOURNAL`, `FSCTL_READ_USN_JOURNAL`), admin, NTFS/ReFS only |
| Growth | daily `disk_snapshots` rows |

## G. Static details (Details tab, on demand, user)

| Fact | Source |
|---|---|
| Autostart: Run/RunOnce keys (HKCU/HKLM ×2 views), Startup folders, Task Scheduler (`ITaskService` enumeration, actions whose path is under the install root), services (`EnumServicesStatusExW` + `QueryServiceConfigW` binary path) | registry / COM / SCM |
| Firewall rules for the app's executables | `INetFwPolicy2.Rules` filtered on `ApplicationName` |
| File associations / protocol handlers | `HKCR\*\shell\open\command` and `HKCR\<proto>\shell\open\command` whose command path is under the install root (scan once, cached 24 h) |
| Shell / COM extensions | `HKCR\CLSID\*\InprocServer32` default values under the install root |
| Crash history | Event Log `Application` 1000/1001 filtered by image name; WER folders `%LOCALAPPDATA%\Microsoft\Windows\WER\ReportArchive` |
| Package manager provenance | winget (`winget list --id`, opt-in, slow), Scoop manifest, Chocolatey `.nuspec` |

## H. Not available / explicitly unsupported in v1

| Wanted | Why not | Alternative |
|---|---|---|
| Foreground/focus time per app | needs a shell hook or polling `GetForegroundWindow` (cheap; roadmap v1.1) | usage time = process lifetime |
| Per-process power usage | SRUM/E3 only readable via ESE with admin and VSS gymnastics | roadmap; S8 may find a WinRT route |
| Per-packet capture | no public pktmon API; Npcap licensing; payload privacy | v2 behind S6 (`23_NON_GOALS.md`) |
| Per-process handles / open files | `NtQuerySystemInformation(SystemHandleInformation)` + `NtQueryObject` hangs on pipe handles and needs `DUP_HANDLE` → forbidden | FileIO sampling shows recently touched files |
| Module list | needs `VM_READ` | ETW ImageLoad since Agent start |
| "Is this app safe?" | not a verdict engine | signature status + hash + external VirusTotal link (opens browser, no API call) |

## Privilege matrix (summary)

| Capability | Lite (user) | Agent (admin) |
|---|---|---|
| Process table, CPU, RAM, I/O, threads, handles | own session, own user | all sessions (filtered to own by default) |
| Command line / token of other users' processes | no | yes (except PPL) |
| GPU counters | yes | yes |
| Connections with PID | yes | yes |
| Network bytes per process, DNS per process, real disk I/O, file hot spots, exit codes, runtime detection | **no** | yes |
| ESTATS (RTT/retransmit) | no | yes |
| USN incremental scan | no | yes |
| History persistence | no | yes |
