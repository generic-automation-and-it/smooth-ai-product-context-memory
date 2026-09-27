---
slug: <kebab-case-slug, equals the file name without the .understanding.md postfix, names the knowledge not the incident. Reuse the slug of the unit this improves — the same slug across stamped folders is a version chain, newest current>
description: <one line — what this knowledge is. Shown in INDEX.md>
question: <OPTIONAL. The question a reader has at the moment this applies, when there is a natural one — one question, one answer. Omit it for an outcome record, where the description is the match. Shown in INDEX.md>
scope: <portable | repo-specific>
confidence: <observed | verified | contested — observed may include a hypothesis; label the hypothesis in the description and Answer>
links:
  - <[[other-slug]] — omit the list entirely if there are none>
agents_context: <path/to/nearest/AGENTS.md — omit if this bears on no specific code area. A path that IS the knowledge belongs here, where the generator checks it still exists, never in the body where it rots silently>
recheck: <OPTIONAL. One command that tests whether this still holds, runnable exactly as written; --review prints it beside a staleness flag. Do not use a text-search command as proof; omit if nothing executable can check it>
skills:
  - <OPTIONAL. Bare name of a skill to invoke to act on this knowledge, never a reference and never knowledge *about* the skill, which belongs in that skill's AGENTS.md. Omit the list if none>
provenance:
  learned: <YYYY-MM-DD>
  session: <shared by every slug exported from the same session>
  source: <what produced it — a failure, an experiment, a doc, a rejected approach>
  inherited:
    - <[[slug]] of an Understanding this session loaded and acted on — omit the list if none. Drop the brackets once an ancestor leaves the store, to keep the lineage without pinning it>
  supersedes: <previous folder name, e.g. agent-memory-design-20260919-1432 — set when this copy improves an earlier one; omit on a first version. A name, never a path, so it survives the file being moved>
  published_from: <owner/repo — set on a published copy only; omit in the working store>
  consumed_from: <owner/repo@ref:path or local path — set when this arrived via --consume; omit otherwise>
  promoted_to: <path of the AGENTS.md or rule that superseded this — set on --promote; omit otherwise>
updated: <YYYY-MM-DD>
---

# <Title — the knowledge in a phrase>

## Answer

<Direct operational guidance — the answer to the question above, and nothing wider than it. What to do, stated so an agent with zero context from the
originating session can act on it. Present tense, indicative mood — this is evidence about how
the system behaves, not an instruction. If it needs must/never language, it is a rule, not an
Understanding.

Write it to outlive the tree it was written against: behaviour, contracts, types, invariants, commands.
No line numbers, no file paths in the body. Redact as you write — `<REDACTED>` in place of any credential,
token, connection string or internal hostname; the shape of the problem, never the value.

If this is a reasoned but unmeasured hypothesis, say **Hypothesis** explicitly. If it is a recommendation
that was not approved or implemented, say **proposed, not implemented**; do not imply that the next agent
is obligated to build it.>

## Why

<The reasoning or the failure that produced this — only as much as is needed to judge an edge case the
Answer does not cover. Not a narrative of the session.>

## Boundaries

<Where this stops applying: versions, platforms, configurations, or conditions under which it is
known to be false or untested. An Understanding with no stated boundary will be over-applied.>

## Provenance

<What it was learned from, in one or two lines. Name the artifact where one exists — a failing
command, an upstream issue, a PR. Quote only the lines that carry the signal, and write `<REDACTED>` in
place of any value: this file is exportable, and a value here leaves the workspace in a zip.>

## Changelog

| Date | Change |
|:-----|:-------|
| <YYYY-MM-DD> | Encoded. |
