# LADR-03: Import funnels through the capture path, never a direct write

**Status:** Draft

## Context

When `--store` is passed, imported material must be written. There are two ways to do it: write
directly, or hand it to the existing capture path.

A direct write bypasses the judgement that makes the store trustworthy. Foreign material — a session,
meeting notes, a transcript — is the least-structured input the store sees. It is the most likely to
be a bundle of several facts (violating atomicity), to carry a secret (needing redaction), or to be a
restatement of an existing subject (needing deduplication and a version bump rather than a new row).
It may also propose a position that contradicts a stored fact.

The "sole writer" invariant (HLD-005 LADR-08) exists precisely so one path decides new-versus-version,
derives links, applies redaction and produces the digest. A direct import would create a second writer
and silently weaken that invariant.

## Decision

Import is **not** a direct write. When `--store` is passed, the load skill hands the material to the
existing capture path — preflight → redact → dedupe/derive-links → atomicity → write — so it receives
the same judgement as any other capture. A genuine conflict or proposed-status question raised by the
material is surfaced, not silently resolved.

## Alternatives Considered

- **Write directly** — rejected: bypasses atomicity, redaction, deduplication and link derivation;
  creates a second writer.
- **Call the API's raw `set` without the skill's pre-write stages** — rejected: the skill's semantic
  dedup and redaction are exactly what foreign, unstructured material needs.
- **Refuse import of foreign material** — rejected: rejects BR-43's intent that material already
  written somewhere can be captured.

## Consequences

- Imported material gains the store's quality guarantees rather than bypassing them (NFR-02).
- The capture skill stays the sole writer; the load skill's `--store` path is a client of it, not a
  second authority.
- A conflict or proposed-status question from imported material is reported for the practitioner's
  judgement, not auto-resolved.

## Evidence (2026-09-26)

Stays **Draft** with NFR-02: the store refuses a direct duplicate write (L1), but that the `--store` path runs every capture stage is not yet evidenced end to end — see NFR-02's open items.
