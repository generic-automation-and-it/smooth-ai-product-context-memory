#!/usr/bin/env bash
set -euo pipefail

controller_image="${1:?usage: smoke-apphost-container.sh CONTROLLER_IMAGE}"
engine_cli="${ENGINE_CLI:-docker}"
engine_socket="${ENGINE_SOCKET:-/var/run/docker.sock}"
engine_config_directory="${ENGINE_CONFIG_DIRECTORY:-${HOME}/.docker}"
engine_kind="${ENGINE_KIND:-docker}"
engine_host_address="${ENGINE_HOST_ADDRESS:-}"
installation_id="${INSTALLATION_ID:-smoke-${GITHUB_RUN_ID:-$$}}"
dashboard_port="${DASHBOARD_PORT:-35278}"
otlp_port="${OTLP_PORT:-39075}"
api_port="${API_PORT:-35141}"
postgres_port="${POSTGRES_PORT:-35432}"
blob_port="${BLOB_PORT:-39000}"
blob_console_port="${BLOB_CONSOLE_PORT:-39001}"
seq_port="${SEQ_PORT:-35341}"
controller_name="mimisbrunnr-${installation_id}-orchestrator"
state_directory="$(mktemp -d "${TMPDIR:-/tmp}/mimisbrunnr-controller-smoke.XXXXXX")"
postgres_password="$(openssl rand -hex 24)"
blob_access_key="$(openssl rand -hex 12)"
blob_secret_key="$(openssl rand -hex 24)"

if [ -z "$engine_host_address" ]; then
  if [ "$engine_kind" = "podman" ]; then
    engine_host_address="host.containers.internal"
  else
    engine_host_address="host.docker.internal"
  fi
fi

engine() {
  "$engine_cli" "$@"
}

controller_args=(
    run -d
    --name "$controller_name" \
    --label "io.smooth-mimisbrunnr.smoke=$installation_id" \
    -v "$engine_socket:/var/run/docker.sock" \
    -v "$state_directory:/var/lib/mimisbrunnr" \
    -p "$dashboard_port:15278" \
    -p "$otlp_port:19075" \
    -e "InstallationConfiguration__Id=$installation_id" \
    -e "EngineConfiguration__Kind=$engine_kind" \
    -e "EngineConfiguration__HostAddress=$engine_host_address" \
    -e "HostConfiguration__Port=$api_port" \
    -e "PostgresConfiguration__Password=$postgres_password" \
    -e "PostgresConfiguration__Port=$postgres_port" \
    -e "BlobConfiguration__AccessKey=$blob_access_key" \
    -e "BlobConfiguration__SecretKey=$blob_secret_key" \
    -e "BlobConfiguration__Port=$blob_port" \
    -e "BlobConfiguration__ConsolePort=$blob_console_port" \
    -e "SeqConfiguration__Port=$seq_port"
)

if [ -d "$engine_config_directory" ]; then
  controller_args+=(-v "$engine_config_directory:/root/.docker:ro")
fi

controller_args+=("$controller_image")

start_controller() {
  engine "${controller_args[@]}" >/dev/null
}

remove_controller() {
  engine rm --force "$controller_name" >/dev/null 2>&1 || true
}

reset_installation() {
  engine run --rm \
    -v "$engine_socket:/var/run/docker.sock" \
    -e "InstallationConfiguration__Id=$installation_id" \
    "$controller_image" reset >/dev/null 2>&1 || true
}

cleanup() {
  remove_controller
  reset_installation
  case "$state_directory" in
    "${TMPDIR:-/tmp}"/mimisbrunnr-controller-smoke.*) rm -rf "$state_directory" ;;
  esac
}
trap cleanup EXIT

wait_for_health() {
  for _ in $(seq 1 120); do
    if curl --fail --silent "http://127.0.0.1:$api_port/health" >/dev/null 2>&1; then
      return
    fi

    if ! engine inspect "$controller_name" >/dev/null 2>&1; then
      echo "controller exited before the API became healthy" >&2
      return 1
    fi

    sleep 1
  done

  engine logs "$controller_name" >&2 || true
  echo "API did not become healthy within 120 seconds" >&2
  return 1
}

wait_for_reconciled_health() {
  previous_host_id="$1"
  host_name="mimisbrunnr-${installation_id}-host"

  for _ in $(seq 1 120); do
    current_host_id="$(engine inspect --format '{{.Id}}' "$host_name" 2>/dev/null || true)"
    if [ -n "$current_host_id" ] &&
      [ "$current_host_id" != "$previous_host_id" ] &&
      curl --fail --silent "http://127.0.0.1:$api_port/health" >/dev/null 2>&1; then
      return
    fi

    sleep 1
  done

  engine logs "$controller_name" >&2 || true
  echo "reconciled API did not become healthy within 120 seconds" >&2
  return 1
}

assert_owned_resources() {
  for suffix in postgres blob-well seq host; do
    name="mimisbrunnr-${installation_id}-${suffix}"
    owner="$(engine inspect --format '{{ index .Config.Labels "io.smooth-mimisbrunnr.installation" }}' "$name")"
    managed="$(engine inspect --format '{{ index .Config.Labels "io.smooth-mimisbrunnr.managed" }}' "$name")"
    [ "$owner" = "$installation_id" ]
    [ "$managed" = "true" ]
  done
}

