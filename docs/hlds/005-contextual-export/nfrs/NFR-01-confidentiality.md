# NFR-01: Confidentiality

**Status:** Draft

## Requirement

**Zero** memories hidden from ordinary retrieval appear in a bundle or a dossier, at **any** widening
depth, and regardless of whether they were requested directly or reached by relationship.

Specifically:

- A memory whose scope dimension is hidden for the caller is absent — as a body, as an identity, and as an endpoint of a reported edge. Its edges' reasons are absent too, since a reason discloses the memory it describes.
- A widening path routed **through** a hidden memory is dropped, not repaired by skipping the hidden hop. Reporting the far end would disclose that a connection exists.
- Reading a hidden dimension requires the caller to declare that dimension, exactly as the blob proxy and version history already require. An undeclared request receives the narrowed result, never an error that reveals what was withheld.
- Every dossier artefact carries a sensitivity banner naming the store it came from, and is written to a location excluded from version control and from any synchronisation by default.

## Verification

- **L2** — seed a store where a programme-scoped memory is reachable at depth 2 from a product-scoped anchor. Assert it is absent from the bundle, that its uuid appears nowhere in the response, and that the path through it is absent rather than shortened.
- **L2** — the same assertion on the reason text of every edge returned.
- **L1** — assert the scope plan is pushed into the composed statement, using the hidden-dimension set rather than the excluded-dimension set. The latter is empty for every explicit dimension, so a wiring mistake there stops filtering exactly when the caller narrows — the same trap HLD-003 records for traversal.
- **L0** — assert the artefact banner is emitted and that the default output path is in the ignore set, as an exact-string test.
- **Repository check** — the default output directory is gitignored; asserted as a literal-line test, not by inspection.

## Acceptance Criteria

- A hidden memory reachable at any depth from any anchor appears in no bundle, in no dossier, and in no manifest.
- No edge reason, path, count or omission entry reveals the existence of a hidden memory.
- An export requested without declaring a hidden dimension returns the narrowed result and never signals that something was withheld.
- Every produced artefact states its sensitivity in its first lines.
- The default artefact location is ignored by version control, proven by test.

## Applies To

Goal 1 (deterministic, scope-safe selection) and Goal 4 (the artefact). LADR-01, LADR-03. `BR-33`,
`BR-34`. Inherits the store-wide confidentiality rule that forbids memory content in any log, span
attribute or metric tag — an export must not become the exception to it.
