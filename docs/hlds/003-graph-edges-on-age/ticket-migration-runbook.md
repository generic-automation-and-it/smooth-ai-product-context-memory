# Ticket Ownership Migration Runbook

Preflight for `20260914180000_AddTicketGraph`, governed by
[LADR-08](./ladrs/LADR-08-captured-ticket-hierarchy.md). Legacy concurrent ticket attaches
could leave the same exact provider/key on different groups. The migration deliberately
fails with `23505` rather than choosing an owner. This runbook does not change that guard.

## Before Host Startup

1. Schedule a maintenance window. Stop **all writers**: Host replicas, workers, agent capture,
   direct SQL sessions and automatic restarts. Exiting AppHost alone does not stop persistent
   containers; ensure no Host remains. Keep PostgreSQL running. Do not start the new Host yet:
   startup applies migrations before serving requests.
2. Confirm the intended container/database and version. Take a full PostgreSQL backup covering
   relational data and AGE graph objects; verify restoration in an isolated database using the
   established [restore procedure](./nfrs/NFR-03-restore-verification.md). Retain the backup securely.
   Do not seed, reset, drop, or clone over the source database.
3. Run the read-only preflight from the repository root:

```bash
scripts/check-ticket-ownership.sh                         # mimisbrunnr-postgres / app
scripts/check-ticket-ownership.sh "postgres-container" "database-name"
```

Docker and Bash are required on the host; `psql` runs inside the supplied container. The
container's `POSTGRES_USER` (default `postgres`) and `POSTGRES_PASSWORD` provide credentials,
without `docker inspect`, host-side password extraction or password arguments. Connection uses
TCP `127.0.0.1:5432` inside the container. The supplied database is a literal name, not a URI or
libpq connection string. Secret-file-only/custom-port deployments need an approved connection
setup; do not print credentials to diagnose them. Disable shell tracing/session recording.

| Exit | Meaning | Operator action |
|---|---|---|
| `0` | No cross-group duplicates or malformed memberships in the snapshot | Continue only while writers remain stopped |
| `1` | At least one exact identity has multiple distinct owners | Block startup; adjudicate each identity |
| `2` | Malformed data, connection/SQL/tool failure or incomplete output | Block startup; investigate and rerun |

The report is one JSON object. `duplicates` contains exact `provider`, `key` and distinct sorted
`groupUuids`; `malformed` contains group UUID, one-based array `position` (null for an invalid
container), and a shape error, not the corrupt object. Malformed data takes exit precedence but
does not hide valid duplicate identities. Empty arrays are clean; SQL/JSON null, non-array
containers, non-object entries, or missing/null/non-string identity fields are blockers.
String identities are compared with `C` collation: no trimming, case folding, URL matching or
new 512-character limit on legacy data. Repeated entries inside one group are not a conflict.

**This output is sensitive operational data.** JSON escaping preserves quotes, whitespace and
control characters without treating ticket strings as SQL or terminal commands. Do not copy
reports into PRs, application logs, traces, shared terminals or CI artifacts. The check uses one
read-only repeatable-read transaction, no data writes or AGE calls, and rolls back before
reporting success. Its 60-second statement / 5-second lock timeouts fail closed. A timeout is not
proof of clean data; arrange an approved diagnostic for a larger store, never bypass the guard.

## Choose Ownership

For each duplicate, an authorized operator must select the exact keeper group based on intended
scope and provenance. Record that decision in a restricted change record, together with every
losing group and its original full `tickets` JSONB. Inspect associated memories/history before
approving: removing a membership does not move or delete memories, but **ticket lookup will no
longer discover the losing group's memories via that ticket**. Never merge/delete groups or
memories to make the precheck pass; never select the first/minimum UUID automatically.

Malformed data requires separate operator adjudication against the backup and original intent.
Do not coerce numeric/null identities, trim strings or discard unknown objects. The duplicate
procedure below is not a malformed-data repair. Resolve those blockers first, then rerun preflight.

## Guarded Manual Repair

**Use this procedure only before ticket graph installation.** If the migration was already
attempted, its transaction-suppressed label creation may have committed even though backfill
failed. Inspect that state separately; the template deliberately refuses it. Do not drop labels,
disable triggers or run Down merely to bypass this check.

