#!/bin/bash
# Launch a Mímisbrunnr MCP server with its credential in the process environment.
#
# Why this exists: an MCP server inherits the environment of whatever launched it, and a token on disk
# is not a token in a process. `.mcp.json` pointed straight at `python3 <server>.py`, so
# CONTEXT_MEMORY_READ_TOKEN was never injected and every call failed closed with
# `HTTP 0 missing-credential` — a precise error that named the symptom rather than the cause, and cost
# several session restarts to trace back to a missing `env` block.
#
# The token is read from the file at launch and never appears in argv, so it stays out of the process
# table and out of version control. This file contains no secret; the credential file it reads is
# gitignored and mode 600.
#
# Usage: launch-mcp.sh read | write
set -euo pipefail

role="${1:-}"
case "$role" in
  read | write) ;;
  *) echo "launch-mcp.sh: expected 'read' or 'write', got '${role}'" >&2; exit 2 ;;
esac

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
credential_file="${MIMIS_CREDENTIAL_FILE:-$repo_root/.context/mimisbrunnr.env}"

if [ ! -f "$credential_file" ]; then
  echo "launch-mcp.sh: no credential file at $credential_file" >&2
  echo "               run scripts/provision-credentials.sh first, then start the stack with scripts/run.sh" >&2
  exit 1
fi

# Both tokens live in that file. memory_read_mcp.py pops the write token from its own environment at
# startup, so exporting both here cannot widen the read surface - that check is the read surface's
# by-construction guarantee, not a convention this script has to reproduce.
set -a
# shellcheck disable=SC1090
source "$credential_file"
set +a

token_name="CONTEXT_MEMORY_READ_TOKEN"
[ "$role" = write ] && token_name="CONTEXT_MEMORY_WRITE_TOKEN"
if [ -z "${!token_name:-}" ]; then
  echo "launch-mcp.sh: $token_name is absent from $credential_file" >&2
  echo "               re-run scripts/provision-credentials.sh (a plain re-run reuses, it does not rotate)" >&2
  exit 1
fi

exec python3 "$repo_root/.agents/skills/mimisbrunnr-odin-context-memory/scripts/memory_${role}_mcp.py"