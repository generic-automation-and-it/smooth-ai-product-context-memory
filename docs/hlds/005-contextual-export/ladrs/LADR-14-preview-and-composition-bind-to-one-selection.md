# LADR-14: Preview and composition bind to one recorded selection

**Status:** Draft

<!-- Tactical. Appended; numbers are never reassigned. -->

## Context

`BR-32` makes the preview a consent step: the practitioner sees the effective scope, volume, reach
limits and estimated cost, then proceeds, narrows or cancels. `BR-20` requires the export to record the
effective selection so it can be repeated.

Between those two moments the store can change. Capture happens at task checkpoints, and a practitioner
may well preview an export, answer a question, capture the answer, and then compose. If composition
silently re-selects, the document is not the thing that was approved — and the consent `BR-32` exists to
obtain was given for a different scope.

Nothing in the design said what happens here. Left unaddressed, the implementation default is a
re-select, because that is what calling the endpoint twice does.

## Decision

**Bind** the preview and the composition to one **recorded effective selection**, and treat a change
between them as something to report rather than absorb.

The preview returns the effective selection — resolved criteria, combination semantics, reach bound,
retrieval policy, history policy, and the identities and versions selected — together with the counts
and estimate. Composition takes that recorded selection as its input. Because selection is
reproducible (NFR-02), re-running it against an unchanged store yields the same result, so the
ordinary case needs no special handling.

When the store **has** changed, composition does not silently proceed on new material and does not
silently proceed on stale material. It reports the difference — what appeared, what changed version,
what is no longer selected — and the practitioner re-previews or proceeds deliberately. The document
records which selection it was composed from.

This is the same posture the write path already takes with its dry run: the plan that was inspected is
the plan that executes, and a divergence is surfaced rather than resolved.

## Alternatives Considered

- **Re-select at composition time and compose the current state** — rejected: the approved scope and the composed scope differ with no signal, which makes the `BR-32` preview decorative.
- **Compose strictly from the previewed identities, ignoring changes** — rejected: a superseded version could be composed as current after the practitioner had already recorded its replacement, which `BR-24` treats as a correctness failure rather than a staleness inconvenience.
- **Lock the store, or snapshot it, for the duration** — rejected: an export is a read (`BR-31`) and must not make writes wait. A transaction spanning a model-driven composition step is unbounded in time.
- **Attach a validity window and expire the preview** — rejected as the primary mechanism: an arbitrary window neither prevents the problem inside it nor explains what happened outside it. Reporting the actual difference is strictly more informative and needs no tuning.

## Consequences

- The scope the practitioner approved is the scope composed, or they are told why not.
- The recorded effective selection is what makes an export repeatable under `BR-20`, so this decision supplies that record rather than adding a separate mechanism for it.
- An export requested and composed in one uninterrupted step is unaffected; the report only appears when something actually moved.
- Composition needs the recorded selection as input, so the preview is not merely an estimate — it is the first half of the operation. That is a slightly larger contract than "return a manifest".
- Nothing locks and nothing waits, so a concurrent capture is never delayed by an export in progress.

## Related

- **LADR-02** — the bundle is what carries the recorded selection across the boundary.
- **NFR-02** — reproducibility is what makes the ordinary case a no-op.
- **NFR-03** — the preview whose consent this protects.
