#!/bin/bash
# bash, not sh: the engine hands the controller `Parameters__api-read-token` / `Parameters__api-write-token`,
# and dash silently drops env names that are not shell identifiers, so the AppHost would never see them.
set -eu

ownership_label="io.smooth-mimisbrunnr.installation"
managed_label="io.smooth-mimisbrunnr.managed"
installation_id="${InstallationConfiguration__Id:-default}"
command_name="${1:-run}"
stop_timeout="${ControllerConfiguration__StopTimeoutSeconds:-30}"
data_root_mount="${ControllerConfiguration__DataRootMount:-/var/lib/mimisbrunnr-data}"
data_root_host=""
PATH="/app:$PATH"
export PATH

fail() {
  echo "mimisbrunnr-controller: $*" >&2
  exit 1
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
    "mimisbrunnr-${installation_id}-host" \
    "mimisbrunnr-${installation_id}-postgres" \
    "mimisbrunnr-${installation_id}-blob-well" \
    "mimisbrunnr-${installation_id}-seq"
}

volume_suffixes() {
  printf '%s\n' postgres-data blob-well-data seq-data host-context
}

check_engine() {
  case "${DOCKER_HOST:-unix:///var/run/docker.sock}" in
    unix://*) ;;
    tcp://*) [ "${DOCKER_TLS_VERIFY:-}" = 1 ] || fail "TCP engine access requires DOCKER_TLS_VERIFY=1 and mutual TLS credentials" ;;
    *) fail "engine transport must be a Unix socket or mutually authenticated TLS" ;;
  esac
  docker version >/dev/null 2>&1 || fail "container engine is unreachable; check DOCKER_HOST, socket permissions, and TLS configuration"
}

verify_controller_identity() {
  controller_name="mimisbrunnr-${installation_id}-controller"
  controller_hostname="$(printenv HOSTNAME || true)"
  case "$controller_hostname" in
    ""|*[!0-9a-f]*) fail "retain the engine-generated controller hostname (container ID)" ;;
  esac
  [ "${#controller_hostname}" -ge 12 ] || fail "controller hostname must be its container ID (at least 12 characters)"
  controller_identity="$(docker container inspect --format '{{.Id}} {{.State.Running}}' "$controller_name")" ||
    fail "start this command in a container named '$controller_name'; remove a stopped controller explicitly before replacement"
  case "$controller_identity" in
    "$controller_hostname"*" true") ;;
    *) fail "this process does not own canonical controller '$controller_name'; do not run a second controller or override its hostname" ;;
  esac
}

