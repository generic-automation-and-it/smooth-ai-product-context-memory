# Diagrams — Memory recall feedback

Two diagrams. An entity-relationship diagram is still omitted, now for a different reason: the placement
question is settled — feedback lives in append-only records, and LADR-02 records the field shape — but no
table exists yet. An ER diagram drawn ahead of the shipped one would be a second, divergent source for it.

The recall path below is unchanged by that decision: feedback was always emitted off the critical path, and
the chosen placement is where the outcome lands, not a new step.

---

## C1 — System Context

Shows that this design adds no participant. Feedback is produced by an existing path and consumed
occasionally by the practitioner.

```mermaid
C4Context
    title System Context — memory recall feedback

    Person(dev, "Practitioner", "Tunes the system by examining whether recall is working.")

    System_Boundary(cm, "Context Memory") {
        System(api, "Context Memory API", "Serves retrieval. Emits a recall outcome per retrieval.")
        SystemDb(store, "Store", "Memories, versions, relationships. See HLD 001 and 003.")
        System(fb, "Recall feedback", "Identity, outcome and time. No content. Disposable.")
    }

    System_Ext(agent, "AI harness", "Runs the mimisbrunnr-context-memory skill; issues retrievals on the practitioner's behalf.")

    Rel(dev, agent, "Works through")
    Rel(agent, api, "Retrieves", "HTTP")
    Rel(api, store, "Reads")
    Rel(api, fb, "Records outcome")
    Rel(dev, fb, "Examines when tuning")

    UpdateLayoutConfig($c4ShapeInRow="2", $c4BoundaryInRow="1")
```

**Read this for:** who consumes feedback. The practitioner reads it **occasionally, while tuning** —
not the agent, and not the retrieval path. Nothing feeds back into ranking, which is a deliberate
exclusion in the guiding principle.

---

## Sequence — The recall path and its feedback point

Marks where feedback is emitted and, importantly, that it is emitted on **both** outcomes.

```mermaid
sequenceDiagram
    autonumber
    participant S as Skill
    participant A as API
    participant DB as Store
    participant F as Feedback

    Note over S,F: Hit — memories returned

    S->>A: Retrieve (filters and/or question)
    A->>DB: Indexed query
    DB-->>A: Matching memories (cheap fields)
    A->>F: Outcome — identities returned, shape, time
    A-->>S: Results

    Note over S,F: Miss — nothing returned

    S->>A: Retrieve
    A->>DB: Indexed query
    DB-->>A: No matches
    A->>F: Outcome — miss, shape, time (no identities)
    A-->>S: Empty result (a normal response)

    Note over A,F: Feedback never carries content or query text (LADR-03).<br/>A feedback failure must not fail the retrieval (NFR-02).

    Note over S,F: Occasionally, while tuning

    participant P as Practitioner
    P->>F: Which memories have never been recalled?
    P->>F: How often does retrieval return nothing?
```

**Read this for three properties:**

1. **Feedback is emitted on the miss path too.** Recording only hits produces a record of what recall already does well and silence about everything it does badly — and the miss is the more actionable signal.
2. **An empty result is a normal response**, not an error. The miss is interesting to *tuning*, not to the caller.
3. **Feedback is off the critical path.** A failure to record must not fail the retrieval; losing a tuning signal is acceptable, losing a recall is not.

Note what is absent: no arrow returns from feedback to the query. Ranking influenced by prior recall
would be self-reinforcing, and that failure would be slow and difficult to detect.
