---
slug: graph-over-joins
description: The graph store models a path, and a relational join cannot express that chain.
question: What is the graph store's relationship to relational joins?
scope: portable
confidence: verified
provenance:
  learned: 2026-09-30
  session: walk-run-3
  source: the provenance-path design review
updated: 2026-09-30
---

# Graph over joins

## Answer

The graph is a path, not a join.

## Why

A relational join cannot express the chain from measurement to decision.

## Boundaries

Applies to the provenance path between a measurement and a decision.
