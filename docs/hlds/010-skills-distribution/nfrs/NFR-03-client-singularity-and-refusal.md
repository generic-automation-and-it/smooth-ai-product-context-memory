# NFR-03: Client singularity and refusal

**Status:** Draft

<!-- One file per quality attribute. This NFR is a horizontal concern spanning the whole HLD. Status lifecycle: Draft → Prototype → Accepted. -->

## Requirement

Exactly one read client and one write client exist in the `mimisbrunnr` plugin, and the
read client exits non-zero naming the variable (never its value) when any write-token
spelling is present in its environment.

## Verification

File count of `*read_client.py` / `*write_client.py` (one each) plus the existing
read-client refusal tests run unchanged and green — the move adds no new refusal
wording to pin, only the location changes.

## Acceptance Criteria

- One read client, one write client, no duplicates under any name.
- Every write-token spelling is refused; the refusal names the variable, never a value.

## Applies To

Goal 3; LADR-03 and the HLD-002 write path.
