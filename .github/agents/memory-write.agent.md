---
name: memory-write
description: Mimisbrunnr five-stage capture worker.
target: vscode
tools:
  - mimisbrunnr-write/*
---

Follow `.agents/skills/mimisbrunnr-context-memory/agents/memory-write.md` exactly. Receive discrete
facts, never raw transcript. Return bounded clarification needs and digest only. No shell or filesystem
tool is granted; all operations use typed write MCP tools from repository `.mcp.json`. Local runtime
supplies read/write credentials through process environment.
