#!/usr/bin/env bash
# Harness for the skills installers (`install-skills.sh`, and `install-skills.ps1` when pwsh is present).
#
# Scope: the manifest reconciliation and the root search. The clone/copy path is exercised against the
# checkout itself as the clone source (SKILLS_REPO=the repo, SKILLS_BRANCH=the current branch), so no
# network and no GitHub access is involved — a harness that reached the network would be red for reasons
# that have nothing to do with the code under test.
#
# Every case runs in a scratch tree under $TMPDIR. The installer only ever writes inside the skills root
# it resolves, and every case forces that root with SKILLS_ROOT, so a red run cannot touch the checkout's
# own .agents/skills.
#
# Both ports are held to the same expectations and, when both are runnable, to each other: parity is the
# point of shipping two scripts for one contract, and a divergence that only one platform sees is exactly
# the bug this file exists to catch.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
installer_sh="$repo_root/scripts/install-skills.sh"
installer_ps1="$repo_root/scripts/install-skills.ps1"
source_repo="$repo_root"
# Empty unless HEAD really is a branch. `git rev-parse --abbrev-ref HEAD` answers the literal string
# "HEAD" in a detached checkout — which is what actions/checkout produces — and that is not a valid
# `git clone --branch` argument, so passing it through made every case die at the clone on the gate while
# the same harness passed on a developer machine. `symbolic-ref` answers nothing at all when detached,
# which is the honest signal that there is no branch to name. **A green run here does not prove this
# works:** on a branch, both spellings agree, so the fix is only observable in a detached checkout.
source_branch="$(git -C "$repo_root" symbolic-ref --quiet --short HEAD 2>/dev/null || true)"

scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

passed=0
failed=0
have_pwsh=0
[ -n "${SKILLS_TEST_SKIP_PS1:-}" ] || command -v pwsh >/dev/null 2>&1 && have_pwsh=1

ok() { passed=$((passed + 1)); echo "  ok   $1"; }
no() {
  failed=$((failed + 1))
  echo "  FAIL $1"
  [ -n "${2:-}" ] && echo "       $2"
  return 0
}

# A fresh scratch skills root carrying $2 named skills, one SKILL.md each. Echoes the root.
make_root() {
  local root="$scratch/case-$1/.agents/skills"
  shift
  mkdir -p "$root/_mimisbrunnr-shared"
  local name
  for name in "$@"; do
    mkdir -p "$root/$name"
    printf '# %s\n' "$name" >"$root/$name/SKILL.md"
  done
  printf '%s\n' "$root"
}

# The shared folder is always refreshed from the clone source, so point the branch at this checkout.
run_sh() {
  local root="$1"; shift
  SKILLS_ROOT="$root" SKILLS_REPO="$source_repo" SKILLS_BRANCH="$source_branch" \
    bash "$installer_sh" "$@" >"$scratch/out" 2>"$scratch/err"
}

run_ps1() {
  local root="$1"; shift
  SKILLS_ROOT="$root" SKILLS_REPO="$source_repo" SKILLS_BRANCH="$source_branch" \
    pwsh -NoProfile -File "$installer_ps1" "$@" >"$scratch/out" 2>"$scratch/err"
}

# manifest_field ROOT 'name' -> the installed flag ("true"/"false"), or empty when absent.
manifest_flag() {
  python3 -c "
import json, sys
data = json.load(open(sys.argv[1]))
for s in data.get('skills', []):
    if s['name'] == sys.argv[2]:
        print('true' if s['installed'] else 'false')
        raise SystemExit(0)
" "$1/_mimisbrunnr-shared/installation.json" "$2"
}

manifest_names() {
  python3 -c "
import json, sys
data = json.load(open(sys.argv[1]))
print(' '.join(s['name'] for s in data.get('skills', [])))
" "$1/_mimisbrunnr-shared/installation.json"
}

# ISO-8601 UTC: 2026-10-15T13:10:22Z. Asserted against a regex rather than a parse, because the point is
# the *shape* — a machine must be able to compare two manifests as strings.
manifest_timestamp_ok() {
  python3 -c "
import json, re, sys
stamp = json.load(open(sys.argv[1]))['updated']
raise SystemExit(0 if re.fullmatch(r'\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z', stamp) else 1)
" "$1/_mimisbrunnr-shared/installation.json"
}



echo "skills installer harness (branch: $source_branch)"

# ---------------------------------------------------------------------------------------------
# 1. a first run creates the manifest from what is on disk
# ---------------------------------------------------------------------------------------------

