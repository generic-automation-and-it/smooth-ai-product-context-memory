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

    G --> H{"Preview only?"}
    H -->|"yes — no bodies, no composition (NFR-03)"| Z["Return preview<br/>effective selection · cost · limits<br/>practitioner proceeds, narrows or cancels"]

    subgraph judgement["Judgement — dossier skill, model, not reproducible"]
        H -->|"no"| I["Order: topological over supersedes · depends_on · implements<br/>stated tiebreak, cycles reported (LADR-07)"]
        I --> J["Consolidate equivalent claims<br/>meaning + applicability + lifecycle must match<br/>every origin retained, never counted as corroboration (LADR-05)"]
        J --> K["Mark lifecycle: current · proposed · superseded · stale · unknown<br/>preserve conditions and exceptions (NFR-07)<br/>cite memory · version · capture time (NFR-05)"]
        K --> L["Derive findings<br/>bounded taxonomy, each with basis + scope (LADR-13, NFR-04)"]
        L --> M["Reconcile:<br/>present + consolidated + omitted-with-reason == bundle count"]
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
| Findings (NFR-04) | Judgement | A gap is an unanswered question against the task, an included claim or a stated expectation (`BR-27`) — none of which is a predicate |
| Fidelity (NFR-07) | Judgement | Whether a condition still bounds a claim after rewriting is a semantic property, not a count |

## Implemented and accepted ticket reach

Delivered widening travels **memory to memory**. LADR-09 now resolves separate captured ticket
hierarchy under HLD-003 LADR-08; HLD-002 LADR-08 resolves its practitioner-declared writer.
Ticket implementation and release gates are accepted against final HLD-003 evidence.
Tag synonyms stay blocked; generic ticket blockers are not approved.

```mermaid
flowchart LR
    T["Ticket anchor"] -.->|"resolved relationally"| M1["Memory"]
    G["Tag anchor"] -.->|"exact array matching"| M1
    M1 -->|"depends_on"| M2["Memory"]
    M2 -->|"supersedes"| M3["Memory"]
    T ==>|"IMPLEMENTED AND ACCEPTED<br/>declared parent -> child, LADR-09"| T2["Child ticket"]
    T2 -.->|"live JSONB owner join<br/>not a membership edge"| M4["Current non-proposed memories"]
    G ==>|"BLOCKED — no tag identity<br/>LADR-10"| G2["Synonym tag"]
```

Arrows distinguish memory links, implemented and accepted ticket hierarchy, and blocked tags.
The separate ticket read requires depth 1..5 and exactly one live owner per ticket, gates every hop
with `HiddenDimensions`, drops whole hidden paths, and narrows returned memories through `Plan()`.
The read uses a Cypher anchor plus recursive SQL over indexed AGE adjacency; only selected capped
path endpoint groups and anchor contribute memories. Ticket identity grants no consent. Generic undeclared-upstream and
unverified-freshness disclosure plus visible-only cap flags qualify completeness without exposing
hidden IDs/counts (`BR-19`, `BR-30`). This diagram does not claim the full dossier is implemented.
