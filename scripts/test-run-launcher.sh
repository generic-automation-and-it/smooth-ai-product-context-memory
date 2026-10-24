#!/usr/bin/env bash
# Harness for the operator controller launchers (`run.sh`).
#
# Scope: everything that does not need a container engine. It never starts, stops or pulls anything, and
# it never reads the operator's real credentials or shell profile — every case runs in a cleared
# environment against a scratch MIMIS_HOME, HOME and profile, so a red run cannot leave a token file
# behind, rotate a credential a running Host holds, or add a line to the operator's ~/.zshrc.
#
# `run.ps1` has no equivalent here. Its credential path is the same algorithm as this one, and the
# Windows-specific parts (named-pipe socket, ACL inheritance, the `-v` label branch) cannot be executed
# on a macOS or Linux runner at all. Those remain parse-verified only, and the gap is recorded in the
# PR's Skip Areas rather than pretended over here.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
launcher="$repo_root/scripts/run.sh"
scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

passed=0
failed=0

# ---------------------------------------------------------------------------------------------
# helpers
# ---------------------------------------------------------------------------------------------

# GNU and BSD spell a file's mode differently, and on GNU the BSD flag does not simply fail: `stat -f`
# means *filesystem status* and prints a report to stdout while exiting non-zero, so a
# `stat -f … || stat -c …` fallback captures that report with the answer appended. Probe once, sending
# both streams to /dev/null so no diagnostic can be captured.
file_mode() {
  if stat -c '%a' "$1" >/dev/null 2>&1; then
    stat -c '%a' "$1"
  else
    stat -f '%Lp' "$1"
  fi
}

ok() { passed=$((passed + 1)); echo "  ok   $1"; }
# Neither pass nor fail, with the reason stated: a precondition this user cannot create makes a case
# untestable here, which is not the same as the property being false, and counting it either way would
# put a wrong number in the summary.
skip() { echo "  skip $1"; [ -n "${2:-}" ] && echo "       $2"; return 0; }
no() {
  failed=$((failed + 1))
  echo "  FAIL $1"
  [ -n "${2:-}" ] && echo "       $2"
  return 0
}

# Portable in-place substitution, because `sed -i` is not one command.
#
# BSD sed requires an explicit empty suffix (`-i ''`) and GNU sed attaches the suffix to the flag
# (`-i.bak`), so on GNU the quoted empty string is taken as the *script* and the real expression is
# taken as a filename: `sed: can't read s/…/: No such file or directory`. This harness is a PR-gate
# step and the gate runs on ubuntu, so a BSD-only spelling is red on the platform that actually gates
# it while green on the developer's machine. Write beside the file and rename, which is atomic and
# behaves identically on both.
sed_inplace() {
  sed_tmp="$(mktemp "${1}.XXXXXX")" || { no "sed_inplace could not create a temp file" ; return 1; }
  if ! sed "$2" "$1" >"$sed_tmp"; then
    rm -f "$sed_tmp"
    no "sed_inplace: sed failed on $1"
    return 1
  fi
  mv "$sed_tmp" "$1"
}

# Run the launcher against a scratch home, in a cleared environment.
#
# `env -i`, not a list of unsets. The launcher now takes CONTEXT_MEMORY_BASE_URL and both tokens as the
# master, reads the decision settings from the environment, and writes the shell profile — so a case
# that inherited any CONTEXT_MEMORY_* name from the operator's shell would exercise the operator's
# configuration, and one that inherited HOME would add a block to their real ~/.zshrc. An unset list has
# to name every such variable and silently misses the next one; a cleared environment cannot.
# MIMIS_MACHINE_CREDENTIALS and MIMIS_PROFILE are pinned as well, because the launcher derives both from
# the home and a harness that redirected only MIMIS_HOME once wrote to the operator's real ~/.mimisbrunnr.
#
# Leading NAME=value arguments are added to that environment, so a case states every variable it depends
# on at the call site. The home is a separate entry point rather than an optional leading argument:
# `run_launcher env` is how every other case calls it, and a leading argument would read the verb `env`
# as a directory — which fails late, as a usage message, in cases that have nothing to do with homes.
run_launcher_in() {
  local home="$1"
  shift
  local -a extra=()
  while [ $# -gt 0 ]; do
    case "$1" in
      *=*) extra+=("$1"); shift ;;
      *) break ;;
    esac
  done
  env -i PATH="$PATH" TMPDIR="${TMPDIR:-/tmp}" HOME="$home/fake-home" SHELL=/bin/zsh     MIMIS_HOME="$home" MIMIS_MACHINE_CREDENTIALS="$home/credentials" MIMIS_PROFILE="$home/.zshrc"     ${extra[@]+"${extra[@]}"} "$launcher" "$@"
}

# How many profile backups exist beside a profile. A glob loop rather than `ls | wc -l`: `ls` on a
# pattern with no match exits non-zero, and under `pipefail` that aborts the harness mid-run.
count_backups() {
  local n=0 f
  for f in "$1"-mimisbrunnr-*; do
    [ -e "$f" ] && n=$((n + 1))
  done
  echo "$n"
}

# The read token a POSIX profile block exports, quotes removed.
profile_read_token() {
  sed -n "s/^export CONTEXT_MEMORY_READ_TOKEN='\(.*\)'\$/\1/p" "$1"
}

run_launcher() { run_launcher_in "$scratch/home" "$@"; }

expect_ok() {
  local name="$1"
  shift
  if run_launcher "$@" >"$scratch/out" 2>"$scratch/err"; then
    ok "$name"
  else
    no "$name" "exit $? — $(tail -1 "$scratch/err")"
  fi
}

expect_fail() {
  local name="$1" needle="$2"
  shift 2
  if run_launcher "$@" >"$scratch/out" 2>"$scratch/err"; then
    no "$name" "expected failure, got exit 0"
  elif grep -qF "$needle" "$scratch/out" "$scratch/err"; then
    ok "$name"
  else
    no "$name" "failed, but without naming '$needle': $(tail -2 "$scratch/err" | tr '\n' ' ')"
  fi
}

echo "run.sh harness — scratch home $scratch/home"

# ---------------------------------------------------------------------------------------------
# argument validation, before any resource name is built from it
# ---------------------------------------------------------------------------------------------

# An invalid id must be refused before any resource name is built from it. MIMIS_ID is passed through
# the environment rather than as an argument, because the launcher reads it only from the environment —
# `env MIMIS_ID=… env …` would re-exec `env` without forwarding the assignment and silently test nothing.
expect_fail_id() {
  local name="$1" needle="$2" id="$3"
  if run_launcher MIMIS_ID="$id" env >"$scratch/out" 2>"$scratch/err"; then
    no "$name" "expected failure, got exit 0"
  elif grep -qF "$needle" "$scratch/out" "$scratch/err"; then
    ok "$name"
  else
    no "$name" "failed without naming '$needle': $(tail -1 "$scratch/err")"
  fi
}

