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

# --- reuse and migration paths ------------------------------------------------------------------
# A plain re-run must reuse the existing values: the Host and the skills already hold them, so
# regenerating here would silently invalidate both and 403 every request until a restart.
reused="$(bash "$repo_root/scripts/provision-credentials.sh" --env-file "$scratch/creds.env" --skip-apphost 2>&1)"
grep -q 'Reusing existing credentials' <<<"$reused" \
  || { echo "FAIL: a plain re-run did not report reusing the existing credentials" >&2; echo "$reused" >&2; exit 1; }
after_reuse="$(grep -m1 '^ApiAccess__ReadToken=' "$scratch/creds.env" | cut -d= -f2-)"
[ "$after_reuse" = "$read_token" ] \
  || { echo "FAIL: a plain re-run rotated the read token" >&2; exit 1; }

# --rotate must regenerate.
bash "$repo_root/scripts/provision-credentials.sh" --env-file "$scratch/creds.env" --skip-apphost --rotate >/dev/null
after_rotate="$(grep -m1 '^ApiAccess__ReadToken=' "$scratch/creds.env" | cut -d= -f2-)"
[ "$after_rotate" != "$read_token" ] \
  || { echo "FAIL: --rotate did not regenerate the read token" >&2; exit 1; }

# Migration from the pre-split single file: the pair must be re-split AND keep the token values.
# Drop the controller sibling and fold the Parameters__* names back into the sourceable file, which is
# the exact state an operator upgrading from the earlier layout is in.
legacy="$scratch/legacy.env"
rm -f "$scratch/creds.env.controller"
cat >"$legacy" <<EOF
ApiAccess__ReadToken=$(grep -m1 '^ApiAccess__ReadToken=' "$scratch/creds.env" | cut -d= -f2-)
ApiAccess__WriteToken=$(grep -m1 '^ApiAccess__WriteToken=' "$scratch/creds.env" | cut -d= -f2-)
Parameters__api-read-token=$(grep -m1 '^ApiAccess__ReadToken=' "$scratch/creds.env" | cut -d= -f2-)
Parameters__api-write-token=$(grep -m1 '^ApiAccess__WriteToken=' "$scratch/creds.env" | cut -d= -f2-)
CONTEXT_MEMORY_READ_TOKEN=$(grep -m1 '^ApiAccess__ReadToken=' "$scratch/creds.env" | cut -d= -f2-)
CONTEXT_MEMORY_WRITE_TOKEN=$(grep -m1 '^ApiAccess__WriteToken=' "$scratch/creds.env" | cut -d= -f2-)
CONTEXT_MEMORY_BASE_URL=http://localhost:5141
EOF
legacy_read="$(grep -m1 '^ApiAccess__ReadToken=' "$legacy" | cut -d= -f2-)"

migrated="$(bash "$repo_root/scripts/provision-credentials.sh" --env-file "$legacy" --skip-apphost 2>&1)"
grep -q 'Re-splitting' <<<"$migrated" \
  || { echo "FAIL: a pre-split file was not re-split" >&2; echo "$migrated" >&2; exit 1; }
[ -f "$legacy.controller" ] \
  || { echo "FAIL: re-splitting did not create the controller env file" >&2; exit 1; }
if grep -q '^Parameters__' "$legacy"; then
  echo "FAIL: re-splitting left a Parameters__* line in the sourceable file" >&2
  exit 1
fi
after_split="$(grep -m1 '^ApiAccess__ReadToken=' "$legacy" | cut -d= -f2-)"
[ "$after_split" = "$legacy_read" ] \
  || { echo "FAIL: re-splitting rotated the token instead of carrying it forward" >&2; exit 1; }
grep -q "^Parameters__api-read-token=$legacy_read" "$legacy.controller" \
  || { echo "FAIL: the re-split controller file does not carry the preserved token" >&2; exit 1; }

echo "provision-credentials.sh security and reuse checks passed."

# The sibling credential-guard harness (test-opencode-credential-guard.sh) is NOT run here: the guard
# uses `declare -A`, which needs bash 4.0, and this script must stay runnable on the macOS default
# bash 3.2 where the harness exits 2 on an associative-array error. CI is ubuntu (bash 5) and runs it.
