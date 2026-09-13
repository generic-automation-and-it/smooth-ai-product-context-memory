# BRD-002: Contextual Knowledge Export

| | |
|---|---|
| **Document** | Business Requirements Document |
| **Status** | Draft |
| **Owner** | Product owner / practitioner |
| **Last updated** | 2026-09-13 |
| **Extends** | [BRD 001 — Cross-product linked context memory](../001-context-memory/) |
| **Related** | [HLD 005 — Contextual knowledge export](../../hlds/005-contextual-export/) |

> This document states **what the business needs and why**. It deliberately contains no technology
> choices, no data structures and no implementation detail — those live in the HLDs.
>
> It **extends BRD-001** rather than replacing it. Requirement numbering continues BRD-001's space
> (`BR-18` onwards): one product, one requirement vocabulary. Every requirement below is either new,
> or an explicitly named extension of an existing `BR-NN`.

---

## 1. Executive summary

BRD-001 gives the practitioner a durable memory that answers questions **one at a time**. Recall is
deliberately narrow: a small, relevant, attributed set, because a knowledge base large enough to be
useful is too large to load (BR-06).

That narrowness has a cost. There is no way to ask the store for **everything it knows about one body
of work** and receive it as a single readable thing. The knowledge exists, it is labelled with the
work it came from, and it is linked to the decisions it follows from — but it can only be consumed in
question-sized pieces, by the assistant, one session at a time.

This capability produces the missing artefact: a **contextual export** — one ordered, deduplicated,
attributed document assembled from everything the store holds about a chosen slice of work, selected
by **repository, initiative, ticket and tags**, and widened along the **relationships between
memories** so that knowledge which is relevant but differently labelled is not left behind.

The document is not a dump. Composing it applies judgement: material is ordered so that reasoning
reads as reasoning, restatements of the same claim are collapsed once, and superseded positions are
marked as superseded rather than mixed in with current ones.

Crucially, the composition also reports **what it could not do**: the **gaps** in what the store
knows, the **contradictions** it holds without resolving them, and the **quality problems** it found
while reading itself. The store is at its most valuable when it is honest about its limits, and this
is the first capability that can assess those limits across a whole slice of work rather than one
answer at a time.

The business case is **transferable context**. Reasoning that today can only be recalled into an AI
session becomes an artefact a human can read, a new agent can be handed, a pull request or design
document can be written from, and a returning practitioner can use to re-enter work abandoned months
ago — without re-deriving any of it from source.

---

## 2. Business problem

BRD-001 solved capture and recall. Four problems remain, and all four are consumption problems.

**Knowledge cannot leave the assistant.** The store's only consumer is an AI session. Anything a human
wants to read — a handover, a design rationale, a summary for a client — has to be re-elicited from
the assistant conversationally and re-typed. The knowledge is durable; access to it is not.

**Nobody can see the whole picture of one thing.** Narrow recall is correct for answering a question
and wrong for understanding a body of work. Re-entering a repository after six months means asking
the same store twenty questions and hoping the twenty answers cover it, with no way to know what the
twenty questions missed.

**Labels fragment what belongs together.** Knowledge about one initiative is captured across several
tickets, several repositories and several sessions, under whatever labels applied at the time. Any
single label therefore returns a fraction of the truth. The relationships between memories know the
rest, and nothing reads them in bulk.

**The store's own defects are invisible.** BRD-001 requires contradictions to be surfaced (BR-10) and
gaps to be admitted (BR-15) — but only in the moment a retrieval happens to touch them. A
contradiction between two memories that are never recalled together is never surfaced. A gap in a
whole area is invisible because nobody asked the question that would reveal it. Defects are found
by accident, one at a time, if ever.

### What today costs

| Cost | How it shows up |
|---|---|
| **Re-elicitation** | Knowledge that exists is asked for again, conversationally, to get it into readable form |
| **Twenty questions** | Re-entering a body of work means guessing which questions to ask, with no coverage signal |
| **Fragmented recall** | One label returns one slice; the rest is reachable only by knowing to follow it |
| **Undetected contradiction** | Two current claims that cannot both be true, never recalled together, never surfaced |
| **Unknown unknowns** | Gaps are found when a decision goes wrong, not when the store is consulted |
| **Non-transferable context** | A colleague, a client or a fresh agent cannot be handed the reasoning; only its holder can retell it |

---

## 3. Business objectives

