# LADR-07: Surfaces split by liveness — HTTP from the running Host; one-shot containers for verify and restore

**Status:** Draft

## Context

Users run the published Docker release; they do not have a .NET SDK, so `dotnet run` verbs are a
development convenience, not a deliverable mechanism. At the same time the four operations divide
by what they may assume alive: preflight and snapshot report on and read from a healthy store,
while verify and restore exist precisely for the scenarios where the store — or the whole stack —
is dead. The recovery mechanism must have strictly fewer prerequisites than the thing it recovers.

## Decision

**Split** the surfaces by liveness, delivered through the one Host image already published:

- **`preflight` and `snapshot` are HTTP endpoints on the running Host container.** Preflight is a
  read-only report (snapshot age, corpus counts, orphan/dangling numbers) — the mechanism that
  makes BR-37's "recency visible without asking" real, and invokable by the skill as a thin
  caller. Snapshot reads both stores through the app's own storage abstraction and writes the
  artefact to a mounted volume, with the destination reported in the response. Long-running
  snapshot uses accepted-then-poll, not a held connection.
- **`verify` and `restore` run as one-shot containers from the same image**
  (`docker run --rm <image> verify|restore <archive>`): an alternate entrypoint argument, exiting
  when done. Verify needs only the archive mounted — no database, no object store, no network.
  Restore needs the archive plus store connection configuration, and runs while no API container
  is serving; it owns its connections exclusively, enforces the empty-target refusal, and prints
  its reconciliation to the container log.

Development retains `dotnet run --project … -- <verb>` for the same verbs — the container
entrypoint and the CLI verb are one code path, not two implementations.

## Alternatives Considered

- **All four as HTTP endpoints** — restore under a serving API means dropping the database beneath an active connection pool, and makes recovery depend on a healthy service; HTTP verify asks the system whose survival is in question to certify its own backup.
- **All four as CLI/`dotnet` verbs only** — unusable from the Docker release without an SDK; also forfeits skill-invokable preflight, weakening BR-37's visibility clause.
- **A separate tooling image** — a second image to version, publish and keep in lock-step with the storage abstraction; the single-image alternate-entrypoint form costs nothing extra.

## Consequences

- One published image serves both modes; nothing new to release.
- The disaster path (`verify`, `restore`) works on a machine with only Docker and the archive.
- Snapshot and preflight responses must stay content-free — counts, hashes, ages, destination path only (NFR-03).
- Restore's "no API serving" precondition is operational, not enforceable by the tool itself; the runbook and the empty-target refusal are the guards.
- The HTTP snapshot endpoint gives the artefact-producing path a network surface — accepted because the Host binds locally in the single-user deployment, and the artefact itself never transits HTTP.

## Related

- **LADR-04** — preflight is the visibility mechanism it requires.
- **LADR-05** — the printed reconciliation becomes the one-shot container's log output.
