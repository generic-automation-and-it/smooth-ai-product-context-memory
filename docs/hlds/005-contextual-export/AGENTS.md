# AGENTS.md - Contextual knowledge export

AI Context: HLD for contextual knowledge export. Updated: 2026-09-13

## TL;DR

Exports a **slice** of the store — selected by repository, initiative, ticket and tags, widened over the
graph — as one composed document plus a findings report. Intent in [README.md](./README.md); decisions in
[./ladrs/](./ladrs/); quality bar in [./nfrs/](./nfrs/); the determinism boundary in
[./diagrams/flow-selection-and-composition.md](./diagrams/flow-selection-and-composition.md). Business
authority is [BRD-002](../../brd/002-contextual-export/).

**This HLD is In Discovery, and three LADRs are Blocked** (09, 10, 11). The blocked set is not a backlog —
it is the reason anchor-level widening is absent. Do not implement around it.

## Non-Negotiables

- **Never extend the forensic `export` CLI to do this.** It bypasses the retrieval scope filter by design. A scoped, shareable artefact produced by that code path is one flag away from leaking hidden material (LADR-01).
- **Never call a model from Application or Host.** Selection is mechanical and server-side; ordering, collapse, contradiction detection and findings are the skill's (LADR-02). This is a standing repo non-negotiable, not a local preference.
- **Never default the widening bound.** It is required on the wire and refused outside 1–5 at the validator *and* the store layer. A default is a bound the caller never considered — and here it also decides the export's cost and completeness claim (LADR-03, NFR-03).
- **Gate every vertex a widening crosses, not just the endpoints — and use the hidden-dimension set, not the excluded-dimension set.** The latter is empty for every explicit dimension, so wiring it stops filtering exactly when the caller narrows. A path through a hidden memory is **dropped, never shortened** (NFR-01).
- **Never write anything on this path.** No version, no edge, no label registration, no blob. Including the "missing" `contradicts` edge a finding describes — that is a claim, on the authority of one composition pass (LADR-06, NFR-06).
- **Never resolve a contradiction silently.** Apply a stated authority and show it, or report the conflict. Recency is not authority (LADR-04).
- **Never let a collapse discard an origin.** Three captures of one conclusion are one claim and three pieces of corroboration (LADR-05).
- **Never truncate silently.** Everything selected is present, collapsed into something present, or listed as omitted with a reason from the bounded set (NFR-04).
- **Never implement focus as a selection filter.** It is a presentation lens applied *after* selection, over one unfocused bundle (LADR-12). Filtering is the reading an implementer reaches for first; it produces a confidently incomplete document, splinters NFR-02 into one guarantee per focus, and makes two focuses of one slice incomparable.
- **Never suppress a finding under a focus.** Reorder by relevance, never drop. Implementation is where a contradiction becomes a defect, so the focus that seems least interested is the one that needs it most.
- **Focus is a bounded enum and a single-valued switch.** Not free text — that is unauditable and a channel for text that steers composition. Not five booleans — that makes two focuses at once representable.
- **Do not invent an answer for LADR-09 / 10 / 11.** Specifically: do not add a non-`Memory` vertex label, and do not project ticket or tag relationships onto memory edges. The second is the tempting one and it corrupts both ordering and contradiction detection.

## Architecture Decisions

See [./ladrs/](./ladrs/). LADRs 01–08 Draft; 09–11 **Blocked**.

| LADR | Decision | Why it matters |
|------|----------|----------------|
| [LADR-01](./ladrs/LADR-01-scoped-read-not-forensic-export.md) | Separate from the forensic export | The forensic path bypasses the scope filter; sharing that code path is a confidentiality boundary crossed by a flag |
| [LADR-02](./ladrs/LADR-02-api-bundles-skill-composes.md) | API bundles, skill composes | Puts the reproducibility guarantee in the only place it can be held |
| [LADR-03](./ladrs/LADR-03-relational-anchors-graph-closure.md) | Relational anchors, graph widening, bound on the wire | Two stores, one round trip; and the bound is the cost control |
| [LADR-04](./ladrs/LADR-04-contradictions-reported-not-resolved.md) | Contradictions reported, not resolved | Most contradictions carry no edge, so edge-only detection reports what was already known |
| [LADR-05](./ladrs/LADR-05-collapse-with-provenance.md) | Collapse retains every origin | Corroboration is frequently the most useful thing in a slice |
| [LADR-06](./ladrs/LADR-06-findings-are-output-not-writes.md) | Findings are output | A side-effect write has no checkpoint and no judgement |
| [LADR-07](./ladrs/LADR-07-deterministic-provenance-ordering.md) | Deterministic topological ordering | Ordering is the one part of composition that can be specified and tested |
| [LADR-08](./ladrs/LADR-08-read-only-dossier-skill.md) | Read-only dossier skill; capture skill is sole *writer* | Makes read-only structural rather than behavioural |
| [LADR-09](./ladrs/LADR-09-ticket-anchors-have-no-graph-representation.md) | Ticket vertices — **Blocked** | No ticket vertex exists; ticket-to-ticket relationships are unreachable |
| [LADR-10](./ladrs/LADR-10-tag-anchors-have-no-graph-representation.md) | Tag vertices — **Blocked** | Tags are open strings with no identity; synonyms are invisible |
| [LADR-11](./ladrs/LADR-11-no-writer-derives-anchor-edges.md) | Anchor-edge derivation — **Blocked** | Even with vertices, nothing would write the edges |
| [LADR-12](./ladrs/LADR-12-focus-is-a-composition-lens.md) | Focus is a lens over one unfocused bundle | Filtering by focus breaks completeness, reproducibility and comparability at once |