expect_fail_id "rejects an uppercase installation id" "must start with a lowercase letter or digit" "Bad"
expect_fail_id "rejects an installation id with a space" "must start with a lowercase letter or digit" "bad id"
expect_fail_id "rejects a leading-hyphen installation id" "must start with a lowercase letter or digit" "-lead"
expect_fail_id "rejects an over-long installation id" "at most 32 characters" "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
expect_fail "rejects an unknown verb" "unknown verb" nonsense

# ---------------------------------------------------------------------------------------------
# credentials
# ---------------------------------------------------------------------------------------------

expect_ok "creates the credential file on first run" env
credential_file="$scratch/home/controller.env"

if [ -f "$credential_file" ]; then
  ok "credential file exists"
else
  no "credential file exists" "missing $credential_file"
fi

if [ "$(file_mode "$credential_file")" = "600" ]; then
  ok "credential file is mode 600"
else
  no "credential file is mode 600" "mode is $(file_mode "$credential_file")"
fi

for key in PostgresConfiguration__Password BlobConfiguration__AccessKey BlobConfiguration__SecretKey \
  Parameters__api-read-token Parameters__api-write-token; do
  if grep -q "^$key=.\+" "$credential_file"; then
    ok "credential file carries $key"
  else
    no "credential file carries $key" "absent or empty"
  fi
done

if [ "$(sed -n 's/^Parameters__api-read-token=//p' "$credential_file")" \
  = "$(sed -n 's/^Parameters__api-write-token=//p' "$credential_file")" ]; then
  no "read and write tokens differ" "they are identical"
else
  ok "read and write tokens differ"
fi

# Idempotence: a plain re-run must reuse, never rotate. Rotating 403s every client holding the old
# value, so this is the single most damaging thing the launcher could get wrong.
before="$(cksum <"$credential_file")"
run_launcher env >/dev/null 2>&1
after="$(cksum <"$credential_file")"
if [ "$before" = "$after" ]; then
  ok "a plain re-run reuses the credentials unchanged"
else
  no "a plain re-run reuses the credentials unchanged" "the file was rewritten"
fi

# Re-assert the mode on the reuse path — reuse is the quiet path, where nobody is watching.
chmod 644 "$credential_file"
run_launcher env >/dev/null 2>&1
if [ "$(file_mode "$credential_file")" = "600" ]; then
  ok "mode 600 is re-asserted on reuse"
else
  no "mode 600 is re-asserted on reuse" "stayed $(file_mode "$credential_file")"
fi

# A supplied value wins for the run and is never written to disk. Writing a secret the operator did
# not ask to store is the surprise; the omission is not.
supplied="0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
run_launcher "Parameters__api-write-token=$supplied" env >/dev/null 2>&1
if grep -qF "$supplied" "$credential_file"; then
  no "a supplied secret is not written to disk" "it was persisted"
else
  ok "a supplied secret is not written to disk"
fi

# An env-sourced value must not cause the stored one to be dropped. An earlier draft persisted only the
# values the current run selected, so supplying one variable silently dropped the stored token for it —
# and the next run without that variable minted a different one, 403ing a Host already running.
stored_read="$(sed -n 's/^Parameters__api-read-token=//p' "$credential_file")"
run_launcher "Parameters__api-read-token=$supplied" env >/dev/null 2>&1
if [ "$(sed -n 's/^Parameters__api-read-token=//p' "$credential_file")" = "$stored_read" ]; then
  ok "an env-sourced value does not displace the stored one"
else
  no "an env-sourced value does not displace the stored one" "the stored token changed"
fi

# A symlink at the credential path must be refused, not written through. `[ -f ]` follows a symlink, so
# a plain "is it a regular file" test passes a link to an ordinary file.
symlink_dir="$scratch/symlink"
mkdir -p "$symlink_dir"
printf 'target\n' >"$symlink_dir/target"
ln -s "$symlink_dir/target" "$symlink_dir/controller.env"
if run_launcher_in "$symlink_dir" env >"$scratch/out" 2>"$scratch/err"; then
  no "refuses a symlinked credential file" "it wrote through the link"
elif grep -q "symlink" "$scratch/err"; then
  ok "refuses a symlinked credential file"
else
  no "refuses a symlinked credential file" "$(tail -1 "$scratch/err")"
fi
if [ "$(cat "$symlink_dir/target")" = "target" ]; then
  ok "the symlink target was not written through"
else
  no "the symlink target was not written through" "target was modified"
fi

# ---------------------------------------------------------------------------------------------
# the shell profile: every missing client setting is added, nothing present is touched
# ---------------------------------------------------------------------------------------------

# The run above (`env`, against the scratch home) published the client environment into the scratch
# profile `run_launcher_in` names. These cases read what landed there.
home_profile="$scratch/home/.zshrc"

for key in CONTEXT_MEMORY_BASE_URL CONTEXT_MEMORY_READ_TOKEN CONTEXT_MEMORY_DECISIONS_ENABLED \
  CONTEXT_MEMORY_DECISIONS_BASE_URL CONTEXT_MEMORY_DECISIONS_MODEL CONTEXT_MEMORY_DECISIONS_TIMEOUT; do
  if [ "$(grep -c "^export $key=" "$home_profile" 2>/dev/null)" = "1" ]; then
    ok "the profile exports $key exactly once"
  else
    no "the profile exports $key exactly once" "count $(grep -c "^export $key=" "$home_profile" 2>/dev/null)"
  fi
done

# The write token is the one client credential that must never be exported: it would reach every
# process the shell starts, and the read client refuses to run beside one.
if grep -q 'CONTEXT_MEMORY_WRITE_TOKEN' "$home_profile"; then
  no "the profile never exports the write token" "it does"
else
  ok "the profile never exports the write token"
fi

# The decision API key is the operator's own secret, never a launcher default.
if grep -q 'CONTEXT_MEMORY_DECISIONS_API_KEY' "$home_profile"; then
  no "the profile never carries the decision API key" "it does"
else
  ok "the profile never carries the decision API key"
fi

# The gate is OFF by default: a launcher that shipped ENABLED=true would score every export on a
# machine with no decision model.
if grep -q "^export CONTEXT_MEMORY_DECISIONS_ENABLED='false'$" "$home_profile"; then
  ok "the decision gate defaults to disabled"
