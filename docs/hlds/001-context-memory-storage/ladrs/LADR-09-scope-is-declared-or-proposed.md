# LADR-09: Scope is declared or client-proposed; the store never infers it

**Status:** Accepted

## Context

The scope dimension (`product`, `program`, `customer:<id>`, `self`) is a **visibility class**, not a
product name: `program` is hidden from default recall and never cited as shipped fact. The dimension
lives on the group and the store enforces it on read, so a capture under the wrong dimension is stored
correctly and retrieved wrongly — the error is invisible until later.

The dimension has always been the caller's: `--scope`, defaulting to `product` when omitted. That default
is invisible in the stored data, and the optional client-side decision model already judges records for
role value, so the question is whether it should also judge the dimension.

## Decision

**The dimension is declared by the caller or proposed by the client-side decision model, and never
inferred by the store.** The store keeps no classifier; it validates only that the value is one of the
four dimensions, with a non-empty identifier required for `program` and `customer`.

A client-side proposal is opt-in and fail-safe:

- `export --scope auto` plus `CONTEXT_MEMORY_SCOPE_DECISIONS_ENABLED=true` asks one `choice` question per
  candidate, scored against the same threshold as role value.
- Adoption requires **every** candidate to agree above the threshold, because a group carries exactly one
  dimension. A split batch, a low-confidence answer or a failed question keeps `product` and states why.
- `program` takes the bound initiative as its identifier; `customer` is never adopted because no
  identifier is inferable.
- Adoption applies only to a group the export **creates**. `resolve-group` returns an existing
  ticket-bound group unchanged, and the export prints the stored dimension with a note that the proposal
  was not applied.
- A dry run asks the model nothing. A bad setting or an unavailable redactor is a refusal; an unreachable,
  hung or malformed model is a fallback with the reason stated.

## Alternatives Considered

- **Server-side inference** — rejected: it moves a judgement the database cannot express as a constraint
  onto the request path, and the store holds no content to judge.
- **Silent automatic assignment** — rejected: a visibility boundary nobody confirmed is the wrong kind of
  automation. The proposal is disclosed, and any disagreement leaves the default in place.
- **Per-record dimensions** — rejected without a model change: scope is a group property, so a batch that
  disagrees cannot be written under one answer.
- **Auto-reclassifying a store export** — rejected: those records already carry a scope, and re-scoping
  them is the silent boundary move the store-export reconciliation exists to refuse.

## Consequences

- The store stays a pure index with no model dependency; the published scope limit becomes "declared, at
  most proposed".
- `--scope auto` cannot adopt a dimension a batch does not agree on, so heterogeneous session material
  still lands under the default and says so.
- An existing ticket-bound group keeps its scope; changing it remains an explicit `update-group`.
- Adoption is a client-side judgement and degrades quietly, like the rest of the write path's judgement.

## Related

- **LADR-07** — enforcement tiers: scope is enforced on read, not constrained by the schema.
- **HLD-007 LADR-01** — an Understanding rides the default scope with no repo anchor.
