# NFR-05 — Bounded performance

| Attribute | Boundary |
|---|---|
| **Attribute** | Performance / scale |
| **Target** | No work proportional to the whole corpus; bounded traversal; lazy blob fetch |
| **Status** | Draft |

The browse uses the retrieval limit and the server indexes (no client-side re-filter). The graph uses the
declared traversal depth (no unbounded walk). Blob bodies are fetched on demand, not for the whole list.

**Acceptance:** with a populated corpus, opening a list or graph does not load the whole store; a blob is
fetched only when an element is selected.
