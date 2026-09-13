# NFR-01: Confidentiality

**Status:** Draft

## Requirement

No feedback record contains memory content or query text. Specifically:

- **No subject, claim, summary or body** of any memory.
- **No query text**, in any form — not raw, not truncated, not hashed.
- **Retrieval-shape classification is a bounded category**, never free text. Free text is where content re-enters.
- Feedback is subject to the same prohibition as logs, spans and metric labels: **content never appears**.

## Verification

- Inspect the feedback record's fields; assert every one is an identifier, a bounded category, a count or a timestamp.
- Perform a retrieval using a recognisable phrase, then assert the phrase appears in **no** feedback record.
- Repeat for a retrieval that **returns nothing** — the miss path is the case most likely to be tempted into recording the query, because it is the one someone will later want to diagnose.
- Assert any classification field rejects a value outside its defined set, so it cannot become free text by accident.

## Acceptance Criteria

- Every field is identifier, bounded category, count or timestamp.
- A recognisable query phrase appears in no record, on either the hit or the miss path.
- Classification values are constrained, not conventional.
- If diagnosing a specific past miss proves impossible without content, that limitation is **accepted and recorded** — not solved by relaxing this NFR.

## Applies To

Core Separation of Concerns; LADR-03. Extends the storage design's confidentiality requirement to a
new surface that would otherwise sit outside it.
