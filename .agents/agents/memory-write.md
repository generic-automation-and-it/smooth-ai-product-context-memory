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
the skill's `AGENTS.md` names — `set -a && source ~/.mimisbrunnr/credentials && set +a`. Create the
scratch folder owner-only with `mkdir -p -m 700 .context/mimisbrunnr-scratch` before writing into it —
if it already exists, `chmod 700` it (or remove and recreate it) first — and mask every secret as
`<REDACTED>` and mask and generalise every personal identifier (any personal data under the GDPR) in every
candidate before the first file exists — drop the value, name a role or type instead, and skip a fact that means
nothing once generalised; neither the scrubber nor the cleanup removes personal data.
`Write` is for that folder's `.gitignore` (the single line `*`, written first) and the batch and payload
files under `.context/mimisbrunnr-scratch/`, never anything else. Never print either credential.
