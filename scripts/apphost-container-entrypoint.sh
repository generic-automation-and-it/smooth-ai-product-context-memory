#!/bin/sh
set -eu

ownership_label="io.smooth-mimisbrunnr.installation"
managed_label="io.smooth-mimisbrunnr.managed"
installation_id="${InstallationConfiguration__Id:-default}"
command_name="${1:-run}"

fail() {
  echo "mimisbrunnr-controller: $*" >&2
  exit 1
}

require_value() {
  variable_name="$1"
  value="$(printenv "$variable_name" 2>/dev/null || true)"
  [ -n "$value" ] || fail "required configuration '$variable_name' is missing"
}

validate_installation_id() {
  case "$installation_id" in
    ""|*[!a-z0-9-]*|-*)
      fail "InstallationConfiguration__Id must start with a lowercase letter or digit and contain only lowercase letters, digits, and hyphens"
      ;;
  esac

  [ "${#installation_id}" -le 32 ] || fail "InstallationConfiguration__Id must be at most 32 characters"
}

container_names() {
  printf '%s\n' \
    "mimisbrunnr-${installation_id}-postgres" \
    "mimisbrunnr-${installation_id}-blob-well" \
    "mimisbrunnr-${installation_id}-seq" \
    "mimisbrunnr-${installation_id}-host"
}

volume_names() {
  printf '%s\n' \
    "mimisbrunnr-${installation_id}-postgres-data" \
    "mimisbrunnr-${installation_id}-blob-well-data" \
    "mimisbrunnr-${installation_id}-seq-data"
}

container_label() {
  docker inspect --format "{{ index .Config.Labels \"$1\" }}" "$2" 2>/dev/null || true
}

volume_label() {
  docker volume inspect --format "{{ index .Labels \"$1\" }}" "$2" 2>/dev/null || true
}

verify_container_ownership() {
  container_name="$1"
  docker container inspect "$container_name" >/dev/null 2>&1 || return 1

  owner="$(container_label "$ownership_label" "$container_name")"
  managed="$(container_label "$managed_label" "$container_name")"
  [ "$owner" = "$installation_id" ] && [ "$managed" = "true" ] ||
    fail "container '$container_name' exists but is not owned by installation '$installation_id'"
}

ensure_volume() {
  volume_name="$1"
  if docker volume inspect "$volume_name" >/dev/null 2>&1; then
    owner="$(volume_label "$ownership_label" "$volume_name")"
    managed="$(volume_label "$managed_label" "$volume_name")"
    [ "$owner" = "$installation_id" ] && [ "$managed" = "true" ] ||
      fail "volume '$volume_name' exists but is not owned by installation '$installation_id'"
    return
  fi

  docker volume create \
    --label "$ownership_label=$installation_id" \
    --label "$managed_label=true" \
    "$volume_name" >/dev/null
}

check_engine() {
  docker version >/dev/null 2>&1 || fail "container engine is unreachable through DOCKER_HOST='${DOCKER_HOST:-unset}'"
}

configure_engine() {
  engine_kind="${EngineConfiguration__Kind:-docker}"
  case "$engine_kind" in
    docker)
      engine_host_address="${EngineConfiguration__HostAddress:-host.docker.internal}"
      ;;
    podman)
      engine_host_address="${EngineConfiguration__HostAddress:-host.containers.internal}"
      ;;
    *)
      fail "EngineConfiguration__Kind must be 'docker' or 'podman'"
      ;;
  esac

  case "$engine_host_address" in
    ""|*://*) fail "EngineConfiguration__HostAddress must be a host name or IP address without a URI scheme" ;;
  esac

  export EngineConfiguration__Kind="$engine_kind"
  export EngineConfiguration__HostAddress="$engine_host_address"
  export AppHost__ContainerHostname="$engine_host_address"
}

stop_owned_containers() {
  container_names | while IFS= read -r container_name; do
    if verify_container_ownership "$container_name"; then
      docker rm --force "$container_name" >/dev/null
      echo "Removed owned container $container_name"
    fi
  done
}

reset_owned_volumes() {
  volume_names | while IFS= read -r volume_name; do
    docker volume inspect "$volume_name" >/dev/null 2>&1 || continue
    owner="$(volume_label "$ownership_label" "$volume_name")"
    managed="$(volume_label "$managed_label" "$volume_name")"
    [ "$owner" = "$installation_id" ] && [ "$managed" = "true" ] ||
      fail "volume '$volume_name' exists but is not owned by installation '$installation_id'"
    docker volume rm "$volume_name" >/dev/null
    echo "Removed owned volume $volume_name"
  done
}

run_controller() {
  require_value PostgresConfiguration__Password
  require_value BlobConfiguration__AccessKey
  require_value BlobConfiguration__SecretKey
  require_value HostConfiguration__Image

  case "$HostConfiguration__Image" in
    *@sha256:????????????????????????????????????????????????????????????????) ;;
    *) fail "HostConfiguration__Image must be pinned by sha256 digest" ;;
  esac

  configure_engine
  check_engine

  container_names | while IFS= read -r container_name; do
    verify_container_ownership "$container_name" || true
  done
  stop_owned_containers
  volume_names | while IFS= read -r volume_name; do
    ensure_volume "$volume_name"
  done

  export AppHostConfiguration__Mode=Release
  export HostConfiguration__UseProject=false
  export InstallationConfiguration__Id="$installation_id"
  export DCP_WORKLOAD_ID="mimisbrunnr-${installation_id}"
  export ASPIRE_DASHBOARD_FRONTEND_AUTH_MODE=BrowserToken
  export DCP_INSTANCE_ID_PREFIX="mimisbrunnr-${installation_id}"

  echo "Starting Mímisbrunnr controller version ${ReleaseConfiguration__Version:-development}; installation=$installation_id; engine=$engine_kind; api=$HostConfiguration__Image"

  shutdown_requested=false
  apphost_pid=""
  handle_shutdown() {
    shutdown_requested=true
    [ -z "$apphost_pid" ] || kill -TERM "$apphost_pid" 2>/dev/null || true
  }
  trap handle_shutdown INT TERM

  /app/SmoothAiProductContextMemory.AppHost &
  apphost_pid=$!
  set +e
  wait "$apphost_pid"
  apphost_status=$?
  set -e

  if [ "$shutdown_requested" = true ]; then
    stop_owned_containers
  fi

  exit "$apphost_status"
}

validate_installation_id

case "$command_name" in
  run)
    run_controller
    ;;
  stop)
    check_engine
    stop_owned_containers
    ;;
  reset)
    check_engine
    stop_owned_containers
    reset_owned_volumes
    ;;
  version)
    echo "${ReleaseConfiguration__Version:-development}"
    ;;
  *)
    fail "unknown command '$command_name'; expected run, stop, reset, or version"
    ;;
esac
