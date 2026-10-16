# AGENTS.md — Viewer Console (HLD)

AI Context: HLD for the viewer/management UI. Updated: 2026-10-15

## TL;DR

A loopback human-facing Console over the store API. Separate viewer pod fronted by Caddy; the bridge
drives the capture pipeline rather than reimplementing it; the browser holds the session token (read or
write) and the Host enforces the capability;
[README.md](./README.md) is the design, with LADRs under [`./ladrs/`](./ladrs/) and NFRs under
[`./nfrs/`](./nfrs/). Business authority: BRD-004 (`BR-46` … `BR-53`).

## Non-Negotiables

- **The P4a bridge never drives a write; the P4b bridge writes only by driving the capture `set`
  pipeline (never a judge).** The "sole writer" invariant (HLD-002) and the no-model rule (HLD-005 §2.6)
  hold (LADR-03). Neither bridge reimplements a capture gate.
- **The Console is credential-gated.** The practitioner supplies a CONTEXT_TOKEN (read or write) at
  login; the SPA holds it for the session, Caddy forwards it to the Host, which enforces the capability
  (NFR-02). No token is ambient: the image carries none, and a read-only token can never write.
- **Loopback only** (NFR-01). Published on loopback only; the container binds all interfaces so the Aspire/DCP proxy can reach it.
- **Browse filters map 1:1 onto the query API** — no client-side re-filtering after materialisation
  (LADR-06). Filtering in the browser would defeat the server indexes.
- **The graph is bounded** — depth ≤ the store's declared max, with an edge cap (LADR-05, NFR-05). It is
  not a graph database; it must not over-promise a rich network.
- **An intermediate hop is a uuid, and one read labels it.** `/paths` returns uuid references for every
  hop but the endpoint, and `POST /query` has no uuid predicate, so `GET /api/context/memories/{uuid}` (current
  version, cheap fields, scope-gated) is what the graph calls — once per click, never per node at render.
- **No-build SPA** (LADR-04). Vendored libs, no Node toolchain, served by Caddy.

## System Context

```mermaid
C4Context
  Person(p, "Practitioner", "Browses the corpus")
  System(console, "Console", "SPA + bridge, loopback, fronted by Caddy")
  System(host, "Host API", "read/write capability")
  System_Ext(db, "Postgres + AGE")
  System_Ext(blob, "MinIO")
  Rel(p, console, "opening loopback URL")
  Rel(console, host, "/api + session bearer token")
  Rel(host, db, "queries")
  Rel(host, blob, "bodies")
```

## Architecture Decisions

The high-impact ones — see [`./ladrs/`](./ladrs/) for detail:

- **LADR-01** — separate pod fronted by Caddy (Host image stays pure).
- **LADR-02** — credential-gated console: the session's bearer token is forwarded to the Host, which is the capability authority.
- **LADR-03** — the bridge drives the capture pipeline; never reimplements the gates.
- **LADR-06** — browse filters map 1:1 onto the query fields.
- **LADR-08** — read-first phasing.
- **LADR-09** — the bridge is a Python service that imports the capture client and reuses its gates.
- **LADR-10** — the review queue is derived from the store (proposed status); conflict choices and the
  dry-run digest are reproduced by re-running dedup.
