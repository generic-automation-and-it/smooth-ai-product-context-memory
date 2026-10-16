---
slug: <kebab-case-slug, equals the file name without the .memory.md postfix, names the knowledge not the incident. Reuse the slug of the memory this improves — the same slug across run folders is a version chain, newest current>
description: <one line, at least 12 characters — what this memory IS, not what happened>
question: <the question this answers; omit on an outcome record, which records what a piece of work produced>
scope: <portable only if it is true outside this repo, else repo-specific>
confidence: <observed unless you reproduced it, verified only if EVERY claim was checked>
updated: <YYYY-MM-DD>
agents_context: <repo-relative paths this was learned against — existence is checked at index time; never put a path in the body, prose legitimately names files>
recheck: <optional: one command that re-checks whether this still holds>
skills: <optional: bare names of skills an agent should invoke to act on this>
provenance:
  learned: <YYYY-MM-DD>
  session: <shared by every slug exported from the same session>
  source: <what produced it — a failure, an experiment, a doc, a rejected approach>
  inherited: <optional: block list of [[slugs]] this session read and acted on>
  supersedes: <previous run folder name, e.g. 20261016-1432-ses_abc — set when this copy improves an earlier one; omit on a first version. A name, never a path, so it survives the file being moved>
---

# <Question, as the question>

<The answer. Behaviour, contracts, types, invariants, commands — never a file path or line number.
A path that IS the knowledge goes in `agents_context`, not here.>

## Why

<1-3 lines. The reason it holds, which is what a future reader needs and the answer alone does
not say.>

## Boundaries

<Where this stops being true, and what would change it. Omit only when the memory cannot
mislead.>