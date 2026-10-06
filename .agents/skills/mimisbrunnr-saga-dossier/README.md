# mimisbrunnr-saga-dossier

## What this is

The context-memory store is good at answering one question at a time, but it can't hand you a
**document**. This skill can. Point it at a slice of the store — a repository, an initiative, a ticket,
some tags, any combination — and it hands back one readable, ordered, fully-cited write-up of
everything the store knows about that slice, plus a findings report on what's wrong with the
knowledge itself: gaps, contradictions, stale claims, weak summaries.

Think of it as **"catch me up on this"**, produced automatically instead of by re-reading a hundred
memories yourself.

## When to reach for it

- **Re-entering a repo or ticket after weeks away** — read the dossier instead of the whole memory
  store.
- **Handing off to a colleague or another agent** — give them one document instead of store access.
- **Drafting a design doc or an HLD** — ground it in what's already been captured, with citations you
  can check.
- **Auditing the store itself** — the findings report is the only place that surfaces contradictions
  and gaps nobody happened to notice because they never asked the right question — and the `gap` and
  `contradiction` entries in it are the semantic judgement you supply, not something the composer
  derives on its own (see *What you get back* below).

## What it is not

| Not this | Why |
|---|---|
| A live query / `get` | That's `mimisbrunnr-odin-context-memory`'s job — one question, a cited answer. This skill produces a standing document over a whole slice |
| A write path | This skill cannot write to the store, full stop — no version, no edge, no label, no blob. See *Guarantees* below |
| The whole-store forensic dump (`export` CLI) | That's a complete, unordered, unjudged data dump that deliberately bypasses retrieval scope. This skill is scoped, ordered and judged — a different artefact for a different question |
| A summary you can't check | Every substantive line cites the memory it came from (identity, version, capture time) — nothing here is take-my-word-for-it |

## How it works — two steps, two kinds of trust

| Step | Who does it | Is it the same every time? |
|---|---|---|
| **Bundle** | The Host API selects and assembles the raw material — the memories in scope plus the recorded links between them | Yes — ask for the same slice of an unchanged store and get byte-identical results back |
| **Dossier** | This skill reads the bundle and **composes** it — orders it, merges genuine restatements, flags contradictions, writes the findings | No — composition is judgement, the same way a human writing the same brief twice would phrase it differently |

The skill never re-decides what's in scope. It takes exactly the bundle it was handed and composes
from that — nothing added, nothing quietly dropped.

## Try it

```bash
# 0. Scratch space, readable by you only. The judgement files you write later get your default file
#    mode, so this directory is what keeps them private; compose refuses them otherwise.
mkdir -p -m 700 .context/mimisbrunnr-saga-dossier/scratch

# 1. Preview first. This is blob-free and cheap: it shows the selection the store would hand back —
#    which memories, how many, how far the links reach, the estimated cost and any limit it hit.
python3 -B .agents/skills/mimisbrunnr-saga-dossier/scripts/dossier_composer.py \
  preview --repo kingstown --widen-depth 3

# 2. Stop and decide. Approve the scope as shown, narrow it (change the flags and preview again),
#    or cancel. Nothing past this point runs until the scope is approved.

# 3. Make sure the approved slice holds no personal data first: records captured before the GDPR
#    capture rule may, so if you cannot rule it out, ask, and if it still cannot be established, stop.
#    Then fetch the bundle for exactly the approved anchors — same flags as the approved preview. It is
#    written owner-only, and only to a gitignored path: it holds every selected memory's full text.
python3 -B .agents/skills/mimisbrunnr-saga-dossier/scripts/dossier_composer.py \
  bundle --repo kingstown --widen-depth 3 --out .context/mimisbrunnr-saga-dossier/scratch/bundle.json

# 4. Optional: write your judgements — which memories restate each other, what conflicts, what is
#    missing — to .context/mimisbrunnr-saga-dossier/scratch/judgements.json (shape in SKILL.md).
#    Skip it and the findings carry no gaps, contradictions or merges. Tag near-misses are not written
#    here: they come only from the shared helper's evidence, passed as --near-miss-evidence.

# 5. Turn that bundle into a readable document, angled for an architecture write-up.
#    Skipped step 4? Drop the --judgements line — there is no file for it to read.
python3 -B .agents/skills/mimisbrunnr-saga-dossier/scripts/dossier_composer.py \
  compose --bundle .context/mimisbrunnr-saga-dossier/scratch/bundle.json \
  --judgements .context/mimisbrunnr-saga-dossier/scratch/judgements.json \
  --focus architecture --out .context/mimisbrunnr-saga-dossier/architecture.md

# 6. Done composing? Delete the scratch files, even if something failed along the way.
rm -rf .context/mimisbrunnr-saga-dossier/scratch
```

