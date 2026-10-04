#!/usr/bin/env bash
# Harness for the operator controller launchers (`run.sh`).
#
# Scope: everything that does not need a container engine. It never starts, stops or pulls anything, and
# it never reads the operator's real credentials — every case runs against a scratch MIMIS_HOME, so a
# red run cannot leave a token file behind or rotate a credential a running Host holds.
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
no() {
  failed=$((failed + 1))
  echo "  FAIL $1"
  [ -n "${2:-}" ] && echo "       $2"
  return 0
}

# Run the launcher against a scratch home. Never inherits the operator's MIMIS_* overrides, and points
# the machine-credential publication at the scratch tree as well — the launcher derives that path from
# MIMIS_HOME, and a harness that only redirected the home would still write to the real ~/.mimisbrunnr.
run_launcher() {
  env -u MIMIS_TOKEN_FILE -u CONTEXT_MEMORY_READ_TOKEN -u CONTEXT_MEMORY_WRITE_TOKEN \
    -u CONTEXT_MEMORY_BASE_URL -u CONTEXT_MEMORY_CREDENTIAL_FILE \
    MIMIS_HOME="$scratch/home" MIMIS_MACHINE_CREDENTIALS="$scratch/home/credentials" \
    "$launcher" "$@"
}

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
  if env -u MIMIS_TOKEN_FILE MIMIS_ID="$id" MIMIS_HOME="$scratch/home" \
    MIMIS_MACHINE_CREDENTIALS="$scratch/home/credentials" "$launcher" env \
    >"$scratch/out" 2>"$scratch/err"; then
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
env -u MIMIS_TOKEN_FILE \
  MIMIS_HOME="$scratch/home" "Parameters__api-write-token=$supplied" \
  "$launcher" env >/dev/null 2>&1
if grep -qF "$supplied" "$credential_file"; then
  no "a supplied secret is not written to disk" "it was persisted"
else
  ok "a supplied secret is not written to disk"
fi

# An env-sourced value must not cause the stored one to be dropped. An earlier draft persisted only the
# values the current run selected, so supplying one variable silently dropped the stored token for it —
# and the next run without that variable minted a different one, 403ing a Host already running.
stored_read="$(sed -n 's/^Parameters__api-read-token=//p' "$credential_file")"
env -u MIMIS_TOKEN_FILE \
  MIMIS_HOME="$scratch/home" "Parameters__api-read-token=$supplied" \
  "$launcher" env >/dev/null 2>&1
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
if MIMIS_HOME="$symlink_dir" "$launcher" env >"$scratch/out" 2>"$scratch/err"; then
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

# Idempotence is the whole reason the flag exists: an appended block duplicates on every run, and the
# stale copy of a rotated token survives in a world-readable file while overriding the fresh one.
before="$(cksum <"$profile")"
run_launcher env-export --profile "$profile" >/dev/null 2>&1
run_launcher env-export --profile "$profile" >/dev/null 2>&1
if [ "$before" = "$(cksum <"$profile")" ]; then
  ok "--profile is idempotent across repeated runs"
else
  no "--profile is idempotent across repeated runs" "the file changed on a re-run"
fi

if [ "$(grep -c 'CONTEXT_MEMORY_READ_TOKEN' "$profile")" = "1" ]; then
  ok "a re-run rotates in place rather than appending a second token"
else
  no "a re-run rotates in place rather than appending a second token" "count $(grep -c 'CONTEXT_MEMORY_READ_TOKEN' "$profile")"
fi

# A first write must not empty a file that has content — the `>` failure mode, reached through the
# script rather than a redirect.
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
# editing — regardless of whether it already carried a managed block.
if [ -n "$(ls "$profile_dir"/.zshrc-mimisbrunnr-* 2>/dev/null | head -1)" ]; then
  ok "--profile backs up a target that had no managed block"
else
  no "--profile backs up a target that had no managed block" "no backup file"
fi

# The re-rotate path (lines above ran two more `--profile` writes) must also leave a pre-rotation
# copy, not only the first-time append.
if [ "$(ls "$profile_dir"/.zshrc-mimisbrunnr-* 2>/dev/null | wc -l)" -ge 2 ]; then
  ok "--profile backs up on a re-rotate too"
else
  no "--profile backs up on a re-rotate too" "only $(ls "$profile_dir"/.zshrc-mimisbrunnr-* 2>/dev/null | wc -l) backup(s)"
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

if grep -q "CONTEXT_MEMORY_WRITE_TOKEN" "$scratch/export.sh"; then
  ok "env-export includes the write token"
else
  no "env-export includes the write token" "absent"
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

# ---------------------------------------------------------------------------------------------
# machine credential publication
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
if [ -f "$scratch/home/credentials-read-only" ]; then
  ok "publishes a read-only credential file"
else
  no "publishes a read-only credential file" "absent"
fi

# The read-only file must not carry a write token: a read-only worker sourcing it would otherwise hold
# write capability, and the read client refuses to start when one is present.
if grep -q "CONTEXT_MEMORY_WRITE_TOKEN" "$scratch/home/credentials-read-only"; then
  no "the read-only file carries no write token" "it does"
else
  ok "the read-only file carries no write token"
fi

# The machine file must publish the CONTEXT_MEMORY_* names the clients read, not the container's.
if grep -q "CONTEXT_MEMORY_READ_TOKEN=.\+" "$scratch/home/credentials"; then
  ok "machine file carries CONTEXT_MEMORY_READ_TOKEN"
else
  no "machine file carries CONTEXT_MEMORY_READ_TOKEN" "absent or empty"
fi

# The add-only pattern: a key the operator or a previous feature added must survive a re-run.
# A full rewrite would silently drop it — the defect this pattern closes.
operator_key="CONTEXT_MEMORY_DECISIONS_ENABLED=true"
printf '%s\n' "$operator_key" >>"$scratch/home/credentials"
run_launcher env >/dev/null 2>&1
if grep -qF "$operator_key" "$scratch/home/credentials"; then
  ok "a re-run preserves an operator-added key in the machine credential file"
else
  no "a re-run preserves an operator-added key in the machine credential file" "the key was dropped"
fi

# The three managed keys must still be present after the re-run above.
for key in CONTEXT_MEMORY_BASE_URL CONTEXT_MEMORY_READ_TOKEN CONTEXT_MEMORY_WRITE_TOKEN; do
  if grep -q "^$key=.\+" "$scratch/home/credentials"; then
    ok "machine file still carries $key after a re-run"
  else
    no "machine file still carries $key after a re-run" "absent or empty"
  fi
done

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

MIMIS_HOME="$adopt_dir" MIMIS_MACHINE_CREDENTIALS="$adopt_dir/credentials" MIMIS_TOKEN_FILE="$adopt_dir/mimisbrunnr.env.controller" \
  "$launcher" env >/dev/null 2>&1
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
MIMIS_HOME="$half_dir" MIMIS_MACHINE_CREDENTIALS="$half_dir/credentials" MIMIS_TOKEN_FILE="$half_dir/mimisbrunnr.env.controller" \
  "$launcher" env >/dev/null 2>&1
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