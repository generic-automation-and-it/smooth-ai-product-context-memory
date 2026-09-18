# AGENTS.md - Context memory write pipeline

AI Context: HLD for the context-memory write pipeline. Updated: 2026-09-17

## TL;DR

The contract the agent-facing skill executes: five ordered stages, the judgement the skill owns, and
the receipt it renders. Intent and goals in [README.md](./README.md); decisions in
[./ladrs/](./ladrs/); measurable quality bar in [./nfrs/](./nfrs/); pipeline sequence in
[./diagrams/c4-context.md](./diagrams/c4-context.md). Storage is HLD 001. The business requirements
this pipeline answers are in [BRD 001](../../brd/001-context-memory/): principally BR-01 (capture is a
byproduct of work, ranked there as the single most important requirement), BR-02 (no mid-task
interruption), BR-03 (sensitive material never stored), BR-10 (rule-resolvable disagreements settled
by stated authority; genuine conflicts surfaced, not resolved),
BR-14 (every change reportable) and BR-15 (gaps visible).

## Non-Negotiables

- **BR-01 outranks every other requirement this pipeline serves.** Any stage that introduces upkeep the practitioner must remember to perform fails the BRD outright, however well it satisfies the rest. Sequencing and batching decisions are made against that constraint first.
- **This document owns the mechanics of link derivation (LADR-05); BR-11 fixes the *when* at the capture checkpoint.** BR-11 originally named no moment, which is why three prior trials produced zero links unnoticed — the checkpoint clause exists precisely to prevent deferring derivation to a later phase.
- **Do not re-sequence the five stages.** Ordering is the design. Redaction after the body write is useless; deduplication after the write cannot change the write decision (LADR-01).
- **Redaction runs before anything reaches storage.** Objects are immutable and content-addressed — after the write there is no remedy, only orphaning (LADR-02).
- **Never log the matched span, the secret, or memory content** — not in diagnostics, not in the digest. Rule name and candidate only (LADR-03, NFR-01).
- **Deduplication matches on the subject, across groups.** Matching on the claim defeats versioning; matching within a group misses the duplicates that matter (LADR-04).
- **String similarity is a negative-only pre-filter, never the deciding signal.** A prior trial's heuristic returned nothing on the motivating case.
- **Never withhold a memory write pending approval.** Gate the status instead (LADR-06). This does not authorize inferred ticket hierarchy: LADR-08 requires an explicit practitioner declaration, not a proposed-memory surrogate.
- **The digest is a receipt, not a gate.** Dry-run is the pre-write veto, and it must share one code path with the real write (LADR-07).
- **The preflight judges nothing.** It returns facts; the merge verdict is the skill's. An endpoint returning a decision is a boundary violation.
- **Do not deduplicate the restated atomicity and matching rules** across the contract documents. The repetition counters a drift all three trials demonstrated.
- **Ticket hierarchy is declared, not derived (LADR-08).** Implemented and accepted via `ticket-parent`, PUT `/api/context/tickets/parent` and `ITicketGraph`; final HLD-003 evidence closes its release gates. Capture explicit set/reparent/remove; check expected parent before identical-state no-op, with no operation replay token. Local dry-run validates shape only. JSONB backfill creates identities, never hierarchy; tag identity/synonym writers stay blocked.

## Architecture Decisions

See [./ladrs/](./ladrs/).

