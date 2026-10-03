#!/bin/bash
# Standalone launcher for the published Mímisbrunnr release controller.
# Windows/PowerShell 7 equivalent: run.ps1, same contract.
#
# Self-contained on purpose: copy this one file out of the repository and run it. It reads nothing
# from the checkout and needs no .NET SDK — only bash, docker and openssl. The optional version-drift
# report is the only thing that looks for a git checkout, and it degrades to a plain image digest when
# there is none.
#
# It starts ONE container - ghcr.io/...-apphost - which carries the Aspire AppHost, DCP and dashboard.
# That controller then starts mimisbrunnr-<id>-{host,postgres,blob-well,seq} through the mounted
# socket. Do not start those four here: the controller owns them, and its ownership, health and
# lifecycle rules live in its own entrypoint. This script starts and replaces that one container.
#
# Running it again stops the running controller (which removes the four workloads), pulls the newest
# image, and starts a fresh controller. Data volumes and the data-root folders are preserved; only the
# containers are replaced. As the docs put it, an upgrade is a migration: the new image applies its own
# migrations at startup, so snapshot a corpus you care about before running this over it.
#
# Host layout (data root, so the corpus is a folder you can see, back up and delete):
#
#   ~/.mimisbrunnr/controller.env                      # 600, the five release secrets
#   ~/.mimisbrunnr/volumes/<id>/postgres-data/
#   ~/.mimisbrunnr/volumes/<id>/blob-well-data/
#   ~/.mimisbrunnr/volumes/<id>/seq-data/
#   ~/.mimisbrunnr/volumes/<id>/host-context/
#   ~/.mimisbrunnr/volumes/<id>/controller-state/       # mounted, not an engine volume
#
# Verbs: up (default, and a restart when one is already running) | env | status | stop | logs
# Env:   MIMIS_ID, CONTROLLER_IMAGE, MIMIS_DATA_ROOT (set to "off" for engine-managed volumes),
#        MIMIS_BIND_ADDRESS, MIMIS_SELINUX, and the P_* ports below.
set -euo pipefail
# Narrow at the top rather than around each write: the credential temp file, the data-root folders and
# the final rename then all inherit it, and there is no create-then-chmod window in which a secret is
# briefly group-readable.
umask 077

# The checkout, if this file was run from one. Only the version-drift report uses it, so a copy in
# ~/bin or on a jump host works with no repository present.
repo_root=""
if git -C "$(dirname "$0")" rev-parse --show-toplevel >/dev/null 2>&1; then
  repo_root="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"
fi

installation_id="${MIMIS_ID:-default}"
controller_image="${CONTROLLER_IMAGE:-ghcr.io/generic-automation-and-it/smooth-ai-product-context-memory-apphost:latest}"

data_home="${MIMIS_HOME:-$HOME/.mimisbrunnr}"
data_root="$data_home/volumes"
env_file="$data_home/controller.env"
# Derived from data_home, not $HOME. Anchoring it to $HOME meant a run with a custom MIMIS_HOME
# published machine credentials into the operator's real ~/.mimisbrunnr anyway — so a harness pointed at
# a scratch home still overwrote live credentials, which is the one thing an operator-launcher test must
# never do. The default is unchanged for normal use, because data_home defaults to ~/.mimisbrunnr.
machine_credentials="${MIMIS_MACHINE_CREDENTIALS:-$data_home/credentials}"
machine_credentials_read_only="${machine_credentials%.env}-read-only"
data_root_setting="${MIMIS_DATA_ROOT:-}"

# The documented release ports (docker.md): API 5141, PostgreSQL 5432, blob 9000/9001, Seq 5341,
# dashboard 15278. They are also what provision-credentials.sh writes into CONTEXT_MEMORY_BASE_URL and
# what the context-memory client defaults to, so moving them here would break every skill and
# client the moment a controller was started. An earlier draft shifted them all by +20000 to dodge a
# dev AppHost that may be running; that made the launcher correct in isolation and wrong in use, and it
# is the reason a provisioned credential file could not reach the stack it was provisioned for.
# MIMIS_SHIFT_PORTS=1 restores the old offsets for the rare side-by-side case.
if [ "${MIMIS_SHIFT_PORTS:-0}" = 1 ]; then
  p_host="${P_HOST:-25141}"
  p_postgres="${P_POSTGRES:-25432}"
  p_blob="${P_BLOB:-29000}"
  p_blob_console="${P_BLOB_CONSOLE:-29001}"
  p_seq="${P_SEQ:-25351}"
  # Inside the branch: the dev AppHost holds 15278 and 19075 too, so shifting only the workload ports
  # would leave the two stacks contending for the dashboard and OTLP and preflight would refuse with
  # "Dashboard port 15278 is already in use" - the side-by-side case could never start.
  p_dashboard="${P_DASHBOARD:-25278}"
  p_otlp="${P_OTLP:-29075}"
