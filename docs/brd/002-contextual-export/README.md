# BRD-002: Contextual Knowledge Export

| | |
|---|---|
| **Document** | Business Requirements Document |
| **Status** | Draft |
| **Owner** | Product owner / practitioner |
| **Last updated** | 2026-09-13 |
| **Extends** | [BRD 001 — Cross-product linked context memory](../001-context-memory/) |
| **Related** | [HLD 005 — Contextual knowledge export](../../hlds/005-contextual-export/) |

> This document states **what the business needs and why**. Technology choices, data structures
> and implementation belong in the HLDs. It extends BRD-001 and continues its requirement space
> at `BR-18` through `BR-36`.

---

## 1. Executive summary

A practitioner should be able to carry accumulated product understanding into the next task without
reconstructing it from past conversations or supplying the entire knowledge base.

For a product manager preparing a specification, this means concentrating on the new change while
their AI collaborator supplies established terminology, rules, constraints, exceptions and decision
rationale. The resulting spec should be explicit enough for a coding agent to implement without
filling important gaps with plausible but incorrect assumptions. Missing answers should become
focused questions for the practitioner.

**Contextual export** provides a portable grounding document for this work. It assembles a selected
body of knowledge, adds supporting context through recorded relationships, and presents the result
with its sources, lifecycle and limits. The same capability supports handovers and returning to
earlier work. A recipient can read the document without access to the application.

The export must preserve the details that determine how knowledge applies. A summary that loses an
exception can produce an incorrect spec; a document that includes too much can overwhelm the task.
The practitioner must be able to understand and adjust that tradeoff.

One slice of knowledge feeds several different kinds of work, so the document can be **focused** on the
work it is about to feed — gathering requirements, deciding architecture, writing a specification,
implementing, or reviewing. A focus changes ordering, weighting and depth. It never changes what was
selected, and it never hides a finding: a focused document that quietly omits is worse than an
unfocused one, because its emphasis reads as the store's. The unfocused document remains the default.

Success is less repeated explanation and more consistent subsequent work, with uncertainty visible.
The export provides evidence for that work; it does not make unresolved product decisions.

## 2. Business problem

BRD-001 addresses capture and recall. This capability addresses the work of assembling recalled
knowledge into something another person or agent can use independently.

| Problem | Cost to the practitioner |
|---|---|
| **Existing context must be repeated** | A new spec requires restating established product details to prevent inconsistent assumptions |
| **Recall requires knowing what to ask** | Re-entry or handover becomes a sequence of questions with no clear view of what was missed |
| **Knowledge spans work items** | A ticket's labels may omit relevant rules or rationale recorded elsewhere |
| **Summaries can erase distinctions** | Conditions, customer exceptions or proposed behavior are flattened into misleading general statements |
| **Uncertainty is difficult to inspect** | Conflicts and missing answers surface late, after they have influenced the work |

The existing whole-store readable dump makes stored records accessible. Contextual export adds
selection, composition and findings for a chosen body of work. Narrow recall remains useful during
a conversation; an export supplies a coherent starting point without requiring the recipient to
repeat that retrieval process.

## 3. Business objectives

| # | Objective | Why it matters |
|---|---|---|
| **BO-6** | Make accumulated knowledge usable as one portable document | A practitioner can ground a spec, hand over reasoning or resume work without reconstructing the context |
| **BO-7** | Assemble by work association and widen through relationships | Relevant supporting knowledge can be included even when labelled differently |
| **BO-8** | Preserve meaning and expose the document's limits | Recipients can distinguish applicable knowledge from proposals, conflicts and unanswered questions |
| **BO-9** | Make discovered gaps and defects actionable | Findings can lead to clarification and correction through normal work |
| **BO-10** | Keep the artefact under the practitioner's control | Producing portable context does not publish it or grant access to the store |

These extend BRD-001's BO-1 through BO-5. BO-6 supports removing repeated context-setting (BO-2);
BO-8 and BO-9 apply trustworthy recall (BO-4) to a composed document.

## 4. Users and stakeholders

| Party | Interest |
|---|---|
| **Primary user — the practitioner** | Requests the export, reviews its scope and uses the document and findings |
| **The AI assistant** | Assembles and consumes attributed context; brings unresolved product choices back to the practitioner |
| **Recipient — human or agent** | Uses the exported context without needing access to the application or store |

