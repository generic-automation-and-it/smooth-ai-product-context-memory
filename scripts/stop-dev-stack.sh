#!/usr/bin/env bash
# Data-preserving stop of the local AppHost stack.
#
# AppHost resources use ContainerLifetime.Persistent so captured memories and blobs survive an
# AppHost exit. This script is the supported way to tear those containers down without deleting
# named volumes. It does not stop the AppHost process, DCP, or the test fixtures.
#
# Names must match DistributedApplicationBuilderExtensions.cs. Do not glob mimisbrunnr-* —
# that prefix also matches mimisbrunnr-testcontainer-* (TestFramework.Aspire).
#
# Usage: scripts/stop-dev-stack.sh
# Runtime: $DOTNET_ASPIRE_CONTAINER_RUNTIME (default docker). Same env Aspire uses.
#
# Destroy volumes instead: scripts/reset-dev-stack.sh

set -euo pipefail

RUNTIME="${DOTNET_ASPIRE_CONTAINER_RUNTIME:-docker}"
DEV_PROJECT_LABEL="smooth-mímisbrunnr"

# Allowlist — never a name glob. Host is included even though it is session-lifetime:
# a hard kill of AppHost can leave mimisbrunnr-host running (DCP leftover).
CONTAINERS=(
    mimisbrunnr-postgres
    mimisbrunnr-blob-well
    mimisbrunnr-seq
    mimisbrunnr-host
)

if ! command -v "$RUNTIME" >/dev/null 2>&1; then
    echo "FAIL: container runtime '$RUNTIME' not found" >&2
    exit 1
fi

remove_container() {
    local name="$1"

    if ! "$RUNTIME" inspect "$name" >/dev/null 2>&1; then
        echo "skip $name (not present)"
        return 0
    fi

    local project
    project="$("$RUNTIME" inspect --format '{{index .Config.Labels "com.docker.compose.project"}}' "$name")"
    if [ "$project" != "$DEV_PROJECT_LABEL" ]; then
        echo "FAIL: $name has com.docker.compose.project='$project', expected '$DEV_PROJECT_LABEL'" >&2
        echo "refusing to touch a container that is not the AppHost dev stack" >&2
        exit 1
    fi

    "$RUNTIME" rm -f "$name" >/dev/null
    echo "removed $name"
}

for container in "${CONTAINERS[@]}"; do
    remove_container "$container"
done

echo "OK: dev containers removed; named volumes left in place"
echo "data-destroying reset: scripts/reset-dev-stack.sh"
