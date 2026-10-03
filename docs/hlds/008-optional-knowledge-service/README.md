# Optional knowledge service and additive core contracts — HLD-008

| | |
|---|---|
| **Status** | Prototype — implementation and independent acceptance in progress |
| **Owner** | Feature practitioner / maintainers |
| **Business authority** | [BRD-004, BR-47–BR-53](../../brd/004-optional-knowledge-service/) |
| **Last updated** | 2026-10-03 |

## Intent and boundaries

The optional service performs bounded retrieval, evidence assessment, synthesis and learning capture over the existing Host API. It uses Jev for structured judgments, an OpenAI model for generation, and code for execution, validation and durable budgets. Ordinary direct use stays independent. Disabled installations start no optional service and require no provider keys.

The core changes are explicit additive contracts, not private service implementation details. Existing clients may omit the new fields. They retain ordinary core semantics, while their mutations participate in the shared revision boundary so the optional service cannot overwrite a concurrent direct edit based on a stale comparison. No Understanding export/import script is invoked by the service. The independently reproduced upstream preflight-to-unconditional-create defect remains an upstream fix; failure at this service's comparison or dry-run boundary cannot become a new insert.

## Goals and acceptance

- BR-47/48: one small caller integration returns compact cited context from the same corpus, preserving conditions and lifecycle. Direct access survives service outage.
- BR-49/50: shared atomicity fixtures and detector preserve independently meaningful rules with conditions. Semantic comparison handles existing/new mixtures. Evidence additions produce immutable versions with per-source attribution and safe replay.
- BR-51: core operation receipts and journal checkpoints survive ambiguous commits. Snapshot compatibility and explicit restored-epoch reconciliation are implemented, tested contracts.
- BR-52/53: shared budgets survive restart; both modes execute the same redactor rules. Recall attribution distinguishes caller requests, internal passes and comparison activity.

## Contract decisions

| Decision | Scope | Status |
|---|---|---|
| [LADR-01](ladrs/LADR-01-additive-host-commit-and-evidence-contracts.md) | Core state, idempotency, concurrency, evidence attachment and read-only lookup | Implemented; independent review in progress |
| [LADR-02](ladrs/LADR-02-versioned-snapshots-and-journal-reconciliation.md) | v4 snapshots, v3 compatibility, restore detection and explicit reconciliation | Implemented; extended live reconciliation acceptance pending |
| [LADR-03](ladrs/LADR-03-shared-semantics-and-attributed-workflows.md) | Shared atomicity/redaction, recall origins, lifecycle and bounded batch accounting | Implementation and verification in progress |

## Quality requirements

[NFR-01](nfrs/NFR-01-acceptance-and-evidence.md) maps deterministic, real-store and live evidence to these contracts. Parent design status does not imply acceptance of a child decision or successful deployment. The [execution matrix](../../optional-knowledge-service/EXECUTION.md) is the current gate ledger.

## Integration and alternatives

A standalone process was selected so provider outages and keys do not become core availability dependencies. Adding generation inside the existing Host was rejected for that coupling. Evidence remains on versioned Source documents; an independent mutable evidence side table was rejected because it would introduce different history and snapshot semantics. CAS and operation receipt fields extend supported Set/Resolve calls rather than creating a parallel write implementation. The shared Python detector is packaged from its canonical source, avoiding a separately maintained translation. Current execution is single-instance with serialized captures, not a distributed service.

[Technical design](../../optional-knowledge-service/TECHNICAL-DESIGN.md), [operations](../../optional-knowledge-service/OPERATIONS.md), and [owner-decision trace](../../optional-knowledge-service/OWNER-DECISIONS.md) supply detailed workflow, setup and historical context. Nothing here settles existing upstream open identity or design-acceptance decisions by implication.