**"Blocked" is not in the shared status vocabulary.** It is used here for a decision with a missing input:
an upstream gap makes at least one option unbuildable, so the options cannot be compared. Each blocked
LADR states the gap, the unblocking trigger and the interim behaviour that ships. Treat it as stronger
than Draft — a Draft may be revised by this work, a Blocked may not be resolved by it.

## Key Behaviors

- **Widening travels memory to memory only.** Anchors are resolved relationally and then left behind. A ticket anchor cannot reach that ticket's blockers; a tag anchor cannot reach that tag's synonyms. The manifest must say so on every export — an unstated reach is indistinguishable from completeness.
- **A tag slice under-selects when the vocabulary drifted.** Tags are an open vocabulary typed over months, so this is the more frequent of the two under-selection failures. Reporting the near-miss tag as a finding is the interim answer and costs nothing (LADR-10).
- **`relates_to` and unknown relations connect without ordering.** Including them in the topological sort manufactures cycles constantly, because reciprocal `relates_to` edges are normal (LADR-07).
- **A provenance cycle is a finding, not a rendering problem.** It means capture recorded that A rests on B and B on A. Break at a stated point, report it, and still produce the document.
- **The bounded sets are decided before first use, not discovered.** Omission reasons and finding categories both. Adding a category later changes the meaning of every earlier export — the same reasoning HLD-004 applies to its retrieval-shape classification.
- **Registering an observed facet is the most plausible accidental write.** It looks helpful. It is a write (NFR-06).
- **Byte-equality applies to the bundle only.** A test asserting dossier byte-equality contradicts LADR-02 and must not be written.
- **The manifest-only path must hydrate no blob.** It exists to price the request, so touching bodies defeats it (NFR-03).
- **One bundle, many dossiers.** Several focuses of one slice reuse a single selection, so the traversal is paid for once. A design that re-selects per focus throws that away and re-introduces the comparability problem it was meant to avoid.
- **`review` inverts the document** — findings first, narrative as supporting evidence — rather than re-weighting it. Treat that as a consequence of LADR-12, not as a second axis to model, and expect `review` to be the focus most likely to turn out to be a different document shape.
- **`specification` versus `architecture` must stay sharply separated:** specification carries what must observably be true (acceptance criteria, behaviours, interfaces); architecture carries why the shape is what it is (decisions, rejected alternatives, boundaries). If the distinction stops holding, merge them — do not let both exist while blurring.

## Quality Constraints

Targets and verification live in [./nfrs/](./nfrs/). Three shape how code is written:

- **The bundle carries no generation timestamp.** Stored times are data; a generation time is variance, and it would break byte-equality on the first run (NFR-02).
- **Cut-on-cap is decided by the ordering, never by what the traversal reached first.** Otherwise the cut is order-dependent and reproducibility is gone (NFR-02, NFR-03).
- **The reconciliation is printed in the dossier, not merely asserted in a test.** A reader must be able to see the arithmetic close; that is what makes silent loss detectable by the person holding the document (NFR-04).

## Migration Plans

- **`context-memory` documentation must be amended in the same change** that introduces the dossier skill: its stated invariant narrows from sole *interface* to sole **writer** (LADR-08). Unamended, the two skills' documentation contradicts each other — the exact defect this design reports on elsewhere.
- **LADR-09 and LADR-10 both depend on a decision owned by HLD-003** (whether a non-`Memory` vertex label is permissible against its thin-vertex rule). Neither can be resolved here. LADR-11 depends on both, and on HLD-002, which owns link derivation.
- **LADR-06's Open question belongs to HLD-004.** Whether an export counts as a recall event — and whether "findings already dismissed" is recorded anywhere — must be settled with recall feedback, not ahead of it.
- **The five focuses are unvalidated** (LADR-12 Open). They were named from how the practitioner works, not derived. Whether `specification` survives beside `architecture`, and whether `review` is a focus at all, is answerable only by use. Do not add a sixth before the five have been used, and do not build a focus-registry abstraction for five enum values.
- **Gap detection has no expectation model.** BRD-002 records this: `BR-27` measures against "what a slice should reasonably contain" without saying where that comes from. Until settled, gap findings are whatever the composition finds conspicuous. Do not build a rule engine for it on a guess.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-09-13 | Created — discovery HLD for contextual knowledge export. Selection/composition boundary set at the judgement line; ticket and tag anchor traversal recorded as three Blocked LADRs rather than designed around. | BRD-002 |
| 2026-09-13 | LADR-12 added — focus (requirements / architecture / specification / implementation / review) is a presentation lens over one unfocused bundle, never a selection filter. NFR-02 records that focus does not enter reproducibility; NFR-04 gains the `outside-focus` omission reason and a per-focus reconciliation test; NFR-05 requires the document to state its focus. | BR-35, BR-36 |
