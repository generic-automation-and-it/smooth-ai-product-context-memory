# AGENTS.md - context-load-agents-context

## TL;DR

Read [`SKILL.md`](./SKILL.md) first — invocation, hook registration and runtime guarantees live there; LADRs and internals in [`references/design.md`](./references/design.md).

## Non-Negotiables

- **The rule-dir list appears twice in the script.** `is_auto_loaded_dir()` and the skill-on-path `case`
  already differ (`.ai/rules/` only in the first). Adding or removing a rule dir updates both.
- **The hook wrapper fails silently.** `.agents/hooks/load-agents-context.sh` exits 0 when the script is
  not executable, so a lost exec bit or a moved script disables context loading with no error. Keep
  `scripts/load-agents-context.sh` `+x` and the wrapper path in step with it.

## Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
| 2026-06-12 | Initial version | — |
| 2026-10-15 | Cut to HLD shape: SKILL.md pointer + non-SKILL.md traps only | — |
