# NFR-04 — Postgres / AGE version pairing

Recorded at HLD-003 foundation adoption. Update this file whenever either version changes.

| | |
|---|---|
| **Image** | `docker.io/apache/age:release_PG17_1.7.0` |
| **Postgres version** | 17 — 17.11 inside the AGE image (Aspire 13.3.0 default was `library/postgres:17.6`; same major, volume compatible) |
| **AGE extension** | 1.7.0 |
| **Supported AGE ceiling (upstream)** | Postgres 11–18 for released tags — `release_PG18_1.8.0` and `release_PG18_1.7.0` both published; PG19 exists only as `dev_snapshot_PG19`. Re-checked 2026-09-13. |
| **Newest PG17 release tag** | `release_PG17_1.7.0` — the pin *is* the newest PG17 release (only `release_PG17_1.6.0` precedes it), so there is no newer minor to move to |
| **Hosts** | Dev AppHost (`mimisbrunnr-postgres`) and test Aspire (`mimisbrunnr-testcontainer-postgres`) |

## Pre-upgrade check

The pairing is also asserted at runtime: `MigrateSmoothAiProductContextMemoryAsync` verifies
PG major 17 + AGE `1.7.0` (constants in `AgeSession`) after applying migrations and fails
startup on mismatch. A version bump therefore updates `AgeSession` constants, this file, and
the image pins in both Aspire hosts together.

Before any Postgres major upgrade:

0. Run `scripts/verify-graph-preupgrade.sh <target-image>` — it performs steps 1 and 2 below, and for a
   minor bump also round-trips a populated backup into the target image and traverses it.
1. Confirm Apache AGE publishes a release tag for the target major (`release_PG<major>_*`, not `dev_snapshot_*`).
2. If no release exists, **block the upgrade** — wait, or remove the extension. Do not silently defer.
3. Recreate the persistent container after the image pin. `ContainerLifetime.Persistent` keeps the previous image until the container is removed.
4. Named volume `mimisbrunnr-postgres-data` is compatible across this pin (same major). A later major bump requires an explicit volume reset; a mismatch refuses to start and looks like a broken image.

## Restore

Graph catalog objects live in the same database as the relational model, so a single `pg_dump` / restore
includes them. At adoption, a custom-format dump of an *empty* `memory_graph` was verified to round-trip.

**The non-zero edge-count restore is now verified** — 500 edges over 200 vertices dumped and restored
into an empty database with counts matching and traversal working, recorded in
[NFR-03-restore-verification.md](./NFR-03-restore-verification.md).

## Verification runs (2026-09-13)

[`scripts/verify-graph-preupgrade.sh`](../../../../scripts/verify-graph-preupgrade.sh) was exercised on
all three paths it can take, against a populated database:

| Target image | Classification | Outcome |
|---|---|---|
| `docker.io/apache/age:release_PG17_1.7.0` | minor, within major 17 | **PASSED** — dumped from the running minor, restored into a fresh container of the target image, 500 edges preserved, 1,797 depth-1..2 paths traversed, `memory_graph` present, `age` 1.7.0 present, **nothing recreated** |
| `docker.io/apache/age:release_PG18_1.8.0` | major, 17 → 18 | correctly refused to round-trip and emitted the major-upgrade steps: reset the named volume deliberately, and update `AgeSession.PostgresMajor`, this file and both Aspire hosts together |
| `docker.io/apache/age:dev_snapshot_PG19` | development snapshot | correctly **BLOCKED**, exit 1 — "wait for a `release_PG<major>_*` tag, or record the explicit decision to remove the extension. Do not silently defer." |

The minor round trip used the same image as source and target, because
`release_PG17_1.7.0` is the newest PG17 release published — there is no newer minor to upgrade *to*. The
check exercises the whole dump → fresh instance → restore → traverse path regardless, which is the part
that could break.

**AGE does not currently pin us below a supported PostgreSQL.** PG18 has released tags, so the next
major is available whenever we want it; only PG19 would be blocked today, and the script blocks it
loudly rather than deferring quietly.
