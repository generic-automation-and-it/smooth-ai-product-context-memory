# LADR-07 — Rendered material carries the evidence/notice frame

## Context

The read path frames recalled material as cited historical data (RECALL_NOTICE), never as instructions. A
memory rendered in the Console is the same kind of material — it must not read as authoritative orders.

## Decision

Every rendered memory carries a provenance/notice frame and its source identity, matching the read path's
RECALL_NOTICE convention. Sources, validity and version are shown adjacent to the content, so a reader
sees *when and from where* a claim came, not just the claim.

## Consequences

- Consistency with the read path's untrusted-data framing.
- The reader can reason about currency and provenance inline.
- The frame is non-negotiable: no memory renders without its source and validity visible.

## Status

Draft
