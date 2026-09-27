#!/usr/bin/env bash
# One-command API credential provisioning for a local Mímisbrunnr deployment.
#
# Writes two distinct random Bearer tokens to a gitignored env file that BOTH the service and the
# host-side skills read, AND bridges the AppHost path so Aspire injects the same values. One file, two
# name forms, one source of truth:
#   - ApiAccess__ReadToken / ApiAccess__WriteToken   (what the Host's authorizer reads)
#   - Parameters__api-read-token / Parameters__api-write-token
#                                                   (the AppHost/container controller reads these —
#                                                    the env-var spelling of the same keys the
#                                                    user-secrets bridge below sets, and the only form
#                                                    that works in Production where user secrets are not
#                                                    loaded; usable as `--env-file` for the published
#                                                    controller)
#   - CONTEXT_MEMORY_READ_TOKEN / CONTEXT_MEMORY_WRITE_TOKEN / CONTEXT_MEMORY_BASE_URL
#                                                   (what the skills read)
# The values are identical across the two name forms; only the env var names differ.
#
# The tokens are runtime configuration only: the Host hashes them at startup and never writes them to
# the store, so regenerating them invalidates no data. Deleting the file and re-running is a complete
# recovery procedure. See docs/wiki/setup.md.
#
# Run modes this makes work with the same tokens:
#   - Container: the Host reads the ApiAccess__* names from this file via `--env-file`.
#   - Direct Host run: the exported ApiAccess__* names are ordinary .NET configuration.
#   - AppHost: Aspire's `AddParameter("api-read-token", secret: true)` falls back to user secrets, so
#     the script writes `Parameters:api-read-token` / `Parameters:api-write-token` for the AppHost
#     project — making Aspire inject the same values the skills hold. Without this, the AppHost
#     generates its own per-session tokens and every skill request 403s.
#     User secrets load in **Development only**. For the published `-apphost` controller (Production),
#     the env file carries `Parameters__api-read-token` / `Parameters__api-write-token` (the env-var
#     spelling, which loads in every environment), so pass the file via `--env-file`.
#
# Usage:
#   scripts/provision-credentials.sh [--rotate] [--env-file PATH] [--base-url URL] [--skip-apphost]
#     --rotate       regenerate the tokens even if the file already exists
#     --env-file     write to this path (default .context/mimisbrunnr.env)
#     --base-url     the skill-side base URL (default http://localhost:5141)
#     --skip-apphost do not write the AppHost user secrets (e.g. no .NET SDK / gitignored env)
#
# Idempotent: re-running without --rotate reuses the existing tokens and just reprints the export
# lines. Secrets must never be committed — *.env and .context/ are both gitignored.

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
APPHOST_PROJECT="${ROOT_DIR}/src/SmoothAiProductContextMemory.AppHost"
ENV_FILE="${ROOT_DIR}/.context/mimisbrunnr.env"
BASE_URL="http://localhost:5141"
ROTATE=0
WRITE_APPHOST=1

while [[ $# -gt 0 ]]; do
  case "$1" in
    --rotate) ROTATE=1; shift ;;
    --env-file) ENV_FILE="$2"; shift 2 ;;
    --base-url) BASE_URL="$2"; shift 2 ;;
    --skip-apphost) WRITE_APPHOST=0; shift ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

mkdir -p "$(dirname "$ENV_FILE")"

if [[ ! -f "$ENV_FILE" || "$ROTATE" -eq 1 ]]; then
  READ_TOKEN="$(openssl rand -hex 32)"
  WRITE_TOKEN="$(openssl rand -hex 32)"
  if [[ "$READ_TOKEN" == "$WRITE_TOKEN" ]]; then
    echo "unexpected: generated tokens are identical" >&2
    exit 1
  fi

  cat > "$ENV_FILE" <<EOF
# Mímisbrunnr API credentials — generated $(date -u +%Y-%m-%dT%H:%M:%SZ). Not a secret worth
# protecting at rest beyond file permissions, but does not belong in version control.
ApiAccess__ReadToken=${READ_TOKEN}
ApiAccess__WriteToken=${WRITE_TOKEN}
Parameters__api-read-token=${READ_TOKEN}
Parameters__api-write-token=${WRITE_TOKEN}
CONTEXT_MEMORY_READ_TOKEN=${READ_TOKEN}
CONTEXT_MEMORY_WRITE_TOKEN=${WRITE_TOKEN}
CONTEXT_MEMORY_BASE_URL=${BASE_URL}
EOF
  chmod 600 "$ENV_FILE"
  echo "Wrote credentials to ${ENV_FILE}" >&2
else
  echo "Reusing existing credentials in ${ENV_FILE}" >&2
  READ_TOKEN="$(grep -m1 '^ApiAccess__ReadToken=' "$ENV_FILE" | cut -d= -f2-)"
  WRITE_TOKEN="$(grep -m1 '^ApiAccess__WriteToken=' "$ENV_FILE" | cut -d= -f2-)"
fi

if [[ "$WRITE_APPHOST" -eq 1 ]]; then
  if [[ -f "${APPHOST_PROJECT}/SmoothAiProductContextMemory.AppHost.csproj" ]]; then
    dotnet user-secrets set "Parameters:api-read-token" "$READ_TOKEN" --project "$APPHOST_PROJECT" >/dev/null
    dotnet user-secrets set "Parameters:api-write-token" "$WRITE_TOKEN" --project "$APPHOST_PROJECT" >/dev/null
    echo "Wrote AppHost user secrets (Parameters:api-read-token / api-write-token)." >&2
    echo "NOTE: user secrets load in Development only. For the published controller (Production), pass" >&2
    echo "the env file via --env-file (it now carries Parameters__api-read-token / __api-write-token)." >&2
  else
    echo "warning: AppHost project not found at ${APPHOST_PROJECT}; skipping AppHost bridge" >&2
  fi
fi

# Sourceable export lines for the operator's shell: the file already declares the CONTEXT_MEMORY_*
# names as literal assignments, so `set -a` before sourcing exports them for the skills. The container
# path reads the ApiAccess__* names from the same file via --env-file, so one file serves both.
echo "# To export the skill-side credentials, source the file:"
echo "set -a && source ${ENV_FILE} && set +a"
echo "# (The container reads the same file's ApiAccess__* names via --env-file; see docs/wiki/docker.md)"
