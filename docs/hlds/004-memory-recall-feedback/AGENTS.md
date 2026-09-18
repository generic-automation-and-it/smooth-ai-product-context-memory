# AGENTS.md - Memory recall feedback

AI Context: HLD for memory recall feedback. Updated: 2026-09-16

## TL;DR

Records whether retrieval is working — which memories are returned, which never are, and when a
retrieval finds nothing. Intent in [README.md](./README.md); decisions in [./ladrs/](./ladrs/);
quality bar in [./nfrs/](./nfrs/); context and recall path in
[./diagrams/c4-context.md](./diagrams/c4-context.md). Business authority is
[BRD 001](../../brd/001-context-memory/).

**This HLD is In Discovery**, but its central decision is no longer open: feedback lives in **append-only
records** (LADR-02), resolved on measured evidence and not by default. Do not reopen it without new
measurements — the [evidence](./nfrs/NFR-02-placement-evidence-2026-09-18.md) records what would have to
change first.

## Non-Negotiables

- **Never put memory content or query text in a feedback record.** Not raw, not truncated, not hashed. The query is frequently as sensitive as the content (LADR-03).
- **Never make retrieval depend on feedback succeeding.** Losing a tuning signal is acceptable; losing a recall is not (NFR-02).
- **Never let feedback influence ranking.** A store that surfaces what it has surfaced before is self-reinforcing, and that failure is slow and hard to detect.
- **Never version feedback or bring it under the append-only guarantee.** It is classification, not a claim — the same reasoning that keeps tags and facets off the versioned row (LADR-04).
- **Record misses, not just hits.** Hits alone document what already works and hide everything that does not.
- **Never reintroduce the counter column.** It was measured and eliminated: a second reader of the same memory blocks on the first, every read rewrites the row, it cannot answer recency, and — decisively — it has nowhere to record a miss (LADR-02).
- **Every record carries a per-retrieval identifier.** Fifty rows from one retrieval are otherwise indistinguishable from fifty retrievals, and the miss-rate question becomes unanswerable (LADR-02 record shape).
- **"Recently captured" comes from `min(memory_version.created_on)`.** The stable memory row carries no timestamp, so the exclusion cannot be a predicate on the thing being measured.

## Architecture Decisions

See [./ladrs/](./ladrs/). LADR-02 Accepted; the rest Draft.

| LADR | Decision | Why it matters |
|------|----------|----------------|
| [LADR-01](./ladrs/LADR-01-record-recall-outcomes.md) | Record recall outcomes, including misses | The miss is the actionable half and the one usually omitted |
| [LADR-02](./ladrs/LADR-02-feedback-placement.md) | Feedback lives in append-only records | A counter column turns every read into a write, discards recency, and cannot hold a miss at all |
| [LADR-03](./ladrs/LADR-03-identity-not-content.md) | Identity and outcome only | Prevents a less-guarded second copy of sensitive material |
| [LADR-04](./ladrs/LADR-04-unversioned-and-disposable.md) | Unversioned and disposable | Keeps feedback out of history, backup and the version chain |

## Key Behaviors

- **An empty retrieval is a normal response.** It is interesting to tuning, not to the caller, and must not become an error to make it observable.
- **Retrieval-shape classification must be a bounded set**, defined before the first record is written. Adding a category later changes the meaning of existing records, and free text is how content leaks back in.
- **"Never recalled" must exclude the recently captured**, or every new memory pollutes the signal the measure exists to provide.
- **Feedback is excluded from backup and restore.** It is disposable, which keeps it out of the cross-store consistency problem entirely.
- **Resetting feedback is legitimate**, not destructive — a tuning experiment should be able to start from a clean baseline.

## Quality Constraints

Targets and verification live in [./nfrs/](./nfrs/). Two shape how code is written:

- **The concurrency test decided the placement**, and latency did not: the counter–append p95 gap (0.797 ms) is smaller than one placement's own spread across repeated rounds (1.169 ms), while the counter column blocked a second reader of the same memory outright (`55P03`). A wall-clock comparison alone would have chosen wrongly.
- **Any latency comparison in this design rotates the order of what it measures.** A fixed order made the append placement look slower than the counter; rotating it and resetting between rounds reversed the sign, because the placement measured last runs against a bigger feedback table and a dirtier `memory` table.
- **The confidentiality test must include the miss path.** That is the case someone will later want to diagnose, and therefore the one most likely to be tempted into recording the query.

## Migration Plans

- The telemetry-derived placement (LADR-02, option C) was rejected rather than chosen, so this design takes on no dependency on an observability retention policy owned elsewhere. Reviving C would reintroduce that coupling, and would buy a latency saving the measurements could not detect.
- Feedback retention is a bound, not a policy: **30 days or 2,000,000 records, whichever comes first** (193 bytes/record measured, ~9.4 KiB per 50-memory retrieval, ~370 MiB at the cap). If long-term trend analysis is ever wanted, that is a different design — old recall data describes a store and a recall implementation that no longer exist.
- The feedback table sits outside the six entity types the DbContext exposes and `ModelShapeGuardTests` asserts. Whether it becomes an EF entity is a deliberate decision for the write path, not something to settle by adding a `DbSet` and following the compiler. The prototype's surrogate `bigserial` key is scaffolding, not part of the record shape.
- **What the placement evidence does not discharge.** It settles which mechanism to build, measured at the prototype boundary. The shipped fire-and-forget writer, the identical-results-with-feedback-on-and-off criterion against the retrieval handler, the growth projection from an *observed* retrieval rate, and the never-recalled and miss-rate query surfaces are all verified where they are built — which is why NFR-01..03 stay Draft.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-09-14 | Created — discovery HLD for recall feedback. Placement deliberately left open. | Gap identified during pre-dogfooding review |
| 2026-09-14 | Added business-authority back-reference to BRD 001, completing the three-place rule. | BRD 001 |
| 2026-09-16 | README Intent reworded: the stemming/trigram deferral is closed — stemming adopted and trigram rejected on HLD-001's measured evidence; trigram's reopening threshold now waits on this HLD's feedback data. | HLD-001 NFR-02 recall-tuning measurements |
| 2026-09-18 | Placement evidence re-measured after harness review, and the record it supports tightened. Latency now runs three rotated rounds with a reset between them, so the per-placement run-to-run spread is measured rather than asserted — that reversed the A/B ordering the first pass reported and is recorded as a standing caution. The read-path comparison is field-by-field and the failing feedback write sits inside the retrieval's unit of work; the serialisation and classification probes accept only `55P03` and `23514`; plan output is recorded, not asserted. Figures updated (193 bytes/record, ~9.4 KiB per retrieval, ~370 MiB at the cap) and the verification the prototype does *not* discharge is stated. | [NFR-02 placement evidence](./nfrs/NFR-02-placement-evidence-2026-09-18.md), `FeedbackPlacementEvidenceTests` |
| 2026-09-18 | LADR-02 resolved on measured evidence: feedback lives in append-only records. The counter column is eliminated (serialises a second reader of the same memory, rewrites the row on every read, answers no recency, and cannot record a miss at all); the telemetry-derived option is rejected for an undetectable latency saving bought with an external retention dependency. Record shape, retention bound and the per-retrieval identifier requirement recorded; non-negotiables and migration notes updated accordingly. LADR-01/03/04 and all three NFRs stay Draft — the placement was settled, not the implementation. | [NFR-02 placement evidence](./nfrs/NFR-02-placement-evidence-2026-09-18.md), `FeedbackPlacementEvidenceTests` |