# Docker Desktop reports one host directory as /Users/... or /host_mnt/Users/... depending on which
# client asks, so both sides of every comparison drop that prefix.
host_path() {
  case "$1" in
    /host_mnt/*) printf '%s' "${1#/host_mnt}" ;;
    *) printf '%s' "$1" ;;
  esac
}

# The engine resolves a bind volume's device on the host, so the host path is read from the
# controller's own mount rather than configured a second time where the two could disagree.
resolve_data_root() {
  [ -d "$data_root_mount" ] || return 0
  data_root_source="$(docker container inspect --format "{{range .Mounts}}{{if eq .Destination \"$data_root_mount\"}}{{.Type}} {{.Source}}{{end}}{{end}}" "$controller_name")" ||
    fail "cannot inspect the controller's data-root mount"
  case "$data_root_source" in
    "bind /"*) data_root_host="$(host_path "${data_root_source#bind }")/$installation_id" ;;
    *) fail "data root '$data_root_mount' must be a bind mount of an absolute host directory" ;;
  esac
}

preflight_resources() {
  existing_containers="$(docker container ls --all --format '{{.Names}}')" || fail "cannot list containers"
  existing_volumes="$(docker volume ls --format '{{.Name}}')" || fail "cannot list volumes"
  owned_containers=""
  owned_volumes=""
  missing_volumes=""

  for container_name in $(container_names); do
    case "
$existing_containers
" in
      *"
$container_name
"*)
        identity="$(docker container inspect --format "{{.Id}} {{ index .Config.Labels \"$ownership_label\" }} {{ index .Config.Labels \"$managed_label\" }}" "$container_name")" ||
          fail "cannot inspect container '$container_name'"
        container_id="${identity%% *}"
        [ "$identity" = "$container_id $installation_id true" ] ||
          fail "container '$container_name' exists but is not owned by installation '$installation_id'"
        owned_containers="${owned_containers}${container_id} ${container_name}
"
        ;;
    esac
  done

  for suffix in $(volume_suffixes); do
    volume_name="mimisbrunnr-${installation_id}-${suffix}"
    case "
$existing_volumes
" in
      *"
$volume_name
"*)
        verify_volume_ownership "$volume_name"
        [ "$command_name" = stop ] || verify_volume_backing "$volume_name" "$suffix"
        owned_volumes="${owned_volumes}${volume_name} ${suffix}
"
        ;;
      *) missing_volumes="${missing_volumes}${volume_name} ${suffix}
" ;;
    esac
  done
}

# Releases before the controller created host-context left its creation to DCP, which labels nothing;
# that one canonical name is adopted unlabeled, while a volume labelled for anyone else still fails.
verify_volume_ownership() {
  identity="$(docker volume inspect --format "{{ index .Labels \"$ownership_label\" }} {{ index .Labels \"$managed_label\" }}" "$1")" ||
    fail "cannot inspect volume '$1'"
  case "$identity" in
    "$installation_id true") ;;
    " ") [ "$1" = "mimisbrunnr-${installation_id}-host-context" ] ||
      fail "volume '$1' exists but is not owned by installation '$installation_id'" ;;
    *) fail "volume '$1' exists but is not owned by installation '$installation_id'" ;;
  esac
}

verify_volume_backing() {
  device="$(docker volume inspect --format '{{ index .Options "device" }}' "$1")" ||
    fail "cannot inspect volume '$1'"
  if [ -n "$data_root_host" ]; then
    [ "$(host_path "$device")" = "$data_root_host/$2" ] ||
      fail "volume '$1' is not bound to '$data_root_host/$2'; snapshot it, reset the installation, then start with the data root"
  elif [ -n "$device" ] && [ "$command_name" = reset ]; then
    fail "volume '$1' is bound to host directory '$device'; mount the data root at '$data_root_mount' so reset can remove its contents"
  fi
}

create_missing_volumes() {
  printf '%s' "$missing_volumes" | while read -r volume_name suffix; do
    if [ -n "$data_root_host" ]; then
      mkdir -p "$data_root_mount/$installation_id/$suffix"
      docker volume create \
        --driver local --opt type=none --opt o=bind --opt "device=$data_root_host/$suffix" \
        --label "$ownership_label=$installation_id" \
        --label "$managed_label=true" \
        "$volume_name" >/dev/null
    else
      docker volume create \
        --label "$ownership_label=$installation_id" \
        --label "$managed_label=true" \
        "$volume_name" >/dev/null
    fi
    verify_volume_ownership "$volume_name"
  done
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
  printf '%s' "$owned_containers" | while read -r container_id container_name; do
    docker stop --time "$stop_timeout" "$container_id" >/dev/null || exit 1
    docker rm "$container_id" >/dev/null || exit 1
    echo "Removed owned container $container_name"
  done
}

# Removing a bind volume leaves its host directory untouched, so reset clears the contents itself to
# keep its promise that the installation's data is gone; the directory and the root stay.
reset_owned_volumes() {
  printf '%s' "$owned_volumes" | while read -r volume_name suffix; do
    verify_volume_ownership "$volume_name"
    docker volume rm "$volume_name" >/dev/null
    echo "Removed owned volume $volume_name"
    if [ -n "$data_root_host" ]; then
      find "$data_root_mount/$installation_id/$suffix" -mindepth 1 -delete
      echo "Removed contents of $data_root_host/$suffix"
    fi
  done
}

run_controller() {
  export AppHostConfiguration__Mode=Release
  export HostConfiguration__UseProject=false
  export DOTNET_ENVIRONMENT=Production
  export ASPNETCORE_ENVIRONMENT=Production
  export InstallationConfiguration__Id="$installation_id"
  export DCP_WORKLOAD_ID="mimisbrunnr-${installation_id}"
  export ASPIRE_DASHBOARD_FRONTEND_AUTH_MODE=BrowserToken
  export DCP_INSTANCE_ID_PREFIX="mimisbrunnr-${installation_id}"

  SmoothAiProductContextMemory.AppHost --validate-configuration || fail "release configuration validation failed; no workloads changed"
  check_engine
  verify_controller_identity
  resolve_data_root
  preflight_resources
  stop_owned_containers
  create_missing_volumes

  echo "Starting Mimisbrunnr controller version ${ReleaseConfiguration__Version:-development}; installation=$installation_id; engine=$engine_kind"

  shutdown_requested=false
  apphost_pid=""
  handle_shutdown() {
    [ "$shutdown_requested" = false ] || return 0
    shutdown_requested=true
    [ -z "$apphost_pid" ] || kill -TERM "$apphost_pid" 2>/dev/null || true
  }
  trap handle_shutdown INT TERM

  SmoothAiProductContextMemory.AppHost &
  apphost_pid=$!
  if [ "$shutdown_requested" = true ]; then
    kill -TERM "$apphost_pid" 2>/dev/null || true
  fi
  set +e
  while :; do
    wait "$apphost_pid"
    apphost_status=$?
    kill -0 "$apphost_pid" 2>/dev/null || break
  done
  set -e

  if [ "$shutdown_requested" = true ]; then
    verify_controller_identity
    preflight_resources
    stop_owned_containers
  fi

  exit "$apphost_status"
}

validate_installation_id
trap 'exit 130' INT
trap 'exit 143' TERM

case "$stop_timeout" in
  ""|*[!0-9]*|?????*) fail "ControllerConfiguration__StopTimeoutSeconds must be between 1 and 3600" ;;
esac
[ "$stop_timeout" -ge 1 ] && [ "$stop_timeout" -le 3600 ] ||
  fail "ControllerConfiguration__StopTimeoutSeconds must be between 1 and 3600"

case "$command_name" in
  run)
    configure_engine
    run_controller
    ;;
  stop)
    configure_engine
    check_engine
    verify_controller_identity
    preflight_resources
    stop_owned_containers
    ;;
  reset)
    configure_engine
    check_engine
    verify_controller_identity
    resolve_data_root
    preflight_resources
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
