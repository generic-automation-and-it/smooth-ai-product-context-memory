# mimisbrunnr-vitsmunir-dump — AGENTS.md

## TL;DR

Pure-prompt behavioral skill (no scripts): a listen-first capture session whose entire value is in NOT acting — never implement, modify, or synthesize until explicitly asked.

## Non-Negotiables

- **A braindump is never permission to implement.** Even with all switches on, only *grounding* (reading/searching) is widened — the no-modify rule holds until the user asks to synthesize.
- **Don't "optimize" the SKILL.md by deduplicating switch semantics.** The switch rules are intentionally restated in Switches, Listen, and Guardrails — reinforcement against the model's drift toward premature questioning/action is the point, not redundancy.
- **A git-discovered ticket is a suggestion, never a binding.** The Heimdallr reporter runs no earlier than synthesis (or during Listen under `--oktoreaddocs`/`--all`), and every ticket it finds needs the operator's confirmation before it reaches an artifact.

## Architecture Decisions

- **LADR-001** (2026-05, accepted): Default mode is tool-free, opt-in switches relax it. *Context:* file/web tool payloads are injected into context and re-billed every subsequent turn of a long capture session. *Decision:* default forbids tools; `--oktoreaddocs`/`--oktowebsearch` opt back in. *Consequence:* any edit that adds default tool use destroys the skill's cost profile — see the README cost table before changing switch behavior.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-10-06 | README intent review names the Compare / Clarify phase: Listen defers by default, and Compare or Clarify (or `--oktoreaddocs` / `--oktoask`) inspects material and asks grounded questions; synthesis still waits for an explicit request (issue 188). | issue 188 |
| 2026-10-05 | **Heimdallr autofill moved out of Initialize and stopped binding tickets on its own.** Initialize ran the git-scanning reporter on every session, breaking the tool-free default (LADR-001); the reporter now runs no earlier than synthesis, or during Listen only when `--oktoreaddocs`/`--all` enables grounding. A branch-seen or newest-commit ticket was promoted straight to the binding, so an artifact could be filed against unrelated work; discovered tickets are now listed with their source and need the operator's confirmation — no answer means unbound. Wording only, no script. | issue 179 |
| 2026-10-03 | **Initialize fills a missing repository/ticket binding from Heimdallr by default** (same skills root, no hardcoded path); tags stay agent-derived keywords; explicit caller values always win. Wording only, no script. | session request |
| 2026-06-12 | Initial version. | |
| 2026-09-13 | Added `--all` (enables `--oktoask` `--thinking` `--oktoreaddocs` `--oktowebsearch`). | |
| 2026-09-14 | Renamed `ai-brain-dump` → `mimisbrunnr-vitsmunir-dump`. Vitsmunir is Old Norse for intelligence, wits, and the power of comprehension — this is the AI Brain / Intelligence Dump. | |
| 2026-09-26 | `SKILL.md`/`README.md` re-laid out switches-first: the switch table opens each file ahead of the H1 and the section was renamed `Modes & Switches` → `Switches`, matching `mimisbrunnr-context-memory` and `mimisbrunnr-kvasir-understanding`. In-body pointers and this file's Non-Negotiables reference updated. No behavioural or contract change. | PR #108 |