**There is no single primary consumer, and that is deliberate.** The practitioner is always the
*requester*; who reads the result varies by occasion — themselves months later, a colleague, an agent
about to do the work. Naming one of them primary would optimise the document for that one and quietly
degrade it for the others. The design instead names the **work** the document is about to feed
(BR-35), which is knowable at request time in a way the reader's identity is not.

BRD-001's single-user boundary is unchanged. The file is the sharing mechanism; multi-user access
and a shared team corpus are out of scope. The practitioner reviews the file before handing it on.

### Reference workflow: preparing a specification

1. The practitioner requests context for a change, identifying the relevant work. The intended use
   may already be clear from the conversation; a separate configuration task is not required.
2. The assistant resolves the selection and shows its scope, reach, size and estimated composition
   cost. The practitioner can proceed, narrow the request or cancel.
3. The export presents applicable knowledge and reasoning, distinguishes proposals and history,
   and identifies unresolved questions and coverage limits.
4. The practitioner and their collaborator use it to develop the spec. Established details come
   from evidence; missing product decisions are clarified with the practitioner.
5. Answers and decisions arising from that work use BRD-001's normal capture checkpoint. Export
   generation remains a read, and the practitioner does not maintain the export as a second source.

## 5. Scope

### In scope

- Selecting knowledge by repository, initiative, ticket and tags, individually or in combination.
- Widening through recorded relationships between memories within stated limits.
- Composing one readable, attributed document with the detail needed to apply its claims correctly.
- Focusing that document on the work it is about to feed — requirements, architecture, specification,
  implementation, review — while leaving the unfocused document as the default.
- Consolidating equivalent restatements while preserving all origins and meaningful distinctions.
- Distinguishing current, proposed, superseded and no-longer-true knowledge.
- Reporting gaps, conflicts, quality problems and omissions within the examined material.
- Previewing size and estimated composition cost, then producing a portable local file.

### Out of scope

| Excluded | Reason |
|---|---|
| Writing the final spec or making new product decisions | The export grounds subsequent work; unresolved choices remain with the practitioner |
| Importing an edited export into the store | The file is a projection. New decisions arising from its use follow normal capture. *(A separate, opt-in understanding import — including the current-session dump under BR-45 — is a distinct capability owned by [BRD 003](../003-understanding-transfer/); it does not reopen this row for the contextual document.)* |
| Replacing the whole-store readable dump | BR-17's whole-store readability and this curated document serve different needs |
| Automatically resolving genuine conflicts or correcting stored knowledge | Reporting is a read; changes require the existing capture path and appropriate judgement |
| A complete audit of the store | Findings are limited to material examined during the export |
| Shared team use or recipient access to the store | The product remains single-user |
| Continuous or scheduled regeneration | This capability produces a requested snapshot; ongoing regeneration is outside its scope |
| Publishing or transmitting the file | Distribution is a separate practitioner decision |

## 6. Business requirements

### Selection

**BR-18 — A slice must be selectable by the way work is organised.**
Extends BR-04 to document assembly. Repository, initiative, tracker-qualified ticket and tags are
usable individually and in combination. Combined criteria must have defined, visible semantics.

*Accepted when:* the practitioner can select each category and combinations of categories, see how
the criteria combine, and receive matching knowledge subject to stated limits and retrieval policy.
Ambiguous identifiers are clarified; no matches are reported without silently broadening the request.

**BR-19 — Selection must follow relationships beyond the labels given and state its reach.**
Extends BR-11. Related knowledge may carry different labels, but a relationship alone does not make
a rule applicable to another product or customer.

*Accepted when:* eligible related knowledge is included within the stated reach; direct matches and
related additions are distinguishable, with the reason for each addition and its original scope.
The result reports where expansion stopped and why, without revealing excluded material (BR-33).

**BR-20 — Selection must be reproducible.**
The same effective criteria, limits and retrieval policy against an unchanged store must select the
same knowledge. Composition may word it differently.

*Accepted when:* repeated selection identifies the same memories and versions. The export records
the effective selection so it can be repeated; wording is not required to be identical.

### The document

**BR-21 — The output must be one self-contained document, readable without the application.**
Extends BR-17 to the curated case. The document identifies its scope, generation date and intended
use when known. Without a stated use, it provides an overview of the selected work.

*Accepted when:* a recipient without store access can understand the included claims, their
applicability and the findings. Essential meaning is in the file, not only behind source links.
The file is identifiable as a dated snapshot, not a guarantee of current truth when read later.