| # | Objective | Why it matters |
|---|---|---|
| **BO-6** | Make accumulated knowledge consumable as one document, not only as answers | Answers serve a question; a document serves an understanding — and it can be handed to someone else |
| **BO-7** | Assemble by the way work is organised, then widen by relationship | Any single label under-selects; relationships hold the part the label missed |
| **BO-8** | Make the composition honest about its own limits | A document that hides its gaps and contradictions is more dangerous than no document, because it reads as complete |
| **BO-9** | Turn the store's defects into a reviewable list | Contradictions and gaps found in bulk can be fixed; found by accident they are absorbed as confusion |
| **BO-10** | Keep the exported artefact under the same ownership as the store | An export is a copy of commercially sensitive reasoning; ease of sharing must not become accidental disclosure |

These extend, and do not replace, BRD-001's BO-1 … BO-5. BO-6 is the consumption half of BO-2 (removing
the cost of re-establishing context); BO-8 and BO-9 are BO-4 (keeping recalled knowledge trustworthy)
applied to a whole slice rather than one answer.

---

## 4. Users and stakeholders

| Party | Interest |
|---|---|
| **Primary user — the practitioner** | Requests an export, reads it, acts on its findings |
| **The AI assistant** | Composes the document and its findings; consumes an export when handed one as context. Never a decision-maker |
| **A recipient of an exported document (human or agent)** | Reads reasoning they did not capture. **Has no access to the store** — they receive a document the practitioner chose to give them |

The single-user decision in BRD-001 §4 is unchanged. An export is a **file the practitioner produces
and controls**, not a shared surface: producing one grants nobody access to the store. This is
deliberately the only sharing mechanism, because a file the practitioner reviews before sending is
governed by their judgement, where a shared query surface would require the access-control model
BRD-001 exists without.

---

## 5. Scope

### In scope

- Selecting a slice of stored knowledge by repository, initiative, ticket and tags, in combination
- Widening that selection along recorded relationships between memories, to a stated limit
- Composing the selection into one ordered, readable, self-contained document
- Collapsing restatements of the same knowledge, retaining where each came from
- Marking superseded and no-longer-true material as such, rather than mixing it with current material
- Reporting gaps: what the slice should contain and does not
- Reporting contradictions: claims that cannot both be true, presented for decision
- Reporting quality problems found while reading: unattributed claims, unreachable knowledge, summaries too weak to find
- Producing the document as a portable file the practitioner can read, keep and hand on

### Out of scope

| Excluded | Reason |
|---|---|
| Reading an exported document back into the store | The store is the record; a document is a projection of it. Importing would make an edited copy authoritative and destroy that direction — the same reason the existing whole-store dump has no import path |
| Replacing the whole-store readable dump | That exists to satisfy BR-17 (readability without specialist tooling) and is deliberately complete, unordered and unjudged. This is a curated slice; both are needed and neither substitutes for the other |
| Automatically resolving the contradictions it finds | BR-10 already forbids silent resolution. Finding a contradiction in bulk does not change who decides it |
| Automatically fixing the defects it reports | Reporting is a read. Acting on a report is a write, and writes go through the approved capture path with the practitioner's judgement (BR-01, BR-13) |
| Granting a recipient access to the store | §4 — the artefact is the sharing mechanism, deliberately |
| Continuous or scheduled regeneration | An export answers a question at a moment. A standing regeneration is upkeep, and upkeep is what BR-01 forbids |
| Publishing or transmitting exports anywhere | The document holds the store's most sensitive material in its most portable form. Where it goes is the practitioner's decision, made outside this capability |

---

## 6. Business requirements

### Selection

**BR-18 — A slice must be selectable by the way work is organised.**
Repository, initiative, ticket and tags, usable individually and in combination. This is BR-04's
retrieval vocabulary applied to bulk assembly rather than to a single question.
*Accepted when:* a practitioner can name any combination of repository, initiative, ticket and tags
and receive everything the store associates with it.

**BR-19 — Selection must follow relationships beyond the labels given, and must say how far it went.**
Knowledge relevant to a slice is frequently labelled differently, and the relationships between
memories are what connect the two. Unbounded following returns the whole store; so the reach is
limited and the limit is stated in the result, because an unstated limit is indistinguishable from
completeness. Extends BR-11 (relationships preserved) into retrieval.
*Accepted when:* the export includes related knowledge not carrying the requested labels, and reports
both how far it followed relationships and what it stopped short of.

**BR-20 — Selection must be reproducible.**
The same slice against an unchanged store must select the same knowledge. Composition applies judgement
and may word things differently; *what was selected* must not vary, or no finding is checkable and no
two exports are comparable.
*Accepted when:* repeating a request against an unchanged store selects an identical set of knowledge.

### The document

**BR-21 — The output must be one self-contained document, readable without the application.**
Satisfies BR-17 for the curated case: an artefact whose value depends on the tool that made it is not
transferable.
*Accepted when:* the document can be read in full by someone with no access to the store or the
application.

