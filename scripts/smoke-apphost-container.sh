#!/usr/bin/env bash
set -euo pipefail

controller_image="${1:?usage: smoke-apphost-container.sh CONTROLLER_IMAGE}"
engine_cli="${ENGINE_CLI:-docker}"
engine_socket="${ENGINE_SOCKET:-/var/run/docker.sock}"
engine_config_directory="${ENGINE_CONFIG_DIRECTORY:-}"
engine_kind="${ENGINE_KIND:-docker}"
engine_host_address="${ENGINE_HOST_ADDRESS:-}"
engine_bind_address="${ENGINE_BIND_ADDRESS:-}"
installation_id="${INSTALLATION_ID:-smoke-${GITHUB_RUN_ID:-$$}}"
dashboard_port="${DASHBOARD_PORT:-35278}"
otlp_port="${OTLP_PORT:-39075}"
api_port="${API_PORT:-35141}"
postgres_port="${POSTGRES_PORT:-35432}"
blob_port="${BLOB_PORT:-39000}"
blob_console_port="${BLOB_CONSOLE_PORT:-39001}"
seq_port="${SEQ_PORT:-35341}"
controller_name="mimisbrunnr-${installation_id}-controller"
state_volume="mimisbrunnr-${installation_id}-smoke-state"
controller_id=""
foreign_id=""
installation_started=false
state_created=false
state_directory="$(mktemp -d "${TMPDIR:-/tmp}/mimisbrunnr-controller-smoke.XXXXXX")"
postgres_password="$(openssl rand -hex 24)"
blob_access_key="$(openssl rand -hex 12)"
blob_secret_key="$(openssl rand -hex 24)"
api_read_token="$(openssl rand -hex 24)"
api_write_token="$(openssl rand -hex 24)"

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

existing_containers="$(engine container ls --all --format '{{.Names}}')"
existing_volumes="$(engine volume ls --format '{{.Name}}')"
for suffix in controller host postgres blob-well seq; do
  if printf '%s\n' "$existing_containers" | grep -Fxq "mimisbrunnr-${installation_id}-${suffix}"; then
    echo "Smoke refuses pre-existing installation containers; choose a unique INSTALLATION_ID." >&2
    exit 1
  fi
done
for suffix in postgres-data blob-well-data seq-data smoke-state; do
  if printf '%s\n' "$existing_volumes" | grep -Fxq "mimisbrunnr-${installation_id}-${suffix}"; then
    echo "Smoke refuses pre-existing installation volumes; choose a unique INSTALLATION_ID." >&2
    exit 1
  fi
done

if [ -z "$engine_bind_address" ]; then
  if [ "$(engine info --format '{{.OperatingSystem}}')" = "Docker Desktop" ]; then
    engine_bind_address=127.0.0.1
  else
    engine_bind_address="$(engine network inspect bridge --format '{{(index .IPAM.Config 0).Gateway}}')"
  fi
fi

curl() {
  if [ "$engine_bind_address" = 127.0.0.1 ]; then
    command curl --max-time 10 "$@"
  else
    engine run --rm docker.io/curlimages/curl:8.12.1 --max-time 10 "$@"
  fi
}

controller_args=(
    create
    --name "$controller_name" \
    --label "io.smooth-mimisbrunnr.smoke=$installation_id" \
    -v "$engine_socket:/var/run/docker.sock" \
    -v "$state_volume:/var/lib/mimisbrunnr" \
    --add-host "host.docker.internal:host-gateway" \
    -p "$engine_bind_address:$dashboard_port:15278" \
    -p "$engine_bind_address:$otlp_port:$otlp_port" \
    -e "ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL=http://0.0.0.0:$otlp_port" \
    -e "InstallationConfiguration__Id=$installation_id" \
    -e "EngineConfiguration__Kind=$engine_kind" \
    -e "EngineConfiguration__HostAddress=$engine_host_address" \
    -e "EngineConfiguration__BindAddress=$engine_bind_address" \
    -e "HostConfiguration__Port=$api_port" \
    -e "PostgresConfiguration__Password=$postgres_password" \
    -e "PostgresConfiguration__Port=$postgres_port" \
    -e "BlobConfiguration__AccessKey=$blob_access_key" \
    -e "BlobConfiguration__SecretKey=$blob_secret_key" \
    -e "BlobConfiguration__Port=$blob_port" \
    -e "BlobConfiguration__ConsolePort=$blob_console_port" \
    -e "SeqConfiguration__Port=$seq_port" \
    -e "Parameters__api-read-token=$api_read_token" \
    -e "Parameters__api-write-token=$api_write_token"
)

if [ -d "$engine_config_directory" ]; then
  controller_args+=(-v "$engine_config_directory:/root/.docker:ro")
fi

controller_args+=("$controller_image")

start_controller() {
  if [ "$state_created" = false ]; then
    engine volume create --label "io.smooth-mimisbrunnr.smoke=$installation_id" "$state_volume" >/dev/null
    state_created=true
  fi
  controller_id="$(engine "${controller_args[@]}")"
  engine start "$controller_id" >/dev/null
  installation_started=true
}

remove_controller() {
  if [ -n "$controller_id" ]; then
    engine rm --force "$controller_id" >/dev/null 2>&1 || true
    controller_id=""
  fi
}

reset_installation() {
  engine run --rm \
    --name "$controller_name" \
    -v "$engine_socket:/var/run/docker.sock" \
    -e "InstallationConfiguration__Id=$installation_id" \
    "$controller_image" reset >/dev/null 2>&1 || true
}

