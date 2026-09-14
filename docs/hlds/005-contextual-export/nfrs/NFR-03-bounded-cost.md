# NFR-03: Bounded cost

**Status:** Draft

## Requirement

An export's reach is bounded, the bounds are visible, and the size and cost are knowable **before**
composition is paid for. `BR-32` treats the preview as consent, not as a performance feature.

### Structural bounds — settled

- **Widening depth** is required on the request, has **no server-side default**, and is refused outside 1–5 by the request validator *and* by the store layer, so a direct caller cannot bypass the validator. This mirrors the existing traversal bound rather than inventing a second rule.
- **A request whose scope exceeds a supported limit is refused and reported**, never silently truncated and never silently broadened.
- **Every limit actually reached is named** in the response that reached it.
- **A budget never overrides applicability** — reaching a limit narrows what is included and reports the omission; it never keeps a claim while compressing away its conditions (NFR-07).

### Preview — settled

- A **preview** returns the effective scope, the selected volume, the reach limits and the estimated composition usage **without composing anything and without hydrating a single body**.
- Estimates **state their assumptions and their uncertainty**. A single confident number invites the practitioner to treat an estimate as a quote.
- **Monetary cost is shown when pricing is known and identified as unavailable when it is not.** Silently omitting it reads as free.
- The practitioner can proceed, **narrow, or cancel** from the preview, and cancelling changes nothing (NFR-06).

### Numeric limits — provisional, pending validation

Item caps and latency targets are **not yet set**. `BR-32` and the success measures require them to be
derived from the reference workflows (spec preparation, handover, re-entry) rather than assumed, and
BRD-002 records that size and usability limits remain unmeasured.

What is deliberately **not** assumed: that a memory count predicts a useful document, or that a slice
which fits a cap composes acceptably in one pass. Fidelity and size are assessed together — a shorter
document that lost an exception is a worse document (NFR-07).

Until validation supplies numbers, an implementation carries a **stated, configurable** item limit and
records which value produced each export, so the first exports become the evidence rather than a
retrospective guess. The provisional value is not a specification and must not be cited as one.

## Verification

- **L0** — validator tests: depth absent ⇒ refused; depth 0 and 6 ⇒ refused; scope beyond the configured item limit ⇒ refused with what it exceeded, never a shortened result.
- **L1** — store-layer test that an out-of-range depth is refused when the validator is bypassed.
- **L1** — assert a preview performs **zero** blob reads, using a counting blob-storage test double.
- **L1** — assert every limit reached appears in the manifest, and that reaching one never produces a silently shorter payload.
- **Skill-level test** — assert the preview carries its assumptions and an uncertainty statement, and that monetary cost is either present or explicitly marked unavailable.
- **Reference-workflow validation** — run the three representative workflows, record slice size, composition usage, and whether the document was fit for its task. These runs **set** the numeric limits. Benchmarked behind an environment flag in the style of the existing traversal benchmark, so the ordinary test run stays fast.

## Acceptance Criteria

- No request can be made without an explicit widening bound; depth outside 1–5 is refused at both the validator and the store layer.
- A preview hydrates no body and states its assumptions, its uncertainty, and whether monetary cost is known.
- The practitioner can narrow or cancel from the preview; cancelling leaves the store unchanged.
- Scope beyond a limit is refused and reported; no response is silently truncated or broadened.
- Numeric item and latency limits are recorded as derived from the reference workflows, with the value used captured per export — not inherited from this document as an assumption.

## Applies To

Goal 4 (cost visible before it is paid) and Goal 1 (bounded selection). LADR-02 (composition cost is
the skill's, which is why the preview must be servable without it), LADR-03 (the bound, and the history
policy the preview must price), LADR-14 (the preview is the first half of the operation, not a detached
estimate). `BR-32`, and `BR-30` for the no-silent-truncation half.
