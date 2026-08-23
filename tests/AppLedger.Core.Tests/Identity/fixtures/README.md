# Identity fixtures (S2)

One JSON per scenario in `docs/03_APP_IDENTITY.md` §Test fixtures. Shape:

```jsonc
{
  "name": "01_chrome",
  "indexes": {                      // the synthetic environment the resolver sees
    "uninstall": [ { "key": "Google Chrome", "display_name": "Google Chrome",
                     "install_location": "C:\\Program Files\\Google\\Chrome\\Application", "publisher": "Google LLC" } ],
    "msix": [], "steam": [], "epic": [], "gog": [], "itch": [], "scoop": [], "choco": []
  },
  "processes": [                    // createTime ascending; parent refers to an earlier entry by pid + createTime
    { "pid": 4100, "createTime": 1000, "image": "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe",
      "cmdline": "\"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe\"", "signer": "Google LLC", "expect": "cat:chrome" },
    { "pid": 4132, "createTime": 1002, "parentPid": 4100, "parentCreateTime": 1000,
      "image": "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe",
      "cmdline": "chrome.exe --type=renderer --field-trial-handle=...", "signer": "Google LLC", "expect": "cat:chrome" }
  ],
  "overrides": []                   // user overrides applied before resolution (fixture 12)
}
```

The test loads the seed catalog from `catalog/appledger-catalog.json`, runs `IdentityResolver` over `processes` in
order and asserts `expect` per process. Every grouping bug fixed in the resolver adds a fixture here first.
