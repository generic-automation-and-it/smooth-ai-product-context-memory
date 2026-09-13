# Graph edges on Apache AGE — High-Level Design

| | |
|---|---|
| **Status** | Accepted |
| **Owner** | generik0 |
| **Tracker** | Context-memory V2 |
| **Last updated** | 2026-09-13 |

> **Delivered and accepted.** All seven decisions and all four quality requirements are Accepted,
> each against committed evidence: integrity and the relationship contract at the cutover, traversal
> performance in [NFR-02-traversal-measurements.md](./nfrs/NFR-02-traversal-measurements.md), the
> restore round-trip in [NFR-03-restore-verification.md](./nfrs/NFR-03-restore-verification.md), and
> the version pairing plus pre-upgrade check in
> [NFR-04-version-pairing.md](./nfrs/NFR-04-version-pairing.md).
>
> This document delivers **intent + spec** — what we built and why, the decisions behind it, and the
> quality bar it had to meet. It does **not** contain an implementation plan.

## Intent

Memory relationships were a relational table (`memory_link`) at foundation, serving exactly one
access pattern: a one-hop reverse lookup — *what points at this memory?* The stated ambition is larger.
R10 (Context-memory V2 tracker) exists so the store can reconstruct **why** something is true: the chain from a measurement, to
the finding it produced, to the decision it justified. That is a variable-depth path query, and SQL
serves it poorly.

This design introduces **Apache AGE inside the Postgres instance we already run**, and moves
relationships — and only relationships — onto it. Entities, constraints, temporal validity and the
append-only guarantees stay exactly where they are. Nothing new is deployed: AGE is an extension,
not a service.

## Key Goals

### 1. Multi-hop traversal over memory relationships

**Delivered.** A caller can ask *what points at this memory* and *what chain of reasoning connects
these two memories*. The cutover collapsed the five foundation elabels into the open-vocabulary
`relation` property on `:LINKS`; bounded variable-depth traversal followed, exposed as
`POST /api/context/paths`. The relations modelled — `depends_on`, `relates_to`, `contradicts`,
`supersedes`, `implements`, and anything else a caller records — are traversable, not merely
listable.

The capability targeted is provenance reconstruction, not analytics. Bounded paths between known
endpoints, not whole-graph algorithms.

**Acceptance criteria / DoD** — all met

- A bounded variable-depth path query between two memory identities returns the intermediate hops and their relation types.
- Traversal can be filtered by relation type and direction (outbound, inbound, either).
- The existing one-hop reverse lookup remains available and returns the same results as before — pinned by a depth-1-inbound parity test against `ListTouchingAsync`.
- Every traversal is bounded: the depth limit is a `required` property of the query type, so an unbounded path query does not compile (LADR-07).

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

- Every constraint guarantee the existing suite pins still holds; tests were ported to the graph store where the mechanism moved (entity-count guard updated deliberately — see AGENTS.md).
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

LADRs 01–03 are strategic (*what* and *why*); 04–07 are tactical (*how*). Each is a single
decision — a horizontal concern spanning this HLD. See [`./ladrs/`](./ladrs/).

| LADR | Decision | Status |
|------|----------|--------|
| [LADR-01](./ladrs/LADR-01-adopt-age-for-relationships.md) | Adopt Apache AGE in the existing Postgres for relationship storage | Accepted |
| [LADR-02](./ladrs/LADR-02-edges-only-thin-vertices.md) | Vertices carry identity only; all properties stay relational | Accepted |
| [LADR-03](./ladrs/LADR-03-replace-not-dual-write.md) | Replace the relationship table outright; never dual-write | Accepted |
| [LADR-04](./ladrs/LADR-04-connection-session-initialisation.md) | Initialise the AGE session per physical connection | Accepted |
| [LADR-05](./ladrs/LADR-05-edge-integrity-as-invariant.md) | Edge integrity becomes an enforced application invariant | Accepted |
| [LADR-06](./ladrs/LADR-06-anchor-lookups-use-property-predicates.md) | Anchor lookups are property predicates over a btree; `MERGE` keeps the inline map over a GIN | Accepted |
| [LADR-07](./ladrs/LADR-07-every-traversal-carries-its-bound.md) | The depth bound is a `required` property of the query type | Accepted |

## Non-Functional Requirements

Each NFR is a horizontal quality concern spanning the whole design, with a measurable
target, a verification mechanism, and acceptance criteria. See [`./nfrs/`](./nfrs/).

| NFR | Attribute | Target (summary) | Status |
|-----|-----------|------------------|--------|
| [NFR-01](./nfrs/NFR-01-referential-integrity.md) | Integrity | Zero orphan edges; zero duplicate edges | Accepted |
| [NFR-02](./nfrs/NFR-02-traversal-performance.md) | Performance | Depth-3 bounded path p95 ≤ 50 ms at 10k edges | Accepted — measured 0.978 / 0.595 / 8.374 ms p95 in [NFR-02-traversal-measurements.md](./nfrs/NFR-02-traversal-measurements.md); one-hop is 1.4× the [baseline](./nfrs/NFR-02-one-hop-baseline.md), adjudicated as accepted |
| [NFR-03](./nfrs/NFR-03-operability.md) | Operability | No added container; one backup; one-command start | Accepted — restore round-trip in [NFR-03-restore-verification.md](./nfrs/NFR-03-restore-verification.md) |
| [NFR-04](./nfrs/NFR-04-compatibility.md) | Compatibility | Extension must not pin us below a supported Postgres | Accepted — pairing and pre-upgrade check in [NFR-04-version-pairing.md](./nfrs/NFR-04-version-pairing.md) |