else
  p_host="${P_HOST:-5141}"
  p_postgres="${P_POSTGRES:-5432}"
  p_blob="${P_BLOB:-9000}"
  p_blob_console="${P_BLOB_CONSOLE:-9001}"
  p_seq="${P_SEQ:-5341}"
  p_dashboard="${P_DASHBOARD:-15278}"
  p_otlp="${P_OTLP:-19075}"
fi
api_base_url="http://localhost:${p_host}"

# Docker Desktop on macOS and Windows and native Linux all expose the socket at this path inside the
# VM; only rootless engines differ, and that is a documented override rather than a guess.
engine_socket="${MIMIS_SOCKET:-/var/run/docker.sock}"
bind_address="${MIMIS_BIND_ADDRESS:-127.0.0.1}"

controller_name="mimisbrunnr-${installation_id}-controller"
state_volume="mimisbrunnr-${installation_id}-controller-state"
postgres_volume="mimisbrunnr-${installation_id}-postgres-data"
group_name="smooth-mímisbrunnr-release-${installation_id}"
dashboard_host="http://localhost:${p_dashboard}"
container_data_root="/var/lib/mimisbrunnr-data"

die() { echo "run.sh: $*" >&2; exit 1; }
# Diagnostics go to stderr, never stdout. stdout is a data channel here - `env-export` is captured with
# $(...) and eval'd into a shell - so a status line written there becomes a command the shell tries to
# run. Caught by sourcing the export, which failed on "run.sh: ...: command not found".
log() { echo "run.sh: $*" >&2; }

# The entrypoint validates this too, but it does so after the image has started. Validating here keeps
# the id out of resource names, labels and paths until it is known to be a plain lowercase slug.
case "$installation_id" in
  "" | *[!a-z0-9-]* | -*) die "MIMIS_ID must start with a lowercase letter or digit and contain only lowercase letters, digits and hyphens" ;;
esac
[ "${#installation_id}" -le 32 ] || die "MIMIS_ID must be at most 32 characters"

# A secret file that is a symlink would send this run's credentials wherever the link points, and a
# directory would make the write fail after the values were already chosen. Test the link itself: `-f`
# follows it, so a link to a perfectly ordinary file passes a plain `[ -f ]` test.
if [ -L "$env_file" ]; then
  die "$env_file is a symlink; refusing to write secrets through it"
fi
if [ -e "$env_file" ] && [ ! -f "$env_file" ]; then
  die "$env_file exists but is not a regular file; refusing to write secrets through it"
fi

# Removes a half-written credential file if the run is interrupted between mktemp and mv.
temp_env=""
cleanup() { [ -z "$temp_env" ] || rm -f "$temp_env"; }
trap cleanup EXIT INT TERM HUP

use_data_root=1
case "$data_root_setting" in
  "" | on | true | yes | 1) ;;
  off | false | no | 0 | none) use_data_root=0 ;;
  *) die "MIMIS_DATA_ROOT must be a path, or off/named — not '${data_root_setting}'" ;;
esac

port_is_free() {
  # bash /dev/tcp rather than nc: nc's flags differ between BSD and GNU, and this repo has already
  # shipped one harness that only ever passed on the platform it was written on. The connect happens
  # in a subshell, so its descriptor dies with the subshell — never close it in the parent, or
  # `exec 3>&-` closes a descriptor this shell never opened, which bash reports as a redirection
  # error and exits on (a silent exit 1, with no message).
  if (exec 3<>"/dev/tcp/127.0.0.1/$1") 2>/dev/null; then
    return 1
  fi
  return 0
}

# require_port <port> <label> <override-variable>
require_port() {
  port_is_free "$1" || die "$2 port $1 is already in use; override it (e.g. $3=25432 ./$(basename "$0") up)"
}

selinux_enforcing() {
  case "${MIMIS_SELINUX:-auto}" in
    1 | on | true | yes) return 0 ;;
    0 | off | false | no) return 1 ;;
  esac
  [ "$(cat /sys/fs/selinux/enforce 2>/dev/null || echo 0)" = "1" ]
}

# Emits docker run arguments for a bind, NUL-separated so a path can contain spaces. `--mount` is
# unambiguous about colons in a Windows drive letter but cannot carry an SELinux label; on an enforcing
# host (Fedora/RHEL) the bind is refused without `:z`, so use -v there.
bind_args() {
  local source="$1" target="$2"
  if selinux_enforcing; then
    printf '%s\0%s\0' -v "$source:$target:z"
  else
    printf '%s\0%s\0' --mount "type=bind,source=$source,target=$target"
  fi
}