assert_volumes_exist() {
  for suffix in postgres-data blob-well-data seq-data; do
    engine volume inspect "mimisbrunnr-${installation_id}-${suffix}" >/dev/null
  done
}

assert_volumes_absent() {
  for suffix in postgres-data blob-well-data seq-data; do
    if engine volume inspect "mimisbrunnr-${installation_id}-${suffix}" >/dev/null 2>&1; then
      echo "volume mimisbrunnr-${installation_id}-${suffix} survived reset" >&2
      return 1
    fi
  done
}

capture_fixture() {
  group_uuid="$(curl --fail --silent \
    -H 'Content-Type: application/json' \
    -d '{"scopeDimension":"product","tickets":[]}' \
    "http://127.0.0.1:$api_port/api/context/groups/resolve" | jq -r '.uuid')"

  first_uuid="$(curl --fail --silent \
    -H 'Content-Type: application/json' \
    -d '{"groupUuid":"'"$group_uuid"'","items":[{"name":"Controller smoke source","description":"Controller smoke source","statement":"Containerized AppHost starts the stack","contentSummary":"Synthetic smoke content","kind":"decision","facets":["architecture"],"tags":["controller-smoke"],"status":"approved","confidence":90,"content":"controller-smoke-blob","sources":[],"validFrom":"2026-09-15T00:00:00Z"}]}' \
    "http://127.0.0.1:$api_port/api/context/memories" | jq -r '.items[0].uuid')"

  second_uuid="$(curl --fail --silent \
    -H 'Content-Type: application/json' \
    -d '{"groupUuid":"'"$group_uuid"'","items":[{"name":"Controller smoke target","description":"Controller smoke target","statement":"Graph data crosses the controller boundary","contentSummary":"Synthetic smoke content","kind":"decision","facets":["architecture"],"tags":["controller-smoke"],"status":"approved","confidence":90,"sources":[],"validFrom":"2026-09-15T00:00:00Z"}]}' \
    "http://127.0.0.1:$api_port/api/context/memories" | jq -r '.items[0].uuid')"

  curl --fail --silent \
    -H 'Content-Type: application/json' \
    -d '{"sourceUuid":"'"$first_uuid"'","targetUuid":"'"$second_uuid"'","relation":"depends-on","reason":"controller smoke graph"}' \
    "http://127.0.0.1:$api_port/api/context/links" >/dev/null
}

assert_fixture() {
  blob="$(curl --fail --silent "http://127.0.0.1:$api_port/api/context/memories/$first_uuid/versions/1/blob")"
  [ "$blob" = "controller-smoke-blob" ]

  path_count="$(curl --fail --silent \
    -H 'Content-Type: application/json' \
    -d '{"sourceUuid":"'"$first_uuid"'","targetUuid":"'"$second_uuid"'","maxDepth":2}' \
    "http://127.0.0.1:$api_port/api/context/paths" | jq '.paths | length')"
  [ "$path_count" = "1" ]
}

start_controller
echo "Controller started; waiting for API health."
wait_for_health
assert_owned_resources
assert_volumes_exist
curl --fail --silent "http://127.0.0.1:$dashboard_port/" >/dev/null
capture_fixture
assert_fixture
echo "Initial API, blob, graph, and ownership checks passed."

engine stop --timeout 30 "$controller_name" >/dev/null
for suffix in postgres blob-well seq host; do
  if engine inspect "mimisbrunnr-${installation_id}-${suffix}" >/dev/null 2>&1; then
    echo "owned workload survived graceful controller stop" >&2
    exit 1
  fi
done
assert_volumes_exist
engine rm "$controller_name" >/dev/null
echo "Graceful stop removed workloads and retained data volumes."

start_controller
wait_for_health
assert_fixture
echo "Graceful restart retained relational, graph, and blob data."

previous_host_id="$(engine inspect --format '{{.Id}}' "mimisbrunnr-${installation_id}-host")"
engine kill "$controller_name" >/dev/null
engine rm "$controller_name" >/dev/null
start_controller
wait_for_reconciled_health "$previous_host_id"
assert_owned_resources
assert_fixture
echo "Forced-stop recovery reconciled workloads and retained data."

engine stop --timeout 30 "$controller_name" >/dev/null
engine rm "$controller_name" >/dev/null
reset_installation
assert_volumes_absent
echo "Explicit reset removed installation-scoped data volumes."

foreign_name="mimisbrunnr-${installation_id}-postgres"
engine create --name "$foreign_name" docker.io/library/alpine:3.22 >/dev/null
if engine run --rm \
  -v "$engine_socket:/var/run/docker.sock" \
  -e "InstallationConfiguration__Id=$installation_id" \
  -e "PostgresConfiguration__Password=$postgres_password" \
  -e "BlobConfiguration__AccessKey=$blob_access_key" \
  -e "BlobConfiguration__SecretKey=$blob_secret_key" \
  "$controller_image" >/tmp/mimisbrunnr-controller-collision.log 2>&1; then
  echo "controller adopted a foreign container name" >&2
  exit 1
fi
grep -q "is not owned by installation" /tmp/mimisbrunnr-controller-collision.log
engine rm "$foreign_name" >/dev/null
echo "Foreign container collision was rejected."

echo "AppHost controller smoke test passed for installation '$installation_id'."
