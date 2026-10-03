# Optional knowledge-service caller

This caller transports evidence, not internal database plans. Preserve stable task/message identity, receipt acknowledgement versus incorporation, and one capture owner per task. The service is optional and the direct skills remain independent.

Only `knowledge_client.py` receives `MIMI_KNOWLEDGE_URL`, `MIMI_KNOWLEDGE_READ_TOKEN`, `MIMI_KNOWLEDGE_WRITE_TOKEN`, and `MIMI_KNOWLEDGE_CREDENTIALS_FILE`. It launches no model process and prints no credential, request body, server error body or endpoint in diagnostics. The protected file is parsed internally, not loaded by an agent or shell. Changes to routes must match Knowledge/Contracts.cs and black-box transport tests.

Run `python -m unittest discover -s .agents/skills/mimisbrunnr-knowledge-service/tests`. No service or provider is needed for these tests.

## Changelog

| Date | Change |
| --- | --- |
| 2026-10-03 | Independent contract review corrected source-reference documentation: the fingerprint binds the namespace and complete sanitized message, including attribution, order and timestamp; it is not a namespace-only hash. Caller message IDs remain immutable. |
| 2026-10-03 | Added explicit restore reconciliation transport: operator pins previous/current epochs, acknowledges restore, preserves identical retry JSON, and supplies extra allowance only explicitly. Public recovery/audit/deadline fields are documented. |
| 2026-10-03 | Caller sends stable task ID as the optional source namespace; namespaces stay constant across incremental captures, differ across conversations, and participate in handoff retry identity. Older uncertain requests must be replayed unchanged before adopting the additive field. |
| 2026-10-03 | Added two-operation caller skill, protected stdlib transport, stable incremental handoff state, receipt polling and clarification. Explicit end-task fallback covers runtimes without compaction hooks. |
