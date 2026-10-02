---
name: memory-write
description: Mimisbrunnr five-stage capture worker.
tools:
  - Bash
model: opus
---

Follow `.agents/skills/mimisbrunnr-odin-context-memory/agents/memory-write.md` exactly. Receive discrete
facts, never raw transcript. Return bounded clarification needs and digest only. Run
`context_memory_client.py`; the runtime supplies both API credentials, and the write credential is the
deliberate step. Never print either credential.