# bash 3.2 (still the macOS default) expands `"${arr[@]}"` on an empty array as an unbound-variable
# error under `set -u`, so every expansion of a possibly-empty array needs the `+` guard below.

# ---------------------------------------------------------------------------------------------
# credentials: process environment wins, then the stored file, then generate and persist
# ---------------------------------------------------------------------------------------------

file_value() {
  # Reads a literal KEY=value line. No sourcing: Parameters__api-*-token are not shell identifiers,
  # and `set -a && source` on a file holding them prints "command not found".
  [ -f "$env_file" ] || return 1
  sed -n "s/^$1=//p" "$env_file" | head -1
}

# scripts/provision-credentials.sh writes the *same* two token values into .context/mimisbrunnr.env
# (as CONTEXT_MEMORY_*, which skills read) and .context/mimisbrunnr.env.controller (as
# Parameters__*, which a container reads via --env-file). If this launcher mints its own pair instead,
# the controller and the skills hold different credentials and every call is a 403 - so a provisioned
# pair is adopted rather than replaced.
provisioned_tokens() {
  # Emits "read=<value>" and "write=<value>" for a matched pair, or nothing. All-or-nothing on purpose:
  # resolving each key against its own source could adopt one token and mint the other, producing exactly
  # the split pair the adoption exists to prevent - reached through a plausible-looking knob.
  local source_file read_token write_token
  for source_file in ${MIMIS_TOKEN_FILE:+"$MIMIS_TOKEN_FILE"} "${repo_root:+$repo_root/.context/mimisbrunnr.env.controller}"; do
    [ -n "$source_file" ] && [ -f "$source_file" ] || continue
    read_token="$(sed -n 's/^Parameters__api-read-token=//p' "$source_file" | head -1)"
    write_token="$(sed -n 's/^Parameters__api-write-token=//p' "$source_file" | head -1)"
    if [ -n "$read_token" ] && [ -n "$write_token" ] && [ "$read_token" != "$write_token" ]; then
      printf 'read=%s\nwrite=%s\n' "$read_token" "$write_token"
      return 0
    fi
  done
  return 1
}

env_value() {
  # printenv takes the name as an argument, so the hyphenated parameter names work unchanged.
  printenv "$1" 2>/dev/null || true
}

# Hex from the kernel CSPRNG, with openssl as the preferred path where it exists. `/dev/urandom` is
# POSIX and present everywhere the script runs; openssl is declared by neither Dockerfile, so relying on
# it alone made credential generation fail on a minimal image with nothing but bash. This was found by
# the harness on a stock `bash:5.2` container, where the failure surfaced as an empty credential file
# rather than as a missing command.
secret() {
  local bytes="${1:-24}"
  if command -v openssl >/dev/null 2>&1; then
    openssl rand -hex "$bytes"
  else
    od -An -tx1 -N "$bytes" /dev/urandom | tr -d ' \n'
  fi
}

# Publish the tokens at machine level, outside every repository, so the skills work in any checkout
# without that repository's provisioner. Written here rather than read from a per-repo file because a
# gitignored .context/ cannot exist in a repo the skill has never been installed into — which is the
# whole cross-repo case. Two files, not one: a read-only worker that sources both would hold a write
# token, and the read client refuses to start in that state by design.
write_machine_credentials() {
  local read_token write_token
  read_token="$(file_value Parameters__api-read-token || true)"
  write_token="$(file_value Parameters__api-write-token || true)"
  [ -n "$read_token" ] && [ -n "$write_token" ] || return 0

  local tmp
  ( umask 077
    tmp="$(mktemp "$machine_credentials.XXXXXX")"
    {
      echo "# Mímisbrunnr API credentials — machine level, outside any repository."
      echo "# Written by scripts/run.sh. Read by the context-memory clients automatically."
      echo "CONTEXT_MEMORY_BASE_URL=$api_base_url"
      echo "CONTEXT_MEMORY_READ_TOKEN=$read_token"
      echo "CONTEXT_MEMORY_WRITE_TOKEN=$write_token"
    } >"$tmp"
    mv "$tmp" "$machine_credentials"
    tmp="$(mktemp "$machine_credentials_read_only.XXXXXX")"
    {
      echo "# Read-only half. Source this for a worker that must not mutate; the read client refuses to"
      echo "# start when a write token is present, so it reads only what it needs from the file above."
      echo "CONTEXT_MEMORY_BASE_URL=$api_base_url"
      echo "CONTEXT_MEMORY_READ_TOKEN=$read_token"
    } >"$tmp"
    mv "$tmp" "$machine_credentials_read_only"
  )
  log "machine credentials written: $machine_credentials"
}

