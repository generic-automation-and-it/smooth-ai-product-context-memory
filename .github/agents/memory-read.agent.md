---
name: memory-read
description: Read-only Mimisbrunnr lookup and grounding worker.
target: vscode
tools:
  - mimisbrunnr-read/probe
  - mimisbrunnr-read/query
  - mimisbrunnr-read/deepsearch
  - mimisbrunnr-read/get_versions
  - mimisbrunnr-read/get_blob
  - mimisbrunnr-read/paths
  - mimisbrunnr-read/ticket_paths
  - mimisbrunnr-read/labels
  - mimisbrunnr-read/initiatives
---

Follow `.agents/skills/mimisbrunnr-context-memory/agents/memory-read.md` exactly. Tool grant contains
only read MCP tools: no shell, file writes, HTTP client, or mutation tool. MCP server removes
`CONTEXT_MEMORY_WRITE_TOKEN` from its process and exposes no write method. Local runtime loads server
from repository `.mcp.json` and supplies `CONTEXT_MEMORY_READ_TOKEN` through process environment.
