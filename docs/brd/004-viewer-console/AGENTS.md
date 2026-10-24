# AGENTS.md — Mímisbrunnr Console (BRD)

AI Context: BRD for the Viewer/management UI. Updated: 2026-10-13

## TL;DR

Business authority for a loopback human-facing Console on top of the existing store API — browse, filter,
detail, graph, recall feedback, and write judgment. The write half must drive the existing capture
pipeline, never reimplement its gates. [README.md](./README.md) continues the shared requirement space at
`BR-46` … `BR-53`; implementation owned by HLD 009.

## Non-Negotiables

- **Never reimplement a capture gate.** Redaction, atomicity, dedup and the value gate are the capture
  skill's. The Console surfaces their outcome; it does not re-encode them (BR-51, HLD-009 LADR-03).
- **A circuit that bypasses a gate is a defect.** Approving must re-run the full sequence (redaction →
  atomicity → dedup → `set --dryrun` → write), never write directly (BRD-004 §5, HLD-009 NFR-03).
- **The write token is never ambient.** The browser holds no credential. The write token is server-side
  and exercised only on an explicit human approve (BR-52, HLD-009 NFR-02).
- **Loopback only.** The Console adds a human surface, not a network-accessible one (BO-26, HLD-009 NFR-01).
- **No model inside the Console or its bridge.** The no-model-in-Application/Host rule stands; the bridge
  is a reader/presenter, not a judge.
- **No edit/delete.** Append-only versions; a correction is a newer version, not a mutation.
- **Read-first phasing.** Browse → detail → graph → management. The write half is built only after the
  read half shows real use (HLD-009 LADR-08).

## System Context

BRD-001 governs capture and recall. This BRD governs the human surface over it. HLD 009 owns the design
(separate viewer pod + Caddy, the write-path bridge, the data views).

## Key Behaviors

- **`Accepted when:` clauses are the contract.** The paragraph explains why; the clause is what a
  reviewer tests (BRD-004 §6).
- **Recalled material is framed as evidence, not instructions** (BR-49). A rendered memory carries the
  read path's RECALL_NOTICE frame; it never reads as authoritative orders.
- **A ticket recorded as a plain tag is invisible to the ticket filter** — a data-quality constraint the
  UI can surface but cannot repair (BR-47).
- **Read is cheap; write is gated.** Browse/detail/graph go through Caddy with the injected read token;
  only the approve action reaches a write-capable path.
- **Recall feedback is not duplicated.** The Console reads via the query path, which already records
  feedback; it does not write feedback itself (HLD-009 NFR-06).

## Migration Plans

- **No store migration.** The Console is additive; it reads and presents.
- **The Host image stays pure.** No UI bundle in the API container; the viewer pod carries the SPA + bridge.
- **No `Memory` → `Console` rename.** Memory is the correct name; the Console is a surface over it.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-10-13 | Created — BRD-004 for the Mímisbrunnr Console (Viewer/management UI). Continues the requirement space at `BR-46` … `BR-53`; implementation owned by HLD 009. | BRD-004 |
