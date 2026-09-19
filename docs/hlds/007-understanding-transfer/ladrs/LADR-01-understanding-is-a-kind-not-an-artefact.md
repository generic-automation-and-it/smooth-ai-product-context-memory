# LADR-01: Understanding is a kind, not an artefact

**Status:** Draft

## Context

BRD-003 defines an **Understanding** — a distilled, self-contained unit of hard-won knowledge. The
question is how it is stored.

The natural instinct is to give it its own structure: a new file convention (`/understandings/<slug>.md`,
as the originating repo used), a new table, or a new entity. Each of those re-introduces what EXPORT_AGENTS
forbids (a 7th entity, a schema change) and splits the write path — an Understanding would then be
written by a different mechanism than every other fact.

But an Understanding is recognisably a fact about a subject, with a claim and provenance. It is a
*memory* — just one whose purpose is transfer rather than recall-an-answer.

## Decision

Store an Understanding exactly like any other memory — one atomic subject with versioned claims —
and classify it with `kind = understanding`. Add it as a constant in `KindValue` (the open,
non-enum `kind` vocabulary), where it is a recognisable, retrievable value. No new entity, no new
table, no new file-dump convention.

The five-part shape of an Understanding is carried by the memory; the mapping onto existing fields is
fixed in LADR-04.

## Alternatives Considered

- **A separate `/understandings/<slug>.md` file convention** — rejected: it is a parallel store
  outside the DB, not a fact in the store; it breaks the "database-as-truth" principle and cannot be
  recalled by label or similarity.
- **A new `understanding` entity/table** — rejected: EXPORT_AGENTS forbids a 7th entity, and it splits
  the write path.
- **A separate file dump for Understandings** — rejected: the whole-store export already renders
  memories; an Understanding rides on it as a kind.

## Consequences

- The write path, the retrieval path and the export path all handle Understandings with the machinery
  they already have.
- `kind = understanding` is retrievable by label/similarity like any other kind (via the existing
  facet/kind selectors), so a practitioner can pull Understandings back.
- The only code change is a new constant in `KindValue` plus whatever renders/validates the kind.
