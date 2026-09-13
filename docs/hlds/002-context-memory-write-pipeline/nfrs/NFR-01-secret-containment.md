# NFR-01: Security — secret containment

**Status:** Accepted

## Requirement

- **No content with a detected secret reaches storage.** Detection runs before the body write, with zero tolerance: one leaked object is a failure, because the object is immutable and its address stable.
- **The secret is never emitted anywhere else.** Not in logs, not in error messages, not in the digest. The digest names the candidate and the matching rule only.
- **Detection failure is graceful.** If detection cannot run, the write does not silently proceed unscrubbed.

## Verification

- Plant a representative secret of each supported shape in candidate content, run the pipeline, and assert the stored body contains no occurrence of the planted value.
- Force detection to fail (non-zero exit or timeout from the redaction script) and assert the write is blocked, or the candidate flagged unverified — never silently persisted unscrubbed.
- Assert the digest names the rule and candidate but contains neither the matched span nor surrounding content.
- Capture all log output during a redacting write and assert the planted value appears nowhere in it.
- Assert a candidate with no secret passes through byte-identical, so detection is not corrupting ordinary content.

The log assertion matters as much as the storage one: a secret scrubbed from the body and then printed
in a diagnostic has moved, not been contained — often somewhere less protected than the store.

## Acceptance Criteria

- Zero occurrences of any planted secret in stored bodies, logs or digests.
- Every supported shape has a positive test; adding a shape without a test is incomplete.
- Non-secret content is unmodified.
- **Residual risk is stated, not implied:** detection is fingerprint-based, so a novel secret shape passes. This bounds the risk; it does not eliminate it.

## Applies To

Goal 1; LADR-02, LADR-03.
