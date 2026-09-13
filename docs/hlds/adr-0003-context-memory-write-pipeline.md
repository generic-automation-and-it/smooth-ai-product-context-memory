# ADR-0003: Context-memory write pipeline and skill contract

**Status:** Superseded — converted to [HLD 002 — Context memory write pipeline](./002-context-memory-write-pipeline/)

| This ADR's content | Now in |
|---|---|
| Five-stage pipeline and canonical ordering | [LADR-01](./002-context-memory-write-pipeline/ladrs/LADR-01-five-stage-pipeline.md) |
| Redaction ordering and immutability rationale | [LADR-02](./002-context-memory-write-pipeline/ladrs/LADR-02-redaction-precedes-blob-write.md) |
| Redact-and-flag failure mode | [LADR-03](./002-context-memory-write-pipeline/ladrs/LADR-03-redact-and-flag.md) |
| Semantic deduplication ownership | [LADR-04](./002-context-memory-write-pipeline/ladrs/LADR-04-semantic-dedup-is-skill-owned.md) |
| Link derivation placement | [LADR-05](./002-context-memory-write-pipeline/ladrs/LADR-05-link-derivation-batched.md) |
| Approval gating semantics | [LADR-06](./002-context-memory-write-pipeline/ladrs/LADR-06-gate-status-not-persistence.md) |
| Digest as receipt; dry-run as veto | [LADR-07](./002-context-memory-write-pipeline/ladrs/LADR-07-digest-is-a-receipt.md) |
| Pipeline sequence | [diagrams](./002-context-memory-write-pipeline/diagrams/c4-context.md) |
| Dedup test approach and divergence fixture | [NFR-02](./002-context-memory-write-pipeline/nfrs/NFR-02-deduplication-accuracy.md) |

**The API surface list** — the operation-by-operation contract — is implementation detail rather than
design, and now lives with the API it specifies rather than in the HLD.

Retained as a pointer so existing references resolve. Do not extend this file.
