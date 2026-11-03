# Collaborator design review — October 3, 2026

These requirements were added during implementation. Preserve the agreed architecture, inspect existing components before changing them, and resolve each item before deployment completion. This is an acceptance amendment, not a restart of the project.

## Required amendments

1. **Recall attribution:** distinguish direct retrieval, service retrieval, and capture-comparison searches. Separate caller requests from internal search passes so the service does not distort recall metrics. Preserve existing metrics' documented meaning.
2. **Atomicity:** preserve one independently meaningful claim, including necessary conditions and exceptions. Reconcile service and direct-mode behavior through shared fixtures. The existing detector flags “refunds are allowed within 30 days, but only for unused products” as bundled while equivalent wording without “but” passes. Address this enforcement mismatch without simply bypassing atomicity checks.
3. **Evidence attachment:** explicitly choose how additional sources attach without mutating immutable versions. Document and test the choice, including retries.
4. **Snapshot and recovery:** version the changed snapshot contract and define compatibility/refusal behavior. Implement and test how the service detects a restored corpus and reconciles its journal before replaying work. “Pause and reconcile” alone is not an implemented mechanism.
5. **Redaction:** prefer one shared implementation across both modes, enforced at all necessary boundaries. Avoid independently maintained copies of the same rules.
6. **Evidence metadata:** before finalizing it, check existing owner decisions concerning capture identity and design acceptance. Distinguish document approval, approved intent, and evidence of shipped behavior.

## Implementation assessment and acceptance

| Point | Existing implementation to inspect | Required remaining evidence | Status |
|---|---|---|---|
| 1 | Core recall feedback is reused through service searches | Twelve real-store attribution checks; default direct population, explicit all, original per-pass measures and additive caller measures | Verified |
| 2 | Canonical Python atomicity detector executed in both modes, including repaired extraction | Independent direct and packaged-adapter runs pass the same16 fixtures (conditions/exceptions and genuine independent claims); live conditional case retains one30-day/unused-product claim | Verified |
| 3 | Additional evidence creates one next immutable core version; same-target additions coalesce with per-source attribution | Independent live v3 read found v1 original source/body unchanged and exactly one v2 with combined sources, confidence/status/validity preserved; retry unchanged. Component tests cover multiple authority sources. | Verified; retain regression |
| 4 | Snapshot v4 includes receipts; v3 compatibility; restore rotates epoch and pauses journal | Seven actual process/recovery checks, public epoch-pinned reconciliation, preserved ledger/deadline, replay unchanged;43 snapshot checks plus one existing platform skip | Verified |
| 5 | Service packages and executes the canonical direct-mode Python redactor | Independent actual-Python component checks cover ingress, retrieved legacy body, provider input/output, journal, receipt and commit payload; fail-closed/parity checks pass | Verified |
| 6 | Source evidence stores category and quoted authority; namespaced capture identity | OWNER-DECISIONS trace; live document/child/implementation records, category contract and hostile authority tests | Independently verified |

The independent reviewer verifies actual code and test evidence for all six points. A passing older acceptance run does not automatically cover later amendments. Main integrates changes and updates PRD, technical design, implementation plan and KS-01–KS-15 matrix. Live failure-injection remains isolated from production.

## Subsequent clarification: upstream defect and contract review

The Understanding export defect (failed preflight followed by unconditional creates) is an independent upstream bug. The optional service uses the Host boundary directly and must not depend on that path. Upstream was refreshed to `1fbe8d7` during this build; the new metadata-autofill/doc commits did not include that export fix. Recheck before publication and reuse a relevant upstream fix if one appears rather than duplicating it.

The additive Host contracts now trace through BRD-004 and HLD-008, including idempotency/CAS, evidence metadata and immutable attachment, read-only group lookup, and snapshot compatibility. Independent review includes those documents against actual code.

Mixed existing/new candidates and over-twenty candidate handoffs are explicit acceptance cases. Passing a simple all-new or under-limit case cannot stand in for them. Retained candidates, comparison progress and any omitted tail must be accounted for, including budget continuation and failed preflight. These extend existing tests instead of creating another write path.

Publication of the feature branch and explicit enablement on the requesting practitioner's Unraid stack remain required. Default behavior for other installations stays disabled. Older planning summaries do not revoke this authorization.
