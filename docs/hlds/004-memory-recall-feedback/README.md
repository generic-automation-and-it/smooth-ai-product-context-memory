# Memory recall feedback — High-Level Design

| | |
|---|---|
| **Status** | In Discovery |
| **Owner** | generik0 |
| **Tracker** | Context-memory tuning |
| **Last updated** | 2026-09-16 |

> Discovery / prototyping HLD. Delivers **intent + spec** — what we are building and why, the decisions
> behind it, and the quality bar it must meet. No implementation plan; execution is tracked in the
> issue/work tracker.

## Intent

The store records what it was told and nothing about whether that was useful. There is no signal for
which memories are recalled, which are never returned, or whether a retrieval found what it was for.

This matters now because the system is entering daily use for **tuning**. Stemming was adopted and
trigram rejected on measured evidence (HLD-001 NFR-02 recall-tuning measurements); the recorded
reopening threshold for trigram waits on exactly this feedback data. **Dogfooding is that
measurement**, and without feedback it produces impressions rather than evidence.

The asymmetry is the point: capture is instrumented by its digest, so we know exactly what went in.
Retrieval reports nothing, so we know nothing about what comes out.

## Key Goals

### 1. Identify knowledge that is never recalled

A memory returned by no retrieval is either badly captured, badly classified, or genuinely not needed.
All three are useful to know and none is currently observable.

This is the cheapest quality signal available. Capture judgement — atomicity, subject phrasing, facet
choice — is the hardest thing in the system to assess, and "never surfaced" is a proxy for it that
needs no human review.

**Acceptance criteria / DoD**

- For any memory, it can be established whether it has ever been returned by a retrieval.
- Memories never returned can be listed, so capture patterns can be examined rather than guessed at.
- A recently captured memory is distinguishable from a long-lived one never recalled.

### 2. Distinguish a miss from an absence

A retrieval returning nothing means either the store does not hold the answer, or it holds it and
recall failed to surface it. These demand opposite responses — capture more, or tune recall — and are
currently indistinguishable.

An honest miss is a signal that should feed a question loop — proposed here; the current design
does not yet define that loop. That signal currently evaporates the moment it is rendered.

**Acceptance criteria / DoD**

- Retrievals returning nothing are recorded as such.
- A miss can be reviewed later with enough context to judge whether the store should have answered.
- Misses are countable over time, so a tuning change can be shown to reduce them.

### 3. Turn deferred decisions into evidenced ones

Several decisions were deferred pending measurement. This design supplies the measurement.

**Acceptance criteria / DoD**

- Recall effectiveness is comparable before and after a tuning change.
- The evidence is sufficient to decide the deferred recall-quality items, or to defer them again with a stated reason.

## Core Separation of Concerns

> Feedback records **that** a memory was recalled, never **what** it said.

Recall feedback is metadata about findability. It carries identity, outcome and time — never subject,
claim, summary or query content.

This is not a precaution bolted on. The store exists to hold commercially sensitive reasoning, and its
confidentiality requirement forbids content in any log, span attribute or metric label. Feedback is a
new surface with the same exposure, and it would be a poor trade to gain tuning signal by creating a
side channel around a constraint the rest of the design honours.

## Guiding Principle — Observation must not change the thing observed

> Reading is a read. Recording that a read happened must not make it a write in any way the user feels.

- Feedback is **classification, not a claim** — it describes findability, so it does not version and does not enter the version chain.
- Feedback is **additive and disposable**. Losing it costs a tuning signal, never knowledge.
- We will deliberately **not** let feedback influence ranking, at least initially. A store that surfaces what it has surfaced before is self-reinforcing, and that failure would be slow and hard to detect.
- We will deliberately **not** build dashboards or alerting. This produces evidence for occasional questions, not a monitoring product.

---

## Diagrams

- [System Context (C1) and the recall path with its feedback point](./diagrams/c4-context.md)

## Architecture Decisions (LADRs)

LADRs 01–03 are strategic; 04 is tactical. See [`./ladrs/`](./ladrs/). LADR-02 is resolved on measured
evidence; the rest remain Draft pending implementation.

| LADR | Decision | Status |
|------|----------|--------|
| [LADR-01](./ladrs/LADR-01-record-recall-outcomes.md) | Record recall outcomes, including misses | Draft |
| [LADR-02](./ladrs/LADR-02-feedback-placement.md) | Feedback lives in append-only records | Accepted |
| [LADR-03](./ladrs/LADR-03-identity-not-content.md) | Feedback carries identity and outcome only | Draft |
| [LADR-04](./ladrs/LADR-04-unversioned-and-disposable.md) | Feedback is unversioned and disposable | Draft |

## Non-Functional Requirements

See [`./nfrs/`](./nfrs/).

| NFR | Attribute | Target (summary) | Status |
|-----|-----------|------------------|--------|
| [NFR-01](./nfrs/NFR-01-confidentiality.md) | Confidentiality | No content, no query text, in any feedback record | Draft |
| [NFR-02](./nfrs/NFR-02-read-path-cost.md) | Performance | Retrieval latency unchanged; no contention on hot rows | Draft — [placement measured](./nfrs/NFR-02-placement-evidence-2026-09-18.md) |
| [NFR-03](./nfrs/NFR-03-actionability.md) | Actionability | Answers the three tuning questions or it is not worth building | Draft |

NFR-02 stays Draft deliberately: the measured evidence settled which placement to build, not that the
shipped write path meets the target. All three are verified against the implementation, not the prototype.
