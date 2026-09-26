# NFR-02: Import integrity — no write without `--store`, and import passes through the capture path

**Status:** Draft

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

## Evidence and open items (2026-09-26)

Status stays **Draft**.

- **Met — store side:** L1 `tests/SmoothAiProductContextMemory.Application.ComponentTest/Features/UnderstandingTransferStoreTests.cs` `Understanding_is_written_and_version_bumped_by_the_capture_path` — a restatement carrying the uuid is a version bump, not a duplicate row, and a write that skipped preflight (same subject, no uuid) is refused with a conflict. Skill L0 `.agents/skills/mimisbrunnr-understanding/tests/run_tests.py`: no write and nothing emitted without `--store`.
- **Open:** the redaction, atomicity-split and conflict-surfacing criteria are judgement stages in the capture skill (Python), not in the backend. The import → capture chain is mediated by the agent, so no L1 test can assert "a secret is redacted before the blob write" or "a bundled fact is split" end to end without a cross-language harness. Closing this needs either that harness or an explicit decision to accept skill-level evidence for those stages.

