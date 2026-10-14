# NFR-03 — Gate integrity

| Attribute | Boundary |
|---|---|
| **Attribute** | Integrity / write path |
| **Target** | Zero change to the capture gates; an approve re-runs them |
| **Status** | Draft |

The Console neither bypasses nor changes the capture gates (redaction, atomicity, dedup, value gate,
`set --dryrun`). An approve re-runs the full sequence through the bridge.

**Acceptance:** approving a proposal in the Console is equivalent to running the capture path's approve —
same gates, same order, same veto. No write reaches the store that the capture path would have refused.
