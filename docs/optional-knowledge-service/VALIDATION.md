# Validation evidence

This record distinguishes measured results from pending acceptance. Live reports and journals are retained outside Git. No private domain notes or credentials are included here.

## Direct baseline and real-provider retrieval

The independent synthetic fixture SHA256 is `74ffd537a0e72327be3ed77c256ae70e212bf8b9edb6d87087d2117b6bd52a28`. Expectations were frozen before tuning. One development case exposed inadequate service retrieval and was used to correct query planning. Six different held-out questions were then evaluated with real OpenAI and Jev calls, followed by independent answer review.

| Measurement across six cases | Direct workflow | Service |
|---|---:|---:|
| Required details preserved, human review | 10/10 | 10/10 |
| Unsupported claims / invalid citations | 0 / 0 | 0 / 0 |
| Caller context estimate, UTF-8 bytes / 4 | 10,425 | 4,678 |
| Caller tool turns | 24 | 6 |
| Provider input tokens | 5,117 | 30,005 |
| Provider output tokens | 1,639 | 4,087 |
| Aggregate wall seconds | 18.96 | 48.08 |

The service reduced estimated caller context by 55% and tool turns by 75%. It used more provider tokens and approximately 2.5 times the latency. No cost reduction is claimed. The direct baseline uses scoped API search/body retrieval and one independent model synthesis call; capture evaluation is black-box service acceptance, not a measured direct-capture comparison. Small synthetic samples do not establish population-level accuracy.

Literal automated scoring understated direct-answer quality because of valid synonyms. Human review also found one coverage-label mismatch despite a correct brief. A separately frozen three-question epistemic follow-up initially produced two correct answers and one incomplete planning response. That failed case remains a regression gate after correction; reruns are not new held-out evidence.

## Real process failure and restore

`scripts/knowledge-evaluation/crash_acceptance.py` ran against isolated Linux containers with PostgreSQL/AGE, blob storage, SQLite journal and real providers. The successful run used three handoffs; preparation used two additional acknowledged jobs. Independent review inspected the harness and saved report.

- Forced process termination after acknowledgement retained the same handoff identity and completed incorporation after restart.
- A provider reservation was observed in durable SQLite state before termination. After restart it remained charged alongside settled calls: three total charged/reserved calls against a twelve-call limit. Restart did not recover the allowance of an uncertain provider call.
- A temporary SQLite write lock exposed the interval after the core committed and before its journal receipt was saved. After SIGKILL and restart, the operation replayed once; UUID/version/source fingerprints stayed unchanged.
- Identical handoff replay preserved corpus versions; changed payload under the same key returned 409.
- Direct core write, query and body retrieval worked while the optional service was stopped.
- Restoring an actual older core snapshot removed post-snapshot writes and rotated the epoch. Affected service work became `partial` with `corpus_epoch_changed`; continuation returned 409 `reconciliation_required`, and ordinary replay did not reapply it.

These tests intentionally did not disrupt production or exercise server power failure. Journal durability uses SQLite WAL with FULL synchronous mode and depends on the host storage honoring synchronization.

## Capture and implementation checks

Initial live capture failed all four cases and exposed an API source-shape mismatch and extraction authority errors. A second live run correctly retained suggestion and approved-but-unshipped meaning and replayed unchanged. Independent review found duplicate same-target attachment version inflation and metadata degradation; ordered correction remained partial. These findings must be resolved before final capture acceptance.

The upstream baseline passed domain 32, application 209, host 41 and controller 39 unit tests, with one existing host skip. The full baseline infrastructure suite passed 185 tests with eight opt-in skips. Main independently passed all thirteen new real-store corpus-boundary tests. Independent service tests cover hostile authority, scope isolation, legacy bodies, purge, receipt replay and persisted budgets; final counts and remaining live gates will be recorded after repairs.

## Collaborator amendment verification checkpoint

Main independently ran the new real-store recall attribution suite (12 passing) and additive Host API suite (3 passing). Snapshot archive coverage passed 43 tests with one existing platform skip. The independent service suite expanded to 35 passing tests, including real canonical Python atomicity/redaction adapters, immutable per-source attachments, document approval, public-receipt-driven reconciliation, audit retention, 25 mixed claims across budget interruptions, mixed dry-run rejection, and a persisted deadline across journal reopen.

Live capture regression v3 passed all four cases after independent inspection of actual stored bodies, historical sources and versions. Duplicate evidence created one version (v1 to v2), retained confidence 100 and original evidence, and exact replay changed nothing. Suggestion, unshipped approved intent and ordered correction retained their intended meanings. The epistemic regression passed three cases; its earlier first-run2/3 remains recorded and no regression is relabelled held-out.

