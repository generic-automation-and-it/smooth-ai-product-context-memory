# LADR-03: Import funnels through the capture path, never a direct write

**Status:** Accepted

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

Accepted on Path A — skill-level evidence. The import→capture chain is **agent-mediated**: the
`--store` path in the load skill hands material to the capture skill (`mimisbrunnr-context-memory`),
which runs the judgement stages. An agent sits between the two skills and the store, so no C# L1 test
can assert those stages end to end — that is an architectural boundary, not a test gap, and the
resolution is to accept the evidence that exists rather than build a harness bridging the agent
boundary.

- The capture path's judgement stages are exercised by the capture skill's own harness:
  `.agents/skills/mimisbrunnr-context-memory/tests/run_tests.py` (75 tests, all green) — redaction
  (`test_redact_and_flag_never_rejects`), atomicity/bundling
  (`test_bundled_tally_is_flagged`, `test_bundled_compound_is_flagged`,
  `test_semicolon_between_clauses_is_bundled`, `test_single_contrastive_junction_is_bundled`,
  `test_noun_phrase_list_alone_is_not_bundled`), conflict surfacing
  (`test_genuine_conflict_composes_proposed_record_and_two_links`, `test_scope_mismatch_is_not_a_conflict`).
- The load skill's import path routes through that capture path rather than a direct write, and refuses
  without `--store`: `.agents/skills/mimisbrunnr-understanding/tests/run_tests.py` (48 tests, all green).
- Store side is separately proven at L1:
  `tests/SmoothAiProductContextMemory.Application.ComponentTest/Features/UnderstandingTransferStoreTests.cs`
  `Understanding_is_written_and_version_bumped_by_the_capture_path` — a restatement carrying the uuid
  is a version bump, not a duplicate row, and a write that skipped preflight (same subject, no uuid) is
  refused with a conflict.


