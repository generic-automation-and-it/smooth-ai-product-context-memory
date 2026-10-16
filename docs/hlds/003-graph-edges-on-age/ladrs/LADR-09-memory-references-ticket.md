# LADR-09: Memory references a ticket it does not own

**Status:** Accepted, owner-approved (2026-10-15); implemented — read union, snapshots, dossier and clients shipped; release gates passed.
**Owner:** generik0
**Decision date:** 2026-10-15
**Supersedes:** HLD-005 [LADR-11](../../005-contextual-export/ladrs/LADR-11-no-writer-derives-anchor-edges.md) for this one edge only.

## Context

[HLD-005's split-and-export flow](../../005-contextual-export/ladrs/LADR-11-no-writer-derives-anchor-edges.md)
lets a second initiative capture a **WET copy** of material that a ticket in a first initiative owns.
The copy belongs to the second initiative's group, but the shared ticket's owner never moves and is
never shared. Recall under the shared ticket must return both the owner group's originals and the
second initiative's copies. Today it cannot: ticket ownership is exclusive, a ticket query resolves
to its **one** owning group, and no edge links a ticket to a memory — so `--initiative <other> --ticket X`
is always empty.

This LADR adds that missing edge. It is **narrow**: it allows one new edge label and nothing else. It
does **not** change ticket exclusivity, memory identity, scope semantics, or the relational/graph split
already accepted by LADR-02, LADR-03 and LADR-08.

## Decision

### The `ABOUT` edge

- New edge label **`ABOUT`**, directed **`(Memory)-[:ABOUT]->(Ticket)`**. A memory may reference a
  ticket it does not own; the ticket's owning group is unchanged.
- Edge properties are **`reason`** (text, required, ≤ 4000 UTF-16 units) and **`declaredAt`**. No
  subject, claim, scope, validity or membership is copied — LADR-08's identity-only rule on `Ticket`
  vertices is unchanged, and a `Memory` vertex still carries exactly `memory_uuid`.
- The edge lives **only** in the graph, exactly like `LINKS` (LADR-03: replace, never dual-write; no
  fact held in two stores). There is no relational column for the reference.

### Declared, never inferred

- A reference is **declared** by the export binding or an explicit call — the same posture LADR-11's
  "no writer derives anchor edges" codifies. No writer infers an `ABOUT` edge from shared group
  membership, ticket spelling, memory links, scope or any naming convention.

### Write-time ownership rule

- An `ABOUT` target must be an **owned ticket at write time** — exactly one owner, as
  `SetTicketParent` requires. A memory must not reference a ticket its **own** group owns: ownership
  already covers that case, so it is refused rather than duplicating it.

### Invariant change (D1)

- `ticket_graph_membership()` currently `DETACH DELETE`s a `Ticket` vertex once no group's JSONB owns
  it. That would silently drop every incoming `ABOUT` edge. New rule: when the **last owner releases a
  ticket that still has incoming `ABOUT` edges, refuse** the ownership change (raise, like the
  ownership-conflict path) — references are never lost silently.

### Scope (D2)

- A ticket grants **no scope consent**. Memories reached through `ABOUT` are filtered with the
  **non-group-context** scope plan (programme hidden unless explicitly asked), even though the owner
  group's memories keep today's group-context plan. A reference never widens what a ticket sees.

## Target behaviour

With both exports done, a shared ticket `github:231` owned by the Search group:

| Query | Returns |
|---|---|
| `import --initiative Mímisbrunnr-Search --ticket github:231` | The originals in the Search group (it owns 231) |
| `import --initiative Mímisbrunnr-MVP --ticket github:231` | The WET copies in the MVP group (they reference 231) |
| `import --ticket github:231` | Both sets; each copy reachable from its original through a twin `LINKS` edge |

Identical statements at export time. Memory identity stays `(group, uuid)`; the copies can drift, and
the twin link is the drift signal.

## Rejected alternatives

- **A ticket owned by several groups** — breaks `resolve-group`'s one-owner answer and the ownership
  trigger. Rejected.
- **Moving ownership** (`update-group`) — a move, not a copy; the first initiative loses the ticket. Rejected.
- **One shared record visible in both initiatives** (an initiative↔memory association, or a product +
  initiative model) — not the owner's choice; a larger model change. Rejected.
- **Following `TICKET_PARENT` in `import`** — the hierarchy relates tickets to tickets, not memories to
  tickets. Rejected.

## Consequences

- `IMemoryGraph` gains the `ABOUT` reference operations (create, exists, and the reference-uuid lookup
  the read path uses); `NpgsqlMemoryGraph` implements them on the same AGE session and transaction.
  API read/write, snapshot/restore, the kvasir/odin clients and the benchmark carry the new edge.
- This carves out a narrow exception to HLD-005 LADR-11's practitioner-declared ticket-hierarchy
  writer rule for `ABOUT` only; the rest of LADR-11 — including the **Blocked** tag half, which still
  permits no tag edges at all — is unchanged.

## Implementation

- **Write half** (PR #238): `20261015100000_AddAboutReference` migration (label + `ticket_graph_about_count` + D1 trigger change), `IMemoryGraph.AboutExistsAsync`/`CreateAboutAsync`, `SetMemories` `ticketReferences`, `POST /api/context/memories/ticket-references`.
- **Read half** (this change): `IMemoryGraph.ListAboutAsync`, `MemorySearchCriteria.ReferencedUuids` (disjunctive with `GroupId`) plus the reference-branch scope plan, `NpgsqlMemorySearch`'s `(GroupId == owner OR Uuid IN referenced[scope-ok])` predicate, `CheapMemory.TicketMatch` (`owner`/`reference`), the dossier's reference-anchor union with `referencedAnchors` reach, `ABOUT` in the snapshot archive (format v5) capture/restore/reconcile and `scripts/verify-graph-restore.sh`, and the kvasir `export --reference-tickets` flag with its synthetic owner ticket and twin links.
- **Row-level binding read** (HLD-009): `IMemoryGraph.FindAboutAsync` reads the same edges from the memory side — one statement anchored on a page of memory uuids, never a loop per row — so `CheapMemory.Tickets` can name a memory's owned **and** referenced tickets. Ownership is still decided by the group's membership array (LADR-08), so an `ABOUT` edge on a ticket the group owns is one binding, not two. The write-time refusal above makes that state unreachable through the API; the read defends it anyway.

## Changelog

| Date | Change |
|:-----|:-------|
| 2026-10-15 | Authored for the shared-ticket-reference feature. |
| 2026-10-16 | Read half implemented: recall union + `ticketMatch`, snapshot coverage (format v5), dossier reference anchors, client flags. Release gates passed. |
| 2026-10-16 | Row-level binding read: `FindAboutAsync` + `CheapMemory.Tickets` assembled per page from the ownership array and `ABOUT` edges (HLD-009). |
