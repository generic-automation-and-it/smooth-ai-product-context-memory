# NFR-04 — Postgres / AGE version pairing

Recorded at HLD-003 WT-01 adoption. Update this file whenever either version changes.

| | |
|---|---|
| **Image** | `docker.io/apache/age:release_PG17_1.7.0` |
| **Postgres major** | 17.11 inside the AGE image (Aspire 13.3.0 default was `library/postgres:17.6`; same major, volume compatible) |
| **AGE extension** | 1.7.0 |
| **Supported AGE ceiling (upstream, at adoption)** | Postgres 11–18 for released tags; PG19 exists only as `dev_snapshot_PG19` |
| **Hosts** | Dev AppHost (`smooth-project-memory-dev-postgres`) and test Aspire (`project-test-postgres`) |

## Pre-upgrade check

Before any Postgres major upgrade:

1. Confirm Apache AGE publishes a release tag for the target major (`release_PG<major>_*`, not `dev_snapshot_*`).
2. If no release exists, **block the upgrade** — wait, or remove the extension. Do not silently defer.
3. Recreate the persistent container after the image pin. `ContainerLifetime.Persistent` keeps the previous image until the container is removed.
4. Named volume `smooth-project-memory-postgres-data` is compatible across this pin (same major). A later major bump requires an explicit volume reset; a mismatch refuses to start and looks like a broken image.

## Restore

Graph catalog objects live in the same database as the relational model, so a single `pg_dump` / restore includes them. WT-01 verified a custom-format dump of an empty `memory_graph` round-trips: after restore, `age` 1.7.0 is present and `ag_graph` still lists `memory_graph`. Non-zero edge-count restore is verified in WT-03 after cutover.
