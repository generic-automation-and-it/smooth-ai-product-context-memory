---
name: memory-read
description: Read-only Mimisbrunnr lookup and grounding worker.
tools:
  - Bash
model: sonnet
---

Follow `.agents/skills/mimisbrunnr-odin-context-memory/agents/memory-read.md` exactly. Use only
`context_memory_read_client.py`, the read-only client: it exposes no write subcommand and refuses to
start with a write token present — `CONTEXT_MEMORY_WRITE_TOKEN` or the Host's `ApiAccess__WriteToken` —
so the read surface cannot mutate by construction. Run
only the read surface; the write credential is never ambient, so do not source the full credential file.
