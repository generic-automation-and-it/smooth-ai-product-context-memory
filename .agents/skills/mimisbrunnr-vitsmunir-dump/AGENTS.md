# mimisbrunnr-vitsmunir-dump — AGENTS.md

## TL;DR

Pure-prompt behavioral skill (no scripts): a listen-first capture session whose entire value is in NOT acting — never implement, modify, or synthesize until explicitly asked.

## Non-Negotiables

- **A braindump is never permission to implement.** Even with all switches on, only *grounding* (reading/searching) is widened — the no-modify rule holds until the user asks to synthesize.
- **Don't "optimize" the SKILL.md by deduplicating switch semantics.** The switch rules are intentionally restated in Switches, Listen, and Guardrails — reinforcement against the model's drift toward premature questioning/action is the point, not redundancy.

## Architecture Decisions

- **LADR-001** (2026-05, accepted): Default mode is tool-free, opt-in switches relax it. *Context:* file/web tool payloads are injected into context and re-billed every subsequent turn of a long capture session. *Decision:* default forbids tools; `--oktoreaddocs`/`--oktowebsearch` opt back in. *Consequence:* any edit that adds default tool use destroys the skill's cost profile — see the README cost table before changing switch behavior.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-06-12 | Initial version. | |
| 2026-09-13 | Added `--all` (enables `--oktoask` `--thinking` `--oktoreaddocs` `--oktowebsearch`). | |
| 2026-09-14 | Renamed `ai-brain-dump` → `mimisbrunnr-vitsmunir-dump`. Vitsmunir is Old Norse for intelligence, wits, and the power of comprehension — this is the AI Brain / Intelligence Dump. | |
| 2026-09-26 | `SKILL.md`/`README.md` re-laid out switches-first: the switch table opens each file ahead of the H1 and the section was renamed `Modes & Switches` → `Switches`, matching `mimisbrunnr-context-memory` and `mimisbrunnr-understanding`. In-body pointers and this file's Non-Negotiables reference updated. No behavioural or contract change. | PR #108 |