cleanup() {
  if [ "${KEEP_SMOKE_RESOURCES:-false}" = true ]; then
    echo "Retained scratch installation '$installation_id' and state at '$state_directory' for local diagnosis." >&2
    return
  fi
  remove_controller
  if [ -n "$foreign_id" ]; then
    engine rm "$foreign_id" >/dev/null 2>&1 || true
  fi
  if [ "$installation_started" = true ]; then
    reset_installation
  fi
  if [ "$state_created" = true ]; then
    engine volume rm "$state_volume" >/dev/null 2>&1 || true
  fi
  case "$state_directory" in
    "${TMPDIR:-/tmp}"/mimisbrunnr-controller-smoke.*) rm -rf "$state_directory" ;;
  esac
}
trap cleanup EXIT

wait_for_health() {
  for _ in $(seq 1 120); do
    if curl --fail --silent "http://$engine_bind_address:$api_port/health" >/dev/null 2>&1; then
      return
    fi

    if [ "$(engine inspect --format '{{.State.Running}}' "$controller_name" 2>/dev/null || true)" != true ]; then
      echo "controller exited before the API became healthy" >&2
      return 1
    fi

    sleep 1
  done

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
      curl --fail --silent "http://$engine_bind_address:$api_port/health" >/dev/null 2>&1; then
      return
    fi

    sleep 1
  done

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
    -H "Authorization: Bearer $api_write_token" \
    -H 'Content-Type: application/json' \
    -d '{"scopeDimension":"product","tickets":[]}' \
    "http://$engine_bind_address:$api_port/api/context/groups/resolve" | jq -r '.uuid')"

  first_uuid="$(curl --fail --silent \
    -H "Authorization: Bearer $api_write_token" \
    -H 'Content-Type: application/json' \
    -d '{"groupUuid":"'"$group_uuid"'","items":[{"name":"Controller smoke source","description":"Controller smoke source","statement":"Containerized AppHost starts the stack","contentSummary":"Synthetic smoke content","kind":"decision","facets":["architecture"],"tags":["controller-smoke"],"status":"approved","confidence":90,"content":"controller-smoke-blob","sources":[],"validFrom":"2026-09-15T00:00:00Z"}]}' \
    "http://$engine_bind_address:$api_port/api/context/memories" | jq -r '.items[0].uuid')"

  second_uuid="$(curl --fail --silent \
    -H "Authorization: Bearer $api_write_token" \
    -H 'Content-Type: application/json' \
    -d '{"groupUuid":"'"$group_uuid"'","items":[{"name":"Controller smoke target","description":"Controller smoke target","statement":"Graph data crosses the controller boundary","contentSummary":"Synthetic smoke content","kind":"decision","facets":["architecture"],"tags":["controller-smoke"],"status":"approved","confidence":90,"sources":[],"validFrom":"2026-09-15T00:00:00Z"}]}' \
    "http://$engine_bind_address:$api_port/api/context/memories" | jq -r '.items[0].uuid')"

  curl --fail --silent \
    -H "Authorization: Bearer $api_write_token" \
    -H 'Content-Type: application/json' \
    -d '{"sourceUuid":"'"$first_uuid"'","targetUuid":"'"$second_uuid"'","relation":"depends-on","reason":"controller smoke graph"}' \
    "http://$engine_bind_address:$api_port/api/context/links" >/dev/null
}

assert_fixture() {
  blob="$(curl --fail --silent -H "Authorization: Bearer $api_read_token" \
    "http://$engine_bind_address:$api_port/api/context/memories/$first_uuid/versions/1/blob")"
  [ "$blob" = "controller-smoke-blob" ]

  path_count="$(curl --fail --silent \
    -H "Authorization: Bearer $api_read_token" \
    -H 'Content-Type: application/json' \
    -d '{"sourceUuid":"'"$first_uuid"'","targetUuid":"'"$second_uuid"'","maxDepth":2}' \
    "http://$engine_bind_address:$api_port/api/context/paths" | jq '.paths | length')"
  [ "$path_count" = "1" ]
}

start_controller
echo "Controller started; waiting for API health."
wait_for_health
assert_owned_resources
assert_volumes_exist
curl --fail --silent "http://$engine_bind_address:$dashboard_port/" >/dev/null
capture_fixture
assert_fixture
echo "Initial API, blob, graph, and ownership checks passed."

engine stop --timeout 180 "$controller_name" >/dev/null
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

engine stop --timeout 180 "$controller_name" >/dev/null
engine rm "$controller_name" >/dev/null
reset_installation
assert_volumes_absent
echo "Explicit reset removed installation-scoped data volumes."

foreign_name="mimisbrunnr-${installation_id}-postgres"
foreign_id="$(engine create --name "$foreign_name" docker.io/library/alpine:3.22)"
if engine run --rm \
  --name "$controller_name" \
  -v "$engine_socket:/var/run/docker.sock" \
  -e "InstallationConfiguration__Id=$installation_id" \
  -e "PostgresConfiguration__Password=$postgres_password" \
  -e "BlobConfiguration__AccessKey=$blob_access_key" \
  -e "BlobConfiguration__SecretKey=$blob_secret_key" \
  -e "EngineConfiguration__BindAddress=$engine_bind_address" \
  "$controller_image" >"$state_directory/collision.log" 2>&1; then
  echo "controller adopted a foreign container name" >&2
  exit 1
fi
grep -q "is not owned by installation" "$state_directory/collision.log"
engine rm "$foreign_id" >/dev/null
foreign_id=""
echo "Foreign container collision was rejected."

echo "AppHost controller smoke test passed for installation '$installation_id'."