else
  no "the decision gate defaults to disabled" "$(grep DECISIONS_ENABLED "$home_profile")"
fi

# The read token in the profile is the one the Host is started with — a mismatch is a 403 on every read.
if [ "$(profile_read_token "$home_profile")" = "$(sed -n 's/^Parameters__api-read-token=//p' "$credential_file")" ]; then
  ok "the profile's read token is the one the controller holds"
else
  no "the profile's read token is the one the controller holds" "they differ"
fi

if [ "$(file_mode "$home_profile")" = "600" ]; then
  ok "the profile is written mode 600"
else
  no "the profile is written mode 600" "mode is $(file_mode "$home_profile")"
fi

# Idempotence: with nothing missing, a re-run leaves the profile byte-identical and takes no backup.
before="$(cksum <"$home_profile")"
backups_before="$(count_backups "$home_profile")"
run_launcher env >/dev/null 2>&1
if [ "$before" = "$(cksum <"$home_profile")" ] &&
  [ "$backups_before" = "$(count_backups "$home_profile")" ]; then
  ok "a re-run with nothing missing leaves the profile untouched"
else
  no "a re-run with nothing missing leaves the profile untouched" "the profile changed or was backed up"
fi

# The environment is the master: an operator's hand-tuned value inside the block survives every run.
sed_inplace "$home_profile" "s|^export CONTEXT_MEMORY_DECISIONS_MODEL=.*|export CONTEXT_MEMORY_DECISIONS_MODEL='openjev'|"
sed_inplace "$home_profile" '/^export CONTEXT_MEMORY_DECISIONS_TIMEOUT=/d'
run_launcher env >/dev/null 2>&1
if grep -q "^export CONTEXT_MEMORY_DECISIONS_MODEL='openjev'$" "$home_profile"; then
  ok "a re-run preserves an operator's edited value"
else
  no "a re-run preserves an operator's edited value" "$(grep DECISIONS_MODEL "$home_profile")"
fi
if [ "$(grep -c '^export CONTEXT_MEMORY_DECISIONS_TIMEOUT=' "$home_profile")" = "1" ]; then
  ok "a re-run adds back a setting that went missing"
else
  no "a re-run adds back a setting that went missing" "count $(grep -c '^export CONTEXT_MEMORY_DECISIONS_TIMEOUT=' "$home_profile")"
fi

# A setting the operator already has in the environment is never added: the environment is the master,
# and a second assignment in the profile would override it in every new shell.
env_home="$scratch/home-env-set"
mkdir -p "$env_home"
run_launcher_in "$env_home" CONTEXT_MEMORY_DECISIONS_ENABLED=true env >/dev/null 2>&1
if grep -q 'CONTEXT_MEMORY_DECISIONS_ENABLED' "$env_home/.zshrc"; then
  no "a setting already in the environment is not added to the profile" "it was added"
else
  ok "a setting already in the environment is not added to the profile"
fi

# …and one the operator assigned *outside* the managed block is theirs, not a gap to fill.
own_home="$scratch/home-own-line"
mkdir -p "$own_home"
printf '# mine\nexport CONTEXT_MEMORY_DECISIONS_MODEL=mine\n' >"$own_home/.zshrc"
run_launcher_in "$own_home" env >/dev/null 2>&1
if [ "$(grep -c 'CONTEXT_MEMORY_DECISIONS_MODEL' "$own_home/.zshrc")" = "1" ] &&
  grep -q '^export CONTEXT_MEMORY_DECISIONS_MODEL=mine$' "$own_home/.zshrc"; then
  ok "an assignment outside the managed block is respected, not duplicated"
else
  no "an assignment outside the managed block is respected, not duplicated" "$(grep DECISIONS_MODEL "$own_home/.zshrc" | tr '\n' ' ')"
fi
if grep -q '^# mine$' "$own_home/.zshrc"; then
  ok "the profile's own content is preserved"
else
  no "the profile's own content is preserved" "it was lost"
fi

# MIMIS_PROFILE=off leaves every profile alone.
off_home="$scratch/home-profile-off"
mkdir -p "$off_home"
if run_launcher_in "$off_home" MIMIS_PROFILE=off env >/dev/null 2>&1 && [ ! -e "$off_home/.zshrc" ] &&
  [ ! -e "$off_home/off" ]; then
  ok "MIMIS_PROFILE=off writes no profile"
else
  no "MIMIS_PROFILE=off writes no profile" "a profile was written"
fi

# ---------------------------------------------------------------------------------------------
# the client environment is the master for the base URL and the tokens
# ---------------------------------------------------------------------------------------------

master_home="$scratch/home-master"
mkdir -p "$master_home"
master_read="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
master_write="bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
run_launcher_in "$master_home" env >/dev/null 2>&1
run_launcher_in "$master_home" CONTEXT_MEMORY_BASE_URL=http://localhost:6141 \
  "CONTEXT_MEMORY_READ_TOKEN=$master_read" "CONTEXT_MEMORY_WRITE_TOKEN=$master_write" env \
  >"$scratch/out" 2>"$scratch/err"
if grep -q "^Parameters__api-read-token=$master_read$" "$master_home/controller.env" &&
  grep -q "^Parameters__api-write-token=$master_write$" "$master_home/controller.env"; then
  ok "exported tokens overwrite the controller's stored pair"
else
  no "exported tokens overwrite the controller's stored pair" "$(grep api- "$master_home/controller.env" | cut -c1-40 | tr '\n' ' ')"
fi
if grep -q "^CONTEXT_MEMORY_WRITE_TOKEN=$master_write$" "$master_home/credentials"; then
  ok "the exported write token is what the credential file publishes"
else
  no "the exported write token is what the credential file publishes" "it is not"
fi
if grep -q 'NOTE: CONTEXT_MEMORY_WRITE_TOKEN is exported' "$scratch/err"; then
  ok "an exported write token is reported, because it stops the read client"
else
  no "an exported write token is reported, because it stops the read client" "no note"
fi
# The profile already holds a base URL and read token from the first run, so neither is re-added
# under the exported ones — the environment wins in every shell, not the profile.
if [ "$(grep -c '^export CONTEXT_MEMORY_BASE_URL=' "$master_home/.zshrc")" = "1" ]; then
  ok "an exported base URL adds no second assignment"
else
  no "an exported base URL adds no second assignment" "count $(grep -c '^export CONTEXT_MEMORY_BASE_URL=' "$master_home/.zshrc")"
fi

# The base URL decides the published port, which env-export reports back.
run_launcher_in "$master_home" CONTEXT_MEMORY_BASE_URL=http://localhost:6141 env-export \
  >"$scratch/out" 2>/dev/null
