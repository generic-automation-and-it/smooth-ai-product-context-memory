#!/usr/bin/env bash
# One-command API credential provisioning for a local Mímisbrunnr deployment.
#
# Writes two distinct random Bearer tokens to a gitignored env file that BOTH the service and the
# host-side skills read. One file, two name forms, one source of truth:
#   - ApiAccess__ReadToken / ApiAccess__WriteToken   (what the Host's authorizer reads)
#   - CONTEXT_MEMORY_READ_TOKEN / CONTEXT_MEMORY_WRITE_TOKEN / CONTEXT_MEMORY_BASE_URL
#                                                   (what the skills read)
# The values are identical across the two name forms; only the env var names differ. That mapping is
# what removes the manual dashboard-copy step from the first-run path.
#
# The tokens are runtime configuration only: the Host hashes them at startup and never writes them to
# the store, so regenerating them invalidates no data. Deleting the file and re-running is a complete
# recovery procedure. See docs/wiki/setup.md.
#
# Usage:
#   scripts/provision-credentials.sh [--rotate] [--env-file PATH] [--base-url URL]
#     --rotate     regenerate the tokens even if the file already exists
#     --env-file   write to this path (default .context/mimisbrunnr.env)
#     --base-url   the skill-side base URL (default http://localhost:5141)
#
# Idempotent: re-running without --rotate reuses the existing tokens and just reprints the export
# lines. Secrets must never be committed — *.env and .context/ are both gitignored.

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ENV_FILE="${ROOT_DIR}/.context/mimisbrunnr.env"
BASE_URL="http://localhost:5141"
ROTATE=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --rotate) ROTATE=1; shift ;;
    --env-file) ENV_FILE="$2"; shift 2 ;;
    --base-url) BASE_URL="$2"; shift 2 ;;
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
CONTEXT_MEMORY_READ_TOKEN=${READ_TOKEN}
CONTEXT_MEMORY_WRITE_TOKEN=${WRITE_TOKEN}
CONTEXT_MEMORY_BASE_URL=${BASE_URL}
EOF
  chmod 600 "$ENV_FILE"
  echo "Wrote credentials to ${ENV_FILE}" >&2
else
  echo "Reusing existing credentials in ${ENV_FILE}" >&2
fi

# Sourceable export lines for the operator's shell: the file already declares the CONTEXT_MEMORY_*
# names as literal assignments, so `set -a` before sourcing exports them for the skills. The container
# path reads the ApiAccess__* names from the same file via --env-file, so one file serves both.
echo "# To export the skill-side credentials, source the file:"
echo "set -a && source ${ENV_FILE} && set +a"
echo "# (The container reads the same file's ApiAccess__* names via --env-file; see docs/wiki/docker.md)"
