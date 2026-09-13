# BRD-001: Cross-Product Linked Context Memory

| | |
|---|---|
| **Document** | Business Requirements Document |
| **Status** | Approved |
| **Owner** | Product owner / practitioner |
| **Last updated** | 2026-09-13 |
| **Related** | [HLD 001 — Storage](../../hlds/001-context-memory-storage/) · [HLD 002 — Write pipeline](../../hlds/002-context-memory-write-pipeline/) · [HLD 003 — Graph edges](../../hlds/003-graph-edges-on-age/) |

> This document states **what the business needs and why**. It deliberately contains no technology
> choices, no data structures and no implementation detail — those live in the HLDs.

---

## 1. Executive summary

AI coding assistants forget everything between sessions. The reasoning behind a decision — why an
approach was chosen, what was rejected, what was already tried and failed — survives only as long as
the conversation that produced it. When the session ends, the conclusion may survive in code or a
ticket, but the *reasoning* does not.

This application gives one practitioner a **durable, private memory** for that reasoning: captured as
a byproduct of normal work, **tagged with metadata identifying the work it came from** — ticket,
repository, initiative — **linked to the decisions it follows from or supersedes**, and retrievable
months later.

Those tags and links are the retrieval keys, which makes the store **one memory spanning every
product** rather than a set of per-repository silos: reasoning captured while working on one product
surfaces when the same question arises on another.

The business case is **compounding leverage**. A practitioner whose assistant remembers prior
decisions, and the reasoning behind them, spends less time re-establishing context and makes fewer
decisions that contradict earlier ones. That advantage grows with the store and does not transfer to
anyone who has not been accumulating it.

---

## 2. Business problem

The same problem appears in three forms, and research across existing approaches found all three
independently:

**In the assistant.** Language models are stateless. Each session begins with no knowledge of the last,
so context must be re-supplied every time — a recurring cost paid in both time and attention.

**In the documentation.** Teams repeatedly attempt a durable knowledge base — a wiki, a shared
document space, a glossary. They fail the same way: **updating it is a separate chore**, so it drifts
from reality until nobody trusts it. An untrusted knowledge base is worse than none, because it still
costs time to consult.

**In the organisation.** Knowledge is scattered across documents, messages, tickets and systems, and
the most valuable part exists only in individuals' memories — difficult to search, impossible to
scale, and lost entirely when someone moves on.

A fourth problem is specific to working with AI assistants: a knowledge base large enough to be useful
is too large to load. Supplying everything crowds out the work itself, so the assistant spends its
capacity holding the filing cabinet rather than doing the task.

### What today costs

| Cost | How it shows up |
|---|---|
| **Re-explanation** | Context re-supplied at the start of every session |
| **Repeated decisions** | The same question re-analysed because the earlier answer's reasoning is gone |
| **Contradiction** | A new decision quietly reverses an older one nobody remembered |
| **Lost rationale** | The choice survives; *why* it was chosen does not |
| **Onboarding a repository** | Returning to an untouched project means rebuilding context from source |

---

## 3. Business objectives

| # | Objective | Why it matters |
|---|---|---|
| **BO-1** | Preserve the reasoning behind decisions, not just the decisions | The conclusion is usually recoverable from artefacts; the reasoning is not |
| **BO-2** | Remove the recurring cost of re-establishing context | The most frequent and most quantifiable waste |
| **BO-3** | Make knowledge findable the way work is organised | Recall must follow tickets, repositories and initiatives, not a separate filing scheme |
| **BO-4** | Keep recalled knowledge trustworthy as it ages | A confidently wrong answer is worse than no answer |
| **BO-5** | Retain complete ownership and privacy of accumulated knowledge | The store holds commercially sensitive reasoning and is a personal asset |

---

## 4. Users and stakeholders

| Party | Interest |
|---|---|
| **Primary user — the practitioner** | Sole user and sole beneficiary. Captures and recalls knowledge during normal work |
| **The AI assistant** | Consumes the store on the practitioner's behalf; never a decision-maker |
| **Employer / clients (indirect)** | Benefit from faster, more consistent decisions. Do **not** have access — the store is personal |

**This is deliberately a single-user product.** A shared organisational knowledge base is a different
product with a different problem: it must respect existing access boundaries, and introducing an
intelligence layer over shared content tends to make pre-existing over-broad permissions easier to
exploit rather than creating new ones. Remaining single-user avoids that class of problem entirely
rather than solving it.

