# NFR-05: Traceability

**Status:** Draft

## Requirement

**100%** of substantive statements in a dossier carry a citation to the stored knowledge they came from:
memory identity, version, and capture time. Zero uncited substantive statements.

- A citation is machine-recognisable, in a single stated form, so the rate is measurable rather than reviewed by eye.
- A collapsed claim cites **every** origin it was drawn from, not the one the composition preferred (LADR-05).
- Statements the composition made itself — an ordering rationale, a finding, a summary of a section — are marked as **analysis**, not left to look like stored claims. An unmarked inference is the failure this requirement exists to prevent.
- **Analysis states the evidence or expectation behind it** and never cites a memory that does not support it. Fabricating a plausible source for an inference is worse than leaving it unattributed, because it defeats the check.
- **Missing provenance is visible.** A claim whose source or confidence was never recorded says so; it is not quietly presented as though attributed.
- **Headings and navigation are not substantive claims** and are exempt. Requiring a citation on a section title produces noise that trains the reader to ignore citations.
- **Normative source content stays attributed rather than adopted.** An attributed product rule keeps its normative meaning — "administrators only" is still a restriction — without becoming an instruction the consuming agent obeys (`BR-26`). The distinction is who the imperative belongs to, not whether imperatives may appear.
- Superseded and no-longer-true material carries its status alongside its citation, so a reader never has to infer currency from position in the document.
- The document declares itself a generated projection of the store at a stated moment, and does so where a reader cannot miss it.
- **A focused document states its focus with the same prominence.** A lens mistaken for the whole is the likeliest misuse of the focus feature (LADR-12), and the only thing that prevents it is the document saying which focus produced it and that material was set aside.

## Verification

- **Skill-level test** — parse a produced dossier, classify every statement, and assert the uncited-substantive count is zero. Automated; a rate reported by the composition itself proves nothing.
- **Skill-level test** — a fixture where three captures collapse into one claim: assert all three citations are present.
- **Skill-level test** — assert every composition-authored statement carries the analysis marker, that no stored claim carries it, and that every analysis statement carries a basis.
- **Skill-level test** — a fixture whose memory has no recorded source or confidence: assert the document says so rather than presenting the claim as attributed.
- **Skill-level test** — a fixture containing an imperative product rule: assert it appears attributed to its memory and does not appear as a directive addressed to the reader.
- **Skill-level test** — assert superseded and stale items carry their status marker adjacent to the citation, and that a current item never does.
- **L0** — assert the citation form is a single fixed shape, as a rendering test.

## Acceptance Criteria

- Zero substantive statements without a citation, asserted automatically.
- Every collapsed claim carries every origin.
- Composition-authored text is distinguishable from stored claims without reading for tone, and states its basis.
- No analysis statement cites a memory that does not support it.
- A claim with no recorded provenance is shown as such.
- Attributed normative content keeps its meaning without becoming an instruction to the reader.
- Supersession and staleness are marked at the point of use, not only in a header.
- The document identifies itself as a generated projection, with the moment it describes, and — when focused — the focus that produced it.

## Applies To

Goal 2 (a composed document). LADR-05 (every origin retained), LADR-04 (findings are composition
statements and must be marked as such). `BR-24`, `BR-25`, `BR-26`.
