# LADR-08: Identity-only tickets and practitioner-declared hierarchy

**Status:** Accepted, owner-approved and implemented; release gates passed on 2026-09-15.
**Performance gate:** PASSED at unchanged p95 <= 100 ms. [Final evidence](../nfrs/NFR-02-ticket-traversal-measurements.md).
**Owner:** generik0
**Decision date:** 2026-09-14
**Supersedes:** [LADR-02](./LADR-02-edges-only-thin-vertices.md), in writing before any ticket-graph migration.

## Context

Ticket selection already resolves an exact provider/key pair to a memory group. Memory-only graph
paths cannot follow a captured parent/child declaration between tickets. Projecting that declaration
onto memory `LINKS` would invent claim relationships and corrupt provenance ordering. Mirroring a
tracker would require reconciliation and freshness guarantees that this local, offline-capable store
does not offer. HLD-005 LADR-09 needs a ticket-specific answer, not permission for arbitrary entities.

## Decision

### Identity and association

- `Memory` vertices retain exactly `memory_uuid`. Add only `Ticket`, with exactly `provider` and
  `key` as identity properties. Preserve existing exact provider/key ownership semantics: no case
  folding, trimming, aliases, URL-derived identity, or synthetic canonical key.
- No descriptive properties on either vertex type. Ticket URL and group membership remain in
  `memory_group.tickets` JSONB; group UUID, scope, repository and initiative remain relational.
  The six-entity EF model stays unchanged.
- Group-ticket change triggers maintain Ticket identities, including groups with no memories.
  Backfill identities from existing JSONB memberships; backfill no hierarchy. Reject ambiguous
  ownership rather than selecting an arbitrary group or merging groups. Removing a membership
  removes its now-unowned vertex and incident hierarchy edges in the same transaction.
- Resolve each ticket's owner live from JSONB, requiring exactly one group. Never cache ownership
  or scope on a vertex. Memories join through that group relationally in the composed SQL/Cypher
  read; there are no Ticket-to-Memory membership edges, group vertices, or membership fanout.
- Group-ticket mutations, their triggers, group-delete cleanup and hierarchy mutations share one
  transaction-scoped advisory lock. Ownership checks and hierarchy validation occur under that
  same lock before mutation, preventing ownership/hierarchy races. Existing exact ownership and
  additive/idempotent group-ticket API behavior remain; no membership-removal API is implied.

### Captured hierarchy, not a tracker mirror

- The only new edge label is `TICKET_PARENT`, directed **parent -> child** between Ticket vertices.
  Each declaration carries mandatory `reason` and `source`, optional `observedAt` (source observation
  time), and mandatory server-recorded `recordedAt`. Neither timestamp asserts upstream freshness.
- A child has at most one parent; self-parenting and cycles are rejected, including under concurrent
  writes. Forest integrity checks cover the actual hierarchy, not a visibility-filtered view or the
  retrieval depth limit. Failures must not disclose hidden identities or graph structure.
- Set, reparent and remove are explicit operations with an expected-parent precondition. Set
  requires expected absence; reparent and remove name the expected current parent by exact identity.
  A stale expectation conflicts without changing state. Reparent replaces the old edge atomically;
  removal deletes the declaration. Replacing a declaration requires its reason/source anew.
- `ExpectedParent` is checked **before** the identical-state no-op. A request naming the current
  expected parent and identical parent/reason/source/observedAt returns `changed: false`, preserving
  `recordedAt`. Replaying the initial set with its old null expectation conflicts after the set
  succeeds. There is no operation replay token or automatic reconciliation of an uncertain outcome.
- This graph is **current state, not history**. Reparent/remove do not retain historical edges;
  `recordedAt` is declaration metadata, not an append-only audit trail.
- Only practitioner-declared relationships are captured, under
  [HLD-002 LADR-08](../../002-context-memory-write-pipeline/ladrs/LADR-08-practitioner-declared-ticket-hierarchy.md).
  No inference from shared group membership, ticket spelling, memory links or tracker polling.
  Blocker, synonym, tag, repository and initiative graphs are not approved by this decision.
