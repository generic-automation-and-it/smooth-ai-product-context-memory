# Viewer Console — High-Level Design

| | |
|---|---|
| **Status** | In Draft |
| **Owner** | generik0 |
| **Tracker** | Viewer Console |
| **Business authority** | [BRD-004 — Mímisbrunnr Console](../../brd/004-viewer-console/) (`BR-46` … `BR-53`), extending [BRD-001](../../brd/001-context-memory/) |
| **Last updated** | 2026-10-13 |

> Discovery / prototyping HLD. Delivers **intent + spec** — what we are building and why, the decisions
> behind it, and the quality bar it must meet. No implementation plan; execution is tracked in the
> issue/work tracker. UX, navigation, data view and usability are the design target; they are refined in
> a ten-pass loop over this document.

## Intent

The store is consumed by agents and the raw OpenAPI surface. The practitioner — the human whose
judgement the write path is built around — has no surface to see the corpus, read a memory's
provenance, view the graph, or apply judgement to a pending capture.

This design adds a **loopback human-facing Console** on top of the existing HTTP API. It is read-first
(browse, filter, detail, graph) and write-judgement-second (review queue, conflict menus, propose/approve),
where the write half drives the **existing capture pipeline** through a thin bridge rather than
reimplementing its redaction, atomicity or dedup logic.

The Console is a **separate pod fronted by Caddy**, on the same single-machine stack. Caddy gives one
loopback entry point, serves the static SPA, reverse-proxies `/api` to the Host, and injects the read
token so the browser never holds a credential. The write token is held server-side in the bridge and
exercised only on an explicit human approve — never ambient, matching the capability split.

## Vocabulary

| Term | Meaning |
|---|---|
| **Console** | The loopback human-facing surface (browse, detail, graph, management, recall feedback) |
| **Viewer pod** | The container running Caddy + the Console SPA + the bridge, on the existing stack |
| **Bridge** | The backend component that drives the capture pipeline and read endpoints for the SPA |
| **Capability split** | Read ambient, write never ambient (BRD-001); the Console honours it |
| **Review queue** | The capture path's pending proposals, conflict choices and human veto |
| **Bounded traversal** | The store's declared graph depth for `/paths` / `/tickets/paths`, not an unbounded walk |

## Key Goals

### 1. The corpus is visible, filterable, and drivable to the exact query semantics

The Console's browse view maps 1:1 onto the declared query fields — no client-side filtering after
materialisation (LADR-06). What the API narrows, the view narrows; facet any/all is a visible choice.

**DoD** — satisfies `BR-46`, `BR-47`

- List of cheap fields; scrollable within the retrieval limit.
- Filters: ticket (provider+key), initiative, repo, facet (any/all), tag, kind, status, group, free text, validity window.
- No post-hoc re-filtering that would defeat the server indexes.

### 2. Selecting an element reveals its full detail and provenance

The reader sees the statement, summary, sources, version history, validity, confidence, scope, and blob
body — with the blob fetched on demand and the material framed as evidence (LADR-07).

**DoD** — satisfies `BR-48`, `BR-49`

- Detail pane renders the cheap fields plus fetched blob on demand.
- Version history distinguishes current vs superseded.
- Recalled material carries a provenance/notice frame (RECALL_NOTICE convention), cited as data.

### 3. The graph is rendered, bounded, and honest about its depth

The graph view draws only what the store holds — typed links and ticket parent/child paths, within the
declared traversal depth (LADR-05). It is not a graph database and does not over-promise a rich network.

**DoD** — satisfies `BR-50`

- A bounded force-directed view of the **path chains** from the selected element (`FindPaths.Response`:
  each `PathResult` is a chain of `Hop`s ending at a `CheapMemory` endpoint).
