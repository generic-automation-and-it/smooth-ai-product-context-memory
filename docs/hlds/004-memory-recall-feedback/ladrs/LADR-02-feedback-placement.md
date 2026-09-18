# LADR-02: Feedback lives in append-only records

**Status:** Accepted — resolved on measurement, 2026-09-18

## Context

Recall feedback has to be written somewhere, and the obvious answer — a counter on the memory row — is
the one most likely to be wrong.

Retrieval is a read. A counter column turns every read into a write: rows are dirtied, a frequently
recalled memory becomes a contention point, and the read path acquires write-path failure modes. The
design has been explicit that constraints and mechanisms should not be added casually, and this would
add one on the hottest path in the system.

Three placements were viable and none obviously correct, so this decision was deliberately deferred with
the three candidates recorded. It is now resolved against prototypes measured at volume rather than by
argument; the measurements are in
[NFR-02 placement evidence](../nfrs/NFR-02-placement-evidence-2026-09-18.md).

**A — Counter on the memory row.** Simplest to query; "never recalled" is a predicate. But every read
becomes a write, it contends on popular rows, and it discards history — a count cannot distinguish
recalled-once-last-year from recalled-weekly.

**B — Append-only event records.** Reads still write, but appends do not contend and history is
preserved, so recency and frequency both become answerable. Costs a growing record set needing
retention, and "never recalled" becomes an anti-join rather than a predicate.

**C — Derive from telemetry.** A recall emits a span carrying memory identities, and "never recalled" is
derived by comparing against the store. **No store writes at all**, which fully satisfies the read-path
concern. But telemetry is ephemeral and capacity-bounded, correlating identities across systems is
awkward, and it makes a tuning signal depend on a debugging tool's retention.

## Decision

**Feedback lives in append-only records — option B.** A is eliminated on measurement and on capability;
C is rejected on the trade it asks for.

**A fails NFR-02 as measured.** A second reader of the same memory blocks on the first
(`55P03 lock_not_available` under a 250 ms `lock_timeout`), and 200 recalls of one memory move its `ctid`
from `(83,51)` to `(83,201)` — every read rewrites the store's hottest row. This is the case NFR-02
nominated as the discriminator, and the counter placement fails it as a property rather than under load.

**A also cannot hold the signal.** It cannot answer recency, which was named as the deciding question.
More decisively, and not named when the candidates were first recorded: **a counter cannot record a miss
at all.** It lives on a memory row, and a retrieval that returned nothing has no memory row to count
against. LADR-01 requires both halves of the outcome, so A cannot implement the decision it exists to
serve.

**C is viable and still rejected.** Its advantage over B is one it cannot demonstrate: recording a
50-memory retrieval costs ~1 ms p95 against a ~7 ms retrieval, a difference smaller than the spread
between repeat runs. For that unmeasurable saving it would make a tuning signal depend on an
observability retention policy owned elsewhere, and move the never-recalled comparison outside the store.

Latency did not decide this. All three placements measured within ~1.2 ms p95 of each other, which is why
the decision rests on the serialisation probe, the row rewrite, and what each mechanism can physically
represent.

### Record shape

A record carries an identifier, a bounded category or a timestamp, and nothing else (LADR-03):

| Field | Kind | Why |
|---|---|---|
| `retrieval_id` | generated identifier | One retrieval returning fifty memories writes fifty records. Without a shared identifier those fifty are indistinguishable from fifty retrievals, and NFR-03's second question is unanswerable. Generated, so it carries no content. |
| `memory_uuid` | identifier, nullable | The memory returned. **Null on the miss path** — a retrieval that returned nothing has no identity to record. |
| `shape` | bounded category | The retrieval shape, constrained by the schema rather than by convention, so it cannot become free text by accident (NFR-01). The category set itself is LADR-03's open item. |
| `occurred_on` | timestamp | When the retrieval happened. Answers recency, and bounds retention. |

No subject, claim, summary or query text, in any form including hashes.

"Never recalled" is an anti-join against this set, excluding the recently captured. Capture age is **not**
a column on the memory row — the stable row carries no timestamp, so it comes from
`min(memory_version.created_on)`.

### Retention bound

182 bytes per record including indexes, so one 50-memory retrieval writes ~8.9 KiB.

**Retained for 30 days, or 2,000,000 records, whichever is reached first.** Both limits are load-bearing:
at dogfooding volume (~200 retrievals/day, ~52 MiB) the time bound governs, and at heavy volume
(~2,000/day) the record cap bites first and holds the set under ~350 MiB. This is a bound, not a policy —
old recall data describes a store and a recall implementation that no longer exist.

### What this placement must not become

- **Feedback does not influence ranking.** Retrieval must not read the feedback set. A store that
  surfaces what it has surfaced before is self-reinforcing, and that failure is slow and hard to detect.
- **Feedback does not version and is not covered by the append-only guarantee** (LADR-04). Measured
  rather than assumed: `append_only_guard` fires only on `memory_version` and `group_description`, and the
  only trigger on `memory` is `trg_memory_graph_cascade` (`BEFORE DELETE`). A separate table introduces no
  coupling to either.
- **Feedback does not survive a restore.** It is excluded from backup and restore, which keeps it out of
  the cross-store consistency problem and out of the HLD-006 snapshot concern entirely.
- **A feedback failure never fails a retrieval.** Verified: an injected write failure leaves the
  retrieval returning byte-identical results in the same order.

## Alternatives Considered

A and C above, both rejected on the recorded evidence. A fourth — writing feedback to the object store —
was dismissed immediately: content addressing makes objects immutable, which is precisely wrong for an
accumulating signal.

## Consequences

- The store owns its own usage data, with no dependency on a retention policy owned elsewhere.
- "Never recalled" is an anti-join, and the capture-age exclusion reaches the version table. Expressed as
  a correlated subquery it dominates the query's cost (25,414 of 25,485 in the measured plan), so it
  belongs as a join.
- The record set grows and needs the bound above. A bound is cheap; the alternative is a second store to
  reason about.
- The feedback table is deliberately outside the six entity types the DbContext exposes and the model-shape
  guard asserts. Whether it becomes an EF entity — and therefore a guard change — is a decision the write
  path must make deliberately rather than by adding a `DbSet` and following the compiler.

## Open

- The retrieval-shape category set (LADR-03's open item) — the prototype used five values only to prove
  the schema constraint rejects a sixth.
- Whether a miss is recorded for every empty result or only where the caller signalled an answer was
  expected (LADR-01's open item). Unaffected by this decision: both record identically.

## Related

- **LADR-01** — the decision this implements.
- **LADR-03** — what a record may contain; the correlation identifier added here stays inside it.
- **LADR-04** — unversioned and disposable, which this placement keeps true.
- **NFR-02** — the constraint that decided it, and [its evidence](../nfrs/NFR-02-placement-evidence-2026-09-18.md).
