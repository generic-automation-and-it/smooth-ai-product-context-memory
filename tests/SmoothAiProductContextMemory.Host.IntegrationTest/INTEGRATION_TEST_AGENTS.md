# INTEGRATION_TEST_AGENTS.md

## TL;DR

L2 Host tests exercise real stores; telemetry capture also observes requests from concurrently running fixtures.

## Non-Negotiables

- Do not select request telemetry by the latest route match: another fixture can write to the same route without blob content, producing no object-store HTTP span.
- Keep default test parallelism and the existing bounded capture wait; longer timeouts and serialization do not fix request identity.

## Key Behaviors

- Trace assertions send a unique W3C `traceparent` with sampled flag `01`, then verify the server trace ID and parent span ID to prove TestServer honored the incoming context. Wait for that trace's server, Npgsql and HTTP spans before asserting across stores.
- A later content-free POST to the same route, under a different trace ID, is captured before reselecting the original trace. This makes competing telemetry deterministic rather than depending on another test's scheduling.

## Test References

- L2: `ObservabilityTests.cs`, especially `One_request_yields_one_trace_spanning_both_stores`.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-09-15 | Bound cross-store telemetry assertions to incoming sampled trace context and added a captured same-route decoy regression. | `ObservabilityTests.cs` |