check_first_run() {
  local root="$1" runner="$2" names
  local manifest="$1/_mimisbrunnr-shared/installation.json"
  if [ ! -f "$manifest" ]; then no "[$runner] first run creates installation.json"; return; fi
  # The catalogue is source ∪ disk: the skills the clone offers (real names from this checkout) plus the
  # synthetic ones this case planted (alpha/beta). A manifest derived from disk alone would omit every
  # real skill and make a first install into an empty folder a no-op.
  names=" $(manifest_names "$root") "
  for expected in alpha beta ai-terse mimisbrunnr-odin-context-memory; do
    case "$names" in
      *" $expected "*) ;;
      *) no "[$runner] first run lists source skills as well as those on disk" "$expected missing from: $names"; return ;;
    esac
  done
  if [ "$(manifest_flag "$root" alpha)" != "true" ] || [ "$(manifest_flag "$root" beta)" != "true" ] || [ "$(manifest_flag "$root" ai-terse)" != "true" ]; then
    no "[$runner] first run marks skills installed:true" "alpha=$(manifest_flag "$root" alpha) beta=$(manifest_flag "$root" beta) ai-terse=$(manifest_flag "$root" ai-terse)"
    return
  fi
  if ! manifest_timestamp_ok "$root"; then no "[$runner] first run stamps an ISO-8601 UTC time"; return; fi
  ok "[$runner] first run: manifest from source ∪ disk, all installed:true, ISO-8601 UTC stamp"
}

for port in sh; do
  [ "$have_pwsh" = 1 ] && port="sh ps1"
  for runner in $port; do
    root="$(make_root "first-$runner" alpha beta)"
    if [ "$runner" = sh ]; then run_sh "$root" >/dev/null 2>&1 || true; else run_ps1 "$root" >/dev/null 2>&1 || true; fi
    check_first_run "$root" "$runner"
  done
done

# ---------------------------------------------------------------------------------------------
# 2. reconcile: keep an operator's false, add new skills, drop removed ones
# ---------------------------------------------------------------------------------------------

for port in sh; do
  [ "$have_pwsh" = 1 ] && port="sh ps1"
  for runner in $port; do
    root="$(make_root "reconcile-$runner" alpha beta gamma)"
    if [ "$runner" = sh ]; then run_sh "$root" >/dev/null 2>&1; else run_ps1 "$root" >/dev/null 2>&1; fi

    # The operator switches alpha off.
    python3 - "$root/_mimisbrunnr-shared/installation.json" <<'PYEOF'
import json, sys
path = sys.argv[1]
data = json.load(open(path))
for entry in data["skills"]:
    if entry["name"] == "alpha":
        entry["installed"] = False
json.dump(data, open(path, "w"), indent=2)
PYEOF
    # …and a skill is removed from disk while another is added.
    rm -rf "$root/gamma"
    mkdir -p "$root/delta"; printf '# delta\n' >"$root/delta/SKILL.md"

    if [ "$runner" = sh ]; then run_sh "$root" >/dev/null 2>&1; else run_ps1 "$root" >/dev/null 2>&1; fi

    # gamma is in neither the source nor disk any more, so it must go; delta is new and must join.
    # The real source skills stay, so "dropped" cannot pass by emptying the manifest.
    names=" $(manifest_names "$root") "
    case "$names" in
      *" gamma "*) no "[$runner] reconcile: a removed skill is dropped" "gamma survived in:$names" ;;
      *" alpha "*) ok "[$runner] reconcile: a removed skill is dropped" ;;
      *) no "[$runner] reconcile: a removed skill is dropped" "alpha went missing too — the manifest was rebuilt, not reconciled:$names" ;;
    esac
    case "$names" in
      *" delta "*) ok "[$runner] reconcile: a newly seen skill is added" ;;
      *) no "[$runner] reconcile: a newly seen skill is added" "delta missing from:$names" ;;
    esac
    case "$names" in
      *" ai-terse "*) ok "[$runner] reconcile: source skills are retained" ;;
      *) no "[$runner] reconcile: source skills are retained" "ai-terse missing from:$names" ;;
    esac
    [ "$(manifest_flag "$root" alpha)" = "false" ] \
      && ok "[$runner] reconcile: an operator's installed:false survives the sync" \
      || no "[$runner] reconcile: an operator's installed:false survives the sync" "alpha=$(manifest_flag "$root" alpha)"
    [ "$(manifest_flag "$root" delta)" = "true" ] \
      && ok "[$runner] reconcile: a newly seen skill joins as installed:true" \
      || no "[$runner] reconcile: a newly seen skill joins as installed:true" "delta=$(manifest_flag "$root" delta)"
  done