# Print the credentials in the form a shell can consume, for operators who want them in their profile
# rather than relying on the machine file. Deliberately a separate verb: `up` must never print a token
# to a terminal that may be scrolled back, captured by a screen recording, or read over someone's
# shoulder. The caller chooses to run this and to redirect it.
#
# Only CONTEXT_MEMORY_* names are printed. They are valid shell identifiers, so `export` accepts them;
# the container's Parameters__* names contain hyphens and cannot be assigned by any shell at all.
#
# A real environment variable always wins over the machine file in the clients, so exporting these
# changes nothing except where the value is read from — it does not fork the credential.
env_export() {
  local format="${1:-posix}"
  local read_token write_token
  read_token="$(file_value Parameters__api-read-token || true)"
  write_token="$(file_value Parameters__api-write-token || true)"
  [ -n "$read_token" ] && [ -n "$write_token" ] ||
    die "no credentials in $env_file yet — run this script first (or its 'env' verb)"

  case "$format" in
    posix | sh | bash | zsh)
      echo "export CONTEXT_MEMORY_BASE_URL='$api_base_url'"
      echo "export CONTEXT_MEMORY_READ_TOKEN='$read_token'"
      echo "export CONTEXT_MEMORY_WRITE_TOKEN='$write_token'"
      ;;
    powershell | ps1 | pwsh)
      echo "\$env:CONTEXT_MEMORY_BASE_URL = '$api_base_url'"
      echo "\$env:CONTEXT_MEMORY_READ_TOKEN = '$read_token'"
      echo "\$env:CONTEXT_MEMORY_WRITE_TOKEN = '$write_token'"
      ;;
    *)
      die "format must be posix or powershell, not '$format'"
      ;;
  esac
}

write_credentials() {
  local keys="PostgresConfiguration__Password BlobConfiguration__AccessKey BlobConfiguration__SecretKey Parameters__api-read-token Parameters__api-write-token"
  # `adopted` is initialised rather than only declared: it is incremented only when a provisioned pair
# exists, so on a machine with no provision file — a fresh clone, a CI container — it is never assigned,
# and `set -u` aborts on the log line that reads it. A checkout that has run the provisioner hides this,
# which is why it survived until the harness ran without one.
local key value from_env stored generated=0 ephemeral=0 missing=0 adopted=0
  local pg_password blob_key blob_secret read_token write_token
  local -a persisted=()
  # Resolved once, before the loop, and only as a matched pair - see provisioned_tokens.
  local provisioned_pair
  provisioned_pair="$(provisioned_tokens || true)"

  for key in $keys; do
    from_env="$(env_value "$key")"
    stored="$(file_value "$key" || true)"

    if [ -n "$from_env" ]; then
      value="$from_env"
      ephemeral=$((ephemeral + 1))
    elif [ -n "$stored" ]; then
      value="$stored"
    else
      # Only the two token parameters have a provisioned counterpart; the engine secrets never do.
      case "$key" in
        Parameters__api-read-token) value="$(printf '%s' "$provisioned_pair" | sed -n 's/^read=//p')" ;;
        Parameters__api-write-token) value="$(printf '%s' "$provisioned_pair" | sed -n 's/^write=//p')" ;;
        *) value="" ;;
      esac
      if [ -n "$value" ]; then
        adopted=$((adopted + 1))
      else
        value="$(secret 32)"
        generated=$((generated + 1))
      fi
    fi

    # The file is rewritten only to fill gaps, and never loses a key it already had. An earlier draft
    # persisted only the values this run selected, so exporting one variable silently dropped the
    # stored token for it — and the next run without that variable minted a different one, 403ing a
    # Host that was already running with the old value.
    if [ -n "$stored" ]; then
      persisted+=("$key=$stored")
    else
      persisted+=("$key=$value")
      missing=$((missing + 1))
    fi

    case "$key" in
      PostgresConfiguration__Password) pg_password="$value" ;;
      BlobConfiguration__AccessKey) blob_key="$value" ;;
      BlobConfiguration__SecretKey) blob_secret="$value" ;;
      Parameters__api-read-token) read_token="$value" ;;
      Parameters__api-write-token) write_token="$value" ;;
    esac
  done

  [ "$read_token" != "$write_token" ] || die "read and write tokens are identical; the read/write capability split would be void"

  if [ "$missing" -gt 0 ]; then
    temp_env="$(mktemp "$data_home/controller.env.XXXXXX" 2>/dev/null || mktemp "${TMPDIR:-/tmp}/mimisbrunnr.env.XXXXXX")"
    {
      echo "# Release-controller secrets for installation '$installation_id'."
      echo "# 600, outside any checkout. Values already present here are reused verbatim."
      echo "# Generated by run.sh; anything supplied through the environment is deliberately absent."
      for pair in ${persisted[@]+"${persisted[@]}"}; do echo "$pair"; done
    } >"$temp_env"
    mv "$temp_env" "$env_file"
    temp_env=""
    log "wrote $env_file (mode 600) — $generated generated, $adopted adopted from provision-credentials.sh, $missing new"
    [ "$adopted" -gt 0 ] && log "  tokens match the CONTEXT_MEMORY_* values your skills hold, so they will not 403"
  fi

  # Every run, not only one that wrote the file: the machine credential file is what makes the skills
  # work in a checkout that has never run this repository's provisioner, so a stack that started before
  # that file existed would leave every other repo unable to authenticate.
  write_machine_credentials

  # Re-assert the mode on every run, not only when writing. A file widened by a backup restore or a
  # careless copy would otherwise stay group-readable for the life of the installation, and no test
  # would notice because reuse is the quiet path.
  chmod 600 "$env_file" 2>/dev/null || die "cannot restrict permissions on $env_file"

  if [ "$ephemeral" -gt 0 ]; then
    log "NOTE: $ephemeral secret(s) came from your environment and are not stored."
    log "      Re-export them next run, or the running Host's tokens change and every skill 403s."
  fi

  # Confirm every value the controller will actually receive is present and non-empty, because a
  # blank one fails much later, inside AppHost startup.
  for pair in "PostgresConfiguration__Password:$pg_password" "BlobConfiguration__AccessKey:$blob_key" \
    "BlobConfiguration__SecretKey:$blob_secret" "Parameters__api-read-token:$read_token" \
    "Parameters__api-write-token:$write_token"; do
    [ -n "${pair#*:}" ] || die "${pair%%:*} resolved empty; the controller would start and never become ready"
  done
}

