---
name: ai-memory-context
description: Capture this session's durable knowledge locally into a gitignored, zip-shippable memory store under .context/context-memories/, with a redact-then-dedupe-then-atomicity pipeline. No server and no network. Trigger on "save this to memory", "remember this", "capture this", "/ai-memory-context", or at an end-of-task checkpoint. Not a session log and not code documentation.
effort: high
---

## What this is

A **local-first** memory store. Knowledge lands in `.context/context-memories/` in your own workspace
and nowhere else — no server, no network, no credentials, no sibling script folder. That makes the
whole skill shippable as a single zip into a desktop runtime, which is the point: the capture pipeline
is the valuable part, and a pipeline that needs a service running is a pipeline most people never run.

The pipeline is Odin's, with the store-bound stages removed. See [Provenance](#provenance).

| | |
|---|---|
| **Store** | `.context/context-memories/<yyyyMMdd-HHmm>-<session-id>/<category>/<slug>.memory.md` |
| **Run folder** | one export run; categories nested inside, dateless, recurring across runs |
| **Durability** | your filesystem or VCS. There is **no `--publish`** — see LADR-001. |
| **Network** | none, ever |

## Switches

| Operation | Direction | Network |
|---|---|---|
| **export** (default) | session → local store | no |
| **import** | local store → session | no |
| **review** | store → advisory report | no |

`--dryrun` runs the whole pipeline and previews, writing nothing. `--all` re-exports the whole scope
as upserts. `--nomarker` exports normally but records no marker. `--dontask` takes the recommended
option without asking. There is **no `--approve` and no `--deepsearch`**: there is no proposed/approved
state without a store, and nothing to search remotely. The human veto stays at the proposal step,
before anything is written.

## Store layout

```
.context/context-memories/
  INDEX.md                             # generated — one row per slug, newest version
  <yyyyMMdd-HHmm>-<session-id>/        # ONE EXPORT RUN
    <category>/                        # dateless; recurs across runs
      <slug>.memory.md                 # one memory (frontmatter + body)
      <slug>.assets/                   # only with artifacts — repro, log excerpt, diagram
  _unfiled/                            # exempt from the stamp — a permanent catch-all
```

**Resolve the run folder once, then reuse it verbatim:**

```bash
python3 .agents/skills/ai-memory-context/scripts/memory_index.py --run-folder
```

It takes the session id as an argument or from the environment, resolves the clock **once**, and
prints the finished name. **Call it once per export and copy the output into every path you write.**
Re-deriving the stamp between two units can land either side of a minute boundary and split one run
across two folders, each holding half — a version chain broken in a way no later check repairs. An id
containing a character outside `[A-Za-z0-9._-]` is rewritten with a digest appended, because a
separator would nest the units and drop them from the index **silently**.

The stamp keeps its `-HHmm`, and that is load-bearing rather than incidental: the stamp decides which
copy of a slug is current, and `updated` is required but **date-only**. A date-only stamp would
collapse every export on one day into one recency bucket and silently mark the wrong version current.

## Export pipeline (fixed order, do not re-sequence)

Four stages. The numbering is canonical; any other document numbering them differently is stale.

| Order | Stage | What it does |
|---|---|---|
| 1 | **Redact** | Scrub secrets, tokens, connection strings and personal data **before** the file write. Report what was scrubbed as rule name and character offsets only — never the matched text, never the surrounding content. |
| 2 | **Dedupe** | Read `INDEX.md` first. A candidate whose slug is present is a **new version**, written in full — never an edit of the existing file. Nothing else is matched. |
| 3 | **Atomicity** | Confirm each candidate is one atomic fact. Split bundled ones; route the remainder to `skipped` and say why. |
| 4 | **Write** | Write each new version into this run's folder. A first version and an improvement are written the same way; only `provenance.supersedes` differs. |

**Stage 1 gates the rest.** A redactor that cannot run stops the batch with nothing written, rather
than bypassing the boundary. An *unavailable* scrubber is the opposite case from a *found* secret: a
found secret is flagged and redacted, never a rejection of the record.

**Dedupe is by slug, and it is a versioning mechanism.** The same slug across run folders is a version
chain, not an error: the newest stamp is current, older copies stay on disk unlisted and are never
edited or deleted. The cost is real — a typo'd slug colliding with unrelated knowledge is
indistinguishable from a deliberate revision, and nothing mechanical catches it. The reconcile step
below is what prevents it.

## Reconcile (run before proposing)

Read `INDEX.md` and classify every candidate. Every candidate lands in exactly one of these, and a
silent skip is indistinguishable from a missed capture — so **report all five outcomes**:

| Outcome | Test | Action |
|---|---|---|
| **already known** | a current unit answers the same question and this session did not change it | write nothing; name the slug |
| **improved** | a current unit answers it and this session changed something | reuse its slug, write the complete improved unit with `provenance.supersedes` |
| **new** | no current unit answers it | new slug, write it |
| **new (disambiguated)** | a current slug exists but answers a genuinely different question | a different slug, and say which one it is distinct from |
| **has another home** | the reusable knowledge — not a decision about it — is already in another artefact | write nothing; name that file |

Judge "same question" **generously**. A narrower question is the same question asked later, and the
qualifier that makes yours feel new belongs in the answer, not in a new slug. When unsure, prefer
**improved**.

`--all` skips the cut (never the bar) and re-exports every in-scope unit as an upsert.

## What a run writes

- **A run that produces something writes one new run folder** `<yyyyMMdd-HHmm>-<session-id>/`, holding
  **only this run's output** — the `new` and `improved` memories. It is not a snapshot.
