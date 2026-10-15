# LADR-10: Product names are a graph-only registry linked to initiatives and repos

**Status:** Accepted, owner-approved (2026-10-15); implemented by this change — release gates pending.
**Owner:** generik0
**Decision date:** 2026-10-15

## Context

Recall is currently reachable by initiative, ticket, repo and scope — but not by **product**. The business
question "everything used by product X" has no answer today, and probing the existing `product` scope
dimension would answer the wrong question: scope marks what *kind of fact* a memory is (citable product
behaviour vs programme/customer/self material), almost always carries no identifier, and a group holds
exactly one dimension, so it cannot express a many-to-many product↔initiative relationship. A live-store
sample (200 memories, 21 groups) found every one scope `product` with no identifier and nothing to migrate.

## Decision

### Graph-only registry, no relational row

- **`Product` is graph-native** (a vertex), exactly like `LINKS` and `Ticket`. **No relational table, no
  group column, no `product` scope change.** Products and their links are queried through the graph
  ("all initiatives used by product X" is one hop), and a memory query by product resolves through the
  graph first.
- A `Product` vertex carries `name` (identity, exact, ≤ 200, no leading/trailing whitespace, no control
  chars), optional `description`, and `status` (`active`/`archived`).
- `Initiative` and `Repo` are **identity-only references** (`name` only), the same exception LADR-08 made
  for `Ticket`. `Initiative` is MERGEd when first linked and only if it exists in the relational registry;
  `Repo` is MERGEd on demand with the `owner/name` format.

### Edges

- **`HAS_INITIATIVE`** — `(Product)-[:HAS_INITIATIVE]->(Initiative)`, properties `reason`, `source`,
  `declaredAt`; at most one per pair.
- **`HAS_REPO`** — `(Product)-[:HAS_REPO]->(Repo)`, same properties; **a repo has at most one incoming
  `HAS_REPO`** (one product per repo, many repos per product).

### Enforced invariants

Uniqueness the graph cannot express — one `Product` per name, case-folded collisions refused (`Gmail` vs
`gmail`), one product per repo, one edge per pair — is enforced in the handlers under a transaction-scoped
advisory lock, the same pattern as ticket ownership. An unknown `product-name` is **refused, never created
implicitly** (register first, like initiatives).

### Attribution and disclosure

- A memory is "of product X" when its group's **initiative** is linked to X, or (Part F) its group's
  **repo** is linked to X.
- A **shared** initiative (linked to more than one product) returns memories of every product linked to it;
  responses disclose this via `sharedInitiative`. Per-memory or per-group product edges are rejected for now.
- `/query productName` is a 404 for an unregistered product (never an empty result that looks like a miss);
  items report `productMatch: "initiative" | "repo"` and `sharedInitiative`.

### Naming rule

The term **`product-name`** is distinct from the scope dimension **`product`**. Scope is untouched and
nothing is migrated; the registry is always referred to as `product-name`, and `product` alone keeps meaning
the visibility class.

## Rejected alternatives

- **A relational product table or a group column** — the owner chose graph-only; no fact in two stores (LADR-02/03).
- **Reusing the `product` scope identifier** — scope is a visibility class with a single dimension; it cannot
  express many-to-many, and there was nothing to migrate.
- **Creating a product implicitly on export/link** — an unknown product is refused so a typo never creates
  a registry entry.
- **Per-memory or per-group product edges** — attribution is at initiative/repo granularity only.
- **Customer → product links** — out of scope.

## Consequences

- `IProductGraph` (+ `NpgsqlProductGraph`) is a separate graph abstraction, mirroring the
  `ITicketGraph`/`IMemoryGraph` split; `IMemoryGraph` stays memory-only.
- `Product`/`Initiative`/`Repo` vertex labels and `HAS_INITIATIVE`/`HAS_REPO` edge labels are added in one
  migration; snapshot/restore and the archive format (v3 → v4) carry them in the same change, so a
  snapshot never omits the graph data a later restore needs.
- API read/write, the kvasir/odin clients and the docs carry the new `product-name` term.

## Implementation

Referenced from this change: `2026…_AddProductGraph` migration, `Features/ProductNames/*` handlers,
`IProductGraph`/`NpgsqlProductGraph`, `QueryMemories` product resolution, `SetMemories` product link, and
the product-name endpoints.

## Changelog

| Date | Change |
|:-----|:-------|
| 2026-10-15 | Authored for the product-name registry feature. |
