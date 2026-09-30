# NFR-02: Correctness — deduplication accuracy

**Status:** Accepted — **evidence re-measured 2026-09-29** on same-group pairs with balanced
controls, and **re-scored 2026-09-30** under corrected denominators: recall 1.0000 / precision 1.0000 /
accuracy 1.0000 across fourteen scenarios. See
[NFR-02-semantic-evidence-2026-09-29-balanced.md](./NFR-02-semantic-evidence-2026-09-29-balanced.md).

## Requirement

Semantic subject matching is measured on **both** axes against an authored fixture set:

- **Recall** — semantically equivalent, surface-different pairs are matched and produce a version rather than a second memory. Measured over the scenarios declared `recall_positive`.
- **Precision** — related-but-distinct pairs are *not* matched and produce a new memory rather than a version. Measured over the collapses the model actually claimed.

A single-axis measurement is not acceptable. A matcher that matches everything scores perfect recall
and destroys canon; one that matches nothing scores perfect precision and fills the store with
duplicates.

**Each figure's denominator is part of the requirement, not an implementation detail of the scorer.**
Recall over every scenario — including the ones for other pipeline stages, and the negative controls
the model got *right* — is accuracy wearing recall's name: a matcher that collapsed nothing scored
0.857 under it. Precision over every dedup-class scenario rather than over the claimed collapses
flatters the score, because most such scenarios cannot be over-merged and so dilute the numerator's
denominator. `score_fixtures.py` now reports the two plus `accuracy`, and the harness asserts the
distinction on a synthetic run rather than trusting the code to keep it.

## Verification

An adversarial fixture of authored pairs, each with an asserted decision — a coherent corpus contains
no live contradictions, so the fixture must be built rather than harvested.

- **Positive pairs** — e.g. *"PostgreSQL is the storage engine"* against *"we store in Postgres"* → must version.
- **Negative controls** — e.g. *"Auth issues tokens with a one-hour expiry"* against *"Auth uses a revocable session cookie"* → must not version. Same domain, same vocabulary, different claim.
- Pairs placed in **the same group**, since matching is now group-scoped. *(Amended 2026-09-28; this previously read "**different** groups, since cross-group matching is the requirement".)*

> **Evidence withdrawn 2026-09-28, re-measured 2026-09-29.** This NFR's cross-group fixture could no
> longer be produced by the shipped write path: a cross-group pair cannot version, so the measurement it
> was taken against no longer described the system. The accuracy claim was therefore **unevidenced, not
> disproven**. Both gaps the withdrawal named are now closed — the fixture places pairs in one group, and
> the negative-control count is balanced (5 against 2) with the balance asserted by the scorer and the
> harness rather than left to be counted by eye. See
> [NFR-02-semantic-evidence-2026-09-29-balanced.md](./NFR-02-semantic-evidence-2026-09-29-balanced.md).
>
> **The withdrawal was itself incomplete, corrected 2026-09-29.** Sweeping the *spec* was not the same as
> sweeping the *evidence*: the skill's committed semantic fixture still expected a cross-group
> `version_bump`, a committed verdicts file recorded the model performing it, and the CI-gated harness
> asserted recall and precision of exactly 1.0 — so the defect was **certified, not caught**. Re-scored
> against the corrected expectation that run yields `accuracy 0.9 / precision 0.5` — figures this NFR
> previously quoted as `recall 0.9 / precision 0.8333`, which were accuracy and a flattering
> precision, and which are corrected here rather than left to be re-derived. That run has no recall
> figure: its frozen fixture predates the `axis` labels and declares no positive pair. A third defect surfaced while re-measuring: the scorer
> paired verdicts to scenarios **by position**, so inserting or reordering a scenario would have
> misaligned every verdict after it and the 1.0 / 1.0 assertion would have certified the wrong verdicts
> against the wrong scenarios with nothing failing. Verdicts now pair by `id`. Sweeping a decision through
> a design set means checking the fixtures, the recorded measurements *and* the scoring machinery, not
> only the prose.
- Each pair asserts its decision individually; the pass criterion is countable — N equivalent pairs collapse to M subjects, with an exact skipped count.

The fixture drives the **model judgement**, not a string heuristic. A test that encodes the rule and
then observes it holding measures nothing — a prior trial did exactly this and reported a result that
did not transfer.

The model must receive the blinded output of `score_fixtures.py --emit-model-input`, never the source
fixture containing `expected`. Expected verdicts are scorer-only; exposing them invalidates the run.

## Acceptance Criteria

- Both recall and precision reported per run; neither alone is a pass.
- **Each is computed over its own denominator**: recall over the declared positive pairs, precision over
  the claimed collapses. A single number covering every scenario is accuracy and must be labelled so.
- Negative controls are present and at least as numerous as positive pairs — **for every scored fixture,
  including one passed via `--fixtures`**, which is a property of the fixture rather than of the run.
  The one exemption is a re-score of a superseded dated run passed with `--allow-legacy-positional`:
  its verdicts carry no `id` and therefore pair by position, and it is measured against the frozen
  pre-`axis` fixture it actually saw, which predates the labels and could not satisfy either rule.
- A fixture declaring no positive pair is refused as unmeasurable rather than scored with an empty
  denominator — under the same re-score exemption recall is reported as `null`, because that fixture
  cannot produce a recall denominator at all.
- A regression on either axis is visible as a changed count, not a passing boolean.
- String similarity appears only as a negative-only pre-filter, never as the deciding signal.

## Applies To

Goal 2; LADR-04. The largest correctness risk in the system.

## Evidence

[2026-09-29 blinded semantic evaluation, same-group pairs with balanced controls](./NFR-02-semantic-evidence-2026-09-29-balanced.md)
scored **recall 1.0000 / precision 1.0000 / accuracy 1.0000** across fourteen authored scenarios — 2
recall positives, 5 precision negatives and 7 covering the other write-path stages — with zero
over-merge events and verdicts paired by scenario id. Those three figures are that run **re-scored
2026-09-30** under corrected denominators, not a fresh run against the corrected scorer: correcting an
instrument does not re-ask the model, and the two facts are kept separable in the evidence document
rather than merged into one claim.

The superseded runs remain on disk and re-scorable against their own frozen fixture: the 2026-09-17 run
re-scores to `accuracy 0.9000 / precision 0.5000` with no recall figure, the mismatch being the
cross-group bump the shipped write path forbids.

[2026-09-17 blinded semantic evaluation](./NFR-02-semantic-evidence-2026-09-17.md) is superseded in part
and retained as the record of that date. Deterministic plumbing remains CI-gated separately.
