# Graph edges on Apache AGE — High-Level Design

| | |
|---|---|
| **Status** | In Discovery |
| **Owner** | generik0 |
| **Tracker** | Context-memory V2 |
| **Last updated** | 2026-09-14 |

> Discovery / prototyping HLD. This document delivers **intent + spec** — what we are
> building and why, the decisions behind it, and the quality bar it must meet. It does
> **not** contain an implementation plan; execution (phasing, sub-issues, sequencing) is
> tracked in the issue/work tracker.

## Intent

Memory relationships are currently a relational table (`memory_link`) serving exactly one access
pattern: a one-hop reverse lookup — *what points at this memory?* The stated ambition is larger.
R10 (Context-memory V2 tracker) exists so the store can reconstruct **why** something is true: the chain from a measurement, to
the finding it produced, to the decision it justified. That is a variable-depth path query, and SQL
serves it poorly.

This design introduces **Apache AGE inside the Postgres instance we already run**, and moves
relationships — and only relationships — onto it. Entities, constraints, temporal validity and the
append-only guarantees stay exactly where they are. Nothing new is deployed: AGE is an extension,
not a service.

## Key Goals

### 1. Multi-hop traversal over memory relationships

Today a caller can ask *what points at this memory*. After this change it can ask *what chain of
reasoning connects these two memories*, bounded by depth and relation type, in one query. The
relations already modelled — `depends_on`, `relates_to`, `contradicts`, `supersedes`, `implements` —
become traversable rather than merely listable.

The capability targeted is provenance reconstruction, not analytics. Bounded paths between known
endpoints, not whole-graph algorithms.

**Acceptance criteria / DoD**

- A bounded variable-depth path query between two memory identities returns the intermediate hops and their relation types.
- Traversal can be filtered by relation type and direction.
- The existing one-hop reverse lookup remains available and returns the same results as before.

### 2. Edges only — the graph never owns an entity

Vertices exist solely to give edges something to attach to. A vertex carries the memory's stable
identity and nothing else — no subject, no claim, no scope, no validity. Every property that
describes a memory stays in its relational row.

This is the rule that keeps the design honest. The moment a vertex carries a property that also
exists in a table, there are two copies of one truth and a dual-write problem, which is the failure
mode that discredits most polyglot designs.

**Acceptance criteria / DoD**

- A vertex exposes identity and label only; asserting on any descriptive property fails.
- Answering any question about a memory's content requires joining back to the relational row.
- Adding a memory property requires no graph change.

### 3. No regression in the guarantees already held

The persistence layer earned its guarantees deliberately: exactly one current version per memory,
append-only history enforced by trigger, bitemporal validity, scope enforced at retrieval. None of
them involve relationships, and none of them may weaken.

Two guarantees *do* live on the relationship itself — cascade deletion and edge uniqueness — and
both are provided today by the relational table. Moving edges to AGE forfeits both, because a graph
edge has no foreign key to a table and no unique constraint. This is the design's real cost; it is
faced directly in LADR-05 rather than discovered later.

**Acceptance criteria / DoD**

- Every constraint test in the existing suite passes unchanged.
- Deleting a memory leaves no edge referencing it.
- Creating the same relationship twice between the same pair produces one edge, not two.

### 4. Operability does not degrade

The service is single-user and local-first. It runs as one API container, one database container and
one object store. Adding graph capability must not add a fourth, must not add a second backup, and
must not make a developer's first run harder.

**Acceptance criteria / DoD**

- Container count is unchanged; only the database image differs.
- One backup captures relational and graph data together.
- Local start-up remains a single command with no additional manual setup.

## Core Separation of Concerns

> AGE holds relationships. Relational tables hold entities. Neither takes the other's job.

The split is by *shape of question*, not by convenience. Filtering — by facet, scope, ticket,
repository, validity window, full text — is a multi-predicate problem that indexes solve and
traversal does not. Connection — reachability, path, depth — is a traversal problem that indexes
serve badly.

Our measured access pattern is four parts filter to one part traverse. That ratio is why the
relational core stays authoritative and the graph is additive: the graph earns a place for the one
question tables answer badly, and is given nothing else.

## Guiding Principle — One engine, two query languages

> If it needs a join, it is a table. If it needs a path, it is an edge.

- The graph is an **index over relationships**, never a second home for data.
- A vertex without a corresponding relational row is a defect, not a valid state.
- We will deliberately **not** mirror the agile hierarchy, ticket trees or repository structure into the graph. Those are trees with an authoritative upstream; a copy would need syncing, and syncing is a product.
- We will deliberately **not** adopt a separate graph server. The extension model is the reason this is affordable.

---

## Diagrams

- [System Context (C1), entity/edge boundary (ER), and relationship write & delete flows (sequence)](./diagrams/c4-context.md)

## Architecture Decisions (LADRs)

LADRs 01–03 are strategic (*what* and *why*); 04–05 are tactical (*how*). Each is a single
decision — a horizontal concern spanning this HLD. See [`./ladrs/`](./ladrs/).

| LADR | Decision | Status |
|------|----------|--------|
| [LADR-01](./ladrs/LADR-01-adopt-age-for-relationships.md) | Adopt Apache AGE in the existing Postgres for relationship storage | Draft |
| [LADR-02](./ladrs/LADR-02-edges-only-thin-vertices.md) | Vertices carry identity only; all properties stay relational | Draft |
| [LADR-03](./ladrs/LADR-03-replace-not-dual-write.md) | Replace the relationship table outright; never dual-write | Draft |
| [LADR-04](./ladrs/LADR-04-connection-session-initialisation.md) | Initialise the AGE session per physical connection | Draft |
| [LADR-05](./ladrs/LADR-05-edge-integrity-as-invariant.md) | Edge integrity becomes an enforced application invariant | Draft |

## Non-Functional Requirements

Each NFR is a horizontal quality concern spanning the whole design, with a measurable
target, a verification mechanism, and acceptance criteria. See [`./nfrs/`](./nfrs/).

| NFR | Attribute | Target (summary) | Status |
|-----|-----------|------------------|--------|
| [NFR-01](./nfrs/NFR-01-referential-integrity.md) | Integrity | Zero orphan edges; zero duplicate edges | Draft |
| [NFR-02](./nfrs/NFR-02-traversal-performance.md) | Performance | Depth-3 bounded path p95 ≤ 50 ms at 10k edges | Draft |
| [NFR-03](./nfrs/NFR-03-operability.md) | Operability | No added container; one backup; one-command start | Draft |
| [NFR-04](./nfrs/NFR-04-compatibility.md) | Compatibility | Extension must not pin us below a supported Postgres | Draft |
