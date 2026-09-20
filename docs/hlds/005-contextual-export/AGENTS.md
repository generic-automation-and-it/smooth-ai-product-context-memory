# AGENTS.md - Contextual knowledge export

AI Context: HLD for contextual knowledge export. Updated: 2026-09-15

## TL;DR

Exports a **slice** of the store — selected by repository, initiative, ticket and tags, widened over the
graph — as one composed document plus a findings report. Intent in [README.md](./README.md); decisions in
[./ladrs/](./ladrs/); quality bar in [./nfrs/](./nfrs/); the determinism boundary in
[./diagrams/flow-selection-and-composition.md](./diagrams/flow-selection-and-composition.md). Business
authority is [BRD-002](../../brd/002-contextual-export/).

**This HLD remains In Discovery.** LADR-09 and LADR-11's ticket half are decision-resolved by
owner-approved HLD-003 LADR-08 and HLD-002 LADR-08, with implementation and release gates accepted
against [final evidence](../003-graph-edges-on-age/nfrs/NFR-02-ticket-traversal-measurements.md). LADR-10 and
LADR-11's tag half remain Blocked. Evidence-only near-miss reporting does not approve a tag graph
or the full dossier implementation.

## Non-Negotiables

- **Never extend the forensic `export` CLI to do this.** It bypasses the retrieval scope filter by design. A scoped, shareable artefact produced by that code path is one flag away from leaking hidden material (LADR-01).
- **Never call a model from Application or Host.** Selection is mechanical and server-side; ordering, collapse, contradiction detection and findings are the skill's (LADR-02). This is a standing repo non-negotiable, not a local preference.
- **Never default the widening bound.** It is required on the wire and refused outside 1–5 at the validator *and* the store layer. A default is a bound the caller never considered — and here it also decides the export's cost and completeness claim (LADR-03, NFR-03).
- **Gate every vertex a widening crosses, not just the endpoints — and use the hidden-dimension set, not the excluded-dimension set.** The latter is empty for every explicit dimension, so wiring it stops filtering exactly when the caller narrows. A path through a hidden memory is **dropped, never shortened** (NFR-01).
- **Never write anything on this path.** No version, no edge, no label registration, no blob. Including the "missing" `contradicts` edge a finding describes — that is a claim, on the authority of one composition pass (LADR-06, NFR-06).
- **Never resolve a contradiction silently.** Apply a stated authority and show it, or report the conflict. Recency is not authority (LADR-04).
- **Never let a consolidation discard an origin.** Equivalent restatements appear once with every origin retained — but the origins are reported as origins, never counted as corroboration (LADR-05, `BR-23`).
- **Never truncate silently.** Everything selected is present, collapsed into something present, or listed as omitted with a reason from the bounded set (NFR-04).
- **Never implement focus as a selection filter.** It is a presentation lens applied *after* selection, over one unfocused bundle (LADR-12). Filtering is the reading an implementer reaches for first; it produces a confidently incomplete document, splinters NFR-02 into one guarantee per focus, and makes two focuses of one slice incomparable.
- **Never suppress a finding under a focus.** Reorder by relevance, never drop. Implementation is where a contradiction becomes a defect, so the focus that seems least interested is the one that needs it most.
- **Focus is a bounded enum and a single-valued switch.** Not free text — that is unauditable and a channel for text that steers composition. Not five booleans — that makes two focuses at once representable.
- **Never emit a finding without a basis, and never phrase a slice observation as a store-wide fact.** A gap needs one of `BR-27`'s three grounds — the task, an included claim, an explicit expectation. A general best-practice suggestion is analysis, not a product gap (LADR-13).
- **Never consolidate on wording alone.** Equivalence is meaning *and* applicability *and* lifecycle. A customer-scoped exception is not the general rule; a proposed change is not the shipped behaviour. Where equivalence is uncertain, keep the distinction (LADR-05, NFR-07).
- **Never present repeated captures as corroboration.** `BR-23` is explicit: several captures of one source are not independent evidence. An earlier version of LADR-05 said the opposite — do not restore it.
- **Never compress away a condition to fit a budget.** Narrow what is included and report the omission; a claim shorn of its exception is a false claim, not a shorter one (NFR-07).
- **Never let composition silently re-select.** The scope the practitioner approved in the preview is the scope composed, or the difference is reported (LADR-14).
- **Use the ticket-only upstream decisions; do not generalize them.** HLD-003 LADR-08 supersedes LADR-02 for exact `Ticket` identities and declared `TICKET_PARENT` only. Group membership remains JSONB, memories join relationally, and no ticket/tag relationship is projected onto memory `LINKS`. Tag identity/synonyms remain blocked.
- **Ticket identity supplies no scope consent.** Separate ticket traversal requires `maxDepth` 1..5 and exactly one live owner for every ticket. Gate all owners with `HiddenDimensions`, drop hidden paths whole, then apply endpoint `Plan()` narrowing. Return capped deterministic paths and distinct current non-proposed memories including the anchor's; disclose only visible cap flags and generic upstream coverage/freshness limits (LADR-09).

