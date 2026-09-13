# LADR-05: Restatements collapse into one claim with every origin retained

**Status:** Draft

## Context

`BR-23` requires a claim captured more than once to appear once, with every capture still identifiable.
Two different things produce those repeats, and they need different treatment.

**Exact repeats** are mechanical: two memories whose bodies hash to the same content-addressed blob, or
the same subject slug reached through two anchors in one slice. These are identifiable without
judgement.

**Restatements** are not: the same conclusion reached in three sessions, worded differently each time,
with different surrounding detail. Recognising these is judgement, and the write path already treats
semantic deduplication as skill-owned for exactly that reason.

The failure to avoid in both cases is a merge that picks a winner and discards the rest. Three
independent captures of one conclusion are one claim and **three pieces of corroboration**; the
corroboration is frequently the most useful thing in the slice, because it shows the conclusion held
across contexts.

## Decision

**Collapse** in two stages, and never destructively.

The bundle collapses only what is mechanically identical — same blob address, or the same memory
reached by more than one anchor path — and records that it did, with the paths that reached it. That
much is deterministic and belongs on the reproducible side of the boundary.

The dossier collapses restatements. A collapsed claim is presented once and carries every origin it was
drawn from: memory uuid, version, capture time, and the group each came from. Where the collapsed
captures differ in substance rather than wording, the difference is either preserved in the claim or
reported as a finding — a near-duplicate that is not quite a duplicate is a signal about capture
quality, not noise to smooth over.

Collapse is a presentation decision, so it never removes anything from the bundle. The count
reconciliation in NFR-04 is performed against the bundle, which means a collapse that quietly loses a
memory fails a test rather than passing unnoticed.

## Alternatives Considered

- **Pick the newest or highest-confidence capture and drop the others** — rejected: destroys corroboration, and "newest" silently discards the reasoning of earlier captures (`BR-08`).
- **Deduplicate mechanically by text similarity in the API** — rejected: string similarity merges claims that differ in a load-bearing detail and misses restatements that share no wording. It would also be irreversible before the judgement stage ever saw the material.
- **Print every capture and let the reader collapse them** — rejected: this is the concatenation BRD-002 identifies as worse than the store.
- **Collapse everything into the bundle so the dossier receives pre-merged input** — rejected: the bundle must stay reproducible and judgement-free, and a merged bundle makes the reconciliation test meaningless.

## Consequences

- Corroboration survives, and a claim reached three times is visibly stronger than one reached once.
- Collapse is inspectable: every merge shows what it merged, so a wrong merge is a review comment rather than silent data loss.
- The dossier carries more citation apparatus than a naive merge would, which costs document space. Accepted — it is the same apparatus `BR-25` requires anyway.
- Near-duplicates that resist collapse become findings, adding to the findings volume early on while capture quality is still settling.

## Related

- **LADR-02** — why mechanical collapse and semantic collapse land on opposite sides of the boundary.
- **NFR-04** — reconciliation against the bundle is what makes a lossy collapse detectable.
- **NFR-05** — every retained origin is part of the attribution requirement.
