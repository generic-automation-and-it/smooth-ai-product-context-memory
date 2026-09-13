# LADR-04: Contradictions are reported with their evidence, never silently resolved

**Status:** Draft

## Context

`BR-28` inherits `BR-10`'s two-case structure: some disagreements are settled by a stated authority —
shipped behaviour outranks specification, agreed vocabulary outranks ad-hoc naming — and the rest are
genuine conflicts where choosing automatically hides information.

The store carries partial evidence for both. A `contradicts` edge is an explicit record that two
memories disagree; a `supersedes` edge records that one replaced another; scope records whether a claim
describes shipped product or programme intent. None of that is complete: the overwhelming majority of
contradictions in the store have **no edge at all**, because nobody was looking at both memories at the
same time.

Reading a whole slice is the first moment anything looks at all of them together.

## Decision

**Report** every contradiction found, with the evidence each side rests on, and **resolve only** those
a stated authority settles — showing the authority and retaining the losing position.

An edge is evidence, not a precondition. A contradiction with a `contradicts` edge is reported *and*
that edge is cited. A contradiction the composition notices between two unlinked current claims is
reported identically, with its absence of an edge noted as itself a finding — the store held a conflict
it had never recorded.

Where a stated authority applies, the resolution is shown rather than performed silently: which claim
won, which rule made it win, and the losing claim retained with its reasoning. Where none applies, the
disagreement is the finding and is presented for a decision.

Nothing is written. Recording a contradiction as knowledge, or adding the missing edge, is a capture
and goes through the approved write path (LADR-06).

## Alternatives Considered

- **Report only contradictions that already carry a `contradicts` edge** — rejected: that reports what the store already knew and hides everything it did not, which is the entire value of reading a slice as a whole.
- **Resolve automatically using recency** — rejected: newer is not truer. This is exactly the silent resolution `BR-10` forbids, and it would erase the losing reasoning, which is frequently the more valuable half (`BR-08`).
- **Write the missing `contradicts` edge as a side effect of finding it** — rejected: turns a read into a write, bypasses the practitioner's judgement, and would let a composition error become stored fact (LADR-06).
- **Suppress low-confidence contradiction findings** — rejected: the suppression threshold would be invisible, so the export would be silently incomplete about the one thing it is most important to be loud about.

## Consequences

- Contradictions the store never recorded become visible for the first time.
- False positives will occur — a composition may read two compatible claims as conflicting. Accepted: a spurious contradiction costs a moment's review, where a missed one costs a wrong decision. Every finding names its memories so review is cheap.
- A missing `contradicts` edge is itself reported, which turns the findings into a route back to better capture.
- The practitioner has a decision queue rather than an answer, which is more work and is the point.

## Open

- **What counts as a "stated authority" mechanically?** Shipped-versus-specified maps onto scope, but the scope model's citation rules are recorded as absent from BRD-001. Until they exist, authority-based resolution is limited to what the composition can justify from scope and supersession alone. Resolved by the scope-and-citation work named in BRD-001's `AGENTS.md` gap list.

## Related

- **LADR-06** — why finding a contradiction does not write one.
- **NFR-04** — findings are counted, so suppression would be visible.
