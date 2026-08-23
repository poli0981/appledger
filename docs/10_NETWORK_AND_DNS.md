# 10 — Network & DNS

Goal: per-app bytes in/out, per-host breakdown, live connection quality, and DNS visibility — passively, with the
privacy defaults from `12`.

## Byte attribution (ETW Kernel-Network)

- `TcpIpSend/Recv` (+IPv6) and `UdpIpSend/Recv` (+IPv6) carry `ProcessID` and payload `size`. Sum per `(pid,createTime)`
  → app. Counted at the TCP/IP layer (payload, no headers), so totals are a few percent below adapter counters —
  the tooltip says so. S3 validates < 10 % over an hour.
- Classification per event: `proto` (tcp/udp), `quic` when udp and remote port 443, `loopback` when `daddr ∈ 127/8, ::1`
  or `daddr == saddr`; `iface` via the local address → interface map.
- Per-endpoint accumulation key `(proto, remoteIp, remotePort)`; cap 2 000 per app (LRU → `(other)`).
- Direction of a connection: `TcpIpConnect` (outbound) / `TcpIpAccept` (inbound) seen for the `connid`; UDP is "flow".
- Processes that exit before the poller sees them still get attributed because `ProcessStart` creates the instance
  from ETW; bytes that arrive after `ProcessStop` (rare, kernel completion) are attributed to the last known instance.

## Connections (IP Helper, 1 Hz)

`GetExtendedTcpTable(TCP_TABLE_OWNER_PID_ALL)` + `GetExtendedTcp6Table` + `GetExtendedUdpTable(UDP_TABLE_OWNER_PID)` +
`GetExtendedUdp6Table`. Rows keyed by the 5-tuple; state transitions tracked to emit `ListenOpened` events
(new `LISTEN` row whose local port is not in the app's known-listeners set; ephemeral-range ports on 0.0.0.0 are
ignored unless persisting > 60 s). Each row is joined with the endpoint accumulator (bytes) and the DNS map (host).

## Connection quality (ESTATS, on demand)

When the Network tab of app X is visible, the UI subscribes with `estats: true`. The Agent enables
`TcpConnectionEstatsPath`, `TcpConnectionEstatsData`, `TcpConnectionEstatsBandwidth` on each TCP row of X
(`SetPerTcpConnectionEStats` / `SetPerTcp6ConnectionEStats`), reads `GetPerTcpConnectionEStats` each second
(`SmoothedRtt`, `MinRtt`, `MaxRtt`, `PktsRetrans`, `DataBytesIn/Out`, `OutboundBandwidth`, `InboundBandwidth`), and
disables on unsubscribe or row disappearance. Never enabled system-wide. Lite mode: N/A.

## DNS

