# Contextual knowledge export — High-Level Design

| | |
|---|---|
| **Status** | In Discovery |
| **Owner** | generik0 |
| **Tracker** | Contextual export |
| **Business authority** | [BRD-002 — Contextual knowledge export](../../brd/002-contextual-export/) (`BR-18` … `BR-36`) |
| **Last updated** | 2026-09-22 |

> Discovery / prototyping HLD. Delivers **intent + spec** — what we are building and why, the decisions
> behind it, and the quality bar it must meet. No implementation plan; execution is tracked in the
> issue/work tracker.

## Intent

The store answers questions. It cannot produce a **document**.

BRD-001 made recall deliberately narrow — a small, relevant, attributed set, because a knowledge base
large enough to be useful is too large to load. That is right for answering and wrong for
understanding. Re-entering a repository after six months, handing reasoning to a colleague, or writing
a design document from what was decided, all need the same thing the store cannot give: **everything it
knows about one slice of work, in one readable artefact, in an order that makes the reasoning legible**.

BRD-002 asks for that artefact, selected by **repository, initiative, ticket and tags**, widened along
the recorded relationships between memories, and accompanied by a report of what the store could not
do: its **gaps**, its **contradictions**, and the **quality problems** found while reading itself.

This matters now because the store is entering daily use and its defects are currently only visible by
accident. A contradiction between two memories that are never recalled together is never surfaced; a
gap across a whole area is invisible because nobody asked the question that would reveal it. An export
is the first vantage point from which the store can be assessed as a body of knowledge rather than one
answer at a time.

A whole-store readable dump already exists. It is forensic: complete, unordered, unjudged, and
deliberately bypasses the retrieval rules. It answers *"what is actually in there?"* This design
answers *"what does it know about this?"* — a different question, a different consumer, and a
different set of rules.

## Vocabulary

Five words that are easy to conflate, plus the pre-existing artefact they are most often confused
with. All are kept distinct throughout this HLD:

| Term | Meaning |
|---|---|
| **Anchor set** | The selection criteria: repository, initiative, ticket, tags — combinable |
| **Bundle** | What the API returns: the selected memories with their bodies, the edges between them with their reasons, and a **manifest** stating the effective selection, what was reached, and what was cut. Deterministic. No judgement |
| **Preview** | The manifest served without bodies and without composition, so scope and cost can be judged before either is paid for. The first half of the operation, not a detached estimate (LADR-14) |
| **Dossier** | What the skill composes from a bundle: the ordered document plus its findings. Judgement. Not deterministic |
| **Focus** | The work a dossier is about to feed — requirements, architecture, specification, implementation, review. A presentation lens over an unfocused bundle: changes order, weighting and depth, never membership. Optional; unfocused is the default (LADR-12) |
| **Forensic dump** | The pre-existing whole-store `export` CLI projection. Not part of this design; see LADR-01 |

## Key Goals

### 1. Deterministic, scope-safe selection of a slice

An export is only trustworthy if what it selected is reproducible and its reach is stated. Widening
along relationships is what makes selection useful and is also the easiest way to leak material the
store deliberately hides — a hidden memory reached at depth two is still a disclosure, and the artefact
is portable.

**Acceptance criteria / DoD** — satisfies `BR-18`, `BR-19`, `BR-20`, `BR-33`

- Any combination of repository, initiative, ticket and tags resolves to one bundle, under a **stated** combination rule reported with the result — values within a category are alternatives, categories are conjunctive. A request matching nothing reports no match and is never silently broadened.
- Whether history is included is part of the request and part of the recorded effective selection, not a composition afterthought.
- The bundle reports how far relationships were followed and what it stopped short of.
- Repeating a request against an unchanged store produces a byte-identical bundle.
- No memory hidden from ordinary retrieval appears in a bundle, including when reached by relationship rather than requested directly.

### 2. A composed document, not a concatenation

Concatenation is the failure mode this design exists to avoid. A slice pasted together in database
order, with the same conclusion printed three times and a reversed decision sitting beside its
replacement, is *less* useful than the store — it reads as complete and is not.

A document with no target reader optimises for nothing, so composition can be **focused** on the work
the document is about to feed. A focus is a lens, not a filter: it re-weights and reorders one
unfocused bundle and never changes what was selected (LADR-12).

