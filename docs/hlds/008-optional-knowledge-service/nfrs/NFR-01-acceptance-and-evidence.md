# NFR-01: Independent evidence before enablement

**Status:** In verification; see execution matrix for remaining gates.

| Contract | Required independent evidence |
|---|---|
| Additive APIs and legacy compatibility | Old clients, read/write authorization, same-key and changed-key behavior, stale revision/target version, real concurrent direct writes |
| Immutable attachment | Old historical source/body unchanged; one coalesced next version with correct per-source metadata; replay unchanged |
| Atomicity and batch completeness | Shared positive condition/exception fixtures and bundled negative controls; mixed existing/new candidates; more-than20 extraction/comparison with explicit retained/deferred accounting; failed preflight never creates |
| Recall attribution | Direct metrics unchanged; service caller requests distinct from internal passes; capture comparisons never counted as direct recalls |
| Recovery | v3/v4 compatibility and refusal tests; actual interrupted process; older corpus restored by one-shot process; explicit reconciliation with epoch, audit, budget and idempotency checks |
| Authority and redaction | Document approval versus draft children/intent/shipped fixtures; exact shared detector and all necessary boundaries; fail-closed and ordinary-prose cases |
| Live usefulness | Frozen baseline/held-out cases and fresh caller demonstration; quality, caller context, provider usage and latency reported separately |
| Release | Tested published commit, identifiable deployment artifacts, protected configuration, verified backup/rollback, default disabled elsewhere |

Evidence locations: [validation](../../../optional-knowledge-service/VALIDATION.md), [execution](../../../optional-knowledge-service/EXECUTION.md), [collaborator review](../../../optional-knowledge-service/COLLABORATOR-REVIEW.md). Runtime reports, journals and credentials stay outside Git. Passing mocked tests does not close live semantics or real recovery. Existing upstream open decisions are disclosed rather than silently marked accepted.