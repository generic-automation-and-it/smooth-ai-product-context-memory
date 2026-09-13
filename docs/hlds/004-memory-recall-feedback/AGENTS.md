# AGENTS.md - Memory recall feedback

AI Context: HLD for memory recall feedback. Updated: 2026-09-14

## TL;DR

Records whether retrieval is working — which memories are returned, which never are, and when a
retrieval finds nothing. Intent in [README.md](./README.md); decisions in [./ladrs/](./ladrs/);
quality bar in [./nfrs/](./nfrs/); context and recall path in
[./diagrams/c4-context.md](./diagrams/c4-context.md). Business authority is
[BRD 001](../../brd/001-context-memory/).

**This HLD is In Discovery.** The central decision — where feedback lives — is deliberately open
(LADR-02). Do not resolve it by implementation default.

## Non-Negotiables

- **Never put memory content or query text in a feedback record.** Not raw, not truncated, not hashed. The query is frequently as sensitive as the content (LADR-03).
- **Never make retrieval depend on feedback succeeding.** Losing a tuning signal is acceptable; losing a recall is not (NFR-02).
- **Never let feedback influence ranking.** A store that surfaces what it has surfaced before is self-reinforcing, and that failure is slow and hard to detect.
- **Never version feedback or bring it under the append-only guarantee.** It is classification, not a claim — the same reasoning that keeps tags and facets off the versioned row (LADR-04).
- **Record misses, not just hits.** Hits alone document what already works and hide everything that does not.
- **Do not choose the placement by default.** LADR-02 is open on purpose; a counter column is the easiest to write and the most likely to be wrong.

## Architecture Decisions

See [./ladrs/](./ladrs/). All Draft.

| LADR | Decision | Why it matters |
|------|----------|----------------|
| [LADR-01](./ladrs/LADR-01-record-recall-outcomes.md) | Record recall outcomes, including misses | The miss is the actionable half and the one usually omitted |
| [LADR-02](./ladrs/LADR-02-feedback-placement.md) | Placement deliberately open | A counter column turns every read into a write and discards recency |
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

- **The concurrency test decides the placement.** Concurrent retrievals returning the *same* memory must show no contention — which a counter column fails under repeat recall of a popular memory and appends do not.
- **The confidentiality test must include the miss path.** That is the case someone will later want to diagnose, and therefore the one most likely to be tempted into recording the query.

## Migration Plans

- If the telemetry-derived placement is chosen (LADR-02, option C), this design becomes dependent on an observability retention policy owned elsewhere. Record that coupling explicitly rather than discovering it when retention changes.
- Feedback retention is a bound, not a policy. If long-term trend analysis is ever wanted, that is a different design — old recall data describes a store and a recall implementation that no longer exist.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-09-14 | Created — discovery HLD for recall feedback. Placement deliberately left open. | Gap identified during pre-dogfooding review |
| 2026-09-14 | Added business-authority back-reference to BRD 001, completing the three-place rule. | BRD 001 |
