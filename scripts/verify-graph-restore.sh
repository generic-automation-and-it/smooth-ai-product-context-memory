#!/usr/bin/env bash
# NFR-03 restore verification — one backup must round-trip relational rows AND graph edges.
#
# A backup that silently omits graph data looks entirely successful until the first traversal after a
# restore, which is the worst possible moment to discover it. This script fails loudly instead: it
# asserts a non-zero edge count in the restored database and that it matches the source.
#
# Usage: scripts/verify-graph-restore.sh [container] [source-db]
#   container  Postgres container running the AGE image (default: mimisbrunnr-postgres)
#   source-db  populated database to back up (default: app)
#
# Requires: docker. pg_dump/pg_restore/psql run inside the container, so no host client is needed.

set -euo pipefail

CONTAINER="${1:-mimisbrunnr-postgres}"
SOURCE_DB="${2:-app}"
RESTORE_DB="restore_check_$(date +%s)"
DUMP_PATH="/tmp/${RESTORE_DB}.dump"

if ! docker inspect "$CONTAINER" >/dev/null 2>&1; then
    echo "FAIL: container '$CONTAINER' not found" >&2
    exit 1
fi

PGPASSWORD_VALUE="$(docker inspect "$CONTAINER" \
    --format '{{range .Config.Env}}{{println .}}{{end}}' \
    | sed -n 's/^POSTGRES_PASSWORD=//p')"
PGUSER_VALUE="$(docker inspect "$CONTAINER" \
    --format '{{range .Config.Env}}{{println .}}{{end}}' \
    | sed -n 's/^POSTGRES_USER=//p')"
PGUSER_VALUE="${PGUSER_VALUE:-postgres}"

psql_in() {
    docker exec -e PGPASSWORD="$PGPASSWORD_VALUE" "$CONTAINER" \
        psql -U "$PGUSER_VALUE" -v ON_ERROR_STOP=1 "$@"
}

# Edge and vertex counts are read from AGE's storage tables rather than through cypher: the point is
# to prove the rows survived the dump, and a plain count cannot be confused by session setup.
count_edges() {
    psql_in -d "$1" -tAc 'SELECT count(*) FROM memory_graph."LINKS";'
}

count_vertices() {
    psql_in -d "$1" -tAc 'SELECT count(*) FROM memory_graph."Memory";'
}

count_memories() {
    psql_in -d "$1" -tAc 'SELECT count(*) FROM memory;'
}

cleanup() {
    docker exec "$CONTAINER" rm -f "$DUMP_PATH" >/dev/null 2>&1 || true
    psql_in -d postgres -tAc "DROP DATABASE IF EXISTS \"$RESTORE_DB\";" >/dev/null 2>&1 || true
}
trap cleanup EXIT

echo "== source: $SOURCE_DB"
SRC_MEMORIES="$(count_memories "$SOURCE_DB")"
SRC_VERTICES="$(count_vertices "$SOURCE_DB")"
SRC_EDGES="$(count_edges "$SOURCE_DB")"
echo "   memory rows: $SRC_MEMORIES | vertices: $SRC_VERTICES | edges: $SRC_EDGES"

if [ "$SRC_EDGES" -eq 0 ]; then
    echo "FAIL: source has zero edges — a restore check against an empty graph proves nothing" >&2
    echo "      populate relationships first, then re-run" >&2
    exit 1
fi

echo "== backup (custom format, single dump covering both models)"
docker exec -e PGPASSWORD="$PGPASSWORD_VALUE" "$CONTAINER" \
    pg_dump -U "$PGUSER_VALUE" -d "$SOURCE_DB" -Fc -f "$DUMP_PATH"
DUMP_BYTES="$(docker exec "$CONTAINER" stat -c %s "$DUMP_PATH")"
echo "   $DUMP_PATH ($DUMP_BYTES bytes)"

echo "== restore into a fresh, empty database: $RESTORE_DB"
psql_in -d postgres -tAc "CREATE DATABASE \"$RESTORE_DB\";" >/dev/null
docker exec -e PGPASSWORD="$PGPASSWORD_VALUE" "$CONTAINER" \
    pg_restore -U "$PGUSER_VALUE" -d "$RESTORE_DB" --no-owner --no-privileges "$DUMP_PATH"

DST_MEMORIES="$(count_memories "$RESTORE_DB")"
DST_VERTICES="$(count_vertices "$RESTORE_DB")"
DST_EDGES="$(count_edges "$RESTORE_DB")"
echo "   memory rows: $DST_MEMORIES | vertices: $DST_VERTICES | edges: $DST_EDGES"

echo "== traversal against the restored database"
TRAVERSAL_HOPS="$(psql_in -d "$RESTORE_DB" -tAc "
    LOAD 'age';
    SET search_path = ag_catalog, \"\$user\", public;
    SELECT count(*) FROM ag_catalog.cypher('memory_graph', \$\$
        MATCH p = (s:Memory)-[:LINKS*1..2]->(t:Memory)
        RETURN nodes(p)
    \$\$) AS (n agtype);" | tail -1)"
echo "   depth-1..2 paths found: $TRAVERSAL_HOPS"

FAILED=0
assert_equal() {
    if [ "$2" != "$3" ]; then
        echo "FAIL: $1 — source $2, restored $3" >&2
        FAILED=1
    else
        echo "OK:   $1 matches ($2)"
    fi
}

assert_equal "relational row count (memory)" "$SRC_MEMORIES" "$DST_MEMORIES"
assert_equal "graph vertex count" "$SRC_VERTICES" "$DST_VERTICES"
assert_equal "graph edge count" "$SRC_EDGES" "$DST_EDGES"

if [ "$DST_EDGES" -eq 0 ]; then
    echo "FAIL: restored edge count is zero — the backup omitted graph data" >&2
    FAILED=1
fi

if [ "$TRAVERSAL_HOPS" -eq 0 ]; then
    echo "FAIL: no traversal result after restore" >&2
    FAILED=1
fi

if [ "$FAILED" -ne 0 ]; then
    echo "== NFR-03 restore verification FAILED"
    exit 1
fi

echo "== NFR-03 restore verification PASSED"
