# Caller contract

The stdlib client runs on Windows and Linux with Python 3.9+. Configure `MIMI_KNOWLEDGE_URL` (default `http://127.0.0.1:5142`), `MIMI_KNOWLEDGE_READ_TOKEN`, and `MIMI_KNOWLEDGE_WRITE_TOKEN` at process launch. These correspond to service configuration `KnowledgeService__ReadToken` and `KnowledgeService__WriteToken`; they are service credentials, not provider credentials. An optional `MIMI_KNOWLEDGE_CREDENTIALS_FILE` selects a protected, non-symlink file containing only these `KEY=value` names. Environment values win. Unix file access must be mode 600; on Windows restrict its ACL to the account. The client reads the file internally and never sources shell code. HTTP redirects, endpoint userinfo and URL query credentials are refused. Non-loopback HTTP is refused; use an existing SSH tunnel to the host's loopback endpoint, consistent with the direct client's protected connection pattern.

`context` POSTs `/api/knowledge/context` with the read token:

```json
{"question":"Which refund exceptions apply?","selectors":{"repo":"synthetic/evaluation","ticketProvider":"eval","ticketKey":"CASE-1","scopeDimension":"product"},"historical":false,"includePortableUnderstandings":false,"briefTokens":2000}
```

Optional `asOf` is an ISO timestamp. Response: `requestId`, `brief`, `evidence` (record UUID/group UUID/version/source and applicability), `coverage`, `conflicts`, `stopReason`, `usage`, `effectiveAsOf`, `historical`, `workflowVersion`. Coverage outcomes are `supported`, `conflicting`, `not_found_in_search`, `unexamined_due_to_limit`. Usage reports service provider usage separately from caller context.

`capture` POSTs `/api/knowledge/captures` with the write token:

```json
{"idempotencyKey":"caller-stable-request-id","task":"Synthetic refund spec; full relevant transcript supplied","sourceNamespace":"task-1","messages":[{"id":"task-1/message-4","role":"user","order":4,"text":"We approved the change, but it has not shipped.","timestamp":"2026-10-03T12:00:00Z"}],"selectors":{"repo":"synthetic/evaluation","scopeDimension":"product"},"preview":false,"previousCursor":null}
```

With `--state` the client derives a stable idempotency key from task ID, payload and prior cursor, excludes already acknowledged message IDs, and atomically saves only receipt IDs, cursor and message IDs after durable acknowledgement. Source IDs must be immutable; a correction is a new message ID. On uncertain delivery retry the same payload and state. Keep the original payload available until a receipt is known; the state deliberately stores no conversation bodies.

`--task-id` automatically supplies optional `sourceNamespace`, with or without `--state`. It must be nonblank, at most 200 characters, and match an explicitly supplied namespace. Use the same task or conversation ID for incremental captures and retries; use a different namespace for another conversation even if it reuses local message IDs. The service generates durable source references as `message:{sourceFingerprint16}:{messageId}`. The fingerprint is the first 16 hexadecimal characters of SHA-256 over the namespace, a newline, and the service's canonical JSON serialization of the complete sanitized message (`id`, `role`, `order`, `text`, and `timestamp`). It therefore binds attribution and content as well as conversation identity; it is not a namespace-only hash. Treat the returned reference as opaque. This protection does not change the caller rule that corrections receive new message IDs. Without the optional field, the service derives a namespace from the root capture and preserves it through `previousCursor`. Existing acknowledged state files remain usable. An uncertain request from an older client must first be retried with its exact original JSON and key, without introducing this field; omission remains supported.

`receipt --capture-id UUID` GETs `/api/knowledge/captures/{id}` with the read token. Receipt: `id`, `status`, `cursor`, `changes`, `committed`, `deferred`, `questions`, `usage`, `errorClass`, `workflowVersion`. Terminal polling statuses: `processed`, `needs_input`, `partial`, `failed`; a bounded poll may return `received` or `processing` and must retain the ID.

`clarify --capture-id UUID` POSTs `/api/knowledge/captures/{id}/clarifications` with the write token:

```json
{"idempotencyKey":"task-1/clarification-1","messages":[{"id":"task-1/message-5","role":"user","order":5,"text":"This applies only to synthetic customer Elm."}]}
```

Optional `additionalBudget` has `calls`, `inputTokens`, `outputTokens`, `dollars`, `deadlineSeconds`. Continuation is an explicit new allowance; polling or retrying does not renew a budget. `--poll-seconds` accepts 0–60. Transport errors output classified metadata only, never server response bodies or credential-bearing URLs.

`reconcile --capture-id UUID` POSTs `/api/knowledge/captures/{id}/reconcile` with the write token after operator review of the receipt's `reconciliationRequired`:

```json
{"idempotencyKey":"task-1/restore-1","previousCorpusEpoch":"ce59eca2-aebb-4b34-b4bd-306386ca38d2","expectedCorpusEpoch":"68d4807b-4315-4c4b-9590-8bf77f1d41ea","acknowledgeRestore":true}
```

Both epochs must be explicit; the client does not fetch or substitute them. The service archives earlier receipts, plan and pending payload, checks the expected current epoch, and re-compares sanitized evidence against the restored corpus using new generation operation keys. Public `reconciliations` summaries contain prior operation keys, plan/pending hashes, usage, epochs and generation. Existing settled usage and abandoned reservations stay charged. Omitted `additionalBudget` preserves limits and absolute `deadlineUtc`; an explicit allowance may add budget/time. Identical reconciliation retries retain the generation; reusing the key for another payload conflicts. `handoff_purged` means the original sanitized evidence is no longer available and must be supplied in a new handoff.
