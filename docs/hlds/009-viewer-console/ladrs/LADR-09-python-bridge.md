# LADR-09 — The bridge is a Python service that reuses the capture client

## Context

The capture gates (redaction, atomicity, dedup, value gate) are client-side Python in the capture skill.
A Console bridge that presents the review queue, reproduces conflict choices and re-runs `set --dryrun`
must invoke those exact scripts. If the bridge were .NET, it would still have to shell out to Python — a
second runtime for no benefit, and the gates could drift.

## Decision

The bridge is a **small Python HTTP service** in the viewer pod. It imports the capture skill client as a
library and reuses its redactor, atomicity detector, dedup and value gate. It is a presenter and a
driver; it contains no gate logic of its own. The optional value-gate decision model stays in the skill
layer (off by default), so it never lands in the Host or in bridge logic.

## Consequences

- No second runtime for the gates; single source of truth (Python).
- The bridge is thin — it calls the capture pipeline and returns its outcome.
- The value-gate model (when enabled) is invoked, not contained, so the no-model rule holds.
- The scripts enter the image as a build artifact: the viewer `Dockerfile` builds from the repo root
  and copies the shared script folder to `/bridge/capture/` (`PYTHONPATH` set; root `.dockerignore`
  re-includes only that folder). Nothing is edited inside the image, so HLD-010 single-source holds.
  Decided 2026-10-15 (HLD-009 F2); the stub bridge does not import them yet.

## Status

Draft
