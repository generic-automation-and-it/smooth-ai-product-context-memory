# LADR-11: An import marks the session's working point; export excludes what was marked

**Status:** Draft

## Context

A practitioner's session typically begins with a deliberate import — kvasir `import`/`load`, Odin
`--import`, or `ai-understanding --import`. Everything brought in that way is already in the store or in
a prior export. The work the session should later distil is what happens **after** that import.

Export cannot currently tell imported context from new work. It re-proposes imported material as fresh
candidates — an **echo** — and relies on staged dedup (HLD-002 LADR-04) to catch it. Dedup is the
system's largest correctness risk, so feeding it avoidable near-duplicates is the wrong place to spend
its precision.

Every supported harness exposes its session id to ordinary shell tool calls, so a skill script can key
state to the session without any hook:

| Harness | Variable |
|---|---|
| Claude Code | `CLAUDE_CODE_SESSION_ID` |
| Codex | `CODEX_THREAD_ID` |
| OpenCode | `OPENCODE_SESSION_ID` |
| GitHub Copilot CLI | `COPILOT_AGENT_SESSION_ID` |
| Pi | `PI_SESSION_ID` |

## Decision

An import writes a **session marker**; export uses it as a **boundary** by id exclusion.

- **Session id.** First non-empty of the variables above, in that order; an explicit `--session <id>`
  overrides. With neither, no marker is written, the import still runs, and it prints `Marker: none`.
- **Where.** Client-side only, in the skill scripts: `.context/session-markers/<type>-<session-id>.json`,
  `<type>` ∈ `kvasir` | `odin` | `ai-understanding`. One marker per skill; a skill's export reads only its
  own marker. No Host, API or schema change.
- **What.** References only — `{id, version}` per item, or `{selector, as_of}` for a selector-wide import
  (replayable because the store is append-only). **Never memory content.**
- **Boundary.** The **last** marked import is the boundary. Every marked import's ids stay excluded.
- **Default export** removes excluded ids from the candidate set before preflight and reports
  `Boundary: marker <time>, N excluded`.
- **`export --all`** ignores the marker and exports the input or current session in full; the skill still
  decides memory vs Understanding. The flag name is shared with `load`/`import --all` on purpose — one
  spelling per concept across directions, as with `--tickets`.
- **`import --nomarker`** (and `load --nomarker`) loads context without moving the boundary or adding
  exclusions: its items are session history and flow into a default export. Ids are still recorded with
  `nomarker: true` for the digest.
- **Precedence.** An id imported both marked and `--nomarker` stays excluded. No marker, or only
  `--nomarker` imports, means default export equals `--all`.
- **Changed imported items** are not dropped: preflight matches a candidate against the exclusion set by
  id or exact subject, mechanically, and proposes a **supersession** of that item. No model judgement.
- **Id-less input** (`load --format foreign`) is session history — no exclusion.
- **Lifetime.** No TTL and no automatic clearing; session length is unknown. Cleanup is manual.
- **Lineage** ("derived from X v3") appears in the export digest only — nothing is persisted.

## Alternatives Considered

- **A session-start index pushed by a hook** — rejected: the deliberate import already is the index,
  auto-injection breaks the deliberate-pull posture, and it needs one adapter per harness.
- **Boundary by transcript position** — rejected: compaction can erase the position; ids survive it.
- **First marked import as the boundary** — rejected for simplicity: "the last marker is the marker".
- **One marker shared by all skills** — rejected: a kvasir import must not change what an Odin export
  excludes.
- **Server-side episodic marker** — rejected for now: imports run on a read-only token, and a new write
  type is not justified by a client-side concern.
- **A separate flag name for "ignore marker"** (`--whole-session`) — rejected: naming symmetry with
  `load`/`import --all` was preferred over distinct spellings.
- **Persisted lineage** (edge or version column) — deferred: digest-only until a consumer needs it.

## Consequences

- Echo is removed at the source; dedup keeps its role as the safety net with fewer near-duplicates.
- Import stays read-only against the store (NFR-01 unaffected); the marker is a local file.
- Env-var names are an undocumented contract in most harnesses and may change; `--session` is the escape
  hatch. Subagent inheritance of the parent id is verified for Claude Code only and is out of scope here.
- Recall-feedback "used vs merely returned" (HLD-004) becomes possible from the marker but is a separate
  decision.

## Related

- **LADR-02** — load vs import, the verbs that now write a marker.
- **LADR-07** — `dump --currentsession` respects the marker and `--all`.
- **LADR-08** — `--all` breadth on load/import; the shared spelling on export.
- **HLD-002 LADR-04** — semantic dedup; amended to receive the exclusion set.
- **NFR-01** — no store write on load/import; unchanged.