## Architecture Decisions

See [./ladrs/](./ladrs/). LADRs 01–08 and 12–14 Draft; 09 Accepted and implemented;
10 Blocked with a verified evidence-only interim; 11 ticket half Accepted and implemented, tag half Blocked.

| LADR | Decision | Why it matters |
|------|----------|----------------|
| [LADR-01](./ladrs/LADR-01-scoped-read-not-forensic-export.md) | Separate from the forensic export | The forensic path bypasses the scope filter; sharing that code path is a confidentiality boundary crossed by a flag |
| [LADR-02](./ladrs/LADR-02-api-bundles-skill-composes.md) | API bundles, skill composes | Puts the reproducibility guarantee in the only place it can be held |
| [LADR-03](./ladrs/LADR-03-relational-anchors-graph-closure.md) | Relational anchors, graph widening, bound on the wire | Two stores, one round trip; and the bound is the cost control |
| [LADR-04](./ladrs/LADR-04-contradictions-reported-not-resolved.md) | Contradictions reported, not resolved | Most contradictions carry no edge, so edge-only detection reports what was already known |
| [LADR-05](./ladrs/LADR-05-collapse-with-provenance.md) | Consolidation retains every origin; equivalence is meaning + applicability + lifecycle | Wording-based merging turns a scoped exception into a false general rule |
| [LADR-06](./ladrs/LADR-06-findings-are-output-not-writes.md) | Findings are output | A side-effect write has no checkpoint and no judgement |
| [LADR-07](./ladrs/LADR-07-deterministic-provenance-ordering.md) | Deterministic topological ordering | Ordering is the one part of composition that can be specified and tested |
| [LADR-08](./ladrs/LADR-08-read-only-dossier-skill.md) | Read-only dossier skill; capture skill is sole *writer* | Makes read-only structural rather than behavioural |
| [LADR-09](./ladrs/LADR-09-ticket-anchors-have-no-graph-representation.md) | Ticket hierarchy implemented and accepted | HLD-003 LADR-08 owns identity-only tickets, live ownership and separate scope-safe traversal |
| [LADR-10](./ladrs/LADR-10-tag-anchors-have-no-graph-representation.md) | Tag vertices — **Blocked** | Tags are open strings with no identity; synonyms are invisible |
| [LADR-11](./ladrs/LADR-11-no-writer-derives-anchor-edges.md) | Ticket writer resolved; tag writer **Blocked** | HLD-002 LADR-08 permits practitioner declarations only, not inference or tracker synchronization |
| [LADR-12](./ladrs/LADR-12-focus-is-a-composition-lens.md) | Focus is a lens over one unfocused bundle | Filtering by focus breaks completeness, reproducibility and comparability at once |
| [LADR-13](./ladrs/LADR-13-findings-carry-a-basis-and-a-scope.md) | Findings carry a basis and a scope | An inference that reads as a discovery is the most persuasive and least checkable thing a composition emits |
| [LADR-14](./ladrs/LADR-14-preview-and-composition-bind-to-one-selection.md) | Preview and composition bind to one selection | Re-selecting at composition time is the implementation default and makes the consent step decorative |

**"Blocked" is not in the shared status vocabulary.** It is used here for a decision with a missing input:
an upstream gap makes at least one option unbuildable, so the options cannot be compared. Each blocked
LADR states the gap, the unblocking trigger and the interim behaviour that ships. Treat it as stronger
than Draft — a Draft may be revised by this work, a Blocked may not be resolved by it.

## Key Behaviors