---

## 5. Scope

### In scope

- Capturing durable facts, decisions and reasoning arising from normal work
- Associating knowledge with tickets, repositories, initiatives and free-form labels
- Retrieval by association and by question, across repositories and over long gaps
- Recording where knowledge came from, and how confident it is
- Tracking what remains true and what has been superseded
- Preserving relationships between related decisions
- Operating entirely on the practitioner's own machine

### Out of scope

| Excluded | Reason |
|---|---|
| Multi-user or organisation-wide knowledge base | Different product; requires an access-control model this deliberately avoids |
| Replacing issue trackers or documentation | The store *references* work items; it does not own them |
| Autonomous action on stored knowledge | A wrong answer causes confusion; a wrong action causes an incident. Acting requires approval, audit and rollback mechanisms not in this scope |
| Hosted or cloud-synchronised storage | Contradicts the ownership and privacy objective |
| Ingesting existing historical material | Value accrues from forward capture; back-filling is a separate initiative |

---

## 6. Business requirements

### Capture

**BR-01 — Capture must be a byproduct of work, never a separate task.**
This is the single most important requirement. Every prior attempt at durable knowledge — in this
research and in the wider practice it drew on — failed because maintaining it was separate from doing
the work. Any design that requires scheduled upkeep will be abandoned regardless of quality.
*Accepted when:* a working session leaves the store current without dedicated maintenance time.

**BR-02 — Capture must not interrupt the work that produces it.**
Knowledge is recorded at a natural checkpoint, not through continuous prompting.
*Accepted when:* the practitioner is not interrupted mid-task to record knowledge.

**BR-03 — Sensitive material must not be captured.**
Work produces credentials and secrets incidentally. Once stored, such material is difficult to remove.
Prevention at capture is required; removal after the fact is not sufficient.
*Accepted when:* detected sensitive content is removed before storage, and the fact is still captured.

### Retrieval

**BR-04 — Knowledge must be retrievable by the way work is organised.**
Association with tickets, repositories, initiatives and labels, not only by text search.
*Accepted when:* a practitioner can recall everything related to a work item, a repository, or a topic.

**BR-05 — Knowledge must be retrievable across repositories and projects.**
A decision made in one context frequently matters in another.
*Accepted when:* recall is not limited to the repository in which knowledge was captured.

**BR-06 — Recall must not overwhelm the task it supports.**
Retrieval returns what is relevant, not everything that matched.
*Accepted when:* recall returns a small, relevant, attributed set rather than bulk content.

### Trust

**BR-07 — Every recalled claim must be explainable.**
Where it came from, when it was recorded, and how confident it is. An unattributed claim cannot be
judged, only believed.
*Accepted when:* every recalled item carries its source and confidence.

**BR-08 — Superseded knowledge must remain readable, with its reasoning.**
Knowing that a decision was reversed is useful; knowing *why* is frequently more valuable than the
current answer.
*Accepted when:* an earlier position and its reasoning remain retrievable after being superseded.

**BR-09 — Stale knowledge must be distinguishable from current knowledge.**
Something may have been correct when recorded and false now. Confidence does not imply accuracy, and
knowledge that was once right is the most dangerous kind of wrong.
*Accepted when:* recall can exclude knowledge no longer true, and can distinguish a correction to a
record from a change in the world.

**BR-10 — Conflicting knowledge must be surfaced, never silently resolved.**
When two recorded positions disagree, the disagreement is itself information. Automatically choosing
one hides it.
*Accepted when:* a genuine conflict is presented for a decision rather than resolved automatically.

**BR-11 — Relationships between decisions must be preserved.**
Finding related items individually is useful; understanding how they connect is far more valuable. A
measurement that produced a finding, which justified a decision, is a chain — and the chain is the
answer to "why is this so?"
*Accepted when:* related knowledge is connected, with the connection's reason recorded.

**BR-12 — Recalled knowledge must be presented as evidence, not instruction.**
Stored knowledge is fed back to an assistant, where an incorrect item can be followed as a directive.
*Accepted when:* recalled items are presented as attributed claims to evaluate, not commands to obey.

### Integrity

**BR-13 — Knowledge must never be silently lost or overwritten.**
*Accepted when:* no operation destroys knowledge without reporting it, and prior states remain
retrievable.

