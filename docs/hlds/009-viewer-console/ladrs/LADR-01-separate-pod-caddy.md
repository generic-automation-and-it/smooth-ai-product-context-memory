# LADR-01 — Viewer is a separate pod fronted by Caddy

## Context

The Host is an ASP.NET Core API container, Session-lifetime in the AppHost (recreated on restart), loopback
by design. Adding the UI bundle and a proxy to it would couple the UI release to the API release and bloat
the API image.

## Decision

Deploy the Console as its own **viewer pod** fronted by **Caddy**. Caddy serves the static SPA on a single
entry published on loopback, reverse-proxies `/api` to the Host, and forwards the session's credential
(LADR-02). The viewer pod also runs a
small bridge that drives the capture pipeline.

Loopback here means the *published* port, not the in-container bind: Caddy listens on all container
interfaces (`:5180`) so the Aspire/DCP proxy can reach it, and the AppHost publishes that port on
loopback only (NFR-01). A `bind 127.0.0.1` inside the container would cut the pod off from its own
proxy with no security gain.

## Consequences

- The Host image stays pure (no UI bundle, no proxy).
- One loopback URL is the human entry point, like the Aspire dashboard.
- One more container in the stack, and a Caddy dependency.
- The viewer pod releases independently of the API.

## Status

Draft