# ---------------------------------------------------------------------------------------------
# data root
# ---------------------------------------------------------------------------------------------

prepare_data_root() {
  [ "$use_data_root" = 1 ] || return 0
  mkdir -p "$data_root/$installation_id/controller-state"
  chmod 700 "$data_home"
  # Docker Desktop only shares some host paths; a data root outside them mounts as an empty folder,
  # which looks exactly like a fresh install and silently orphans the previous corpus.
  case "$data_home" in
    /Users/* | /home/*) ;;
    *) log "NOTE: $data_home is not a default-shared Desktop path; add it in Docker Desktop → Settings → Resources → Shared." ;;
  esac
  log "data root: $data_root/$installation_id"
}

# The entrypoint refuses a volume that is not bound to the folder it expects, before touching
# anything. Catch that here so the operator gets the reason instead of a controller log line, and
# never resolve it by deleting a volume on their behalf.
check_volume_backing() {
  [ "$use_data_root" = 1 ] || return 0
  local suffix volume device expected
  for suffix in postgres-data blob-well-data seq-data host-context; do
    volume="mimisbrunnr-${installation_id}-${suffix}"
    device="$(docker volume inspect --format '{{index .Options "device"}}' "$volume" 2>/dev/null || true)"
    [ -n "$device" ] || continue
    expected="$data_root/$installation_id/$suffix"
    # Desktop reports the same folder as /Users/… or /host_mnt/Users/…; the entrypoint treats them
    # as equal, so compare the same way rather than inventing a stricter rule here.
    case "$device" in
      /host_mnt/*) device="${device#/host_mnt}" ;;
    esac
    if [ "$device" != "$expected" ]; then
      cat >&2 <<EOF
run.sh: '$volume' is bound to '$device', not '$expected'.
  Adopting a data root is a fresh start, not a move — the controller will refuse before changing
  anything. Pick one:

    new installation     MIMIS_ID=<new-id> $0 up
    keep this corpus     MIMIS_DATA_ROOT=off $0 up        # engine-managed volumes, as it was created
    adopt the data root  snapshot + verify, reset the installation, start on the data root, then
                         restore --force into it
EOF
      exit 1
    fi
  done
}

# ---------------------------------------------------------------------------------------------
# engine preflight
# ---------------------------------------------------------------------------------------------

preflight() {
  command -v docker >/dev/null || die "docker is not on PATH"
  docker info >/dev/null 2>&1 || die "the container engine is unreachable"

  # Known open defect (issue #159): a second controller tears down every other installation's running
  # workloads. Volumes survive, so it looks survivable. Refuse instead of letting it happen. This
  # runs after remove_existing_controller, so this installation's own controller is already gone and
  # only a *different* one can reach here.
  local others
  others="$(docker ps --format '{{.Names}}' | grep -E '^mimisbrunnr-.*-controller$' || true)"
  if [ -n "$others" ]; then
    printf 'run.sh: another controller is running:\n%s\n' "$others" >&2
    die "stop it first: docker stop <name> && docker rm <name>   (or run that installation's own launcher with its MIMIS_ID)"
  fi

  # Always pull a mutable tag. "Pull only when the image is absent" silently reuses a stale local
  # `latest` forever. A digest-pinned reference cannot drift, so it is honoured as given.
  case "$controller_image" in
    *@sha256:*)
      docker image inspect "$controller_image" >/dev/null 2>&1 || {
        log "pulling pinned $controller_image"
        docker pull "$controller_image" >/dev/null
      }
      ;;
    *)
      log "pulling $controller_image"
      docker pull "$controller_image" >/dev/null
      ;;
  esac

  report_version_drift

  require_port "$p_dashboard" dashboard P_DASHBOARD
  require_port "$p_otlp" OTLP P_OTLP
  require_port "$p_host" API P_HOST
  require_port "$p_postgres" PostgreSQL P_POSTGRES
  require_port "$p_blob" "blob S3" P_BLOB
  require_port "$p_blob_console" "blob console" P_BLOB_CONSOLE
  require_port "$p_seq" Seq P_SEQ
}

# The controller bakes its API image in at build time, and the pipeline publishes only on main pushes
# — so `latest` lags both this checkout and any run still in flight. Report the gap instead of letting
# it read as current: the API container is what the operator is actually testing.
report_version_drift() {
  local image_env api_image baked_version local_head
  image_env="$(docker image inspect --format '{{range .Config.Env}}{{println .}}{{end}}' "$controller_image")"
  api_image="$(printf '%s\n' "$image_env" | sed -n 's/^HostConfiguration__Image=//p' | head -1)"
  baked_version="$(printf '%s\n' "$image_env" | sed -n 's/^ReleaseConfiguration__Version=//p' | head -1)"

  case "$api_image" in
    "" | @sha256:0000*) die "$controller_image has no usable baked API image (got '${api_image:-none}'); build it locally with --build-arg API_IMAGE=<repo>@sha256:<digest>" ;;
  esac
  log "API image: $api_image"

  local_head="$(git -C "$repo_root" rev-parse --short HEAD 2>/dev/null || true)"
  [ -n "$local_head" ] || return 0

  # Only the `main-<sha>` form the pipeline bakes can be compared against a commit.
  local baked_sha behind
  baked_sha="$(printf '%s' "$baked_version" | sed -n 's/^main-\([0-9a-f]\{7\}\)$/\1/p')"
  if [ -z "$baked_sha" ]; then
    log "image reports version '${baked_version:-unknown}'; cannot compare it to HEAD $local_head."
  elif [ "$baked_sha" = "$local_head" ]; then
    log "controller image matches HEAD ($local_head)"
  elif git -C "$repo_root" cat-file -e "$baked_sha^{commit}" 2>/dev/null; then
    behind="$(git -C "$repo_root" rev-list --count "$baked_sha..$local_head" 2>/dev/null || echo '?')"
    log "WARNING: image is $behind commit(s) behind HEAD ($baked_sha -> $local_head). The API container runs the older code, not your checkout."
  else
    log "image reports main-$baked_sha; that commit is not in this checkout (history rewritten, or a shallow fetch)."
  fi
}

# ---------------------------------------------------------------------------------------------
# verbs
# ---------------------------------------------------------------------------------------------

# Replaces any running controller for this installation, so a plain `up` is a restart rather than a
# failure. The graceful stop is the controller's own: its shutdown trap tears down the four workloads
# it owns, which is what removes mimisbrunnr-<id>-host, and preserves every volume. Only the
# controller container is then deleted — never a volume, and never a container this installation does
# not own.
remove_existing_controller() {
  docker container inspect "$controller_name" >/dev/null 2>&1 || return 0

  local state
  state="$(docker container inspect --format '{{.State.Status}}' "$controller_name")"
  if [ "$state" = running ]; then
    log "replacing the running $controller_name (graceful stop; volumes preserved)"
    # The controller needs time to stop its workloads: 180 s matches its own --stop-timeout.
    docker stop --time 180 "$controller_name" >/dev/null || true
  else
    log "removing the stopped $controller_name"
  fi
  docker rm "$controller_name" >/dev/null 2>&1 || true

  # A hard kill can leave workloads behind with no controller owning them. They carry this
  # installation's ownership label, so remove exactly those and nothing else; a foreign container
  # occupying one of the names is refused by the controller's own preflight rather than deleted here.
  local leftover
  leftover="$(owned_workload_containers || true)"
  if [ -n "$leftover" ]; then
    log "removing orphaned workloads: $(printf '%s' "$leftover" | tr '\n' ' ')"
    printf '%s\n' "$leftover" | while IFS= read -r name; do
      [ -n "$name" ] || continue
      docker stop --time 60 "$name" >/dev/null 2>&1 || true
      docker rm "$name" >/dev/null 2>&1 || true
    done
  fi

  # The ports stay bound for a moment after the container goes; wait rather than fail the free-port
  # check on a race that resolves itself in under a second.
  local waited=0
  while [ "$waited" -lt 15 ]; do
    port_is_free "$p_dashboard" && port_is_free "$p_host" && return 0
    sleep 1
    waited=$((waited + 1))
  done
}

owned_workload_containers() {
  local suffix name
  for suffix in host postgres blob-well seq; do
    name="mimisbrunnr-${installation_id}-${suffix}"
    if docker container inspect "$name" >/dev/null 2>&1 &&
      [ "$(docker container inspect --format "{{index .Config.Labels \"io.smooth-mimisbrunnr.installation\"}}" "$name" 2>/dev/null)" = "$installation_id" ]; then
      printf '%s\n' "$name"
    fi
  done
}

up() {
  mkdir -p "$data_home"
  prepare_data_root
  write_credentials
  # Check the data-root binding BEFORE stopping anything. A mismatch is an operator mistake that no
  # amount of restarting fixes, and tearing the running stack down first would leave the machine with
  # no service at all when the run then refuses.
  check_volume_backing
  remove_existing_controller
  preflight

  local -a mounts=()
  if [ "$use_data_root" = 1 ]; then
    while IFS= read -r -d '' arg; do mounts+=("$arg"); done < <(bind_args "$engine_socket" "$engine_socket")
    while IFS= read -r -d '' arg; do mounts+=("$arg"); done < <(bind_args "$data_root" "$container_data_root")
    while IFS= read -r -d '' arg; do mounts+=("$arg"); done < <(bind_args "$data_root/$installation_id/controller-state" /var/lib/mimisbrunnr)
  else
    docker volume create "$state_volume" >/dev/null
    mounts+=(-v "$engine_socket:$engine_socket")
    # The named volume has to be mounted, not merely created: without it the image's own VOLUME
    # directive satisfies /var/lib/mimisbrunnr from an anonymous volume, so the corpus lands somewhere
    # `docker volume ls` cannot name and this one stays empty. docker.md:38 documents the mount.
    mounts+=(-v "$state_volume:/var/lib/mimisbrunnr")
  fi

  log "starting $controller_name"
  # The group label must equal smooth-mímisbrunnr-release-<installation-id> exactly: the controller
  # cannot label itself, so a mismatch leaves the dashboard's container ungrouped in Docker Desktop.
  # Workload ports are not published here — the controller binds them on EngineConfiguration__BindAddress.
  docker run -d \
    --name "$controller_name" \
    --label "com.docker.compose.project=$group_name" \
    --label "com.docker.compose.service=$controller_name" \
    --stop-timeout 180 \
    ${mounts[@]+"${mounts[@]}"} \
    --env-file "$env_file" \
    -e "InstallationConfiguration__Id=$installation_id" \
    -e "EngineConfiguration__BindAddress=$bind_address" \
    -e "ControllerConfiguration__DataRootMount=$container_data_root" \
    -e "HostConfiguration__Port=$p_host" \
    -e "PostgresConfiguration__Port=$p_postgres" \
    -e "BlobConfiguration__Port=$p_blob" \
    -e "BlobConfiguration__ConsolePort=$p_blob_console" \
    -e "SeqConfiguration__Port=$p_seq" \
    -p "127.0.0.1:$p_dashboard:15278" \
    -p "127.0.0.1:$p_otlp:19075" \
    "$controller_image" >/dev/null

  echo
  printf '  dashboard   %s   (login URL below)\n' "$dashboard_host"
  printf '  API         http://localhost:%s\n' "$p_host"
  printf '  PostgreSQL  127.0.0.1:%s   blob 127.0.0.1:%s/%s   Seq 127.0.0.1:%s\n' "$p_postgres" "$p_blob" "$p_blob_console" "$p_seq"
  printf '  group       %s\n' "$group_name"
  [ "$use_data_root" = 1 ] || printf '  volumes     engine-managed named volumes (MIMIS_DATA_ROOT=off)\n'
  echo
  printf '  A bare visit to the dashboard redirects to /login; this URL carries the token:\n'
  # The dashboard prints its login URL seconds after the container starts, so `up` returns before it
  # exists. Poll briefly rather than report a blank line the operator would read as a failure.
  login_url=""
  local _
  for _ in 1 2 3 4 5 6 7 8 9 10 11 12; do
    login_url="$(docker logs "$controller_name" 2>&1 | grep -oE 'http://[^ ]*/login\?t=[^ ]*' | tail -1 || true)"
    [ -n "$login_url" ] && break
    sleep 5
  done
  if [ -n "$login_url" ]; then
    # Aspire prints the in-container host:port; the operator reaches it on the published one. Only the
    # path and token are reused, so a rewritten port can never produce a URL that looks reachable and
    # is not. Strip through the authority — `${url#*:*}` would stop at the "http:" colon.
    login_path="$(printf '%s' "$login_url" | sed 's|^[^/]*//[^/]*||')"
    printf '    %s%s\n' "$dashboard_host" "$login_path"
  else
    printf '    not printed yet — read it with: %s logs\n' "$0"
  fi
  printf '\n  run it again to pull a newer release and restart. stop with: %s stop\n' "$0"

  wait_for_api || exit 1
  printf '  API         healthy at http://localhost:%s\n' "$p_host"
}