done

# ---------------------------------------------------------------------------------------------
# 3. the manifest survives the shared-folder refresh (write order, not luck)
# ---------------------------------------------------------------------------------------------

for port in sh; do
  [ "$have_pwsh" = 1 ] && port="sh ps1"
  for runner in $port; do
    root="$(make_root "writeorder-$runner" alpha)"
    if [ "$runner" = sh ]; then run_sh "$root" >/dev/null 2>&1; else run_ps1 "$root" >/dev/null 2>&1; fi
    if [ -f "$root/_mimisbrunnr-shared/installation.json" ] && [ -f "$root/_mimisbrunnr-shared/scripts/context_memory_client.py" ]; then
      ok "[$runner] manifest and the refreshed shared scripts both present after one run"
    else
      no "[$runner] manifest and the refreshed shared scripts both present after one run" \
        "manifest=$([ -f "$root/_mimisbrunnr-shared/installation.json" ] && echo yes || echo no) shared=$([ -f "$root/_mimisbrunnr-shared/scripts/context_memory_client.py" ] && echo yes || echo no)"
    fi
  done
done

# ---------------------------------------------------------------------------------------------
# 3b. the shared folder refresh is a refresh, not an accumulate
# ---------------------------------------------------------------------------------------------

# A file the source no longer has must be gone after a run: an updater that only ever copies in leaves
  # a removed script alive forever, and the removed script is exactly the one a fix was meant to delete.
for port in sh; do
  [ "$have_pwsh" = 1 ] && port="sh ps1"
  for runner in $port; do
    root="$(make_root "stale-$runner" alpha)"
    mkdir -p "$root/_mimisbrunnr-shared/scripts"
    printf 'removed upstream\n' >"$root/_mimisbrunnr-shared/scripts/removed_upstream.py"
    printf 'stale at the folder root\n' >"$root/_mimisbrunnr-shared/STALE.md"
    if [ "$runner" = sh ]; then run_sh "$root" >/dev/null 2>&1 || true; else run_ps1 "$root" >/dev/null 2>&1 || true; fi
    if [ ! -e "$root/_mimisbrunnr-shared/scripts/removed_upstream.py" ] && [ ! -e "$root/_mimisbrunnr-shared/STALE.md" ]; then
      ok "[$runner] a shared entry the source no longer has is removed, not merged in"
    else
      no "[$runner] a shared entry the source no longer has is removed, not merged in" "stale files survived"
    fi
    if [ -f "$root/_mimisbrunnr-shared/installation.json" ] && [ -f "$root/_mimisbrunnr-shared/scripts/context_memory_client.py" ]; then
      ok "[$runner] the stale sweep spares installation.json while keeping the source's scripts"
    else
      no "[$runner] the stale sweep spares installation.json while keeping the source's scripts" \
        "manifest=$([ -f "$root/_mimisbrunnr-shared/installation.json" ] && echo yes || echo no) client=$([ -f "$root/_mimisbrunnr-shared/scripts/context_memory_client.py" ] && echo yes || echo no)"
    fi
  done
done

# ---------------------------------------------------------------------------------------------
# 3c. a first install into an EMPTY skills folder actually installs something
# ---------------------------------------------------------------------------------------------

# This is the case the end-to-end run caught and the harness did not: with the catalogue derived from
# disk alone, an empty folder offers nothing to install, so the very first run — the one every operator
# does — left them with an empty folder and a manifest listing nothing, and reported success.
for port in sh; do
  [ "$have_pwsh" = 1 ] && port="sh ps1"
  for runner in $port; do
    root="$scratch/emptyinstall-$runner/.codex/skills"
    mkdir -p "$root"
    if [ "$runner" = sh ]; then run_sh "$root" >/dev/null 2>&1 || true; else run_ps1 "$root" >/dev/null 2>&1 || true; fi
    on_disk="$(find "$root" -maxdepth 2 -name SKILL.md | wc -l | tr -d ' ')"
    if [ "$on_disk" -gt 5 ] && [ "$on_disk" = "$(manifest_names "$root" | wc -w | tr -d ' ')" ]; then
      ok "[$runner] a first install into an empty folder puts the source's skills on disk ($on_disk)"
    else
      no "[$runner] a first install into an empty folder puts the source's skills on disk" \
        "on disk: $on_disk, in manifest: $(manifest_names "$root" | wc -w | tr -d ' ')"
    fi
  done
