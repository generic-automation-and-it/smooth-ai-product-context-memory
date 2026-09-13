# NFR-05: Cost

**Status:** Draft

## Requirement

**Every write costs a model call** — for summary, keywords, deduplication judgement and link
derivation. This is the dominant operating cost of the design and must be stated rather than
discovered.

- The per-write cost profile is **documented in the skill's own README**, including where the design does *not* save.
- **Expensive paths are opt-in.** The default path performs the minimum model work; deeper search is a switch.
- **Dry-run costs approximately a full write**, since it runs the identical pipeline. This is documented so it is not mistaken for a cheap preview.
- **Capture is batched at a checkpoint**, never per-fact during work, so cost scales with checkpoints rather than with facts.

## Verification

- Count model invocations for a representative batch and record the per-candidate figure.
- Assert the default path invokes no optional expensive stage.
- Assert the documented cost profile names both what the design saves and what it does not — an honest accounting, not a sales note.

## Acceptance Criteria

- Per-write model-call count is recorded and matches the documented profile.
- No expensive stage runs without an explicit switch.
- The cost note states the trade plainly, including that dry-run is not cheap.

## Applies To

All goals; LADR-01. Direct consequence of the skill owning judgement — judgement is what costs.