- **Ticket traversal exists separately from memory provenance.** `ITicketGraph` backs POST `/api/context/tickets/paths`; a Cypher anchor composes with recursive SQL over indexed AGE adjacency and live owners. Selected capped path endpoints plus anchor supply memories, not every admitted ticket. No blocker/dependency graph or dossier history mode is implied. Always disclose undeclared upstream hierarchy unfollowed and freshness unverified. The performance gate passed; tags still have no synonym traversal.
- **Near-miss reporting needs evidence, not broader search.** `near-miss-tag` uses only already-authorized examined material and cites UUID/version, basis, scope and observation/analysis classification. No extra query, guessed excluded record, hidden ID/count or write; no evidence means no finding (LADR-10, NFR-04).
- **The near-miss helper is executable, not a dossier implementation.** `near_miss_tags.py` validates
  supplied approved UUID/version evidence and exact supporting quotes, observes failed exact tag
  predicates, and labels caller/skill relevance as analysis. Selection and disclosure stay unchanged.
  Its current schema preserves `originalQuery` with API `facetMatchMode` (default `any`), not separate
  `criteria`/`tagsMatchMode`. Each record's `status` and approved `scopeDimension`/`scopeIdentifier`
  survive into findings; `proposedEvidence` is explicit. Examined-set names and query scope do not
  replace applicability, and examination approval does not promote proposed knowledge.
  Its offline bounded tests establish plumbing, not authorization truth or semantic judgement quality.
- **`relates_to` and unknown relations connect without ordering.** Including them in the topological sort manufactures cycles constantly, because reciprocal `relates_to` edges are normal (LADR-07).
- **A provenance cycle is a finding, not a rendering problem.** It means capture recorded that A rests on B and B on A. Break at a stated point, report it, and still produce the document.
- **The bounded sets are decided before first use, not discovered.** Omission reasons and finding categories both. Adding a category later changes the meaning of every earlier export — the same reasoning HLD-004 applies to its retrieval-shape classification.
- **Registering an observed facet is the most plausible accidental write.** It looks helpful. It is a write (NFR-06).
- **Byte-equality applies to the bundle only.** A test asserting dossier byte-equality contradicts LADR-02 and must not be written.
- **The manifest-only path must hydrate no blob.** It exists to price the request, so touching bodies defeats it (NFR-03).
- **One bundle, many dossiers.** Several focuses of one slice reuse a single selection, so the traversal is paid for once. A design that re-selects per focus throws that away and re-introduces the comparability problem it was meant to avoid.
- **`review` inverts the document** — findings first, narrative as supporting evidence — rather than re-weighting it. Treat that as a consequence of LADR-12, not as a second axis to model, and expect `review` to be the focus most likely to turn out to be a different document shape.
- **The combination rule is stated, not inferred.** Within a category values are alternatives; across categories they are conjunctive. It goes in the manifest, because a reader should not have to deduce it from result size (`BR-18`).
- **A no-match is a no-match.** Never broaden the criteria to return something.
- **History is a selection dimension**, priced by the preview — not something composition decides to include.
- **A widened memory carries the reason it was added and its original scope.** A relationship does not make a rule applicable to another product or customer (`BR-19`).
- **`no-links-in-slice`, not `orphan`.** A slice cannot establish that a memory is unlinked anywhere in the store. `weak-summary` is analysis, not observation — which is what keeps it distinct from HLD-004's observed signal.
- **`specification` versus `architecture` must stay sharply separated:** specification carries what must observably be true (acceptance criteria, behaviours, interfaces); architecture carries why the shape is what it is (decisions, rejected alternatives, boundaries). If the distinction stops holding, merge them — do not let both exist while blurring.
- **Understanding is a `kind`, not a new export surface here.** It rides on this export (and the forensic dump) as a selectable kind; the load/import of Understandings is a separate capability owned by HLD 007, not a change to this read-only dossier.

## Quality Constraints

Targets and verification live in [./nfrs/](./nfrs/). Three shape how code is written:

- **The bundle carries no generation timestamp.** Stored times are data; a generation time is variance, and it would break byte-equality on the first run (NFR-02).
- **Cut-on-cap is decided by the ordering, never by what the traversal reached first.** Otherwise the cut is order-dependent and reproducibility is gone (NFR-02, NFR-03).
- **Numeric caps are provisional.** NFR-03 no longer states an item cap or a latency target: BRD-002 requires them to come from the reference workflows. Carry a stated, configurable limit and record which value produced each export; do not cite a placeholder as a specification.
- **The reconciliation is printed in the dossier, not merely asserted in a test.** A reader must be able to see the arithmetic close; that is what makes silent loss detectable by the person holding the document (NFR-04).

## Migration Plans

