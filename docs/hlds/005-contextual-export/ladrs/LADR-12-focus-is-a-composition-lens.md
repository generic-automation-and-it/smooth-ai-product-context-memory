# LADR-12: Focus is a composition lens over one unfocused bundle

**Status:** Draft

<!-- Strategic in nature; numbered after the tactical LADRs because numbers are never reassigned. -->

## Context

`BR-35` requires an export to be focusable on the work it is about to feed — requirements,
architecture, specification, implementation, review — and `BR-36` forbids that focus changing what was
selected or suppressing a finding.

The obvious implementation is a selection predicate: narrow the slice by `kind` and facet according to
the focus, so an implementation-focused export selects fewer memories. It is cheap, it looks like an
optimisation, and it breaks three things at once.

It produces the confidently incomplete document `BR-19` and `BR-30` exist to prevent. It splinters
NFR-02's single reproducibility guarantee into one per focus. And it makes two focuses of one slice
incomparable, so a reader cannot tell whether a difference between them is the lens or the store.

## Decision

**Apply** focus after selection, as a presentation lens over one unfocused bundle, drawn from a
bounded set, with unfocused as the default.

One selection serves every focus. The bundle is identical whether the caller intends to focus or not —
the focus is not part of the request that produces it, so NFR-02's byte-equality guarantee is
unaffected and stays a single guarantee. The skill then composes any number of documents from that one
bundle.

A focus may change **order, weighting, section structure, and depth** — which material is quoted in
full versus summarised to a line. It may not change membership. Anything the lens does not surface is
listed as omitted with the bounded reason `outside-focus`, so NFR-04's reconciliation still closes and
the lens is auditable rather than trusted.

**Findings are focus-invariant in presence and focus-ordered in prominence.** A focus may put the most
relevant findings first; it may never drop one. "Implementation does not care about contradictions" is
exactly backwards — implementation is where a contradiction becomes a defect.

The focus set is bounded and stated before first use, for the same reason the findings taxonomy is
(NFR-04): an unbounded set drifts, and a free-text focus is both unauditable and a route for injected
instructions to reach the composition. Adding a focus requires naming the work it feeds and how it
differs from the nearest existing one.

The document **states the focus that produced it**, prominently. A lens mistaken for the whole is the
most likely misuse of this feature, and it is only preventable by the document saying what it is.

## Alternatives Considered

- **Focus as a selection predicate, server-side** — rejected: the three failures in Context. It is the reading an implementer will reach for first, which is why `BR-36` and this LADR exist.
- **Free-text focus prompt supplied by the caller** — rejected: unbounded, so no two exports are comparable; unauditable, so a reader cannot tell what was de-emphasised; and it makes the request a channel for text that steers composition, which a bounded enum cannot be.
- **A separate skill per focus** — rejected: five copies of the ordering, collapse, citation and findings rules, drifting independently. The composition contract is the thing worth having.
- **Focus as an audience rather than a kind of work** ("for a colleague", "for an agent") — rejected: who reads an export varies by occasion and is unknown at request time, while the artefact the reader is heading for is stated by the request itself. `BR-35` records this.
- **No focus; one document for everyone** — rejected: it is the current behaviour, and a composition with no target reader optimises for nothing. `BR-06`'s crowding-out problem at document scale.

## Consequences

- One expensive selection amortises across several documents. Producing a requirements view and an implementation view of one slice pays for the traversal once, which is a direct win against NFR-03.
- Adding a sixth focus later changes nothing about any earlier export, because selection never varied by focus. A selection-filtering design would have made every earlier export's meaning depend on the focus set current at the time.
- **The `review` focus inverts the document** — findings first, narrative as supporting evidence — rather than merely re-weighting it. That is a consequence of taking the lens seriously, not a second axis to be modelled. It also makes `review` the focus most likely to turn out to be a different document shape entirely.
- The reconciliation printed in every dossier gets larger under a narrow focus, because more material lands in `outside-focus`. That is the feature working: the reader can see exactly what the lens set aside.
- `specification` and `architecture` overlap unless separated sharply. Pinned here: **specification** carries what must observably be true — acceptance criteria, behaviours, interfaces; **architecture** carries why the shape is what it is — decisions, rejected alternatives, boundaries. If that distinction does not survive use, they merge; they must not be allowed to blur while both exist.
- The skill's switch surface grows. A single-valued switch is required rather than five booleans, so that two focuses cannot be requested at once — mutually exclusive booleans are how that request becomes representable.

## Open

- **Are five focuses the right five?** They were named from how the practitioner actually works, not derived. Whether `specification` is distinct enough from `architecture`, and whether `review` is a focus rather than a different document shape, is answerable only by use. Trigger: the first focused exports. Do not add a sixth before the five have been used.
- **Does a focus change what "unattributed" or "weak-summary" means?** A memory adequate for a requirements view may be too thin for an implementation view. If quality findings become focus-relative, that is a change to the taxonomy's meaning and must be decided deliberately rather than emerging.

## Related

- **LADR-02** — focus lands entirely on the judgement side of the boundary, which is why NFR-02 is untouched.
- **LADR-08** — the composing skill owns the focus contract; it remains read-only.
- **NFR-04** — `outside-focus` is a bounded omission reason, so a lens cannot lose material silently.
- **NFR-05** — the document states its focus, alongside stating that it is a generated projection.
