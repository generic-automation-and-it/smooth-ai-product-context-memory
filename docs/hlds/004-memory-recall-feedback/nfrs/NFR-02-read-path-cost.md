# NFR-02: Performance — read-path cost

**Status:** Draft

## Requirement

Recording feedback must not change how retrieval behaves.

- **No measurable increase in retrieval latency** at expected volume.
- **No contention introduced on frequently recalled memories.** A popular memory must not become a hot row.
- **A feedback failure must never fail a retrieval.** Losing a tuning signal is acceptable; losing a recall is not.
- Feedback volume must be **bounded**, by retention or by sampling.

## Verification

- Measure retrieval latency with feedback disabled and enabled, over the same data and query mix; compare p50 and p95.
- Exercise concurrent retrievals returning the **same** memory; assert no lock contention and no serialisation on a shared row.
- Inject a feedback-write failure; assert the retrieval still returns its results normally.
- Project feedback growth from observed retrieval rate and confirm the bound holds.

The concurrency case is the one that discriminates between the placement options in LADR-02: a counter
column fails it under repeat recall of a popular memory, while appends do not.

## Acceptance Criteria

- Latency difference is within measurement noise; any real difference is recorded with its cause.
- Concurrent recall of one memory shows no contention.
- A deliberately broken feedback path leaves retrieval fully functional.
- Growth is bounded and the bound is stated.

## Applies To

Guiding Principle; LADR-02. This is the constraint that decides the placement.
