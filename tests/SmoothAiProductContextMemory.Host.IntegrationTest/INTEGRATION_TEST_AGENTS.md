# INTEGRATION_TEST_AGENTS.md

## TL;DR

L2 Host tests exercise real stores; telemetry capture also observes requests from concurrently running fixtures.

## Non-Negotiables

- Do not select request telemetry by the latest route match: another fixture can write to the same route without blob content, producing no object-store HTTP span.
- Keep default test parallelism and the existing bounded capture wait; longer timeouts and serialization do not fix request identity.

## Key Behaviors

- Trace assertions send a unique W3C `traceparent` with sampled flag `01`, then verify the server trace ID and parent span ID to prove TestServer honored the incoming context. Wait for that trace's server, Npgsql and HTTP spans before asserting across stores.
- A later content-free POST to the same route, under a different trace ID, is captured before reselecting the original trace. This makes competing telemetry deterministic rather than depending on another test's scheduling.
- Business lifecycle tests create all state through authenticated HTTP routes, then use a separate read-capability client to model later-session recall. Unique tickets, repositories, initiatives, facets, tags and topics isolate each scenario without serialization.
- `BusinessCaptureRecallLifecycleTests` proves API contributions to BR-04--09 and BR-11, BR-13 and BR-14: work-associated and cross-repository recall, bounded attributed cheap reads, business-time filtering, relationship reasons, revision history, blobs and mechanical receipts. It does not claim agent judgment, human presentation, failure integrity or checkpoint acceptance.

## Test References

- L2: `ObservabilityTests.cs`, especially `One_request_yields_one_trace_spanning_both_stores`.
- L2: `TicketApiTests.cs` (parent round-trip, scope-consent hiding) and `TicketTraversalCorruptionApiTests.cs` (persisted-corruption 500 vs hidden-corruption indistinguishability).
- L2: `SnapshotApiTests.cs` (HLD-006 accepted-then-poll snapshot → Completed → preflight reports it). `HostWebAppFixture` pins `Snapshot:Directory` / `Snapshot:DestinationDirectory` to a per-fixture temp directory so archives never land in the test working directory.
- L2: `SecretContainmentTests.cs` with `SecretContainmentWebAppFixture` (HLD-002 NFR-01 server-side containment: a secret-shaped value sent to the Host appears in no log record at any level — message, template, structured properties and scopes — and in no 400 body). The fixture forces every Serilog level and category override to `Verbose` and asserts a Debug control record is captured, so the absence check cannot pass vacuously.
- L2: `DossierApiTests.cs` (HLD-005 bundle and preview over the real Host: API-seeded anchor + widened neighbour, read-token client, inlined MinIO body, edge, manifest reach and preview volume).
- L2: `BusinessCaptureRecallLifecycleTests.cs` (BRD-001 business capture/recall lifecycle and focused selector, cross-repository, attribution, temporal and receipt scenarios).

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-10-01 | Added `SecretContainmentTests` and its fixture for HLD-002 NFR-01's log assertion. The existing NFR-05 telemetry tests capture at the configured Information level and only the rendered message; this fixture captures every level and every structured property, so a value carried only in a log scope is also seen. | HLD-002 NFR-01 |
| 2026-10-01 | Added `DossierApiTests`: the first successful L2 call to `/api/context/dossier/bundle` and `/preview`. Every earlier dossier assertion ran at handler level against fake blob storage, so the route binding, the read capability and the MinIO body hydration were never exercised together. | HLD-005 |
| 2026-09-25 | Added `SnapshotApiTests` and per-fixture snapshot directories in `HostWebAppFixture`; the test would have caught the snapshot filename `FormatException` that made every HTTP snapshot return 500. | HLD-006 |
| 2026-09-18 | Added HTTP-only business capture/recall lifecycle coverage across real PostgreSQL/AGE and MinIO, with explicit API-level BRD mapping and later-session read capability. | BRD-001 BR-04--09, BR-11, BR-13--14 |
| 2026-09-15 | Bound cross-store telemetry assertions to incoming sampled trace context and added a captured same-route decoy regression. | `ObservabilityTests.cs` |
| 2026-09-15 | Test References extended with the ticket graph's L2 API tests. | `TicketApiTests.cs`; `TicketTraversalCorruptionApiTests.cs` |
