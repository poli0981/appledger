# spikes/

One console project per spike (`S1.EtwBudget`, `S2.Identity`, …), each `net10.0-windows10.0.19041.0`, referencing
`src/AppLedger.Collector` / `src/AppLedger.Infrastructure` only. Nothing under `src/` may reference this folder.
Procedures and pass criteria: `docs/20_SPIKES.md`. Results go into the status table there, not into code comments.
