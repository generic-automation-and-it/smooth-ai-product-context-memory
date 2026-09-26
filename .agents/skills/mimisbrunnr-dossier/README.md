# mimisbrunnr-dossier

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
| A live query / `get` | That's `mimisbrunnr-context-memory`'s job — one question, a cited answer. This skill produces a standing document over a whole slice |
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
# 1. Ask the store for everything about this repo, following recorded links up to 3 hops out.
mkdir -p .context/mimisbrunnr-dossier
python3 -B .agents/skills/mimisbrunnr-dossier/scripts/dossier_composer.py \
  bundle --body '{"repo":"kingstown","widenDepth":3}' > .context/mimisbrunnr-dossier/bundle.json

# 2. Turn that bundle into a readable document, angled for an architecture write-up.
python3 -B .agents/skills/mimisbrunnr-dossier/scripts/dossier_composer.py \
  compose --bundle .context/mimisbrunnr-dossier/bundle.json --focus architecture \
  --out .context/mimisbrunnr-dossier/architecture.md
```

Step 1 costs a request against the store; step 2 is free to re-run as many times as you like against
the same saved bundle — try a few focuses on one bundle without re-asking the store.

## Focus: reading the same material for a different purpose

A focus changes what gets emphasized and in what order — it never changes *what's in* the document.
Anything a focus doesn't lead with is still there, just later, or listed as omitted with the reason
`outside-focus`.

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
  judgement you supply (see `SKILL.md`), so the two commands above report neither — an empty result for
  those two means "not examined", not "none found".
- **A reconciliation line** — present + merged + omitted always adds up to what the bundle actually
  contained. If something's missing from the document, that line is where you'd catch it.

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
python3 -B .agents/skills/mimisbrunnr-dossier/tests/run_tests.py
```

## The rest

- **`SKILL.md`** — the full invocation contract, written for the agent rather than for you.
- **`AGENTS.md`** (in this folder) — the composition rules and the design decisions behind them, if
  you're changing the skill itself.
- **`docs/hlds/005-contextual-export/`** — the design: why a dossier exists, the determinism boundary
  between bundle and dossier, the full findings taxonomy.
- **`.agents/skills/mimisbrunnr-context-memory/`** — the capture skill and the only thing in this
  system that can write. This skill only ever reads.
- **`.agents/skills/mimisbrunnr-understanding/`** — the sibling that loads and imports Understandings;
  this skill selects and composes them alongside every other kind of memory, but doesn't load or write
  them.
