# Independent knowledge acceptance tests

These tests exercise the real workflow and SQLite journal with deliberately hostile provider answers and a synthetic core boundary. They are maintained independently of the workflow classifiers. Keep assertions tied to externally observable authority, acknowledgement, scope, budgets and replay behavior; do not copy the implementation's classification rules into the oracle.

Run `dotnet test tests/SmoothAiProductContextMemory.Knowledge.ComponentTest`. Each test owns its temporary SQLite file. No operational corpus, provider credentials or private documents are used. This component suite does not replace real PostgreSQL concurrency, process-kill tests or live semantic evaluation.

## Changelog

| Date | Change |
| --- | --- |
| 2026-10-03 | Added independent hostile-provider authority checks, scope validation, clarification/purge preservation, equivalent-source durability, reopened-journal replay and outstanding budget reservation tests. |
| 2026-10-03 | Added regressions from live acceptance for duplicate target version inflation and equivalent-evidence confidence loss; extended hostile authority cases to withheld approval and investigation-only approval. |
| 2026-10-03 | Added amendment acceptance for explicit epoch-pinned reconciliation, preserved reservations and audit retention, document approval with draft children, shared atomicity after stronger repair, attributed recall passes, canonical redaction across workflow boundaries, mixed existing/new writes, preflight failure, and 25-claim budget continuation. Updated overflow refusal from 21 to 101 only after the bounded contract increased to 100; retained explicit overflow and omitted-tail checks. |
| 2026-10-03 | Added persisted-deadline restart/explicit-extension checks and public restore-boundary regressions: clarification cannot grant allowance across an unreconciled epoch, capture/reconciliation replay cannot report stale success, and a new durable acknowledgement remains independent of core availability. |
| 2026-10-03 | Added rejected-preflight continuation after a concurrent direct writer: fresh comparison must select the now-existing target while retaining extracted claims, omitted-tail disclosure and prior spending across journal reopen. |
