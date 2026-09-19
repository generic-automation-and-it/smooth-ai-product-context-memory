# LADR-07: The current session dumps to a local folder for cross-session reuse

**Status:** Draft

## Context

BRD-003 BR-46 wants the understanding capability to share context across sessions and across
repositories by writing the current session's context to a local folder. This is a **session export**:
a projection of what this session holds, written so another session or repo can discover and load it.

The instinct is to write this into the store so it compounds. But that conflates two different needs.
A store write makes the material part of the corpus and needs the capture path's judgement. A session
dump is a lightweight, immediate, shareable artifact — the practitioner wants to hand this session's
context to another repo or another agent *now*, without waiting for the capture path or touching the
corpus.

The folder name must be discoverable by another agent from the output — the agent picks a fitting
folder name derived from the session, writes there, and reports it — so a future session can find the
folder by that name and load it.

## Decision

Add a `--currentsession` dump to the understanding skill. It writes the current session's context (its
Understandings, decisions and key learnings) to `.context/understandings/<folder>/`, where `<folder>`
is a fitting name derived from the session, chosen on output so another agent can discover it by name.
The dump:

- Writes a local Markdown projection of the session's understanding.
- Changes **nothing** in the store — it is an export, not a write (NFR-01).
- Is written to the gitignored `.context/` tree, so it is not committed.

Another session or repository can then **load** that folder via the same skill, regardless of whether
the originating repo is the same.

## Alternatives Considered

- **Write the session to the store instead** — rejected: conflates a shareable projection with a
  corpus capture; the dump is immediate and non-destructive, and a repo may not even be the store's.
- **A fixed folder name** — rejected: a per-session fitting name is what makes the folder discoverable
  and unambiguous across many sessions.
- **Commit it** — rejected: it is a projection, and the `.context/` tree is gitignored like the export
  output.

## Consequences

- Cross-session and cross-repo context sharing is as simple as dumping to a local folder and loading it
  from the other side.
- The dump is an export, so it is regenerable and never a source; the store is the record.
- The folder name is discoverable from the dump's output, so a follow-up session can locate it by name.

## Related

- **LADR-02** — the load side that consumes a dumped folder.
- **NFR-01** — a session dump is an export and writes nothing to the store.
- **BRD-003 BR-46**, **BRD-002** (§10 glossary "Session export").
