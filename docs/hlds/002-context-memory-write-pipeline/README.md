# Context memory write pipeline — High-Level Design

| | |
|---|---|
| **Status** | Accepted — implemented |
| **Owner** | generik0 |
| **Tracker** | Context-memory MVP |
| **Last updated** | 2026-09-14 |

> Converted from ADR-0003. This HLD delivers **intent + spec** — the contract the agent-facing skill
> executes and the judgement it owns. Execution is tracked in the issue/work tracker.

## Intent

The storage layer is merged and constraint-tested, but **every judgement the design depends on lives
above it**. Two constraints are soft by necessity, and three behaviours — semantic deduplication, link
derivation and redaction — cannot be expressed as database constraints at all. Until this contract
exists, the store is a schema nobody can safely write to.

This design specifies the **write pipeline**: an ordered sequence of stages the skill executes, the
judgement it owns at each, and the receipt it renders afterwards. It also fixes what the API owns, so
mechanics and judgement never blur.

## Key Goals

### 1. A secret never reaches an immutable object

Captured content may contain tokens, connection strings and keys. Bodies are content-addressed, so a
stored object is immutable and its address is a function of its content: a leaked secret cannot be
edited out, only orphaned — leaving an address that no longer resolves to wanted content.

Mitigation must therefore be **preventive, not corrective**, and must run before the body is written.

**Acceptance criteria / DoD**

- Detection runs before any content leaves for storage.
- A detected secret is scrubbed; the fact itself is still captured.
- The digest records that redaction fired and on which candidate — never the matched span, never the content.

### 2. The same subject recognised across different wording

*"PostgreSQL is the storage engine"* and *"we store in Postgres"* are the same subject. The database
offers only an exact-match backstop, so recognising them is the skill's job.

This was independently identified as the **top risk in the project** by two separate simulation
trials. A wrong call is expensive in both directions and silent in both: a missed match inserts a
near-duplicate that dilutes every future retrieval, and a false match rewrites canon under a subject
that never changed.

**Acceptance criteria / DoD**

- A semantically equivalent, surface-different candidate becomes a version, not a second memory.
- A related-but-distinct candidate becomes a new memory, not a version.
- Matching runs **across groups**, since the same subject legitimately arises under different work items.
- Accuracy is measured as recall *and* precision against authored pairs, not asserted.

### 3. Relationships get created at all

Nothing else creates links, so without a named stage the relationship model stays permanently empty
and its requirement is decorative. Three simulation trials produced **zero links** without anyone
noticing — which is itself the evidence that this needs an owner rather than good intentions.

**Acceptance criteria / DoD**

- Link derivation is a named stage with a fixed position, not a side effect.
- Derived links appear in the digest; none is created silently.
- Every link carries a reason, structurally enforced by the schema.

### 4. Nothing mutates silently

Every write returns a digest: created, versioned, linked, diverged, skipped and labels proposed, each
with a count. A human can audit what happened without reading the store.

**Acceptance criteria / DoD**

- Every write path renders a digest; a silent mutation is a defect.
- The skipped count distinguishes its causes rather than collapsing them.
- A pre-write inspection mode runs the identical pipeline and renders the identical digest while persisting nothing.

## Core Separation of Concerns

> The skill owns judgement. The API owns mechanics.

The skill decides what a fact is about, whether it is the same subject as an existing one, whether it
is one fact or several, what it relates to, and what must be scrubbed. The API decides nothing: it
enforces version-bump ordering and its transaction, owns the current-version flag, proxies bodies with
scope enforcement, and rejects self-links.

The split matters because the two fail differently. A mechanical failure is loud and reproducible; a
judgement failure is silent and data-dependent. Keeping them apart means a bug in one is not mistaken
for the other.

## Guiding Principle — Automate what is reversible; ask about what is not

> Recording a fact is reversible. Making it citable canon is not.

- Capture is **additive** — a fact is recorded even when imperfect, because losing it costs more than storing it imperfectly.
- Gating governs **status**, not persistence. Withholding a write until approval loses the fact if the session ends first — and the checkpoint *is* the moment the session is ending.
- Repetition of the atomicity and matching rules across phases is **deliberate reinforcement**, not redundancy. All three trials demonstrated the drift it counters.
- We will deliberately **not** trust the store simply because it is local. Local is not the same as trusted.

---

## Diagrams

- [System Context (C1) and the write pipeline (sequence)](./diagrams/c4-context.md)

## Architecture Decisions (LADRs)

LADRs 01–05 are strategic; 06–07 are tactical. See [`./ladrs/`](./ladrs/).

| LADR | Decision | Status |
|------|----------|--------|
| [LADR-01](./ladrs/LADR-01-five-stage-pipeline.md) | Five stages in a fixed order, specified together | Accepted |
| [LADR-02](./ladrs/LADR-02-redaction-precedes-blob-write.md) | Redaction precedes the body write | Accepted |
| [LADR-03](./ladrs/LADR-03-redact-and-flag.md) | Redact and flag, never reject | Accepted |
| [LADR-04](./ladrs/LADR-04-semantic-dedup-is-skill-owned.md) | Semantic deduplication belongs to the skill | Accepted |
| [LADR-05](./ladrs/LADR-05-link-derivation-batched.md) | Link derivation batched into the pre-write round | Accepted |
| [LADR-06](./ladrs/LADR-06-gate-status-not-persistence.md) | Approval gates status, not persistence | Accepted |
| [LADR-07](./ladrs/LADR-07-digest-is-a-receipt.md) | The digest is a receipt; dry-run is the veto | Accepted |

## Non-Functional Requirements

See [`./nfrs/`](./nfrs/).

| NFR | Attribute | Target (summary) | Status |
|-----|-----------|------------------|--------|
| [NFR-01](./nfrs/NFR-01-secret-containment.md) | Security | No detected secret reaches storage; content never logged | Accepted |
| [NFR-02](./nfrs/NFR-02-deduplication-accuracy.md) | Correctness | Measured recall *and* precision against authored pairs | Accepted |
| [NFR-03](./nfrs/NFR-03-auditability.md) | Auditability | Every mutation appears in a digest | Accepted |
| [NFR-04](./nfrs/NFR-04-poisoning-resistance.md) | Security | Retrieved memories render as quoted data, never instructions | Accepted |
| [NFR-05](./nfrs/NFR-05-cost.md) | Cost | Per-write model cost stated and bounded; expensive paths opt-in | Draft |