**Acceptance criteria / DoD** — satisfies `BR-21`, `BR-22`, `BR-23`, `BR-24`, `BR-25`, `BR-26`, `BR-35`, `BR-36`

- A decision and the reasoning it rests on appear in a followable order.
- A claim captured more than once appears once, with every origin still identifiable.
- Current, proposed, superseded and no-longer-true material stay distinguishable, and unknown status is stated as unknown. Capture recency is never evidence that behaviour shipped.
- Every definition, condition, threshold, permission and exception needed to apply a claim survives composition; wording is retained verbatim where paraphrase would change meaning.
- Consolidation merges only where meaning, applicability and lifecycle match, and repeated captures of one source are not presented as independent corroboration.
- Every statement traces to the memory, version and capture time it came from.
- The document is identifiable as a generated projection, states the focus that produced it, and reads as evidence rather than instruction.
- Any focus from the bounded set produces a document ordered and weighted for that work; requesting none produces the complete unfocused document.
- Two focuses of one slice contain the same selected knowledge; whatever a focus does not surface is listed as omitted with reason `outside-focus`.
- A `kind = understanding` memory is a selectable, composable kind like any other: selected under the same anchor set and widening, cited to the same attribution bar, reconciled in the same arithmetic (LADR-15). Its load/import stays a separate capability owned by HLD 007.
- Gaps, contradictions and quality problems are present under every focus.

### 3. Findings as first-class output

The store's honesty about its limits is the whole reason to trust it. In a document that honesty must
be structural — named sections with a bounded taxonomy — not remarks scattered through prose that a
reader can miss and a script cannot count.

A finding is only useful if it can be checked, so each carries its **basis** (what prompted it), its
**scope** (the examined material, never the store or the product) and its **classification** as
observation or analysis. `BR-27` supplies the basis for a gap — the stated task, an included claim
needing interpretation, or an explicit practitioner expectation — which is what an earlier draft of this
design left open and could not supply for itself (LADR-13).

**Acceptance criteria / DoD** — satisfies `BR-27`, `BR-28`, `BR-29`, `BR-30`, `BR-31`

- Gaps, contradictions and quality problems are separate, addressable sections.
- Every finding names the memories it concerns and states its basis, so it can be acted on and rejected without re-deriving it.
- Every gap is a specific question with one of `BR-27`'s three grounds. A general best-practice suggestion is offered as analysis, never as evidence of a product gap.
- Findings distinguish "not found in this material" from "does not exist", and the export does not claim to have found every gap.
- Contradictions are compared on applicability and lifecycle first: a scoped exception and a proposed-versus-shipped pair are not conflicts. Those a stated authority settles are resolved with the authority shown; the rest are presented for decision.
- Everything selected is either in the document or listed as omitted with a reason.
- Producing an export leaves the store byte-identical.

### 4. Cost visible before it is paid

Composition is the most expensive operation in the product, and its cost scales with the slice rather
than with a fact. "Everything about this repository" must be answerable *as a size* before it is
answerable as a document.

One selection can serve several documents: because focus is applied after selection (LADR-12),
producing a requirements view and an implementation view of one slice pays for the traversal once.

**Acceptance criteria / DoD** — satisfies `BR-32`, `BR-34`

- A preview returns the effective scope, volume, reach limits and estimated composition cost without composing anything and without hydrating a body. Estimates state their assumptions and uncertainty; monetary cost is shown when pricing is known and marked unavailable when it is not.
- Every limit that would be hit is named in that preview, before composition. Scope beyond a supported limit is refused and reported, never silently truncated.
- The practitioner can proceed, narrow or cancel, and composition is bound to the selection they approved (LADR-14).
- The artefact declares its sensitivity and is written where nothing commits or synchronises it by default.

## Core Separation of Concerns

> The API assembles the **bundle**. The skill composes the **dossier**. Selection is deterministic and
> server-side; judgement is model-side and never in Application or Host.

This is not a preference. `Features/FEATURES_AGENTS.md` states it as a non-negotiable — *no LLM in
Application or Host; the skill owns judgement* — and the write path already works this way: the API
persists what the skill has already decided. Ordering, collapsing restatements, spotting a
contradiction nobody recorded as one, and judging that a slice is missing something are all judgement.
None of them can be expressed as a predicate, and none of them belong behind an HTTP endpoint.

