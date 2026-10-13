# LADR-04 — The SPA is static, no-build, vendored libs, served by Caddy

## Context

The store is single-user and local. The product's value is "first run outranks hardening"; a Node build
toolchain and a second deployable add cost with little benefit for an internal human surface. The product
is also deliberately local-first and offline-capable.

## Decision

The SPA is a static, no-build asset (modules + vendored libraries) served by Caddy. Graph rendering uses a
vendored force-graph library (Cytoscape.js or vis-network). No Node build, no npm install at runtime, no
second artifact beyond the viewer pod image.

## Consequences

- Zero build toolchain; the SPA is committed and served as-is.
- Vendored libs keep the page offline-capable (no CDN dependency).
- A no-build page carries a maintenance ceiling; if the management half grows large, a build step can be
  introduced without changing the architecture (LADR decisions long-lived).

## Status

Draft