**BR-22 — Ordering must reflect how the knowledge relates, not how it is stored.**
Reasoning is a chain — a measurement produced a finding, which justified a decision (BR-11). A document
that presents that chain in arrival order forces the reader to reconstruct it, which is the work the
document exists to remove.
*Accepted when:* a decision and the reasoning it rests on appear in an order a reader can follow
without cross-referencing.

**BR-23 — Restated knowledge must appear once, with every origin retained.**
The same conclusion reached in three sessions is one piece of knowledge and three pieces of evidence.
Printing it three times pads the document; discarding two loses the corroboration.
*Accepted when:* a claim captured more than once appears once, and every capture it was drawn from
remains identifiable.

**BR-24 — Superseded and no-longer-true material must be marked, never silently mixed or silently dropped.**
Extends BR-08 and BR-09 into the document. Both mistakes are serious: mixing presents a reversed
decision as current, and dropping loses the reasoning that made the reversal worth knowing.
*Accepted when:* the document distinguishes current material from superseded and from no-longer-true
material, and retains the latter with its reasoning.

**BR-25 — Every statement in the document must be traceable to the knowledge it came from.**
Extends BR-07 from a recalled item to a composed sentence. Composition rewrites and merges; without
traceability the reader cannot tell a stored claim from an inference the composition made.
*Accepted when:* every statement in the document can be followed back to the stored knowledge it was
drawn from.

**BR-26 — The document must read as evidence, not as instruction.**
Extends BR-12. The risk grows here: a long, ordered, confident-looking document handed to an agent is
far more likely to be obeyed than a short attributed recall.
*Accepted when:* the document presents its content as attributed claims to evaluate, and is
identifiable as a generated projection rather than an authority.

### The findings

**BR-27 — The export must report the gaps in what it assembled.**
Extends BR-15 from one absent answer to a whole slice. A slice with decisions but no measurements, or
a repository with no recorded constraints, is a gap worth knowing about — and the export is the first
vantage point from which such an absence is visible at all.
*Accepted when:* the export reports what the slice should reasonably contain and does not, as a
distinct part of its output.

**BR-28 — The export must report contradictions, and must not resolve them.**
Extends BR-10. Where BR-10's stated authority settles a disagreement, the export applies it and says
so. Where nothing settles it, the contradiction is the finding.
*Accepted when:* claims that cannot both be true are reported together with what each rests on, and
are never silently reconciled or silently omitted.

**BR-29 — The export must report the quality problems it found while reading.**
Knowledge nothing links to, claims without a source, summaries too weak to ever be found again. These
are only visible when the slice is read as a whole, and each is actionable.
*Accepted when:* the export reports the quality problems it encountered, specifically enough to act on.

**BR-30 — Nothing may be omitted silently.**
If a limit was reached — a relationship not followed, material left out — the omission is reported.
An export that quietly truncates is worse than one that refuses, because it reads as complete.
*Accepted when:* everything selected is either present in the document or listed as omitted with a
reason.

**BR-31 — The findings must be reportable without changing the store.**
Extends BR-13. An export is a read. Recording its findings as knowledge is a capture, and captures go
through the normal approved path so that judgement and attribution are not bypassed by a side effect.
*Accepted when:* producing an export leaves the store unchanged, and any finding recorded afterwards
goes through normal capture.

### Cost and control

**BR-32 — The cost of an export must be knowable before it is paid.**
Composition is the expensive operation in the product — proportional to the size of the slice, not to
one fact. A practitioner asking for "everything about this repository" must be able to see the size
of what they asked for before committing to it. Consistent with BR-06's refusal to let volume
overwhelm the task.
*Accepted when:* the size and cost of a requested export can be seen before the document is composed.

**BR-33 — An export must not contain material excluded from ordinary retrieval.**
Extends BR-03 (sensitive material never captured) and BR-16 (sole control). The store deliberately hides some
material from being cited as fact; a bulk assembly is exactly where such a rule is most easily and
most damagingly bypassed, and the artefact is portable, so a leak leaves the machine.
*Accepted when:* material hidden from ordinary retrieval is absent from the export, including where it
was reached by relationship rather than requested directly.

**BR-34 — The artefact must be treated as sensitive by default.**
It is the store's most sensitive content in its most copyable form, in plain text.
*Accepted when:* the artefact declares its sensitivity, and is produced somewhere that is not
committed or synchronised by default.

---

## 7. Success measures

