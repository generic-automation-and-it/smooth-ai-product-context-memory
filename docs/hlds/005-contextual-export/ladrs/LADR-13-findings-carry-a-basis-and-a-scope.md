# LADR-13: Every finding carries a stated basis and is scoped to the examined material

**Status:** Draft

<!-- Strategic. Appended after the tactical and blocked LADRs because numbers are never reassigned. -->

## Context

`BR-25`, `BR-27` and `BR-29` were sharpened together, and they turn out to be one requirement seen
from three angles.

`BR-27` now **defines** what a gap is, where an earlier draft of this design left it open: an answer
missing from the examined material, needed for the stated task or to interpret an included claim — or
missing against an explicit practitioner expectation. A general best-practice suggestion is analysis,
not evidence of a product gap.

`BR-25` requires generated inferences and questions to be labelled as analysis **and to state the
evidence or expectation behind them**, never to borrow the appearance of a stored fact.

`BR-29` requires quality findings to separate what was observed from what is hypothesised, and forbids
slice-scoped observations from being stated as store-wide facts.

The common failure is a finding that sounds like a discovery and is actually an inference with no
stated ground — the most persuasive and least checkable thing a composition can emit.

## Decision

**Require** every finding to carry three things: its **basis**, its **scope**, and its
**classification** as observation or analysis.

- **Basis** — what prompted it. For a gap: the task, the included claim needing interpretation, or the stated practitioner expectation. For a conflict: the claims and their applicability. For a quality finding: the observation itself. A finding whose basis cannot be stated is not emitted.
- **Scope** — what was examined. Every finding is a statement about the **selected material**, never about the store or the product. "Not found in this material" and "does not exist" are different claims, and only the first is available. `None detected` is always qualified by the examined scope.
- **Classification** — observation (present in the examined material, checkable by a reader) or analysis (an inference the composition made). Analysis states its ground; it never cites a memory that does not support it, and it never invents one.

Two consequences for the taxonomy fixed in NFR-04. `orphan` becomes **`no-links-in-slice`** — the
original name asserts a store-wide fact a slice cannot establish. `weak-summary` is explicitly
**analysis**, a hypothesis about findability, which keeps it distinct from HLD-004's observed
never-recalled signal rather than competing with it.

The export also states that it does **not** claim to have found every gap. Completeness of findings is
not achievable, and implying it is the same over-claim in aggregate form.

## Alternatives Considered

- **Leave the gap basis to the composition's judgement** — that was this design's earlier position, and `BR-27` has superseded it. It produced findings that were whatever the model found conspicuous: useful, unfalsifiable, and impossible to review.
- **Emit findings without a basis and let the reader judge** — rejected: the reader cannot reconstruct the ground for an inference, so an unfounded finding is indistinguishable from a well-founded one exactly when it matters.
- **Suppress low-confidence findings instead of labelling them** — rejected: the suppression threshold is invisible, so the export becomes quietly incomplete about the thing it most needs to be loud about. Label, do not hide.
- **Keep `orphan` and `weak-summary` as observations** — rejected: neither is observable from a slice. A record with no links *in the slice* may be richly linked outside it.

## Consequences

- A finding is reviewable: a reader can check the basis and reject the inference without re-deriving the slice.
- Findings volume drops, because a general suggestion no longer qualifies as a gap. What remains is more likely to be acted on, which is how `BR-29` and the success measures judge this.
- Every finding carries scope-qualifying language, which is more words per finding. Accepted — the alternative is a confident over-claim.
- The taxonomy rename (`orphan` → `no-links-in-slice`) must land before the first export, since NFR-04 fixes the set before first use.
- This design no longer needs an "expectation model" it could not supply. `BR-27` provides the basis, and where none of its three grounds applies, there is no gap to report.

## Related

- **LADR-04** — contradictions are a finding and take the same basis-and-scope discipline.
- **LADR-12** — a focus may reorder findings; it cannot remove the basis or widen the scope claim.
- **NFR-04** — the bounded taxonomy this renames within.
- **NFR-05** — attribution, which this extends from claims to findings.
- **HLD-004** — the observed findability signal that `weak-summary`-as-analysis stays distinct from.
