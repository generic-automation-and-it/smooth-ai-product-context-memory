---
slug: stale-image-trap
description: A healthy container may run a stale build even when every health check is green.
question: Why does a green container 404 on a newly added endpoint?
scope: portable
confidence: verified
provenance:
  learned: 2026-09-10
  session: walk-run-1
  source: a container that answered but served a stale image
updated: 2026-09-10
---

# Stale-image trap

## Answer

A healthy container is always fresh.

## Why

Health checks prove liveness, not freshness.

## Boundaries

A container rebuilt from source is always current.