- Never project hierarchy onto memory `LINKS`. Sharing a group associates memories with each of its
  tickets; it does not establish a parent relation or a relation between any two memories.

### Separate bounded ticket traversal

- Provide a separate ticket traversal contract, not a reinterpretation of `IMemoryTraversal` or
  `POST /api/context/paths`. `maxDepth` is required and validated to **1..5** at both request and
  store boundaries, with no default. It bounds ticket hops, not memory provenance hops.
- Return deterministic, capped ticket paths with ordered hops and declaration metadata, plus a
  separately capped set of distinct **current, non-proposed** memories associated with the endpoint
  groups of the **selected, capped ticket paths**, unioned with the anchor's group. Paths excluded
  by the path cap do not contribute memories. Include the anchor's memories even without an edge, subject to
  the same visibility, narrowing and memory cap. This is not dossier history selection.
- Order eligible paths by depth then the ordered sequence of exact provider/key pairs (ordinal
  comparison); break any remaining tie by directed edge identity. Order distinct memories by UUID
  then version. Apply declared path and memory caps only after visibility filtering and stable
  ordering; deduplicate before the memory cap. Cap values are stated in the response, not hidden
  defaults or invented dossier-wide numeric limits.
- Resolve **every** ticket on a path, including anchor and intermediates, to exactly one live owner
  in the composed read. Missing or ambiguous ownership fails closed: admit no such path or anchor
  contribution. Never choose the first owner.
- Gate every owner against `MemoryScopeFilter.HiddenDimensions`, not `Plan().ExcludedDimensions`.
  If any hop is hidden, **drop the whole path, never shorten it**. Gate before returning ticket
  identities, reasons, sources, timestamps, memory fields or cap metadata.
- Apply endpoint `Plan()` narrowing to returned memories, including anchor-associated memories.
  A ticket identifier is **not consent** to a hidden dimension; only explicit scope consent can
  authorize it. Relational ticket lookup's existing in-group shortcut must not grant traversal
  consent. Visible intermediates need not match endpoint narrowing, but must pass the hop gate.
- Always disclose generically: **undeclared upstream hierarchy was not followed; upstream
  freshness is unverified**. Report visible-result depth/path/memory cap flags without hidden IDs, hidden
  counts, reasons, or flags whose value reveals that hidden paths exist. Never claim full tracker
  coverage or infer missing upstream parents from a locally rootless ticket.

### Lifecycle and reversal

- Deleting a memory removes only its Memory vertex and incident `LINKS`; it does **not** delete
  ticket identities or hierarchy, including when the group's final memory is deleted.
- Group deletion removes its Ticket vertices and all incident `TICKET_PARENT` edges in the same
  transaction as relational deletion and existing memory cascade. Trigger failure rolls back both
  graph and relational changes. Retain one trigger-owned cleanup path per owning row type.
- The ticket-graph migration's Down warns that captured hierarchy declarations will be
  lost, then removes ticket-specific triggers/indexes/labels and declarations only. It preserves memory
  `LINKS`, Memory vertices, relational ticket JSONB and all other relational metadata. Reapplying
  backfills identities, not lost declarations. This differs from reversing the historical memory
  relationship cutover in LADR-03.

## Implementation and Release Gate

`ITicketGraph` is separate from `IMemoryGraph`/`IMemoryTraversal`, implemented by
`NpgsqlTicketGraph`. `PUT /api/context/tickets/parent` dispatches `SetTicketParent` and returns
`changed`; explicit `parent: null` removes. The API treats null/absent `expectedParent` as expected
absence; the skill requires the field explicitly. Reason/source are required even for removal.
`POST /api/context/tickets/paths` dispatches `FindTicketPaths`: required depth 1..5, outbound/inbound/
either (outbound default), optional scope/kind and separate limits defaulting to 50, each capped at 200.

