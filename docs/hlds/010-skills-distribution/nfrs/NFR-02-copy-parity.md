# NFR-02: Copy parity

**Status:** Draft

<!-- One file per quality attribute. This NFR is a horizontal concern spanning the whole HLD. Status lifecycle: Draft → Prototype → Accepted. -->

## Requirement

Every WET copy is byte-identical to the shared source it was copied from
(`decisions_gate.py`, `decisions_rubric.json`, plus `redact.py` and `session_marker.py`
where a plugin carries them), and the parity harness comparing them is green.

## Verification

`md5` equality of each copy pair plus the parity harness
(`parity_decisions_gate.py` and the session-marker parity pin) run green; any drift
fails naming the side that changed.

## Acceptance Criteria

- All copy pairs byte-identical; parity harness green on the moved tree.
- A one-character drift in any copy fails exactly its parity case.

## Applies To

Goal 2; `ai-understanding` and every future cross-plugin copy.