| LADR | Decision | Why it matters |
|------|----------|----------------|
| [LADR-01](./ladrs/LADR-01-five-stage-pipeline.md) | Accepted; implemented and verified | The stage numbers are canonical; decisions and stages are two different lists and do not map one-to-one |
| [LADR-02](./ladrs/LADR-02-redaction-precedes-blob-write.md) | Accepted; implemented and verified | The only point at which prevention is still possible |
| [LADR-03](./ladrs/LADR-03-redact-and-flag.md) | Accepted; implemented and verified | Rejecting discards knowledge for an incidental problem |
| [LADR-04](./ladrs/LADR-04-semantic-dedup-is-skill-owned.md) | Accepted; implemented and verified | The database offers exact-match only; this is the top correctness risk |
| [LADR-05](./ladrs/LADR-05-link-derivation-batched.md) | Accepted; implemented and verified | Without a named stage, relationships are never created at all |
| [LADR-06](./ladrs/LADR-06-gate-status-not-persistence.md) | Accepted; implemented and verified | Recording is reversible; becoming canon is not |
| [LADR-07](./ladrs/LADR-07-digest-is-a-receipt.md) | Accepted; implemented and verified | The transaction has committed before a digest can describe it |
| [LADR-08](./ladrs/LADR-08-practitioner-declared-ticket-hierarchy.md) | Accepted; declared ticket writer implemented and verified | Resolves HLD-005 LADR-11's ticket writer; strict expected-parent semantics, no inference or tracker synchronization |

## Key Behaviors

- **The preflight is batched, array in and array out.** A per-record pass misses intra-batch collisions — two candidates in one batch sharing a subject, neither yet written, so neither visible to the other.
- **Stages 1 and 3 are distinct bounded reads, not one traversal.** The stage-1 preflight supplies the batched exact subject/ticket backstops and intra-batch collisions; stage 3 supplies the bounded semantic recall that deduplication judgement and link derivation use. Only ticket uniqueness is group-relative: the candidate carries its target group so the group being written into is not reported as its own conflict.
- **The skill never sequences the version bump.** The API owns the flip-then-insert ordering and its transaction; splitting it across round-trips can strand a memory with zero current versions.
- **Skipped has distinct causes.** An atomicity split never reaches the API; a duplicate link is skipped at the API. Reporting one aggregate hides a split remainder behind an unrelated zero.
- **A duplicate link inside a write is skipped and counted, never fatal** — a stale derived link must not discard the capture it arrived with.
- **Business time comes from the source date when known**, not the moment of capture. Defaulting to now makes bitemporality decorative.
- **Dry-run costs approximately a full write.** It runs the identical pipeline; it is not a cheap preview.
- **Proposed records are visible to the write path** even though retrieval excludes them — so a later capture on the same subject versions rather than duplicates.

## Quality Constraints

Targets and verification live in [./nfrs/](./nfrs/). Three shape how code is written:

- **Deduplication tests must drive the model judgement**, with negative controls at least as numerous as positive pairs. A test that encodes the rule and observes it holding measures nothing — a prior trial did exactly this and its result did not transfer.
- **Dry-run and write must share one code path**, with the persist step last and every verdict reached before it branches. A separate estimation path drifts and stops predicting.
- **Scope enforcement is pinned by standing unit, component and HTTP tests.** Ticket reads additionally
  verify live ownership, hidden-hop rejection and no consent from ticket identity. Preserve these
  checks; the final full-suite pass is linked from HLD-003's ticket evidence.

## Migration Plans

- Transactional links to new memories use an additive caller-selected `createUuid`. Existing `uuid`
  keeps its sole meaning (version target); both fields together are invalid. The skill assigns every
  create identity before dry-run and reuses the unchanged payload for write, so links and receipts have
  stable identities without a prepare/commit protocol. Legacy creates may still omit `createUuid`.
- Genuine conflicts use the existing model: a proposed open-kind `divergence` memory plus two
  `contradicts` links. The skill owns the semantic decision; the API counts newly created divergence
  items mechanically. Generated divergence is excluded from conflict evidence and deduplicated by the
  unordered pair of exact claim references. Rule-resolvable disagreement applies only BR-10's stated
  authority: verified shipped behaviour over specification, and agreed vocabulary over alternatives.
  Candidate-winner resolution is one version bump. Existing-winner resolution is two ordered version
  writes in one request: candidate loser becomes history, then existing winner becomes current again.
  Same-subject genuine conflict gives the candidate a deterministic non-colliding storage subject so
  both claims remain current and reachable; claim text and provenance are not rewritten.
