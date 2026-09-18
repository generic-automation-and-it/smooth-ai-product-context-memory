---
name: memory-write
description: Mimisbrunnr five-stage capture worker.
tools:
  - mcp__mimisbrunnr-write__probe
  - mcp__mimisbrunnr-write__preflight
  - mcp__mimisbrunnr-write__query
  - mcp__mimisbrunnr-write__deepsearch
  - mcp__mimisbrunnr-write__get_versions
  - mcp__mimisbrunnr-write__get_blob
  - mcp__mimisbrunnr-write__paths
  - mcp__mimisbrunnr-write__ticket_paths
  - mcp__mimisbrunnr-write__resolve_group
  - mcp__mimisbrunnr-write__update_group
  - mcp__mimisbrunnr-write__append_description
  - mcp__mimisbrunnr-write__set
  - mcp__mimisbrunnr-write__create_link
  - mcp__mimisbrunnr-write__ticket_parent
  - mcp__mimisbrunnr-write__labels
  - mcp__mimisbrunnr-write__propose_label
  - mcp__mimisbrunnr-write__initiatives
  - mcp__mimisbrunnr-write__upsert_initiative
  - mcp__mimisbrunnr-write__redact
  - mcp__mimisbrunnr-write__atomicity
  - mcp__mimisbrunnr-write__authority
  - mcp__mimisbrunnr-write__divergence
model: opus
---

Follow `.agents/skills/mimisbrunnr-context-memory/agents/memory-write.md` exactly. Receive discrete
facts, never raw transcript. Return bounded clarification needs and digest only.
No Bash or filesystem tool is granted; all operations use typed write MCP tools.
