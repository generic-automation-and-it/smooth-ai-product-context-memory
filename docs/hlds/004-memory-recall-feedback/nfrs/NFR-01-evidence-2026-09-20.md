# NFR-01 evidence: confidentiality against the shipped path

**Date:** 2026-09-20
**Status:** Measured — passed. No feedback record carries memory content or query text, on either the hit or the
miss path, and the classification field is schema-constrained.
**Command:** `SMOOTH_NFR_BENCH=1 dotnet test tests/SmoothAiProductContextMemory.Infrastructure.ComponentTest --filter NfrEvidenceTests`
**Environment:** Aspire test fixture, `docker.io/apache/age:release_PG17_1.7.0`, isolated per-test database, macOS/arm64 dev host.
**Harness:** [`NfrEvidenceTests`](../../../../tests/SmoothAiProductContextMemory.Infrastructure.ComponentTest/Persistence/NfrEvidenceTests.cs)

## Question

LADR-03 requires that feedback carries identity and outcome only — never subject, claim, summary or query
text, in any form. NFR-01 asks whether the shipped `QueryMemories.Handler` → `NpgsqlRecallFeedback` path
actually honours that, on the hit *and* the miss path, and whether the `shape` field can become free text.

## Method

Seeded a memory whose subject is the recognisable phrase **"acquisition pricing"** (a noun phrase that would
be the natural thing to record when diagnosing a miss), then drove the real `QueryMemories.Handler` twice
against an isolated database:

- a **hit** retrieval on that phrase (returns the sensitive-phrase memory);
- a **miss** retrieval on a nonsense query (returns nothing — the case most likely to be tempted into
  recording the query, since it is the one someone later wants to diagnose).

Then asserted, against the `recall_feedback` table via raw SQL:

- the phrase string appears in **no** record, in **any** field (hit or miss);
- every column is a uuid / bounded category / timestamp (field-type inventory);
- an out-of-set classification value is rejected by the `ck_recall_feedback_shape` CHECK constraint
  (SQL state `23514`).

## Results

- Hit retrieval on "acquisition pricing" returned 1 row; miss retrieval returned 0 rows.
- **The phrase appears in no feedback record** (any field, hit or miss): clean.
- Every feedback column is `uuid` / bounded `text` / `timestamp with time zone`:

  ```
  retrieval_id uuid, memory_uuid uuid, shape text, occurred_on timestamp with time zone
  ```

  No free text: `shape` is a bounded category, `retrieval_id`/`memory_uuid` are identifiers, `occurred_on` is
  a timestamp. There is no column that could hold content or query text.
- Out-of-set classification value "free text leaking content" is **rejected** by `ck_recall_feedback_shape`
  (`23514`), so the classification is constrained by the schema, not by convention.

## Decision

NFR-01 is **met** and moves from Draft to **Accepted**: no feedback record carries memory content or query
text on either path, and the classification cannot become free text by accident.

## Accepted limitation

Diagnosing a *specific* past miss without knowing its query text is not possible from the feedback records
alone — that limitation is accepted and recorded, per NFR-01's own criterion, rather than solved by relaxing
the confidentiality requirement. (Logging is asserted-only for content — NFR-05 — so content also never
reaches a log line.)

## Applies To

Core Separation of Concerns; LADR-03. This is the confidentiality requirement of the storage design, now
verified against the shipped feedback surface rather than the prototype.
