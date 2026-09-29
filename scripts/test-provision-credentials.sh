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

# `stat` for a file's permission bits is spelled two incompatible ways: GNU uses `-c '%a'`, BSD/macOS
# uses `-f '%Lp'`, and the two do not fail the same way. The wrong flag on GNU does not merely error —
# `stat -f` there means *filesystem status*, so it prints a filesystem report **to stdout** and exits
# non-zero. An expression like
#     stat -f '%Lp' "$f" 2>/dev/null || stat -c '%a' "$f"
# therefore captures that report and has the fallback's answer *appended* to it, yielding something
# like `  File: "f" ... 600` — never equal to `600`, so the mode assertion below fails on every Linux
# run. `2>/dev/null` hides the diagnostic but not the stdout. This harness is CI-gated on ubuntu, so
# it was red on the gate while passing on the author's macOS.
#
# Probed once into a function rather than expressed as a fallback, for two reasons: the probe sends
# both streams to /dev/null so no diagnostic can be captured by a caller, and a function has no second
# command to append to. Ordering the flags "correctly" would only move the trap to the other platform.
if stat -c '%a' /dev/null >/dev/null 2>&1; then
  # GNU coreutils.
  file_mode() { stat -c '%a' "$1"; }
else
  # BSD / macOS.
  file_mode() { stat -f '%Lp' "$1"; }
fi

# The scratch dir is outside the checkout, so the unignored-path refusal below would otherwise fire
# on every call here. The refusal has its own test below.
provision() {
  "$repo_root/scripts/provision-credentials.sh" --allow-unignored-env-file "$@"
}

provision --env-file "$scratch/creds.env" --skip-apphost >/dev/null

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
reused="$(provision --env-file "$scratch/creds.env" --skip-apphost 2>&1)"
grep -q 'Reusing existing credentials' <<<"$reused" \
  || { echo "FAIL: a plain re-run did not report reusing the existing credentials" >&2; echo "$reused" >&2; exit 1; }
after_reuse="$(grep -m1 '^ApiAccess__ReadToken=' "$scratch/creds.env" | cut -d= -f2-)"
[ "$after_reuse" = "$read_token" ] \
  || { echo "FAIL: a plain re-run rotated the read token" >&2; exit 1; }

# --rotate must regenerate.
provision --env-file "$scratch/creds.env" --skip-apphost --rotate >/dev/null
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

migrated="$(provision --env-file "$legacy" --skip-apphost 2>&1)"
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

# --- least privilege in the controller file -------------------------------------------------------
# The controller reads two keys. Every other name in that file is a second copy of a bearer
# credential in a file whose only consumer ignores them. Uses its own file: the migration case above
# deliberately removes the original's controller sibling to simulate the pre-split layout.
narrow="$scratch/narrow"
mkdir -p "$narrow"
provision --env-file "$narrow/creds.env" --skip-apphost >/dev/null
if grep -qE '^(ApiAccess__|CONTEXT_MEMORY_)' "$narrow/creds.env.controller"; then
  echo "FAIL: the controller env file carries names beyond Parameters__*" >&2
  exit 1
fi
grep -q '^Parameters__api-read-token=' "$narrow/creds.env.controller" \
  || { echo "FAIL: the controller env file lost its read token" >&2; exit 1; }
grep -q '^#' "$narrow/creds.env.controller" \
  || { echo "FAIL: the controller env file lost its explanatory header" >&2; exit 1; }

# --- an unignored --env-file is refused -----------------------------------------------------------
# The token file is only safe from version control while something ignores it. A caller-chosen path
# outside .context/ and not ending in .env is covered by neither `*.env` nor `.context/`, and
# setup.md asserts it is gitignored regardless of where it points.
unignored_out="$scratch/unignored-report"
unignored_name="not-an-env-file"
unignored_path="$scratch/$unignored_name"
# Three outcomes, not two. `check-ignore` exits non-zero both for "not ignored" and for "cannot
# answer" — the latter happens when git cannot resolve the repository at all (a read-only mount of a
# linked worktree, whose .git is a file pointing outside the mount, is the case that surfaced this).
# Collapsing them makes the harness report a product failure for an environment it cannot evaluate,
# which is the same class of defect as the `stat` bug this section sits next to: a check that reads as
# an assertion but is not one here.
git_usable=0
if git -C "$repo_root" rev-parse --git-dir >/dev/null 2>&1; then
  git_usable=1
fi

if [ "$git_usable" -eq 0 ]; then
  echo "SKIP: git cannot resolve a repository at $repo_root, so the ignore-refusal cannot be evaluated" >&2
elif git -C "$repo_root" check-ignore -q "$unignored_path" 2>/dev/null; then
  echo "SKIP: $unignored_path is unexpectedly ignored by this checkout" >&2
else
  if (cd "$repo_root" && scripts/provision-credentials.sh \
        --env-file "$unignored_path" --skip-apphost) >"$unignored_out" 2>&1; then
    echo "FAIL: wrote credentials to a path git does not ignore" >&2
    exit 1
  fi
  grep -q 'git does not ignore' "$unignored_out" \
    || { echo "FAIL: the refusal did not explain itself:" >&2; cat "$unignored_out" >&2; exit 1; }
  [ ! -e "$unignored_path" ] \
    || { echo "FAIL: a token file was created despite the refusal" >&2; exit 1; }
  # The override must actually work, or the refusal is the only outcome an operator can reach.
  provision --env-file "$unignored_path" --skip-apphost >/dev/null
  grep -q '^ApiAccess__ReadToken=' "$unignored_path" \
    || { echo "FAIL: --allow-unignored-env-file did not write the file" >&2; exit 1; }
