#!/usr/bin/env bash
# NFR-04 pre-upgrade check — run BEFORE any PostgreSQL version change.
#
# The extension must never become the reason the database cannot be upgraded, and the way that happens
# is silently: someone bumps the image, the extension has no release for the target major, and the
# upgrade is quietly deferred instead of the trade being recorded. This script makes the ceiling
# explicit and, for a minor bump, proves graph objects survive a restore without recreation.
#
# Usage: scripts/verify-graph-preupgrade.sh <target-image> [container] [source-db]
#   target-image  image to upgrade to, e.g. docker.io/apache/age:release_PG17_1.7.0
#   container     currently-running Postgres container (default: mimisbrunnr-postgres)
#   source-db     populated database to round-trip (default: app)
#
# Requires: docker. Everything else runs inside containers.

set -euo pipefail

TARGET_IMAGE="${1:-}"
CONTAINER="${2:-mimisbrunnr-postgres}"
SOURCE_DB="${3:-app}"

if [ -z "$TARGET_IMAGE" ]; then
    echo "usage: $0 <target-image> [container] [source-db]" >&2
    echo "  e.g. $0 docker.io/apache/age:release_PG17_1.7.0" >&2
    exit 2
fi

if ! docker inspect "$CONTAINER" >/dev/null 2>&1; then
    echo "FAIL: container '$CONTAINER' not found" >&2
    exit 1
fi

PGPASSWORD_VALUE="$(docker inspect "$CONTAINER" \
    --format '{{range .Config.Env}}{{println .}}{{end}}' | sed -n 's/^POSTGRES_PASSWORD=//p')"
PGUSER_VALUE="$(docker inspect "$CONTAINER" \
    --format '{{range .Config.Env}}{{println .}}{{end}}' | sed -n 's/^POSTGRES_USER=//p')"
PGUSER_VALUE="${PGUSER_VALUE:-postgres}"

psql_current() {
    docker exec -e PGPASSWORD="$PGPASSWORD_VALUE" "$CONTAINER" \
        psql -U "$PGUSER_VALUE" -v ON_ERROR_STOP=1 "$@"
}

echo "== step 1: record the pairing in use"
CURRENT_IMAGE="$(docker inspect "$CONTAINER" --format '{{.Config.Image}}')"
CURRENT_PG_FULL="$(psql_current -d postgres -tAc "SHOW server_version;")"
CURRENT_PG_MAJOR="$(psql_current -d postgres -tAc "SELECT current_setting('server_version_num')::int / 10000;")"
CURRENT_AGE="$(psql_current -d "$SOURCE_DB" -tAc "SELECT extversion FROM pg_extension WHERE extname = 'age';")"
echo "   image:    $CURRENT_IMAGE"
echo "   postgres: $CURRENT_PG_FULL (major $CURRENT_PG_MAJOR)"
echo "   age:      ${CURRENT_AGE:-<not installed>}"

echo "== step 2: is the target major supported by a released extension tag?"
# release_PG<major>_<x.y.z> is a release; dev_snapshot_* is not, and must not be used to justify an
# upgrade. Parsed from the target image tag rather than guessed.
TARGET_TAG="${TARGET_IMAGE##*:}"
case "$TARGET_TAG" in
    release_PG*)
        TARGET_PG_MAJOR="$(printf '%s' "$TARGET_TAG" | sed -n 's/^release_PG\([0-9]\{1,\}\)_.*$/\1/p')"
        echo "   target tag is a release tag for PostgreSQL major $TARGET_PG_MAJOR"
        ;;
    dev_snapshot_*)
        echo "FAIL: '$TARGET_TAG' is a development snapshot, not a release." >&2
        echo "      BLOCK the upgrade. Wait for a release_PG<major>_* tag, or record the explicit" >&2
        echo "      decision to remove the extension. Do not silently defer." >&2
        exit 1
        ;;
    *)
        echo "FAIL: cannot tell from tag '$TARGET_TAG' whether the extension supports the target." >&2
        echo "      BLOCK the upgrade until the pairing is established and recorded." >&2
        exit 1
        ;;
esac

if [ -z "${TARGET_PG_MAJOR:-}" ]; then
    echo "FAIL: could not parse a PostgreSQL major from '$TARGET_TAG'" >&2
    exit 1
fi

echo "== step 3: classify the change"
if [ "$TARGET_PG_MAJOR" != "$CURRENT_PG_MAJOR" ]; then
    echo "   MAJOR upgrade: $CURRENT_PG_MAJOR -> $TARGET_PG_MAJOR"
    echo "   The named data volume is NOT compatible across majors. A mismatch refuses to start and"
    echo "   looks like a broken image. Reset the volume deliberately as part of the upgrade, and"
    echo "   update AgeSession.PostgresMajor, nfrs/NFR-04-version-pairing.md and both Aspire hosts"
    echo "   together."
    echo "   This script does not round-trip a major upgrade — do that in a scratch environment first."
    echo "== NFR-04 pre-upgrade check: PROCEED WITH THE MAJOR STEPS ABOVE"
    exit 0
fi

echo "   MINOR upgrade within major $CURRENT_PG_MAJOR — round-tripping a backup into the target image"

