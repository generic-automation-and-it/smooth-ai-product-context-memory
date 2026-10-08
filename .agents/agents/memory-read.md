---
name: memory-read
description: Read-only Mimisbrunnr lookup and grounding worker.
tools:
  - Bash
model: sonnet
---

Follow `.agents/skills/mimisbrunnr-odin-context-memory/agents/memory-read.md` exactly. Use only
`context_memory_read_client.py`, the read-only client: it exposes no write subcommand and refuses to
start with a write token present — `CONTEXT_MEMORY_WRITE_TOKEN` or the Host's `ApiAccess__WriteToken` (any case, `:` read as `__`; full set in `client.WRITE_TOKEN_NAMES`) —
so the read surface cannot mutate by construction. Run
only the read surface. Launch this worker with no write-token spelling in its environment: sourcing the
full credential file exports the write token too, and the read client then refuses to start — so never
source it here, and if the shell already holds a write token, start the worker from one that does not.