fi

# --- files are never group- or world-readable, even under a permissive umask ---------------------
# Two separate claims, and only one of them is observable here.
#
# The end state is observable: run under umask 022 — the common default, under which an un-narrowed
# create is 644 — and assert the resulting mode.
permissive="$scratch/permissive"
mkdir -p "$permissive"
(umask 022 && provision --env-file "$permissive/creds.env" --skip-apphost >/dev/null)
for created in "$permissive/creds.env" "$permissive/creds.env.controller"; do
  mode="$(file_mode "$created")"
  case "$mode" in
    600|400) ;;
    *) echo "FAIL: $created has mode $mode; a bearer-token file must be owner-only" >&2; exit 1 ;;
  esac
done
#
# The *window* is not observable, and this test does not claim to cover it. `chmod 600` after the
# write reaches the same end state whether or not the file was briefly 644, so no post-hoc assertion
# can tell the two apart — the exposure is over before any test could look. What is assertable is
# that the script narrows the umask itself, so the file is never created wide. That is a structural
# check on the script, not a behavioural one, and it is labelled as such rather than dressed up as
# evidence about the window itself.
grep -Eq '^umask 0?77$' "$repo_root/scripts/provision-credentials.sh" \
  || { echo "FAIL: the script does not narrow its umask, so token files are briefly created" >&2
       echo "  group/world-readable before chmod 600. That window is not observable by any" >&2
       echo "  assertion below and is asserted structurally only." >&2; exit 1; }

# --- a corrupt reuse file is refused, not propagated ----------------------------------------------
# Reuse that does not re-validate its parse rewrites a truncated file *keeping the broken value*,
# then propagates that value into the controller file and the user secrets.
corrupt="$scratch/corrupt.env"
sed 's/^ApiAccess__ReadToken=.*/ApiAccess__ReadToken=deadbeef/' "$permissive/creds.env" >"$corrupt"
corrupt_out="$scratch/corrupt-report"
if provision --env-file "$corrupt" --skip-apphost >"$corrupt_out" 2>&1; then
  echo "FAIL: reused a file whose read token is not a 64-char hex token" >&2
  exit 1
fi
grep -q 'not a 64-character hex token' "$corrupt_out" \
  || { echo "FAIL: the corrupt-file refusal did not explain itself:" >&2; cat "$corrupt_out" >&2; exit 1; }
grep -q 'deadbeef' "$corrupt.controller" 2>/dev/null \
  && { echo "FAIL: the corrupt value was propagated into the controller file" >&2; exit 1; }

# Identical read and write tokens mean the read capability is the write capability.
identical="$scratch/identical.env"
sed "s/^ApiAccess__WriteToken=.*/ApiAccess__WriteToken=$(grep -m1 '^ApiAccess__ReadToken=' "$permissive/creds.env" | cut -d= -f2-)/" \
  "$permissive/creds.env" >"$identical"
if provision --env-file "$identical" --skip-apphost >"$scratch/identical-report" 2>&1; then
  echo "FAIL: reused a file whose read and write tokens are identical" >&2
  exit 1
fi
grep -q 'identical' "$scratch/identical-report" \
  || { echo "FAIL: the identical-token refusal did not explain itself" >&2; exit 1; }

# --- AppHost user secrets carry the token without it ever being an argv element -------------------
# `dotnet user-secrets set NAME VALUE` puts the value in the process table. Point HOME at a scratch
# dir so the real user-secrets store is untouched, and assert the file the SDK reads.
secrets_home="$scratch/secrets-home"
mkdir -p "$secrets_home"
HOME="$secrets_home" provision --env-file "$scratch/apphost.env" >/dev/null
secrets_json="$secrets_home/.microsoft/usersecrets/smooth-project-memory-host/secrets.json"
[ -f "$secrets_json" ] \
  || { echo "FAIL: AppHost user secrets were not written to $secrets_json" >&2; exit 1; }
apphost_read="$(grep -m1 '^ApiAccess__ReadToken=' "$scratch/apphost.env" | cut -d= -f2-)"
apphost_write="$(grep -m1 '^ApiAccess__WriteToken=' "$scratch/apphost.env" | cut -d= -f2-)"
python3 - "$secrets_json" "$apphost_read" "$apphost_write" <<'PY'
import json, sys
data = json.load(open(sys.argv[1], encoding="utf-8-sig"))
assert data["Parameters:api-read-token"] == sys.argv[2], "read token not in user secrets"
assert data["Parameters:api-write-token"] == sys.argv[3], "write token not in user secrets"
PY
secrets_mode="$(file_mode "$secrets_json")"
case "$secrets_mode" in
  600|400) ;;
  *) echo "FAIL: user secrets file has mode $secrets_mode" >&2; exit 1 ;;
esac

echo "provision-credentials.sh security and reuse checks passed."

# The sibling credential-guard harness (test-opencode-credential-guard.sh) runs on the same bash
# 3.2 as this script: the guard no longer uses `declare -A`, so it is runnable on macOS too and the
# two harnesses can be run back to back locally.
