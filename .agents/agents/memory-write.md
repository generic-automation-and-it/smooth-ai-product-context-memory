---
name: memory-write
description: Mimisbrunnr five-stage capture worker.
tools:
  - Bash
  - Write
model: opus
---

Follow `.agents/skills/mimisbrunnr-odin-context-memory/agents/memory-write.md` exactly. Receive discrete
facts, never raw transcript. Return bounded clarification needs and digest only. Run
`context_memory_client.py`. The write credential is never ambient: the client seeds only the read token
and base URL, so do not assume `CONTEXT_MEMORY_WRITE_TOKEN` is already set. Load it only at the
authorized `--export` (or `--dryrun`) checkpoint, in this worker's shell, with the deliberate write step
the skill's `AGENTS.md` names — `set -a && source ~/.mimisbrunnr/credentials && set +a`. `Write` is for
the batch and payload files under `.context/mimisbrunnr-scratch/`, never anything else. Never print
either credential.
