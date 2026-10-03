# Knowledge workflow contract

The optional Knowledge library owns bounded context and capture workflows; it has no connection to the business database. KnowledgeHost supplies HTTP/provider/secret-filter adapters. Keep all corpus writes behind `ICoreCommitter` and all provider work behind persisted `BudgetLedger` reservations.

Capture acknowledgement follows sanitized SQLite commit. Persist the exact pending core request before dispatch; operation-key replay is safe only with the original payload. Stop automatic replay on corpus epoch or workflow version mismatch. A proposal cannot replace approved current behavior; explicit attributed authority establishes approved intent separately from implementation.

Preserve the absolute processing deadline, spent budget and outstanding reservations across restart, clarification and restore. Only explicit additional allowance extends them. Restore reconciliation pins both epochs, archives previous plans/receipts and creates one idempotent generation before fresh read/compare work; it never replays the obsolete pending payload. Keep its public recovery metadata usable without core credentials.

Evidence attachment creates a new immutable core version, retaining existing claim metadata. Coalesce equivalent facets into one target write while preserving each source's own evidence metadata. Document approval is separate from product intent and observed implementation; open owner capture/design decisions stay open. Namespaced source references are an additive service convention.

Python `redact.py` is packaged verbatim from the direct skill. Pass content through stdin, enforce process timeout, discard stderr, and fail closed. Sanitize ingress, retrieved evidence and generated outputs before persistence or provider dispatch.

Package the canonical direct-mode `atomicity.py` too. Apply the same detector before and after any bounded extraction repair. Shared fixtures must keep necessary conditions and exceptions together while rejecting genuinely independent bundled claims. Persist all returned claims and comparison progress; commit a bounded plan atomically after comparison, with explicit unexamined material and continuation allowances.

| Date | Change |
|---|---|
| 2026-10-03 | Added optional bounded HTTP-only orchestration, durable capture journal and budget reservations. |
