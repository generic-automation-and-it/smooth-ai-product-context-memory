# Diagrams — Context memory write pipeline

Two diagrams. The entity model belongs to the storage HLD and is not repeated here; a container
diagram would add nothing, since this design introduces no container.

---

## C1 — System Context

Establishes where judgement lives. The skill is the only agent-facing interface, and it runs on the
harness rather than inside the service.

```mermaid
C4Context
    title System Context — context memory write pipeline

    Person(dev, "Developer", "Captures durable facts at an end-of-task checkpoint.")

    System_Boundary(harness, "AI harness") {
        System(skill, "Context-memory orchestrator", "Owns interaction; receives only cited conclusions and receipts.")
        System(readAgent, "Read agent", "Read credential only; bounded lookup and grounding.")
        System(writeAgent, "Write agent", "Owns redaction, semantic matching, divergence, link derivation and atomicity.")
    }

    System_Boundary(cm, "Context Memory") {
        System(api, "Context Memory API", "Owns mechanics: transaction, version-bump ordering, current-version flag, scope enforcement, blob proxying.")
        SystemDb(store, "Storage", "PostgreSQL index + content-addressed object store. See HLD 001.")
    }

    Rel(dev, skill, "Captures and recalls")
    Rel(skill, readAgent, "Delegates retrieval")
    Rel(skill, writeAgent, "Delegates discrete facts")
    Rel(readAgent, api, "Query and drill-down", "HTTP + read token")
    Rel(writeAgent, api, "Preflight and set", "HTTP + write token")
    Rel(api, store, "One transaction per write")

    UpdateLayoutConfig($c4ShapeInRow="2", $c4BoundaryInRow="2")
```

**Read this for:** the separation. The skill decides; the API enforces. A judgement failure is silent
and data-dependent; a mechanical failure is loud and reproducible. Keeping them in different systems
keeps the two kinds of bug distinguishable.

---

## Sequence — The five-stage write pipeline

The pipeline is inherently temporal — its correctness *is* its ordering — so this is the diagram that
carries the design.

```mermaid
sequenceDiagram
    autonumber
    participant H as Human
    participant S as Main skill (orchestration)
    participant W as Write agent (judgement)
    participant A as API (mechanics)
    participant DB as PostgreSQL
    participant B as Object store

    H->>S: Checkpoint — capture what was learned
    S->>W: Discrete facts + mode (never raw transcript)

    rect rgb(245, 245, 245)
        Note over W,DB: Stage 1 — Preflight (batched, judges nothing, writes nothing)
        W->>A: Candidate batch (subjects, kinds, facets, ticket refs, target group)
        A->>DB: Batched exact subject/ticket checks
        DB-->>A: Exact-match candidates, ticket conflicts, intra-batch collisions
        A-->>W: Array out — facts only, no decisions
    end

    rect rgb(245, 245, 245)
        Note over W: Stage 2 — Redact (before anything reaches storage)
        W->>W: Fingerprint detection; scrub spans
    end

    rect rgb(245, 245, 245)
        Note over W,DB: Stage 3 — Dedupe, divergence and derive links
        W->>A: Baseline recall; optional bounded deep-search passes
        A->>DB: Bounded candidate set
        DB-->>A: Cheap fields only
        A-->>W: Candidates
        W->>W: Judge per pair — version / new / divergence / skip; derive links with reasons
    end

    rect rgb(245, 245, 245)
        Note over W: Stage 4 — Atomicity check
        W->>W: One memory = one fact. Split bundles; route remainder to skipped
    end

    rect rgb(245, 245, 245)
        Note over W,B: Stage 5 — Write (one transaction, API-owned)
        W->>A: Writes with create UUIDs + resolved links
        A->>B: Store bodies, content-addressed
        B-->>A: Addresses
        A->>DB: BEGIN
        A->>DB: Flip old is_current false, then insert new current
        A->>DB: Insert links
        A->>DB: COMMIT
        A-->>W: Digest
    end

    W-->>S: Bounded clarification needs + digest only
    S-->>H: Receipt — created / versioned / linked / diverged / skipped / labels-proposed
```

**Read this for three properties the ordering encodes:**

1. **Redaction sits at stage 2 because stage 5 is irreversible.** Once a body is stored it is immutable and its address is a function of its content; prevention is the only clean remedy.
2. **Stages 1 and 3 have distinct bounded reads.** Stage 1 supplies exact subject/ticket backstops and
   intra-batch collisions. Stage 3 supplies semantic candidates for deduplication and link derivation;
   optional deep search only expands this stage.
3. **Stage 5 is one transaction, owned by the API.** The version flip must precede the insert or the partial unique index rejects it — and both must share a transaction, because a failure between them leaves the memory with *zero* current versions, which no constraint forbids and nothing detects.

Note also what stage 1 does **not** do: it returns facts, never decisions. The judgement is entirely
the skill's, which is why an endpoint returning a merge verdict would be a boundary violation.