# An earlier draft returned here immediately, so `up` printed the API URL and exited 0 while the API
# container was crash-looping on a database it could not authenticate to. "The script ran" and "the app
# started" looked identical, which is exactly the failure it took three session restarts to diagnose.
# Wait for real health, and on failure print the one line that identifies the cause.
wait_for_api() {
  local waited=0 code
  log "waiting for the API to answer on 127.0.0.1:$p_host"
  while [ "$waited" -lt "${P_WAIT_SECONDS:-180}" ]; do
    code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 4 "http://127.0.0.1:$p_host/health" 2>/dev/null || true)"
    [ "$code" = "200" ] && return 0
    sleep 3
    waited=$((waited + 3))
  done

  echo
  log "the API did not become healthy within ${P_WAIT_SECONDS:-180}s (last status '${code:-no response}')."
  printf '  the containers are up, so the fault is inside one of them. Most likely, in order:\n\n'
  printf '    1. postgres rejected the password — this data root was initialised under a different one.\n'
  printf '       PostgreSQL fixes its password on first init and ignores it forever after, so a\n'
  printf '       freshly generated one can never match. Check: docker logs %s 2>&1 | grep -i auth | tail -3\n' \
    "mimisbrunnr-${installation_id}-postgres"
  printf '    2. the token pair drifted — %s logs, then look for 403.\n' "$0"
  printf '    3. migrations are pending — %s logs, look for "Migration".\n\n' "$0"
  printf '  full stack state: %s status\n' "$0"
  return 1
}