With a graph already installed, group-ticket triggers serialize on advisory lock `(734921, 1)`
and maintain identities. Removing the last membership of a ticket deletes its vertex and **all
incident `TICKET_PARENT` edges in the same transaction**; hierarchy is current state, not history.
Removing a losing membership while an exact keeper remains should retain that identity, but
remaining duplicates can still make a trigger reject the update. Post-migration repairs require
a separately reviewed plan covering existing declarations and trigger effects. Do not reuse this
pre-migration template or manipulate graph rows directly.

Connect through an approved interactive `psql -X -v ON_ERROR_STOP=1` session with passwords from
the container environment, not command arguments. For the supported container setup:

```bash
docker exec -it "postgres-container" sh -c '
  set +x
  export PGPASSWORD="${POSTGRES_PASSWORD-}" PGUSER="${POSTGRES_USER:-postgres}"
  export PGDATABASE="$1" PGHOST=127.0.0.1 PGPORT=5432 PGCONNECT_TIMEOUT=10
  exec psql -X -w -v ON_ERROR_STOP=1
' ticket-repair "database-name"
```

Review and run the template below for **one exact identity and one losing group per transaction**.
Use `\prompt` to set the seven psql variables before beginning; values are interpolated as quoted
SQL literals (`:'name'`), never as SQL code or inside a dollar-quoted block:

```text
\prompt 'Exact provider: ' provider
\prompt 'Exact key: ' key
\prompt 'Keeper UUID: ' keeper
\prompt 'One losing UUID: ' loser
\prompt 'All current owner UUIDs as {uuid,uuid,...}: ' owners
\prompt 'Full expected losing tickets JSON array (one line): ' before
\prompt 'Expected number of this identity in losing array: ' removed
```

For embedded newlines/control characters in an identity, use an approved parameter-input file
with safe psql literal quoting; do not paste raw values into SQL. Keep files restricted and outside
the repository. The `before` value must be the entire original JSONB array, including all object
fields and order, not a reconstructed provider/key subset. `owners` must contain the complete
current owner set with no repeated UUID. After each committed losing-group repair, rerun preflight
and refresh these inputs before repairing another group.

