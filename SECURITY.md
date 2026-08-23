# Security policy

## Reporting

Email code@poli0981.dev with "AppLedger security" in the subject. Please do not open public issues for
vulnerabilities. Expect an acknowledgement within 7 days and a fix or mitigation plan within 30 days for confirmed
issues. Credit is given in the release notes unless you prefer otherwise.

## In scope

- Anything that lets a **standard-user process** make the elevated Agent do more than read: path policy bypass
  (canonicalization, junctions, 8.3 names, `\\?\` prefixes, ADS), pipe message handling (framing, oversized frames,
  malformed JSON), peer verification, Scheduled Task definition or update flow abuse (`docs/11_SAFETY_POLICY.md`).
- Any way the Agent opens a process with rights beyond `PROCESS_QUERY_LIMITED_INFORMATION`, or touches a Tier-2 process.
- Any data leaving the machine that is not listed in `legal/PRIVACY_POLICY.md`, or any stored field missing from the
  data inventory in `docs/12_PRIVACY_AND_RETENTION.md`.
- Catalog signature verification bypass (loading an unsigned or downgraded catalog).
- Unredacted sensitive data in default-level logs.

## Not vulnerabilities (by design)

- UAC is not a security boundary; the Agent runs as the same user, elevated, by that user's one-time consent.
- A process running as the same user can read `%LOCALAPPDATA%\AppLedgerData` — that is the Windows user model; we
  document it and offer purge and policy controls rather than claiming isolation.
- Releases are not Authenticode-signed (SmartScreen warning); verify SHA-256 checksums and the minisign signature
  published with each release.

## Supported versions

Only the latest release receives fixes. Pre-release versions are best effort.