if grep -q "^export CONTEXT_MEMORY_BASE_URL='http://localhost:6141'$" "$scratch/out"; then
  ok "an exported base URL is the one the stack serves"
else
  no "an exported base URL is the one the stack serves" "$(grep BASE_URL= "$scratch/out" | head -1)"
fi

# A base URL that lives only in the profile — a shell that has not sourced it yet — is still the master.
profile_url_home="$scratch/home-profile-url"
mkdir -p "$profile_url_home"
printf "export CONTEXT_MEMORY_BASE_URL='http://localhost:7141'\n" >"$profile_url_home/.zshrc"
run_launcher_in "$profile_url_home" env-export >"$scratch/out" 2>/dev/null
if grep -q "^export CONTEXT_MEMORY_BASE_URL='http://localhost:7141'$" "$scratch/out"; then
  ok "a base URL held only in the profile is still the master"
else
  no "a base URL held only in the profile is still the master" "$(grep BASE_URL= "$scratch/out" | head -1)"
fi

expect_fail "a P_HOST that disagrees with the base URL is refused" "the base URL is the master" \
  CONTEXT_MEMORY_BASE_URL=http://localhost:6141 P_HOST=7000 env
expect_fail "a non-loopback base URL is refused" "loopback, explicit port" \
  CONTEXT_MEMORY_BASE_URL=http://example.com:6141 env
expect_fail "a base URL without a port is refused" "loopback, explicit port" \
  CONTEXT_MEMORY_BASE_URL=http://localhost env

# One value exported as both tokens would void the read/write split for every client holding it.
expect_fail "identical exported read and write tokens are refused" "read and write tokens are identical" \
  "CONTEXT_MEMORY_READ_TOKEN=$master_read" "CONTEXT_MEMORY_WRITE_TOKEN=$master_read" env

# With no base URL anywhere, the run's own port is published — the shifted one under MIMIS_SHIFT_PORTS.
shift_home="$scratch/home-shifted"
mkdir -p "$shift_home"
run_launcher_in "$shift_home" MIMIS_SHIFT_PORTS=1 env >/dev/null 2>&1
if grep -q "^export CONTEXT_MEMORY_BASE_URL='http://localhost:25141'$" "$shift_home/.zshrc"; then
  ok "a shifted run publishes its own port when no base URL exists"
else
  no "a shifted run publishes its own port when no base URL exists" "$(grep BASE_URL "$shift_home/.zshrc")"
fi

# The write token lives in the credential file, and with no controller.env it is still the master: the
# next start rebuilds the pair around it rather than minting a new write token.
rebuild_home="$scratch/home-rebuild"
mkdir -p "$rebuild_home"
run_launcher_in "$rebuild_home" env >/dev/null 2>&1
kept_write="$(sed -n 's/^CONTEXT_MEMORY_WRITE_TOKEN=//p' "$rebuild_home/credentials")"
rm -f "$rebuild_home/controller.env"
run_launcher_in "$rebuild_home" env >/dev/null 2>&1
if [ -n "$kept_write" ] && grep -q "^Parameters__api-write-token=$kept_write$" "$rebuild_home/controller.env"; then
  ok "the stored write token is the master when controller.env is rebuilt"
else
  no "the stored write token is the master when controller.env is rebuilt" "a new write token was minted"
fi

# ---------------------------------------------------------------------------------------------
# migration: a profile and a credential file written by an earlier release
# ---------------------------------------------------------------------------------------------

migrate_home="$scratch/home-migrate"
mkdir -p "$migrate_home"
{
  echo "# >>> mimisbrunnr — managed by scripts/run.sh env-export --profile >>>"
  echo "export CONTEXT_MEMORY_BASE_URL='http://localhost:5141'"
  echo "export CONTEXT_MEMORY_DECISIONS_BASE_URL=http://127.0.0.1:1234"
  echo "export CONTEXT_MEMORY_WRITE_TOKEN=$master_write"
  echo "# <<< mimisbrunnr <<<"
} >"$migrate_home/.zshrc"
{
  echo "CONTEXT_MEMORY_READ_TOKEN=old-read"
  echo "CONTEXT_MEMORY_WRITE_TOKEN=old-write"
  echo "CONTEXT_MEMORY_DECISIONS_BASE_URL=http://localhost:11434"
  echo "CONTEXT_MEMORY_DECISIONS_API_KEY="
  echo "CONTEXT_MEMORY_DECISIONS_API_KEY="
  echo "CONTEXT_MEMORY_BASE_URL=http://localhost:5141"
} >"$migrate_home/credentials"
printf 'CONTEXT_MEMORY_READ_TOKEN=old-read\n' >"$migrate_home/credentials-read-only"
run_launcher_in "$migrate_home" env >"$scratch/out" 2>"$scratch/err"

if grep -q 'CONTEXT_MEMORY_WRITE_TOKEN' "$migrate_home/.zshrc"; then
  no "a write token in the managed block is removed" "it is still exported"
else
  ok "a write token in the managed block is removed"
fi
# The profile's write token was the master, so it — not the stale file's — is what the file now holds.
if [ "$(grep -c . "$migrate_home/credentials" | tr -d ' ')" = "4" ] &&
  grep -q "^CONTEXT_MEMORY_WRITE_TOKEN=$master_write$" "$migrate_home/credentials"; then
  ok "the credential file is reduced to the master write token"
else
  no "the credential file is reduced to the master write token" "$(grep -v '^#' "$migrate_home/credentials" | cut -c1-40 | tr '\n' ' ')"
fi
if grep -q "^export CONTEXT_MEMORY_DECISIONS_BASE_URL=http://127.0.0.1:1234$" "$migrate_home/.zshrc"; then
  ok "the operator's decision endpoint in the block survives the migration"
else
  no "the operator's decision endpoint in the block survives the migration" "$(grep DECISIONS_BASE_URL "$migrate_home/.zshrc")"
fi
if [ -f "$migrate_home/credentials-read-only" ] && grep -q 'no longer read by any client' "$scratch/err"; then
  ok "the retired read-only file is named, never deleted"
else
  no "the retired read-only file is named, never deleted" "it was removed or not mentioned"
fi

# The removal must not depend on something else being missing: a block that is otherwise complete and
# only carries a write token is still rewritten without it.
sed_inplace "$migrate_home/.zshrc" "s|^# <<< mimisbrunnr <<<\$|export CONTEXT_MEMORY_WRITE_TOKEN=$master_write\\
# <<< mimisbrunnr <<<|"
run_launcher_in "$migrate_home" env >/dev/null 2>&1
if grep -q 'CONTEXT_MEMORY_WRITE_TOKEN' "$migrate_home/.zshrc"; then
  no "a write token is removed from an otherwise complete block" "it is still exported"