```sql
BEGIN;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '60s';
SELECT pg_advisory_xact_lock(734921, 1);
LOCK TABLE public.memory_group IN SHARE ROW EXCLUSIVE MODE NOWAIT;

CREATE TEMP TABLE ticket_repair_plan ON COMMIT DROP AS
SELECT :'provider'::text COLLATE "C" AS provider, :'key'::text COLLATE "C" AS key,
       :'keeper'::uuid AS keeper, :'loser'::uuid AS loser,
       :'owners'::uuid[] AS owners, :'before'::jsonb AS before,
       :'removed'::integer AS removed;

DO $repair$
DECLARE
    p record;
    actual_owners uuid[];
    original jsonb;
    replacement jsonb;
    removed_count integer;
    affected integer;
BEGIN
    IF to_regclass('memory_graph."Ticket"') IS NOT NULL
       OR to_regclass('memory_graph."TICKET_PARENT"') IS NOT NULL
       OR EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = 'public.memory_group'::regclass
                  AND tgname IN ('trg_ticket_graph_lock', 'trg_ticket_graph_membership'))
       OR EXISTS (SELECT 1 FROM public."__EFMigrationsHistory"
                  WHERE "MigrationId" = '20260914180000_AddTicketGraph') THEN
        RAISE EXCEPTION 'Not a pre-ticket-migration database; stop and review graph state';
    END IF;

    SELECT * INTO STRICT p FROM ticket_repair_plan;
    IF p.keeper = p.loser OR p.removed < 1 OR cardinality(p.owners) < 2
       OR NOT (p.keeper = ANY(p.owners)) OR NOT (p.loser = ANY(p.owners)) THEN
        RAISE EXCEPTION 'Invalid operator ownership plan';
    END IF;
    IF EXISTS (SELECT 1 FROM public.memory_group
               WHERE jsonb_typeof(tickets) IS DISTINCT FROM 'array') THEN
        RAISE EXCEPTION 'Malformed ticket container; stop and adjudicate';
    END IF;
    IF EXISTS (
        SELECT 1 FROM public.memory_group g CROSS JOIN LATERAL jsonb_array_elements(g.tickets) t
        WHERE jsonb_typeof(t) IS DISTINCT FROM 'object'
           OR jsonb_typeof(t->'provider') IS DISTINCT FROM 'string'
           OR jsonb_typeof(t->'key') IS DISTINCT FROM 'string'
    ) THEN
        RAISE EXCEPTION 'Malformed ticket identity; stop and adjudicate';
    END IF;
    SELECT array_agg(g.uuid ORDER BY g.uuid) INTO actual_owners
    FROM public.memory_group g WHERE EXISTS (
        SELECT 1 FROM jsonb_array_elements(g.tickets) t
        WHERE t->>'provider' COLLATE "C" = p.provider AND t->>'key' COLLATE "C" = p.key
    );
    IF actual_owners IS DISTINCT FROM (SELECT array_agg(u ORDER BY u) FROM unnest(p.owners) u) THEN
        RAISE EXCEPTION 'Owner set changed or incomplete; refresh preflight and approval';
    END IF;
    SELECT tickets INTO STRICT original FROM public.memory_group WHERE uuid = p.loser;
    IF original IS DISTINCT FROM p.before THEN
        RAISE EXCEPTION 'Losing membership array changed; refresh approval';
    END IF;
    SELECT COALESCE(jsonb_agg(t ORDER BY ord) FILTER (WHERE NOT (
               t->>'provider' COLLATE "C" = p.provider AND t->>'key' COLLATE "C" = p.key
           )), '[]'::jsonb),
           count(*) FILTER (WHERE t->>'provider' COLLATE "C" = p.provider
                             AND t->>'key' COLLATE "C" = p.key)
    INTO replacement, removed_count
    FROM jsonb_array_elements(original) WITH ORDINALITY AS entry(t, ord);
    IF removed_count <> p.removed THEN
        RAISE EXCEPTION 'Unexpected number of losing memberships';
    END IF;
    UPDATE public.memory_group SET tickets = replacement
    WHERE uuid = p.loser AND tickets = p.before;
    GET DIAGNOSTICS affected = ROW_COUNT;
    IF affected <> 1 THEN
        RAISE EXCEPTION 'Expected exactly one changed group';
    END IF;
    IF (SELECT tickets FROM public.memory_group WHERE uuid = p.loser) IS DISTINCT FROM replacement
       OR EXISTS (SELECT 1 FROM public.memory_group g,
                  LATERAL jsonb_array_elements(g.tickets) t
                  WHERE g.uuid = p.loser AND t->>'provider' COLLATE "C" = p.provider
                    AND t->>'key' COLLATE "C" = p.key)
       OR NOT EXISTS (SELECT 1 FROM public.memory_group g,
                      LATERAL jsonb_array_elements(g.tickets) t
                      WHERE g.uuid = p.keeper AND t->>'provider' COLLATE "C" = p.provider
                        AND t->>'key' COLLATE "C" = p.key) THEN
        RAISE EXCEPTION 'Post-update verification failed';
    END IF;
END
$repair$;

SELECT g.uuid, p.before AS original_tickets, g.tickets AS proposed_tickets,
       p.removed AS removed_memberships
FROM public.memory_group g JOIN ticket_repair_plan p ON g.uuid = p.loser;
-- Transaction remains open. Inspect the restricted output before deciding below.
```

The template changes only `tickets` on one losing group. `WITH ORDINALITY` and ordered
`jsonb_agg(t)` retain every other JSON object (including unknown fields/URLs) and its array order;
JSONB does not preserve textual object-key formatting in the first place. All occurrences of the
chosen identity are removed from that losing array, not from the keeper. No memory, version,
history, group metadata, or keeper row is updated. The full preimage and exact owner/count guards
prevent applying stale or incomplete decisions. Lock contention (`55P03`) means stop writers and
retry, not remove the lock. Any SQL error aborts the transaction: issue `ROLLBACK;` and investigate.

**First perform a rehearsal:** inspect the proposed result, issue `ROLLBACK;`, then verify the
original arrays and preflight output are unchanged. An external checker cannot see an uncommitted
repair, so it is not a substitute for the in-transaction checks. After reviewing the rehearsal,
rerun the transaction with fresh approved inputs, inspect the result again, and only then issue
the separate, explicit command:

```sql
COMMIT;
```

If anything is unexpected, use `ROLLBACK;` instead. Do not leave an open transaction unattended.
Do not wrap the template and `COMMIT` into an unattended script.

## Resume Upgrade

Keep writers stopped. Rerun `check-ticket-ownership.sh` after committed repairs until exit `0`;
retain the restricted decision/verification evidence. Start one new Host instance to apply the
migration. Verify startup/migration success before restoring other writers. A clean preflight
establishes membership shape and ownership only, not every migration precondition. If migration
fails, leave writers stopped, inspect partial state, and recover from the approved backup/plan;
do not weaken the duplicate guard or silently select a different owner.
