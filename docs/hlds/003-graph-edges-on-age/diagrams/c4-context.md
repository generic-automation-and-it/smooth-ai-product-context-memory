# Diagrams — Graph edges on Apache AGE

Three diagrams, each covering one concern. Container and flow diagrams are deliberately omitted:
the container topology does not change (same API, same database, same object store — only the
database image differs), and there is no new cross-service flow.

---

## C1 — System Context

Establishes that nothing new is deployed. The graph capability arrives inside an existing container.

```mermaid
C4Context
    title System Context — context memory with graph relationships

    Person(dev, "Developer", "Single user, local-first. Captures and recalls durable facts through an agent.")

    System_Boundary(cm, "Context Memory") {
        System(api, "Context Memory API", "Capture and retrieval. Owns the write pipeline and enforces scope at retrieval.")
        SystemDb(pg, "PostgreSQL + AGE", "Entities, constraints, temporal validity and full text as tables. Relationships as graph edges. One instance, one transaction boundary, one backup.")
        SystemDb(blob, "Object store", "Document bodies, content-addressed and immutable.")
    }

    System_Ext(agent, "AI harness", "Runs the mimisbrunnr-context-memory skill. Sole agent-facing interface.")
    System_Ext(tracker, "Issue trackers", "Jira, Linear, GitHub. Exact provider/key identity; no synchronization or freshness guarantee.")

    Rel(dev, agent, "Works through")
    Rel(agent, api, "Captures and retrieves", "HTTP")
    Rel(api, pg, "SQL and Cypher in one session")
    Rel(api, blob, "Stores and fetches by content address")
    Rel(api, tracker, "References", "provider + key")

    UpdateLayoutConfig($c4ShapeInRow="2", $c4BoundaryInRow="1")
```

**Read this for:** the boundary. The graph stays inside the existing database. Trackers remain
external; no network dependency or synchronization is added. LADR-08 accepts locally captured
practitioner-declared hierarchy, not an authoritative tracker mirror. Implementation and release
gates are accepted against the final NFR-02 evidence.

---

## ER — The entity / edge boundary

The design *is* this boundary. Everything above the line stays relational; only the relationship
crosses over.

```mermaid
erDiagram
    INITIATIVE ||--o{ MEMORY_GROUP : "classifies"
    MEMORY_GROUP ||--o{ GROUP_DESCRIPTION : "versioned history"
    MEMORY_GROUP ||--o{ MEMORY : "contains"
    MEMORY ||--o{ MEMORY_VERSION : "versioned history"
    MEMORY ||--o| GRAPH_VERTEX : "anchored by (identity only)"
    GRAPH_VERTEX ||--o{ GRAPH_EDGE : "source of"
    GRAPH_VERTEX ||--o{ GRAPH_EDGE : "target of"
    MEMORY_GROUP ||--o{ TICKET_VERTEX : "live JSONB ownership join, not a graph edge"
    TICKET_VERTEX ||--o{ TICKET_PARENT : "parent source"
    TICKET_VERTEX ||--o| TICKET_PARENT : "child target, at most one parent"

    INITIATIVE {
        bigint id PK
        text name UK
        text status
    }
    MEMORY_GROUP {
        bigint id PK
        uuid uuid UK
        text scope_dimension
        bigint initiative_id FK
        jsonb tickets
    }
    GROUP_DESCRIPTION {
        bigint id PK
        int version
        text body
    }
    MEMORY {
        bigint id PK
        uuid uuid UK "the identity the graph anchors on"
        uuid lineage_id
        text description "SUBJECT - stable"
        text_array tags
        text_array facets
    }
    MEMORY_VERSION {
        bigint id PK
        int version
        boolean is_current "partial unique index"
        text statement "CLAIM - volatile"
        timestamptz valid_from "BUSINESS time"
        timestamptz created_on "SYSTEM time"
    }
    GRAPH_VERTEX {
        uuid memory_uuid "IDENTITY ONLY - no properties"
    }
    GRAPH_EDGE {
        text relation "depends_on|relates_to|contradicts|supersedes|implements"
        text reason "mandatory - why this link exists"
    }
    TICKET_VERTEX {
        text provider "exact identity only"
        text key "exact identity only"
    }
    TICKET_PARENT {
        text reason "mandatory"
        text source "mandatory"
        timestamptz observedAt "optional"
        timestamptz recordedAt "mandatory"
    }
```