else
  ok "a write token is removed from an otherwise complete block"
fi

if [ "$(file_mode "$migrate_home/credentials")" = "600" ]; then
  ok "the credential file is mode 600"
else
  no "the credential file is mode 600" "mode is $(file_mode "$migrate_home/credentials")"
fi

# ---------------------------------------------------------------------------------------------
# env-export --profile
# ---------------------------------------------------------------------------------------------

# The redirect this replaces differs by one character: `>` empties the profile, `>>` grows it. These
# cases exist so a regression there is a test failure rather than somebody's shell profile.
profile_dir="$scratch/profile"
mkdir -p "$profile_dir"
profile="$profile_dir/.zshrc"
printf '# my profile\nalias ll="ls -l"\nexport FOO=bar' >"$profile"   # no trailing newline

run_launcher env-export --profile "$profile" >/dev/null 2>&1

if grep -q '^alias ll=' "$profile" && grep -q '^export FOO=bar' "$profile"; then
  ok "--profile preserves the target's existing content"
else
  no "--profile preserves the target's existing content" "existing lines lost"
fi

if [ "$(grep -c 'CONTEXT_MEMORY_BASE_URL' "$profile")" = "1" ]; then
  ok "--profile writes exactly one credential block"
else
  no "--profile writes exactly one credential block" "count $(grep -c 'CONTEXT_MEMORY_BASE_URL' "$profile")"
fi

before="$(cksum <"$profile")"
run_launcher env-export --profile "$profile" >/dev/null 2>&1
run_launcher env-export --profile "$profile" >/dev/null 2>&1
if [ "$before" = "$(cksum <"$profile")" ]; then
  ok "--profile is idempotent across repeated runs"
else
  no "--profile is idempotent across repeated runs" "the file changed on a re-run"
fi

if [ -s "$profile" ]; then
  ok "--profile never truncates a populated target"
else
  no "--profile never truncates a populated target" "the file is empty"
fi

if [ "$(file_mode "$profile")" = "600" ]; then
  ok "--profile writes mode 600"
else
  no "--profile writes mode 600" "mode is $(file_mode "$profile")"
fi

# A profile is backed up before any modification, so the operator never loses the version they were
# editing — the first write, and every later write that adds something.
if [ "$(count_backups "$profile")" -ge 1 ]; then
  ok "--profile backs up a target that had no managed block"
else
  no "--profile backs up a target that had no managed block" "no backup file"
fi
sed_inplace "$profile" '/^export CONTEXT_MEMORY_DECISIONS_PATH=/d'
run_launcher env-export --profile "$profile" >/dev/null 2>&1
if [ "$(count_backups "$profile")" -ge 2 ]; then
  ok "--profile backs up again when a later write adds a setting"
else
  no "--profile backs up again when a later write adds a setting" "only $(count_backups "$profile") backup(s)"
fi

link_target="$scratch/profile-link-target"
printf 'x\n' >"$link_target"
ln -s "$link_target" "$scratch/profile-link"
if run_launcher env-export --profile "$scratch/profile-link" >"$scratch/out" 2>"$scratch/err"; then
  no "--profile refuses a symlinked target" "it wrote through the link"
elif grep -qi "symlink" "$scratch/err"; then
  ok "--profile refuses a symlinked target"
else
  no "--profile refuses a symlinked target" "$(tail -1 "$scratch/err")"
fi
if [ "$(cat "$link_target")" = "x" ]; then
  ok "--profile did not write through the symlink"
else
  no "--profile did not write through the symlink" "the target was modified"
fi

run_launcher env-export --profile "$profile_dir/created" >/dev/null 2>&1
if [ -f "$profile_dir/created" ] && [ "$(grep -c 'CONTEXT_MEMORY_BASE_URL' "$profile_dir/created")" = "1" ]; then
  ok "--profile creates a target that does not exist"
else
  no "--profile creates a target that does not exist" "absent or empty"
fi

run_launcher env-export --profile "$profile_dir/p.ps1" powershell >/dev/null 2>&1
if grep -q '^\$env:CONTEXT_MEMORY_READ_TOKEN' "$profile_dir/p.ps1"; then
  ok "--profile honours the powershell format"
else
  no "--profile honours the powershell format" "no \$env: assignment"
fi
before="$(cksum <"$profile_dir/p.ps1")"
run_launcher env-export --profile "$profile_dir/p.ps1" powershell >/dev/null 2>&1
if [ "$before" = "$(cksum <"$profile_dir/p.ps1")" ]; then
  ok "--profile is idempotent in the powershell format"
else
  no "--profile is idempotent in the powershell format" "the file changed on a re-run"
fi

# The flag must win over the format slot, or `env-export --profile <file>` reads as a format.
if run_launcher env-export --profile "$profile_dir/ambiguous" >"$scratch/out" 2>"$scratch/err" &&
  [ -f "$profile_dir/ambiguous" ]; then
  ok "--profile is parsed as the flag, not as a format name"
else
  no "--profile is parsed as the flag, not as a format name" "$(tail -1 "$scratch/err")"
fi

# ---------------------------------------------------------------------------------------------
# env-export (stdout)
# ---------------------------------------------------------------------------------------------

# stdout is a data channel: the export is captured with $(...) and eval'd, so a status line written
# there becomes a command the shell tries to run.
run_launcher env-export >"$scratch/export.sh" 2>"$scratch/err"
if grep -q "^export CONTEXT_MEMORY_READ_TOKEN='.\+'$" "$scratch/export.sh"; then
  ok "env-export emits a sourceable export line"
else
  no "env-export emits a sourceable export line" "$(head -2 "$scratch/export.sh" | tr '\n' ' ')"
fi

if [ "$(grep -c '^run\.sh:' "$scratch/export.sh")" -eq 0 ]; then
  ok "env-export keeps diagnostics off stdout"
else
  no "env-export keeps diagnostics off stdout" "a status line reached stdout"
fi

# Evaluated in the *current* shell, not a subshell: `export` inside `( … )` cannot set the caller's
# variables, so a subshell test would report a failure the emitted lines do not actually have.
unset CONTEXT_MEMORY_READ_TOKEN 2>/dev/null || true
# shellcheck disable=SC1090
if eval "$(cat "$scratch/export.sh")" 2>/dev/null && [ -n "${CONTEXT_MEMORY_READ_TOKEN:-}" ]; then
  ok "the exported form evaluates in a shell"
