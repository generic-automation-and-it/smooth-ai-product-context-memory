# LADR-03: Odin stays the documented owner; one read client, one write client, refusal intact

**Status:** Draft

<!-- Status lifecycle: Draft → Prototype → Accepted. Also: "Superseded by LADR-MM", "Deprecated". -->

## Context

The move relocates files that enforce a security boundary: the read client
(`context_memory_read_client.py`) refuses to start with a write token present in any
spelling, and the store rejects a read credential on a write route. If the move
duplicated either client, the boundary would rest on parity tests staying green rather
than on there being one thing to check. Likewise the write path has one authority
(HLD-002; odin is the sole authority on the write path, formerly LADR-005, now LADR-008
after the read/write subagent removal) — a second writer, or a second copy of the
writer, reintroduces the sequencing hazards (e.g. the `is_current` bump ordering) that
single ownership exists to prevent.

## Decision

**Preserve** odin as the documented owner of the capture contract; only the code location
moves. There is exactly one read client and one write client in the plugin after the
move, the read client still refuses with any write-token spelling present
(`CONTEXT_MEMORY_WRITE_TOKEN`, the Host's `ApiAccess__WriteToken`, the controller's
`Parameters__api-write-token`, any case, `:` read as `__`), and the store-side
capability check is untouched.

## Alternatives Considered

- **Duplicate the clients per consumer for independence** — rejected: two writers break
  single ownership; two refusal checks are two chances to drift on a security boundary.
- **Relax the refusal during the move and re-tighten after** — rejected: a boundary down
  "temporarily" across a flag day is the window an accident walks through.

## Consequences

- The move is location-only; no behaviour, route, or credential change ships with it
  (NFR-03).
- Widening WET coverage to the clients stays out of scope permanently, not just for this move.

## Related

- **HLD-002** — the write pipeline whose single authority this preserves.
- **NFR-03** — the singularity-and-refusal bar.