- **`mimisbrunnr-context-memory` documentation must be amended in the same change** that introduces the dossier skill: its stated invariant narrows from sole *interface* to sole **writer** (LADR-08). Unamended, the two skills' documentation contradicts each other — the exact defect this design reports on elsewhere.
- **Ticket prerequisites were resolved in their owning HLDs before migration.** HLD-003 LADR-08 superseded LADR-02; HLD-002 LADR-08 owns the accepted declared-only writer. Final evidence verifies ticket migration/API and the deterministic near-miss helper. LADR-10 and the tag half of LADR-11 still require tag identity/synonym and writer decisions; full dossier implementation is not implied by this acceptance.
- **LADR-06's Open question belongs to HLD-004.** Whether an export counts as a recall event — and whether "findings already dismissed" is recorded anywhere — must be settled with recall feedback, not ahead of it.
- **The five focuses are unvalidated** (LADR-12 Open). They were named from how the practitioner works, not derived. Whether `specification` survives beside `architecture`, and whether `review` is a focus at all, is answerable only by use. Do not add a sixth before the five have been used, and do not build a focus-registry abstraction for five enum values.
- **Gap detection now has a defined basis and this design has been aligned to it.** `BR-27` supplies three grounds (task, included claim, explicit expectation); LADR-13 implements them. The earlier "no expectation model" position is **resolved, not open** — do not reintroduce it, and do not build a generic-expectations rule engine, which `BR-27` explicitly excludes.
- **The finding taxonomy changes before first use.** `orphan` becomes `no-links-in-slice`, `duplicate-not-collapsed` becomes `equivalence-uncertain`, and evidence-only `near-miss-tag` is added. NFR-04 fixes the set before the first export.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-09-19 | Scoped the "no import / projection is never a source" principle to the dossier and noted the understanding load/import capability as a separate concern owned by HLD 007 (opt-in `--store` through the capture skill). Understanding rides on this export as a selectable kind. | HLD-007; BRD-003 |
| 2026-09-15 | Replaced NFR-04's stale near-miss harness count with a dated evidence reference and the current verification command; full dossier verification remains unclaimed. | PR #63 review finding 2 |
| 2026-09-15 | Aligned LADR-10 with the current near-miss helper schema: unchanged originalQuery/facetMatchMode, per-record lifecycle/applicability and explicit proposed evidence. Selection/disclosure and blocked tag decisions unchanged; no new verification recorded. | LADR-10 |
| 2026-09-15 | Finalized ticket representation/writer and evidence-only helper acceptance using final HLD-003 verification. Performance and full-suite gates passed; full dossier remains In Discovery and tag identity/synonym decisions remain blocked. | LADRs 09-11; HLD-003 final NFR-02 evidence |
| 2026-09-14 | Synced ticket decisions to ITicketGraph/API/migration implementation and selected capped path association; release/performance gate remains open. Recorded executable offline evidence-only near-miss helper, bounded validation and unchanged selection/disclosure. Tag decisions remain blocked and full dossier stays In Discovery. | LADRs 09-11; NFR-04; HLD-003 NFR-02 |
| 2026-09-14 | Ticket LADR-09 and ticket half of LADR-11 decision-resolved by HLD-003/HLD-002 LADR-08, implementation pending. Tag identity/synonyms and tag writer stay blocked. LADR-10 interim and NFR-04 now require evidence-only `near-miss-tag` skill findings with UUID/version, basis, scope and classification, without search broadening. No full dossier implementation approved. | LADRs 09-11; NFR-04 |
| 2026-09-13 | Created — discovery HLD for contextual knowledge export. Selection/composition boundary set at the judgement line; ticket and tag anchor traversal recorded as three Blocked LADRs rather than designed around. | BRD-002 |
| 2026-09-13 | LADR-12 added — focus (requirements / architecture / specification / implementation / review) is a presentation lens over one unfocused bundle, never a selection filter. NFR-02 records that focus does not enter reproducibility; NFR-04 gains the `outside-focus` omission reason and a per-focus reconciliation test; NFR-05 requires the document to state its focus. | BR-35, BR-36 |
| 2026-09-14 | Capture-skill identity updated to `mimisbrunnr-context-memory` (LADR-08, migration plan). | skill rename |
| 2026-09-14 | Aligned to BRD-002's revised requirements. Added LADR-13 (findings carry a basis and a scope — closes the former "no expectation model" gap using `BR-27`'s three grounds), LADR-14 (preview and composition bind to one recorded selection) and NFR-07 (fidelity). Revised LADR-05 — equivalence is meaning + applicability + lifecycle, and repeated captures are **not** corroboration, superseding this design's earlier position; LADR-03 — stated combination semantics, no silent broadening, history as a selection dimension; LADR-04 — applicability and lifecycle compared before declaring a conflict. NFR-01 enumerates preview/citations/findings/omission lists; NFR-03 drops assumed numeric caps pending reference-workflow validation; NFR-04 renames `orphan` and `duplicate-not-collapsed`; NFR-05 requires analysis to state its basis and missing provenance to be visible. | BRD-002 §§6–7 |