The split also draws the reproducibility line in the one place it can be held. Selection is
mechanical, so it can be required to be byte-identical (NFR-02). Composition is judgement, so it
cannot — and pretending otherwise would either forbid the judgement or make the guarantee a lie.

## Guiding Principle — A dossier is a projection, never a source

> Regenerating an export must always be easier than editing one.

- **One-way, no import.** The store is the record; a dossier is a view of it at a moment. An import path would make an edited copy authoritative and reverse the direction HLD 001 chose — the same reasoning that keeps the forensic dump one-way. *(The understanding load/import capability is a deliberate, scoped exception owned by HLD 007 with an opt-in `--store` path through the capture skill; it does not apply to this dossier.)*
- **A read that changes nothing.** Findings are output. Recording one as knowledge is a capture, and captures go through the approved write path with the practitioner's judgement — never as a side effect of reading (`BR-31`).
- **Honest before complete.** Where the design must choose, it reports the limit rather than papering over it. A confidently incomplete document is the failure mode; a loudly incomplete one is a working feature.
- **We will deliberately not** schedule or continuously regenerate exports. An export answers a question at a moment; a standing regeneration is upkeep, and upkeep is what `BR-01` forbids.
- **We will deliberately not** let the export fix what it finds. Reporting a contradiction and resolving one are different acts with different authority.

---

## Diagrams

- [System Context (C1)](./diagrams/c4-context.md) — the actors, the store, and where the artefact lands
- [Flow — selection, bundle, composition, findings](./diagrams/flow-selection-and-composition.md) — where determinism stops and judgement starts. This boundary *is* the thesis, so it earns a diagram

## Architecture Decisions (LADRs)

LADRs 01–06 are strategic, 07–08 tactical, **09–11 track anchor prerequisites**: tickets are now
implemented and accepted against final verification; tags remain blocked. LADRs 12–15 were appended after
the original set (12, 13 and 15 strategic, 14 tactical), because numbers are never reassigned. See
[`./ladrs/`](./ladrs/).

**Status legend.** `Draft → Prototype → Accepted` is the shared vocabulary. **Blocked** is used here for
a decision whose options cannot be evaluated yet because an upstream gap makes one of them unbuildable.
A blocked LADR states the gap, the trigger that unblocks it, and the interim behaviour that ships
without it. It is not a deferred decision — it is a decision with a missing input.

| LADR | Decision | Status |
|------|----------|--------|
| [LADR-01](./ladrs/LADR-01-scoped-read-not-forensic-export.md) | A scoped read path, not an extension of the forensic export | Draft |
| [LADR-02](./ladrs/LADR-02-api-bundles-skill-composes.md) | The API bundles; the skill composes | Draft |
| [LADR-03](./ladrs/LADR-03-relational-anchors-graph-closure.md) | Anchors resolve relationally; widening runs on the graph | Draft |
| [LADR-04](./ladrs/LADR-04-contradictions-reported-not-resolved.md) | Contradictions are reported, never silently resolved | Draft |
| [LADR-05](./ladrs/LADR-05-collapse-with-provenance.md) | Restatements collapse with every origin retained | Draft |
| [LADR-06](./ladrs/LADR-06-findings-are-output-not-writes.md) | Findings are output; recording one is a normal capture | Draft |
| [LADR-07](./ladrs/LADR-07-deterministic-provenance-ordering.md) | Ordering is a deterministic topological sort over provenance relations | Draft |
| [LADR-08](./ladrs/LADR-08-read-only-dossier-skill.md) | A read-only dossier skill; `mimisbrunnr-context-memory` narrows to sole *writer* | Draft |
| [LADR-09](./ladrs/LADR-09-ticket-anchors-have-no-graph-representation.md) | Captured ticket hierarchy under HLD-003 LADR-08 | Accepted; implemented, release gates passed |
| [LADR-10](./ladrs/LADR-10-tag-anchors-have-no-graph-representation.md) | Tag anchors as graph vertices | **Blocked** |
| [LADR-11](./ladrs/LADR-11-no-writer-derives-anchor-edges.md) | Practitioner-declared ticket hierarchy under HLD-002 LADR-08; tag writer unresolved | Ticket half implemented and accepted; tag half **Blocked** |
| [LADR-12](./ladrs/LADR-12-focus-is-a-composition-lens.md) | Focus is a composition lens over one unfocused bundle | Draft |
| [LADR-13](./ladrs/LADR-13-findings-carry-a-basis-and-a-scope.md) | Every finding carries a stated basis and is scoped to the examined material | Draft |
| [LADR-14](./ladrs/LADR-14-preview-and-composition-bind-to-one-selection.md) | Preview and composition bind to one recorded selection | Draft |
| [LADR-15](./ladrs/LADR-15-understanding-additional-kind.md) | `kind = understanding` is a first-class selectable kind on this read-only export | Draft |

