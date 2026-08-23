# Identity fixtures (S2)

One JSON file per scenario from `docs/03_APP_IDENTITY.md` §Test fixtures. Together they are the S2 gate
(`docs/20_SPIKES.md`): **≥ 95 %** of expected `app_id` matches and **zero** games merged into a launcher.

The fixtures were written **before** the resolver, on purpose. A gate whose test data is authored after the
implementation measures the implementation against itself. `IdentityFixtureTests` already runs today and
keeps them honest — they parse strictly, parents precede children, Tier-2 entries carry no handle-derived
facts, and every expected `cat:` id exists in the shipped catalog. The scoring test is `Skip`ped until
`IdentityResolver` lands (`docs/21_ROADMAP.md` v0.3), so enabling it is a red-to-green step, not a rewrite.

## Shape

```jsonc
{
  "name": "01_chrome",
  "scenario": "One sentence saying what this file proves.",

  // The synthetic environment the resolver sees instead of the live registry, store and manifests.
  "indexes": {
    "uninstall": [ { "key": "Google Chrome", "displayName": "Google Chrome",
                     "installLocation": "C:\\Program Files\\Google\\Chrome\\Application",
                     "displayIcon": null, "publisher": "Google LLC" } ],
    "msix":  [ "Microsoft.WindowsTerminal_8wekyb3d8bbwe" ],
    "steam": [ { "id": "1091500", "installLocation": "…\\steamapps\\common\\Game", "name": "Game" } ],
    "epic": [], "gog": [], "itch": [], "scoop": [], "choco": [],

    // Signals the anti-cheat and svchost scenarios need.
    "loadedDrivers": [ "EasyAntiCheat.sys" ],
    "services": [ "EasyAntiCheat" ]
  },

  // Ascending createTime; a parent must appear before its child, or be absent to model one that exited.
  "processes": [
    {
      "pid": 4100,
      "createTime": 1000,
      "imageName": "chrome.exe",                       // always known: it comes from ETW
      "image": "C:\\…\\chrome.exe",                    // omit for a Tier-2 process: no handle, no path
      "cmdline": "\"C:\\…\\chrome.exe\"",              // omit for Tier 2 or when policy redacts it
      "signer": "Google LLC",
      "packageFamily": null,
      "parentPid": null, "parentCreateTime": null,
      "productName": null, "companyName": null,

      "expect": "cat:chrome",                          // the id a correct resolver must produce
      "expectTier": null,                              // 2 for zero-touch cases
      "expectConfidence": null,                        // set where the confidence table is the point
      "why": "signer + exe + install root all match the catalog rule"
    }
  ],

  // Applied before resolution; fixture 12 uses this.
  "overrides": [ { "kind": "split", "match": "--type=utility", "value": "user:0123…" } ]
}
```

Unknown fields are rejected, so a typo fails loudly instead of silently weakening a scenario.

## Rules for a new fixture

- **A bug fixed in the resolver adds a fixture here first**, red before green (`docs/19_TESTING.md`
  §Regression rules).
- `why` is for humans. Say what makes the case interesting, not what the code does.
- A Tier-2 process (`expectTier: 2`) must have **no** `cmdline` and **no** `packageFamily`: we never open a
  handle to one, so a fixture that supplies those facts is testing something that cannot happen.
- Paths use the `C:\Users\fixture\…` profile so nothing personal ever lands in the repo.
- `cat:` expectations may name an `apps[].id` **or** an `anticheat[].id` — both mint `cat:<id>`
  (`docs/13_CATALOG_RULES.md` §Matching semantics).
