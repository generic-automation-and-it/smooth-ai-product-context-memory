# NFR-04 — No credential in log or artifact

| Attribute | Boundary |
|---|---|
| **Attribute** | Secrets handling |
| **Target** | Read/write tokens never reach a log, span attribute, metric tag or committed artifact |
| **Status** | Draft |

Consistent with NFR-05 of the store (memory content never reaches a log) and the skill secret-handling
rule: credentials are not logged, not echoed in errors, and not committed.

**Acceptance:** grep the Console source and the viewer pod config for the token values — zero hits. An
error or trace carries no token value.