else
  no "the exported form evaluates in a shell" "eval did not define the token"
fi
unset CONTEXT_MEMORY_READ_TOKEN CONTEXT_MEMORY_BASE_URL 2>/dev/null || true
for exported in $(sed -n 's/^export \(CONTEXT_MEMORY_DECISIONS_[A-Z_]*\)=.*/\1/p' "$scratch/export.sh"); do
  unset "$exported"
done

# Evaluating it must never make the write token ambient.
if grep -q "CONTEXT_MEMORY_WRITE_TOKEN" "$scratch/export.sh"; then
  no "env-export omits the write token" "it is present"
else
  ok "env-export omits the write token"
fi

# The container's own names cannot be exported by any shell, so they must not appear.
if grep -q "Parameters__" "$scratch/export.sh"; then
  no "env-export omits the unexportable Parameters__ names" "they are present"
else
  ok "env-export omits the unexportable Parameters__ names"
fi

run_launcher env-export powershell >"$scratch/export.ps1" 2>/dev/null
if grep -q '^\$env:CONTEXT_MEMORY_READ_TOKEN = .\+.$' "$scratch/export.ps1"; then
  ok "env-export emits a PowerShell assignment"
else
  no "env-export emits a PowerShell assignment" "$(head -2 "$scratch/export.ps1" | tr '\n' ' ')"
fi

expect_fail "env-export rejects an unknown format" "format must be posix or powershell" \
  env-export tcsh

# env-export publishes what the operator CONFIGURED, not the built-in default — from the environment,
# else from the profile, else the default.
run_launcher CONTEXT_MEMORY_DECISIONS_ENABLED=true CONTEXT_MEMORY_DECISIONS_API_KEY=sk-planted-export-key \
  env-export >"$scratch/decisions-export.sh" 2>/dev/null
if grep -q "^export CONTEXT_MEMORY_DECISIONS_ENABLED='true'$" "$scratch/decisions-export.sh"; then
  ok "env-export publishes the environment's value, not the default"
else
  no "env-export publishes the environment's value, not the default" "$(grep DECISIONS_ENABLED "$scratch/decisions-export.sh")"
fi
if grep -q "^export CONTEXT_MEMORY_DECISIONS_MODEL='openjev'$" "$scratch/decisions-export.sh"; then
  ok "env-export publishes the profile's value when the environment has none"
else
  no "env-export publishes the profile's value when the environment has none" "$(grep DECISIONS_MODEL "$scratch/decisions-export.sh")"
fi
if grep -q "^export CONTEXT_MEMORY_DECISIONS_BELOW_THRESHOLD='hold'$" "$scratch/decisions-export.sh"; then
  ok "env-export still publishes defaults for untouched settings"
else
  no "env-export still publishes defaults for untouched settings" "$(grep BELOW_THRESHOLD "$scratch/decisions-export.sh")"
fi
if grep -qF 'sk-planted-export-key' "$scratch/decisions-export.sh"; then
  no "env-export never prints the decision API key" "the planted value reached stdout"
else
  ok "env-export never prints the decision API key"
fi

# ---------------------------------------------------------------------------------------------
# the machine credential file holds the write token and nothing else
# ---------------------------------------------------------------------------------------------

if [ -f "$scratch/home/credentials" ]; then
  ok "publishes the machine credential file"
else
  no "publishes the machine credential file" "missing $scratch/home/credentials"
fi
if [ "$(file_mode "$scratch/home/credentials")" = "600" ]; then
  ok "machine credential file is mode 600"
else
  no "machine credential file is mode 600" "mode is $(file_mode "$scratch/home/credentials")"
fi
if [ "$(sed -n 's/^CONTEXT_MEMORY_WRITE_TOKEN=//p' "$scratch/home/credentials")" = \
  "$(sed -n 's/^Parameters__api-write-token=//p' "$credential_file")" ]; then
  ok "machine file carries the controller's write token"
else
  no "machine file carries the controller's write token" "they differ"
fi
if [ "$(grep -v '^#' "$scratch/home/credentials" | grep -c .)" = "1" ]; then
  ok "machine file carries no other setting"
else
  no "machine file carries no other setting" "$(grep -v '^#' "$scratch/home/credentials" | cut -d= -f1 | tr '\n' ' ')"
fi
if [ -e "$scratch/home/credentials-read-only" ]; then
  no "no read-only credential file is published" "one exists"
else
  ok "no read-only credential file is published"
fi

# ---------------------------------------------------------------------------------------------
# a credential file that cannot be read whole is refused, not rewritten smaller
# ---------------------------------------------------------------------------------------------

# The filter that replaced `cat` returns 0 on a file it cannot read and 0 on one it cannot read *as
# text*, so the protection `set -e` used to give for free is gone unless it is stated. Left unguarded,
# a root-owned or restored-backup file is rewritten with only the keys this run could resolve: every
# operator key dropped, the decision settings reset to their defaults, exit 0. Each case asserts the
# file still carries what it did, not merely that the run failed.
#
# Own scratch home per case, so a refusal cannot disturb the cases above.
#
# The `chmod 000` case is the only one that needs the root guard: root reads a 000 file, so the
# precondition cannot be created there. The NUL case must NOT share that guard — root writes NUL bytes
# perfectly well, so it is testable as root, and guarding it too dropped it in exactly the environment
# this repository mandates (the `ubuntu:24.04` container run is root), leaving it uncovered in CI.
if [ "$(id -u)" = "0" ]; then
  skip "the unreadable machine credential file refusal" "running as root: a 000 file is still readable"
else
  unreadable_home="$scratch/home-unreadable"
  mkdir -p "$unreadable_home"
  cp "$scratch/home/credentials" "$unreadable_home/credentials"
  printf 'MY_OPERATOR_KEY=keep-me\n' >>"$unreadable_home/credentials"
  chmod 000 "$unreadable_home/credentials"
  if run_launcher_in "$unreadable_home" env >"$scratch/out" 2>"$scratch/err"; then
    no "an unreadable credential file fails the run" "exited 0 — it would have been rewritten smaller"
  else
    ok "an unreadable credential file fails the run"
  fi
  # Restore the mode before asserting: the run refused to read the file, so a grep here would fail on
  # permissions and report a lost key for a file that is in fact untouched. What is being asserted is
  # that the refusal left the *content* alone.
  chmod 600 "$unreadable_home/credentials"
  if grep -q 'MY_OPERATOR_KEY=keep-me' "$unreadable_home/credentials"; then
    ok "an unreadable credential file is left untouched, operator key intact"
  else
    no "an unreadable credential file is left untouched, operator key intact" "the key is gone"
  fi
