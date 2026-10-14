# LADR-11: Imports and exports mark the session's working point; export excludes what was marked

**Status:** Accepted — implemented 2026-10-14 (evidence below; no Host, API or schema change)

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
  (Implementation note 2026-10-14: "or" reads as "in addition" — a selector-wide import
  records its ids *and* its selector. Ids serve present exclusion; without them the marker
  would move the boundary but exclude nothing. The selector serves future replay.)
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

### Export is a marker source (amended 2026-10-14)

A long session exports at several checkpoints. If only imports mark, each checkpoint re-offers what the
previous one already wrote. So export marks too, and every default export becomes a **delta** since the
last marker.

- **Export marks.** A successful export appends `{verb: "export", at, items}` to the same per-skill marker:
  kvasir `export --write` and `dump --currentsession`, Odin `--export`, `ai-understanding --export`.
- **Boundary.** Still the last marker — of **any** verb, import or export.
- **Exclusion.** Exported ids join the exclusion set. A later in-session change to an exported item is a
  supersession of the version just written, by the same mechanical preflight match.
- **`export --nomarker`** exports normally but writes no entry: boundary and exclusions are unchanged. For a
  one-off share or snapshot that must not break the delta series. Symmetric with `import --nomarker`, and
  orthogonal to Odin `--approve` (one governs kept state, the other write status).
- **Nothing written, nothing marked.** `--dryrun`, kvasir `export` without `--write`, a refused export
  (redaction fail-closed, veto), an empty candidate set, and a review-queue deferral write no entry.
- **The marker follows the receipt.** A partial export records only what was actually written, never the
  plan (HLD-002 LADR-07).
- **`export --all`** ignores the marker for selecting candidates but still writes an export entry;
  `--all --nomarker` is a full re-export with no state change.
- **Identity per target.** Store exports record `{id, version}`; `dump` records `{folder}`
  (authored prose carries no slugs — a later export of that content is covered by the export's
  own receipt, not the dump entry); `ai-understanding --export` records `{path, slug}`.
- **Digest.** Prints `Marker: set (export, N items)` | `skipped (--nomarker)` | `skipped (nothing written)`
  | `none (no session id)`.

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
- **Import-only markers** — rejected on amendment: multi-checkpoint sessions re-propose earlier exports.
- **Marking from the export plan** — rejected: a refused or partial write would exclude ids that never
  reached the store, silently losing them from every later export.
- **A separate LADR for export marking** — rejected: one boundary concept, one record.

## Consequences

- Echo is removed at the source; dedup keeps its role as the safety net with fewer near-duplicates.
- Checkpointed capture is incremental: a second default export proposes nothing the first one wrote.
- Import stays read-only against the store (NFR-01 unaffected); the marker is a local file.
- Env-var names are an undocumented contract in most harnesses and may change; `--session` is the escape
  hatch. Subagent inheritance of the parent id is verified for Claude Code only and is out of scope here.
- Recall-feedback "used vs merely returned" (HLD-004) becomes possible from the marker but is a separate
  decision.

## Evidence

Implemented 2026-10-14, client-side only (skill scripts; the store, API and schema are
unchanged):

- **`session_marker.py`** (WET copy in all three skill script dirs —
  `mimisbrunnr-kvasir-understanding`, `mimisbrunnr-odin-context-memory`, `ai-understanding` —
  parity-pinned by each harness): five-name harness env chain
  (`CLAUDE_CODE_SESSION_ID`, `CODEX_THREAD_ID`, `OPENCODE_SESSION_ID`,
  `COPILOT_AGENT_SESSION_ID`, `PI_SESSION_ID`) with explicit `--session` override, per-skill
  JSON markers under `.context/session-markers/<type>-<session-id>.json` (atomic temp + rename),
  last-marked import as the boundary, marked-wins exclusion, digest-only lineage, and a
  `record`/`boundary` CLI for agent-driven skills.
- **Kvasir** (`understanding_client.py`): `load`/`import` gained `--nomarker`/`--session` and
  print `Marker:`; `export`/`dump` gained `--all`/`--session` and print `Boundary:`. Echo ids
  are dropped before preflight; a changed imported item is kept as a supersession (mechanical id /
  exact-subject match). `export --all` keeps its shared-spelling meaning (ignore the marker).
  The boundary is the last marker of any verb. Harness `run_tests.py` 221 → 233
  (`SessionMarkerTests`, 12 cases covering every acceptance criterion below).
- **Odin** (`context_memory_client.py`): `query`/`import` gained `--nomarker`/`--session` and
  mark + `Marker:` on stderr; `set`/`export` gained `--all`/`--session`, drop echo items (same
  uuid **and** description) and keep changed ones as supersessions; `preflight` gained
  `--all`/`--session` and discloses exact-subject matches as supersession routing. `Boundary:`
  on stderr so stdout stays parseable JSON. The read-only client deliberately never marks (it is
  the shared recall path kvasir `import` shells out to). Harness 248 → 255.
- **ai-understanding**: `SKILL.md` marker procedure via the `session_marker.py` CLI (`record` on
  `--import`, `boundary` on `--export`); a default export skips excluded slugs and writes a
  changed excluded unit as `improved`. Harness +6 `SessionMarkerCLITests`.

Export-marks amendment (same day): `export --nomarker` on kvasir `export`/`dump` and Odin
`set`/`export` (exports normally, writes no entry); a successful kvasir `export --write`
appends one entry per completed batch (written version-target uuids / caller createUuids),
`dump --currentsession` appends its folder, Odin `set` appends sent ids, ai-understanding
`--export` records `{path, slug}` — the marker follows the receipt, so dry runs, refusals,
empty and held-back sets mark nothing. Slug/file-identity exclusion (`exclusion_slugs`) covers
understanding-file re-exports; `export --all` still records. Kvasir harness 233 → 238, Odin
255 → 258.

Acceptance mapping (worktask criteria): marked import → default export drops echo with
`Boundary: marker …`; `export --all` ignores with `Boundary: ignored (--all)`; `--nomarker`-only
behaves as `--all` (`Boundary: none`); marked-wins order-independent; two marked imports move the
boundary and keep both exclusion sets; per-skill marker files are separate; changed-imported →
supersession; each env var resolves in isolation and none-set prints `Marker: none`; markers hold
references only (asserted on keys); existing `load`/`import --all` breadth unchanged (existing
breadth cases still green).

## Related

- **LADR-02** — load vs import, the verbs that now write a marker.
- **LADR-07** — `dump --currentsession` respects the marker and `--all`, and marks on success.
- **HLD-002 LADR-07** — the digest is a receipt; the export marker records the receipt.
- **LADR-08** — `--all` breadth on load/import; the shared spelling on export.
- **HLD-002 LADR-04** — semantic dedup; amended to receive the exclusion set.
- **NFR-01** — no store write on load/import; unchanged.
