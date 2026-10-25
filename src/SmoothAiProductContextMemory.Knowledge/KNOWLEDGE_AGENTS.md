# Knowledge workflow contract

The optional Knowledge library owns bounded context and capture workflows; it has no connection to the business database. KnowledgeHost supplies HTTP/provider/secret-filter adapters. Keep all corpus writes behind `ICoreCommitter` and all provider work behind persisted `BudgetLedger` reservations.

Capture acknowledgement follows sanitized SQLite commit. Persist the exact pending core request before dispatch; operation-key replay is safe only with the original payload. Stop automatic replay on corpus epoch or workflow version mismatch. A proposal cannot replace approved current behavior; explicit attributed authority establishes approved intent separately from implementation.

Preserve the absolute processing deadline, spent budget and outstanding reservations across restart, clarification and restore. Only explicit additional allowance extends them. Restore reconciliation pins both epochs, archives previous plans/receipts and creates one idempotent generation before fresh read/compare work; it never replays the obsolete pending payload. Keep its public recovery metadata usable without core credentials.

Evidence attachment creates a new immutable core version, retaining existing claim metadata. Coalesce equivalent facets into one target write while preserving each source's own evidence metadata. Document approval is separate from product intent and observed implementation; open owner capture/design decisions stay open. Namespaced source references are an additive service convention.

Python `redact.py` is packaged verbatim from the direct skill. Pass content through stdin, enforce process timeout, discard stderr, and fail closed. Sanitize ingress, retrieved evidence and generated outputs before persistence or provider dispatch.

Package the canonical direct-mode `atomicity.py` too. Apply the same detector before and after any bounded extraction repair. Shared fixtures must keep necessary conditions and exceptions together while rejecting genuinely independent bundled claims. Persist all returned claims and comparison progress; commit each completed bounded comparison prefix atomically before a limit pauses further comparisons, with explicit unexamined material and continuation allowances.

| Date | Change |
|---|---|
| 2026-10-03 | Added optional bounded HTTP-only orchestration, durable capture journal and budget reservations. |

| 2026-10-24 | Natural-language authority is assessed against the full attributed conversation, without approval phrase matching. Missing or unsupported authority retains an explicitly unconfirmed proposed claim and re-compares it; it does not abort unrelated learning. Exact full-message provenance remains required for promotion. |

Ordinary facts, explanations, preferences and attributed findings use reported_fact and require no authority fields. Fully examined messages with no reusable knowledge use noLearningSourceIds so an empty extraction can complete without writes or questions. The service owns novelty comparison; callers send context at meaningful checkpoints, not preclassified records.

| 2026-10-24 | Capture defaults to 40 provider calls with the existing $2/120-second ceilings. Budget/deadline pauses commit completed comparisons, persist committed/pending plan checkpoints, and expose remainingClaims. Comparison reserves ten seconds for persistence; previews never write. Continuations preserve spent allowance and never renew it automatically. Re-read corpus revision on resume while retaining the committed prefix. |
