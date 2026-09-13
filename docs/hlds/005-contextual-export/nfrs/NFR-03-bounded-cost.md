# NFR-03: Bounded cost

**Status:** Draft

## Requirement

An export's size and cost are bounded, the bounds are stated on the wire, and the cost is knowable
before composition is paid for.

- **Widening depth** is required on the request, has **no server-side default**, and is refused outside 1–5 — by the request validator *and* by the store layer, so a direct caller cannot bypass the validator. This mirrors the existing traversal bound rather than inventing a second rule.
- **Item count** is capped. Default and hard maximum are stated in the contract; a request exceeding the hard maximum is refused rather than truncated.
- **A manifest-only request** returns counts, reach, the caps that would be hit, and an estimate of composition size — **without** hydrating bodies and without composing anything.
- The manifest-only path is materially cheaper than the full bundle: **p95 under 1 second** against a store of 1,000 memories on the development container, and it must not hydrate a single blob.
- Every cap actually hit is named in the manifest of the response that hit it.

## Verification

- **L0** — validator tests: depth absent ⇒ refused; depth 0 and 6 ⇒ refused; item count above the hard maximum ⇒ refused.
- **L1** — store-layer test that an out-of-range depth is refused when the validator is bypassed.
- **L1** — assert a manifest-only request performs zero blob reads, using a counting blob-storage test double.
- **L1** — seeded 1,000-memory store; measure manifest-only p95 over repeated runs. Benchmarked behind an environment flag, in the same style as the existing traversal benchmark, so the ordinary test run stays fast.
- **L1** — assert every cap hit appears in the manifest, and that a hit cap never produces a silently shorter payload.

## Acceptance Criteria

- No request can be made without an explicit widening bound.
- Depth outside 1–5 is refused at both the validator and the store layer.
- A manifest-only request hydrates no blob and meets the p95 target on the seeded store.
- Every cap hit is named in the manifest of the response that hit it.
- No response is silently truncated; a request too large to serve is refused with what it exceeded.

## Applies To

Goal 4 (cost visible before it is paid) and Goal 1 (bounded selection). LADR-02 (composition cost is the
skill's, which is why the manifest must be servable without it), LADR-03 (the bound). `BR-32`, and
`BR-30` for the no-silent-truncation half.