fi

nul_home="$scratch/home-nul"
mkdir -p "$nul_home"
cp "$scratch/home/credentials" "$nul_home/credentials"
printf 'MY_OPERATOR_KEY=keep-me\n' >>"$nul_home/credentials"
# printf, not a literal: a NUL in the harness source would truncate this line for every reader.
printf 'CORRUPT=one\000two\n' >>"$nul_home/credentials"
if run_launcher_in "$nul_home" env >"$scratch/out" 2>"$scratch/err"; then
  no "a credential file holding a NUL byte fails the run" "exited 0 — grep read it as binary and emitted nothing"
else
  ok "a credential file holding a NUL byte fails the run"
fi
if grep -q 'MY_OPERATOR_KEY=keep-me' "$nul_home/credentials"; then
  ok "a NUL-bearing credential file is left untouched, operator key intact"
else
  no "a NUL-bearing credential file is left untouched, operator key intact" "the key is gone"
fi

# The container credential file gets the same guard, and it is the one with teeth: regenerating it
# produces a *new* read/write token pair while `~/.mimisbrunnr/credentials` still holds the old one,
# so every skill on the machine 403s against a Host that never said anything was wrong. Asserted on
# the file's checksum, because "the run failed" alone would also pass against a run that died for an
# unrelated reason after having already rewritten it.
if [ "$(id -u)" = "0" ]; then
  skip "the unreadable controller.env refusal" "running as root: a 000 file is still readable"
else
  controller_home="$scratch/home-unreadable-env"
  mkdir -p "$controller_home"
  cp "$scratch/home/controller.env" "$controller_home/controller.env"
  cp "$scratch/home/credentials" "$controller_home/credentials"
  before_sum="$(cksum <"$controller_home/controller.env")"
  chmod 000 "$controller_home/controller.env"
  if run_launcher_in "$controller_home" env >"$scratch/out" 2>"$scratch/err"; then
    no "an unreadable controller.env fails the run" "exited 0 — it regenerates all five secrets"
  else
    ok "an unreadable controller.env fails the run"
  fi
  chmod 600 "$controller_home/controller.env"
  if [ "$(cksum <"$controller_home/controller.env")" = "$before_sum" ]; then
    ok "an unreadable controller.env is left untouched, no token pair rotated"
  else
    no "an unreadable controller.env is left untouched, no token pair rotated" "the file changed"
  fi
fi

# A symlink at the machine-credential path: `[ -f ]` follows it, so `mv` used to replace the link with
# a regular file and freeze every client reading the shared path on tokens this run had rotated away.
# No root dependency, so unlike the `chmod 000` cases this one runs everywhere — including the
# container run where they do not.
link_home="$scratch/home-symlink"
mkdir -p "$link_home/shared"
cp "$scratch/home/credentials" "$link_home/shared/credentials"
printf 'SHARED_OPERATOR_KEY=keep-me\n' >>"$link_home/shared/credentials"
ln -s "$link_home/shared/credentials" "$link_home/credentials"
if run_launcher_in "$link_home" env >"$scratch/out" 2>"$scratch/err"; then
  no "a symlinked machine credential file is refused" "it wrote through the link"
elif grep -q "symlink" "$scratch/err"; then
  ok "a symlinked machine credential file is refused"
else
  no "a symlinked machine credential file is refused" "$(tail -1 "$scratch/err")"
fi
if [ -L "$link_home/credentials" ]; then
  ok "the refused symlink is still a symlink, not replaced by a regular file"
else
  no "the refused symlink is still a symlink, not replaced by a regular file" "it was overwritten"
fi
if grep -q 'SHARED_OPERATOR_KEY=keep-me' "$link_home/shared/credentials"; then
  ok "the shared target behind the refused symlink is untouched"
else
  no "the shared target behind the refused symlink is untouched" "the target changed"
fi

# The interrupt-cleanup contract, asserted on the function rather than on an interrupt: sending
# SIGINT into the window between `mktemp` and `mv` would be a timing test that passes on a slow
# machine and hangs on a fast one. The launcher already extracts `file_value` and evaluates it, so
# this follows the harness's existing idiom — the temp list is newline-delimited, and the second path
# carries a space because a space-delimited list would split there.
temp_dir="$scratch/cleanup-temps"
mkdir -p "$temp_dir"
printf 'secret\n' >"$temp_dir/one.XXXXXX"
printf 'secret\n' >"$temp_dir/two with space.XXXXXX"
cleanup_assert() {
  bash -c '
    set -euo pipefail
    launcher="$1"; src="$2"; dir="$3"
    temp_env=""
    temp_credentials="$4
$5
"
    eval "$(sed -n "/^cleanup()/,/^}/p" "$src")"
    cleanup
  ' _ "$launcher" "$launcher" "$temp_dir" "$temp_dir/one.XXXXXX" "$temp_dir/two with space.XXXXXX"
}
if cleanup_assert && [ ! -e "$temp_dir/one.XXXXXX" ] && [ ! -e "$temp_dir/two with space.XXXXXX" ]; then
  ok "cleanup removes both registered credential temp files, space in a path included"
else
  no "cleanup removes both registered credential temp files, space in a path included" \
    "$(ls "$temp_dir" 2>/dev/null | tr '\n' ' ')"
fi
leftover="$temp_dir/three.XXXXXX"
printf 'secret\n' >"$leftover"
if bash -c '
  set -euo pipefail
  launcher="$1"
  temp_env=""
  temp_credentials=""
  eval "$(sed -n "/^cleanup()/,/^}/p" "$launcher")"
  cleanup
' _ "$launcher" && [ -e "$leftover" ]; then
  ok "cleanup with nothing registered removes nothing"
else
  no "cleanup with nothing registered removes nothing" "an unregistered file was removed"
fi

# …and that `write_machine_credentials` registers the path it creates, which the two cases above
# cannot see: they set `temp_credentials` themselves, so they pass against a launcher that never
# registers anything — which is the pre-fix state. Asserted structurally, as labelled, because the
# behavioural version needs a SIGINT to land between `mktemp` and `mv`.
write_machine_credentials_body="$(sed -n '/^write_machine_credentials()/,/^}/p' "$launcher")"
if printf '%s' "$write_machine_credentials_body" | grep -q 'temp_credentials="\$temp_credentials\$tmp'; then
  ok "write_machine_credentials registers its temp path for interrupt cleanup"
else
  no "write_machine_credentials registers its temp path for interrupt cleanup" "no registration in the function body"
fi

# ---------------------------------------------------------------------------------------------
# file_value: the reader every credential path depends on
# ---------------------------------------------------------------------------------------------

