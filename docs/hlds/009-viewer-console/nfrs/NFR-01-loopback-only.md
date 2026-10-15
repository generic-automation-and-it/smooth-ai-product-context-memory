# NFR-01 — Loopback only

| Attribute | Boundary |
|---|---|
| **Attribute** | Boundary / network reach |
| **Target** | The Console is reachable on loopback only |
| **Status** | Draft |

The viewer pod and Caddy bind all interfaces *inside the container* (`:5180`) so the
Aspire/DCP proxy on the internal network can reach the pod; loopback enforcement is the host-side
published port (`ViewerConfiguration:Port`, default 5441), not a bind restriction in the container.
No interface is published on a network-accessible address. The human entry point is a localhost URL.

**Acceptance:** the Console is reachable only on the loopback interface; a request from a non-loopback
source is refused. In Release mode the published host is `EngineBindAddress`, which is configurable —
a non-loopback bind address there weakens this NFR, so release deployments keep the loopback bind.

*(Corrected 2026-10-15: the text used to say the pod and Caddy bind loopback only. The shipped
`Caddyfile` binds `:5180` on all container interfaces; the Aspire published port is what refuses
non-loopback sources. See HLD-009 F7.)*
