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

count_label() {
    psql_in -d "$1" -tAc "SELECT count(*) FROM memory_graph.\"$2\";"
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
SRC_PN_VERTICES="$(count_label "$SOURCE_DB" Product)"
SRC_PN_INIT="$(count_label "$SOURCE_DB" Initiative)"
SRC_PN_REPO="$(count_label "$SOURCE_DB" Repo)"
SRC_HAS_INIT="$(count_label "$SOURCE_DB" HAS_INITIATIVE)"
SRC_HAS_REPO="$(count_label "$SOURCE_DB" HAS_REPO)"
SRC_ABOUT="$(count_label "$SOURCE_DB" ABOUT)"
echo "   product-name vertices: product=$SRC_PN_VERTICES initiatives=$SRC_PN_INIT repos=$SRC_PN_REPO | HAS_INITIATIVE=$SRC_HAS_INIT HAS_REPO=$SRC_HAS_REPO | ABOUT=$SRC_ABOUT"
if [ "$SRC_EDGES" -eq 0 ] && [ "$SRC_HAS_INIT" -eq 0 ] && [ "$SRC_HAS_REPO" -eq 0 ] && [ "$SRC_ABOUT" -eq 0 ]; then
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
DST_PN_VERTICES="$(count_label "$RESTORE_DB" Product)"
DST_PN_INIT="$(count_label "$RESTORE_DB" Initiative)"
DST_PN_REPO="$(count_label "$RESTORE_DB" Repo)"
DST_HAS_INIT="$(count_label "$RESTORE_DB" HAS_INITIATIVE)"
DST_HAS_REPO="$(count_label "$RESTORE_DB" HAS_REPO)"
DST_ABOUT="$(count_label "$RESTORE_DB" ABOUT)"
echo "   product-name vertices: product=$DST_PN_VERTICES initiatives=$DST_PN_INIT repos=$DST_PN_REPO | HAS_INITIATIVE=$DST_HAS_INIT HAS_REPO=$DST_HAS_REPO | ABOUT=$DST_ABOUT"

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
assert_equal "product vertex count" "$SRC_PN_VERTICES" "$DST_PN_VERTICES"
assert_equal "initiative vertex count" "$SRC_PN_INIT" "$DST_PN_INIT"
assert_equal "repo vertex count" "$SRC_PN_REPO" "$DST_PN_REPO"
assert_equal "HAS_INITIATIVE edge count" "$SRC_HAS_INIT" "$DST_HAS_INIT"
assert_equal "HAS_REPO edge count" "$SRC_HAS_REPO" "$DST_HAS_REPO"
assert_equal "ABOUT edge count" "$SRC_ABOUT" "$DST_ABOUT"

if [ "$DST_EDGES" -eq 0 ] && [ "$DST_HAS_INIT" -eq 0 ] && [ "$DST_HAS_REPO" -eq 0 ] && [ "$DST_ABOUT" -eq 0 ]; then
    echo "FAIL: restored edge count is zero — the backup omitted graph data" >&2
    FAILED=1
fi

if [ "$SRC_EDGES" -gt 0 ] && [ "$TRAVERSAL_HOPS" -eq 0 ]; then
    echo "FAIL: no traversal result after restore" >&2
    FAILED=1
fi

if [ "$FAILED" -ne 0 ]; then
    echo "== NFR-03 restore verification FAILED"
    exit 1
fi

echo "== NFR-03 restore verification PASSED"
