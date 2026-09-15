#!/usr/bin/env bash
# Read-only preflight for AddTicketGraph. Output contains sensitive ticket identities.
# Usage: scripts/check-ticket-ownership.sh [container] [database]
# Exit: 0 clean, 1 cross-group duplicates, 2 malformed data or execution failure.
set -uo pipefail

if [ "$#" -gt 2 ]; then
    printf 'Usage: %s [container] [database]\n' "$0" >&2
    exit 2
fi
CONTAINER="${1-mimisbrunnr-postgres}"
DATABASE="${2-app}"
case "$CONTAINER" in
    ''|-*|*[!a-zA-Z0-9_.-]*)
        printf 'ERROR: invalid container name or ID.\n' >&2
        exit 2 ;;
esac
if [ -z "$DATABASE" ]; then
    printf 'ERROR: database name must not be empty.\n' >&2
    exit 2
fi
SCRIPT_DIR="$(dirname -- "${BASH_SOURCE[0]}")"

# Credentials never leave the container or become command-line arguments. PGDATABASE
# avoids psql -d interpreting a database name containing '=' as a connection string.
if ! RESULT="$(docker exec -i "$CONTAINER" sh -c '
    set +x
    export PGPASSWORD="${POSTGRES_PASSWORD-}"
    export PGUSER="${POSTGRES_USER:-postgres}"
    export PGDATABASE="$1" PGHOST=127.0.0.1 PGPORT=5432 PGCONNECT_TIMEOUT=10
    export PGOPTIONS="-c default_transaction_read_only=on -c statement_timeout=60000 -c lock_timeout=5000"
    exec psql -X -w -qAt -v ON_ERROR_STOP=1 -f -
' ticket-ownership "$DATABASE" < "$SCRIPT_DIR/check-ticket-ownership.sql")"; then
    printf 'ERROR: ticket ownership check failed; block migration.\n' >&2
    exit 2
fi

# A missing completion marker is an error, never a clean result.
case "$RESULT" in
    *$'\n'CHECK_EXIT=0) STATUS=0 ;;
    *$'\n'CHECK_EXIT=1) STATUS=1 ;;
    *$'\n'CHECK_EXIT=2) STATUS=2 ;;
    *) printf 'ERROR: incomplete ownership report; block migration.\n' >&2; exit 2 ;;
esac
printf '%s\n' "${RESULT%$'\n'CHECK_EXIT=*}"
exit "$STATUS"
