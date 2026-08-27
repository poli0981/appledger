# minisign test corpus

Test-only key material and signatures for the catalog signature path (`docs/13_CATALOG_RULES.md` §Signing &
verification). Shared by two test projects, which is why it lives here rather than under one of them:

- `AppLedger.Core.Tests` exercises **parsing** (`Core/Catalog/MinisignSignature.cs`) — file shape, base64 blobs,
  algorithm tag, key id, trusted-comment line.
- `AppLedger.Infrastructure.Tests` exercises **verification** (Ed25519 + BLAKE2b-512 via NSec, behind `ICatalogVerifier`).

## Files

| File | What it is | A correct verifier must |
|---|---|---|
| `sample.json` | the smallest schema-valid catalog | parse strictly, accept |
| `test.pub` | minisign public key, key id `05E0E1316342AA8C` | be the trusted key in tests |
| `test-wrong.pub` | a second, unrelated public key, key id `D35927E1F7DC5C7A` | never be trusted |
| `sample.json.minisig` | prehashed (`ED`) signature over `sample.json` | **accept** |
| `sample.json.legacy.minisig` | legacy (`Ed`) signature over the raw bytes | **reject** — `docs/13` requires prehashed `ED` |
| `sample.json.corrupt.minisig` | valid shape, one flipped signature byte | **reject** |
| `sample.json.wrongkey.minisig` | correctly signed, but by `test-wrong` | **reject** on key-id mismatch, before any crypto |
| `test.seed`, `test-wrong.seed` | the raw 32-byte Ed25519 seeds, base64 | regenerate the corpus |

## Regenerating

The corpus is produced deterministically from two fixed phrases, so regenerating yields byte-identical files and an
unrelated diff means something changed that should not have. The generator is a throwaway .NET file-based app using
NSec (the same library Infrastructure uses); the `minisign` CLI is **not** required for development. See
`docs/24_ADR.md` §Findings for why.

## Not a release key

The release secret key exists only as the GitHub Actions secret `CATALOG_MINISIGN_KEY` (`docs/18_CI_CD.md`) and on the
maintainer's machine. Nothing in this folder ever signs a shipped catalog: the key embedded in
`AppLedger.Infrastructure` is `6ED9A5D305231FDB`, and `CatalogLoaderTests` asserts it is neither of the two key ids
above.