- Edge types distinguishable and labelled (a hop's `Relation` + `Reason`); a legend explains them.
- Traversal bounded by the API's declared depth (`MaxDepth` 1–5); the UI cannot issue an unbounded walk.
- The ticket graph surfaces its `Disclosure` and `PathLimitReached` / `MemoryLimitReached` flags (BR-50
  honesty), not just the paths.

### 4. Management drives the capture pipeline, never reimplements it

The console surfaces the review queue and conflict-choice menus, and an approve re-runs the gate
sequence (redaction → atomicity → dedup → `set --dryrun` → write) through the bridge (LADR-03).

**DoD** — satisfies `BR-51`, `BR-52`

- A pending proposal is viewable with its surrounding memory context.
- A conflict choice is the same choice the capture path presents.
- The browser holds no write token; the write token is server-side and exercised only on approve.

### 5. Recall quality is visible

Never-recalled and miss-rate are rendered, with the miss-vs-absence distinction kept (LADR-08 ties
read path framing; BR-53).

**DoD** — satisfies `BR-53`

- The never-recalled and miss-rate surfaces are renderable.
- A miss is shown as a miss, absence as absence — never collapsed.

## Core Separation of Concerns

> The Console **drives** the pipeline; it does not replace it.

The API owns mechanics. The capture skill owns judgement on the write path. The Console is a **reader and
a presenter**: it reads via the API (and the recall-feedback surfaces) and presents the capture path's
review queue and conflict choices back to a human through a thin **bridge**. The bridge invokes the
existing capture scripts (redact, atomicity, dedup, value gate, `set --dryrun`), so the "sole writer"
invariant (HLD-002) and "no model in Application/Host" (HLD-005 §2.6) hold. Nothing in the Console makes
a judgement the skill does not already make.

## Guiding Principle — the human browses and approves; the pipeline judges

> A Console that bypasses a gate is a defect, not a convenience.

- **Read is ambient; write is gated.** The read token is injected client-side by Caddy; the write token
  is server-side and used only on an explicit human approve.
- **No reimplementation.** Redaction, atomicity, dedup and the value gate are the skill's; the bridge
  calls them. The browser renders their outcome.
- **Bounded and honest.** The graph shows what the store holds; the traversal is bounded. The Console
  does not turn a shallow, additive graph into a knowledge-graph product.
- **Read-first.** Phasing (P1 browse → P2 detail → P3 graph → P4 management) lets the value be observed
  before the write half is built.
- **We will deliberately not** add a model, add edit/delete, add multi-user, or make the write token
  ambient — each is a recorded boundary (BRD-004 §5).

---

## Integration & correctness

- **Same-origin, no CORS.** The SPA is served by Caddy and calls `/api` on the same origin, so no CORS
  configuration is needed. The SPA sends **no `Authorization` header** — Caddy injects the read token into
  the `/api` proxy; a client-supplied header is not required and must not override it.
- **The bridge is Python.** A small Python HTTP service in the viewer pod imports the capture skill client
  and reuses its redactor, atomicity detector, dedup and value gate as a library. No new toolchain beyond
  what the skills already require; the optional value-gate decision model stays in the skill layer (off by
  default), so it never lands in the Host or in bridge logic — the bridge invokes it, it does not contain it.
- **The bridge holds both tokens, the SPA holds none.** Read token (for any Host read the bridge does) and
  write token (for an approved capture) come from the pod's credentials. The SPA never holds either (LADR-02).
- **As-of is UTC.** `AsOf` must be a UTC instant with a `Z` or explicit offset (`InstantJson.Parse` rejects
  a naive string with a 400). The as-of date control serializes a UTC instant; validity windows are rendered
  in the user's local timezone but are sent/stored as UTC.
- **Loopback only.** Caddy binds localhost; the viewer pod is not network-addressable (NFR-01). A reachable
  Caddy would expose the injected read token, so the bind is part of the security model, not a convenience.

---

## UX Design

Refined across a ten-pass loop over navigation, information architecture, data view, usability and
polish. The Console is a single-page console; it is not a marketing site and not a report — it is a tool
the practitioner opens to see and judge the corpus.

### Navigation & information architecture

Three-level structure, deliberately flat and keyboard-reachable:

```
Mímisbrunnr Console
├── Browse            (default; the corpus — filters + results)
├── Graph             (the typed edges / ticket paths around a selected element)
├── Review            (the capture review queue + conflict choices)      [P4]
└── Status            (recall feedback: never-recalled, miss-rate)
```

- **One default view.** On open, land on Browse. No landing/splash; the first paint is the corpus.
- **A single nav rail (left or top), not a page tree.** The four views are peers. No multi-level menus;
  the store's model is flat (groups → memories → versions), so the nav is flat too.
- **View ↔ element coupling is explicit, not hidden.** Selecting a memory in Browse does not silently
  change the Graph view's root; the Graph has its own selector. This keeps "what am I looking at" honest.
- **Breadcrumb-lite, not full breadcrumbs.** A selected element shows its group / scope / ticket lineage
  as a compact pill row, not a navigational crumb stack.
- **Persistent state on the URL.** Filters and the selected element live in the query string
  (`?group=…&slot=…`), so a reload or a shareable loopback URL restores the view. This is the only
  state the SPA keeps; no server-side session.

*Navigation/IA is accepted when* a user can reach any of the four views in one click and know at all times
which view and which element they are in.

### Data view — the corpus list

The Browse view is a **dense table** of cheap fields, not a card wall. It is the fastest scan for a corpus
the practitioner wants to judge at a glance. A row is selected (not expanded inline); selection opens the
detail pane and, in the Graph view, can seed the graph root.

| Column | Source (`CheapMemory`) | Notes |
|---|---|---|
| **Subject** | `Name` | Primary identifier; truncated with full value on selection |
| **Kind** | `Kind` | Badge; `understanding` visually distinct |
| **Status** | `Status` | e.g. proposed / current; a proposed row is visually marked |
| **Version** | `Version` / `IsCurrent` | `v3` plus a "current/superseded" dot |
| **Validity** | `ValidFrom` … `ValidUntil` | `ValidUntil` null shown as "open"; a superseded row shows its replaced window |
| **Scope** | `ScopeDimension`:`ScopeIdentifier` | Compact pill |
| **Lineage** | `GroupUuid` / ticket | Ticket provider:key pill; group uuid on hover |
| **Created** | `CreatedOn` | Default sort |

- **Default sort: `CreatedOn` descending.** The newest capture is first, which is what a practitioner
  reviewing recent work wants.
- **Sort is on the returned rows, not a server sort.** The query API exposes no sort; sorting the bounded
  result set client-side is cheap (≤ Limit rows). No re-filtering is done client-side (LADR-06).
- **Column set is defaulted, not fixed.** A column picker hides/shows columns; the default above is the
  starting set. The SPA is no-build, so a one-off `localStorage` column preference is fine.
- **The list is bounded by the retrieval limit.** The query API has `Limit` (default **50**, max
  `MaxLimit` **200**) and no cursor/offset. When the result set equals the limit, the view shows a
  **count + a "narrow your filters (ticket / initiative / repo / facet / kind)" hint**, and never silently
  truncates. This is an honest limit of the API, not a UI bug; a result set that exceeds the cap must be
  narrowed, not paged past.
- **No client-side aggregation.** The view shows what the API returned. Grouping by kind/dimension is
  offered only as a filter preset, never as a client-side rollup of rows the API did not return.

*Data view is accepted when* a row set is scannable, sortable, and honestly bounded, and the user always
sees why the list is this size.

### Filtering UX

The filter bar is the primary action on Browse, so it is **always visible**, not behind a drawer. Each
control maps 1:1 onto a declared query field (LADR-06); the UI translates controls into the request and
never re-filters the returned rows.

| Control | Query field | Behaviour |
|---|---|---|
| **Free text** | `Query` | Debounced (≈300 ms); a single box, one or two words, not a sentence (lexeme-AND) |
| **Facet** (multi-select) | `Facets` + `FacetMatchMode` | Structured categorical axis; a visible **any/all** toggle, not an implicit default |
| **Tag** (chip input) | `Tags` | Free labels; the "any-tag" safety net, so label drift does not break recall |
| **Kind** (select, auto-complete) | `Kind` | Open vocabulary; pre-fill from existing kinds; `understanding` is a first-class option |
| **Status** (select) | `Status` | proposed / current, plus the `IncludeProposed` toggle |
| **Ticket** (provider + key) | `TicketProvider` + `TicketKey` | **Paired** — both required together; provider auto-completes (e.g. `github`, `jira`). Matches the API validator |
| **Initiative** (select) | `InitiativeName` | Populated from `GET /initiatives` |
| **Repo** (select/free) | `Repo` | Populated from scope; free text allowed |
| **Group** (implicit) | `GroupUuid` | Set by selecting an element's group, shown as a pill, removable |
| **Validity** (toggle) | `CurrentOnly` / `AsOf` | Default current-only; an as-of date is a separate, explicit control |

- **Facet vs tag mental model, shown, not hidden.** Facets are the controlled structured axis (reliable
  cross-reference); tags are the forgiving free-label net. The UI separates them visually and never forces
  facet reuse — a tag is a first-class input, because the any-tag column is what absorbs label drift.
- **Active-filter summary as removable chips.** Each active filter renders as a chip with an `×`; a
  "clear all" action resets to default (current-only, default limit).
- **Default empty state = a useful query, not a blank.** With no filters, the view runs query
  `{ currentOnly: true, limit: default }` so the first paint is real rows, not an empty screen that must be
  explained.
- **Auto-apply, debounced for text.** Local tool, so instant apply is right; text is debounced to avoid
  chatter. No explicit Search button required, but a manual apply is available for the free-text box.
- **Ticket data-quality constraint, surfaced.** A ticket recorded as a **plain tag** (not through the
  ticket model) is invisible to the ticket filter. The UI cannot repair that; but the filter bar shows the
  ticket filter as distinct from a tag filter, so a mismatch is diagnosable rather than invisible.

*Filtering is accepted when* every declared filter is visibly reachable, the any/all facet choice is
explicit, and the active state is always summarised and removable.

### Graph view

A bounded force-directed view of the **path chains** returned by `/paths` and `/tickets/paths` (LADR-05).
`FindPaths` returns `PathResult(Depth, Hop[], Endpoint: CheapMemory)` — each path is a **chain of hops**
from the selected source, not a node/edge adjacency list. The Graph renders those chains; it does not
invent a neighbourhood the API did not walk.

- **A root is chosen explicitly.** The Graph has its own selector (search a memory or pick from Browse's
  selection); selecting in Browse does not silently re-root the Graph. `FindPaths.Request` takes a
  `SourceUuid` — the root is the source, required.
- **Bounded depth.** `MaxDepth` is required and validated 1–5 (`MemoryTraversalDefaults.MaxDepth = 5`); the
  default in the UI is 2. The SPA always sends it — there is no server-side default, so the UI cannot
  issue an unbounded walk by omission.
- **A node is a hop's uuid; a labelled node is one whose `CheapMemory` the response carries.** The selected
  source and each path's `Endpoint` arrive with a `CheapMemory` and are labelled. **Intermediate hops are
  uuid references** — the UI renders them as compact ids and fetches their `CheapMemory` **on click**, not
  in a burst (NFR-05), so a long chain never triggers N reads on render.
- **An edge is a hop: `SourceUuid → TargetUuid` with a `Relation` and a `Reason`.** Edge colour encodes
  `Relation` type; a legend maps colour → relation, and hovering/selecting an edge shows the `Reason`.
- **Edge-type filtering is server-side, not a client-side filter.** `FindPaths.Request` exposes `Relation`,
  `Direction`, `Kind`, `Status`, `TargetUuid`, `ScopeDimension`, `Limit` — so narrowing the graph by edge
  type is a new bounded request, not re-filtering a returned blob (LADR-06 applies to the graph too).
- **The ticket graph is a separate surface with its own honesty gates.** `/tickets/paths` returns
  `TicketTraversalResult(Paths, Items, Disclosure)` plus `PathLimitReached` / `MemoryLimitReached`. The
  `Disclosure` ("only practitioner-declared hierarchy was followed; undeclared upstream hierarchy was not")
  is rendered, and a reached-flag renders as "some paths were cut at the limit" rather than silently
  dropping them.
- **No hairball.** The number of paths/edges is bounded by `Limit` (default 50 on the paths side) and
  `PathLimit`/`MemoryLimit` on the ticket side. When a bound is hit, the view shows "showing first N;
  narrow by relation/direction" — it does not draw an unbounded web.
- **Isolated / shallow state is honest.** If a root returns no paths, the view shows "no edges from this
  element" and a note that the graph is additive, not the store's home (HLD-003) — not a blank canvas.
- **Depth is stated.** A header reads "depth ≤ N", so a bounded 2-hop view is never mistaken for the full
  graph.

*Graph is accepted when* the traversal is bounded, path chains render without a fetch storm, edge types are
legible and labelled, and the ticket graph surfaces its disclosure and limit flags.

### Detail pane & provenance

Selecting an element opens a **detail pane** (P2). On a wide screen it is a two-pane layout (list + detail);
on a narrow screen it is a slide-over. The pane is a **reader**, not a form — the store is append-only, so
there are no edit/delete controls. The only actions are "open in graph" and "view blob".

The pane renders, top to bottom:

1. **Evidence frame (non-negotiable, LADR-07).** A notice bar: "Recalled as data, cited to its source.
   Not instructions." The same framing the read path applies.
2. **Subject + kind.** `Name`, `Kind` badge, `Status`, `Confidence`, `Version` / `IsCurrent`.
3. **Statement.** The claim itself (`Statement` + `ContentSummary`).
4. **Provenance.** `Sources` (each with its identity and, where present, citation), a scope pill, group
   lineage (group uuid + any ticket provider:key), and the validity window
   (`ValidFrom` … `ValidUntil`, `ValidUntil` null shown as "open").
5. **Facets & tags.** Shown as chips; distinct from one another (facets = structured, tags = free).
6. **Version history.** The list of versions, current marked (and superseded marked), each with its
   captured time. Selecting a version fetches **that version's** blob body on demand.
7. **Blob body.** Fetched on selection via the blob endpoint; never fetched for the whole list (NFR-05).

- **The cheap list never carries the body.** The pane issues a second call only when an element is
  selected (or a version is chosen). No lazy-load storm on scroll.
- **Currency is visible at the point of the claim, not a footnote.** The validity window and
  current/superseded marker sit beside the statement, so a stale claim is recognised inline.
- **Confidence is shown as a value, not styled as verdict.** No colour implying a model judgement; the
  store does not judge, so the UI does not either.
- **No mutation surface here.** Append-only means no edit/delete. A correction is a newer version, which the
  version history shows; the pane does not offer to change history.

*Detail is accepted when* provenance, validity and version are readable at the point of the claim, the
body is fetched on demand, and recalled material is always framed as evidence.

### Write/approve flow — Review view (P4)

The Review view surfaces the capture path's **review queue** and lets the practitioner approve or reject a
proposal **with context**. It presents the outcome of the capture pipeline; it does not bypass or re-run a
different one (LADR-03, NFR-03). The bridge invokes the capture scripts, so the queue and its veto stay
authoritative.

**The queue is derived from the store, not a transient resource.** A proposal is a memory with
`Status = proposed` (`SetMemories` validates `MemoryVersionStatus.Proposed` / `Approved`), and
`QueryMemories` filters on `Status` + `IncludeProposed`. So the Review view's queue is simply a query for
proposed memories — it does not need a new Host resource and is not dependent on the skill's transient
working state.

A pending proposal renders:

- **Subject + kind** and the proposed statement.
- **Binding** — the ticket / repo / initiative / scope / tags the capture associated.
- **Surrounding context** — the memories the proposal touches (by subject, facet or tag), shown beside it,
  because judging a capture needs the surrounding record (BR-51).
- **Conflict-choice menu.** When dedup flags that the candidate paraphrases or relates to an existing
  memory, the same choice the capture path presents is shown — e.g. *new version / new memory / skip* —
  with the existing memory's subject and statement displayed alongside so the choice is informed.
- **Dry-run digest.** Before an approve is possible, the `set --dryrun` digest is shown: what would be
  written (create vs version-bump), any links, and any label proposals. This is the human veto point.

The two actions:

- **Approve** — re-runs the full gate sequence (redaction → atomicity → dedup → `set --dryrun` → write)
  via the bridge, then performs the write. The write token is exercised here and only here (NFR-02).
- **Reject** — records the decision without writing; no write-capable request is issued.

- **No write on open.** Opening Review does no write. The digest and the approve action are separated, so
  a mis-click cannot write.
- **Approve requires the digest to be seen after any change.** If the practitioner amends the subject or
  statement (client-side compose), the digest is re-rendered from the amended content and the gates re-run
  — never a write against stale content.
- **The veto is structural.** There is no "write immediately" path; the only write-capable trigger is the
  approve action, which always shows the digest first.
- **Conflict choices are reproduced, not stored.** The conflict-choice menus and the `set --dryrun` digest
  are capture-time judgements. The bridge **re-runs the capture pipeline's dedup** on the selected proposal
  to reproduce them; they are not persisted as a separate resource. This is what keeps the gate
  single-source (LADR-03).
- **P4 dependency — the approve transition.** An approve must move the proposal from `proposed` to
  `approved`. Whether the store exposes that as an explicit transition, or it is a `set` version-bump with
  `Status = approved` on the same subject, must be confirmed before P4 builds. If neither exists, P4 needs
  a store capability — flag, don't assume.

*Approve flow is accepted when* approving re-runs the capture gates, the digest is shown before any write,
and the browser holds no write token.

### Empty, loading, error and offline states

The read path distinguishes three recall outcomes and **never collapses them** (HLD-004 "miss vs
absence"). The Console must too. These are rendered as distinct states, never one generic "something went
wrong".

| State | How it looks | Action |
|---|---|---|
| **Empty** (store answered, no rows) | "No matches — widen your filters." | Not an error; no retry. Widen the filter bar or clear all |
| **Unreachable** (connection refused) | "The store is not reachable." | Operator action: check the stack is up. A retry |
| **Timed-out** (store hung) | "The store did not answer within <budget>." | Worth a retry; a bounded wait, not a hang |
| **Error** (4xx/5xx) | A readable line with the HTTP code | Show a hint, never a raw JSON dump |
| **Host down** (bridge/Caddy unavailable) | "The Console backend is not reachable." | Separate from a store-empty; a retry |

- **Loading is a skeleton, not a spinner alone.** The table renders skeleton rows so the layout is stable
  and the user can see where the data will land. No layout shift.
- **A bounded wait, not a hang.** The SPA respects a time budget on a request; past it, it shows the
  timed-out state with a retry. It does not spin forever (consistent with the read deadline idea, though
  the read-path budget itself is a separate improvement).
- **Miss vs absence stays distinct.** The Status view's *never-recalled* is a result about memories that
  have never returned (absence of recall); a Browse query returning no rows is a different thing. The
  Console does not render both as "nothing found."
- **No raw JSON on the surface.** Any error is rendered as a readable line; the payload is never dumped. A
  diagnostics toggle (opt-in) may show the raw response for a human, but it is hidden by default.
- **Empty is a prompt, not a failure.** The store answering empty is a normal outcome; the UI nudges the
  practitioner to widen filters or check the data quality (e.g. a ticket recorded as a plain tag).

*States are accepted when* empty, unreachable, timed-out and error are each distinct and actionable, and a
request never hangs without a way out.

### Accessibility & affordances

A single-user local tool still needs to be operable and legible. The Console is a tool the practitioner
opens while doing other work, so keyboard reach matters.

- **Full keyboard navigation.** A visible focus ring on every interactive element. Tab through the nav
  rail, the filter controls and the results; arrow keys move through rows; Enter selects; Esc closes the
  detail slide-over. `/` focuses the free-text filter. A "skip to results" affordance for screen readers.
- **Accessible names match the domain.** Each filter control's accessible name is the query field it maps
  to (so a screen reader says "initiative filter", not "dropdown"). The results table has proper
  `<th>` headers; the detail pane is a labelled region.
- **Not colour-only.** Edge types and kind badges encode meaning by colour **plus** a label or shape/line
  style, so they read without colour. Same for current vs superseded (hollow vs filled marker).
- **Contrast meets WCAG AA.** Text on the surface and in badges has sufficient contrast; a dark and a
  light theme are both supported, chosen by the user, not the corpus.
- **Affordances are explicit.** A row is visibly clickable (hover + focus state, not just a cursor).
  A filter chip has an obvious `×`, and the "remove" affordance is consistent. A filtered control is
  distinguishable from an unfiltered one.
- **Tooltips explain the jargon, not everything.** Tooltips (or an inline help affordance) explain only
  the domain-specific terms a practitioner may not know: *superseded*, *valid_until*, *facet vs tag*,
  *as-of*, and the paired ticket provider/key requirement. No help text on plain controls.
- **Reduced motion.** The force graph offers a static layout (no continuous animation); a
  prefers-reduced-motion user gets pan/zoom without auto-animation.

*Accessibility is accepted when* every action is keyboard-reachable, no meaning relies on colour alone,
and domain jargon is explained where it matters.

### Performance & large corpus

The Console is read-light and bounded. The heavy path is the bridge on an approve (the capture pipeline),
not the read views. The design keeps read views responsive as the corpus grows.

- **The browse is bounded by the retrieval limit, and the UI says so.** No cursor/offset in the query API,
  so a huge corpus is narrowed by filters and facets, not by paging. When the result set hits the limit,
  the view shows the count and a "narrow your filters" hint (already in the data view). This is an honest
  bound, not a truncation to hide.
- **Lazy, on-demand blob fetch (NFR-05).** Bodies are fetched only when an element or version is selected;
  never a scroll-time storm.
- **No re-query on keystroke.** Free text is debounced (~300 ms); filter changes apply a single request, not
  a burst. Requests for the same criteria are coalesced.
- **Virtualise only if a larger limit is ever exposed.** A bounded result (≤ `MaxLimit` 200) renders as a
  plain table; virtualisation is a fallback if the browse limit is raised, not a default.
- **The graph stays bounded (NFR-05).** Depth ≤ `MaxDepth` (5); a path/edge bound prevents a hairball; the
  "showing first N" affordance avoids drawing an entire neighbourhood. Edge-type narrowing is a new bounded
  request, not client-side trimming.
- **First paint is one query.** On open, a single current-only query runs; no parallel request storm that
  would make the first paint slow or janky.
- **Recall-feedback surfaces are capped and honest.** `never-recalled` and `miss-rate` have their own
  limits; the UI honours the store's bound and states when it is hit. `miss-rate` is a time-window
  aggregate — render it as a small metric, not a row of the corpus.
- **The view never fans out to the bridge on open.** Only the approve action reaches the bridge; browsing
  and graph reads go through Caddy to the read API.

*Performance is accepted when* a populated corpus opens in a single query, blobs fetch on demand, the graph
stays bounded, and no read view does work proportional to the whole store.

### Consistency & polish

Final pass. The Console is a **tool**, so the wording is plain and the vocabulary is the store's, not
invented. The labels match the API and the glossary so a practitioner who reads the docs reads the same
words in the UI.

| UI label | Store term | Note |
|---|---|---|
| **Memory** | memory | The store's unit; do not call it "record" or "entry" |
| **Understanding** | `kind = understanding` | A first-class kind, visually distinct from a scoped memory |
| **Facet** · **Tag** | `Facets` · `Tags` | Structured axis vs free-label net, shown as distinct |
| **Ticket** | ticket (provider:key) | The ticket model, not a tag. Never call a ticket a "label" |
| **Initiative** · **Repo** · **Scope** | same | Match the query fields exactly |
| **Superseded** · **Version** · **Validity** | version semantics | The append-only model's own terms |
| **Sources** | `Sources` | Provenance; shown adjacent to the claim |
| **Recalled as data** | RECALL_NOTICE | The read path's evidence frame, repeated in the detail pane |

- **One word per concept.** No synonyms across views — the Browse, Graph, Review and Status views use the
  same names for the same fields, so a filter on one view is a filter on the others.
- **Tone is plain, not clever.** No marketing copy, no emoji, no casual phrasing. The Console states what
  it shows and what the user can do.
- **Redundant-to-glossary terms are not redefined.** The UI does not introduce a competing word for
  "scope", "kind", or "validity"; it uses the store's.
- **Cross-view consistency of filters.** A filter set applied on Browse is offered identically as a Graph
  root selector and a Review context filter; the same control set appears in each view, so the mental model
  carries over.

The ten-pass refinement loop is complete: navigation/IA → data view → filtering → graph → detail/provenance →
write/approve → states → accessibility → performance → consistency. Each pass tightened one facet; the
design above is the consolidated target. The next refinement should come from **using** the Console — the
read-first phasing (LADR-08) is specifically so the value is observed before the write half is built.

---

## Diagrams

- [System Context (C1)](./diagrams/c4-context.md)
- [Flow — view vs approve](./diagrams/flow-view-and-approve.md)

## Architecture Decisions (LADRs)

See [`./ladrs/`](./ladrs/).

| LADR | Decision | Status |
|------|----------|--------|
| [LADR-01](./ladrs/LADR-01-separate-pod-caddy.md) | Viewer is a separate pod fronted by Caddy, not served by the Host | Draft |
| [LADR-02](./ladrs/LADR-02-read-ambient-write-never.md) | Browser holds no token; Caddy injects the read token; write token server-side only | Draft |
| [LADR-03](./ladrs/LADR-03-drive-capture-pipeline.md) | The Console drives the existing capture pipeline via a bridge; never reimplements the gates | Draft |
| [LADR-04](./ladrs/LADR-04-no-build-spa.md) | The SPA is static no-build (vendored libs), served by Caddy; no Node toolchain | Draft |
| [LADR-05](./ladrs/LADR-05-bounded-graph.md) | The graph view renders `/paths` + `/tickets/paths` with bounded traversal, not a graph DB | Draft |
| [LADR-06](./ladrs/LADR-06-query-1-to-1.md) | Browse filters map 1:1 onto the query API fields; no client-side re-filtering | Draft |
| [LADR-07](./ladrs/LADR-07-evidence-framing.md) | Rendered material carries the evidence/notice frame (RECALL_NOTICE), cited as data | Draft |
| [LADR-08](./ladrs/LADR-08-read-first-phasing.md) | Phased: P1 browse → P2 detail → P3 graph → P4 management, value measured before the write half | Draft |
| [LADR-09](./ladrs/LADR-09-python-bridge.md) | The bridge is a Python service that imports the capture client and reuses its gates | Draft |
| [LADR-10](./ladrs/LADR-10-queue-derived-and-conflicts-reproduced.md) | The review queue is derived from the store (proposed status); conflict choices and the dry-run digest are reproduced by re-running dedup | Draft |

## Non-Functional Requirements

See [`./nfrs/`](./nfrs/).

| NFR | Attribute | Target (summary) | Status |
|-----|-----------|------------------|--------|
| [NFR-01](./nfrs/NFR-01-loopback-only.md) | Boundary | Console binds loopback only; no network exposure | Draft |
| [NFR-02](./nfrs/NFR-02-write-non-ambient.md) | Security | Browser never holds a write token; no write without an explicit approve | Draft |
| [NFR-03](./nfrs/NFR-03-gate-integrity.md) | Integrity | Zero change to the capture gates; approve re-runs them | Draft |
| [NFR-04](./nfrs/NFR-04-no-credential-in-log.md) | Secrets | Read/write tokens never reach a log, span attribute or committed artifact | Draft |
| [NFR-05](./nfrs/NFR-05-bounded-performance.md) | Performance | Bounded traversal, lazy blob fetch, no work proportional to the whole corpus | Draft |
| [NFR-06](./nfrs/NFR-06-recall-feedback-integrity.md) | Integrity | Recall feedback recorded exactly as the read path records it, never duplicated | Draft |

## Known gaps & assumptions to verify

These are the open points found in the gap/bug/architecture loop; each must be confirmed before the
relevant phase is built. They are stated so the design does not silently assume them.

| # | Gap | Where it bites | Resolution needed |
|---|---|---|---|
| **G1** | **Are proposals persisted with `Status = proposed`?** `SetMemories` validates `Proposed`/`Approved`, so a proposal *can* be stored proposed; PR #208 kept the review queue client-side ("no Host/API change"). If the capture path holds proposals client-side rather than persisting them, the queue is **not** in the store and a `Status = proposed` query returns nothing. | P4 Review queue (LADR-10) | Confirm whether the capture path writes proposals with `Status = proposed`. If not, P4 needs either a store write on propose, or the bridge must read the skill's queue state. This is the biggest open dependency |
| **G2** | **Approve transition.** An approve moves a proposal proposed → approved. Whether the store exposes that as an explicit transition, or as a `set` version-bump with `Status = approved`, is unconfirmed. | P4 approve action | Confirm the transition path before P4 builds |
| **G3** | **FindPaths node labelling.** `PathResult` carries a `CheapMemory` for the endpoint, but intermediate hops are uuid references. The start node is the selected `CheapMemory`. Mid-chain labels must be fetched on click (NFR-05). | P3 graph | Confirm the start node's `CheapMemory` is always available from the selection (it is), and that no render-time N-fetch is introduced |
| **G4** | **Initiative / label list endpoints.** `GET /initiatives` → `InitiativeRow(Name, Description, Status)`; `GET /labels` → `LabelRow(Name, Status, Uses)`. These serve the filter dropdowns. Confirm they are unpaginated and the shape is stable. | P1 filter bar | No action if stable; note pagination if the corpus grows |
| **G5** | **Recall-feedback limits.** `never-recalled` defaults to a limit that may exceed the query `MaxLimit` (200). Confirm the recall-feedback endpoint's own bound, and that the UI honours it rather than assuming the browse cap. | Status view | Clamp the UI to the recall-feedback endpoint's declared bound |

## Migration Plans

- **No store migration is required.** The Console is additive: it reads and presents. It does not
  change the models, the version semantics, or the write path.
- **The Host image stays pure.** No UI bundle is added to the API container; the viewer pod carries the
  SPA and the bridge.
- **The capture "sole writer" invariant is preserved.** The bridge calls the capture scripts; it does not
  become a writer.
- **No `Memory` → `Console` rename.** Memory is the correct name; the Console is a surface over it.
