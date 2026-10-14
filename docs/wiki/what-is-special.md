# What is special with Mímisbrunnr

Compares Mímisbrunnr against guidance projects researched during the MVP
(MemOS, ICM Architect, Utopia, MegaMemory, OpenViking, holoself) and the
wider session-memory / second-brain landscape (mem0, Zep/Graphiti, Letta,
Cognee, LangMem, Supermemory, OpenMemory, CortexDB, OKF Agent Memory,
Obsidian). Researched 2026-10-13; CortexDB and OKF added 2026-10-14.

## Guidance projects (compared feature-by-feature in-session)

| Project | What | Storage | Capture | Retrieval | Correction | Verdict |
|---|---|---|---|---|---|---|
| [MemTensor/MemOS](https://github.com/MemTensor/MemOS) | Self-evolving memory OS | Neo4j+Qdrant / SQLite | Auto background queue | Hybrid FTS+vector, auto-inject | Edit/delete, model curation | Refuse: background capture, model-side curation, edit/delete. Take: untrusted-data framing, read deadline, context cap |
| [RinDig/icm-architect](https://github.com/RinDig/icm-architect) | Claude skill, folder-as-architecture | Markdown files, no DB | Deliberate human build | None (read files) | File edits via migration map | Refuse: filesystem-as-state (kills scope, versions, traversal). Take: durability guard, cold-agent walk test, Hits/Does-not-hit |
| [deeplethe/utopia](https://github.com/deeplethe/utopia) | Enterprise world-model KB (Rust) | Postgres+pgvector+Tantivy | Auto connectors + gated proposals | Hybrid + bitemporal graph | Append-only bitemporal + ledger | Take: review queue, staged dedup (exact→lexical→model), closed choice menus. Refuse: ingestion, auto-adjudication, embeddings, ontology, inference, multi-user |
| [0xK3vin/MegaMemory](https://github.com/0xK3vin/MegaMemory) | Per-project knowledge-graph MCP server | SQLite per project | Deliberate agent calls | Vector-only MiniLM + graph nav | In-place update + soft delete | Closest deliberate-capture idea, but vector-only, no versions/scope |
| [volcengine/OpenViking](https://github.com/volcengine/OpenViking) | Context DB (memory+RAG+skills) | RAGFS + vector, `viking://` FS | Auto extraction + session commit | Scoped vector + rerank, L0/L1 summaries | LLM merge patches | Multi-tenant SaaS shape we refuse. AGPL |
| [smota/holoself](https://github.com/smota/holoself) | Whole-person context protocol | Local Markdown, disposable index | Deliberate, human-approved diffs | Deterministic index, lens-scoped, no vector | Approved diffs + provenance | Philosophy closest (deliberate, local-first, MIT) but no server/graph/versions |

Detail: MemOS and ICM comparisons ran as full in-session analyses and became
worktasks (ICM: `.context/work-tasks/icm/`, MemOS: `.context/work-tasks/memos/`,
both since delivered). Utopia is recorded as the local Understanding
`utopia-what-to-take-and-what-to-refuse` (2026-10-05).

## Wider landscape

| Rival | What | Storage | Capture | Retrieval | Correction | Scope |
|---|---|---|---|---|---|---|
| [mem0ai/mem0](https://github.com/mem0ai/mem0) | Drop-in memory layer | Pluggable vector + graph | Auto LLM extract | Semantic+BM25+entity fused | V3 accumulates; API update/delete exposed | Multi-user/app SDK+Cloud. Apache-2.0 |
| [getzep/zep + graphiti](https://github.com/getzep/zep) / [graphiti](https://github.com/getzep/graphiti) | Temporal context-graph infra | Proprietary graph / Neo4j/FalkorDB | Auto episode ingestion | Hybrid + bi-temporal traversal | Auto invalidation, history kept | Multi-user enterprise |
| [letta-ai/letta](https://github.com/letta-ai/letta) (ex-MemGPT) | Stateful self-editing agents | Postgres: core blocks + archival | Agent self-writes + dreaming | Pinned blocks + archival search | Agent rewrites own blocks | Multi-user platform. Apache-2.0 |
| [topoteretes/cognee](https://github.com/topoteretes/cognee) | "Company brain" ETL | Graph+vector+relational | `remember()` pipelines | `recall()` auto-routed hybrid | `improve()`/`forget()` | Local→multi-tenant. Apache-2.0 |
| [langchain-ai/langmem](https://github.com/langchain-ai/langmem) | LangGraph memory toolkit | Bring-your-own store | Hot-path tools + background manager | Namespaced semantic search | Agent-managed create/update | Library. MIT |
| [supermemoryai/supermemory](https://github.com/supermemoryai/supermemory) | Memory+context engine/API | Hosted graph + Postgres | Auto extract + connectors | Hybrid `search()`+`profile()` | Auto supersede/expire | Multi-user/app. MIT repo |
| [mem0ai/openmemory](https://github.com/mem0ai/openmemory) | Session porter for coding CLIs | Local session files | `port --from/--to` | TUI list, no semantic search | Selective re-export | Single-dev. MIT |
| [liliang-cn/cortexdb](https://github.com/liliang-cn/cortexdb) | Embedded memory + knowledge-graph library (Go) | One SQLite file (or Postgres+pgvector); RDF/SPARQL graph | Explicit `remember` + optional auto session distillation by a model | FTS5 + vector + hybrid + graph, auto-recall hook; lexical mode without embeddings | Replace by slug; outdated memories marked superseded, kept | Local file or shared gRPC brain. MIT |
| [okf-memory/okf-agent-memory](https://github.com/okf-memory/okf-agent-memory) | Git-native agent memory on the OKF v0.2 format | Markdown+YAML bundle in repo, no DB | Agent writes via CLI/skill, search-before-write, `generated`/`verified` trust tiers | In-memory BM25 + link graph, scope shadowing | `okf update` in place; history via git | Project/vendor/user/system layers, optional encrypted hub sync. MIT |
| [Obsidian](https://obsidian.md/) | Human second brain, local-first notes | Markdown vault on device | Human writes | Keyword search, human browses `[[links]]`/graph/canvas | File edits, 1-yr history via paid Sync | Single human; E2EE Sync + Publish paid. Proprietary |

Complementary, not competing: Obsidian serves human thinking, the well serves
agent recall. Bridge: dossier output and Understanding markdown (`[[links]]` +
generated `INDEX.md`) can live in a vault.

## What is special

Rivals are automatic, model-inside, vector-first, editable, multi-user SaaS.
Mímisbrunnr is the opposite on each axis — every row a recorded decision:

1. **Deliberate capture, human veto.** Checkpoint + `set --dryrun` digest +
   approval (BR-11). Rivals auto-capture in background.
2. **No model in Application/Host.** The skill judges, the API enforces
   (HLD-005 §2.6). Rivals curate server-side.
3. **Append-only versions; correction ≠ world change.** Supersession is a
   version bump; blobs are content-addressed and immutable. Rivals
   edit/delete in place.
4. **Identity `(group, uuid)` + declared scope.** Initiative, scope, repo,
   tickets, subject, tags/facets anchor each fact; scope enforced on read.
   Rivals use SaaS `user_id` isolation or no scope.
5. **Index-first retrieval, vectors refused on evidence.** `english` stemming
   FTS (recall 0.44→0.78, HLD-001 NFR-02) + bounded Apache AGE traversal
   (depth 1–5 required). `pg_trgm` measured and rejected.
6. **Graph additive, not home.** Relational core authoritative; edges only
   where paths are needed (`LINKS` + ticket parent/child). HLD-003.
7. **Understanding = `kind = understanding`, cross-repo.** Distilled portable
   skill, not chat history. BRD-003, HLD-007.
8. **Capability-split credentials.** Read ambient, write never ambient; read
   client refuses to start with a write token present.
9. **Self-verifying corpus.** `snapshot`/`verify`/`restore` tar+manifest
   (sha256) + pre-upgrade check. HLD-006.
10. **Recalled = evidence, never orders.** Untrusted-data framing,
    fail-closed redaction gate, recall-feedback miss-vs-absence (HLD-004),
    dossier with provenance (HLD-005).
11. **One machine, one practitioner, honest limits.** See README
    *Where Mímisbrunnr loses*: no multi-tenant, no real-time multi-agent,
    lexeme-AND not semantic, cross-group dup invisible, nothing deletable.
12. **Session markers on import and export.** A deliberate import or a successful
    export marks the session's working point (client-side, ids only, per skill); a
    default export is a delta since the last marker, so imported or already-exported
    knowledge is never re-proposed. `export --all` ignores the marker, `--nomarker`
    leaves it untouched (HLD-007 LADR-11). Rivals deduplicate after the fact; Mímisbrunnr
    removes the echo before preflight.
