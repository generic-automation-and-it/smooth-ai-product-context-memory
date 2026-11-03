# LADR-03: Share capture rules and attribute internal work

**Status:** Implementation and independent verification in progress.

## Context

BR-49, BR-50, BR-52 and BR-53 require consistent atomicity, meaningful lifecycle, one secret policy and metrics that remain interpretable when a service performs several searches for one caller.

## Decision

Both modes use the canonical direct atomicity detector and shared fixtures. A rule with a necessary condition or exception is one independently meaningful claim; a conjunction is not by itself proof of bundling. Equivalent “but only” and condition-first wording must agree, while independent-claim negative controls continue to fail. Extraction does not bypass enforcement.

Both modes use the canonical Python redactor source. The service sanitizes ingress, retrieved/provider-bound evidence, provider output, derived persistence and response data, failing closed if detection cannot run. Packaging copies the canonical build input; it does not maintain a second rules implementation.

Recall attribution distinguishes `direct_retrieval`, `service_retrieval` and `capture_comparison`, with a caller request identity separate from internal search passes. Legacy omitted attribution means direct. Existing direct miss-rate/never-recalled semantics exclude internal service/comparison activity; explicit attribution reporting exposes those populations separately. No raw query or source body is added to recall telemetry.

Evidence categories distinguish suggestion, document approval, approved intent, observed implementation and unknown material. Document approval alone never promotes its draft children, intended implementation or shipment. Exact attributed source identity and specific claim support are required; Jev judgment and model confidence cannot create missing authority. Existing owner decisions remain as traced in OWNER-DECISIONS.md.

Capture persists extracted candidates and comparison progress before writing. Mixed existing/new candidates remain one reviewed plan. More than twenty candidates are not silently truncated: retained candidates and remaining comparison index survive budget exhaustion, while omitted source/claim material is explicitly deferred. Extraction accepts at most 100 retained claims. Comparison progress is durable, and a single atomic core request commits the completed plan after coalescing. Explicit budget continuation resumes remaining comparison without renewing past spending. An invalid extractor response exceeding 100 claims is refused as claim_batch_limit with the original handoff retained. When the model cannot examine all material within its output allowance, named deferred claims and source IDs disclose the tail; a failed preflight stops all planned writes. Independent tests exercise 25 mixed claims across two budget interruptions and a rejected mixed dry-run.

## Consequences

Metric population changes are explicit instead of silently altering legacy denominators. Shared scripts constrain drift while semantic judgment remains imperfect and measured. Larger handoffs may require a visible continuation; successful acknowledgement does not imply every claim has been incorporated. Batch limits never justify losing an unreported tail.