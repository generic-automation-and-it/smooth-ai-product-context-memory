---
name: mimisbrunnr-recall-feedback
description: Run the three recall-feedback tuning queries against the context-memory store — never-recalled list, miss rate over a window, and baseline reset. Use when a practitioner needs to see which memories are never recalled, measure how often retrieval returns nothing, or reset the feedback baseline before a tuning experiment. Read-only insight into retrieval health. Triggers on "never recalled", "miss rate", "recall feedback", "which memories are never returned".
models:
  claude: sonnet
  copilot: auto
  codex: gpt-5.4
---

# Recall Feedback

Thin helper for the three HLD-004 NFR-03 tuning questions. Calls the Host API — no direct database
access, no raw feedback records. Output is identity / count / time only; content and query text never
leave the store.

Base URL: `CONTEXT_MEMORY_BASE_URL`, fallback `http://localhost:5141`. Loopback origins only.

API access requires runtime credentials: `CONTEXT_MEMORY_READ_TOKEN` for the two read queries and
`CONTEXT_MEMORY_WRITE_TOKEN` for reset (write includes read). The **values** are runtime-only — never
written to a file, prompt or commit. Each request carries the token as a `Bearer` Authorization header.
A missing or wrong token returns `403`.

## Queries

### 1. Which memories have never been recalled?

```bash
curl -s -H "Authorization: Bearer $CONTEXT_MEMORY_READ_TOKEN" \
  "$BASE/api/context/recall-feedback/never-recalled?asOf=2026-09-18&limit=500"
```

`asOf` is required (ISO 8601). The list already excludes memories captured within the recency grace
(it distinguishes "never recalled" from "recently captured and not yet retrieved"), so a new memory does
not pollute the signal.

### 2. How often does retrieval return nothing?

```bash
curl -s -H "Authorization: Bearer $CONTEXT_MEMORY_READ_TOKEN" \
  "$BASE/api/context/recall-feedback/miss-rate?from=2026-09-11&to=2026-09-18"
```

Returns `{ "retrievals": N, "misses": M, "missRate": 0.0 }`. Count a window before and after a tuning
change to show whether a change moved it — this is the before/after comparison NFR-03 exists for.

### 3. Reset the baseline

```bash
curl -s -X POST -H "Authorization: Bearer $CONTEXT_MEMORY_WRITE_TOKEN" \
  "$BASE/api/context/recall-feedback/reset"
```

Legitimate, not destructive: feedback is disposable (LADR-04), and a tuning experiment must be able to
start from a clean baseline. Returns the number of records deleted.

## Posture

- These are **practitioner tuning** surfaces, consumed occasionally. Not a monitoring product — no
  dashboards, no alerting, no polling.
- Never call them from the retrieval path; feedback must not influence ranking.
- The output is identity and counts. Do not attempt to reconstruct query text or memory content from it.
