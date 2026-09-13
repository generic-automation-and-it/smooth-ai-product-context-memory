# NFR-05: Traceability

**Status:** Draft

## Requirement

**100%** of substantive statements in a dossier carry a citation to the stored knowledge they came from:
memory identity, version, and capture time. Zero uncited substantive statements.

- A citation is machine-recognisable, in a single stated form, so the rate is measurable rather than reviewed by eye.
- A collapsed claim cites **every** origin it was drawn from, not the one the composition preferred (LADR-05).
- Statements the composition made itself — an ordering rationale, a finding, a summary of a section — are marked as **composition**, not left to look like stored claims. An unmarked inference is the failure this requirement exists to prevent.
- Superseded and no-longer-true material carries its status alongside its citation, so a reader never has to infer currency from position in the document.
- The document declares itself a generated projection of the store at a stated moment, and does so where a reader cannot miss it.

## Verification

- **Skill-level test** — parse a produced dossier, classify every statement, and assert the uncited-substantive count is zero. Automated; a rate reported by the composition itself proves nothing.
- **Skill-level test** — a fixture where three captures collapse into one claim: assert all three citations are present.
- **Skill-level test** — assert every composition-authored statement carries the composition marker, and that no stored claim carries it.
- **Skill-level test** — assert superseded and stale items carry their status marker adjacent to the citation, and that a current item never does.
- **L0** — assert the citation form is a single fixed shape, as a rendering test.

## Acceptance Criteria

- Zero substantive statements without a citation, asserted automatically.
- Every collapsed claim carries every origin.
- Composition-authored text is distinguishable from stored claims without reading for tone.
- Supersession and staleness are marked at the point of use, not only in a header.
- The document identifies itself as a generated projection, with the moment it describes.

## Applies To

Goal 2 (a composed document). LADR-05 (every origin retained), LADR-04 (findings are composition
statements and must be marked as such). `BR-24`, `BR-25`, `BR-26`.
