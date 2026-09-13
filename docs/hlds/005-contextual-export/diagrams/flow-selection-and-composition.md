# Flow — selection, bundle, composition, findings

**Why this diagram exists:** the design's thesis is a boundary — deterministic selection on one side,
model judgement on the other (LADR-02). Everything downstream follows from where that line falls: which
guarantee is testable as byte-equality (NFR-02), where the cost lives (NFR-03), and why findings cannot
be produced server-side. A context diagram cannot show a line that runs *through* two components.

## The boundary

```mermaid
flowchart TD
    subgraph deterministic["Deterministic — API, no judgement, reproducible (NFR-02)"]
        A["Anchor set<br/>repo · initiative · ticket · tags<br/>+ required widening bound"]
        A --> B["Resolve anchors relationally<br/>indexes: repo btree, tag/facet GIN, ticket lookup"]
        B --> C["Widen over the graph from those memory identities<br/>bounded 1–5, relation-filtered"]
        C --> D{"Scope-gated at<br/>every vertex crossed"}
        D -->|"hidden hop"| D1["Drop the whole path<br/>never shorten it (NFR-01)"]
        D -->|"visible"| E["Collapse the mechanically identical<br/>same blob · same memory via several paths"]
        E --> F["Hydrate bodies from blob storage"]
        F --> G["Bundle + manifest<br/>selected · reached · cut · caps hit"]
    end

    G --> H{"Manifest only?"}
    H -->|"yes — no bodies, no composition (NFR-03)"| Z["Return manifest<br/>cost visible before it is paid"]

    subgraph judgement["Judgement — dossier skill, model, not reproducible"]
        H -->|"no"| I["Order: topological over supersedes · depends_on · implements<br/>stated tiebreak, cycles reported (LADR-07)"]
        I --> J["Collapse restatements<br/>every origin retained (LADR-05)"]
        J --> K["Mark superseded and stale<br/>cite memory · version · capture time (NFR-05)"]
        K --> L["Derive findings<br/>bounded taxonomy (NFR-04)"]
        L --> M["Reconcile:<br/>present + collapsed + omitted-with-reason == bundle count"]
        M --> N["Dossier artefact<br/>sensitivity banner, gitignored path"]
    end

    L -.->|"never written back — a finding is output (LADR-06)"| X(["Store unchanged (NFR-06)"])
```

## What the boundary buys

| Property | Side it lives on | Why it could not live on the other |
|---|---|---|
| Byte-identical results (NFR-02) | Deterministic | Judgement legitimately varies; requiring equality would forbid the judgement |
| Scope enforcement (NFR-01) | Deterministic | Must be pushed into the composed statement; a skill filtering afterwards has already received the material |
| Ordering rationale (LADR-07) | Deterministic rule, applied on the judgement side | The rule is mechanical and testable; what the document does with the ordered material is not |
| Contradiction detection (LADR-04) | Judgement | Most contradictions carry no edge; no predicate finds them |
| Cost estimate (NFR-03) | Deterministic | Must be answerable *without* paying the composition cost it estimates |
| Findings (NFR-04) | Judgement | A gap is an absence relative to an expectation, which is not a query |

## Interim reach, and what it excludes

The widening step travels **memory to memory only**. Anchors are resolved relationally and then left
behind — a ticket anchor cannot follow that ticket's blockers, and a tag anchor cannot follow that tag's
synonyms, because neither has a vertex and nothing writes such edges.

```mermaid
flowchart LR
    T["Ticket anchor"] -.->|"resolved relationally"| M1["Memory"]
    G["Tag anchor"] -.->|"array containment"| M1
    M1 -->|"depends_on"| M2["Memory"]
    M2 -->|"supersedes"| M3["Memory"]
    T ==>|"BLOCKED — no ticket vertex<br/>LADR-09"| T2["Related ticket"]
    G ==>|"BLOCKED — no tag identity<br/>LADR-10"| G2["Synonym tag"]
    M1 -.->|"BLOCKED — no writer<br/>LADR-11"| T
```

Solid arrows work today. The heavy arrows are the blocked prerequisites, and the manifest states that
they were not followed — so an export's completeness claim is qualified rather than overstated
(`BR-19`, `BR-30`).
