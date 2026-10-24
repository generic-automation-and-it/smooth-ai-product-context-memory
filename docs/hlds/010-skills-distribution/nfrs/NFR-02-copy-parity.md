# NFR-02: Copy parity

**Status:** Accepted

<!-- One file per quality attribute. This NFR is a horizontal concern spanning the whole HLD. Status lifecycle: Draft → Prototype → Accepted. -->

## Requirement

Every WET copy is byte-identical to the shared source it was copied from
(`decisions_gate.py`, `decisions_rubric.json`, plus `redact.py` and `session_marker.py`
where a plugin carries them), and the parity harness comparing them is green.

## Verification

`ai-understanding`'s parity harness (`tests/parity_decisions_gate.py`) imports the shared gate and
the WET copy and compares rubric bytes, constants, env-name sets, request JSON, classified outcomes
and redaction on the shared secret corpus; `tests/run_tests.py` pins `session_marker.py`'s copy
against the shared original. Both are green. A one-character drift in any copy fails exactly its
parity case — the harness proves its own sensitivity on the rubric bytes.

## Acceptance Criteria

- All copy pairs byte-identical; parity harness green on the moved tree.
- A one-character drift in any copy fails exactly its parity case.

## Applies To

Goal 2; `ai-understanding` and every future cross-plugin copy.