### Learning (ETW `Microsoft-Windows-DNS-Client`)
- 3006: `QueryName`, `QueryType` + header `ProcessID` → `DnsQuery{app, name, type, tsUtc}` (counted per app per day:
  `queries`, `nxdomain`, `servfail`, `timeouts`; names persisted per the app's host policy).
- 3008: `QueryName`, `QueryStatus`, `QueryResults` → parse results; for each address → `ip_names[ip] = name`
  (**global** map, not per app — the map alone must not reveal which app resolved what) and the per-app live map
  (memory) used to label connections.
- 3020 (cache hit) → same handling as 3008 when present.
- Parser (`DnsResultsParser`) is tolerant: entries separated by `;`, `type: N value` pairs, bare IPv4/IPv6, `::ffff:`
  mapped v4; unknown tokens ignored; unit-tested against `tests/.../Dns/samples.txt` captured from real machines.

### Explicit lookups (on expand, `ResolveHost`)
`DnsQueryEx` with `DNS_QUERY_STANDARD` for A, AAAA, CNAME (and HTTPS/SVCB type 65 if present) → chain built from the
returned record list; TTLs; status; DNS server and DoH from `GetInterfaceDnsSettings`. Cached 10 min. This is a
lookup the user explicitly asked for — it is the one place AppLedger originates DNS traffic itself, stated in `12` §Network calls.

### Hostname for an IP (precedence)
1. The app's own live DNS map (it resolved that name).
2. Global `ip_names` (another app resolved it — e.g., a launcher resolving for a game).
3. TLS SNI — **not in v1** (needs packet mode).
4. Reverse DNS (`DnsQueryEx` PTR) — **opt-in** setting, off by default (generates traffic and is often misleading).
5. IP literal.

## Host policy (applied at rollup and on the live stream)

| Policy | Stored `host` | Live display |
|---|---|---|
| `none` (default for category **Browser**; for `sys:*`) | `(hidden)` single bucket | no host column |
| `etld1` (default for everything else) | registrable domain from the Public Suffix List (`cdn.discordapp.com` → `discordapp.com`) | registrable domain |
| `full` | full name | full name |

Plus: cap 200 hosts per app per local day (overflow → `(other)`); `(ip)` bucket for unnamed IPs is one row per
`/24` (v4) or `/48` (v6) prefix, not per IP, to bound cardinality. The PSL (`public_suffix_list.dat`, MPL-2.0) ships
in the package and updates with the catalog.

## Interfaces

`GetUnicastIpAddressTable` → local address → `InterfaceLuid`; `GetIfTable2` → `Type` (`IF_TYPE_ETHERNET_CSMACD`,
`IF_TYPE_IEEE80211`, `IF_TYPE_TUNNEL`, `IF_TYPE_PPP`, `IF_TYPE_WWANPP`), `Description` (WireGuard/Tailscale/OpenVPN TAP
adapters report as Ethernet — a name heuristic list in the catalog maps them to `tunnel`). Refreshed on
`NotifyUnicastIpAddressChange`/`NotifyIpInterfaceChange`. Metered: WinRT `ConnectionCost.NetworkCostType != Unrestricted`.
`iface_mask` persists which kinds carried the app's traffic each day.

## Geo / ASN (opt-in, offline)

Settings › Data › "Download GeoIP database": fetches the DB-IP Lite country (+ASN lite) mmdb from the project's
GitHub Releases (mirrored monthly by CI, CC BY 4.0 attribution shown in Settings and `THIRD_PARTY_NOTICES`), verified by
SHA-256 in the release manifest. Lookups via `MaxMind.Db` (Apache-2.0) at display time only; nothing geo-related is
persisted. If not downloaded, the column is absent.

## History backfill (S8)

`ConnectionProfile.GetAttributedNetworkUsageAsync(start, end, NetworkUsageStates)` per connection profile returns
hourly per-attribution usage from SRUM for roughly the last 30–60 days, including before AppLedger was installed.
If S8 confirms desktop (non-packaged) processes are attributed by executable path, the Agent imports them once into
`metrics_1h.net_in/net_out` with `degraded=2` (meaning "backfilled, other columns absent") and maps `AttributionId` →
`app_id` through the resolver's path rules. Otherwise the feature is dropped (roadmap note).

## Events

- `NewHost`: first time an `etld1`/`full` host appears for a non-browser, non-`sys` app (after the app's first 24 h of
  history to avoid a flood at install). Severity notice.
- `ListenOpened`: new listening TCP port / bound UDP port on a non-loopback address. Severity notice.
- Daily summary event (info) with bytes/hosts/queries counts.

## Packet mode — deferred to v2 (S6)

Flow/header capture with SNI would improve hostnames and protocol detection, but: no public pktmon API (CLI/ETL only),
Npcap has redistribution limits (OEM license), payloads are a privacy hazard, and 5-tuple→PID attribution races on
short flows. The managed-only constraint makes pktmon's ETW path (`Microsoft-Windows-PktMon`) the only candidate; S6
evaluates it. Until then the Network tab says "flow-level data from the TCP/IP stack; no packet capture".

## Tests

- `DnsResultsParser` samples; PSL eTLD+1 cases (`co.uk`, `github.io`, IDN, IP literals); host cap and `(other)` overflow;
  loopback/QUIC classification; direction inference; `ListenOpened` debounce; policy application in both live and rollup
  paths (a test that asserts a Browser-category app never yields a hostname string anywhere in the rollup output).
- `Category=Admin`: ESTATS enable/disable on a local TCP echo; Kernel-Network attribution of a known transfer.
