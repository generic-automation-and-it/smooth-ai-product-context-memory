# LADR-01: Extend the supported Host boundary

**Status:** Implemented; independent acceptance in progress.

## Context

BR-49–BR-51 require compared writes that survive retries and concurrent direct edits. A service-only lock cannot protect against direct clients, and mutable source attachment would break existing immutable-version history.

## Decision

`POST /api/context/memories` and `POST /api/context/groups/resolve` accept optional `operationKey`, `expectedCorpusEpoch` and `expectedCorpusRevision`. Individual memory writes also accept `expectedVersion`. With a key, the same canonical payload returns the previously committed result; a changed payload under that key conflicts. Operation receipt and core mutation commit together. Dry-run does not create an operation receipt. Missing optional fields preserves legacy call shapes.

`GET /api/context/corpus-state` returns restore epoch and revision. `GET /api/context/operations/{operationKey}` reports a committed operation. `POST /api/context/groups/lookup` resolves existing candidates without creating a group and uses read authorization. Mutating resolution remains a separate write capability. No caller uses read-only lookup as implicit authorization to create a group.

All participating mutations acquire the corpus boundary before business locks. Business-table triggers advance revisions; graph mutations executed through AGE explicitly advance revision because that executor bypasses ordinary statement triggers. Comparison validates epoch, revision and target version before commit. A stale comparison returns conflict and requires fresh reading; it never falls through to an unconditional create.

Sources may contain optional versioned evidence metadata: category, applicability, authority, authority reference/quote and scope. Old records without it remain legacy/unknown. SourceInput accepts source write fields; a response SourceDocument's own `v` field is not a write field. Ordinary reads, historical versions, export and snapshots retain evidence. Additive metadata is not a proof of authority on its own.

Additional supporting evidence creates a next immutable memory version. Existing version payloads, source arrays and blobs remain unchanged; the established current-version marker moves to the new version. Equivalent attachment preserves the existing claim, confidence, status, validity and classification, unions sources by stable identity, and retains every source's own authority metadata. A single capture coalesces equivalent additions to the same target before committing; exact retry returns the original receipt without another version. There is no mutable attachment side channel.

## Consequences and alternatives

A shared revision lock serializes mutations but makes stale service reasoning detectable across direct clients. Content-addressed blob I/O remains outside the short database transaction; unused immutable objects can remain after a rejected attempt. Operation receipts are corpus recovery data, not disposable recall telemetry. Source attachment grows version history deliberately; coalescing limits unnecessary inflation. The existing `(group, uuid)` memory identity is unchanged.

## Verification

Real-store corpus-boundary tests cover duplicate keys, changed payloads, concurrent writes, target versions, graph revisions and rollback. Independent live attachment review found exactly old v1 plus new v2, preserved original body/source and confidence, and unchanged replay. Mixed existing/new and failing preflight fixtures are required by NFR-01. No upstream Understanding export result is used as evidence for this contract.