done

# ---------------------------------------------------------------------------------------------
# 4. only installed:true folders are replaced from the clone source
# ---------------------------------------------------------------------------------------------

for port in sh; do
  [ "$have_pwsh" = 1 ] && port="sh ps1"
  for runner in $port; do
    # ai-terse and mimisbrunnr-odin-context-memory both exist in the checkout, so both are real
    # clone-and-copy cases rather than synthetic names that would only exercise the warning branch.
    root="$(make_root "update-$runner" ai-terse mimisbrunnr-odin-context-memory)"
    printf 'operator edit\n' >"$root/ai-terse/SKILL.md"
    if [ "$runner" = sh ]; then run_sh "$root" >/dev/null 2>&1; else run_ps1 "$root" >/dev/null 2>&1; fi

    if grep -q '^name:' "$root/ai-terse/SKILL.md" 2>/dev/null; then
      ok "[$runner] an installed:true skill is replaced from the clone source"
    else
      no "[$runner] an installed:true skill is replaced from the clone source" "SKILL.md still: $(head -1 "$root/ai-terse/SKILL.md")"
    fi
    if [ -d "$root/mimisbrunnr-odin-context-memory/scripts" ]; then
      ok "[$runner] the copied skill carries its scripts folder"
    else
      no "[$runner] the copied skill carries its scripts folder"
    fi

    # Now the same run with one skill switched off: the off folder must be left exactly as it is.
    # The manifest has to exist before it can be switched off, so this is a first run followed by an
    # edit: that is the operator's actual sequence (install, then turn a skill off).
    root2="$(make_root "update-off-$runner" ai-terse mimisbrunnr-odin-context-memory)"
    printf 'operator edit\n' >"$root2/ai-terse/SKILL.md"
    if [ "$runner" = sh ]; then run_sh "$root2" >/dev/null 2>&1; else run_ps1 "$root2" >/dev/null 2>&1; fi
    printf 'operator edit\n' >"$root2/ai-terse/SKILL.md"
    python3 - "$root2/_mimisbrunnr-shared/installation.json" <<'PYEOF'
import json, sys
path = sys.argv[1]
data = json.load(open(path))
for entry in data["skills"]:
    entry["installed"] = False
json.dump(data, open(path, "w"), indent=2)
PYEOF
    if [ "$runner" = sh ]; then run_sh "$root2" >/dev/null 2>&1; else run_ps1 "$root2" >/dev/null 2>&1; fi
    if [ "$(cat "$root2/ai-terse/SKILL.md")" = "operator edit" ]; then
      ok "[$runner] an installed:false skill is never overwritten"
    else
      no "[$runner] an installed:false skill is never overwritten" "content is now: $(head -1 "$root2/ai-terse/SKILL.md")"
    fi
  done
done

# ---------------------------------------------------------------------------------------------
# 5. a corrupt manifest is an error, not a first install
# ---------------------------------------------------------------------------------------------

for port in sh; do
  [ "$have_pwsh" = 1 ] && port="sh ps1"
  for runner in $port; do
    root="$(make_root "corrupt-$runner" alpha)"
    printf '{ not json' >"$root/_mimisbrunnr-shared/installation.json"
    # A non-zero exit is the expected outcome here. `|| st=$?` (not `|| true`) keeps the harness's own
    # `set -e` from tripping while still capturing the status — swallowing it would let an installer that
    # exits 0 on corrupt input pass while violating the hard-error contract.
    st=0
    if [ "$runner" = sh ]; then run_sh "$root" >/dev/null 2>&1 || st=$?; else run_ps1 "$root" >/dev/null 2>&1 || st=$?; fi
    if [ "$st" -ne 0 ]; then
      ok "[$runner] a corrupt manifest exits non-zero"
    else
      no "[$runner] a corrupt manifest exits non-zero" "exit was 0"
    fi
    if [ "$(cat "$root/_mimisbrunnr-shared/installation.json")" = "{ not json" ]; then
      ok "[$runner] a corrupt manifest is refused and left untouched"
    else
      no "[$runner] a corrupt manifest is refused and left untouched" "it was rewritten"
    fi
  done
done

# ---------------------------------------------------------------------------------------------
# 6. the root search, in the documented order
# ---------------------------------------------------------------------------------------------