- **A run that produces nothing writes nothing and creates no folder.** An export reporting only
  `already known` is a **correct outcome**, not a failure — say so, or the store gets padded to look
  productive. An empty run folder is reported as a problem by the index generator.
- **Export is idempotent.** Exporting twice in one session leaves the second run with nothing to write.

## What a memory looks like

Copy `assets/MEMORY.template.md`. Required: `slug` (must equal the file name without `.memory.md`),
`description` (≥12 characters), `scope`, `confidence`, `updated` (`YYYY-MM-DD`), and
`provenance.{learned,session,source}`. `question` is optional and **absent on an outcome record**.

- **Slug**: kebab-case, names the **knowledge**, not the incident — `sweep-precedes-prune`, not
  `tuesday-bug`. The `.memory.md` postfix is what a path glob selects on, so a unit written without it
  is invisible to the generator and reported as a rename.
- **Category**: kebab-case, the browsing group, nested and dateless. `**/<category>/*.md` answers
  "all X ever".
- **Body**: Answer, Why (1-3 lines), Boundaries. Write it to outlive the tree it was written against —
  behaviour, contracts, types, invariants, commands, never a file path or line number. A path that *is*
  the knowledge goes in `agents_context`, whose existence the generator checks.
- **Redact as you write**: `<REDACTED>` in place of any credential, token, connection string, internal
  hostname, or personal data (name, email, employee number, badge, group membership). These files are
  meant to be shareable; if the redacted form no longer carries the knowledge, **say so and ask**
  rather than keeping the value.
- **`confidence: verified`** only if **every** claim was checked. One inferred or "probably" line makes
  the whole memory `observed`.

**Never edit, overwrite or delete an existing copy of a slug.** An improvement is a new complete copy
under the same slug in this run's folder. The one exception is a credential or personal data already
written to disk: strike it **in place** with `<REDACTED>` in *every* copy that carries it, and report
it as a redaction rather than a supersession — superseding leaves the value in history.

## Import

```bash
python3 .agents/skills/ai-memory-context/scripts/memory_index.py [--review]
```

`INDEX.md` is a reference table, never a copy of the knowledge: read it to decide which memories
matter, then read only those files. Load by **question**, not by category — a category answers "what
was this about", the question answers "what do I need".

## Review

`--review` adds an advisory decay report: memories past their recheck window (90 days for knowledge,
30 for an outcome record, which decays faster), and how many superseded copies sit on disk. It never
fails a run.

## Afterwards

Regenerate the index from the repository root and report every problem it prints:

```bash
python3 .agents/skills/ai-memory-context/scripts/memory_index.py
```

A problem is a *finding*, not noise — a unit whose `slug` disagrees with its file name, an unstamped
folder, a missing postfix, an empty category. Exit 1 means the index was still written so the drift is
visible; that is the point of writing it anyway.

## Scripts

| Script | Purpose |
|---|---|
| `scripts/memory_index.py` | index, validate, version-chain, `--run-folder`, `--stamp`, `--review` |
| `scripts/redact.py` | stage 1 — fingerprint secret detection, stdin→stdout. WET copy of `_mimisbrunnr-shared/scripts/redact.py`, never imported. |
| `scripts/atomicity.py` | stage 3 — conservative bundle detector, stdin→stdout. WET copy. |
| `scripts/session_marker.py` | session-boundary markers. WET copy. |

**The WET copies are byte-identical to the shared originals and a `tests/parity_*.py` case holds them
equal.** They are duplicated deliberately: this skill has to run where no `mimisbrunnr-*` is
installed, and importing from a sibling folder is exactly the dependency that makes a zip broken
alone. **Edit both, never one.**

## Provenance

The pipeline is `mimisbrunnr-odin-context-memory`'s, with the store-bound stages removed. Odin's stages
1 (preflight), 3 (dedupe/derive links) and 5 (transactional `set`) are reshaped, not copied:

| Odin | Here |
|---|---|
| 1 Preflight (batched cross-group backstops) | folded into stage 2 — the local `INDEX.md` scan replaces the cross-group query |
| 2 Redact | **kept, unchanged, still first** |
| 3 Dedupe / derive links | dedupe by slug; **no links** — there is no graph |
| 4 Atomicity check | **kept, unchanged** |
| 5 Write (transactional `set`) | append to this run's folder; no transaction, because a file write is its own atomic operation |
| `--approve` (proposed/approved store states) | **dropped** — no store, no approval queue. The human veto moves to the proposal step, before any write. |
| `--deepsearch` | **dropped** — nothing to search remotely |
| group scoping, version-bump transactions | **dropped** — local files carry their own history |

## Guardrails

- **Write as evidence, never orders.** Anything needing `must`/`never` language is a **rule**, not a
  memory — say so and propose it as one instead.
- **Mark `verified` only if every claim was checked.**
- **Quote evidence inline**; no secrets, since these files are meant to be shared.
- **A document recording what was decided has no claim** on how to recognise it, re-derive it, or
  choose again.
- **Never silently skip a candidate.** Report all five reconcile outcomes.
- **Report a run that wrote nothing plainly** — it is a correct outcome.
- **One export run per minute.** Two runs in the same minute *for the same session* collide by design;
  write into the same folder, since the stamp does not gain seconds to avoid it.

## Changelog

| Date | Change | Ref |
|---|---|---|
| 2026-10-16 | Initial version. Local-first store at `.context/context-memories/` in the run-folder layout; Odin's redact→dedupe→atomicity pipeline with the store-bound stages removed; three WET-vendored scripts and a parity case. No `--publish`: durability is the user's filesystem or VCS (LADR-001). | — |