# LADR-05: Equivalent claims consolidate with every origin retained; equivalence is meaning, applicability and lifecycle

**Status:** Draft

<!-- Revised after BRD-002's BR-22/BR-23/BR-24 were sharpened. An earlier version of this decision
     treated repeated captures as corroboration; BR-23 supersedes that. See Consequences. -->

## Context

`BR-23` requires a claim captured more than once to appear once with every origin retained, and it now
states the test for "more than once": **equivalence requires the same meaning, the same applicability
and the same lifecycle.** Similar wording is explicitly insufficient. `BR-22` adds that concision must
preserve definitions, conditions, thresholds, permissions and exceptions, and `BR-24` that proposed,
superseded and no-longer-true material stay distinguishable.

Three different things produce apparent repeats, and they need different treatment.

**Exact repeats** are mechanical: two memories whose bodies hash to the same content-addressed blob, or
one memory reached through two anchor paths in one slice. Identifiable without judgement.

**Equivalent restatements** are judgement: the same claim, worded differently, with the same
applicability and lifecycle. The write path already treats semantic deduplication as skill-owned for
exactly this reason.

**Near-misses that must not merge** are the dangerous case: same wording, different customer;
same rule, different lifecycle state; same conclusion, one proposed and one shipped. These *look* like
the second category and are not.

## Decision

**Consolidate** in two stages, never destructively, and only where meaning, applicability and lifecycle
all match.

The bundle consolidates only what is mechanically identical — same blob address, or one memory reached
by several anchor paths — and records that it did, with the paths that reached it. That much is
deterministic and belongs on the reproducible side of the boundary (LADR-02).

The dossier consolidates equivalent restatements. A consolidated claim appears once and carries every
origin it was drawn from: memory identity, version, capture time, and the group each came from. Where
candidates differ in scope, condition, time or status they stay distinct. **Where equivalence is
uncertain, the distinction is kept** — an unmerged near-duplicate costs a little length, a wrong merge
produces a false claim.

**Multiple origins are reported as origins, not as strength.** `BR-23` states that several captures of
one source are not necessarily independent corroboration, so the document distinguishes independent
observations from re-captures of one source and never presents a count as weight.

Consolidation is a presentation decision and never removes anything from the bundle, so NFR-04's
reconciliation is performed against the bundle: a consolidation that loses a memory fails a test rather
than passing unnoticed.

## Alternatives Considered

- **Pick the newest or highest-confidence capture and drop the others** — rejected: destroys the origin record `BR-23` requires, and "newest" discards earlier reasoning (`BR-08`).
- **Deduplicate mechanically by text similarity** — rejected, and `BR-23` now names why: similar wording is not equivalence. It merges a customer exception into the general rule and misses restatements that share no vocabulary.
- **Print every capture and let the reader consolidate** — rejected: the concatenation BRD-002 identifies as worse than the store.
- **Consolidate into the bundle so the dossier receives pre-merged input** — rejected: the bundle must stay reproducible and judgement-free, and a merged bundle makes the reconciliation test meaningless.
- **Treat repeat captures as corroboration** — this decision's own earlier position, **superseded by `BR-23`**. Recorded because the reasoning was appealing and wrong: three captures of one conclusion frequently trace to one source, so counting them as three independent confirmations manufactures confidence the store never had.

## Consequences

- A false merge is much less likely, because the equivalence test is explicit and uncertainty resolves toward keeping the distinction.
- Consolidation is inspectable: every merge shows what it merged, so a wrong merge is a review comment rather than silent loss.
- The dossier carries more citation apparatus and slightly more length than a naive merge. Accepted — it is the apparatus `BR-25` requires anyway.
- Near-duplicates that resist consolidation become findings, so early exports will report more of them while capture quality settles.
- Whoever implements this must **not** reinstate corroboration counting. It reads as a feature and is a fabrication.

## Related

- **LADR-02** — why mechanical and semantic consolidation land on opposite sides of the boundary.
- **LADR-13** — an uncertain equivalence reported as a finding takes the basis-and-scope discipline.
- **NFR-04** — reconciliation against the bundle makes a lossy consolidation detectable.
- **NFR-07** — the fidelity bar this serves, including the lifecycle and near-miss cases.