**BR-22 — Composition must preserve reasoning and the detail needed to apply it.**
Extends BR-11 into a readable account. Ordering should connect evidence, findings and decisions.
Concision must preserve definitions, conditions, thresholds, permissions and exceptions.

*Accepted when:* a reader can follow a decision and its recorded rationale. In the acceptance
examples, every condition and exception needed to interpret the selected claims survives composition;
exact wording is retained where paraphrasing would change meaning. Missing rationale is not invented.

**BR-23 — Equivalent restatements must appear once, with every origin retained.**
Equivalence requires the same meaning, applicability and lifecycle. Similar wording is insufficient.
Several captures of one source are not necessarily independent corroboration.

*Accepted when:* equivalent repeated claims are consolidated with all contributing origins
identifiable. Claims that differ by scope, condition, time or status remain distinguishable;
uncertain equivalence is not used to discard a distinction.

**BR-24 — Current, proposed and historical knowledge must remain distinguishable.**
Extends BR-08 and BR-09. Proposed behavior must not appear as shipped behavior. Selected superseded
and no-longer-true positions remain readable with their recorded reasoning.

*Accepted when:* the document distinguishes these states, retains selected earlier positions and
their recorded replacements, and explicitly identifies unknown status or rationale. Capture recency
alone is not treated as evidence that behavior has shipped or remains true.

**BR-25 — Every substantive claim must be traceable, and analysis must be labelled.**
Extends BR-07. Composition can rewrite and merge claims; it must not make its own inferences look
like stored facts. Headings and navigation are not substantive claims.

*Accepted when:* each substantive factual claim identifies its supporting memories, versions and
capture dates, with source and confidence as recorded. Missing provenance is visible. Generated
inferences and questions are labelled as analysis and state the evidence or expectation behind them.

**BR-26 — The document must present evidence for evaluation, not agent operating instructions.**
Extends BR-12. An attributed product rule can retain its normative meaning without becoming an
unconditional instruction to the consuming agent.

*Accepted when:* the document identifies itself as a generated projection, presents claims with
their scope and status, and keeps instruction-like source content attributed rather than adopting
it as directions to the recipient agent.

### The findings

**BR-27 — Gaps must become specific questions with a stated basis.**
Extends BR-15. A gap is an answer missing from the examined material, needed for the stated task or
to interpret an included claim. An explicit practitioner expectation can also supply that basis.
A general best-practice suggestion may be offered as analysis, but is not evidence of a product gap.

*Accepted when:* each gap states the unanswered question, why it matters and what evidence or
expectation prompted it. An absent answer is not invented. The export distinguishes “not found in
this material” from “does not exist” and does not claim to have discovered every possible gap.

**BR-28 — Disagreements must be explained; genuine conflicts must remain unresolved.**
Extends BR-10. Compare applicability and lifecycle before declaring a conflict. Where a stated
authority settles a disagreement, apply it transparently and retain the losing position.

*Accepted when:* incompatible same-scope claims are presented with their sources and the decision
needed. Any authority-based resolution names its basis. Scoped exceptions and differences between
proposed and shipped behavior are not automatically classified as genuine conflicts.

**BR-29 — Quality findings must be supported and actionable.**
Report problems observed while examining the slice, such as missing provenance or unclear scope.
Distinguish observable defects from hypotheses about weak summaries or findability.

*Accepted when:* each finding identifies affected knowledge, the observation, its consequence and a
suggested next action. A slice-only review does not claim that a record has no links anywhere in the
store or can never be retrieved. “None detected” is qualified by the examined scope.

**BR-30 — Nothing may be omitted silently.**
Selection limits and composition omissions must both be visible. An export cannot imply exhaustive
product knowledge merely because it accounted for all selected records.

*Accepted when:* every selected memory is represented directly, mapped to a consolidated claim, or
listed as omitted with a reason. The document reports reached limits and unexamined boundaries,
and explicitly marks incomplete output. This accounting must not reveal hidden material (BR-33).

**BR-31 — Producing an export must leave the store unchanged.**
Extends BR-13. Findings are output. Recording answers or corrections is a subsequent capture through
the normal workflow, with its attribution and judgement.

*Accepted when:* generation, cancellation and failure do not change stored knowledge or its
relationships. Findings are not silently saved as facts, and subsequent updates follow normal capture.

### Cost and control

**BR-32 — Size and estimated cost must be visible before composition.**
Extends BR-06's context constraint to a document. The practitioner needs to judge whether the scope
is useful and affordable before composition begins.

