# Diagrams — Context memory storage

Three diagrams, each one concern. A sequence diagram is omitted: the write *order* is a behavioural
concern belonging to the write-pipeline HLD, not to storage.

---

## C1 — System Context

Establishes who uses the store and what it depends on.

```mermaid
C4Context
    title System Context — context memory storage

    Person(dev, "Developer", "Single user, local-first. Captures and recalls durable facts.")

    System_Boundary(cm, "Context Memory") {
        System(api, "Context Memory API", "Capture and retrieval. Owns transactional writes and enforces scope at retrieval.")
        SystemDb(pg, "PostgreSQL", "Index over content: entities, relationships, classification, validity, full text.")
        SystemDb(blob, "Object store", "Document bodies, content-addressed and immutable.")
    }

    System_Ext(agent, "AI harness", "Runs the context-memory skill. Sole agent-facing interface.")
    System_Ext(tracker, "Issue trackers", "Jira, Linear, GitHub. Referenced by identity, never copied.")

    Rel(dev, agent, "Works through")
    Rel(agent, api, "Captures and retrieves", "HTTP")
    Rel(api, pg, "Reads and writes", "SQL")
    Rel(api, blob, "Stores and fetches by content address")
    Rel(api, tracker, "References", "provider + key")

    UpdateLayoutConfig($c4ShapeInRow="2", $c4BoundaryInRow="1")
```

**Read this for:** the boundary. Trackers are referenced, never mirrored. The agent reaches the store
only through the API.

---

## C2 — Containers

Shows the two-store split that NFR-03 exists to protect, and why the object store is a separate
backup concern.

```mermaid
C4Container
    title Containers — context memory storage

    Person(dev, "Developer")
    System_Ext(agent, "AI harness")

    Container_Boundary(cm, "Context Memory") {
        Container(api, "API", "ASP.NET Core", "Vertical slices; owns the transaction boundary and scope enforcement at retrieval.")
        ContainerDb(pg, "PostgreSQL", "Relational + JSONB", "Seven entities. Arrays with GIN, validity with GIST, full text, partial unique index, append-only triggers.")
        ContainerDb(minio, "Object store", "S3-compatible", "Bodies keyed by content hash, gzip-compressed client-side, immutable.")
    }

    Rel(dev, agent, "Works through")
    Rel(agent, api, "HTTP")
    Rel(api, pg, "One transaction per write")
    Rel(api, minio, "Store / fetch by address")

    UpdateLayoutConfig($c4ShapeInRow="2", $c4BoundaryInRow="1")
```

**Read this for:** why recoverability is an NFR. Two durable stores means two snapshots, and a database
restored past its matching object snapshot yields references that resolve to nothing.

---

## ER — The entity model

Seven entities. The diagram's job is to show *what is not a table*: tags, facets, sources, tickets and
repository are all attributes, and that absence is the placement rule made visible.

```mermaid
erDiagram
    INITIATIVE ||--o{ MEMORY_GROUP : "classifies (mandatory, sentinel default)"
    MEMORY_GROUP ||--o{ GROUP_DESCRIPTION : "append-only history"
    MEMORY_GROUP ||--o{ MEMORY : contains
    MEMORY ||--o{ MEMORY_VERSION : "append-only history"
    MEMORY ||--o{ MEMORY_LINK : "links from"
    MEMORY ||--o{ MEMORY_LINK : "links to"
    LABEL }o..o{ MEMORY : "advisory registry - no FK by design"

    INITIATIVE {
        bigint id PK
        text name UK "seeded: to-be-decided"
        text status
    }
    LABEL {
        bigint id PK
        text name UK
        text status "active|draft|deleted"
    }
    MEMORY_GROUP {
        bigint id PK
        uuid uuid UK
        text scope_dimension "product|customer|program|self"
        text scope_identifier
        bigint initiative_id FK "NOT NULL"
        text repo "denormalised column, nullable"
        text repo_url "denormalised column, nullable"
        jsonb tickets "GIN - accumulates over time"
        timestamptz created_on
    }
    GROUP_DESCRIPTION {
        bigint id PK
        int version "monotonic"
        text body
    }
    MEMORY {
        bigint id PK
        uuid uuid UK "stable across versions"
        uuid lineage_id "shared across clones"
        text description "SUBJECT - stable"
        text name "FTS-indexed with description"
        text subject_slug "unique per group"
        text_array tags "GIN - unversioned"
        text_array facets "GIN - unversioned"
    }
    MEMORY_VERSION {
        bigint id PK
        int version
        boolean is_current "partial unique index"
        text statement "CLAIM - volatile"
        text content_summary
        smallint confidence
        jsonb summary_stamp "D42 stamp"
        text blob_address "SHA-256"
        text kind "open vocabulary"
        text status "proposed|approved"
        jsonb sources
        timestamptz valid_from "BUSINESS time"
        timestamptz valid_until "BUSINESS time"
        timestamptz created_on "SYSTEM time"
    }
    MEMORY_LINK {
        bigint source_memory_id FK
        bigint target_memory_id FK
        text relation
        text reason "mandatory"
    }
```

*(`text_array` denotes a text array; bracket notation is avoided for diagram parsing.)*

**Read this for four decisions made visible:**

1. **Tags and facets sit on the stable row, not the versioned one.** That placement *is* the unversioned-classification decision — moving them one row down would version them.
2. **Sources sit on the version**, because provenance answers where *this claim* came from.
3. **The label association is dashed and has no foreign key.** The registry is advisory by design; an FK would make it enforcing.
4. **Two stable/versioned pairs** at different levels — group with its description, memory with its versions. One shape, applied twice.

Also visible: two time columns on the version row are business time and one is system time, and the
subject/claim split runs across the two memory rows.
