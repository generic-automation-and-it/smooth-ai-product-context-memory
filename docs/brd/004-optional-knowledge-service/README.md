# BRD-004: Optional knowledge assistance

| | |
|---|---|
| **Document** | Business Requirements Document |
| **Status** | Scope authorized by the requesting practitioner; implementation acceptance in progress. No upstream owner acceptance is asserted. |
| **Owner** | Product owner / practitioner |
| **Last updated** | 2026-10-03 |
| **Extends** | [BRD-001](../001-context-memory/) and its existing requirement space at BR-47 |
| **Related** | [HLD-008 — Optional knowledge service and additive core contracts](../../hlds/008-optional-knowledge-service/) |

This document states business outcomes. The detailed approved feature scope remains [the PRD](../../optional-knowledge-service/PRD.md); technical choices and contracts belong to HLD-008. Document authorization is distinct from successful implementation or release.

## 1. Problem and outcome

A working assistant should spend its attention on the practitioner's task while relevant product knowledge is retrieved with its conditions and exceptions. Learning should be captured from attributed evidence without requiring that assistant to operate the internal memory workflow. Existing direct use must remain available against the same knowledge.

## 2. Users and scope

The single practitioner and their working assistant are the users. Assistance is optional and explicitly enabled. It does not introduce a second authoritative knowledge store, multi-user sharing, unattended repository ingestion, or a requirement for other installations to adopt it. Existing core business requirements still apply.

## 3. Requirements

**BR-47 — Optional assistance with direct continuity.** The practitioner may select assistance while retaining direct access to the same knowledge. Accepted when: disabling or losing assistance leaves direct retrieval, capture and already incorporated knowledge usable; unconfigured installations do not require external model credentials. Traces KS-01–KS-03.

**BR-48 — Grounded, useful context.** Returned knowledge retains necessary conditions, exceptions, applicability, conflicts and lifecycle, with traceable evidence. Accepted when: exact and paraphrased questions recover the relevant claims; absent evidence, dependency failures and unexamined questions are distinguished; document approval cannot imply shipped behavior. Traces KS-04–KS-07.

**BR-49 — Atomic, compared learning.** Attributed messages become independently meaningful claims after comparison with existing knowledge. A necessary condition belongs with its rule. Accepted when: both modes agree on shared atomicity examples, mixed existing/new claims are handled correctly, failed comparison never becomes an unconditional create, and every item beyond a processing batch is committed, skipped or explicitly deferred. Traces KS-08–KS-09.

**BR-50 — Evidence and authority remain distinct.** New sources may substantiate an existing claim without rewriting its history. Suggestions, document approvals, intended changes, disagreements and observed implementation keep their intended meaning. Accepted when: earlier evidence remains inspectable; retries do not manufacture versions; high model confidence or approval of a parent document does not approve draft children or establish deployment. Traces KS-09–KS-11.

**BR-51 — Durable and recoverable learning.** Acknowledged work survives interruption and cannot silently duplicate on retry. Restoring earlier knowledge must not silently reapply later work. Accepted when: interrupted writes reconcile safely, recovery explicitly accounts for prior work and restored knowledge, and repeat recovery requests do not repeat incorporation. Traces KS-12.

**BR-52 — Bounded and protected processing.** Assistance operates within a shared allowance and removes detected secrets before external processing or persistence. Accepted when: retries and restart do not reset spending, all necessary boundaries use the same detection policy, and detector failure prevents unsafe progression. Traces KS-13–KS-14.

**BR-53 — Honest operational and recall measures.** The practitioner can distinguish direct recall, assisted recall and internal capture comparison. Accepted when: one caller request is distinguishable from its internal searches, existing metrics retain their documented meaning, and failures, usage and deferred work are visible without revealing private content. Traces KS-15 and KS-04.

## 4. Acceptance and limitations

Independent implementation review, real-store recovery tests and live-provider semantic evaluation are required. Retrieval quality, caller context, total provider usage and latency are separate measures; lower caller context is not evidence of lower cost. Pattern detection does not promise recognition of every possible secret. The service does not settle the existing open owner decisions concerning untracked capture identity or accepting designs over draft children.

## 11. Related designs and evidence

[HLD-008](../../hlds/008-optional-knowledge-service/) owns this BRD's design and additive core contracts. [Execution matrix](../../optional-knowledge-service/EXECUTION.md), [collaborator amendment](../../optional-knowledge-service/COLLABORATOR-REVIEW.md), [owner-decision trace](../../optional-knowledge-service/OWNER-DECISIONS.md) and [validation](../../optional-knowledge-service/VALIDATION.md) record implementation status. None is a declaration that pending release gates passed.