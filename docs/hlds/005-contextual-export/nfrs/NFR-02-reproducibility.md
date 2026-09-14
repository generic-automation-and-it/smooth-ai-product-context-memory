# NFR-02: Reproducibility

**Status:** Draft

## Requirement

One **effective selection** — criteria, combination rule, widening bound, history policy and retrieval
policy — against one unchanged store ⇒ a **byte-identical bundle**, across repeated calls and across
process restarts.

`BR-20` also requires the effective selection to be **recorded**, so the export can be repeated rather
than merely being reproducible in principle. That record is what LADR-14 binds preview and composition
to, and what makes the ordinary case of that decision a no-op.

This binds the bundle only. The dossier is judgement (LADR-02) and is deliberately **not** required to be
identical between runs; requiring it would either forbid the judgement or make the guarantee untrue.

**Focus does not enter this requirement.** A focus is applied after selection and is not part of the
request that produces a bundle (LADR-12), so this stays a single guarantee rather than one per focus —
which is the main reason focus is a lens and not a selection predicate.

Concretely, the bundle must be free of every ordinary source of run-to-run variance:

- No timestamp of its own generation anywhere in the payload. Stored times are data; a generation time is variance.
  **This does not conflict with `BR-21`**, which requires the *document* to be identifiable as a dated snapshot. The date belongs to the dossier, where it is useful and where nothing is required to be byte-identical; putting it in the bundle would break this requirement for no gain. Anyone reading the two requirements as contradictory has conflated the two artefacts.
- Deterministic ordering at every level — selected memories, edges, paths, manifest entries — using the stated tiebreak of business-time validity, then capture time, then memory identity.
- Deterministic cut behaviour when a cap is reached: the cut is decided by the ordering, never by whatever the traversal reached first.
- Deterministic collapse of the mechanically identical (LADR-05), including when one memory was reached by several anchor paths.

## Verification

- **L1** — call twice against one real store, hash both payloads, assert equality. Repeat across a process restart to catch in-process caching and hash-seed dependence.
- **L1** — call twice with the anchor set supplied in a different order, and with tags listed in a different order; assert identical payloads. Request order must not be result order.
- **L1** — seed a store where two memories tie on validity and capture time; assert the identity tiebreak resolves them stably.
- **L1** — seed a slice that exceeds the item cap; assert both runs cut at the same boundary and that the manifest names the same cut.
- **L0** — assert the payload contains no generation timestamp, as a schema-level test rather than an inspection.

## Acceptance Criteria

- Two bundles from one request against an unchanged store are byte-identical, including after a restart.
- The effective selection is recorded in the bundle in a form sufficient to repeat it.
- Reordering the anchors or the tags in a request changes nothing in the response.
- Every ordering has a documented, tested tiebreak; no ordering falls back to database order.
- A cap hit cuts at the same place in both runs and is reported identically.
- No test asserts dossier byte-equality — that would contradict LADR-02.

## Applies To

Goal 1 (deterministic selection). LADR-02 (the boundary this guarantee stops at), LADR-03 (widening),
LADR-05 (mechanical collapse), LADR-07 (ordering tiebreak). `BR-20`.
