# NFR-03: Actionability

**Status:** Draft

## Requirement

The feedback must answer the three questions it exists for. If it cannot, it is not worth building.

1. **Which memories have never been recalled?** — answerable as a list, not by manual inspection.
2. **How often does retrieval return nothing?** — countable over a period, so a tuning change can be shown to move it.
3. **Did a tuning change improve recall?** — the same measure, comparable across a change.

## Verification

- Capture a set of memories, retrieve some, then produce the never-recalled list and assert it matches the untouched set exactly.
- Perform a mix of hits and misses; assert the miss rate is derivable and matches the known mix.
- Take a baseline, apply a recall change, re-measure, and confirm the difference is attributable rather than merely present.

## Acceptance Criteria

- All three questions answerable without ad-hoc inspection of raw records.
- The never-recalled list distinguishes **never recalled** from **recently captured and not yet retrieved** — otherwise every new memory pollutes the signal it exists to provide.
- A before/after comparison is meaningful, which requires the baseline to be resettable (LADR-04).
- **If a question cannot be answered, the design is reconsidered rather than the question quietly dropped.**

## Applies To

All three Key Goals. This NFR is the test of whether the design earns its cost — the feature exists to
supply evidence for deferred decisions, and evidence that cannot be queried is not evidence.