*Accepted when:* a preview shows the effective scope, selected volume, reach limits and estimated
composition usage before composition runs. Estimates state their assumptions and uncertainty;
monetary cost is shown when pricing is known, otherwise identified as unavailable. The practitioner
can narrow or cancel. If requested scope exceeds supported limits, the system reports this rather
than silently truncating or expanding the work. Essential conditions are not compressed away to fit.

**BR-33 — An export must not contain material excluded from ordinary retrieval.**
Extends BR-03 and BR-16. The applicable retrieval policy governs every part of the export, including
historical material and relationships.

*Accepted when:* hidden material is absent from direct selection, relationship expansion, preview,
composed claims, citations, findings and omission lists. It cannot be disclosed indirectly through
an otherwise visible memory's relationship to it.

**BR-34 — The artefact must be treated as sensitive by default.**
The export is a portable copy of potentially sensitive reasoning. The practitioner controls its
destination and any subsequent distribution.

*Accepted when:* the artefact declares its sensitivity, its destination is visible, and the default
destination is neither committed nor synchronised. Generation does not publish or transmit it.
If a safe default destination cannot be established, the practitioner must choose one before saving.

### Focus

**BR-35 — An export must be focusable on what the reader is about to produce.**
"For anyone" is not a target reader, and a document with no target reader is optimised for nothing —
BR-06's problem returning at document scale. The same slice of knowledge serves different work
differently: gathering requirements wants constraints, business rules and open questions; making an
architectural decision wants what was decided and what was rejected; writing a specification wants
what must observably be true; implementing wants concrete constraints, prior failures and known
limitations; reviewing wants the problems first. The unfocused form remains the default, because a
complete dump is itself a legitimate and frequent need.

*Accepted when:* the practitioner can request a focus and receive a document ordered and weighted for
the work named, the available focuses are a stated set, and requesting none yields the complete
unfocused document.

**BR-36 — A focus must not change what was selected, and must never suppress a finding.**
The dangerous reading of BR-35 is a focus that quietly narrows selection. That produces the
confidently incomplete document BR-19 and BR-30 exist to prevent, and it makes two focuses of one
slice incomparable — a reader cannot tell whether a difference is the lens or the store. Findings are
the honesty mechanism, so they survive every focus: a contradiction is *more* consequential to
implementation than to requirements, not less.

*Accepted when:* two focuses of one slice contain the same selected knowledge and differ only in
ordering, weighting and depth; whatever a focus does not surface is listed as omitted with a reason;
and gaps, contradictions and quality problems appear under every focus.

## 7. Success measures

Evaluate spec preparation, handover and re-entry using representative examples reviewed by the
product owner. Identify expected critical details and unresolved questions before producing exports.

| Measure | Acceptance or evaluation |
|---|---|
| **Detail fidelity** | All identified critical rules, conditions and exceptions in the selected examples survive composition |
| **Grounding** | No invented product facts, unlabelled inferences or proposals presented as shipped behavior in reviewed examples |
| **Traceability and coverage** | Every factual claim is attributed and every selected memory is accounted for |
| **Useful questions** | Known answers are supplied; unresolved task-relevant questions are surfaced with their basis |
| **Integrity** | No excluded material is disclosed and no stored knowledge is changed by export |
| **Practitioner effort** | Measure time and repeated explanation needed to complete an acceptable spec or resume work, compared with the current workflow |
| **Transferability** | A recipient without store access uses the document for the intended task; record missing context and corrections needed |
| **Focus earns its keep** | A focused export is used to produce the artefact it was focused for, without the practitioner falling back to the unfocused document; consistent fallback means the focuses are wrong or unnecessary |

Establish effort baselines and improvement targets during validation. Document size should be
assessed alongside fidelity; fewer words or tokens alone is not success. Trust means less need to
reconstruct context with evidence still available to inspect, not an expectation of sharing blindly.

### Illustrative acceptance scenario

An invitation-related slice contains a current rule limiting invitations to administrators, a
customer-specific expiry exception, a proposed change to the default expiry, an older superseded
default and equivalent copies of the administrator rule. It has no answer about whether resending
invalidates a previous invitation.

The export must retain the role restriction and scoped exception, consolidate equivalent copies
with all origins, label the proposed change, and keep the old default separate with its recorded
history. For a resend-spec task it must ask the invalidation question rather than invent an answer.
Additional acceptance cases cover genuine same-scope conflicts, no matches, missing provenance,
hidden related material and a reached selection or composition limit. This scenario is synthetic.

## 8. Constraints and assumptions

### Constraints

