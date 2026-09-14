# LADR-01: A scoped read path, not an extension of the forensic export

**Status:** Draft

## Context

A whole-store Markdown projection already exists as a CLI command. It looks like the natural place to
add a filter, and adding one would be a small change.

It is the wrong place, for reasons its own contract records. The forensic dump exists to answer *"what
is actually in there?"*, so it deliberately **bypasses the retrieval scope rules**, includes
programme-scoped rows and marks them, reads the database sets directly, and never calls the search
abstraction. Its output is complete, unordered and unjudged by design.

A contextual export answers a different question for a different consumer. It is scoped, it must obey
every retrieval rule because its artefact is portable and may be handed to someone else, and it is
curated rather than complete.

## Decision

**Build** contextual export as a separate scoped read path with its own request shape, its own
bounded selection, and full application of the retrieval scope rules. **Leave** the forensic dump
exactly as it is.

The two share a purpose only at the level of "produce Markdown". They differ on every rule that
matters: who may see the output, whether the scope filter applies, whether ordering is meaningful,
whether judgement is applied, and whether omissions must be reported. A single command carrying both
sets of rules would be one flag away from emitting a shareable document containing material the store
hides.

Reuse is welcome where it is genuinely mechanical — rendering primitives, slug and collision rules,
blob hydration, the deterministic-output discipline. Reuse of the *policy* is forbidden.

## Alternatives Considered

- **Add `--repo` / `--ticket` / `--tags` filters to the forensic dump** — rejected: it would inherit the scope-filter bypass, so the most shareable output in the product would be the one least governed by the confidentiality rule.
- **Make the forensic dump a special case of contextual export (empty anchor set)** — rejected: the forensic dump must show hidden material and this must not. One of the two would have to lose its defining property.
- **One command, two modes behind a flag** — rejected: the difference is a security boundary, and a boundary selected by a flag is a boundary someone will cross by accident.

## Consequences

- The confidentiality rule is applied in one direction only, so there is no combination of flags that produces a shareable document containing hidden material.
- The forensic dump keeps its forensic value untouched, and its "generated, never maintained" contract is unchanged.
- Two Markdown producers exist, which is duplicated surface area. Accepted deliberately: the shared part is mechanical rendering, and the unshared part is exactly the part that must not be shared.
- A reader must understand which artefact they are holding. Both declare it in their output banners.

## Related

- **LADR-02** — establishes where judgement lives, which the forensic dump has none of.
- **NFR-01** — the confidentiality rule this separation exists to protect.
