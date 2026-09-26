# LADR-02: Load injects into context; import is an opt-in `--store` switch

**Status:** Accepted

## Context

BRD-003 wants material loaded into a fresh agent's context, and also wants material capturable back
into the store. The easy reading is one "ingest" operation. But loading and importing have opposite
safety profiles: loading should be free and safe; importing changes the store and must be judged.

If loading ever wrote, a practitioner would be unable to use prior material without mutating the
store. If import were automatic, every session, meeting note or transcript read would create store
records the practitioner never reviewed.

## Decision

Split them into two acts, and make the safer one the default:

- **Load** (default) injects the material into the agent's session context. It writes nothing.
- **Import** is an explicit, opt-in `--store` switch, applied to the same load operation.

The two are never conflated. A load without `--store` is non-destructive; a load with `--store` is a
capture.

## Alternatives Considered

- **One "ingest" operation that always writes** — rejected: mutates the store by default; unsafe for
  using prior material.
- **Always load, never import** — rejected: rejects BRD-003's BO-13; material that should compound
  cannot.
- **Ask every load "store this?"** — rejected: makes the common, safe case a prompt; noise.

## Consequences

- Default behaviour is non-destructive (NFR-01), so loading a session, meeting note or transcript is
  safe by default.
- Import is a deliberate act (NFR-02), so nothing reaches the store without a decision.
- The load skill exposes the `--store` switch as the one thing that turns a read into a capture.

## Evidence (2026-09-26)

Skill L0 `.agents/skills/mimisbrunnr-understanding/tests/run_tests.py`: a default load creates no files and import is refused without `--store`, emitting nothing. The load client is file-only and never reaches the store; L1 `tests/SmoothAiProductContextMemory.Application.ComponentTest/Features/UnderstandingTransferStoreTests.cs` proves the store read paths a load consumes (query, export, dossier) write nothing.