# The function is extracted from the launcher and evaluated here rather than sourcing the whole file:
# the script executes its verb dispatch on entry, so sourcing it would run a container. Asserting on
# the launcher's output instead would prove the *result* is right without proving which file was
# read — a file_value ignoring its second argument still yields a correct credential file whenever
# $env_file happens to hold the same key.
file_value_body() {
  sed -n "/^file_value() {/,/^}/p" "$launcher"
}

if [ -n "$(file_value_body)" ]; then
  eval "$(file_value_body)"

  fv_dir="$scratch/file-value"
  mkdir -p "$fv_dir"
  printf 'SHARED=from-env-file\nONLY_ENV_FILE=env-only\n' >"$fv_dir/env"
  printf 'SHARED=from-other-file\nONLY_OTHER=other-only\n' >"$fv_dir/other"

  # Default target is $env_file, which is a launcher global and unset here — declared locally so
  # the default path is exercised rather than tripping `set -u`.
  env_file="$fv_dir/env"

  if [ "$(file_value SHARED || true)" = "from-env-file" ]; then
    ok "file_value defaults to \$env_file"
  else
    no "file_value defaults to \$env_file" "got $(file_value SHARED || true)"
  fi

  # The second argument selects a different file — the property the whole add-only loop rests on.
  if [ "$(file_value SHARED "$fv_dir/other" || true)" = "from-other-file" ]; then
    ok "file_value reads the file named by its second argument"
  else
    no "file_value reads the file named by its second argument" \
       "got $(file_value SHARED "$fv_dir/other" || true)"
  fi

  if [ "$(file_value ONLY_OTHER "$fv_dir/other" || true)" = "other-only" ]; then
    ok "file_value does not fall back to \$env_file for a key the named file lacks"
  else
    no "file_value does not fall back to \$env_file for a key the named file lacks" \
       "got $(file_value ONLY_OTHER "$fv_dir/other" || true)"
  fi

  # A missing key prints nothing and still exits 0 — sed succeeds on a non-match. Asserting the real
  # contract rather than a tidier one: every caller is written `|| true` plus an empty test, so a
  # reader that "fixed" this to return non-zero would break the callers this documents.
  if [ -z "$(file_value NO_SUCH_KEY "$fv_dir/other")" ]; then
    ok "file_value prints nothing for a missing key"
  else
    no "file_value prints nothing for a missing key" \
       "got $(file_value NO_SUCH_KEY "$fv_dir/other")"
  fi

  # The unguarded form is what a caller must never write: under `set -e` it is fine here (the file
  # exists) but the pattern exists precisely so nobody writes it.
  if file_value ONLY_OTHER "$fv_dir/other" >/dev/null; then
    ok "file_value succeeds on a key the file carries"
  else
    no "file_value succeeds on a key the file carries" "unexpected non-zero"
  fi

  if file_value SHARED "$fv_dir/absent-file"; then
    no "file_value returns non-zero for a missing file" "it reported success"
  else
    ok "file_value returns non-zero for a missing file"
  fi

  # A value containing '=' must survive whole: sed splits on the first '=' only, so a value that
  # itself holds one is returned intact rather than truncated at the second.
  printf 'WITH_EQUALS=a=b=c\n' >"$fv_dir/equals"
  if [ "$(file_value WITH_EQUALS "$fv_dir/equals" || true)" = "a=b=c" ]; then
    ok "file_value returns a value containing '=' whole"
  else
    no "file_value returns a value containing '=' whole" \
       "got $(file_value WITH_EQUALS "$fv_dir/equals" || true)"
  fi
else
  no "file_value is defined in run.sh" "no definition found"
fi

# ---------------------------------------------------------------------------------------------
# adoption of a provisioned pair, as a unit
# ---------------------------------------------------------------------------------------------

# A provisioned pair is adopted rather than minted, so the controller and the skills hold one
# credential. A file carrying only one of the two cannot yield a matched pair and must be ignored —
# adopting one and minting the other is the split-pair 403 adoption exists to prevent.
adopt_dir="$scratch/adopt"
mkdir -p "$adopt_dir"
provisioned_read="1111111111111111111111111111111111111111111111111111111111111111"
provisioned_write="2222222222222222222222222222222222222222222222222222222222222222"
printf 'Parameters__api-read-token=%s\nParameters__api-write-token=%s\n' \
  "$provisioned_read" "$provisioned_write" >"$adopt_dir/mimisbrunnr.env.controller"

run_launcher_in "$adopt_dir" MIMIS_TOKEN_FILE="$adopt_dir/mimisbrunnr.env.controller" env >/dev/null 2>&1
adopted_file="$adopt_dir/controller.env"
if [ "$(sed -n 's/^Parameters__api-read-token=//p' "$adopted_file")" = "$provisioned_read" ]; then
  ok "adopts the provisioned read token verbatim"
else
  no "adopts the provisioned read token verbatim" "got $(sed -n 's/^Parameters__api-read-token=//p' "$adopted_file")"
fi
# The prefix bug: an adopted token once kept its "read=" label because a pipe was parsed as part of an
# assignment, so the controller held a credential no skill had.
if sed -n 's/^Parameters__api-read-token=//p' "$adopted_file" | grep -q "read="; then
  no "the adopted token carries no field prefix" "it still contains 'read='"
else
  ok "the adopted token carries no field prefix"
fi

half_dir="$scratch/half"
mkdir -p "$half_dir"
printf 'Parameters__api-read-token=%s\n' "$provisioned_read" >"$half_dir/mimisbrunnr.env.controller"
run_launcher_in "$half_dir" MIMIS_TOKEN_FILE="$half_dir/mimisbrunnr.env.controller" env >/dev/null 2>&1
half_read="$(sed -n 's/^Parameters__api-read-token=//p' "$half_dir/controller.env")"
half_write="$(sed -n 's/^Parameters__api-write-token=//p' "$half_dir/controller.env")"
if [ "$half_read" != "$provisioned_read" ] && [ "$half_write" != "2222222222222222222222222222222222222222222222222222222222222222" ]; then
  ok "a half-populated provision file is ignored, not half-adopted"
else
  no "a half-populated provision file is ignored, not half-adopted" "one token was taken from it"
fi
if [ "$half_read" != "$half_write" ]; then
  ok "the half-adopted pair still yields two distinct tokens"
else
  no "the half-adopted pair still yields two distinct tokens" "they match"
fi

# ---------------------------------------------------------------------------------------------
echo
echo "passed: $passed   failed: $failed"
[ "$failed" -eq 0 ] || exit 1