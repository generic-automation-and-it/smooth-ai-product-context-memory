# LADR-01: Shared scripts live in `_mimisbrunnr-shared/scripts/`; no skill reaches into a peer

**Status:** Accepted

<!-- Status lifecycle: Draft → Prototype → Accepted. Also: "Superseded by LADR-MM", "Deprecated". -->

## Context

Inside the `mimisbrunnr` plugin, sharing is inconsistent. Kvasir, saga-dossier and
heimdallr reach into odin's `scripts/` by sibling path (`parents[2] /
"mimisbrunnr-odin-context-memory" / "scripts"`); kvasir additionally carries WET copies
of the rubric and the session marker. A peer reach couples a skill to another skill's
internal layout: renaming, moving or splitting odin's folder silently breaks every
consumer, and the breakage surfaces at runtime on the consumer's least-tested path
(heimdallr's redactor lookup fails closed with `ticketsUnavailable`; kvasir's autofill
just stops). Measured inventory, 2026-10-14:

| Consumer | Peer-reach mechanism | Targets |
|---|---|---|
| kvasir | `parents[2]` sibling path (`_CAPTURE_SCRIPTS`) | odin: `redact.py`, `atomicity.py`, `context_memory_read_client.py`, `context_memory_client.py`, `review_queue.py`, `dedup.py`, `decisions_gate.py`; plus `HEIMDALLR_SCRIPT` sibling path to heimdallr's reporter |
| kvasir | WET copies | `decisions_rubric.json`, `session_marker.py` |
| saga-dossier | `sys.path` insert + import of odin's `context_memory_client`; `HEIMDALLR_SCRIPT` sibling path; odin `near_miss_tags.py` path | odin scripts + heimdallr reporter |
| heimdallr | `parents[2]` sibling path (`REDACTOR`) | odin `redact.py` |
| ymir-bootstrap | documented `SKILL.md` command path (agent-executed, no script import) | odin `context_memory_read_client.py probe` |
| ai-understanding | WET copies only (separate plugin — see LADR-02) | `decisions_gate.py`, `decisions_rubric.json`, `redact.py`, `session_marker.py` |

A third mechanism is a mirrored constant, not a file: kvasir carries its own
`MAX_CANDIDATES = 20`, commented as the capture skill's cap "mirrored rather
than imported". saga-dossier was measured alongside it and carries **no** such
mirror — only kvasir does. It is not a peer reach and not a copy — changing the cap is a two-file
edit today. Disposition: stays mirrored and comment-pinned; if the cap ever changes, the
change rides with it explicitly rather than through this move.

> **Shipped 2026-10-14 (B2).** The inventory above is the pre-move measurement. After the move
> every row resolves into `_mimisbrunnr-shared/scripts/` — including the Heimdallr reporter, which
> kvasir and saga-dossier reached by sibling path — and kvasir's WET `decisions_rubric.json` and
> `session_marker.py` collapsed into the shared copy, because the reason they existed for (two
> separately shipped script folders) no longer holds. The mirrored constant kept its stated
> disposition with the reason rewritten: a shared-folder reference is the rule for files, and a
> constant is not a file. saga-dossier was re-measured and carries no `MAX_CANDIDATES` mirror.

## Decision

**Adopt** one shared folder, `.agents/skills/_mimisbrunnr-shared/scripts/`, holding every
script used by more than one skill in the plugin. A skill references only that folder —
never a peer skill. The folder has no `SKILL.md` (it is not a skill and must never be
loaded as one). Kvasir's and odin's duplicate `decisions_rubric.json` / `session_marker.py`
copies collapse into the shared one; ai-understanding keeps its copies (LADR-02). No
compatibility shims are left at the old peer paths.

Evidence the folder ships on every route (B0, measured 2026-10-14 with Claude Code
2.1.286): a throwaway clone carrying a dummy
`_mimisbrunnr-shared/scripts/probe.py` installed cleanly, the plugin cache held the whole
repository, the probe file was present in the installed tree, a kvasir-relative
`parents[2] / "_mimisbrunnr-shared"` resolved from both skill trees, and the probe ran.
The whole-repo-copy premise was corroborated on 2.1.280 during the Part M install check
(both plugins install; every manifest entry resolves); the probe-resolution half was not
re-run there. The real GitHub-URL `source` was not run; it is expected to clone the whole
repository the same way.

## Alternatives Considered

- **WET copies everywhere** — rejected: multiplies every shared fix across N copies and
  would duplicate the sole writer and the read client's write-token refusal, a security
  boundary then held only by parity tests.
- **Keep peer reaching, documented** — rejected: documentation does not break the
  coupling; the `.claude-plugin/AGENTS.md` "correct as a whole" rule already records it
  and the failure mode stays silent until runtime.
- **Keep shared code inside odin (the fallback)** — held in reserve, not adopted: if a
  future installer stops copying the whole repository so the shared folder no longer
  ships, the shared code stays inside one listed skill (odin), each consuming skill names
  the dependency explicitly, and this LADR is revisited. B0 passing is what keeps this a
  fallback rather than the decision.

## Consequences

- One copy to fix, one owner, one path per consumer; `grep` for peer reaches is empty (NFR-01).
- Every packaging manifest must name the shared folder (NFR-04); a route that drops it
  breaks every consumer at once, loudly.
- The move touches every consumer's references at once — a flag day with no shims, so it
  lands as one reviewable change, not a drift.

## Open

- **The real GitHub-URL `source` is still untested.** The 2026-10-14 B0 run and the post-move
  verification both pointed `source` at `./` in a throwaway clone (never in this repository). Both
  confirmed the plugin cache holds the whole repository — B0 with a dummy `probe.py`, the post-move
  run with the real `_mimisbrunnr-shared/scripts/` contents and a reporter that resolved its redactor
  through them. A GitHub-URL `source` is expected to clone the whole repository the same way; the
  claim is inference, not evidence, and stays here until someone runs it.

## Related

- **LADR-02** — the cross-plugin counterpart (WET + parity, not shared).
- **LADR-03** — what the move must not change (ownership, client singularity, refusal).