### Resolved tickets, blocked tags

[HLD-003 LADR-08](../003-graph-edges-on-age/ladrs/LADR-08-captured-ticket-hierarchy.md) supersedes
LADR-02 in writing before any ticket migration. Exact provider/key-only `Ticket` identities and
practitioner-declared parent -> child `TICKET_PARENT` are implemented under the accepted design. Group
association stays JSONB; memories join relationally, without membership fanout or projected `LINKS`.
[HLD-002 LADR-08](../002-context-memory-write-pipeline/ladrs/LADR-08-practitioner-declared-ticket-hierarchy.md)
resolves the writer: explicit expected-parent set/reparent/remove, not automatic derivation or a
synchronized tracker mirror. This is current-state hierarchy, not history or general ticket blockers.

The separate ticket traversal requires `maxDepth` 1..5, deterministic capped paths and distinct
current non-proposed memories including the anchor's. Every ticket resolves to exactly one live
owner; `HiddenDimensions` gates every hop, drops hidden paths whole, and grants no consent merely
from ticket identity. Endpoint `Plan()` narrowing still applies. Generic disclosures say undeclared
upstream hierarchy was not followed and freshness is unverified; visible cap flags reveal no hidden
IDs or counts. `ITicketGraph` backs separate parent PUT and ticket-path POST endpoints. Traversal
composes a Cypher anchor with recursive SQL over indexed AGE adjacency, with memories from selected
capped path endpoints plus anchor. **Performance gate PASSED:** [final evidence](../003-graph-edges-on-age/nfrs/NFR-02-ticket-traversal-measurements.md)
records the accepted implementation at unchanged memory budgets and ticket p95 <= 100 ms. The
depth-5 request was benchmarked on a three-deep hierarchy; no five-deep performance claim is made.

**Tag identity/synonyms and the tag writer remain blocked.** LADR-10 permits only evidence-backed
`near-miss-tag` skill reporting from already-authorized examined material, with supporting UUID/version,
basis, scope and observation/analysis classification. No extra search, broadened selection, guessed
excluded record or write. Executable `near_miss_tags.py` validates approved examined references,
exact tag mismatch and grounded caller/skill analysis without I/O beyond stdin/stdout. NFR-04 fixes
the taxonomy; this helper does not implement or approve the full dossier, which remains In Discovery.

## Non-Functional Requirements

See [`./nfrs/`](./nfrs/).

| NFR | Attribute | Target (summary) | Status |
|-----|-----------|------------------|--------|
| [NFR-01](./nfrs/NFR-01-confidentiality.md) | Confidentiality | Zero hidden-dimension memories in a bundle, at any depth; artefact declares sensitivity | Draft |
| [NFR-02](./nfrs/NFR-02-reproducibility.md) | Reproducibility | Byte-identical bundle for one anchor set against an unchanged store | Draft |
| [NFR-03](./nfrs/NFR-03-bounded-cost.md) | Bounded cost | Reach bounded and named on the wire; preview without composition; numeric limits derived from the reference workflows, not assumed | Draft |
| [NFR-04](./nfrs/NFR-04-completeness.md) | Completeness | Selected count reconciles: present + omitted-with-reason, no silent loss | Draft |
| [NFR-05](./nfrs/NFR-05-attribution.md) | Traceability | 100% of dossier claims carry memory uuid, version and capture time | Draft |
| [NFR-06](./nfrs/NFR-06-read-only.md) | Integrity | Zero writes — row counts and version chains byte-identical after an export | Draft |
| [NFR-07](./nfrs/NFR-07-fidelity.md) | Fidelity | Every condition, exception and lifecycle state needed to apply a claim survives composition | Draft |
