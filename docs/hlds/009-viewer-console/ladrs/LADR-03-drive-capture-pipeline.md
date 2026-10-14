# LADR-03 — The Console drives the capture pipeline; never reimplements the gates

## Context

The capture path gates (redaction, atomicity, dedup, value gate, `set --dryrun` veto) are client-side
Python in the capture skill. Reimplementing them in the browser would create two sources of truth and
drift, and would risk a gate being bypassed.

## Decision

The Console presents the capture path's **outcome** — the review queue, the conflict-choice menus, the
`set --dryrun` digest — and an approve **re-runs the gate sequence** through a thin **bridge** that
invokes the existing capture scripts. The bridge never becomes a writer and never makes a judgement the
skill does not already make.

## Consequences

- No divergence; redaction/atomicity/dedup stay single-source.
- An approve cost the full gate sequence, never a direct write.
- The bridge needs access to the capture scripts and the write token (server-side).

## Status

Draft
