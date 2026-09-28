# NFR-02: Correctness — deduplication accuracy

**Status:** Accepted — **evidence basis withdrawn 2026-09-28**; accuracy still unmeasured, see Verification

## Requirement

Semantic subject matching is measured on **both** axes against an authored fixture set:

- **Recall** — semantically equivalent, surface-different pairs are matched and produce a version rather than a second memory.
- **Precision** — related-but-distinct pairs are *not* matched and produce a new memory rather than a version.

A single-axis measurement is not acceptable. A matcher that matches everything scores perfect recall
and destroys canon; one that matches nothing scores perfect precision and fills the store with
duplicates.

## Verification

An adversarial fixture of authored pairs, each with an asserted decision — a coherent corpus contains
no live contradictions, so the fixture must be built rather than harvested.

- **Positive pairs** — e.g. *"PostgreSQL is the storage engine"* against *"we store in Postgres"* → must version.
- **Negative controls** — e.g. *"Auth issues tokens with a one-hour expiry"* against *"Auth uses a revocable session cookie"* → must not version. Same domain, same vocabulary, different claim.
- Pairs placed in **the same group**, since matching is now group-scoped. *(Amended 2026-09-28; this previously read "**different** groups, since cross-group matching is the requirement".)*

> **Evidence withdrawn 2026-09-28.** This NFR's cross-group fixture can no longer be produced by the
> shipped write path: a cross-group pair cannot version, so the measurement it was taken against no longer
> describes the system. The accuracy claim below is therefore **unevidenced, not disproven**. Re-running
> it requires the fixture to place pairs in one group, and the negative-control count is still
> unbalanced (fewer negatives than positives), which the acceptance criteria below already require to be
> fixed. Closure of this NFR is a batch-5 work item; it is not treated as accepted-and-closed meanwhile.
- Each pair asserts its decision individually; the pass criterion is countable — N equivalent pairs collapse to M subjects, with an exact skipped count.

The fixture drives the **model judgement**, not a string heuristic. A test that encodes the rule and
then observes it holding measures nothing — a prior trial did exactly this and reported a result that
did not transfer.

The model must receive the blinded output of `score_fixtures.py --emit-model-input`, never the source
fixture containing `expected`. Expected verdicts are scorer-only; exposing them invalidates the run.

## Acceptance Criteria

- Both recall and precision reported per run; neither alone is a pass.
- Negative controls are present and at least as numerous as positive pairs.
- A regression on either axis is visible as a changed count, not a passing boolean.
- String similarity appears only as a negative-only pre-filter, never as the deciding signal.

## Applies To

Goal 2; LADR-04. The largest correctness risk in the system.

## Evidence

[2026-09-17 blinded semantic evaluation](./NFR-02-semantic-evidence-2026-09-17.md) scored 1.0000 recall
and 1.0000 precision across ten authored scenarios, including genuine conflict, two authority controls
and a related-but-distinct negative control. Deterministic plumbing remains CI-gated separately.