- **LADR-11** — the Console approve is a gated `set` version-bump with `Status=approved` on the existing
  `Uuid`; no separate transition endpoint, and no Host guard for `approved` (a fresh approved create is
  the capture path's normal shape; `proposed` is reserved for gate-held candidates).

## Key Behaviors

- **Approving re-runs the gates** (NFR-03): an approve is equivalent to the capture path's approve — same
  gates, same order, same veto. No write reaches the store the capture path would have refused.
- **The cheap list never carries the body.** Blobs fetch on demand when an element or version is selected
  (NFR-05).
- **Empty, unreachable and timed-out are distinct states.** The read path never collapses them; the UI
  must not either.
- **Recall feedback is not duplicated** by Console reads (NFR-06).
- **Facet any/all is an explicit control** matching the API's `FacetMatchMode` (LADR-06), and ticket
  provider/key are paired, matching the query validator.

## Migration Plans

- **No store migration.** The Console is additive.
- **The Host image stays pure.** The viewer pod carries the SPA and bridge; no UI in the API container.
- **No `Memory` → `Console` rename.** Memory is the correct name; the Console is a surface over it.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-10-16 | **UI fix batch: the review 502, group identity, and four filter improvements.** (a) The review queue is a *query*, so its limit is clamped to the query `MaxLimit` (200) — the old clamp used the recall-feedback `never-recalled` bound (5000), forwarded an over-cap limit, and the store's 400 surfaced as `502 store-refused` / "the bridge returned HTTP 502"; `#review-limit` offers 50/100/200 with 200 default. The queue also takes `?kind=`. (b) `CheapMemory` now carries the group's identity — `tickets` (**all** of them: a group accumulates tickets, so a single `ticketProvider`/`ticketKey` pair would have hidden the rest), `repository`, `initiativeName` — projected from the row the read path already loads; the browse Lineage column and the review binding chips render from it. (c) Repo is a combobox fed by the rows (free text kept); a **Product name** dropdown lists the registry from `GET /api/context/product-names` and sends `productName` (a 404 for an unregistered product shows the store's own wording, never a silent empty). (d) A tab-level **kind scope** (All/Memory/Understanding) in the topbar feeds browse, graph and review; it is a fixed scope because there is no distinct-kinds endpoint, and each pane keeps its own Kind combobox for the full vocabulary (the pane's own value wins). (e) The browse ticket pair is relabelled **System**/**Reference** with a tracker combobox — labels only; the wire still sends `ticketProvider`/`ticketKey`. (f) Selecting a memory offers a copy-able kvasir `import` command pre-filled with its bindings (`--all` when the kind is not `understanding`, since `import` reads that kind only); the Console copies the command and never runs it. New shared module `view-context.js` holds the scope and the query's product. | this PR |
| 2026-10-16 | **Credential-gated Console (HLD-009).** The SPA now asks for a CONTEXT_TOKEN at login — read grants browse/detail/graph/queue; the write token additionally enables Approve/Reject. The session token is forwarded by Caddy and the bridges to the Host, which is the capability authority (new `GET /api/context/capabilities` resolves the tier). Caddy no longer injects a read token and no token is ambient in the image (NFR-02); `_request` gained a per-call token; P4b starts without an ambient write token; a CSP is set on the served SPA. | this PR |
| 2026-10-15 | **G1 verified.** Held candidates now reach the store as `Status=proposed` through the gated capture path (preflight + `set --dryrun` veto + `set`); full candidate fields persisted; the queue is a `POST /query` filtered to proposed with `IncludeProposed=true`. Default `ExcludeProposed` keeps proposed rows out of browse/detail. DoD 1–4 met by `feat[233]`. | this PR |
| 2026-10-15 | **Phase P4a implemented (read-side review queue).** The bridge imports the capture client and `dedup` as libraries; it refuses to load if any write-token env var is present (NFR-02 defense in depth, in addition to the container not exporting one). New routes: `GET /bridge/review-queue?limit=500` (read-side, default `currentOnly: false` so a reviewer sees the full proposed history) and `POST /bridge/dedup-preview` (re-runs `dedup.stage` on a selected proposal; no write). No write route exists on the bridge; `set --dryrun`/approve/reject are P4b (gated on G2). Bridge log lines are start/finish + counts only (NFR-04). SPA Review pane: list of proposals with subject, kind, statement, binding (ticket/repo/initiative/scope/tags), version + status chips, evidence frame, and a "Conflicts" subsection rendered from `dedup-preview`. "Open in Browse" cross-nav. Approve/Reject are visible placeholders disabled until P4b. The queue query re-uses the read client; the dedup re-run re-uses the model gate (default off). | this PR |
| 2026-10-15 | **G2 closed — approve transition is a gated `set` version-bump.** No new Host endpoint: the bridge re-sends a `MemoryWrite` with `Status=approved` on the existing `Uuid` of the proposed memory, producing a new `MemoryVersion` (v2) via the existing `VersionExistingAsync` path. v1 (proposed) becomes superseded; v2 (approved) is current. Append-only preserved. **No validator special-case** — `Status` is validated as `proposed`/`approved` exactly as before, because a fresh approved create is the capture path's normal shape (`set_items` defaults status=approved on a create). `proposed` is reserved for gate-held candidates; the approve transition is just a version-bump with `Status=approved`, which the standard `set` path already accepts. A same-`Uuid` `approved`→`approved` bump with a different claim is a legitimate revision of an approved memory; a same-claim re-approve is a no-op via `MemoryClaim.Same`. The amend surface is a version-bump with `Status=proposed` on the same `Uuid` (P4b scope); LADR-11 added. | this PR |
| 2026-10-15 | **Phase P4b implemented (write path: approve, reject, amend).** New Python process `bridge_p4b.py` on port 5182 alongside the existing P4a process on 5181. NFR-02 satisfied by process isolation: the P4b process is the only one that may hold the write token, and it refuses to start without one; the P4a process refuses to start with one. Caddyfile routes `/bridge/approve`, `/bridge/reject`, `/bridge/amend`, `/bridge/p4b-health` to P4b; P4a routes stay on 5181. The capture client's `cmd_set` prints the response to stdout; the bridge subprocesses the capture client per write (the same isolation pattern `_fetch_blob` uses for reads) to keep the response out of the bridge's HTTP body. **Approve is two-phase on a single route** (P4b.2): first call returns a `set --dryrun` digest plus a SHA-256 fingerprint; second call (`confirm: true` + the same fingerprint) re-runs dryrun, refuses on fingerprint mismatch (digest-stale guard), and writes. **Amend** is a `set` version-bump with `Status=proposed` on the existing `Uuid`, producing v2 in the chain recorded in LADR-11. **Reject** records the decision to a configurable log file (`BRIDGE_P4B_REJECT_LOG`, default `/tmp/bridge_p4b_rejects.log`); no write-capable request is issued. The bridge never holds a credential value in a log line; reject-log writes are by `uuid` only. DoD 1–7 met by the new process. | this PR |
| 2026-10-15 | **Closed three plan-analysis findings without code.** **F2:** the viewer `Dockerfile` builds from the repo root and vendors the capture scripts to `/bridge/capture/` (verified: 13 files, no `__pycache__`, no `.agents` leak, all compile); root `.dockerignore` re-includes only that folder; LADR-09 records the mechanism. **F4:** kind autocomplete stays inferred from returned rows — no distinct-kinds endpoint (recorded deliberate in the README filter table). **F7:** NFR-01/LADR-01 corrected to the shipped topology (Caddy binds all container interfaces; loopback is enforced by the published port, not the bind). | this PR |
| 2026-10-15 | **Phase P3 implemented (graph).** The Graph pane draws the `/paths` path chains as a static force layout: a root chosen explicitly (its own uuid field, or "use the memory open in Browse" — selecting in Browse never re-roots it silently), `maxDepth` always sent (1–5, UI default 2) and stated in the header, edges deduped by hop identity and coloured **plus** dash-patterned, with a legend and per-edge `Reason` on hover/select and a text paths table (never colour-only). Intermediate hops render as compact ids and are labelled one at a time by clicking them — the new `GET /api/context/memories/{uuid}` read, added for exactly that because the query endpoint has no uuid predicate. Narrowing by relation/direction/kind/status/scope/limit issues a new bounded request, never a client-side trim; a hit limit renders "narrow by relation or direction", and a root with no paths renders "no edges from this element" plus the HLD-003 additive note. The ticket graph is a separate surface: paired provider/key, its own depth and path/memory limits, `Disclosure` rendered verbatim, and `depthLimitReached`/`pathLimitReached`/`memoryLimitReached` surfaced as "some paths were cut". No write path; the write token stays absent from the pod. | this PR |
| 2026-10-15 | **P3 gap G3 closed at the API level.** `GET /api/context/memories/{uuid}?scope=` returns the current version's cheap fields by identity (`GetMemory` slice, Read capability, scope-gated like the blob and version-history proxies, 404 on an unknown uuid). It is a read, not a migration; the graph hop label is its only consumer so far. | this PR |
| 2026-10-15 | **Phase P2 implemented (detail + provenance).** Selecting a browse row opens a reader pane (two-pane on wide / slide-over on narrow): the LADR-07 evidence frame, subject/kind/status/confidence/version, statement + summary, provenance (sources, scope pill, group, validity), distinct facet/tag chips, version history, and the blob body fetched on demand with `?scope=` passed on both the versions and blob reads so the row count matches the rows read. Selected element persists in the `?uuid` query string; Esc closes. No edit/delete. | PR #229 |
| 2026-10-15 | **Phase P1 implemented (browse + filters).** Browse pane gains the dense table (Subject/Kind/Status/Version/Validity/Scope/Lineage/Created), the 1:1 filter bar (debounced query, facets any/all, tags, kind/status, ticket pair, initiative, repo, group-pin, validity current/as-of, limit), removable active-filter chips + clear all, the honest-limit prompt, and distinct empty/unreachable/timed-out/error states. Status pane surfaces recall-feedback (never-recalled + miss-rate) with required date controls (G5 clamp). Filtering is server-side (LADR-06); the cheap list never carries a blob body (NFR-05). No Host/API change. | PR #229 |
| 2026-10-14 | **Phase A implemented (viewer pod foundation).** | PR #229 |
| 2026-10-13 | Gap/bug/architecture loop: corrected the browse limit to query defaults (50 / max 200); rewrote the graph view to the real `/paths` path-chain contract (each `PathResult` = hops ending at a `CheapMemory` endpoint; intermediate hops are uuid refs) and to `/tickets/paths`' `Disclosure` + limit flags; made the review queue an explicit store-derived query (`Status = proposed`) with conflict choices reproduced by re-running dedup; added LADR-09 (Python bridge) and LADR-10 (queue derived / conflicts reproduced); added a Known-gaps section (G1 proposal persistence, G2 approve transition, G3 node labelling, G4 initiative/label endpoints, G5 recall-feedback limits); added same-origin/no-CORS, as-of-UTC and loopback correctness notes. | HLD-009 |
| 2026-10-13 | Created — HLD-009 Viewer Console. Renumbered from an initial 008 to avoid colliding with the existing `008-keyword-search` (In Discovery). Draft; LADRs/NFRs all Draft. Business authority BRD-004. | HLD-009 |
