# Synthetic evaluation boundary

The fixtures and expected outcomes are independent evaluation authority. Freeze them before tuning. Do not import service workflows, duplicate their comparison logic, or use model confidence as a quality score. Retain held-out labels and report failures honestly.

All writes use a newly generated `synthetic/knowledge-evaluation/<runId>` scope through the supported core API. Never use private domain content or an existing operational corpus scope, and never perform cleanup of resources not created by the invocation. Reports and runtime state belong outside Git.

The Python harness alone receives `MIMI_EVAL_CREDENTIALS_FILE`, `MIMI_EVAL_LLM_URL`, `MIMI_EVAL_LLM_KEY`, `MIMI_EVAL_LLM_MODEL`, `CONTEXT_MEMORY_BASE_URL`, `CONTEXT_MEMORY_READ_TOKEN`, `CONTEXT_MEMORY_WRITE_TOKEN`, `MIMI_KNOWLEDGE_URL`, `MIMI_KNOWLEDGE_READ_TOKEN`, `MIMI_KNOWLEDGE_WRITE_TOKEN`. Values stay inside runtime environment, protected parsing and HTTP Authorization headers. No child model process receives credentials. Errors contain classes, never provider response bodies or URLs. See the caller AGENTS.md for its additional protected-file variable.

## Changelog

| Date | Change |
| --- | --- |
| 2026-10-03 | Independent review fixes: observe current captures without frozen midnight filtering and compare corpus versions/sources after replay; live API checks require ticket URLs, completion-token caps, no storage and complete baseline responses. |
| 2026-10-03 | Froze synthetic retrieval/capture expectations and quality comparisons; added real-core direct search/body/provider baseline and black-box service comparison with separate caller context/provider/latency measurements. Automated signals require independent review. |
