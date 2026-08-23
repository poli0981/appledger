# Disclaimer

AppLedger is free software released under the GNU General Public License v3.0 only (`LICENSE`). It is provided
**"as is", without warranty of any kind**, express or implied, including but not limited to the warranties of
merchantability, fitness for a particular purpose and non-infringement (GPL-3.0 §15–16).

## What AppLedger is and is not

- AppLedger **displays** information about applications on your own computer. It does not kill, uninstall, block,
  clean or modify anything (`docs/23_NON_GOALS.md`).
- It is **not a security product**. A "Valid" signature badge, a host list or an autostart entry is a fact, not a
  verdict. Use a dedicated security tool for threat decisions.
- Numbers are **measurements with known error bands**, stated on each number's tooltip. Examples: per-app network
  bytes are TCP/UDP payload and differ from adapter counters by header overhead; short-lived connections may be
  unattributed; processes that resolve DNS internally show IP addresses instead of names; disk "on-disk" sizes for
  cloud placeholders and compressed files follow Windows' reporting.
- History exists only while the background Agent ran. Time before installation is not covered unless a future
  opt-in back-fill feature is enabled.

## Elevated background Agent

During onboarding you approve one UAC prompt that registers a Scheduled Task running `AppLedger.Agent.exe` with
highest privileges at logon for your user. It observes processes with query-only rights, reads Event Tracing for
Windows, reads file sizes, and writes only its own data under `%LOCALAPPDATA%\AppLedgerData`. You can pause it,
stop it, or remove the task at any time from Settings. Details: `docs/01_ARCHITECTURE.md`, `docs/11_SAFETY_POLICY.md`.

## Games and anti-cheat

AppLedger never injects code, installs drivers, hooks windows or opens game processes with memory-read rights;
processes associated with known anti-cheat systems are observed in a "zero-touch" mode (no process handle at all).
We document our evidence (`docs/20_SPIKES.md` S7) but **cannot guarantee** how any third-party anti-cheat treats a
system that runs monitoring software. You use AppLedger alongside online games at your own risk. AppLedger contains
no evasion features and will never add any.

## Third-party components and data

Third-party libraries are listed with their licenses in `THIRD_PARTY_NOTICES.md`. Optional IP geolocation uses the
DB-IP Lite database (CC BY 4.0) downloaded only on request. Product, company and game names belong to their owners
and are used for identification only.

## Liability

To the maximum extent permitted by law, poli0981 and contributors are not liable for any damages arising from
the use of AppLedger, including lost data, lost games sessions, account actions by third parties, or decisions taken
on the basis of displayed information.

Contact: contact@poli0981.dev · Source: https://github.com/poli0981/appledger · Last updated: {{RELEASE_DATE}}
