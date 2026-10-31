# AGENTS.md - ai-understanding

## TL;DR

Read [`SKILL.md`](./SKILL.md) first — switches, store layout, the reconcile/export/import/publish/consume procedures and guardrails live there.

## Non-Negotiables

- **Task-start import is wired outside this skill.** The trigger is the host repo's root `AGENTS.md`
  `## Understandings` section; the `understandings` rule attaches only on `**/*understanding.md`
  (LADR-013), which `INDEX.md` does not match. Nothing checks the root section — a port without it
  installs a store nothing reads. Add it first when porting.
- **SKILL.md's contract is restated in three files that drift.** `agents/openai.yaml` `default_prompt`
  (the Codex export contract), `references/publish-consume.md`, and the `understandings` rule (import
  precedence). A change to outcomes, fields, paths or precedence sweeps all three.
- **Accepted divergences from `smooth-devex-template`** — `ai-template-sync` would revert them: the
  case-folded collision check, `context_root()` (`agents_context` resolves against the repo root), the
  `--promote` three-session threshold, the durability guard, `--consume-check`, and the value gate
  (LADR-014). The changelog records each.
- **`scripts/decisions_gate.py`, `decisions_rubric.json`, `redact.py` and `session_marker.py` are
  byte-identical copies of `_mimisbrunnr-shared/scripts/`** — never imported, so the skill runs where no
  `mimisbrunnr-*` is installed; the repository's `tests/parity_decisions_gate.py` holds them equal (no `tests/` ships in an installed copy). **`scripts/assets/` is part of that copy**: the gate loads its
  messages from it, so the parity test compares the asset file set and every byte. `gate_decisions.py` is
  owned here. The gate reads `CONTEXT_MEMORY_DECISIONS_*` from the
  environment only — never a file — and no store token; `CONTEXT_MEMORY_DECISIONS_API_KEY` reaches only
  `decisions_gate.py`, as the bearer header.

## Architecture Decisions

### LADR-001 — Working store is local and disposable; publish is the durability boundary

- **Status:** Accepted — destination superseded by LADR-008 · **Date:** 2026-09-19
- **Decision:** The store is the gitignored `.context/understandings/`; leaving the workspace is a
  separate `--publish`. No mode writes to a tracked path.
- **Consequence:** a tracked store (e.g. `.agents/understandings/`) turns unreviewed session
  observations into shared repo memory.

### LADR-006 — Publish defaults to every unit; `--portable-only` filters for cross-repo

- **Status:** Accepted · **Date:** 2026-09-19
- **Decision:** `--publish` archives all units; `--portable-only` restricts to `scope: portable`.
- **Consequence:** do not flip the default to portable-only "for safety" — the common case is a
  same-repo handoff, and that default cut a real 8-unit store to 1, dropping the build/CI knowledge.

### LADR-007 — Subject folder is stamped when created

- **Status:** Accepted — uniqueness and folder-reuse clauses superseded by LADR-010; **folder shape superseded by LADR-015** · **Date:** 2026-09-19
- **Decision:** `<subject>-<yyyyMMdd-HHmm>`, 24-hour UTC, set once; `_unfiled` exempt; publish never re-stamps. *(The name is now read, never written — see LADR-015.)*
- **Consequence:** only the stamp rules stand; "reuse the existing stamped folder" is void.

### LADR-008 — Publish is a zip under `.context/`; the store is never shared through the repository

- **Status:** Accepted — agent procedure, no script · **Date:** 2026-09-19
- **Decision:** `--publish` writes `understandings-<YYYYMMDD-HHMMSS>.zip` to
  `.context/understandings-publish/`; `--consume` takes a local zip path only. No
  other skill transports Understandings.
- **Consequence:** adding remote fetch or a transport here creates a provenance and merge model of its own
  for knowledge that is meant to leave only as an out-of-band archive.

### LADR-009 — A specified destination is the approval

- **Status:** Accepted · **Date:** 2026-09-19
- **Decision:** `--path` is the one target override for `--export`/`--publish`/`--consume`, never a
  source. Writing to a default or `--path` location asks nothing; every write is reported. Only promote
  and a consume that would make an incoming copy current still ask.
- **Consequence:** the generator's `[store-dir]` stays positional, not `--path` — aligning it breaks
  every command already written against it.

### LADR-010 — An export run is a stamped folder; a slug is a version key

- **Status:** Accepted — supersedes LADR-007's uniqueness and folder reuse · **Date:** 2026-09-19
- **Decision:** Each run writes a new folder holding only its new/improved units; an improved unit is
  re-written in full with `provenance.supersedes`; old copies are never edited or deleted; the index
  shows the newest copy per slug. Rejected: merge-in-place (rewrites history, loses revision depth).
