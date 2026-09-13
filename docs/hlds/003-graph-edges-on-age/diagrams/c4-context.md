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

    System_Ext(agent, "AI harness", "Runs the context-memory skill. Sole agent-facing interface.")
    System_Ext(tracker, "Issue trackers", "Jira, Linear, GitHub. Referenced by identity only, never copied.")

    Rel(dev, agent, "Works through")
    Rel(agent, api, "Captures and retrieves", "HTTP")
    Rel(api, pg, "SQL and Cypher in one session")
    Rel(api, blob, "Stores and fetches by content address")
    Rel(api, tracker, "References", "provider + key")

    UpdateLayoutConfig($c4ShapeInRow="2", $c4BoundaryInRow="1")
```

**Read this for:** the boundary. The graph is inside the database the service already runs, and issue
trackers stay external and referenced — never mirrored into the graph.

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
```

**Read this for:** what the graph is allowed to hold. `GRAPH_VERTEX` has exactly one attribute and it
is an identity; `GRAPH_EDGE` holds only what describes the *relationship*. Every descriptive property
— subject, claim, scope, validity, tags, facets — remains relational. A vertex gaining a second
descriptive attribute is the violation LADR-02 forbids.

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

    Note over Skill,PG: Delete a memory — no cascade exists, so both halves are explicit

    Skill->>API: Delete memory
    API->>PG: BEGIN
    API->>PG: Remove every edge touching this identity
    API->>PG: Delete memory rows
    API->>PG: COMMIT
    Note right of PG: One transaction. A failure between<br/>the two steps rolls both back —<br/>no orphan edge can survive.
```

**Read this for:** why both operations are transactional. Step 2's existence check replaces the
composite primary key, and the delete's two ordered steps replace the foreign-key cascade. Neither is
optional, and neither is enforced by the database — NFR-01 exists to verify both, including the
failure case between the delete's two steps.
