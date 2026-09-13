# AGENTS.md - Graph edges on Apache AGE

AI Context: HLD for graph edges on Apache AGE. Updated: 2026-09-14

## TL;DR

Memory relationships move from a relational table to graph edges inside the same PostgreSQL instance
via the Apache AGE extension. Intent and goals in [README.md](./README.md); decisions in
[./ladrs/](./ladrs/); measurable quality bar in [./nfrs/](./nfrs/); boundary and flows in
[./diagrams/c4-context.md](./diagrams/c4-context.md).

## Non-Negotiables

- **Never put a descriptive property on a vertex.** A vertex holds the memory's identity and nothing else. Subject, claim, scope, kind, validity and tags stay relational (LADR-02). A vertex with a second descriptive attribute is the failure this design exists to avoid.
- **Never write a relationship to both a table and the graph.** There is one representation, not two with a reconciler (LADR-03).
- **Never delete a memory without removing its edges in the same transaction.** No foreign key cascades into the graph; a two-statement delete outside one transaction can strand orphan edges (LADR-05).
- **Never create a relationship without first checking it does not exist.** No unique constraint exists over edges; uniqueness is a read-before-write, and the same pair holding *different* relations remains valid (LADR-05).
- **Never mirror trackers, repositories or agile hierarchy into the graph.** Those are trees with an authoritative upstream; a copy needs syncing and syncing is a separate product (README, Guiding Principle).
- **LADRs are Draft.** Flag a deviation and raise it; do not silently override.

## Architecture Decisions

See [./ladrs/](./ladrs/).

| LADR | Decision | Why it matters |
|------|----------|----------------|
| [LADR-01](./ladrs/LADR-01-adopt-age-for-relationships.md) | AGE as an extension in the existing instance | One transaction spans relational and graph work. A separate graph server cannot join that transaction, which is why one was rejected |
| [LADR-02](./ladrs/LADR-02-edges-only-thin-vertices.md) | Vertices carry identity only | Prevents two copies of one truth. Code that reads a memory property from a vertex is wrong by construction |
| [LADR-03](./ladrs/LADR-03-replace-not-dual-write.md) | Replace the table; never dual-write | Any transitional dual-write path is a defect, not caution |
| [LADR-04](./ladrs/LADR-04-connection-session-initialisation.md) | Initialise AGE per physical connection | Start-up-only initialisation prepares one pooled connection and leaves the rest failing intermittently |
| [LADR-05](./ladrs/LADR-05-edge-integrity-as-invariant.md) | Edge integrity is an application invariant | Two guarantees the database used to provide are now the code's job |

## Key Behaviors

- **Session preparation is per connection, not per database.** A correctly installed extension still fails on a connection that has not loaded it and set its search path. Failures are load-dependent and intermittent, which makes them easy to misdiagnose as flakiness.
- **Graph catalog writes are transactional and must commit to become visible.** Creating a graph or a label inside an uncommitted transaction leaves it invisible to other sessions. Migration tooling wraps statements in a transaction by default, so these statements are deliberately not transaction-wrapped.
- **A scope that looks like a transaction may be a savepoint.** Releasing a savepoint is not a commit; the catalog write stays invisible until the outer transaction commits.
- **Install ordering matters.** The extension must be created before other roles gain schema-creation rights, or its catalog schema can be pre-created under the wrong owner and installation refuses.
- **Traversal returns identities, never content.** Answering what those memories say requires selecting the relational rows — one composed query, since SQL and Cypher share the session.
- **The entity-count guard changes by one.** Removing the relationship entity is expected and is updated deliberately in the same change; it is not a test to weaken when it fails.
- **WT-01 is additive.** The extension, empty `memory_graph`, vertex label `Memory`, and the five relation edge labels exist; `memory_link` is still the relationship store. Do not write or read edges yet.

## Quality Constraints

Measurable targets and their verification live in [./nfrs/](./nfrs/) — integrity, performance,
operability and compatibility. Two shape how code is written rather than merely how it is measured:

- The delete path must have exactly **one** implementation. The orphan invariant is only as strong as its least careful caller, so a second delete path is a defect.
- Relationship access is written against the database driver directly. The ORM does not model graph objects, so they are absent from its migration model and its snapshot does not describe them.

## Migration Plans

- The relational relationship table is dropped in the same change that creates the graph; existing rows are carried over in that migration (LADR-03). **Not yet:** WT-01 created the empty graph beside `memory_link`.
- Reversal is a corrective migration restoring the table — there is no fallback flag, by design.
- The database image is `docker.io/apache/age:release_PG17_1.7.0` (Postgres 17 + AGE 1.7.0) in both the development and test hosts. Pairing: [nfrs/NFR-04-version-pairing.md](./nfrs/NFR-04-version-pairing.md).

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-09-13 | WT-01 shipped: AGE image pin, per-connection init, empty graph + labels, relational one-hop baseline. `memory_link` untouched. | HLD-003 WT-01 |
| 2026-09-14 | Created — edges-only graph adoption, five LADRs, four NFRs, C1 + ER + sequence diagrams. | Amends HLD 001 |