`20260914180000_AddTicketGraph` owns labels, identity backfill and group-ticket triggers.
`trg_ticket_graph_lock` locks before the group statement; `trg_ticket_graph_membership` maintains
identities after each insert/ticket update/delete and rejects cross-group exact ownership conflicts.
Mutation and group handlers share advisory transaction lock `(734921, 1)`. Identity equality uses
separate HASH expression indexes on provider and key, plus GIN on properties for MERGE. It is **not**
a composite btree over unrestricted provider/key strings: existing JSONB keys must not fail btree
entry-size limits. LADR-06's Memory btree prescription is Memory-specific; its property-predicate
rule still applies to Ticket lookups. Ticket endpoint validation bounds each identity component to 512 characters.

Traversal composes a **Cypher anchor lookup with recursive SQL over indexed AGE adjacency** in one
statement; it does not use variable-length Cypher expansion. Materialized identities parse properties
once; exact membership joins use ordinal `C` collation. Live distinct JSONB memberships yield
only exactly-one, visible owners before expansion. SQL orders paths by depth, provider/key sequence
under `C` collation, then edge IDs; selected endpoint groups plus anchor are unioned before the
current-memory join and UUID/version memory cap. One extra visible hop supplies depth disclosure.
Cycle checks separately use unbounded recursive SQL over AGE adjacency, independent of read scope.

Release acceptance is recorded against the [final evidence](../nfrs/NFR-02-ticket-traversal-measurements.md):
the full suite, explicit benchmark cases, build and deterministic skill tests passed. Requested depth 5
was measured on a three-deep hierarchy, not a five-deep chain. The following remain regression obligations:

- Exact identity and property-shape checks; zero/one/multiple-owner cases; empty-group backfill;
  no inferred edges; no membership fanout or change to existing ticket ownership semantics.
- Set/reparent/remove expectations; one-parent and cycle rejection under concurrent hierarchy and
  group-ticket mutation; rollback on failure; group deletion across incoming/outgoing edges;
  memory deletion preserving hierarchy; warned Down preserving `LINKS` and JSONB.
- Depth boundaries; deterministic path/memory caps; deduplication across tickets in one group;
  anchor inclusion; current/non-proposed filtering; missing/ambiguous ownership; hidden anchor and
  intermediate paths dropped whole; explicit scope versus endpoint narrowing; no ticket consent.
- Compare responses with and without hidden-only branches: visible data and cap flags must not
  disclose the hidden branch. Verify generic completeness/freshness disclosures on empty and capped
  results as well as normal results.
- Preserve every existing [NFR-02](../nfrs/NFR-02-traversal-performance.md) memory-query budget and
  its recorded baseline adjudication. The additional composed ticket query must achieve
  **p95 <= 100 ms** with live ownership joins and hop gates active on a hub-active fixture exercising
  actual multi-hop traversal, not an empty graph, zero-hop lookup or bypassed gate. Record plans and
  measurements separately; existing memory evidence does not establish ticket performance.

## Alternatives and Consequences

- Keeping memory-only vertices leaves captured ticket hierarchy unreachable. Supersession is narrow:
  identity-only remains mandatory; arbitrary non-Memory labels remain unapproved.
- Live tracker projection or reconciliation is rejected: offline reads must not depend on upstream
  access, and captured declarations must not pretend to be a synchronized authoritative mirror.
- Memory-link projection and membership fanout are rejected: they invent provenance and scale with
  group memory counts instead of ticket relationships.
- Explicit declarations provide partial, potentially stale coverage. Generic disclosure is permanent;
  current-state replacement deliberately does not preserve declaration history.
- Approval resolves the ticket representation decision in HLD-005, not the full dossier design or
  tag identity/synonym decisions. Migration, API, skill transport and benchmark code now exist;
  final full-suite and explicit benchmark evidence close the release gates, not merely the earlier
  targeted checks. This records acceptance, not publication of a release.
