# AGENTS.md - mimisbrunnr-ymir-bootstrap

## TL;DR

Read [`SKILL.md`](./SKILL.md) first — the opt-in task, the runtime gate, the cited preview and the capture checkpoint live there.

## Non-Negotiables

- **Store behaviour is borrowed from `mimisbrunnr-odin-context-memory`.** Before changing anything
  store-facing, re-read its `SKILL.md`, `references/read-path.md` and `references/write-path.md`: the read
  client's write-token refusal, group resolution, lifecycle filtering, dry-run semantics and the
  20-candidate cap can invalidate this skill without any file here changing.

## Architecture Decisions

### LADR-01 — Change-impact lives in the cited preview, not the store

- **Status:** Accepted · **Date:** 2026-10-01
- **Decision:** "Hits / Does not hit" and inbound referrers are shown in the offline preview and asked in
  discovery; they are never store candidates or relations.
- **Consequence:** rejected — a claim convention inside `statement` (bundles noun, impact and the
  negative, so atomicity flags it) and `depends_on` links ("does not hit" has no relation type).

## Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
| 2026-09-20 | Recorded fresh-agent preview and unavailable-worker evaluation | — |
| 2026-09-20 | Added blinded manual evaluation corpus | — |
| 2026-09-20 | Forward test | — |
| 2026-09-27 | Provenance fields belong in the item's `sources` array | #91 |
| 2026-09-27 | Discovery fenced to git-visible files | #130 |
| 2026-09-27 | Renamed to `mimisbrunnr-ymir-bootstrap` | #91 |
| 2026-09-27 | Frontmatter uses `effort` | #91 |
| 2026-10-01 | Rubric scores change impact | #142 |
| 2026-10-01 | Change impact and inbound referrers live in the cited preview, not the store | #142 |
| 2026-10-02 | Runtime gate checks the bash-client workers | #161 |
| 2026-10-03 | Repository identity defaults to the Heimdallr report when the caller names none | — |
| 2026-10-05 | Heimdallr scans the bootstrapped checkout (`--repo-root`), not the process cwd, which can be another… | #182 |
| 2026-10-06 | Personal identifiers in evidence are generalised to a role before candidates are built | #190 |
| 2026-10-06 | Identity check reads Heimdallr's `rootMatches`, since `root` may be withheld | #188 |
| 2026-10-06 | A branch ticket is proposed and confirmed, never bound unasked | #186 |
| 2026-10-06 | Rubric no longer penalises the owner question the workflow requires | #184 |
| 2026-10-07 | Full `set --dryrun` offered only when the group UUID is known read-only or after… | #199 |
| 2026-10-14 | The runtime gate runs the read client from the plugin's shared script folder (`../_mimisbrunnr-shared/scripts/context_memory_read_client.py`) | #222 |
| 2026-10-14 | Read/write subagents removed (odin LADR-008) | #221 |
| 2026-10-15 | Cut to HLD shape: SKILL.md pointer + non-SKILL.md traps only | — |