- **Consequence:** never restore a store-wide duplicate-slug check — it disables versioning. A typo'd
  slug colliding with unrelated knowledge is now uncaught by design; only the reconcile step guards it.
- **Status amendment:** the *stamped folder* half is unchanged by LADR-015 — one run, one folder, one
  stamp. What moved is the name's **shape** and where the subject lives inside it. The minute
  precision is now load-bearing on its own terms (LADR-015), not merely a uniqueness device.

### LADR-011 — `--all` skips the cut; its bar-widening half is superseded by LADR-014

- **Status:** Accepted — widening half superseded by LADR-014 · **Date:** 2026-09-20
- **Decision:** `--all` waives the user's cut; exclusions report as `has another home`, naming the file.
- **Consequence:** `--all` never loosens the value bar (LADR-014).

### LADR-012 — Durability and redaction are write-time; the body carries no paths

- **Status:** Accepted · **Date:** 2026-09-21
- **Decision:** A path that is the knowledge goes in `agents_context`, which the generator
  existence-checks; redaction happens at write, the pre-publish check is the second net.
- **Consequence:** do not add a path-shaped-string check on the body — prose legitimately names files
  (`Project.slnx`), so it fires on correct units and trains agents to ignore the problem list.

### LADR-013 — A unit file carries a `.understanding.md` postfix

- **Status:** Accepted · **Date:** 2026-09-22
- **Decision:** `<slug>.understanding.md`, mirroring `.instructions.md`; the governance rule is scoped
  `**/*understanding.md`. `INDEX.md` and `<slug>.assets/` keep their names. Rejected:
  `<slug>-understanding.md` — a hyphen reads as part of the slug.
- **Consequence:** the rule no longer always-applies — see the task-start Non-Negotiable.

### LADR-014 — `--all` re-exports the whole scope; it never loosens the decision threshold

- **Status:** Accepted · **Date:** 2026-10-11
- **Decision:** `--all` is scope coverage: it skips the split ask and re-exports every in-scope unit as an
  upsert (a fresh stamped copy with `provenance.supersedes`; unchanged units go unscored). New and improved
  units are scored by the decision gate exactly as in a normal export.
- **Consequence:** the version chain grows for unchanged units. Rejected: `--all` loosening the bar — it
  would be the one mode the value gate does not bind.

### LADR-015 — A run folder is `<stamp>-<session-id>` and holds dateless categories

- **Status:** Accepted — supersedes LADR-007's folder shape · **Date:** 2026-10-16
- **Decision:** The store is `<yyyyMMdd-HHmm>-<session-id>/<category>/<slug>.understanding.md`. One
  export run makes one folder; the subject is a dateless `<category>/` nested inside it and recurs
  across runs. The legacy `<subject>-<yyyyMMdd-HHmm>/<slug>.understanding.md` shape is **read, never
  written**, with no migration: existing trees are left exactly where they are and the writer only
  adds new-shape folders beside them.
- **Consequence:** the stamp had to move to the *front* of the folder name. `SUBJECT_STAMP_RE` is
  `$`-anchored, so appending a session id to the legacy name makes the folder fail the stamp check,
  fail `stamp_of()`, and sort as `_unfiled` does — every run in the store reading as oldest.
- **Consequence:** the stamp **keeps `-HHmm`**, and this is now load-bearing rather than incidental.
  The stamp is the primary sort key for "which copy of a slug is current", and the `updated` fallback
  is required but **date-only** (`:238-242`, `iso_date`). A date-only stamp collapses every export on
  one day into one recency bucket and silently marks the wrong version current — measured, not
  hypothesised. One export run per minute is the assumption that holds it correct.
- **Consequence:** the walk dispatches on **observed shape**, not on the folder name: a top-level
  folder with no unit files directly inside it is read as a run folder of categories. Dispatching by
  name alone would leave the walk unable to see the nested layout at all, because during the
  transition every real folder is a legacy run folder. A folder satisfying both readings — units *and*
  categories — is read as nested, which is the shape the writer produces.
- **Consequence:** the same-minute tiebreak is `session` then `path`, and the session component is
  **redundant with the path today** (the run folder is the path's first segment; 36 same-stamp pairs,
  zero disagreements). It is kept because the ordering is then visible in the key itself rather than
  resting on a reader knowing that, and because it stops being redundant the moment a layout puts the
  category above the run folder. An id is opaque, so this is a *deterministic* tiebreak and not a
  recency one.

## Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
| 2026-10-31 | Durability-guard test asserts on the `unpublished` section only; the whole-output check failed once the fixture date aged past the 30-day staleness flag | #304 |
| 2026-10-24 | **`scripts/assets/` joined the WET copy set.** The shared gate loads two messages from `scripts/assets/`, so this skill's copy carries the folder and `parity_decisions_gate.py` compares the file set and every byte — a gate copied without its prose is not the same gate | this PR |
| 2026-10-22 | **Decision settings are environment-only (APPHOST LADR-007).** The WET `decisions_gate.py` re-copied from the shared original (file loader removed); `gate_decisions._flag_set_nowhere` checks the environment only. `gate_behavior.py`: the two "file-only settings enable" cases replaced by one asserting a credential file naming a live stub leaves the gate `not-configured` with zero requests. | LADR-007 |
| 2026-10-16 | **The WET copy of `decisions_gate.py` gained `--snapshot-file`**, applied identically to the shared original so `parity_decisions_gate.py` still holds them byte-equal. One run's verdicts, written whole beside the stable budget ledger: replaced rather than appended, and naming the ledger it complements. 174 -> 177 gate cases. | PR #278 |
| 2026-10-16 | **An assets sibling is not a category** -- a regression this branch introduced rather than inherited. `categories` counted every subdirectory, and a `<slug>.assets/` folder is a unit's evidence at every level of the layout, not a category holding units. A purely legacy folder carrying one therefore read as a current run folder of categories: its unit was reported as "sits at the run root" and **nothing was indexed at all**. One unit became zero, with two problems naming a move that would not have helped -- strictly worse than the silent drop it replaced, and it hit the shape the layout documents as normal. The earlier dispatch fix passed a fixture with no assets folder, so it could not see the damage it had done: a regression test is only as good as the fixture it is built on. 118 -> 120 cases. | PR #278 review `3.` |
| 2026-10-16 | **`--run-folder` resolves the whole run folder in one call**, stamped once: the session id comes from an argument or the environment ladder (`CLAUDE_CODE_SESSION_ID` … `PI_SESSION_ID`, mirroring the sibling marker scripts) and the clock is read once. The verb exists because an export writes its units across many file operations performed by the *agent*, not one process — re-deriving the stamp between two units can straddle a minute boundary and split one run across two folders, each holding half, which no later check repairs. An id outside `[A-Za-z0-9._-]` is rewritten with a sha256 prefix appended: a separator would nest the units and drop them from the index **silently**, and two ids sanitising alike still get separate folders. Refuses (exit 2) rather than inventing an id. Harness 106 → 115. | LADR-015 |
| 2026-10-16 | **Store layout: `<yyyyMMdd-HHmm>-<session-id>/<category>/<slug>.understanding.md`** (LADR-015). One run folder per export run, subject is a dateless nested category recurring across runs. Legacy `<subject>-<stamp>/<slug>` read dual-shape, never written, no migration. The stamp moves to the front of the name — the legacy regex is `$`-anchored, so a trailing session id made every folder sort as `_unfiled`. **The minute is kept and is now load-bearing:** `updated` is required but date-only, so a date-only stamp would collapse a day's exports into one recency bucket and mark the wrong version current. Walk dispatches on observed shape rather than folder name, since during the transition every real folder is a legacy one. Harness 92 → 106; mutation-verified. | LADR-015 |
| 2026-10-16 | `redact.py` re-copied from `_mimisbrunnr-shared/scripts/`: the `set` body scrubs `ticketReferences[].reason` and the `add_ticket_references` write spec is declared. | HLD-003 LADR-09 |
| 2026-10-15 | Ralph loop: a non-UTF-8 unit and a unit without a slug are reported rather than raised; an unscoreable decision row is disclosed; the advisory ledger is a context manager; `unit_record` emits the key `record_state` reads | — |
| 2026-09-19 | Initial version | — |
| 2026-09-19 | Term provenance recorded so the name is not genericized into session logs | — |
| 2026-09-19 | Eval | — |
| 2026-09-19 | Unit flattened to `<slug>.md` | — |
| 2026-09-19 | Review sweep removed a false claim and a duplicate unit | — |
| 2026-09-19 | `--export`/`--import` mean session↔disk | — |
| 2026-09-19 | One `dangling_references` walk over every frontmatter list | — |
| 2026-09-19 | `--review` reads `provenance.inherited` back as a usage signal | — |
| 2026-09-19 | Single "no other home" qualifying test | — |
| 2026-09-19 | Outcomes admitted as a kind of unit | — |
| 2026-09-19 | A unit is a question/answer pair, fixing triggers broader than the body | — |
| 2026-09-19 | Toolchain knowledge excluded | — |
| 2026-09-19 | `inline_sequences` handles nested flow lists and only fully-bracketed values | — |
| 2026-09-19 | Flow-style lists parsed as scalars escaped the reference walk | — |
| 2026-09-19 | `--publish` publishes every unit by default | — |
| 2026-09-19 | Subject folders stamped `-yyyyMMdd-HHmm` (LADR-007) | — |
| 2026-09-19 | Publish is a zip under `.context/understandings-publish/` (LADR-008) | — |
| 2026-09-19 | Stamp is UTC | — |
| 2026-09-19 | Quoted `"[[slug]]"` entries parse as references | — |
| 2026-09-19 | Export looks before minting a folder | — |
| 2026-09-19 | A slug is a version key | — |
| 2026-09-19 | `--path` is the one target override | — |
| 2026-09-20 | `--all` widens the qualifying bar as well as skipping the ask (LADR-011) | #86 |
| 2026-09-20 | Dangling `ai-asset-sync` reference qualified as upstream-only | #86 |
| 2026-09-20 | Malformed frontmatter shapes reported instead of crashing | #86 |
| 2026-09-21 | Units path-free and redacted at write time (LADR-012) | — |
| 2026-09-22 | Unit files postfixed `.understanding.md` | — |
| 2026-09-22 | A placeholder in `provenance.inherited` is validated, not counted as lineage | — |
| 2026-09-24 | Re-synced from `smooth-devex-template` | #101 |
| 2026-09-30 | Stamp slice lengths matched (12 vs 13 chars), so the same-minute exemption fires | — |
| 2026-09-30 | Committed L0 harness runs in the PR gate | — |
| 2026-09-30 | Durability guard warns on unpublished units in a gitignored store | — |
| 2026-10-01 | Case-folded name collisions reported, and refused before `--consume` writes | — |
| 2026-10-01 | `--promote` proposes after inheritance in three independent sessions | — |
| 2026-10-01 | `agents_context` resolves against the repo root, not the process cwd | — |
| 2026-10-01 | Durability judged by archive membership, not time | — |
| 2026-10-01 | Durability stamp compared at minute precision | — |
| 2026-10-02 | `--dontask` skips the export ask without loosening the qualifying bar | #161 |
| 2026-10-05 | Archives carry current units only, stated everywhere | #182 |
| 2026-10-05 | Consume gate is a runnable script (`allowed-tools` cannot run a heredoc) | #182 |
| 2026-10-05 | A non-list `provenance.inherited` is reported, not ignored | #179 |
| 2026-10-06 | A unit counts as published only with every local `<slug>.assets/` file restorable from the… | — |
| 2026-10-06 | README handoff passes `--from` | — |
| 2026-10-06 | An archive `--consume` would refuse credits nothing, so it cannot silence the durability warning | #190 |
| 2026-10-06 | A symlink entry is not a publication | #188 |
| 2026-10-06 | Only an exact `<subject>/<slug>.understanding.md` member counts | #186 |
| 2026-10-06 | `--consume-check` reads member bytes (names and modes cleared damaged data) | #184 |
| 2026-10-07 | Durability discounts `[[…]]` only where the archive dropped it, so a link unbracketed locally… | — |
| 2026-10-07 | A unit counts as published only when the archived bytes match the working copy… | — |
| 2026-10-07 | Harness docstring | #200 |
| 2026-10-07 | Consume and publish accept a member only when its Unix type matches its name | — |
| 2026-10-11 | WET decision gate added to `--export`, and `--all` redefined as scope coverage (LADR-014) | — |
| 2026-10-14 | WET gate copy's parity target moved to the shared folder | #222 |
| 2026-10-14 | WET gate copy takes the subject-less ledger fix | #222 |
| 2026-10-14 | Session marker: import sets the export boundary (HLD-007 LADR-11) | — |
| 2026-10-15 | Consumer review 5539567320: a marked import records `--slug` per unit — `--selector` supplements and never replaces it, since the exclusion is by slug | — |
| 2026-10-15 | Consumer review 5539567320: marker tests resolve paths with `marker_path`, and the reference-only case pins the entry's key set as well as each item's | — |
| 2026-10-15 | consumer review 5538013336: `session_marker.py` re-copied (digest suffix for sanitised IDs, stored-session check) | — |
| 2026-10-15 | Consumer review 5535929651: `understanding_index.py` takes back the strict ISO-date check on `updated` / `provenance.learned`… | — |
| 2026-10-15 | Cut to HLD shape: SKILL.md pointer + non-SKILL.md traps only | — |
