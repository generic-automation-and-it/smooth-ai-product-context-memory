# Execution and acceptance

October 3, 2026: implementation, independent acceptance, GitHub feature publication and functional Unraid deployment verified. The final source commit and immutable image digests are recorded in the private deployment repository's `knowledge-deployment.json`; running OCI labels and GitHub refs are checked against that record.

Bob authorized implementation, bounded provider use, branch publication and enablement. The feature stays disabled by default elsewhere. Main owns integration/deployment, Sol builders implemented the components, Astra independently reviewed actual code, tests and stored records, and a fresh Sol caller used only the installed caller skill.

## Scope and source control

- Planning baseline `bbc8553`; upstream refreshed and incorporated through `1fbe8d7` before publication.
- Feature branch `codex/optional-knowledge-service` published to the existing upstream GitHub remote; no main merge or force push. Existing checkouts/remotes preserved.
- Understanding export's independent preflight/unconditional-create defect was checked for an upstream fix before publication. None was found in fetched main/relevant remote branches. This service does not use that path.
- Additive Host contracts are explicit in BRD-004/HLD-008. Existing owner decisions about untracked capture identity and parent/child acceptance remain open; this feature does not settle them by inference.

## Acceptance matrix

Implementation gates use independently inspected tests and stored records. Deployed behavior and the fresh-caller demonstration are verified separately from component tests.

| Requirement | Implementation and evidence | Status |
|---|---|---|
| KS-01 | Disabled host needs no keys; real service outage preserves direct read/write/body access | Verified, including deployed disable/restart |
| KS-02 | Same core records and APIs; actual incorporated evidence survives optional-service outage | Verified against deployed same corpus |
| KS-03 | One caller skill, stable handoff/cursor and receipt/reconcile commands; 12 caller tests | Verified by fresh installed caller |
| KS-04 | Scoped retrieval and graph traversal; 12 real-store attribution tests, separate purposes and caller/pass counts | Verified |
| KS-05 | Six held-out live cases preserve 10/10 required details and valid citations; independent body review | Verified |
| KS-06 | Three epistemic regression cases, limits and partial/absence distinctions; original failed case retained | Verified |
| KS-07 | Bounded conditional workflow, concrete repair reasons, merged evidence, persisted limits | Independently verified |
| KS-08 | Namespaced full-message evidence identities; OWNER-DECISIONS trace and immutable attachments | Independently verified |
| KS-09 | Canonical semantic atomicity, 16 shared fixtures; live conditional claim and four capture regressions | Verified |
| KS-10 | Hostile authority tests; actual proposal, approved-unshipped and correction records | Independently verified |
| KS-11 | Immutable historical evidence, CAS, proposal/conflict protection and same-target coalescing | Independently verified |
| KS-12 | 13 real-store boundary tests; v4 snapshots/v3 compatibility, refusal tests; seven extended real recovery checks | Verified, including deployed restart |
| KS-13 | Durable reservations, absolute deadline across restart, explicit audited continuation; actual SIGKILL | Verified |
| KS-14 | One canonical Python redactor packaged in service; ingress/retrieval/provider/journal/write/receipt tests | Independently verified |
| KS-15 | Readiness, classified failures, model IDs, per-pass legacy metrics and separate caller counts | Verified on deployed host |

Additional contract gates: BRD-004/HLD-008 document additive Host behavior. Mixed existing/new batches and 25 candidates across interruptions are independently tested; omitted tails are explicit, and more than 100 extracted claims are refused. Understanding export is excluded from this feature path. Upstream main checked at `1fbe8d7` on October 3: no new fix for the separate preflight/unconditional-create defect was found.

## Measured evidence and limits

See [VALIDATION.md](VALIDATION.md) for initial failures, repairs, final independent evidence and measurement definitions. All six collaborator amendments are closed in [COLLABORATOR-REVIEW.md](COLLABORATOR-REVIEW.md).

The six frozen held-out cases preserve 10/10 required details in both direct and service responses, with no unsupported claims or invalid citations. Estimated caller context drops 55% and caller tool turns 75%; total service provider tokens increase and aggregate latency is 2.5x. No quality superiority or cost-saving claim is made. The fresh deployed caller uses two context requests and one capture, preserves future intent versus shipment, and replays without a new handoff. Main independently checked actual v1 records and unchanged corpus/usage after service disable/restart.

## Deployment and recovery

One existing Unraid controller manages the core and existing persistent stores. A separate optional service binds loopback port 5143 with an SSH tunnel to the project caller. Protected provider/caller configuration and the persistent SQLite journal remain outside source control. Pinned images are retained in a loopback registry; the private deployment repository retains startup scripts, Unraid templates, rollback and artifact provenance.

The original protected deployment backup includes an offline-verified corpus snapshot SHA256 `7ecf77013033e34ef14d65caf6d1c43af900dad05f40d9db532f95800c70c9cd`. Rollback restores original pinned controller configuration and preserves current data, additive schema and journal. Routine image rollback never restores an old corpus. Seven isolated real recovery checks include SIGKILL, ambiguous commit replay, actual older-snapshot restore and explicit epoch-pinned reconciliation preserving spend and deadline.

Limits: one service instance per journal; no unattended backup schedule or host reboot/power-loss test; no universal caller compaction hook. Small synthetic evaluation does not prove population-level accuracy. Private Bunker knowledge was not imported or published. Direct use remains independent; account for pending receipts before changing capture ownership.