**BR-14 — Every change to the store must be reportable.**
The practitioner can see what was recorded, changed, connected or rejected, without inspecting the
store directly.
*Accepted when:* every capture produces a summary of what changed.

**BR-15 — Gaps must be visible.**
When the store does not know something, it must say so rather than answering from weaker material. An
admitted gap becomes a question, and the answer becomes knowledge — which is how the store grows
where it is weakest.
*Accepted when:* an absent answer is reported as absent, and surfaces as a question.

### Ownership

**BR-16 — The store must remain under the practitioner's sole control.**
Local operation, no third-party custody, functional without network access.
*Accepted when:* the application runs and is fully usable offline, with data held only on the
practitioner's machine.

**BR-17 — The practitioner must be able to read the store without specialist tooling.**
An asset that can only be read through the application that wrote it is a dependency, not an asset.
*Accepted when:* the store's contents can be read in a human-readable form.

---

## 7. Success measures

| Measure | Target |
|---|---|
| **Maintenance effort** | Zero dedicated time. Any recurring upkeep task indicates failure of BR-01 |
| **Recall usefulness** | Recall after a long gap returns the reasoning, not only the conclusion |
| **Re-explanation** | Measurable reduction in context re-supplied at session start |
| **Traceability** | Every recalled claim carries a source |
| **Contradiction** | Decisions reversing earlier ones are surfaced, not discovered later |
| **Trust** | The practitioner consults the store by preference rather than reconstructing from source |

The last is the real measure. A knowledge base that exists but is not consulted has failed, however
complete it is.

---

## 8. Constraints and assumptions

### Constraints

| Constraint | Implication |
|---|---|
| Single practitioner, single machine | No multi-user design, no access-control model |
| Operates offline | The store and its data path are fully local; capture-time summarisation uses the assistant's model and degrades to unsummarised + backfill when unavailable |
| Capture happens through the AI assistant | The assistant's judgement determines capture quality |
| Sensitive commercial reasoning | Privacy is a requirement, not a preference |

### Assumptions

- The practitioner works predominantly through an AI assistant, so capture has a natural host.
- Knowledge volume grows to hundreds or low thousands of items per year — substantial for a person, small for a system.
- Work is organised around tickets and repositories, so those are meaningful retrieval keys.
- The practitioner is willing to answer occasional clarifying questions at a checkpoint, since those answers are themselves valuable knowledge.

---

## 9. Risks

| Risk | Impact | Mitigation |
|---|---|---|
| **Capture quality depends on judgement** | Poor capture produces a store of noise | Rules for what constitutes one durable fact; every capture reported for review |
| **The store is not trusted** | Abandonment — the failure mode of every prior attempt | Attribution, staleness handling and visible gaps, so its limits are knowable |
| **Sensitive material is captured** | Difficult to remove once stored | Prevention at capture (BR-03), not remediation |
| **Recalled knowledge is followed as instruction** | An incorrect item propagates into future work | Presentation as evidence, not directive (BR-12) |
| **Value accrues slowly** | Thin and unconvincing early, when habits form | Accept deliberately: capture cost is near zero, so an early store costs little to maintain |
| **Knowledge is unreadable without the application** | The asset becomes a dependency | Human-readable export (BR-17) |

---

## 10. Glossary

| Term | Meaning |
|---|---|
| **Memory** | One durable fact, decision or piece of reasoning worth recalling later |
| **Capture** | Recording knowledge arising from work |
| **Recall** | Retrieving relevant knowledge when it is needed |
| **Label** | A metadata tag associating a memory with the work it came from — ticket, repository, initiative or free-form |
| **Link** | A held relationship between two memories, such as one superseding or following from another |
| **Cross-product recall** | Retrieval that spans every product and repository in the store, not only the one in hand |
| **Provenance** | Where a piece of knowledge came from and when |
| **Superseded** | Knowledge replaced by a later position, retained for its reasoning |
| **Stale** | Knowledge that was correct when recorded and is no longer true |
| **Checkpoint** | The natural pause at which capture occurs |

---

## 11. Related documents

| Document | Covers |
|---|---|
| [HLD 001 — Context memory storage](../../hlds/001-context-memory-storage/) | How knowledge is stored and retrieved |
| [HLD 002 — Context memory write pipeline](../../hlds/002-context-memory-write-pipeline/) | How capture works and what judgement it applies |
| [HLD 003 — Graph edges on Apache AGE](../../hlds/003-graph-edges-on-age/) | How relationships between decisions are held |
