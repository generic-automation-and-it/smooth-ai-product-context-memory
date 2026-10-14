# LADR-01 — Viewer is a separate pod fronted by Caddy

## Context

The Host is an ASP.NET Core API container, Session-lifetime in the AppHost (recreated on restart), loopback
by design. Adding the UI bundle and a proxy to it would couple the UI release to the API release and bloat
the API image.

## Decision

Deploy the Console as its own **viewer pod** fronted by **Caddy**. Caddy serves the static SPA on a single
loopback entry, reverse-proxies `/api` to the Host, and injects the read token. The viewer pod also runs a
small bridge that drives the capture pipeline.

## Consequences

- The Host image stays pure (no UI bundle, no proxy).
- One loopback URL is the human entry point, like the Aspire dashboard.
- One more container in the stack, and a Caddy dependency.
- The viewer pod releases independently of the API.

## Status

Draft