# Every verb needs the same installation id, and MIMIS_ID is per-invocation. If it was not passed,
# adopt the single running controller rather than reporting "not found" for one that is plainly there.
resolve_running_controller() {
  [ "${MIMIS_ID+x}" = x ] && return 0
  local found
  found="$(docker ps --format '{{.Names}}' | grep -E '^mimisbrunnr-.*-controller$' || true)"
  [ "$(printf '%s\n' "$found" | grep -c . || true)" -eq 1 ] || return 0
  controller_name="$found"
  # The id travels with the name. Rewriting only the name left installation_id at its default, so the
  # restart hint printed MIMIS_ID=default and the operator following it started a second, empty
  # installation on the same ports instead of restarting theirs.
  installation_id="${found#mimisbrunnr-}"
  installation_id="${installation_id%-controller}"
  # The group label carries the id as well, and `status` filters its container table on it. It is built
  # once at the top of this script from the id this invocation was given, so without re-deriving it here
  # an adopted installation is filtered on the *default* group and prints an empty table directly above
  # a line naming that controller as running.
  group_name="smooth-mímisbrunnr-release-${installation_id}"
  log "no MIMIS_ID given; using the running $controller_name (installation '$installation_id')"
}

status() {
  docker container ls --all \
    --filter "label=com.docker.compose.project=$group_name" \
    --format 'table {{.Names}}\t{{.Status}}\t{{.Ports}}'
  echo
  docker container inspect "$controller_name" \
    --format 'controller {{.Name}} state={{.State.Status}} exit={{.State.ExitCode}}' 2>/dev/null || true
  [ "$use_data_root" = 1 ] || return 0
  echo
  printf 'data root: %s/%s\n' "$data_root" "$installation_id"
  printf 'credentials: %s\n' "$env_file"
}

stop() {
  # Graceful stop removes owned workloads and preserves every data volume and data-root folder.
  if docker container inspect "$controller_name" >/dev/null 2>&1; then
    log "stopping $controller_name (volumes and $data_root/$installation_id preserved)"
    docker stop --time 180 "$controller_name" >/dev/null
    docker rm "$controller_name" >/dev/null
    log "removed; restart with the same image, id, env file and data root: MIMIS_ID=$installation_id $0 up"
  else
    log "no controller named $controller_name"
  fi
}

case "${1:-up}" in
  up) up ;;
  env) mkdir -p "$data_home"; write_credentials ;;
  status) resolve_running_controller; status ;;
  stop) resolve_running_controller; stop ;;
  logs) resolve_running_controller; docker logs --tail 200 "$controller_name" ;;
  env-export | export-env) mkdir -p "$data_home"; write_credentials; env_export "${2:-posix}" ;;
  *) die "unknown verb '${1}'; use up | env | env-export | status | stop | logs" ;;
esac
