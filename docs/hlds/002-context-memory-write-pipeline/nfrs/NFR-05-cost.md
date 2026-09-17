# NFR-05: Cost

**Status:** Draft

## Requirement

Every write requires logical summary, keyword, deduplication and link judgements. Provider invocation
count is platform-dependent: one invocation may batch several facts, so fact count is not call count.

- The per-write cost profile is **documented in the skill's own README**, including where the design does *not* save.
- **Expensive paths are opt-in.** The default path performs the minimum model work; deeper search is a switch.
- **Dry-run costs approximately a full write**, since it runs the identical pipeline. This is documented so it is not mistaken for a cheap preview.
- **Delegation may increase aggregate token spend** while reducing main-session context pressure.
- **Capture is batched at a checkpoint**, never per-fact during work.

## Verification

- Count provider/agent invocations, logical judgements, candidates inspected, HTTP calls, blob I/O and
  exposed token counts separately for a representative batch.
- Assert the default path invokes no optional expensive stage.
- Assert the documented cost profile names both what the design saves and what it does not — an honest accounting, not a sales note.

## Acceptance Criteria

- Available cost units are recorded and match the documented profile.
- No expensive stage runs without an explicit switch.
- The cost note states the trade plainly, including that dry-run is not cheap.

## Applies To

All goals; LADR-01. Direct consequence of the skill owning judgement — judgement is what costs.

## Evidence

[2026-09-17 structural measurement](./NFR-05-cost-evidence-2026-09-17.md) records deterministic
bounds and main-context byte reduction. NFR remains Draft because provider token/invocation telemetry
and a live delegated run are unavailable.
