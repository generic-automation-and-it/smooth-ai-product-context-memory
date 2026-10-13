# Diagrams — Keyword Search

## System Context (C1)

Keyword search is the recall front door of the well: one query string in, ranked
evidence out. It sits between agents/operators and the PostgreSQL index it never
bypasses; judgement stays outside (capture/recall skills), bodies stay in object storage.

```mermaid
C4Context
    title Keyword Search — System Context

    Person(agent, "Agent / Operator", "Issues keyword queries, judges candidates")
    System(kw, "Keyword Search", "Obsidian-like syntax over english FTS + rank + traverse")
    System_Ext(pg, "PostgreSQL + AGE", "GIN FTS indexes, relational filters, graph edges")
    System_Ext(skills, "Recall Skills", "odin capture judgement, kvasir understanding, dossier composer")
    System_Ext(feedback, "Recall Feedback", "HLD-004 hit/miss signal")

    Rel(agent, kw, "Queries", "HTTPS / read client")
    Rel(kw, pg, "Executes every predicate", "SQL, index-served")
    Rel(kw, feedback, "Emits hit/miss", "Internal")
    Rel(skills, kw, "Adjudicates candidates", "Out of band")
```

## Query Flow

Rationale: the parse→resolve→translate→rank→widen path is the design's load-bearing
seam (LADR-01); C1 cannot show it.

```mermaid
flowchart TD
    S[String query] --> P{Parser\nApplication, pure}
    P -->|malformed| R[Refuse\nstable reason]
    P -->|operator tree| C[Resolve scope/identity\nHandler]
    C --> Q[Criteria + query model]
    Q --> T{Translator\nInfrastructure}
    T --> F[FTS + filters\nindex-served SQL]
    F --> K[Rank ts_rank_cd\ntiebreak recency]
    K --> H[Snippet ts_headline\npost-LIMIT]
    H --> W{Traverse flag?}
    W -->|no| OUT[Ranked hits]
    W -->|yes| G[Bounded AGE widen\ndepth 1-5, scope-pushed]
    G --> OUT2[Anchors + endpoints\ndisclosed counts]
```

## Recall Sequence

Rationale: one composed round trip replacing ten client calls (LADR-05) needs its
participant contract explicit — especially scope enforcement on the widen leg.

```mermaid
sequenceDiagram
    participant A as Agent
    participant H as Query Handler
    participant P as Parser
    participant PG as Postgres + AGE
    participant F as Recall Feedback
    A->>H: keyword string + filters + depth + traverse flag
    H->>P: parse + validate
    P-->>H: operator tree / refusal
    H->>H: resolve group/ticket + scope plan
    H->>PG: criteria + tsquery + depth + scope (one round trip)
    PG-->>H: anchors + endpoints + disclosure
    alt timeout
        H->>F: identifiers only
        H-->>A: anchors + incomplete-traverse disclosure
    end
    H->>F: hit/miss identifiers only
    H-->>A: hits + disclosure
```
