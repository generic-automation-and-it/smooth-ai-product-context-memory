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

## Verification Status

Where each check runs today. Detection is client-side: the `mimisbrunnr-odin-context-memory` skill scrubs
before the request is built, and the Host stores what it is sent.

| Check | Where it is verified | Passes |
|---|---|---|
| Planted secret of each shape absent from what is sent for storage | Skill harness `.agents/skills/mimisbrunnr-odin-context-memory/tests/run_tests.py` — `RedactTests`, `SecretShapeCoverageTests`, `SetRedactionGateTests` (planted value in every declared text field) | Yes |
| Detection failure blocks the write | Same harness — `SetRedactionGateTests.test_unavailable_redactor_refuses_the_write`, `test_redactor_refusal_carries_no_content` | Yes |
| Digest names rule and location, never the matched span | Same harness — `SetRedactionGateTests.test_digest_reports_rule_names_counts_and_locations_only`, `RedactionPrecisionTests.test_set_digest_names_the_field_and_offsets_of_every_scrub` | Yes |
| Non-secret content byte-identical | Same harness — `RedactionPrecisionTests.test_ordinary_prose_passes_byte_identical` | Yes |
| Planted value in no log line | Server: `tests/SmoothAiProductContextMemory.Host.IntegrationTest/SecretContainmentTests.cs` — every Serilog level and category forced to `Verbose`, message, template, structured properties and scopes captured, during a successful write and during rejected writes (validation 400 and body-deserialisation 400) | Yes |
| Planted value in no error response body | Same test, both rejected writes | Yes |

**Not implemented: a server-side backstop.** The Host runs no detection of its own; a caller that
bypasses the skill (direct HTTP, another client) stores whatever it sends. The client gate is the
control. Whether a server-side backstop is required is an open owner decision, not settled here.

**Observed residual, not covered by the above:** a duplicate-subject `409` names the subject slug in
its detail, and the slug is derived from `description`. A secret placed in `description` therefore
reaches that error body, lower-cased with separators replaced. The skill scrubs `description` like
every other text field, so this depends on the same client gate.

## Acceptance Criteria

- Zero occurrences of any planted secret in stored bodies, logs or digests.
- Every supported shape has a positive test; adding a shape without a test is incomplete.
- Non-secret content is unmodified.
- **Residual risk is stated, not implied:** detection is fingerprint-based, so a novel secret shape passes. This bounds the risk; it does not eliminate it.

## Applies To

Goal 1; LADR-02, LADR-03.
