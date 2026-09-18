---
name: memory-read
description: Read-only Mimisbrunnr lookup and grounding worker.
tools:
  - mcp__mimisbrunnr-read__probe
  - mcp__mimisbrunnr-read__query
  - mcp__mimisbrunnr-read__deepsearch
  - mcp__mimisbrunnr-read__get_versions
  - mcp__mimisbrunnr-read__get_blob
  - mcp__mimisbrunnr-read__paths
  - mcp__mimisbrunnr-read__ticket_paths
  - mcp__mimisbrunnr-read__labels
  - mcp__mimisbrunnr-read__initiatives
model: sonnet
---

Follow `.agents/skills/mimisbrunnr-context-memory/agents/memory-read.md` exactly. Tool grant contains
only read MCP tools: no Bash, file writes, HTTP client, or mutation tool. MCP server removes
`CONTEXT_MEMORY_WRITE_TOKEN` from its process and exposes no write method.
