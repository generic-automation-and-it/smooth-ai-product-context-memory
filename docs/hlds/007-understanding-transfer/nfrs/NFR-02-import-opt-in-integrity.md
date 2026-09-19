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
