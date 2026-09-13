# ADR-0002: Persistence layer architecture

**Status:** Superseded — converted to [HLD 001 — Context memory storage](./001-context-memory-storage/)

Merged with ADR-0001, which described the other half of the same design.

| This ADR's content | Now in |
|---|---|
| PostgreSQL as one engine for relational and document data | [LADR-01](./001-context-memory-storage/ladrs/LADR-01-postgresql-single-engine.md) |
| Hybrid placement rule, seven entities, JSONB shape marker | [LADR-02](./001-context-memory-storage/ladrs/LADR-02-hybrid-placement-rule.md) |
| Stable entity / versioned child, three identity keys | [LADR-03](./001-context-memory-storage/ladrs/LADR-03-stable-entity-versioned-child.md) |
| Versioning absorbs supersession; bump ordering | [LADR-04](./001-context-memory-storage/ladrs/LADR-04-versioning-absorbs-supersession.md) |
| Bitemporal separation | [LADR-05](./001-context-memory-storage/ladrs/LADR-05-bitemporal-separation.md) |
| Constraint strategy, triggers, guard test, soft constraints | [LADR-07](./001-context-memory-storage/ladrs/LADR-07-enforcement-tiers.md) |
| Entity model diagram | [diagrams](./001-context-memory-storage/diagrams/c4-context.md) |
| Search surface and index strategy | [NFR-02 Performance](./001-context-memory-storage/nfrs/NFR-02-performance.md) |
| Relationships as a relational table (`memory_link`) | [LADR-02](./001-context-memory-storage/ladrs/LADR-02-hybrid-placement-rule.md) — superseded in discovery by [HLD 003](./003-graph-edges-on-age/) |

Retained as a pointer so existing references resolve. Do not extend this file.
