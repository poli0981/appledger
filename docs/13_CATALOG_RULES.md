# 13 — Catalog Rules

Identity, category, helper-process, data-folder, anti-cheat and sensitive-path knowledge is **data**, not code:
`catalog/appledger-catalog.json`, signed with minisign, shipped in the package and updatable from GitHub Releases.
Fixing "Discord is grouped wrong" is a JSON PR, not a release.

## Files

- `catalog/appledger-catalog.json` — the rules (UTF-8, ≤ 4 MB).
- `catalog/appledger-catalog.json.minisig` — detached minisign signature (generated at release by CI from a secret key;
  never committed).
- `catalog/public_suffix_list.dat` — Mozilla PSL (MPL-2.0), bundled, refreshed with the catalog.
- Installed copies: `DataRoot\catalog\` (verified downloads) and the package's `catalog\` (fallback).

## Schema (v1)

```jsonc
{
  "schema": 1,
  "version": "2026.08.0",                 // CalVer YYYY.MM.N — monotonically increasing; older is never loaded over newer
  "generated_utc": "2026-08-22T00:00:00Z",
  "min_app_version": "1.0.0",             // Agent refuses newer schema than it understands (never "downgrades")
  "categories": ["Game", "Browser", "Communication", "DevTool", "Media", "Productivity", "Launcher", "Runtime",
                 "Security", "System", "Utility", "Unknown"],
  "apps": [
    {
      "id": "discord",                    // → app_id "cat:discord"
      "name": "Discord",
      "publisher": "Discord Inc.",
      "category": "Communication",
      "match": {                          // AND across the kinds present; OR within each list (see below)
        "signer": ["Discord Inc."],       // Authenticode subject CN (exact, case-insensitive)
        "exe": ["Discord.exe"],
        "install_root_glob": ["%LOCALAPPDATA%\\Discord"],
        "package_family": []              // MSIX PackageFamilyName(s) — a strong signal on its own (e.g. Windows Terminal)
      },
      "helpers": { "exe": ["Update.exe"], "cmdline_contains": ["--type="] },   // adopted into this app when under root
      "data_dirs": ["%APPDATA%\\discord"],
      "cache_dirs": ["%APPDATA%\\discord\\Cache", "%APPDATA%\\discord\\Code Cache", "%APPDATA%\\discord\\GPUCache"],
      "host_logging_default": "etld1",    // optional override of the category default
      "notes": "Electron; renderer children use --type="
    }
  ],
  "host_rules": [                         // ordered; see 03 §Host rules. Allowed fields per entry:
    //   "rule": fixed | system | service_group | dll_arg_or_system | attach_parent | script_from_cmdline | anticheat_helper | launcher_children
    //   "exe": [names], "exe_glob": [globs], "cmdline_contains": [substrings]   — OR, across fields and within each list
    //   "pid": n, "app_id": "sys:…"                                           — fixed rules only
    { "rule": "fixed", "exe": ["explorer.exe"], "app_id": "sys:explorer" },
    { "rule": "attach_parent", "exe": ["conhost.exe"], "exe_glob": ["*crashpad_handler*.exe"], "cmdline_contains": ["--type="] },
    { "rule": "launcher_children" }
  ],
  "launchers": ["steam", "epic", "gog-galaxy", "battlenet", "ea-app", "ubisoft-connect", "itch", "xbox"],
  "anticheat": [
    { "id": "eac", "name": "Easy Anti-Cheat",
      "services": ["EasyAntiCheat", "EasyAntiCheat_EOS"],
      "drivers": ["EasyAntiCheat.sys", "EasyAntiCheat_EOS.sys"],
      "dirs": ["EasyAntiCheat"], "match_confidence": "driver" },
    { "id": "battleye", "name": "BattlEye", "services": ["BEService"], "drivers": ["BEDaisy.sys"], "dirs": ["BattlEye"], "match_confidence": "driver" },
    { "id": "vanguard", "name": "Riot Vanguard", "services": ["vgc", "vgk"], "drivers": ["vgk.sys"], "match_confidence": "driver" },
    { "id": "mhyprot", "name": "mhyprot (HoYoverse)", "drivers": ["mhyprot2.sys", "mhyprot3.sys"], "match_confidence": "driver" },
    { "id": "faceit", "name": "FACEIT Anti-Cheat", "drivers": ["faceit.sys"], "match_confidence": "driver" },
    { "id": "punkbuster", "name": "PunkBuster", "services": ["PnkBstrA", "PnkBstrB"], "match_confidence": "service" },
    { "id": "gameguard", "name": "nProtect GameGuard", "dirs": ["GameGuard"], "match_confidence": "dir" },
    { "id": "xigncode", "name": "Xigncode3", "drivers": ["xhunter1.sys"], "match_confidence": "driver" },
    { "id": "denuvo-ac", "name": "Denuvo Anti-Cheat", "dirs": ["Denuvo Anti-Cheat"], "match_confidence": "dir" },
    { "id": "ricochet", "name": "Ricochet", "dirs": ["Ricochet"], "match_confidence": "dir" },
    { "id": "vac", "name": "Valve Anti-Cheat", "match_confidence": "none", "notes": "in-process, no driver/service; games flagged via Steam manifest tag when available" }
  ],
  "protected_paths": [],                  // extensions to the built-in Tier-0 minimum (11) — cannot remove built-ins
  "sensitive_paths": [
    { "glob": "%USERPROFILE%\\Documents\\**\\*.kdbx", "kind": "password-vault" },
    { "glob": "%APPDATA%\\Bitwarden\\**", "kind": "password-vault" },
    { "glob": "%LOCALAPPDATA%\\1Password\\**", "kind": "password-vault" }
  ],
  "protected_processes": [],              // extensions to the built-in PPL list (11)
  "tunnel_adapter_names": ["WireGuard", "Tailscale", "TAP-Windows", "OpenVPN", "ZeroTier", "Hamachi", "NordLynx"],
  "system_display_names": { "Dnscache": "DNS Client", "Schedule": "Task Scheduler", "wuauserv": "Windows Update" }
}
```

Strict parsing: unknown fields → reject the file (prevents silent typos), `schema` must equal the Agent's supported
value, `categories` must be a superset of the built-in taxonomy, every `apps[].category` must be in `categories`,
every `host_rules[].rule` must be a known rule kind. A rejected catalog never replaces the last good one
(`CatalogRejected` event).

### Matching semantics (pinned at kickoff — `docs/24_ADR.md` §Findings)

- **`apps[].match`** — a process matches an app when **every kind present and non-empty** matches; within one kind the
  list is **OR**. So `{signer, exe, install_root_glob}` means signer AND exe AND root, while `{package_family}` alone
  is sufficient. This is what makes portable copies fall through to `root:` instead of stealing a catalog id
  (`03_APP_IDENTITY.md` §Test fixtures, case 7: portable 7-Zip from `D:\Tools\7z\` must **not** be `cat:7zip`).
  Schema rule: an entry must carry `package_family`, or `signer`, or **both** `exe` and `install_root_glob` —
  an `exe`-only entry is rejected.
- **`host_rules[]`** — `exe`, `exe_glob` and `cmdline_contains` are **OR**-ed with each other and within each list.
  A rule that needs two conditions to hold at once must be expressed as its own rule kind, not as two fields.

### Glob grammar

Every glob (`install_root_glob`, `data_dirs`, `cache_dirs`, `sensitive_paths[].glob`, `protected_paths`) must be
**rooted** after `%VAR%` expansion — either drive-absolute (`C:\…`) or beginning with the drive-wildcard token
`?:\`, which matches any single drive letter (`?:\Steam` ⇒ `D:\Steam`, never `D:\Games\Steam`). `%VAR%` comes
only from the allow-list `LOCALAPPDATA`, `APPDATA`, `USERPROFILE`, `PROGRAMDATA`, `PROGRAMFILES`,
`PROGRAMFILES(X86)`, `PUBLIC`, `TEMP`. `*` matches within one path component, `**` spans components.

## Category taxonomy

`Game` · `Browser` · `Communication` (chat, mail, voice) · `DevTool` (IDEs, runtimes' tooling, VCS, containers) ·
`Media` (players, editors, streaming/recording) · `Productivity` (office, notes, PDF) · `Launcher` (stores/launchers) ·
`Runtime` (interpreters when no script is identified) · `Security` (AV, VPN clients, password managers) · `System`
(`sys:*`) · `Utility` (everything else with a clear purpose) · `Unknown`.
Category drives the host-logging default (`Browser` → `none`), alert thresholds (`Game` ignores data-growth alerts
below 10 GB), and icons.

## Signing & verification

- Format: [minisign](https://jedisct1.github.io/minisign/) detached signature (`Ed25519`; pre-hashed mode using
  `BLAKE2b-512`, `ED` algorithm tag). Public key `{{CATALOG_PUBKEY}}` embedded in `AppLedger.Infrastructure` as a
  constant and shown in Settings › Catalog.
- **Where the code lives** (pinned at kickoff): parsing a `.minisig`/`.pub` is pure string and base64 work and lives in
  `AppLedger.Core/Catalog/MinisignSignature.cs`; the Ed25519 + BLAKE2b-512 verification needs `NSec.Cryptography`
  and lives in `AppLedger.Infrastructure` behind the Core port `ICatalogVerifier`. Core keeps no crypto dependency.
- Verification: parse the `.minisig` (base64 line 2: `ED` + key id + 64-byte signature; trusted comment line + global
  signature verified too), compute `BLAKE2b-512` of the file, verify with `Ed25519`. Key id must match the embedded
  key; otherwise reject. Test key pair: `tests/fixtures/minisign/` (never the release key).
- CI signs at release with `minisign -S -m appledger-catalog.json -s <secret from GitHub secret>`; the secret key never
  lives in the repo. Rotation: embed the new public key in an app update first, sign with both for one release cycle.
- The bundled catalog is also signed; the Agent verifies even the bundled copy (defense against tampering in `current\`).

## Update flow

1. Weekly (or manual): `GET https://github.com/poli0981/appledger/releases/latest/download/appledger-catalog.json` and `.minisig` (Velopack-style
   GitHub Releases; conditional `If-None-Match`).
