# DOMAIN_AGENTS.md

## TL;DR

Pure domain model — entities, aggregate roots, and value objects. Zero external dependencies and no I/O.

## Non-Negotiables

- **No outward dependencies.** Domain references no other project and no infrastructure packages (EF Core, ASP.NET, HTTP, serialization). It is the innermost Clean Architecture layer — everything depends on it, it depends on nothing.
- **No I/O or framework concerns.** No persistence, network, logging, or DI registration here — those belong in Infrastructure/Host.
- **Enforce invariants at construction.** Default for domain types. Guard required state in constructors/factory methods so an entity cannot exist in an invalid state. Value objects are immutable and compared by value. (Context-memory persistence rows are exempt — see the ADR-0002 exceptions below.)

## ADR-0002 exceptions (context-memory model)

These are deliberate departures from the defaults above, sanctioned by
[ADR-0002](../../.docs/hlds/adr-0002-persistence-layer-architecture.md). **Do not "correct" them** —
they are the design, not drift.

- **Serialization attribute exception.** `[JsonPropertyOrder]` on `JsonShapeDocument` is the sanctioned
  one-place implementation of the `v` shape marker required by the ADR's JSONB document contract. It is
  the *only* serialization concern permitted in Domain; no other entity may carry serialization
  attributes.
- **Anemic POCOs, not constructor-enforced invariants.** Context-memory entities are persistence rows by
  design. Their invariants are enforced by database constraints (partial unique indexes, the append-only
  trigger, FK and not-null constraints) and, later, the Application validation pipeline — not by
  constructors. Adding constructor guards would duplicate enforcement in a layer that cannot see the
  constraints doing the real work.
- **Value objects still hold.** `Slug` is a pure static value helper with no state and guards its own
  input; the immutability and value-comparison rule is unchanged for anything that is genuinely a value
  object.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-09-09 | Restored the "Enforce invariants at construction" default as the stated Domain default; the ADR-0002 exceptions section now explicitly carves out the context-memory persistence rows. | ADR-0002 |
| 2026-09-09 | Seven-entity context-memory model + `Slug` value logic added; recorded the ADR-0002 serialization and anemic-POCO exceptions. | ADR-0002 |
| 2026-05-30 | Created — empty Clean Architecture domain skeleton (`Entities/`, `ValueObjects/`). | — |
