# Context memory storage — High-Level Design

| | |
|---|---|
| **Status** | Accepted — implemented |
| **Owner** | generik0 |
| **Tracker** | Context-memory MVP |
| **Last updated** | 2026-09-14 |

> Converted from ADR-0001 (blob storage and content addressing) and ADR-0002 (persistence layer
> architecture), which were a single design expressed as two documents. This HLD delivers **intent +
> spec**; execution is tracked in the issue/work tracker.

## Intent

The context-memory service stores atomic facts captured from agent sessions, retrievable long after
the originating session ends. Ordinary CRUD storage cannot express four things this requires: claims
that change while their subject does not, knowledge that goes stale independently of when it was
recorded, subjects that recur across unrelated work items, and document bodies too large to belong in
a queryable row.

This design makes **PostgreSQL an index over content it does not hold**. Entities, relationships,
metadata and every filterable attribute live in the database; document bodies live in
content-addressed object storage, referenced by hash.

A fifth constraint shapes everything: the service is **single-user and local-first**. Write throughput
is negligible and read latency uncritical, so **schema complexity is a real cost and performance is
not** — which inverts the usual normalisation trade-off.

## Key Goals

### 1. A claim can change without losing why the old one was true

The subject of a memory is stable; the claim about it is not. Carrying forward the reasoning behind a
superseded claim is frequently more valuable than the current answer, and a design that requires
someone to remember to copy it forward will lose it.

Separating the stable subject from the volatile claim makes "same subject, new claim" expressible, and
makes the prior reasoning structurally present rather than manually preserved.

**Acceptance criteria / DoD**

- Recording a new claim on an existing subject produces a version, not a duplicate record.
- Exactly one version of a memory is current at any time, enforced by the database.
- A superseded version and its reasoning remain readable without special tooling.
- No write path can update history in place.

### 2. Stale knowledge is distinguishable from corrected knowledge

A fact recorded today may describe something that stopped being true months ago. Equally, a record may
be corrected without the world having changed at all. Conflating the two makes every staleness
judgement unreliable, because a typo fix becomes indistinguishable from a real-world change.

**Acceptance criteria / DoD**

- Real-world validity and record history are stored on independent axes, neither derived from the other.
- A query can ask what is true now, and separately what the store believed at a past moment.
- Closing a validity window and recording a correction are distinguishable operations.

### 3. Retrieval is one indexed query across every filter dimension

The target question combines facets, tags, ticket, repository, initiative, scope, temporal validity
and full text. All of it must be served by indexes in a single query — materialising rows and
filtering them in application code would defeat the indexes and pull whole version chains across the
wire.

**Acceptance criteria / DoD**

- The compound retrieval query is served by indexes with no sequential scan at expected volume.
- Retrieval returns cheap fields only; document bodies are fetched on explicit drill-down.
- An empty result is a normal response, not an error.

### 4. Document bodies never enter the database

The database is an index. Bodies are content-addressed in object storage, so identical content stored
twice yields one object and one address, making writes idempotent and safe to retry.

This has a consequence that must be accepted rather than discovered: a stored object is **immutable
and its address is stable**, so content written by mistake cannot be edited out — only orphaned.

**Acceptance criteria / DoD**

- The database holds a content address, never a document body.
- Writing identical bytes twice produces one stored object.
- A write interrupted and retried produces no duplicate object.
- The storage abstraction exposes no engine concepts — no buckets, keys or endpoints.

## Core Separation of Concerns

> The database indexes content it does not hold.

Every filterable attribute — subject, claim, kind, facets, tags, scope, validity, provenance — is a
queryable column or array in PostgreSQL. Every body — evidence, attachments — is an immutable object
addressed by the hash of its content.

The split is by *queryability*, not by size. A field that participates in a predicate belongs in the
database however long it is; a field that is only ever read whole belongs in the object store however
short it is.

## Guiding Principle — Hard where it can be, honest where it cannot

> Constraints the database can enforce, it enforces. Constraints it cannot, we name as soft and verify.

- Fields filtered on constantly get **typed indexed columns**; JSONB is for the unforeseen long tail. JSONB queryability is not a substitute for an index on a field filtered every request.
- Where a guarantee cannot be a constraint, it becomes a **mechanism** — a trigger, a guard test — never a convention in prose.
- Two constraints are **soft by necessity** and are labelled as such rather than quietly assumed.
- We will deliberately **not** normalise for its own sake. Six entities were removed because the constraints they bought were unneeded, contrary to the design, or replaceable at zero cost.

---

## Diagrams

- [System Context (C1), containers, and the entity model (ER)](./diagrams/c4-context.md)

## Architecture Decisions (LADRs)

LADRs 01–06 are strategic (*what* and *why*); 07 is tactical (*how*). See [`./ladrs/`](./ladrs/).

| LADR | Decision | Status |
|------|----------|--------|
| [LADR-01](./ladrs/LADR-01-postgresql-single-engine.md) | PostgreSQL as one engine for relational and document data | Accepted |
| [LADR-02](./ladrs/LADR-02-hybrid-placement-rule.md) | Placement rule — typed columns for hot filters, JSONB for the long tail | Accepted |
| [LADR-03](./ladrs/LADR-03-stable-entity-versioned-child.md) | Stable entity row with a versioned child row | Accepted |
| [LADR-04](./ladrs/LADR-04-versioning-absorbs-supersession.md) | Versioning absorbs supersession | Accepted |
| [LADR-05](./ladrs/LADR-05-bitemporal-separation.md) | Business time and system time are independent axes | Accepted |
| [LADR-06](./ladrs/LADR-06-content-addressed-blob-storage.md) | Bodies in content-addressed object storage; database holds the reference | Accepted |
| [LADR-07](./ladrs/LADR-07-enforcement-tiers.md) | Three enforcement tiers — constraint, mechanism, verified-soft | Accepted |

## Non-Functional Requirements

See [`./nfrs/`](./nfrs/).

| NFR | Attribute | Target (summary) | Status |
|-----|-----------|------------------|--------|
| [NFR-01](./nfrs/NFR-01-integrity.md) | Integrity | Every stated constraint provably enforced or provably soft | Accepted |
| [NFR-02](./nfrs/NFR-02-performance.md) | Performance | Compound retrieval index-served, no sequential scan | Accepted |
| [NFR-03](./nfrs/NFR-03-recoverability.md) | Recoverability | Two stores restore to a mutually consistent state | Draft |
| [NFR-04](./nfrs/NFR-04-inspectability.md) | Inspectability | A human can read the store without a database client | Draft |
| [NFR-05](./nfrs/NFR-05-confidentiality.md) | Confidentiality | Content never logged; immutability implications stated | Accepted |
