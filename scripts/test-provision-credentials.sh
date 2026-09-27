#!/usr/bin/env bash
# Verifies that the provisioned credential files satisfy both parser grammars without leaking a token.
#   - `set -a && source` of the sourceable file must print nothing (no token as "command not found").
#   - CONTEXT_MEMORY_* must be exported by that source.
#   - The sourceable file must carry NO Parameters__* line.
#   - The controller env file must carry the Parameters__* names for `docker run --env-file`.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT

"$repo_root/scripts/provision-credentials.sh" \
  --env-file "$scratch/creds.env" \
  --skip-apphost >/dev/null

read_token="$(grep -m1 '^ApiAccess__ReadToken=' "$scratch/creds.env" | cut -d= -f2-)"
write_token="$(grep -m1 '^ApiAccess__WriteToken=' "$scratch/creds.env" | cut -d= -f2-)"
[ -n "$read_token" ] && [ -n "$write_token" ] || { echo "FAIL: expected non-blank tokens" >&2; exit 1; }

stderr_file="$scratch/source-stderr"
set +e
(
  set -a
  source "$scratch/creds.env"
  set +a
  [ -n "${CONTEXT_MEMORY_READ_TOKEN:-}" ] || { echo "FAIL: CONTEXT_MEMORY_READ_TOKEN not exported" >&2; exit 1; }
  [ -n "${CONTEXT_MEMORY_WRITE_TOKEN:-}" ] || { echo "FAIL: CONTEXT_MEMORY_WRITE_TOKEN not exported" >&2; exit 1; }
  [ -n "${CONTEXT_MEMORY_BASE_URL:-}" ] || { echo "FAIL: CONTEXT_MEMORY_BASE_URL not exported" >&2; exit 1; }
) 2>"$stderr_file"
source_status=$?
set -e

[ "$source_status" -eq 0 ] || { echo "FAIL: sourcing the env file failed" >&2; cat "$stderr_file" >&2; exit 1; }

if [ -s "$stderr_file" ]; then
  echo "FAIL: sourcing the env file produced output on stderr (a token may have leaked):" >&2
  cat "$stderr_file" >&2
  exit 1
fi

if grep -q "$read_token" "$stderr_file" || grep -q "$write_token" "$stderr_file"; then
  echo "FAIL: a token value appeared on stderr while sourcing" >&2
  exit 1
fi

if grep -q '^Parameters__' "$scratch/creds.env"; then
  echo "FAIL: the sourceable env file carries a Parameters__* line" >&2
  exit 1
fi

grep -q '^Parameters__api-read-token=' "$scratch/creds.env.controller" \
  || { echo "FAIL: controller env file lacks Parameters__api-read-token" >&2; exit 1; }
grep -q '^Parameters__api-write-token=' "$scratch/creds.env.controller" \
  || { echo "FAIL: controller env file lacks Parameters__api-write-token" >&2; exit 1; }

echo "provision-credentials.sh security checks passed."