- All BRD-001 constraints continue to apply, including single-user ownership and offline operation.
- Composition depends on available assistant capability. Unavailability must be reported; an
  uncomposed selection must not be represented as a completed contextual document.
- The export can only preserve knowledge and relationships that are available to it. Missing
  sources, lifecycle information or applicability must be exposed rather than guessed.
- The document is a snapshot. Its date does not certify that every included claim was current on
  that date, and later capture does not update copies already handed to recipients.

### Assumptions to validate

- Repository, initiative, ticket and tags are useful selection criteria for the reference workflows.
- Relevant exports are small enough to remain useful to their recipients; actual size, cost and
  readability limits must be established through examples, not assumed from memory counts alone.
- The practitioner reviews an export before distributing it and can use normal work checkpoints
  to capture answers or corrections arising from its findings.

## 9. Risks

| Risk | Impact | Mitigation |
|---|---|---|
| **Persuasive but inaccurate composition** | A missing condition or invented inference produces an incorrect spec | Fidelity acceptance examples, preserved scope and lifecycle, attribution (BR-22–26) |
| **False completeness** | A recipient assumes omitted or uncaptured knowledge does not exist | Bounded gap claims, visible reach and complete selection accounting (BR-19, BR-27, BR-30) |
| **Excessive volume** | The document overwhelms the task or costs more than expected | Preview and scope adjustment; preserve meaning when reducing size (BR-32) |
| **Vague findings** | The practitioner cannot turn the report into useful action | Findings name evidence, affected knowledge and a question or next action (BR-27–29) |
| **Sensitive content becomes portable** | Excluded knowledge leaks through the document or its metadata | Retrieval exclusions throughout the output and local controlled delivery (BR-33–34) |
| **A stale or edited copy becomes authoritative** | Subsequent work relies on outdated or unrecorded decisions | Dated projection, no import path, corrections through normal capture (BR-21, BR-31) |
| **A focus is mistaken for the whole** | A focused document is read as everything the store knows, and its emphasis is read as the store's | Focus never narrows selection and never hides a finding (BR-36); the document states which focus produced it |
| **Focuses proliferate** | Five becomes fifteen, each thinly different, and no two exports are comparable | The set is stated and bounded (BR-35); adding one requires naming the work it feeds and how it differs from the nearest existing focus |

## 10. Glossary

Extends BRD-001 §10; its terms continue to apply.

| Term | Meaning |
|---|---|
| **Slice** | Knowledge selected under the effective work criteria, retrieval policy and stated limits |
| **Tag** | BRD-001's free-form Label — the terms are interchangeable in this document |
| **Widening** | Following recorded relationships to include eligible supporting knowledge beyond the initial labels |
| **Export** | The act of composing a slice and the portable document it produces |
| **Composition** | Organising knowledge and reasoning, consolidating equivalent claims and presenting findings without changing the store |
| **Focus** | The work a document is about to feed — requirements, architecture, specification, implementation, review. Changes ordering, weighting and depth; never what was selected. Optional; unfocused is the default |
| **Findings** | Gaps, disagreements, quality problems and omissions identified within the examined scope |
| **Contradiction** | Incompatible claims applying to the same circumstances; a genuine conflict remains when no stated authority settles them |
| **Gap** | An unanswered question within the examined material, needed for the task, an included claim or an explicit practitioner expectation |
| **Whole-store dump** | The existing readable projection of stored records, serving BR-17 independently of contextual composition |
| **Session export** | A projection of the current session's context written to a local folder for cross-session, cross-repo reuse (BRD-003 BR-45) |

## 11. Related documents

| Document | Covers |
|---|---|
| [BRD 001 — Cross-product linked context memory](../001-context-memory/) | Capture, recall, trust and ownership (BR-01 … BR-17) |
| [BRD 003 — Understanding transfer and loading](../003-understanding-transfer/) | Extends this document (`BR-38` … `BR-45`): the Understanding kind, the non-destructive load, the opt-in import and the current-session export for cross-session reuse. Owns [HLD 007](../../hlds/007-understanding-transfer/) |
| [HLD 005 — Contextual knowledge export](../../hlds/005-contextual-export/) | Selection, assembly, composition and findings |
| [HLD 003 — Graph edges](../../hlds/003-graph-edges-on-age/) | Recorded relationships and traversal supporting BR-19 |
| [HLD 004 — Memory recall feedback](../../hlds/004-memory-recall-feedback/) | Findability evidence relevant to BR-29 |