DUMP_DIR="$(mktemp -d)"
PROBE="age-preupgrade-probe-$$"
cleanup() {
    docker rm -f "$PROBE" >/dev/null 2>&1 || true
    rm -rf "$DUMP_DIR"
}
trap cleanup EXIT

SRC_EDGES="$(psql_current -d "$SOURCE_DB" -tAc 'SELECT count(*) FROM memory_graph."LINKS";')"
echo "   source edges: $SRC_EDGES"
if [ "$SRC_EDGES" -eq 0 ]; then
    echo "FAIL: source has zero edges — the round trip would prove nothing" >&2
    exit 1
fi

echo "== step 4: dump from the current minor"
docker exec -e PGPASSWORD="$PGPASSWORD_VALUE" "$CONTAINER" \
    pg_dump -U "$PGUSER_VALUE" -d "$SOURCE_DB" -Fc -f "/tmp/preupgrade.dump"
docker cp "$CONTAINER:/tmp/preupgrade.dump" "$DUMP_DIR/preupgrade.dump" >/dev/null
docker exec "$CONTAINER" rm -f /tmp/preupgrade.dump >/dev/null 2>&1 || true

echo "== step 5: restore into $TARGET_IMAGE"
docker run -d --name "$PROBE" \
    -e POSTGRES_PASSWORD="$PGPASSWORD_VALUE" \
    -e POSTGRES_USER="$PGUSER_VALUE" \
    "$TARGET_IMAGE" >/dev/null

for _ in $(seq 1 60); do
    if docker exec -e PGPASSWORD="$PGPASSWORD_VALUE" "$PROBE" \
        pg_isready -U "$PGUSER_VALUE" >/dev/null 2>&1; then
        break
    fi
    sleep 1
done

docker exec -e PGPASSWORD="$PGPASSWORD_VALUE" "$PROBE" \
    psql -U "$PGUSER_VALUE" -v ON_ERROR_STOP=1 -d postgres -tAc "CREATE DATABASE upgraded;" >/dev/null
docker cp "$DUMP_DIR/preupgrade.dump" "$PROBE:/tmp/preupgrade.dump" >/dev/null
docker exec -e PGPASSWORD="$PGPASSWORD_VALUE" "$PROBE" \
    pg_restore -U "$PGUSER_VALUE" -d upgraded --no-owner --no-privileges /tmp/preupgrade.dump

TARGET_PG_FULL="$(docker exec -e PGPASSWORD="$PGPASSWORD_VALUE" "$PROBE" \
    psql -U "$PGUSER_VALUE" -tAc "SHOW server_version;")"
DST_EDGES="$(docker exec -e PGPASSWORD="$PGPASSWORD_VALUE" "$PROBE" \
    psql -U "$PGUSER_VALUE" -d upgraded -tAc 'SELECT count(*) FROM memory_graph."LINKS";')"
DST_AGE="$(docker exec -e PGPASSWORD="$PGPASSWORD_VALUE" "$PROBE" \
    psql -U "$PGUSER_VALUE" -d upgraded -tAc "SELECT extversion FROM pg_extension WHERE extname = 'age';")"
GRAPH_PRESENT="$(docker exec -e PGPASSWORD="$PGPASSWORD_VALUE" "$PROBE" \
    psql -U "$PGUSER_VALUE" -d upgraded -tAc "SELECT count(*) FROM ag_catalog.ag_graph WHERE name = 'memory_graph';")"

echo "== step 6: traverse on the upgraded minor, without recreating anything"
TRAVERSAL="$(docker exec -e PGPASSWORD="$PGPASSWORD_VALUE" "$PROBE" \
    psql -U "$PGUSER_VALUE" -d upgraded -tAc "
        LOAD 'age';
        SET search_path = ag_catalog, \"\$user\", public;
        SELECT count(*) FROM ag_catalog.cypher('memory_graph', \$\$
            MATCH p = (s:Memory)-[:LINKS*1..2]->(t:Memory) RETURN nodes(p)
        \$\$) AS (n agtype);" | tail -1)"

echo "   target postgres: $TARGET_PG_FULL"
echo "   target age:      ${DST_AGE:-<missing>}"
echo "   graph present:   $GRAPH_PRESENT"
echo "   edges:           $DST_EDGES (source $SRC_EDGES)"
echo "   traversal paths: $TRAVERSAL"

FAILED=0
[ "$GRAPH_PRESENT" = "1" ] || { echo "FAIL: memory_graph absent after restore" >&2; FAILED=1; }
[ -n "$DST_AGE" ] || { echo "FAIL: age extension absent after restore" >&2; FAILED=1; }
[ "$DST_EDGES" = "$SRC_EDGES" ] || { echo "FAIL: edge count changed ($SRC_EDGES -> $DST_EDGES)" >&2; FAILED=1; }
[ "$DST_EDGES" -gt 0 ] || { echo "FAIL: zero edges after restore" >&2; FAILED=1; }
[ "$TRAVERSAL" -gt 0 ] || { echo "FAIL: traversal returned nothing after restore" >&2; FAILED=1; }

if [ "$FAILED" -ne 0 ]; then
    echo "== NFR-04 pre-upgrade check FAILED — block the upgrade"
    exit 1
fi

echo "== NFR-04 pre-upgrade check PASSED — minor upgrade round-trips with graph objects intact"
