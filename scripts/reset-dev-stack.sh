#!/usr/bin/env bash
# Data-destroying reset of the local AppHost stack.
#
# THIS DELETES captured memories, blobs, and Seq logs. There is no prompt — choosing this
# command instead of scripts/stop-dev-stack.sh IS the explicit ask.
#
# Removes the four AppHost containers (via stop-dev-stack.sh) then the three named volumes.
# Does not touch mimisbrunnr-testcontainer-* or historical orphan volumes from renames.
#
# Names must match DistributedApplicationBuilderExtensions.cs. Do not glob mimisbrunnr-*.
#
# Usage: scripts/reset-dev-stack.sh
# Runtime: $DOTNET_ASPIRE_CONTAINER_RUNTIME (default docker). Same env Aspire uses.
#
# Keep data, only stop containers: scripts/stop-dev-stack.sh

set -euo pipefail

RUNTIME="${DOTNET_ASPIRE_CONTAINER_RUNTIME:-docker}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

VOLUMES=(
    mimisbrunnr-postgres-data
    mimisbrunnr-blob-well-data
    mimisbrunnr-seq-data
)

if ! command -v "$RUNTIME" >/dev/null 2>&1; then
    echo "FAIL: container runtime '$RUNTIME' not found" >&2
    exit 1
fi

echo "WARNING: destroying named volumes (memories, blobs, Seq logs)"

"$SCRIPT_DIR/stop-dev-stack.sh"

if ! "$RUNTIME" info >/dev/null 2>&1; then
    echo "FAIL: container runtime '$RUNTIME' not reachable (daemon stopped?)" >&2
    exit 1
fi

remove_volume() {
    local name="$1"

    if ! "$RUNTIME" volume inspect "$name" >/dev/null 2>&1; then
        echo "skip volume $name (not present)"
        return 0
    fi

    "$RUNTIME" volume rm "$name" >/dev/null
    echo "removed volume $name"
}

for volume in "${VOLUMES[@]}"; do
    remove_volume "$volume"
done

echo "OK: dev containers and named volumes removed"
echo "next AppHost start is an empty corpus"
