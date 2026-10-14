# NFR-01 — Loopback only

| Attribute | Boundary |
|---|---|
| **Attribute** | Boundary / network reach |
| **Target** | The Console binds loopback only |
| **Status** | Draft |

The viewer pod and Caddy bind `localhost`/loopback only. No interface is bound to a network-accessible
address. The human entry point is a localhost URL.

**Acceptance:** the Console is reachable only on the loopback interface; a request from a non-loopback
source is refused.