Steps 1 and 3 each cost a request against the store; step 5 is free to re-run as many times as you like
against the same saved bundle — try a few focuses on one bundle without re-asking the store. If the
store changed between steps 1 and 3, the bundle's `manifest.selection` will not match the approved
preview's `selection`: preview again and re-approve rather than composing a scope nobody approved.

`--out` must be a gitignored path, for `bundle` and `compose` alike — both carry store content, so a
tracked or un-ignored destination (a `README.md`, say) is refused, as is a path outside a git checkout,
and the file is written readable by you only. Leave `--out` off to print to stdout instead. `--asof YYYY-MM-DD` composes lifecycle and staleness as of that date; a date
that does not parse is refused rather than silently read as today.

The read token and base URL are seeded from the machine credential file (`~/.mimisbrunnr/credentials`)
at import, so the skill works from any checkout with nothing to provision per repo. The bundle stays
read-only: a write token present refuses to run.

## Focus: reading the same material for a different purpose

A focus changes what gets emphasized and in what order — it never changes *what's in* the document.
Material a focus doesn't lead with is not reordered into the document — it is listed as omitted with the
reason `outside-focus`.

| Focus | Leads with |
|---|---|
| _(none — default)_ | A balanced read, no lens applied |
| `requirements` | What was asked for, and why |
| `architecture` | Why the shape is what it is — decisions made, alternatives rejected, boundaries drawn |
| `specification` | What must observably be true — acceptance criteria, behaviours, interfaces |
| `implementation` | What building it actually needs — contracts, sequencing, edge cases |
| `review` | The findings — gaps and contradictions come first, narrative second |

## What you get back

- **The dossier** — an ordered document. Every substantive statement is cited to a specific memory's
  identity, version and capture time, so you can go verify anything that matters.
- **Findings** — a short, bounded list of what's wrong with the material itself. The composer derives
  `no-links-in-slice`, `unattributed`, `stale`, `superseded-still-referenced`, `weak-summary`,
  `provenance-cycle` and `equivalence-uncertain` on its own; `gap` and `contradiction` are the semantic
  judgement you supply in step 4's judgements file (see `SKILL.md`). Compose without it and the dossier
  reports neither — an empty result for those two then means "not examined", not "none found".
  `near-miss-tag` appears only from the shared helper's evidence (`--near-miss-evidence`).
- **A reconciliation line** — present + consolidated + omitted always adds up to what the bundle actually
  contained. If something's missing from the document, that line is where you'd catch it — and if the
  arithmetic would not add up, compose stops with an error instead of handing you the document.

## Guarantees worth knowing about

- **It never writes anything, anywhere.** Not to the store, not a version, not a label — this is
  structural, not a rule the skill has to remember to follow.
- **A contradiction is reported, never quietly resolved.** No "the newer one must be right" — you see
  both claims and decide, unless the store itself states which one has authority.
- **Nothing is silently truncated or dropped.** Cut for size, hidden by scope, or outside the current
  focus — every omission is listed with which of those it was.
- **Every merge keeps its sources.** Two memories that say the same thing get folded into one line in
  the document, but both originals are still named — never presented as if one fact was independently
  confirmed twice.

## Test

```bash
python3 -B .agents/skills/mimisbrunnr-saga-dossier/tests/run_tests.py
```

## The rest

- **`SKILL.md`** — the full invocation contract, written for the agent rather than for you.
- **`AGENTS.md`** (in this folder) — the composition rules and the design decisions behind them, if
  you're changing the skill itself.
- **`docs/hlds/005-contextual-export/`** — the design: why a dossier exists, the determinism boundary
  between bundle and dossier, the full findings taxonomy.
- **`.agents/skills/mimisbrunnr-odin-context-memory/`** — the capture skill and the only path in this skill
  set that can write. This skill only ever reads.
- **`.agents/skills/mimisbrunnr-kvasir-understanding/`** — the sibling that loads and imports Understandings;
  this skill selects and composes them alongside every other kind of memory, but doesn't load or write
  them.