**Read this for:** the accepted boundary. `GRAPH_VERTEX`/`GRAPH_EDGE` represent delivered `Memory`/
`LINKS`. `TICKET_VERTEX`/`TICKET_PARENT` represent LADR-08's implemented and accepted extension.
Vertex properties are identity only; edge properties describe the declaration. Subject, claim,
scope, validity, tags, facets and ticket membership remain relational. The ownership line is a live
join against group JSONB, not a foreign key or membership edge. Memories join through their group;
no Ticket-to-Memory fanout exists. Ticket edges form a current-state forest, not history.

Note also what is *absent*: no foreign key runs from `GRAPH_EDGE` into `MEMORY`. That missing line is
the guarantee LADR-05 replaces with an application invariant.

---

## Sequence — Relationship write and memory delete

The static view cannot show a consistency boundary. These two flows are where the forfeited database
guarantees are paid back, and where a mistake produces an orphan or a duplicate.

```mermaid
sequenceDiagram
    autonumber
    participant Skill as Context-memory skill
    participant API as Context Memory API
    participant PG as PostgreSQL + AGE

    Note over Skill,PG: Create a relationship — uniqueness is now a read-before-write

    Skill->>API: Propose link (source, target, relation, reason)
    API->>PG: BEGIN
    API->>PG: Does an edge with this source, target and relation exist?
    PG-->>API: none
    API->>PG: Ensure both vertices exist (identity only)
    API->>PG: Create edge with relation and reason
    API->>PG: COMMIT
    API-->>Skill: Created (reported in the digest)

    Note over Skill,PG: Duplicate attempt — the invariant, not a constraint, rejects it

    Skill->>API: Propose the same link again
    API->>PG: BEGIN
    API->>PG: Does an edge with this source, target and relation exist?
    PG-->>API: one
    API->>PG: COMMIT
    API-->>Skill: Skipped as duplicate (reported, never silent)

    Note over Skill,PG: Delete a memory - trigger owns graph cleanup

    Skill->>API: Delete memory
    API->>PG: BEGIN
    API->>PG: Delete memory rows
    Note right of PG: BEFORE DELETE trigger removes Memory vertex<br/>and incident LINKS in this transaction.<br/>Ticket hierarchy is unchanged.
    API->>PG: COMMIT
    Note right of PG: One transaction. A failure between<br/>the two steps rolls both back —<br/>no orphan edge can survive.
```

**Read this for:** why both operations are transactional. The existence check replaces the composite
primary key; the database trigger supplies graph cleanup that a foreign-key cascade cannot.
NFR-01 verifies rollback as well as success. Under LADR-08, group-ticket triggers and hierarchy
set/reparent/remove share one transaction-scoped advisory lock; group deletion removes Ticket
vertices and incident hierarchy in that transaction. These ticket flows are implemented and accepted
against standing tests and final benchmarks. Strict expected-parent validation precedes identical-state no-op detection.

The separate `ITicketGraph` read (POST `/api/context/tickets/paths`) requires `maxDepth` 1..5.
One composed statement combines a Cypher anchor with recursive SQL over indexed AGE adjacency,
not variable-length Cypher. Identity lookup uses separate provider/key HASH indexes plus properties
GIN for MERGE, not a composite btree over unrestricted keys. The read resolves each ticket to
exactly one live owner, gates all hops with `HiddenDimensions`, drops hidden paths whole, and applies
endpoint `Plan()` narrowing before returning deterministic capped paths. Their selected endpoint
groups plus the anchor supply distinct current non-proposed memories under a separate memory cap.
Ticket identity supplies no consent. Generic undeclared
upstream/freshness disclosure and visible cap flags reveal no hidden identities or counts.