# Run one installer in $2 (a scratch directory), with SKILLS_ROOT deliberately unset so the search runs.
# Echoes the exit status instead of failing the harness, so a case can assert on it.
run_in_dir_searched() {
  local runner="$1" dir="$2"
  if [ "$runner" = sh ]; then
    ( cd "$dir" && SKILLS_REPO="$source_repo" SKILLS_BRANCH="$source_branch" bash "$installer_sh" ) >/dev/null 2>&1
  else
    ( cd "$dir" && SKILLS_REPO="$source_repo" SKILLS_BRANCH="$source_branch" pwsh -NoProfile -File "$installer_ps1" ) >/dev/null 2>&1
  fi
}

for port in sh; do
  [ "$have_pwsh" = 1 ] && port="sh ps1"
  for runner in $port; do
    # A tree with .claude/skills and .codex/skills but no .agents/skills: the search must land on
    # .claude/skills, in the documented order, and must not create anything.
    base="$scratch/search-$runner"
    mkdir -p "$base/.claude/skills/only-claude" "$base/.codex/skills/only-codex"
    printf '# only-claude\n' >"$base/.claude/skills/only-claude/SKILL.md"
    printf '# only-codex\n' >"$base/.codex/skills/only-codex/SKILL.md"
    if ! run_in_dir_searched "$runner" "$base"; then
      no "[$runner] root search: the run succeeds" "see the run above"
    elif [ -f "$base/.claude/skills/_mimisbrunnr-shared/installation.json" ] && [ ! -e "$base/.codex/skills/_mimisbrunnr-shared" ]; then
      ok "[$runner] root search: .claude/skills wins over .codex/skills"
    else
      no "[$runner] root search: .claude/skills wins over .codex/skills" "the manifest landed in the wrong root"
    fi
    if [ -e "$base/.agents" ]; then
      no "[$runner] root search: creates no skills directory" "it created .agents"
    else
      ok "[$runner] root search: creates no skills directory"
    fi

    # A tree with no skills directory at all: a hard error, and still nothing created.
    empty="$scratch/empty-$runner"
    mkdir -p "$empty"
    if run_in_dir_searched "$runner" "$empty"; then
      no "[$runner] no skills directory exits non-zero" "expected failure, got success"
    else
      ok "[$runner] no skills directory exits non-zero"
    fi
    if [ ! -d "$empty/.agents" ] && [ ! -d "$empty/.claude" ]; then
      ok "[$runner] no skills directory: creates nothing"
    else
      no "[$runner] no skills directory: creates nothing" "it created a skills directory"
    fi
  done
done

# ---------------------------------------------------------------------------------------------
# 7. parity: the two ports agree on the same scenario
# ---------------------------------------------------------------------------------------------

if [ "$have_pwsh" = 1 ]; then
  sh_names=""; ps_names=""; sh_flags=""; ps_flags=""
  for runner in sh ps1; do
    root="$(make_root "parity-$runner" alpha beta)"
    if [ "$runner" = sh ]; then run_sh "$root" >/dev/null 2>&1; else run_ps1 "$root" >/dev/null 2>&1; fi
    python3 - "$root/_mimisbrunnr-shared/installation.json" <<'PYEOF'
import json, sys
path = sys.argv[1]
data = json.load(open(path))
for entry in data["skills"]:
    if entry["name"] == "alpha":
        entry["installed"] = False
json.dump(data, open(path, "w"), indent=2)
PYEOF
    if [ "$runner" = sh ]; then run_sh "$root" >/dev/null 2>&1; else run_ps1 "$root" >/dev/null 2>&1; fi
    names="$(manifest_names "$root")"
    flags="$(manifest_flag "$root" alpha)/$(manifest_flag "$root" beta)"
    if [ "$runner" = sh ]; then sh_names="$names"; sh_flags="$flags"; else ps_names="$names"; ps_flags="$flags"; fi
  done
  [ "$sh_names" = "$ps_names" ] \
    && ok "parity: both ports emit the same skill list" \
    || no "parity: both ports emit the same skill list" "sh='$sh_names' ps1='$ps_names'"
  [ "$sh_flags" = "$ps_flags" ] \
    && ok "parity: both ports preserve the same installed flags" \
    || no "parity: both ports preserve the same installed flags" "sh='$sh_flags' ps1='$ps_flags'"
else
  echo "  skip pwsh not installed — parity and ps1 cases not run (not a failure)"
fi

# ---------------------------------------------------------------------------------------------

echo
echo "passed: $passed  failed: $failed"
[ "$failed" -eq 0 ]