# AGENTS.md - _mimisbrunnr-shared

## TL;DR

Read [`scripts/`](./scripts) first — the scripts and JSON data used by more than one `mimisbrunnr-*` skill live there; this folder is not a skill.

## Non-Negotiables

- **No `SKILL.md`, ever.** The folder must never be loaded as a skill, appear in a `skills` array
  (`.claude-plugin/marketplace.json`) or gain a row in the skill table of `.agents/skills/README.md`.
- **Fixes land here first and are re-copied outward.** `ai-understanding` keeps byte-identical copies of
  `decisions_gate.py`, `decisions_rubric.json`, `redact.py` and `session_marker.py` (it ships as its own
  plugin); a fix made in a copy first is drift.
- **One read client and one write client — both here.** Do not duplicate either, under any name, into a
  skill folder. A skill script references this folder and never a peer skill's `scripts/`.

## Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
| 2026-10-16 | `context_memory_client.py` gained `add-ticket-references` (`POST /api/context/memories/ticket-references`), a write route; `redact.py` scrubs `ticketReferences[].reason` in the `set` body and declares the `add_ticket_references` write spec. | HLD-003 LADR-09 |
| 2026-10-15 | Ralph loop: an error body that cannot be read is still classified; `main` catches `ValueError` like the read client's does; the raw-body banner is unconditional; the receipt's item filter tests the item, not the payload; `park` states what a resolution does; a marker that cannot be moved aside is refused, not overwritten | — |
| 2026-10-14 | Folder created; B2 move landed | #222 |
| 2026-10-15 | consumer review 5538013336: `review_queue.py` serialises every write behind an exclusively-created lock file (portable | — |
| 2026-10-15 | Consumer review 5539567320: `scrubbed_send` returns the scrubbed payload so the `set` receipt records what was **sent**; a resolution closes the hold it follows, so a re-held candidate is open again and counts toward the cap | — |
| 2026-10-15 | Review-loop simplifications | — |
| 2026-10-15 | Consumer review 5535929651 closed in the shared scripts | — |
| 2026-10-15 | Cut to HLD shape: SKILL.md pointer + non-SKILL.md traps only | — |
