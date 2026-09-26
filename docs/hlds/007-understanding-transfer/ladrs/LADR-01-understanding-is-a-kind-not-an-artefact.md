# LADR-01: Understanding is a memory of `kind = understanding`

**Status:** Accepted

## Context

BRD-003 defines an **Understanding** — a distilled, self-contained unit of hard-won knowledge inherited
across lifecycles and iterations. The question is how it is stored.

Two instincts are tempting and both are wrong. The first is a separate artifact — a new file convention or
a new table. That re-introduces what EXPORT_AGENTS forbids (a 7th entity, a schema change) and splits the
write path, so an Understanding would be written by a different mechanism than every other fact. The second
is to define it as something memory is not, forcing memory's scoped, single-repo shape onto a cross-repo
learning.

But an Understanding is recognisably a fact about a subject, with a claim and provenance — it is a memory.
Its only real difference is that it is **cross-repo and cross-scope**, and that is a property of the
**group anchor**, not of the memory row.

## Decision

Store an Understanding exactly like any other memory — one subject with versioned claims — and classify it
with `kind = understanding`. Add it as a constant in `KindValue`.

Its group carries the **default** scope values and **omits the repository anchor**, so it is not tied to
one repo and can cross repos. The model is unchanged: `ScopeDimension` stays required (defaulted, not
nullable), `Repo` stays optional, and an Understanding simply rides a group that has no repo. It is written,
versioned and recalled by the existing machinery — the same `is_current` version semantics, the same
defaults, no new column.

This is a deliberate choice to keep the model strong: cross-repo behaviour comes from **default values and
omitting the repo anchor**, not from weakening scope to nullable.

## Alternatives Considered

- **A separate `/understandings/<slug>.md` file convention** — rejected: a parallel store outside the DB;
  it breaks database-as-truth and cannot be recalled by label or similarity.
- **A new `understanding` entity/table** — rejected: EXPORT_AGENTS forbids a 7th entity and it splits the
  write path.
- **Make `ScopeDimension` nullable to represent cross-repo** — rejected: it weakens the whole store. Default
  values and no repo anchor already give cross-repo reach.
- **A separate file dump for Understandings** — rejected: the whole-store export already renders memories; an
  Understanding rides on it as a kind.

## Consequences

- The write path, retrieval path and export path all handle an Understanding with machinery they already
  have.
- `kind = understanding` is retrievable by label/similarity like any other kind.
- Cross-repo reach is achieved without a schema change or nullable scope.
- The only code change is the new constant plus whatever renders/validates the kind.

## A boundary worth naming

`Repo` is a **query filter**, not just an anchor: `NpgsqlMemorySearch` applies
`WHERE group.repo = <requested>` when a repo is supplied, so a memory with `Repo = null` is **not
recalled under a repository-scoped query** — it is recalled only by an un-scoped query or by the
breadth `--all` path. So "an Understanding crosses repos by omitting the repo anchor" is specifically
true for **un-scoped recall**, and an Understanding is deliberately **not** returned when a caller
queries one specific repo. That is the intended behaviour — a cross-repo learning is not a fact of any
one repo — but an implementer must not assume a `Repo = null` understanding will surface under a
repo-filtered `query`. If a repo-scoped query should also surface it, that is a separate retrieval
decision, not a storage one.

## Evidence (2026-09-26)

L1 `tests/SmoothAiProductContextMemory.Application.ComponentTest/Features/UnderstandingTransferStoreTests.cs`: an `understanding`-kind memory is written and versioned through `SetMemories` with no new entity or table; its group keeps a non-null product `ScopeDimension` and no repo; an un-scoped query returns it and a one-repository query does not (the boundary above).
