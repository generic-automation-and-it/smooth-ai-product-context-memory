# NFR-01: Integrity

**Status:** Draft

## Requirement

Two invariants hold at all times, each previously guaranteed by the database and now enforced by the
application (LADR-05):

- **No orphan edges.** After a memory is deleted, the count of edges whose source or target is that
  memory's identity is exactly **zero**.
- **No duplicate edges.** For any (source, target, relation) triple, the count of edges is at most
  **one**. The same pair holding several *different* relations remains valid and must not be
  collapsed.

## Verification

Component-level tests against a real database instance, not a stub:

- Create a memory with inbound and outbound edges, delete it, then count edges referencing its identity. Non-zero fails.
- Create the same relationship twice between one pair, then count. Greater than one fails.
- Create two different relations between one pair, then count. Fewer than two fails — this guards against over-correcting uniqueness into a same-pair restriction.
- Force a failure between the edge removal and the memory deletion, then assert both are still present. Any partial outcome fails, proving the two-part delete is genuinely one transaction.

The fourth case is the one that matters most: it proves the transaction boundary rather than the
happy path, and it is the case a hand-written delete path is most likely to get wrong.

## Acceptance Criteria

- All four verification cases pass in the standing test suite, not as a one-off check.
- The orphan and duplicate cases run on every build, so a regression breaks the build rather than accumulating silently.
- The delete path has exactly one implementation; a second delete path is a defect, because the invariant is only as strong as its least careful caller.

## Applies To

Goal 3 (no regression in guarantees already held); LADR-05; the relationship write path and the
memory delete path.
