---
slug: stale-image-trap
description: A healthy container may run a stale build even when every health check is green.
question: Why does a green container 404 on a newly added endpoint?
scope: portable
confidence: verified
provenance:
  learned: 2026-09-30
  session: walk-run-3
  source: a container that answered but served a stale image
  supersedes: walkstore-20260930-0900
updated: 2026-09-30
---

# Stale-image trap

## Answer

A healthy container may run a stale build; verify freshness by calling a newly added endpoint.

## Why

Green health checks prove liveness, not freshness.

## Boundaries

A green health check on a container does not prove it serves the latest build.
