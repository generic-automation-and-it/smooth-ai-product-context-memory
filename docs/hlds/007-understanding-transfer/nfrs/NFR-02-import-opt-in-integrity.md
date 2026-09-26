# NFR-02: Import integrity — no write without `--store`, and import passes through the capture path

**Status:** Accepted

## Requirement

A write occurs only when the `--store` switch is passed. When it is, imported material goes through the
normal capture path — preflight, redaction, deduplication, link derivation and atomicity — rather than
being written directly:

- Imported material split into atomic facts where it bundles multiple.
- A secret in imported material is redacted before the blob write.
- A restatement of an existing subject produces a version bump, not a duplicate row.
- A genuine conflict raised by imported material is surfaced, not silently resolved.
- No imported material is adopted as shipped product fact; proposed status is preserved.

## Verification

- **Skill-level test** — assert the default load exposes no write, and that the `--store` path calls the
  capture path's pre-write stages (redact, dedup, atomicity) rather than a direct `set`.
- **L1** — import a foreign document containing a bundled fact, a secret and a restatement; assert the
  store ends with an atomic split, a redacted blob, and a version bump, not a raw duplicate.
- **L1** — import material that proposes a position contradicting a stored fact; assert a genuine
  conflict is reported and unresolved.

## Acceptance Criteria

- No write occurs without `--store`.
- Imported material receives the capture path's full judgement.
- A secret is redacted before write; no secret reaches the blob or DB.
- A restatement is a version bump, not a duplicate row.
- A genuine conflict is surfaced, not auto-resolved.

## Applies To

Goal 3 (import is opt-in and goes through the capture path), LADR-02, LADR-03. `BR-43`.

## Evidence (2026-09-26)

Accepted on Path A — skill-level evidence for the judgement stages, L1 for the store side.

The redaction, atomicity-split and conflict-surfacing criteria are **judgement stages in the capture
skill** (`mimisbrunnr-context-memory`), which the load skill's `--store` path hands material to. The
import → capture chain is **agent-mediated**: an agent sits between the load skill, the capture skill
and the store, so no C# L1 test can reach those stages end to end without a cross-language harness.
That is an architectural boundary, not a test gap; the resolution here is to accept the skill-level
evidence that exists rather than build a harness bridging the agent boundary (the rejected Path B).

- **Met — skill level (judgement stages):** the capture skill's harness
  `.agents/skills/mimisbrunnr-context-memory/tests/run_tests.py` (75 tests, all green) exercises the
  stages this NFR asserts. Redaction: `test_redact_and_flag_never_rejects`. Atomicity split is a
  *propose, not decide* stage — `test_bundled_tally_is_flagged`, `test_bundled_compound_is_flagged`,
  `test_semicolon_between_clauses_is_bundled`, `test_single_contrastive_junction_is_bundled`,
  `test_noun_phrase_list_alone_is_not_bundled` (a candidate is flagged for the capture path's atomicity
  stage, never split by the client). Conflict surfacing:
  `test_genuine_conflict_composes_proposed_record_and_two_links`,
  `test_scope_mismatch_is_not_a_conflict`.
- **Met — skill level (import routes through the capture path):** the load skill's harness
  `.agents/skills/mimisbrunnr-understanding/tests/run_tests.py` (48 tests, all green) asserts the
  import path refuses without `--store`, and hands material to the capture path rather than writing
  directly.
- **Met — store side (L1):**
  `tests/SmoothAiProductContextMemory.Application.ComponentTest/Features/UnderstandingTransferStoreTests.cs`
  `Understanding_is_written_and_version_bumped_by_the_capture_path` — a restatement carrying the uuid is
  a version bump, not a duplicate row, and a write that skipped preflight (same subject, no uuid) is
  refused with a conflict. This closes the "no write without `--store`", "restatement is a version bump",
  and "capture path's full judgement" criteria at the store boundary.
- **Scope boundary:** "no secret reaches the blob or DB" and "a bundled fact is split" are asserted at
  the skill level as redact/bundle-flag stages; the case where the secret or bundle is produced by the
  agent's judgement rather than by the skill is, by design, outside any automated test's reach.

The residual gap — that the agent between the skills and the store always routes correctly — is
accepted by this decision rather than closed by a cross-language harness.



