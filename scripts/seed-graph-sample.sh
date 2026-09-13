#!/usr/bin/env bash
# Creates a populated scratch database so the NFR-03 / NFR-04 checks have something to verify.
#
# The restore check is worthless against an empty graph — an edge count of zero matches zero. This
# clones the dev database's schema (relational tables, the AGE graph, its labels and the cascade
# trigger) and fills it with memories, vertices and edges in the NFR-02 relation-type distribution.
#
# Usage: scripts/seed-graph-sample.sh [target-db] [container] [template-db] [memories] [edges]
#   target-db    database to create (default: nfr03_source)
#   container    Postgres container running the AGE image (default: mimisbrunnr-postgres)
#   template-db  database whose schema is cloned (default: app)
#   memories     memory rows to create (default: 200)
#   edges        edges to create (default: 500)

set -euo pipefail

TARGET_DB="${1:-nfr03_source}"
CONTAINER="${2:-mimisbrunnr-postgres}"
TEMPLATE_DB="${3:-app}"
MEMORIES="${4:-200}"
EDGES="${5:-500}"

if ! docker inspect "$CONTAINER" >/dev/null 2>&1; then
    echo "FAIL: container '$CONTAINER' not found" >&2
    exit 1
fi

PGPASSWORD_VALUE="$(docker inspect "$CONTAINER" \
    --format '{{range .Config.Env}}{{println .}}{{end}}' | sed -n 's/^POSTGRES_PASSWORD=//p')"
PGUSER_VALUE="$(docker inspect "$CONTAINER" \
    --format '{{range .Config.Env}}{{println .}}{{end}}' | sed -n 's/^POSTGRES_USER=//p')"
PGUSER_VALUE="${PGUSER_VALUE:-postgres}"

psql_in() {
    docker exec -i -e PGPASSWORD="$PGPASSWORD_VALUE" "$CONTAINER" \
        psql -U "$PGUSER_VALUE" -v ON_ERROR_STOP=1 "$@"
}

# TEMPLATE copies the schema exactly as migrations left it, including the graph catalog entries a
# hand-written schema script would have to reproduce and could get wrong.
echo "== creating $TARGET_DB from template $TEMPLATE_DB"
psql_in -d postgres -tAc "DROP DATABASE IF EXISTS \"$TARGET_DB\";" >/dev/null
psql_in -d postgres -tAc "CREATE DATABASE \"$TARGET_DB\" TEMPLATE \"$TEMPLATE_DB\";" >/dev/null

echo "== seeding $MEMORIES memories and $EDGES edges"
psql_in -d "$TARGET_DB" <<SQL
LOAD 'age';
SET search_path = ag_catalog, "\$user", public;

INSERT INTO memory_group (uuid, scope_dimension, initiative_id, tickets, created_on)
VALUES (gen_random_uuid(), 'product', 1, '[]'::jsonb, now());

INSERT INTO memory (uuid, lineage_id, group_id, name, description, subject_slug, tags, facets)
SELECT gen_random_uuid(), gen_random_uuid(), (SELECT max(id) FROM memory_group),
       'sample-' || g, 'sample subject ' || g, 'sample-subject-' || g, ARRAY[]::text[], ARRAY[]::text[]
FROM generate_series(1, $MEMORIES) AS g;

INSERT INTO memory_version
    (memory_id, version, is_current, statement, content_summary, blob_address, kind,
     confidence, status, sources, valid_from, created_on)
SELECT m.id, 1, true, 'sample claim ' || m.id, 'sample summary ' || m.id, NULL,
       CASE WHEN m.id % 4 = 0 THEN 'decision' ELSE 'reference' END,
       80, 'approved', '[]'::jsonb, now() - interval '30 days', now()
FROM memory m
WHERE m.name LIKE 'sample-%';

DO \$seed_vertices\$
DECLARE r record;
BEGIN
    FOR r IN SELECT uuid FROM memory WHERE name LIKE 'sample-%' LOOP
        EXECUTE format(
            \$q\$SELECT v FROM ag_catalog.cypher('memory_graph', \$c\$
                CREATE (:Memory {memory_uuid: %L}) RETURN 1
            \$c\$) AS (v agtype)\$q\$, r.uuid::text);
    END LOOP;
END
\$seed_vertices\$;

DO \$seed_edges\$
DECLARE r record;
BEGIN
    FOR r IN
        WITH ordered AS (
            SELECT uuid, row_number() OVER (ORDER BY id) AS rn
            FROM memory WHERE name LIKE 'sample-%'
        )
        SELECT s.uuid AS source_uuid, t.uuid AS target_uuid,
               CASE WHEN g % 20 = 0 THEN 'contradicts'
                    WHEN g % 5 = 0 THEN 'depends_on'
                    WHEN g % 7 = 0 THEN 'supersedes'
                    WHEN g % 11 = 0 THEN 'implements'
                    ELSE 'relates_to' END AS relation,
               'sample seed ' || g AS reason
        FROM generate_series(0, $EDGES - 1) AS g
        JOIN ordered s ON s.rn = (g % $MEMORIES) + 1
        JOIN ordered t ON t.rn = ((g + 1 + (g / $MEMORIES)) % $MEMORIES) + 1
    LOOP
        EXECUTE format(
            \$q\$SELECT v FROM ag_catalog.cypher('memory_graph', \$c\$
                MATCH (s:Memory), (t:Memory)
                WHERE s.memory_uuid = %L AND t.memory_uuid = %L
                CREATE (s)-[:LINKS {relation: %L, reason: %L}]->(t)
                RETURN 1
            \$c\$) AS (v agtype)\$q\$,
            r.source_uuid::text, r.target_uuid::text, r.relation, r.reason);
    END LOOP;
END
\$seed_edges\$;
SQL

psql_in -d "$TARGET_DB" -tAc "
    SELECT 'memories=' || (SELECT count(*) FROM memory)
        || ' vertices=' || (SELECT count(*) FROM memory_graph.\"Memory\")
        || ' edges=' || (SELECT count(*) FROM memory_graph.\"LINKS\");"

echo "== ready. Verify with: scripts/verify-graph-restore.sh $CONTAINER $TARGET_DB"
