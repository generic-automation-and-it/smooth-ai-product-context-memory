# _mimisbrunnr-shared — AI Context

AI Context: the one shared script folder for the `mimisbrunnr` plugin. Decided in
[HLD-010](https://github.com/generic-automation-and-it/smooth-ai-product-context-memory/tree/main/docs/hlds/010-skills-distribution/) (LADR-01); the cross-plugin counterpart is
LADR-02.

> **References.** Every HLD, LADR, NFR, BRD, issue and PR number in this file, and every PR-gate or CI claim,
> belongs to the upstream repository, `generic-automation-and-it/smooth-ai-product-context-memory`
> (`docs/hlds/`, `docs/brd/`), not to a repository this skill is vendored into.

## TL;DR

`scripts/` holds every Python script and JSON data file used by more than one `mimisbrunnr-*` skill —
plus the transitively-shared dependencies of those scripts. A skill references **this** folder and
never a peer skill's. The folder is not a skill and carries no `SKILL.md`.

## Non-Negotiables

- **No `SKILL.md`, ever.** This folder is not a skill, must never be loaded as one, and must never
  appear in a `skills` array (`.claude-plugin/marketplace.json`) or a skill listing
  (`.agents/skills/README.md`).
- **No peer reaches.** A shipped skill script references `parents[2] / "_mimisbrunnr-shared" /
  "scripts"` and nothing else. Reaching into `mimisbrunnr-odin-context-memory/scripts/` (or any
  other peer) is the defect this folder exists to remove — it couples a skill to another skill's
  internal layout and breaks at runtime on the consumer's least-tested path. The PR gate greps for it
  (HLD-010 NFR-01); the grep was checked against a planted peer path and caught it.
- **Copy direction is shared folder → plugin, never the reverse.** A fix to shared logic lands here
  and is re-copied outward (`ai-understanding`, a separate plugin, keeps WET copies under LADR-02).
  A fix made in a WET copy first is drift.
- **No compatibility shims.** A file that moved keeps no copy at its old path: a stale reference must
  fail loudly at its call site rather than silently run a second copy.
- **Location is packaging; ownership is contract.** `mimisbrunnr-odin-context-memory` remains the
  documented owner of the capture contract (sole authority on the write path, HLD-002). Moving a
  script here changes where the code lives, never who owns the behaviour, and never the read/write
  capability split (LADR-03).
- **Exactly one read client and one write client.** The plugin holds one
  `context_memory_read_client.py` and one `context_memory_client.py` — both here. Do not duplicate
  either, under any name, into a skill folder or into a WET copy.

## What lives here

Grouped by owner, not by consumer. The owner column is the contract owner (LADR-03); the folder is
only the location.

| Script / data file | Contract owner | Consumers |
|---|---|---|
| `context_memory_client.py` | odin (write path) | odin, kvasir, saga-dossier, ymir-bootstrap |
| `context_memory_read_client.py` | odin (read path) | odin, kvasir, ymir-bootstrap |
| `redact.py` | odin (redaction gate) | odin, heimdallr, kvasir |
| `atomicity.py` | odin (atomicity stage) | odin, kvasir |
| `deepsearch.py` | odin (bounded recall) | odin (dependency of the read client) |
| `dedup.py` | odin (staged dedup) | odin, kvasir |
| `review_queue.py` | odin (held-candidate queue) | odin, kvasir |
| `conflict_menus.json` | odin (closed resolve menu) | the review queue's consumers |
| `decisions_gate.py` | odin (value gate) | odin, kvasir, `ai-understanding` (WET copy) |
| `decisions_rubric.json` | odin (value-gate roles) | the gate, every consumer of it |
| `session_marker.py` | HLD-007 (session marker) | odin, kvasir, `ai-understanding` (WET copy) |
| `near_miss_tags.py` | odin (evidence-only reporting) | odin, saga-dossier |
| `find_session_metadata.py` | heimdallr (session binding) | heimdallr, odin, kvasir, saga-dossier, ymir-bootstrap |

`mimisbrunnr-odin-context-memory/scripts/` keeps only the two composition helpers odin alone uses —
`authority.py` and `divergence.py`. They import nothing shared, so moving them would buy nothing and
cost the skill its own script folder.

## Key Behaviors

- **Mirrored constants stay mirrored, by decision.** `MAX_CANDIDATES` (20) and the write-token name
  set are pinned in a consumer beside their shared counterpart rather than imported. A shared-folder
  reference is the rule for *files*; these are not files, and an import would either re-couple the
  skills or make the redactor a startup dependency of a client that deliberately holds none
  (LADR-01's stated disposition).
- **A module that imports a sibling works because the shared folder is on `sys.path`.** A consumer
  that imports a shared module inserts this folder on `sys.path` first; a shared script run directly
  gets it as `sys.path[0]`. Heavier than a path lookup, and the reason the *store clients* stay a
  subprocess boundary: they are invoked as scripts, never imported across a skill boundary.
- **Every distribution route must carry the folder** (NFR-04): the marketplace install copies the
  whole repository, `package.json#files` allowlists this folder, and `.claude/skills` /
  `.codex/skills` are symlinks to `.agents/skills`, so they resolve it with no copy step. A route
  that drops the folder breaks every consumer at once — loudly, not silently. Both routes were
  measured on 2026-10-14 with the real move in place; see
  [NFR-04](https://github.com/generic-automation-and-it/smooth-ai-product-context-memory/blob/main/docs/hlds/010-skills-distribution/nfrs/NFR-04-shared-folder-on-every-route.md).

## Contexts

- [Upstream HLD-010 `AGENTS.md`](https://github.com/generic-automation-and-it/smooth-ai-product-context-memory/blob/main/docs/hlds/010-skills-distribution/AGENTS.md) — the design this folder implements.
- [`../mimisbrunnr-odin-context-memory/AGENTS.md`](../mimisbrunnr-odin-context-memory/AGENTS.md) — the capture contract's owner.
- [`../AGENTS.md`](../AGENTS.md) — the skills area this folder sits in.

## Changelog

> AI loading note: Skip this section during routine task execution. Use it only when updating this rule file.

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-10-15 | Review-loop simplifications: `dedup.py` reuses the gate's redirect guard instead of a second copy and orders its outcomes by what happened to the candidate; `session_marker.py show` reports an unreadable marker (exit 2) instead of printing an empty object; both `session_marker.py` copies stay byte-identical. | review loop |
| 2026-10-15 | **Consumer review 5535929651 closed in the shared scripts.** `dedup.py` judges the joined URL (`gate.resolve_url`), refuses redirects, never echoes an HTTP error body, refuses a `noul` outside 0..1 instead of clamping, parses the read client's framed `items` answer, and keeps a candidate `possible-duplicate` while any match is unjudged or any pair has no verdict. `deepsearch.py` requires `groupUuid` on every row. `review_queue.py` scrubs every string leaf of the candidate, `reason` and `evidence`, scrubs `resolve --edit` text, and caps on unresolved entries. `session_marker.py` gains `read_marker`/`MarkerUnreadable`: a default export refuses an unreadable marker (`boundary` exits 2) — re-copied byte-identical to `ai-understanding`. The read client's `query` uses `cmd_query_unmarked` and writes no marker. HLD/NFR links are absolute upstream URLs. Finding 25 (partly skipped `set` receipts) skipped: the server's `Skipped` counts links, every item persists. | consumer review 5535929651 |
| 2026-10-14 | **Folder created; B2 move landed.** Nine scripts plus `conflict_menus.json` moved out of `mimisbrunnr-odin-context-memory/scripts/` and heimdallr's `find_session_metadata.py` out of its own skill's folder; kvasir's WET `session_marker.py` and `decisions_rubric.json` collapsed into the shared copy rather than staying behind as second copies. Three consumers rewired (heimdallr's redactor, kvasir's client, saga-dossier's composer), odin's own script table split, and this file created so the folder has its own context. | HLD-010 LADR-01, issue 222 |
