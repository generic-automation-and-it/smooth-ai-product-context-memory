# ai-understanding

There's a gap between a chat and the repo: things the AI worked out with you that aren't ready to be a
rule, a skill or a doc — you don't have the time, or enough evidence, to promote them yet. Today they die
with the session.

This skill writes them down as small markdown files — what was asked, what came of it, what got learned
along the way. No database, no service, nothing to run: a folder you can read in any editor.

Written for the next agent as much as the next person, so picking up the branch next week doesn't start
from zero. A focus on outcomes — not piles of AI chatter and slop. Terse outcomes, decisions and results
that aren't already in the code or an `AGENTS.md`.

## What it is not

| Not this | Why |
|---|---|
| A session log | Nobody reads a transcript. An Understanding is what remains once the session is thrown away |
| Documentation of the code | If the code shows it, read the code |
| Somewhere to put anything unfiled | If it belongs in an `AGENTS.md` or a rule, it goes there. This is the residue after those have taken what is theirs |

## Where things live

```
.context/understandings/          # gitignored — local to your workspace, never committed
  INDEX.md                        # generated: one row per Understanding, with the question it answers
  <subject>-<yyyyMMdd-HHmm>/      # one export run
    <slug>.understanding.md       # one Understanding
```

A file is a **question and its answer**, plus why it holds and where it stops applying. An agent reads
`INDEX.md`, matches a question against what it is about to do, and opens only what matched.

## Using it

```
/ai-understanding                 # analyse this session and write what is worth keeping
/ai-understanding wiremock stubs  # same, focused — names the subject and leads with those units
/ai-understanding --import        # load what matches the task you are starting
/ai-understanding --review        # what is stale, contested, or nobody ever used
```

A focus narrows nothing on its own: anything else worth keeping is still offered, so you cut it rather
than never seeing it. Best asked for at the end of a piece of work, before you clear or compact the
session — that is the moment the thinking still exists and is about to stop existing.

`--review` prints each flagged unit's own re-check command where it has one, so a stale unit comes with
the way to confirm it rather than just a warning.

Ask for `--export --all` when you want the complete dump. It skips the "which of these should I write?"
question *and* tells the agent to hold its own bar loosely — a marginal one gets written rather than
dropped, so you prune afterwards instead of beforehand.

Sharing is sending someone a zip. The store itself stays local and gitignored — it never travels through
the repository. Ask for `--publish` (add `--portable-only` to drop anything true only of this repo) and
you get an archive under `.context/understandings-publish/` to hand over however you like; they take it
with `--consume <zip>`. It unpacks into their store, where they read it in any editor and their agent
reads it the same way yours does.

## The rest

- **`SKILL.md`** — the full contract, written for the agent rather than for you.
- **`.agents/rules/meta/understandings.instructions.md`** — governance: how Understandings differ from
  rules and from `AGENTS.md`, and which wins when they disagree. Scoped to `**/*understanding.md`, so it
  attaches when an Understanding is opened rather than loading every session; `SKILL.md` points to it at
  the export decisions it governs.
- **`AGENTS.md`** (in this folder) — the design decisions and why, if you are changing the skill itself.

The name is borrowed from Adrian Tchaikovsky's _Children of Time_, where an Understanding is knowledge
distilled and handed to a generation that never had the experience which produced it. That is the
contract: the session is discarded, the transferable part survives.