2. Verify signature → parse strict → `version` must be newer than the active one → atomically replace
   `DataRoot\catalog\appledger-catalog.json` (write temp + rename).
3. Agent hot-reloads: rebuild indexes, re-resolve live instances whose evidence came from catalog rules, emit
   `CatalogUpdated{from,to}`; history untouched.
4. UI shows the active version and the last error in Settings › Catalog.

## Contribution checklist (PR template "Catalog entry")

- [ ] `id` is lower-case kebab, unique, stable (never renamed once released).
- [ ] At least one **strong** match signal (signer or MSIX/launcher id); `exe`-only matches require `install_root_glob` too.
- [ ] `category` from the taxonomy; `data_dirs`/`cache_dirs` verified on a real install (say which version).
- [ ] For launchers: children that are helpers listed in `helpers`; games are **not** helpers.
- [ ] For anti-cheat entries: driver/service names verified from a real system (`driverquery`, `sc query`), or marked `"match_confidence": "dir"`.
- [ ] `tests/AppLedger.Core.Tests/Catalog/catalog_schema_tests` pass (`dotnet test --filter Category=Catalog`).
- [ ] No personal paths, user names, or machine names in the JSON.

## Seed

The repository ships `catalog/appledger-catalog.json` with a starter set (launchers, major browsers, Discord, VS Code,
OBS, Windows Terminal, Python/Java/Node runtimes as host rules, the anti-cheat list above, sensitive-path globs).
It is validated by the schema test on every build.
