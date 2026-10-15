# AGENTS.md - mimisbrunnr-vitsmunir-dump

## TL;DR

Read [`SKILL.md`](./SKILL.md) first — switches, phases and guardrails live there; the cost rationale is in [`README.md`](./README.md).

## Non-Negotiables

- **Don't deduplicate the switch rules in SKILL.md.** They are restated on purpose in Switches, Listen and
  Guardrails — the repetition counters the model's drift toward premature questions and action.
- **A switch change edits three tables.** The switch table lives in SKILL.md, this skill's `README.md`
  (with cost) and `.agents/skills/README.md`; updating one leaves the others stale.

## Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
| 2026-06-12 | Initial version | — |
| 2026-09-13 | Added `--all` (enables `--oktoask` `--thinking` `--oktoreaddocs` `--oktowebsearch`) | #28 |
| 2026-09-14 | Renamed `ai-brain-dump` → `mimisbrunnr-vitsmunir-dump` | #55 |
| 2026-10-03 | Initialize fills a missing repository/ticket binding from Heimdallr by default | #168 |
| 2026-10-05 | Heimdallr autofill moved out of Initialize and stopped binding tickets on its own | #179 |
| 2026-10-06 | Issue 190 #23/#24: README no longer says `--oktoask` permits inspection during Listen | #190 |
| 2026-10-06 | README intent review names the Compare / Clarify phase | #188 |
| 2026-10-15 | Cut to HLD shape: SKILL.md pointer + non-SKILL.md traps only | — |