The new frozen document/atomicity follow-up first run passed the conditional-rule case: one observed claim jointly retained the30-day window and unused-product restriction. Document approval stopped safely with invalid_claim_evidence after two bounded extraction attempts, zero commits. Precise validation diagnostics and constrained category output are under verification. Extended live reconciliation and final enabled deployment remain open.

## Extended recovery acceptance

Independent review closed seven real recovery checks after retained diagnostic attempts. The harness stops the isolated serving core, restores in a one-shot container from the same verified image, then restarts it. Public receipts supply explicit previous/current epochs and missing operation keys. Wrong acknowledgements and epochs are refused. Reconciliation archives old plans/receipts/usage and creates one new generation; uncertain reservations stay charged. The expired deadline required an explicit60-second extension with zero additional calls/tokens/dollars. Repeated reconciliation leaves the generation and corpus fingerprint unchanged.

Independent read-only inspection of both resulting v1 records confirmed approved future intent remains explicitly unshipped and the separate nonshipment observation retains its source-time limitation. Two meaningful records are not duplication; the oracle checks the exact receipt UUID set, versions, no version increments and unchanged replay. Earlier diagnostic failures remain in the private report. No production failure injection was used.

Current independent service suite: 40 passing; builder suite:20 passing. The latest document-approval regression correctly extracts document approval, draft child status and lack of implementation acceptance, but core returned400 because its category allowlist omitted document_approval. Zero writes occurred. Final fix/regression and deployed fresh-caller evidence remain pending.

Document-contract recovery succeeded after adding document_approval to the core's explicit shape-v1 evidence allowlist. Unknown categories and unsupported shape versions remain rejected; 13 validator cases and 3 actual Host API cases pass. An intervening conditional capture advanced the corpus revision, so the stale document plan was refused with zero writes. Explicit time-only continuation re-read/replanned and processed the original capture: 10 total calls, 8,908 input tokens, 2,463 output tokens, prior spend retained. Actual records separately preserve approved document, draft/unapproved child, nonimplementation and no release acceptance; none is promoted to shipped behavior. Exact replay leaves all records and versions unchanged. This is a regression/recovery result; the earlier failures remain recorded.

Document-contract recovery succeeded after adding document_approval to the core's explicit shape-v1 evidence allowlist. Unknown categories and unsupported shape versions remain rejected; 13 validator cases and 3 actual Host API cases pass. An intervening conditional capture advanced the corpus revision, so the stale document plan was refused with zero writes. Explicit time-only continuation re-read/replanned and processed the original capture: 10 total calls, 8,908 input tokens, 2,463 output tokens, prior spend retained. Actual records separately preserve approved document, draft/unapproved child, nonimplementation and no release acceptance; none is promoted to shipped behavior. Exact replay leaves all records and versions unchanged. This is a regression/recovery result; the earlier failures remain recorded.

Final revision-continuation regression passes: a same-epoch direct edit invalidates an uncommitted comparison plan, freshly compares retained validated candidates, preserves named omitted tails and the existing ledger/deadline, and leaves ambiguous pending commits to idempotent receipt replay first. Main independently reran all 40 service component tests.

## Enabled deployment and fresh caller

A fresh Sol agent used only the installed caller skill, contract and public responses against the enabled Unraid service. Its first request returned cited current refund rules with Elm's strict-under-$10 boundary and fraud exception. A synthetic product-owner handoff approving future Spruce refunds strictly under $8 received a durable receipt and one atomic batch creating two v1 records: approved future intent and a separately attributed unshipped report. Follow-up retrieval cited both records, excluded exactly $8, and explicitly said the change had not shipped. Caller replay returned already_acknowledged with the same capture ID. Main independently read the stored bodies, sources, lifecycle and versions; no duplication was found.

The bounded demonstration used two context requests and one capture, totaling 12 internal provider calls, 26,840 input tokens and 3,254 output tokens; estimated provider charge $0.010072211, not a billing measurement. Real OpenAI/Jev ran in the deployed service. Initial Windows shell output replacement of curly apostrophes was corrected by explicit UTF-8 capture; it did not alter server evidence or identity.

Main stopped the optional deployed container, verified direct query/body reads against the same records, then restarted it. All 12 scoped synthetic records, committed receipts and usage were unchanged; the service recovered readiness. The acknowledged job stayed processed with the same two v1 records. Forced failure injection remained isolated. Independent deployment review verified protected permissions, loopback binding, persistent mounts, installed-script/template parity and data-preserving rollback.