| Measure | Target |
|---|---|
| **Re-entry** | Returning to untouched work is served by reading one export rather than by an interrogation of the store |
| **Transferability** | A recipient with no access to the store can act on the reasoning in an export |
| **Coverage confidence** | The practitioner can tell what an export did *not* cover, without inspecting the store |
| **Defects found in bulk** | Contradictions and gaps are surfaced by exports rather than discovered by a decision going wrong |
| **Findings acted on** | Reported gaps become captured knowledge; reported contradictions become decisions. A findings list nobody acts on means the findings are not specific enough |
| **Trust in the document** | The practitioner hands an export to a colleague or an agent without first re-reading it against source. This is the real measure — the others are proxies |

---

## 8. Constraints and assumptions

### Constraints

| Constraint | Implication |
|---|---|
| Inherits every BRD-001 constraint | Single practitioner, offline-capable, sensitive commercial reasoning, assistant-mediated |
| Composition requires the assistant's judgement | Ordering, collapsing and contradiction-finding are judgement, so an export is only as good as the model composing it — and is unavailable when no model is |
| The store's knowledge of relationships is incomplete | Relationships are recorded when capture proposes them and the practitioner accepts (BR-11). Unrecorded relationships cannot be followed, so widening is bounded by what capture noticed |
| Cost scales with the slice | Unlike recall, an export's cost grows with what is asked for — which is why BR-32 exists |

### Assumptions

- Slices worth exporting are tens to low hundreds of memories — large enough to be unreadable by hand, small enough to compose in one pass.
- The practitioner reads an export before handing it on, which is what makes the artefact an acceptable sharing mechanism (§4).
- Repository, initiative, ticket and tags are sufficient selection vocabulary; if they are not, that is a BRD-001 BR-04 failure and is fixed there.
- Findings will initially be dominated by capture-quality problems rather than genuine contradictions, because the store is young. Both are useful and the mix is expected to shift.

---

## 9. Risks

| Risk | Impact | Mitigation |
|---|---|---|
| **The document reads as more authoritative than the store** | A composed narrative is more persuasive than the attributed fragments it was built from, and an error in it propagates further | Traceability on every statement (BR-25) and evidence-not-instruction framing (BR-26) |
| **Collapsing duplicates loses a distinction that mattered** | Two claims that looked like restatements were subtly different; one is silently gone | Collapse retains every origin (BR-23), so a merge is inspectable rather than destructive |
| **Findings are too vague to act on** | The findings section becomes decoration, the way most automated quality reports do | Success is measured by findings acted on, not findings produced (§7) |
| **Selection under-reaches and the export looks complete** | The most dangerous failure: a confidently incomplete document | The reach and its limits are reported (BR-19), and nothing is omitted silently (BR-30) |
| **Selection over-reaches** | An export of "everything about this repository" becomes the whole store, expensive and unreadable | Cost visible before composition (BR-32); reach is bounded and the bound is stated |
| **Sensitive material leaves the machine** | A portable plaintext copy of the store's most sensitive reasoning | Hidden material excluded even when reached by relationship (BR-33); artefact declares sensitivity and is not committed by default (BR-34) |
| **The export becomes a maintained document** | Someone edits one, and the edited copy competes with the store as the record | Projection with no path back in (§5 out of scope); regeneration is cheap enough that editing is never the easier option |

---

## 10. Glossary

Extends BRD-001 §10; every term there still applies.

| Term | Meaning |
|---|---|
| **Slice** | The body of knowledge selected for one export, named by repository, initiative, ticket and tags |
| **Widening** | Extending a slice along recorded relationships to include relevant knowledge that does not carry the requested labels |
| **Export** | The act of assembling a slice, and the document it produces |
| **Composition** | The judgement applied to a slice to make it a document: ordering, collapsing, marking supersession |
| **Findings** | The report accompanying the document: gaps, contradictions, quality problems and omissions |
| **Contradiction** | Two claims held as current that cannot both be true |
| **Gap** | Knowledge a slice should reasonably contain and does not |
| **Whole-store dump** | The pre-existing complete, unordered, unjudged readable projection of everything stored. Not an export in this document's sense |

---

## 11. Related documents

| Document | Covers |
|---|---|
| [BRD 001 — Cross-product linked context memory](../001-context-memory/) | The product this extends: capture, recall, trust, ownership (BR-01 … BR-17) |
| [HLD 005 — Contextual knowledge export](../../hlds/005-contextual-export/) | How selection, assembly, composition and findings work |
| [HLD 003 — Graph edges](../../hlds/003-graph-edges-on-age/) | How relationships are held and traversed — the mechanism BR-19 depends on |
| [HLD 004 — Memory recall feedback](../../hlds/004-memory-recall-feedback/) | Findability signals; overlaps BR-29's weak-summary finding |