- `--deepsearch` is opt-in: baseline recall remains capped at 200, then at most four keyword FTS passes
  of 25 and five depth-one traversals of 20, with 400 unique UUID/version candidates overall. Stable
  first-seen ordering and saturation/cap disclosure are mandatory.
- Deep search skips graph traversal when recall authority came only from group/ticket context and no
  explicit scope was supplied; current path API cannot preserve that context, so skipping with
  disclosure is safer than broadening.
- Read and write execution are delegated. Main-thread orchestration receives cited conclusions and
  receipts, never raw result arrays. Read-only capability is enforced by separate API read/write
  credentials and a read-only client surface; agent prose/frontmatter is defence in depth, not the
  security boundary.
- Candidate recall is stemmed since the storage side moved its text-search configuration to `english` on measured evidence; trigram similarity was measured and rejected (see [HLD-001 NFR-02 recall-tuning measurements](../001-context-memory-storage/nfrs/NFR-02-recall-tuning-measurements.md)). Semantic equivalence judgement stays skill-owned regardless.
- Any column added to a history table must extend the append-only trigger's equality list **in the same migration**, or it becomes silently mutable through an otherwise legal version-bump update.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-09-17 | Key Behaviors corrected: the stage-1 preflight and stage-3 semantic recall are distinct bounded reads; removed the stale "one traversal serves three concerns" claim that contradicted LADR-01 and the c4-context sequence diagram. | LADR-01; c4-context diagram |
| 2026-09-17 | Working-tree AppHost verification confirmed dry-run/write identity and link-count parity for two caller-identified creates plus one new-to-new edge; read-token traversal returned the edge. | NFR-05 evidence |
| 2026-09-17 | Verified ordered same-target version writes for authority-resolved conflicts and deterministic same-subject divergence composition; LADR-01, LADR-04, LADR-05 and LADR-07 now distinguish implementation acceptance from remaining NFR evidence. | BR-10 closure |
| 2026-09-17 | Approved closure contracts: additive `createUuid`, proposed divergence with loop/pair guards, bounded opt-in deep search, delegated read/write execution, and API-enforced read/write capabilities. | HLD-002 delta closure |
| 2026-09-16 | Stemming/trigram deferral resolved by HLD-001's measurement: candidate recall now uses the `english` FTS configuration; trigram rejected with a reopening threshold. Skill-owned semantic judgement unchanged. | HLD-001 NFR-02 recall-tuning measurements |
| 2026-09-15 | Finalized declared ticket writer acceptance against the passing solution, explicit benchmarks and Python harness. Corrected obsolete untested-scope claim; strict expected-parent/no-replay and local shape-only inspection remain unchanged. Full dossier and tag graph remain outside scope. | LADR-08; HLD-003 final NFR-02 evidence |
| 2026-09-14 | Synced declared hierarchy contract to API/store and skill implementation: separate PUT, strict expected-parent check before no-op, no replay token, changed receipt and no-network shape-only dry-run. Full dossier remains separate; ticket performance gate stays open, not release-accepted. | LADR-08; HLD-003 NFR-02 |
| 2026-09-14 | Owner-approved LADR-08 records explicit ticket hierarchy capture, expected-parent mutation and receipts; no automatic derivation, group-membership projection or tracker synchronization. Ticket half of HLD-005 LADR-11 resolved; tag half blocked. Documentation only, implementation pending. | LADR-08; HLD-003 LADR-08 |
| 2026-09-14 | Stage 1's ticket-uniqueness check recorded as group-relative — a candidate declares its target group, and self-ownership is not a conflict. Previously undecidable, so every candidate of a real batch was flagged. | e2e-dogfood |
| 2026-09-13 | Created — converted from ADR-0003. | ADR-0003 |
| 2026-09-13 | Added the upstream BRD as cited business authority, BR-01 primacy, and this HLD's ownership of link-derivation timing where BR-11 is silent. | BRD 001 |
| 2026-09-13 | Synced with BRD second amendment: BR-10 now states authority ranking; BR-11 fixes link proposal at the capture checkpoint. | BRD 001 |
