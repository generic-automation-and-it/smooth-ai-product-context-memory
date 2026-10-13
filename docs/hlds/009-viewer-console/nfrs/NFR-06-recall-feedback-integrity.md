# NFR-06 — Recall feedback integrity

| Attribute | Boundary |
|---|---|
| **Attribute** | Integrity / retrieval observability |
| **Target** | The Console's reads record recall feedback exactly as the read path does, never duplicated |
| **Status** | Draft |

Reading via the query endpoint already records recall feedback (`QueryMemories`). The Console goes through
that path; it must not add a second feedback write that would double-count or corrupt the miss/vs-absence
signal.

**Acceptance:** a Console browse that issues the same query as the read client produces the same feedback
records, no more; the Console does not write feedback itself.
