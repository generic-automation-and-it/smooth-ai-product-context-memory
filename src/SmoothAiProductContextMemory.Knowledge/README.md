# Optional knowledge workflow contracts

The standalone host is disabled by default and depends only on core HTTP read and commit interfaces. It does not access the business database. Provider calls share a bounded ledger; capture reservations and exact pending requests are persisted before dispatch.

Capture extracts at most one hundred claims in a bounded response. All returned candidates and comparison progress are durable. A stopped comparison resumes only within its remaining allowance or an explicitly granted continuation allowance; mixed existing/new candidates are committed in one atomic core request after the complete comparison plan is ready. Named unexamined claims account for omitted material inside otherwise covered messages; `hasMore` also discloses an unknown additional tail. The service never reports this material as fully processed. A finite absolute `deadlineUtc` is persisted at first processing and survives restarts, clarification and reconciliation; only an explicit additional deadline allowance extends it. Legacy jobs with prior calls but no deadline use their recorded job time conservatively.

## Evidence and identity

Additional evidence creates a new ordinary core memory version. The previous version, its body and its sources remain immutable. Equivalent claims retain the target's statement, conditions, lifecycle, confidence, tags, facets and validity; coalesced attachments create one new version per target and preserve each source's own attribution. Exact operation replay returns the core receipt without another version. Evidence text is appended to the new body so it remains readable after operational handoff retention expires.

`sourceNamespace` is an optional caller conversation/task identifier. A source reference hashes that namespace and the canonical sanitized full message (ID, role, order, text and timestamp), followed by its original message ID. Without a supplied namespace, the first capture UUID establishes the lineage; `previousCursor` carries that lineage into incremental handoffs. Reused message IDs in unrelated captures cannot suppress evidence, and changed source text or attribution yields a distinct source revision.

This additive service convention does not settle the owner's open questions concerning untracked capture identity or design acceptance. The owner discussion in the root README and AGENTS remains open; direct mode's local ticket convention continues unchanged.

| Evidence category | Meaning | Promotion |
|---|---|---|
| `suggestion` | Proposed behavior or brainstorming | Proposed record |
| `document_approval` | Approval or acceptance of a document/design | Does not establish approved product intent or implementation |
| `approved_intent` | Attributed explicit decision for product behavior | Approved intent, with unshipped conditions retained |
| `observed_implementation` | Attributed affirmative observation/deployment evidence | Observed behavior only when the specific claim is supported |
| `unknown` | Insufficient lifecycle or authority evidence | No inferred promotion |

The source role, exact reference and quoted authority are checked in code. A bounded Jev judgment additionally compares the precise promoted claim with the full attributed message, including negation and limited approval. Current promotion accepts explicit user attribution; tool/source observations remain evidence without automatic promotion. Approval of a design document is distinct from a decision to implement it, and both are distinct from evidence that behavior shipped.

## Restore reconciliation

The service checks corpus epoch before replay and reconciles previously committed operation identities when a receipt is polled. A mismatch pauses the job. Write-authorized operators can then POST `/api/knowledge/captures/{id}/reconcile` with `idempotencyKey`, `previousCorpusEpoch`, `expectedCorpusEpoch` and `acknowledgeRestore: true`.

The journal transaction verifies those pinned epochs, refuses purged evidence and active work, archives prior plans, receipts and pending memory/group requests, and increments a durable generation. The public receipt exposes prior epoch, operation identities, plan hash and budget usage. The worker rereads the restored corpus and compares retained evidence again; it never replays an old pending request. New group and memory operation keys include the generation. Repeating the same reconciliation key cannot launch another generation.

Receipt polling exposes `reconciliationRequired` with the journal epoch, currently observed epoch, affected operations and missing receipts. No direct core credentials are required to formulate the explicit reconciliation request. Unavailable core state produces an unavailable-reconciliation error without invented epoch values. After normal retention, archived transient bodies/quotes and pending payloads are removed alongside the original handoff; summary hashes, operation identities and receipts remain.

Spent tokens, cost estimates and outstanding reservations survive reconciliation. No budget is silently reset. An optional explicit `additionalBudget` uses the same bounded allowance validation as clarification. History is bounded to ten reconciliations and the existing journal storage limit. Once the original handoff is purged, a fresh evidence submission is required.

## Redaction and recall

Both modes use the same direct-skill `redact.py` source. The host packages that file verbatim during build; there is no separately maintained rule copy. It sanitizes request strings, retrieved bodies/metadata, provider inputs and generated outputs before persistence, commit or response. Invocation uses stdin, a process timeout and discarded stderr; detector failure stops processing. Provider and core exception messages are reduced to bounded error classes.

The same `atomicity.py` source is packaged and enforced on initial extraction and any repaired extraction. Shared direct/service fixtures preserve necessary conditions and exceptions as one meaningful claim while rejecting independent bundled claims. A rejected extraction may receive one bounded stronger review, then must pass the same detector again.

Each context response uses one request UUID across internal searches with `recallPurpose=service_retrieval`. Capture comparison uses the durable capture UUID with `recallPurpose=capture_comparison`. Direct core retrieval remains separately attributed. Core-pass counts retain their original meaning; caller-request aggregation is an additional metric.
