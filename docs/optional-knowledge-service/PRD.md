# Optional Mimi Knowledge Service product requirements

> Execution status (October 3, 2026): Bob subsequently authorized implementation, GitHub feature-branch publication, bounded real provider calls, and installation/enablement on his existing Unraid stack. Earlier planning-only statements below describe the original document's status. See [EXECUTION.md](EXECUTION.md) for current progress and verified evidence.


Draft for implementation planning. October 3, 2026. Owner: Bob. Decision authority: the [agreed decisions](README.md#agreed-decisions). Technical proposals are in the [technical design](TECHNICAL-DESIGN.md).

Build an optional service that accepts knowledge requests and learning handoffs from a working agent, then performs retrieval and maintenance using Jev, a generative LLM, and Mimi's existing storage. A practitioner can continue using Mimi directly without installing, configuring, or running this layer.

## Problem

Mimi currently asks the calling agent environment to understand and execute its knowledge procedures. Agents select queries, inspect records, compare candidate facts, derive relationships, and preserve lifecycle distinctions. Delegated read and write agents isolate some of this work, but their instructions, coordination, and judgment still live in each caller's environment.

This increases integration effort and makes quality depend on how consistently each agent follows the procedure. It also puts instructions and intermediate material into model contexts that the practitioner primarily wants to use for their actual task.

## Product concept

The main agent remains the user's collaborator. It asks Mimi for relevant context or submits what happened during work. Mimi owns the processing between that request and the corpus: query formulation, retrieval, graph traversal, relevance assessment, synthesis, claim extraction, comparison, versioning decisions, and relationship proposals.

Jev supplies bounded judgments and routing signals. A generative LLM interprets material and writes grounded records or briefings. Application code executes the workflows and enforces their limits. This is an optional processing capability, not a replacement corpus or a second chat application.

## Two supported modes

| | Direct-agent mode | Service-assisted mode |
|---|---|---|
| Knowledge workflow | Existing skills and the calling agent perform it | The optional service performs it |
| Caller setup | Existing Mimi setup | One integration skill and service connection |
| Jev and service LLM credentials | Not required | Required for configured service functions |
| Corpus | Existing Mimi corpus | The same corpus |
| Default | Existing behavior remains the default | Explicitly enabled |

Users can disable the new service without losing incorporated knowledge or changing back to another database. Direct access and the service may coexist, so the implementation must handle concurrent changes. An outage in the new service must not stop direct Mimi use.

## Benefits to deliver and measure

| Benefit | Mechanism | Evidence of benefit |
|---|---|---|
| Smaller main-agent instruction burden | One interface replaces caller-owned maintenance orchestration | Compare required instructions and tool turns for identical tasks |
| More room for the user's task | Intermediate searches and comparisons remain inside the service | Measure main-agent context consumed, separately from service tokens |
| Consistent knowledge handling | One implementation of extraction, comparison, and authority rules | Run the same fixtures through different caller integrations |
| Less integration work | Agents supply requests and evidence rather than storage commands | A fresh caller completes read and capture using only the integration skill |
| More controlled model spending | Jev and code select work within a shared allowance | Record total cost, latency, escalation rate, and quality per request |
| More dependable capture | Durable receipt, explicit changes, and safe retries | Restart and replay tests produce no lost acknowledged work or duplicate commits |
| Better traceability | Each returned claim and stored change retains evidence | Inspect citations, change receipts, and unresolved questions |
| Freedom to choose | Optional service shares the existing corpus | Direct mode passes with the service disabled and all model credentials absent |

Reduced main-context usage is a design objective. Reduced total tokens or money is not guaranteed. No claim of universal correctness, zero hallucinations, or complete corpus coverage is part of the product promise.

## Caller experience

The integration skill teaches two primary actions and receipt handling. The caller should not need to learn group resolution, database schemas, duplicate matching, graph queries, or Mimi's internal specialist skills.

**Ask for context:** Supply the task or question, a known project or work reference, and optionally the desired depth or response size. Mimi returns a cited briefing with supporting records, applicable exceptions, conflicts, coverage gaps, and a stopping reason. A follow-up can target one unresolved area.

**Submit learning:** Supply task context, relevant conversation messages with speaker attribution and order, supporting references, and optional highlights. Mimi acknowledges durable receipt, processes the material, and returns the changes made or questions remaining. The main agent does not have to pre-extract atomic records.

A standing instruction can trigger capture at task completion. Compaction integration is supported only where the caller runtime exposes a usable hook. The service cannot inspect an agent's hidden context or guarantee an end-of-session event by itself. A manual handoff remains supported.

## Requirements

### Optional operation

**KS-01 — Direct mode remains independent.** With the service disabled and no model credentials configured, the existing Host, clients, and skills operate as before. Startup creates no service process, provider client, service port, or provider traffic. The existing deployment is not made dependent on service health.

**KS-02 — One corpus serves both modes.** Incorporated records are ordinary Mimi knowledge and are readable through existing interfaces. Disabling the service does not remove or orphan that knowledge. Service work queues are operational state, not a second authoritative corpus.

**KS-03 — The service owns orchestration.** A caller using the single integration skill can retrieve and submit learning without executing the internal search or maintenance steps. Clarification questions concern meaning or missing evidence, rather than database mechanics.

### Retrieval

**KS-04 — Retrieve against the request.** Interpret scope, identify starting records, and follow relevant relationships. Use existing keyword and structured search initially, with bounded alternative queries. Adding embeddings is a separate retrieval improvement to evaluate, not a prerequisite or an implied existing capability.

**KS-05 — Preserve useful detail.** Include the conditions, exceptions, applicability, and lifecycle information needed to use a claim. Every substantive stored claim has a resolvable citation to a record and version; generated analysis is identified as analysis. A response budget narrows disclosed coverage rather than silently removing an exception from an included claim.

**KS-06 — Report coverage honestly.** For the questions examined, report supported, conflicting, not found in searched material, or not examined because of limits. Stop when the identified questions are adequately supported, further search produces no useful evidence, a dependency fails, or the budget ends. Never turn a timeout into a factual no-match or claim the entire corpus is complete.

**KS-07 — Expand with a purpose.** Each additional search, worker, or stronger-model call addresses a named evidence or reasoning gap. Parallel workers investigate independent questions and return evidence to one synthesis step. Missing evidence prompts retrieval; difficult interpretation may prompt stronger reasoning.

### Capture and authority

**KS-08 — Accept evidence rich handoffs.** Preserve who said what, ordering, supplied timestamps, corrections, and source references. Caller highlights are hints to verify, not authority. Support incremental submissions with a last-acknowledged cursor and stable request identities.

**KS-09 — Compare before committing.** Extract atomic candidate claims and compare them with relevant existing knowledge. Decide among create, version, attach evidence, link, skip, preserve disagreement, or ask for clarification. A failed preflight or comparison must not silently continue as a fresh insert. Exact matching alone is not adequate evidence that no semantic duplicate exists.

**KS-10 — Separate storage from promotion.** Supported additions, proposals, and unresolved disagreements may be preserved automatically under the standing capture instruction. Turning a claim into authoritative knowledge requires evidence of the applicable authority. A model's confidence alone is insufficient. An approved future decision is not proof of current shipped behavior.

**KS-11 — Preserve existing truth during disagreement.** An unapproved proposal or unresolved conflict must not displace an approved current record merely because it was received later. Keep both positions with their evidence and scope. Ask only when the unresolved choice matters; useful capture should not pause for approval of every ordinary record.

**KS-12 — Make acknowledgement durable and retries safe.** A received receipt means sanitized input is durably saved for processing, not incorporated. A processed receipt identifies committed changes, skips, deferred items, and unresolved questions. Retrying after a timeout or process restart must not create a second version or duplicate record for the same accepted operation.

### Cost and data handling

**KS-13 — Enforce a shared budget.** Every request has finite limits for elapsed time, model use, traversal or expansion, and worker concurrency. Retries and parallel branches share these limits. Jev can recommend escalation but cannot enlarge the budget. Routine routing within the configured budget requires no extra user approval.

**KS-14 — Keep data handling simple.** Hosted Jev and LLM processing is accepted for the enabled service. Remove detected credentials and secrets before sending or persisting content, including pending handoffs, derived records, and diagnostic output. Do not introduce per-project provider permission rules or rigid field-by-field context restrictions. Supply enough context to do the work accurately within the request budget. Pattern-based detection is tested defense, not a guarantee of recognizing every possible secret.

**KS-15 — Expose useful operational results.** Report service availability, capture progress, failures, and budget exhaustion clearly. Keep model identities, workflow versions, usage, and step outcomes for diagnosis without logging full knowledge bodies or secrets. Provider failures must not be hidden behind an empty successful answer.

## Authority example

Existing knowledge says refunds require manual review. A user says, “We could automate refunds under $20.” Mimi may capture a proposal with that source; it cannot treat the suggestion as a changed rule.

The user later says, “We decided to do this, but it has not shipped.” Mimi records an approved decision about intended behavior while preserving the current operational rule. Evidence that the change shipped is a separate claim. If source authority or applicability remains ambiguous, the system retains the evidence without silently settling it.

These examples are synthetic and do not describe a private product.

## Scope boundaries

The feature includes optional deployment, one integration skill, bounded retrieval and capture graphs, Jev integration, a generative-model adapter, durable capture receipts, and evaluation against direct use.

It does not include a new PM application, a general autonomous agent swarm, multi-user authorization, a provider permission engine, a full Obsidian migration, guaranteed automatic access to every agent's conversation, or a mandatory replacement of existing skills. A thin MCP adapter may later expose the same operations; changing the transport alone does not provide the knowledge workflow.

## Acceptance and evaluation

Use a fixed corpus and task set to compare the direct workflow with the service. Include terminology differences, empty results, exceptions, cross-topic requests, proposals versus current behavior, contradictions, repeated handoffs, and concurrent edits.

Measure task-relevant fact coverage, preservation of exceptions, unsupported statements, duplicate and incorrect-merge rates, main-agent context and tool turns, total provider usage, latency, and human clarification frequency. Use expected evidence and independently reviewed outcomes; a model's own confidence is not the evaluation target.

Release requires the deterministic safety and compatibility tests in the implementation plan to pass. Live model results must show task quality at least comparable to the direct baseline on the reviewed fixture set and reduced caller orchestration. Publish the actual measurements, including cases that are slower or more expensive. Numeric quality and cost targets must be fixed after the baseline run and before tuning the final evaluation.

## Remaining implementation choices

Exact generative model IDs, calibrated Jev thresholds, and deployment budget values require access and measurement. These are bounded configuration and evaluation tasks, not reasons to reopen the agreed product design. The technical design proposes defaults for architecture and persistence; implementation may refine them with documented evidence.

## October 3 collaborator acceptance amendment

The six requirements in [COLLABORATOR-REVIEW.md](COLLABORATOR-REVIEW.md) are required before deployment completion and refine the existing KS requirements:

- **KS-04/KS-15:** Attribute direct retrieval, service retrieval and capture comparison separately. Preserve existing recall metrics' documented denominators; count a caller request separately from its internal searches.
- **KS-08/KS-09:** One claim includes its necessary conditions and exceptions. Both capture modes must use shared atomicity examples and enforcement that rejects independent bundled claims without rejecting a condition solely because it follows “but.”
- **KS-09/KS-12:** Additional sources must not mutate immutable versions. Evidence attachment creates an explicit new version, coalesces same-target evidence in one operation, preserves existing metadata, and replays without another version.
- **KS-12/KS-13:** Version the receipt-bearing snapshot format and test older-compatible and unsupported/refused inputs. Epoch mismatch must have an implemented, explicit reconciliation path that preserves the journal audit and spending ledger and re-reads the restored corpus before any newly planned write.
- **KS-14:** Both modes execute the same redactor source. Validate all necessary boundaries and fail closed if the detector is unavailable.
- **KS-08/KS-10:** Trace capture identity and design acceptance to existing owner decisions. Approval of a document does not establish approval of every contained product change, acceptance of its draft children, or evidence that behavior shipped. The optional service must preserve these distinctions without claiming to settle upstream open decisions.
