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
[ -z "${SKILLS_TEST_SKIP_PS1:-}" ] && command -v pwsh >/dev/null 2>&1 && have_pwsh=1

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
  # The catalogue is the owned union: owned source skills (real names from this checkout) plus owned
  # skills already on disk. alpha/beta are synthetic and UNOWNED, so they must stay out — the case that
  # caught the installer pulling local-only skills (epic-breakdown, jira-ticket-task-from-diff) into a
  # foreign manifest. A manifest derived from disk alone would omit every real skill and make a first
  # install into an empty folder a no-op.
  names=" $(manifest_names "$root") "
  for expected in mimisbrunnr-odin-context-memory ai-brain-dump ai-understanding; do
    case "$names" in
      *" $expected "*) ;;
      *) no "[$runner] first run lists owned source skills" "$expected missing from: $names"; return ;;
    esac
  done
  for excluded in alpha beta; do
    case "$names" in
      *" $excluded "*) no "[$runner] first run excludes unowned disk skills" "$excluded leaked into: $names"; return ;;
    esac
  done
  if [ "$(manifest_flag "$root" mimisbrunnr-odin-context-memory)" != "true" ] || [ "$(manifest_flag "$root" ai-brain-dump)" != "true" ]; then
    no "[$runner] first run marks owned skills installed:true" "odin=$(manifest_flag "$root" mimisbrunnr-odin-context-memory) brain=$(manifest_flag "$root" ai-brain-dump)"
    return
  fi
  if ! manifest_timestamp_ok "$root"; then no "[$runner] first run stamps an ISO-8601 UTC time"; return; fi
  ok "[$runner] first run: manifest from the owned catalogue, all installed:true, ISO-8601 UTC stamp"
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
    # alpha/beta/gamma are synthetic and UNOWNED: they exercise the exclusion, not the catalogue.
    root="$(make_root "reconcile-$runner" alpha beta gamma)"
    if [ "$runner" = sh ]; then run_sh "$root" >/dev/null 2>&1; else run_ps1 "$root" >/dev/null 2>&1; fi

    # The operator switches an OWNED skill off.
    python3 - "$root/_mimisbrunnr-shared/installation.json" <<'PYEOF'
import json, sys
path = sys.argv[1]
data = json.load(open(path))
for entry in data["skills"]:
    if entry["name"] == "ai-brain-dump":
        entry["installed"] = False
json.dump(data, open(path, "w"), indent=2)
PYEOF
    # …and an unowned skill is removed from disk while another unowned one is added.
    rm -rf "$root/gamma"
    mkdir -p "$root/delta"; printf '# delta\n' >"$root/delta/SKILL.md"

    if [ "$runner" = sh ]; then run_sh "$root" >/dev/null 2>&1; else run_ps1 "$root" >/dev/null 2>&1; fi

    # Neither unowned skill may appear, whenever it was seen. The owned source skills stay, so
    # "excluded" cannot pass by emptying the manifest.
    names=" $(manifest_names "$root") "
    leaked=""
    for excluded in alpha beta gamma delta; do
      case "$names" in
        *" $excluded "*) leaked="$leaked $excluded" ;;
      esac
    done
    if [ -z "$leaked" ]; then
      ok "[$runner] reconcile: unowned skills stay out of the manifest"
    else
      no "[$runner] reconcile: unowned skills stay out of the manifest" "leaked into:$leaked"
    fi
    case "$names" in
      *" mimisbrunnr-odin-context-memory "*) ok "[$runner] reconcile: owned source skills are retained" ;;
      *) no "[$runner] reconcile: owned source skills are retained" "odin missing from:$names" ;;
    esac
    [ "$(manifest_flag "$root" ai-brain-dump)" = "false" ] \
      && ok "[$runner] reconcile: an operator's installed:false survives the sync" \
      || no "[$runner] reconcile: an operator's installed:false survives the sync" "ai-brain-dump=$(manifest_flag "$root" ai-brain-dump)"
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
    # ai-brain-dump and mimisbrunnr-odin-context-memory both exist in the checkout and are both
    # owned, so both are real clone-and-copy cases rather than synthetic names that would only
    # exercise the warning branch. (ai-terse would also clone, but it is NOT owned — it now belongs
    # in the exclusion case below, not here.)
    root="$(make_root "update-$runner" ai-brain-dump mimisbrunnr-odin-context-memory)"
    printf 'operator edit\n' >"$root/ai-brain-dump/SKILL.md"
    if [ "$runner" = sh ]; then run_sh "$root" >/dev/null 2>&1; else run_ps1 "$root" >/dev/null 2>&1; fi

    if grep -q '^name:' "$root/ai-brain-dump/SKILL.md" 2>/dev/null; then
      ok "[$runner] an installed:true skill is replaced from the clone source"
    else
      no "[$runner] an installed:true skill is replaced from the clone source" "SKILL.md still: $(head -1 "$root/ai-brain-dump/SKILL.md")"
    fi
    if [ -d "$root/mimisbrunnr-odin-context-memory/scripts" ]; then
      ok "[$runner] the copied skill carries its scripts folder"
    else
      no "[$runner] the copied skill carries its scripts folder"
    fi

    # Now the same run with one skill switched off: the off folder must be left exactly as it is.
    # The manifest has to exist before it can be switched off, so this is a first run followed by an
    # edit: that is the operator's actual sequence (install, then turn a skill off).
    root2="$(make_root "update-off-$runner" ai-brain-dump mimisbrunnr-odin-context-memory)"
    printf 'operator edit\n' >"$root2/ai-brain-dump/SKILL.md"
    if [ "$runner" = sh ]; then run_sh "$root2" >/dev/null 2>&1; else run_ps1 "$root2" >/dev/null 2>&1; fi
    printf 'operator edit\n' >"$root2/ai-brain-dump/SKILL.md"
    python3 - "$root2/_mimisbrunnr-shared/installation.json" <<'PYEOF'
import json, sys
path = sys.argv[1]
data = json.load(open(path))
for entry in data["skills"]:
    entry["installed"] = False
json.dump(data, open(path, "w"), indent=2)
PYEOF
    if [ "$runner" = sh ]; then run_sh "$root2" >/dev/null 2>&1; else run_ps1 "$root2" >/dev/null 2>&1; fi
    if [ "$(cat "$root2/ai-brain-dump/SKILL.md")" = "operator edit" ]; then
      ok "[$runner] an installed:false skill is never overwritten"
    else
      no "[$runner] an installed:false skill is never overwritten" "content is now: $(head -1 "$root2/ai-brain-dump/SKILL.md")"
    fi
  done
done

# ---------------------------------------------------------------------------------------------
# 4b. skills outside the owned set are never touched and never listed
# ---------------------------------------------------------------------------------------------

# The case this gate exists for: a target repo carries local-only skills (here `epic-breakdown`, shaped
# like the real one — a folder with a SKILL.md the source has never heard of). The installer must leave
# the folder byte-identical, keep it out of the manifest, and refuse to install it even when a
# hand-edited manifest names it installed:true.
for port in sh; do
  [ "$have_pwsh" = 1 ] && port="sh ps1"
  for runner in $port; do
    root="$(make_root "excluded-$runner" epic-breakdown)"
    printf 'operator content\n' >"$root/epic-breakdown/SKILL.md"
    printf 'operator content\n' >"$root/epic-breakdown/notes.md"
    if [ "$runner" = sh ]; then run_sh "$root" >/dev/null 2>&1; else run_ps1 "$root" >/dev/null 2>&1; fi
    if [ "$(cat "$root/epic-breakdown/SKILL.md")" = "operator content" ] && [ -f "$root/epic-breakdown/notes.md" ]; then
      ok "[$runner] an unowned disk skill is left byte-identical"
    else
      no "[$runner] an unowned disk skill is left byte-identical" "the folder was modified"
    fi
    case " $(manifest_names "$root") " in
      *" epic-breakdown "*) no "[$runner] an unowned disk skill never enters the manifest" "it was listed" ;;
      *) ok "[$runner] an unowned disk skill never enters the manifest" ;;
    esac

    # …and a manifest that was hand-edited to ask for an unowned skill the SOURCE HAS is refused, not
    # honoured. epic-breakdown would prove nothing here — it is absent from the source, so the
    # not-in-source branch would spare it regardless of ownership. ai-terse exists in the source checkout
    # but is not owned, so only the ownership guard stands between it and a copy.
    mkdir -p "$root/ai-terse"
    printf 'operator content\n' >"$root/ai-terse/SKILL.md"
    python3 - "$root/_mimisbrunnr-shared/installation.json" <<'PYEOF'
import json, sys
path = sys.argv[1]
data = json.load(open(path))
data["skills"].append({"name": "ai-terse", "installed": True})
json.dump(data, open(path, "w"), indent=2)
PYEOF
    if [ "$runner" = sh ]; then run_sh "$root" >/dev/null 2>&1; else run_ps1 "$root" >/dev/null 2>&1; fi
    if [ "$(cat "$root/ai-terse/SKILL.md")" = "operator content" ]; then
      ok "[$runner] a hand-edited manifest cannot opt an unowned skill into updates"
    else
      no "[$runner] a hand-edited manifest cannot opt an unowned skill into updates" "the folder was replaced"
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
    # alpha/beta are unowned, so their flags are always empty and comparing them proves nothing.
    # ai-brain-dump is owned: toggling it to false exercises real flag preservation on both ports.
    root="$(make_root "parity-$runner" alpha beta)"
    if [ "$runner" = sh ]; then run_sh "$root" >/dev/null 2>&1; else run_ps1 "$root" >/dev/null 2>&1; fi
    python3 - "$root/_mimisbrunnr-shared/installation.json" <<'PYEOF'
import json, sys
path = sys.argv[1]
data = json.load(open(path))
for entry in data["skills"]:
    if entry["name"] == "ai-brain-dump":
        entry["installed"] = False
json.dump(data, open(path, "w"), indent=2)
PYEOF
    if [ "$runner" = sh ]; then run_sh "$root" >/dev/null 2>&1; else run_ps1 "$root" >/dev/null 2>&1; fi
    names="$(manifest_names "$root")"
    flags="$(manifest_flag "$root" ai-brain-dump)/$(manifest_flag "$root" mimisbrunnr-odin-context-memory)"
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
# 11. an installed skill carries what runs, not what tests it
# ---------------------------------------------------------------------------------------------
#
# The clone source is this checkout, and every mimisbrunnr-* skill here has a tests/ folder, so the
# exclusion is observable without planting anything. A synthetic skill carrying an empty tests/ would
# also prove the case, but it would prove it against a folder the real tree never ships — and the
# regression this exists to catch is someone re-adding a harness dir to a real skill.

check_pruned() {
  local root="$1" runner="$2"
  local odin="$root/mimisbrunnr-odin-context-memory"

  # tests/ gone. ai-understanding and odin both ship one in the source, so a prune that never ran
  # leaves them here.
  for skill in mimisbrunnr-odin-context-memory ai-understanding mimisbrunnr-saga-dossier; do
    if [ -d "$root/$skill/tests" ]; then
      no "[$runner] an installed skill ships without tests/" "$skill/tests survived the install"
      return
    fi
  done

  # What *does* ship. agents/ and references/ are runtime-referenced — odin's SKILL.md tells the agent
  # to read references/read-path.md, and an install without it is a skill documenting a missing file —
  # so the exclusion list must not have grown to cover them.
  for path in SKILL.md AGENTS.md agents scripts references; do
    if [ ! -e "$odin/$path" ]; then
      no "[$runner] an installed skill keeps its runtime files" "odin/$path missing"
      return
    fi
  done
  # ai-understanding renders its template at export time, so assets/ is runtime too.
  if [ ! -e "$root/ai-understanding/assets/UNDERSTANDING.template.md" ]; then
    no "[$runner] an installed skill keeps its runtime files" "ai-understanding/assets template missing"
    return
  fi
  # kvasir's client loads its messages from assets/ at runtime, so a prune that ate the folder would
  # leave every refusal in that skill unable to print (it fails closed, but the skill is still broken).
  if [ ! -e "$root/mimisbrunnr-kvasir-understanding/assets/store-unreachable.md" ]; then
    no "[$runner] an installed skill keeps its runtime files" "kvasir/assets store-unreachable.md missing"
    return
  fi

  # The scripts the skill actually invokes must still be there. A prune that removed `scripts/` would
  # leave a skill whose own SKILL.md names a file that no longer exists.
  if [ ! -e "$root/mimisbrunnr-odin-context-memory/scripts/authority.py" ]; then
    no "[$runner] an installed skill keeps its scripts" "odin/scripts/authority.py missing"
    return
  fi
  if [ ! -e "$root/_mimisbrunnr-shared/scripts/redact.py" ]; then
    no "[$runner] the shared folder still refreshes" "shared redact.py missing"
    return
  fi

  # __pycache__ at any depth, including the shared folder. Recursive on purpose: an operator running
  # the scripts directly inside their install leaves one wherever Python happened to be.
  local stray
  stray="$(find "$root" -type d -name __pycache__ 2>/dev/null | head -5)"
  if [ -n "$stray" ]; then
    no "[$runner] an installed skill ships without __pycache__" "$stray"
    return
  fi

  # And the clone source itself must be untouched: pruning the destination twice, or pruning the
  # source, would make every later run's source differ from this checkout.
  if [ ! -d "$source_repo/.agents/skills/ai-understanding/tests" ]; then
    no "[$runner] pruning leaves the clone source alone" "source tests/ was removed"
    return
  fi

  ok "[$runner] an installed skill carries its runtime files and neither tests/ nor __pycache__/"
}

# A skill marked installed:false is never copied, so the prune must not touch it either — that is the
# one case where a prune running against the wrong path would be visible.
check_pruned_skips_disabled() {
  local root="$1" runner="$2"
  local kept="$root/ai-brain-dump/tests"
  mkdir -p "$kept"
  printf '# local edit\n' >"$kept/keep.py"
  python3 - "$root/_mimisbrunnr-shared/installation.json" <<'PYEOF'
import json, sys
path = sys.argv[1]
data = json.load(open(path))
for entry in data["skills"]:
    if entry["name"] == "ai-brain-dump":
        entry["installed"] = False
json.dump(data, open(path, "w"), indent=2)
PYEOF
  if [ "$runner" = sh ]; then run_sh "$root" >/dev/null 2>&1; else run_ps1 "$root" >/dev/null 2>&1; fi
  if [ ! -f "$kept/keep.py" ]; then
    no "[$runner] a disabled skill is left byte-identical" "ai-brain-dump/tests was pruned"
    return
  fi
  ok "[$runner] a disabled skill is left byte-identical (the prune never sees it)"
}

for port in sh; do
  [ "$have_pwsh" = 1 ] && port="sh ps1"
  for runner in $port; do
    root="$(make_root "prune-$runner")"
    if [ "$runner" = sh ]; then run_sh "$root" >/dev/null 2>&1 || true; else run_ps1 "$root" >/dev/null 2>&1 || true; fi
    check_pruned "$root" "$runner"

    root2="$(make_root "prune-off-$runner")"
    if [ "$runner" = sh ]; then run_sh "$root2" >/dev/null 2>&1 || true; else run_ps1 "$root2" >/dev/null 2>&1 || true; fi
    check_pruned_skips_disabled "$root2" "$runner"
  done
done

# A shipped document that links into a pruned path is a dangling link in every installed copy. This is
# the failure mode the prune *creates* rather than the one it prevents, and it is invisible in the
# repository, where `tests/` is right there: `mimisbrunnr-ymir-bootstrap/README.md` linked
# `[behavioral test guide](tests/README.md)` and every install of that skill shipped a dead link.
#
# Scoped to the two pruned names on purpose. A blanket "no dead links" check would report every skill
# in the repository for unrelated reasons, and a check that is red for reasons outside the change is a
# check that gets deleted.
pruned_link_refs() {
  python3 - "$1" <<'PYEOF'
import re, sys
from pathlib import Path
root = Path(sys.argv[1])
link = re.compile(r"\[[^\]]*\]\(([^)#\s]+)(?:#[^)]*)?\)")
found = []
for doc in sorted([*root.glob("*/SKILL.md"), *root.glob("*/AGENTS.md"),
                   *root.glob("*/README.md"), *root.glob("*/references/*.md")]):
    for number, line in enumerate(doc.read_text(encoding="utf-8").splitlines(), 1):
        for target in link.findall(line):
            if re.match(r"[a-z]+:", target) or target.startswith("/"):
                continue
            head = target.split("/", 1)[0]
            if head in ("tests", "__pycache__") and not (doc.parent / target).exists():
                found.append(f"{doc.relative_to(root)}:{number} -> {target}")
print("\n".join(found))
PYEOF
}

check_no_dangling_pruned_links() {
  local root="$1" runner="$2" refs
  refs="$(pruned_link_refs "$root")"
  if [ -n "$refs" ]; then
    no "[$runner] no installed doc links into a pruned path" "$refs"
    return
  fi
  ok "[$runner] no installed doc links into a pruned path"
}

for port in sh; do
  [ "$have_pwsh" = 1 ] && port="sh ps1"
  for runner in $port; do
    root="$(make_root "links-$runner")"
    if [ "$runner" = sh ]; then run_sh "$root" >/dev/null 2>&1 || true; else run_ps1 "$root" >/dev/null 2>&1 || true; fi
    check_no_dangling_pruned_links "$root" "$runner"
  done
done

# A `__pycache__` cannot be planted through the clone path: it is gitignored, so a clone never
# carries one and an end-to-end assertion about it is vacuously true forever. So the prune is also
# exercised directly, on a tree planted with one — extracted from each installer rather than restated,
# the same way the parity cases read `is_owned_skill` out of the file that ships.
check_prune_function() {
  local runner="$1" tree="$scratch/prune-fn-$runner"
  mkdir -p "$tree/tests" "$tree/scripts/__pycache__" "$tree/agents/openai/nested/__pycache__"
  printf '# harness\n' >"$tree/tests/run_tests.py"
  printf '# runtime\n' >"$tree/scripts/keep.py"
  printf 'cached\n' >"$tree/scripts/__pycache__/keep.cpython-311.pyc"
  printf 'cached\n' >"$tree/agents/openai/nested/__pycache__/x.cpython-311.pyc"
  printf '# kept\n' >"$tree/agents/openai.yaml"

  if [ "$runner" = sh ]; then
    bash -c "$(extract_prune_sh)
      prune_non_shippable \"\$1\"" _ "$tree"
  else
    pwsh -NoProfile -Command "$(extract_prune_ps1)
      Remove-NonShippable -Dest '$tree'" >/dev/null
  fi

  if [ -d "$tree/tests" ]; then
    no "[$runner] prune removes tests/" "tests/ survived"
    return
  fi
  local stray
  stray="$(find "$tree" -type d -name __pycache__ 2>/dev/null | head -3)"
  if [ -n "$stray" ]; then
    no "[$runner] prune removes __pycache__ at any depth" "$stray"
    return
  fi
  # The runtime files the prune is *not* allowed to touch — the whole reason the list is two names.
  for path in scripts/keep.py agents/openai.yaml; do
    if [ ! -f "$tree/$path" ]; then
      no "[$runner] prune keeps runtime files" "$path was removed"
      return
    fi
  done
  ok "[$runner] prune removes tests/ and nested __pycache__/ and keeps everything else"
}

extract_prune_sh() {
  awk '/^prune_non_shippable\(\) \{/,/^\}/' "$installer_sh"
}

extract_prune_ps1() {
  awk '/^function Remove-NonShippable \{/,/^\}/' "$installer_ps1"
}

check_prune_function sh
[ "$have_pwsh" = 1 ] && check_prune_function ps1 || echo "  skip pwsh not installed — prune function not run on the PowerShell port (not a failure)"

# ---------------------------------------------------------------------------------------------
# 13. WET: a symlink that points inside the clone is copied as what it points at
# ---------------------------------------------------------------------------------------------
#
# The point of the whole mechanism: an installed skill is self-contained, so it can be zipped and
# imported into a runtime that has no repository to resolve a link against. A link copied as a link
# ships a pointer into a path the consumer does not have.
#
# The fixture is upstream's own, from its PR notes — a skill file link, a skill folder link, an
# outside link, a dangling link and a `self -> .` cycle — because those five together are the whole
# decision space: materialise, or keep the link, or stop.
check_wet() {
  local runner="$1" tree="$2"
  mkdir -p "$tree/src/sk/refs"
  printf 'REAL CONTENT\n' >"$tree/src/sk/refs/shared.md"
  mkdir -p "$tree/src/sk/deep"
  ln -s refs/shared.md "$tree/src/sk/REF.md"       # in-tree file link  -> materialise
  ln -s refs "$tree/src/sk/refs-link"              # in-tree folder link -> materialise
  ln -s ../refs "$tree/src/sk/deep/up.md"          # in-tree, relative .. -> materialise
  ln -s /etc/hosts "$tree/src/sk/outside.md"       # outside the repo     -> keep as a link
  ln -s /nowhere-at-all "$tree/src/sk/dangling.md" # no target             -> keep as a link
  ln -s . "$tree/src/sk/self"                      # cycle                -> keep as a link

  if [ "$runner" = sh ]; then
    bash -c "$(extract_wet_sh)
      CLONE_ROOT=\"\$(cd -P \"\$1\" && pwd)\"
      copy_wet \"\$2\" \"\$3\"" _ "$tree/src" "$tree/src/sk" "$tree/dst/sk"
  else
    pwsh -NoProfile -File "$repo_root/scripts/_wet_probe.ps1" "$tree/src" "$tree/src/sk" "$tree/dst/sk" \
      >"$scratch/wet.out" 2>&1 || { no "[$runner] WET copy" "$(cat "$scratch/wet.out")"; return; }
  fi

  local f
  for f in REF.md refs-link deep/up.md; do
    if [ -L "$tree/dst/sk/$f" ]; then
      no "[$runner] WET copy materialises an in-clone link" "$f stayed a link"
      return
    fi
  done
  [ -f "$tree/dst/sk/REF.md" ] && grep -q 'REAL CONTENT' "$tree/dst/sk/REF.md" || {
    no "[$runner] WET copy materialises an in-clone link" "REF.md content wrong"; return; }
  [ -f "$tree/dst/sk/refs-link/shared.md" ] || {
    no "[$runner] WET copy materialises an in-clone folder link" "refs-link/shared.md missing"; return; }

  for f in outside.md dangling.md self; do
    if [ ! -L "$tree/dst/sk/$f" ]; then
      no "[$runner] WET copy keeps an outside, dangling or cyclic link" "$f was materialised"
      return
    fi
  done
  ok "[$runner] WET: in-clone links materialised, outside/dangling/cyclic kept as links"
}

extract_wet_sh() {
  awk '/^real_path\(\) \{/,/^\}/' "$installer_sh"
  awk '/^inside_clone\(\) \{/,/^\}/' "$installer_sh"
  awk '/^copy_wet\(\) \{/,/^\}/' "$installer_sh"
}

check_wet sh "$(mktemp -d)"
if [ "$have_pwsh" = 1 ]; then
  check_wet ps1 "$(mktemp -d)"
else
  echo "  skip pwsh not installed — the WET case ran on the bash port only (not a failure)"
fi

# An inherited CLONE_ROOT must not widen "inside the clone". Run with a value pointing at the
# fixture's own destination: materialising a link to a *destination* path is the failure this stops.
check_clone_root_reset() {
  local tree="$1" out
  mkdir -p "$tree/src/sk"; printf 'REAL\n' >"$tree/src/sk/real.md"
  ln -s ../../escape.md "$tree/src/sk/link.md"
  printf 'ESCAPED\n' >"$tree/escape.md"
  out="$(CLONE_ROOT="$tree" bash -c "$(extract_wet_sh)
    CLONE_ROOT=\"\$(cd -P \"\$1\" && pwd)\"
    copy_wet \"\$2\" \"\$3\" && echo done" _ "$tree/src" "$tree/src/sk" "$tree/dst/sk" 2>&1)"
  if [ -e "$tree/dst/sk/link.md" ] && [ ! -L "$tree/dst/sk/link.md" ]; then
    no "an inherited CLONE_ROOT cannot widen the clone boundary" "a link outside the clone was materialised"
    return
  fi
  ok "an inherited CLONE_ROOT cannot widen the clone boundary"
}
check_clone_root_reset "$(mktemp -d)"

# ---------------------------------------------------------------------------------------------
# 12. global install (-g)
#
# Each case gets its own throwaway HOME with every location pinned (HOME, SMOOTH_MIMISBRUNNR_HOME,
# CODEX_HOME, CLAUDE_CONFIG_DIR, XDG_CONFIG_HOME) and SKILLS_ROOT cleared, so a red run cannot touch the
# operator's real ~/.claude, ~/.codex, ~/.config/opencode or either store. The clone source is a fixture
# repository built here, not the checkout: -g reads rules from .github/instructions and hooks from
# .agents/hooks, and this repo owns neither, so the rule/hook machinery is driven with synthetic owned names
# through SKILLS_TEST_OWNED_RULES_EXTRA / SKILLS_TEST_OWNED_HOOKS_EXTRA (production never sets them).
# ---------------------------------------------------------------------------------------------

g_source="$scratch/g-source"
make_g_source() {
  local d="$g_source"
  mkdir -p "$d/.agents/skills/mimisbrunnr-demo-skill/scripts" "$d/.agents/skills/mimisbrunnr-demo-skill/tests" \
    "$d/.agents/skills/ai-brain-dump" "$d/.agents/skills/unowned-local" \
    "$d/.agents/skills/_mimisbrunnr-shared/scripts" "$d/.github/instructions/sub" "$d/.agents/hooks"
  printf '# demo\n' >"$d/.agents/skills/mimisbrunnr-demo-skill/SKILL.md"
  printf 'echo demo\n' >"$d/.agents/skills/mimisbrunnr-demo-skill/scripts/run.sh"
  printf 'assert 1\n' >"$d/.agents/skills/mimisbrunnr-demo-skill/tests/t.py"
  printf '# brain\n' >"$d/.agents/skills/ai-brain-dump/SKILL.md"
  printf '# local\n' >"$d/.agents/skills/unowned-local/SKILL.md"
  printf 'x = 1\n' >"$d/.agents/skills/_mimisbrunnr-shared/scripts/shared.py"
  printf -- "---\ndescription: 'Top rule'\napplyTo: '**'\n---\n# Top\n" >"$d/.github/instructions/test-rule.instructions.md"
  printf -- '---\ndescription: Cs rule\napplyTo: "**/*.cs"\n---\n# Cs\n' >"$d/.github/instructions/sub/other.instructions.md"
  printf '#!/bin/sh\necho test\n' >"$d/.agents/hooks/test-hook.sh"
  printf '#!/bin/sh\necho other\n' >"$d/.agents/hooks/other-hook.sh"
  chmod +x "$d/.agents/hooks/test-hook.sh" "$d/.agents/hooks/other-hook.sh"
  git -C "$d" init -q -b main
  git -C "$d" add -A
  git -C "$d" -c user.name=harness -c user.email=harness@example.invalid commit -q -m fixture
}
make_g_source

# g_home CASE RUNNER — a fresh HOME with the three tools present and Codex's own .system/ in place.
g_home() {
  local gh="$scratch/g-$1-$2"
  rm -rf "$gh"
  mkdir -p "$gh/.claude" "$gh/.codex/skills/.system" "$gh/.config/opencode"
  printf 'keep\n' >"$gh/.codex/skills/.system/keep"
  printf '%s\n' "$gh"
}

# _run_g RUNNER HOME RULES HOOKS ARGS... — ARGS use bash spelling; UNINSTALL becomes the port's own.
_run_g() {
  local runner="$1" gh="$2" rules="$3" hooks="$4" a
  shift 4
  local args=()
  for a in "$@"; do
    case "$a" in
      UNINSTALL) if [ "$runner" = sh ]; then args+=(--uninstall); else args+=(-Uninstall); fi ;;
      *) args+=("$a") ;;
    esac
  done
  local rc=0
  (
    # Run from the scratch HOME, never the checkout. Without SKILLS_ROOT the per-repo install searches the
    # current directory's .agents/skills, so a refusal that regressed (-r without -g falling through to
    # per-repo mode) would otherwise install the fixture's skills over this repository's own.
    cd "$gh"
    unset SKILLS_ROOT
    export HOME="$gh" SMOOTH_MIMISBRUNNR_HOME="${G_STORE:-$gh/store}" CODEX_HOME="$gh/.codex" CLAUDE_CONFIG_DIR="$gh/.claude" \
      XDG_CONFIG_HOME="$gh/.config" SKILLS_REPO="$g_source" SKILLS_BRANCH=main \
      SKILLS_TEST_OWNED_RULES_EXTRA="$rules" SKILLS_TEST_OWNED_HOOKS_EXTRA="$hooks"
    if [ "$runner" = sh ]; then
      bash "$installer_sh" ${args[@]+"${args[@]}"}
    else
      pwsh -NoProfile -File "$installer_ps1" ${args[@]+"${args[@]}"}
    fi
  ) >"$scratch/out" 2>"$scratch/err" || rc=$?
  return "$rc"
}
run_g() { local r="$1" gh="$2"; shift 2; _run_g "$r" "$gh" "" "" "$@"; }
run_gx() { _run_g "$@"; }

# G_STORE overrides the store path for one call; set it, call, and clear it (a VAR=x prefix on a function leaks in bash 3.2).
G_STORE=""
g_rules="test-rule.instructions.md sub/other.instructions.md"
g_hooks="test-hook.sh other-hook.sh"

pj() { python3 -c "import json,sys; d=json.load(open(sys.argv[1])); print(eval(sys.argv[2]))" "$1" "$2"; }
# norm_json FILE HOME — the file's JSON without its timestamps, key-sorted, with the scratch HOME masked.
norm_json() {
  python3 -c "
import json, re, sys
d = json.load(open(sys.argv[1]))
d.pop('updated', None)
text = json.dumps(d, sort_keys=True).replace(sys.argv[2], '@H')
# A snapshot path carries the second it was taken in; two ports run seconds apart.
print(re.sub(r'\d{8}-\d{6}', '@T', text))
" "$1" "$2"
}
set_flag() {
  python3 -c "
import json, sys
p, key, name, val = sys.argv[1:5]
d = json.load(open(p))
for e in d[key]:
    if e['name'] == name:
        e['installed'] = (val == 'true')
json.dump(d, open(p, 'w'), indent=2)
" "$@"
}
set_harness() {
  python3 -c "
import json, sys
p, name, val = sys.argv[1:4]
d = json.load(open(p))
d['harnesses'][name] = (val == 'true')
json.dump(d, open(p, 'w'), indent=2)
" "$@"
}
sum_of() { cksum <"$1" | cut -d' ' -f1,2; }
count_glob() { local n=0 f; for f in "$@"; do [ -e "$f" ] && n=$((n + 1)); done; echo "$n"; }
err_tail() { cat "$scratch/out" "$scratch/err" | tail -3 | tr '\n' ' '; }
logged() { cat "$scratch/out" "$scratch/err"; }

g_runners="sh"
[ "$have_pwsh" = 1 ] && g_runners="sh ps1"

check_g_first_run() {
  local r="$1" gh store t n
  gh="$(g_home first "$r")"; store="$gh/store"
  if ! run_g "$r" "$gh" -g; then no "[$r] -g first run exits 0" "$(err_tail)"; return; fi
  for t in .claude/skills .codex/skills .config/opencode/skills; do
    for n in mimisbrunnr-demo-skill ai-brain-dump _mimisbrunnr-shared; do
      if [ "$(readlink "$gh/$t/$n" 2>/dev/null)" != "$store/skills/$n" ]; then
        no "[$r] -g links $n into $t" "got: $(readlink "$gh/$t/$n" 2>&1)"; return
      fi
    done
    if [ -e "$gh/$t/unowned-local" ] || [ -e "$store/skills/unowned-local" ]; then
      no "[$r] -g never carries an unowned skill" "unowned-local reached $t or the store"; return
    fi
  done
  if [ ! -f "$store/skills/mimisbrunnr-demo-skill/scripts/run.sh" ] || [ -e "$store/skills/mimisbrunnr-demo-skill/tests" ]; then
    no "[$r] -g stores a pruned, self-contained skill" "run.sh missing or tests/ shipped"; return
  fi
  if [ "$(pj "$store/installation.json" "list(d)")" != "['updated', 'harnesses', 'skills', 'rules', 'hooks', 'links']" ]; then
    no "[$r] -g manifest keys are in the fixed order" "$(pj "$store/installation.json" "list(d)")"; return
  fi
  if [ "$(pj "$store/installation.json" "all(l.get('enabled') for l in d['links']) and len(d['links']) == 9")" != "True" ]; then
    no "[$r] -g records nine enabled links (3 per tool)" "$(pj "$store/installation.json" "len(d['links'])")"; return
  fi
  if [ "$(pj "$store/installation.json" "d['harnesses']")" != "{'claude-code': True, 'codex': True, 'opencode': True}" ]; then
    no "[$r] -g defaults every tool to on"; return
  fi
  if [ -e "$gh/.codex/AGENTS.md" ] || [ -e "$gh/.codex/hooks.json" ] || [ -e "$gh/.config/opencode/opencode.json" ]; then
    no "[$r] -g creates no config file when there is no rule or hook to wire"; return
  fi
  if [ "$(cat "$gh/.codex/skills/.system/keep")" != "keep" ]; then no "[$r] -g leaves Codex's .system/ alone"; return; fi
  ok "[$r] -g: store, pruned skills, shared folder and 9 enabled links; no config file created; .system/ intact"
}

check_g_idempotent() {
  local r="$1" gh store before after
  gh="$(g_home idem "$r")"; store="$gh/store"
  run_g "$r" "$gh" -g || { no "[$r] -g first run for idempotency" "$(err_tail)"; return; }
  before="$(pj "$store/installation.json" "sorted((l['type'], l['path'], l.get('detail')) for l in d['links'])")"
  run_g "$r" "$gh" -g || { no "[$r] -g re-run exits 0" "$(err_tail)"; return; }
  after="$(pj "$store/installation.json" "sorted((l['type'], l['path'], l.get('detail')) for l in d['links'])")"
  if [ "$before" != "$after" ]; then no "[$r] -g re-run converges: same link records"; return; fi
  if logged | grep -q WARNING; then no "[$r] -g re-run raises no warning" "$(logged | grep WARNING | head -2)"; return; fi
  ok "[$r] -g re-run converges: identical link records, no warnings"
}

check_g_default_leaves_config() {
  local r="$1" gh before_o before_a before_h
  gh="$(g_home noedit "$r")"
  printf '{ // a comment makes this invalid JSON\n  "instructions": []\n}\n' >"$gh/.config/opencode/opencode.json"
  printf 'operator\n' >"$gh/.codex/AGENTS.md"
  printf '{ not json' >"$gh/.codex/hooks.json"
  before_o="$(sum_of "$gh/.config/opencode/opencode.json")"; before_a="$(sum_of "$gh/.codex/AGENTS.md")"; before_h="$(sum_of "$gh/.codex/hooks.json")"
  if ! run_g "$r" "$gh" -g; then no "[$r] -g with nothing to wire ignores unparseable operator config" "$(err_tail)"; return; fi
  if [ "$(sum_of "$gh/.config/opencode/opencode.json")" != "$before_o" ] || [ "$(sum_of "$gh/.codex/AGENTS.md")" != "$before_a" ] \
     || [ "$(sum_of "$gh/.codex/hooks.json")" != "$before_h" ]; then
    no "[$r] -g with nothing to wire leaves opencode.json, AGENTS.md and hooks.json byte-identical"; return
  fi
  if [ "$(count_glob "$gh"/.config/opencode/opencode.json.smooth-mimisbrunnr-* "$gh"/.codex/AGENTS.md.smooth-mimisbrunnr-* "$gh"/.codex/hooks.json.smooth-mimisbrunnr-*)" != 0 ]; then
    no "[$r] -g with nothing to wire takes no snapshot"; return
  fi
  ok "[$r] -g with nothing to wire never parses or touches operator config (invalid JSON tolerated, no snapshot)"
}

check_g_absent_tool() {
  local r="$1" gh
  gh="$scratch/g-absent-$r"; rm -rf "$gh"; mkdir -p "$gh/.claude"
  if ! run_g "$r" "$gh" -g; then no "[$r] -g with only Claude present exits 0" "$(err_tail)"; return; fi
  if [ ! -L "$gh/.claude/skills/ai-brain-dump" ]; then no "[$r] -g wires the tool that exists"; return; fi
  if [ -e "$gh/.codex" ] || [ -e "$gh/.config" ]; then no "[$r] -g never creates a tool's config folder" "$(ls -a "$gh")"; return; fi
  ok "[$r] -g wires only the tools that exist and creates no tool folder"
}

check_g_foreign() {
  local r="$1" gh elsewhere
  gh="$(g_home foreign "$r")"; elsewhere="$scratch/g-elsewhere-$r"
  mkdir -p "$elsewhere" "$gh/.claude/skills/ai-brain-dump"
  printf 'mine\n' >"$gh/.claude/skills/ai-brain-dump/own.txt"
  ln -s "$elsewhere" "$gh/.codex/skills/mimisbrunnr-demo-skill"
  run_g "$r" "$gh" -g || { no "[$r] -g beside foreign entries exits 0" "$(err_tail)"; return; }
  if [ ! -f "$gh/.claude/skills/ai-brain-dump/own.txt" ] || [ -L "$gh/.claude/skills/ai-brain-dump" ]; then
    no "[$r] -g leaves a real directory at a skill path alone"; return
  fi
  if [ "$(readlink "$gh/.codex/skills/mimisbrunnr-demo-skill")" != "$elsewhere" ]; then
    no "[$r] -g leaves a link to somewhere else alone"; return
  fi
  if [ "$(pj "$gh/store/installation.json" "any(l['path'].endswith('claude/skills/ai-brain-dump') or l['path'].endswith('codex/skills/mimisbrunnr-demo-skill') for l in d['links'])")" != "False" ]; then
    no "[$r] -g records neither the real directory nor the foreign link"; return
  fi
  if ! logged | grep -q WARNING; then no "[$r] -g warns about both foreign entries"; return; fi
  ok "[$r] -g keeps a real directory and a foreign link, warns, and records neither"
}

check_g_refusals() {
  local r="$1" gh rc=0
  gh="$(g_home refuse "$r")"
  # run_g clears SKILLS_ROOT, so force it here the way an operator would.
  ( cd "$gh"; export HOME="$gh" SMOOTH_MIMISBRUNNR_HOME="$gh/store" SKILLS_ROOT="$gh/.claude" SKILLS_REPO="$g_source" SKILLS_BRANCH=main
    if [ "$r" = sh ]; then bash "$installer_sh" -g; else pwsh -NoProfile -File "$installer_ps1" -g; fi ) >/dev/null 2>"$scratch/err" || rc=$?
  if [ "$rc" = 0 ] || [ -e "$gh/store" ]; then no "[$r] -g refuses SKILLS_ROOT and writes nothing" "rc=$rc"; return; fi
  if run_g "$r" "$gh" UNINSTALL; then no "[$r] --uninstall without -g is refused"; return; fi
  if run_g "$r" "$gh" -r; then no "[$r] -r without -g is refused"; return; fi
  if run_g "$r" "$gh" -h; then no "[$r] -h without -g is refused"; return; fi
  if [ -e "$gh/store" ]; then no "[$r] refusals write nothing"; return; fi
  ok "[$r] -g+SKILLS_ROOT, bare --uninstall, bare -r and bare -h are refused and write nothing"
}

check_g_corrupt_manifest() {
  local r="$1" gh bad
  gh="$(g_home corrupt "$r")"; mkdir -p "$gh/store"
  for bad in '{broken' ''; do
    printf '%s' "$bad" >"$gh/store/installation.json"
    if run_g "$r" "$gh" -g; then no "[$r] -g refuses an unreadable manifest (${bad:-empty})"; return; fi
    if [ "$(cat "$gh/store/installation.json")" != "$bad" ]; then no "[$r] -g leaves an unreadable manifest untouched"; return; fi
  done
  ok "[$r] -g refuses a corrupt or empty manifest and leaves it untouched"
}

check_g_skill_flag() {
  local r="$1" gh store
  gh="$(g_home flag "$r")"; store="$gh/store"
  run_g "$r" "$gh" -g || { no "[$r] -g for the skill flag case" "$(err_tail)"; return; }
  set_flag "$store/installation.json" skills mimisbrunnr-demo-skill false
  printf 'local edit\n' >"$store/skills/mimisbrunnr-demo-skill/edited.txt"
  rm "$gh/.claude/skills/mimisbrunnr-demo-skill"
  run_g "$r" "$gh" -g || { no "[$r] -g re-run with a skill switched off" "$(err_tail)"; return; }
  if [ ! -f "$store/skills/mimisbrunnr-demo-skill/edited.txt" ]; then no "[$r] installed:false skill is not refreshed (local edit survives)"; return; fi
  if [ -e "$gh/.claude/skills/mimisbrunnr-demo-skill" ]; then no "[$r] installed:false skill is not re-linked"; return; fi
  if [ ! -L "$gh/.codex/skills/mimisbrunnr-demo-skill" ]; then no "[$r] installed:false skill keeps an existing link"; return; fi
  ok "[$r] installed:false skill: store copy not refreshed, not re-linked, existing link kept"
}

check_g_tool_off() {
  local r="$1" gh store
  gh="$(g_home off "$r")"; store="$gh/store"
  run_g "$r" "$gh" -g || { no "[$r] -g for the tool-off case" "$(err_tail)"; return; }
  ln -s "$store/skills/ai-brain-dump" "$gh/.codex/skills/unrecorded-link"
  set_harness "$store/installation.json" codex false
  run_g "$r" "$gh" -g || { no "[$r] -g with codex off" "$(err_tail)"; return; }
  if [ -n "$(find "$gh/.codex/skills" -maxdepth 1 -type l 2>/dev/null)" ]; then
    no "[$r] codex off removes every store link, including an unrecorded one" "$(find "$gh/.codex/skills" -maxdepth 1 -type l)"; return
  fi
  if [ ! -L "$gh/.claude/skills/ai-brain-dump" ] || [ ! -L "$gh/.config/opencode/skills/ai-brain-dump" ]; then
    no "[$r] codex off leaves the other tools wired"; return
  fi
  if [ "$(cat "$gh/.codex/skills/.system/keep")" != "keep" ]; then no "[$r] codex off leaves .system/ alone"; return; fi
  if [ "$(pj "$store/installation.json" "[l['enabled'] for l in d['links'] if l['type'] == 'codex-skill'][0]")" != "False" ]; then
    no "[$r] a link the installer unwired is recorded with enabled:false"; return
  fi
  set_harness "$store/installation.json" codex true
  run_g "$r" "$gh" -g || { no "[$r] -g with codex back on" "$(err_tail)"; return; }
  if [ ! -L "$gh/.codex/skills/ai-brain-dump" ]; then no "[$r] codex back on rewires it"; return; fi
  if [ "$(pj "$store/installation.json" "all(l['enabled'] for l in d['links'])")" != "True" ]; then
    no "[$r] re-enabling recomputes enabled:true"; return
  fi
  ok "[$r] a tool switched off is cleaned (unrecorded link too), others stay wired, enabled follows the disk, back on rewires"
}

check_g_uninstall() {
  local r="$1" gh store
  gh="$(g_home uninstall "$r")"; store="$gh/store"
  mkdir -p "$gh/.claude/skills/mine"
  run_g "$r" "$gh" -g || { no "[$r] -g before uninstall" "$(err_tail)"; return; }
  set_harness "$store/installation.json" codex false
  if ! run_g "$r" "$gh" UNINSTALL -g; then no "[$r] -g --uninstall exits 0" "$(err_tail)"; return; fi
  if [ -n "$(find "$gh/.claude/skills" "$gh/.codex/skills" "$gh/.config/opencode/skills" -maxdepth 1 -type l 2>/dev/null)" ]; then
    no "[$r] --uninstall removes every recorded link"; return
  fi
  if [ ! -d "$gh/.claude/skills/mine" ] || [ "$(cat "$gh/.codex/skills/.system/keep")" != "keep" ]; then
    no "[$r] --uninstall leaves the operator's folders and .system/ alone"; return
  fi
  if [ ! -d "$store/skills/ai-brain-dump" ]; then no "[$r] --uninstall leaves the store in place"; return; fi
  if [ "$(pj "$store/installation.json" "d['links']")" != "[]" ] || [ "$(pj "$store/installation.json" "d['harnesses']['codex']")" != "False" ]; then
    no "[$r] --uninstall clears the link history but keeps the operator's flags"; return
  fi
  ok "[$r] --uninstall removes exactly the recorded links, keeps the store, folders, .system/ and the operator's flags"
}

check_g_categories() {
  local r="$1" gh store skills_before
  gh="$(g_home cat "$r")"; store="$gh/store"
  run_g "$r" "$gh" -g -s || { no "[$r] -g -s" "$(err_tail)"; return; }
  if [ "$(pj "$store/installation.json" "list(d)")" != "['updated', 'harnesses', 'skills', 'links']" ]; then
    no "[$r] -g -s writes no rules or hooks list" "$(pj "$store/installation.json" "list(d)")"; return
  fi
  skills_before="$(pj "$store/installation.json" "d['skills']")"
  run_g "$r" "$gh" -g -r || { no "[$r] -g -r" "$(err_tail)"; return; }
  if [ "$(pj "$store/installation.json" "list(d)")" != "['updated', 'harnesses', 'skills', 'rules', 'links']" ] \
     || [ "$(pj "$store/installation.json" "d['skills']")" != "$skills_before" ]; then
    no "[$r] -g -r adds the rules list and carries skills forward" "$(pj "$store/installation.json" "list(d)")"; return
  fi
  run_g "$r" "$gh" -g -h || { no "[$r] -g -h" "$(err_tail)"; return; }
  if [ "$(pj "$store/installation.json" "list(d)")" != "['updated', 'harnesses', 'skills', 'rules', 'hooks', 'links']" ]; then
    no "[$r] -g -h adds the hooks list"; return
  fi
  if [ "$(pj "$store/installation.json" "(d['rules'], d['hooks'])")" != "([], [])" ]; then
    no "[$r] production owns no rules or hooks" "$(pj "$store/installation.json" "(d['rules'], d['hooks'])")"; return
  fi
  ok "[$r] category switches reconcile only their own list and carry the others forward; production lists are empty"
}

check_g_shared_resolves() {
  local r="$1" gh
  gh="$(g_home shared "$r")"
  run_g "$r" "$gh" -g || { no "[$r] -g for the shared-folder case" "$(err_tail)"; return; }
  # A skill resolves ../_mimisbrunnr-shared against the folder it was found in, so the tool's skills
  # folder must hold the shared folder as well - not only the store.
  if [ ! -f "$gh/.claude/skills/_mimisbrunnr-shared/scripts/shared.py" ] || [ ! -f "$gh/.codex/skills/_mimisbrunnr-shared/scripts/shared.py" ] \
     || [ ! -f "$gh/.config/opencode/skills/_mimisbrunnr-shared/scripts/shared.py" ]; then
    no "[$r] every tool's skills folder reaches the shared scripts"; return
  fi
  ok "[$r] the shared folder is linked beside the skills, in all three tools"
}

check_g_rules() {
  local r="$1" gh store a o sa so
  gh="$(g_home rules "$r")"; store="$gh/store"
  printf 'operator text\n' >"$gh/.codex/AGENTS.md"
  printf '{\n  "keep": 1,\n  "instructions": [\n    "mine.md"\n  ]\n}\n' >"$gh/.config/opencode/opencode.json"
  cp "$gh/.config/opencode/opencode.json" "$scratch/orig-opencode-$r"
  run_gx "$r" "$gh" "$g_rules" "" -g -r || { no "[$r] -g -r with rules" "$(err_tail)"; return; }
  if [ ! -f "$store/rules/test-rule.instructions.md" ] || [ ! -f "$store/rules/sub/other.instructions.md" ]; then
    no "[$r] rules are stored with their relative paths"; return
  fi
  if [ "$(readlink "$gh/.claude/rules/sub/other.instructions.md")" != "$store/rules/sub/other.instructions.md" ] \
     || [ "$(readlink "$gh/.claude/rules/test-rule.instructions.md")" != "$store/rules/test-rule.instructions.md" ]; then
    no "[$r] Claude rules are linked, subfolders kept"; return
  fi
  a="$gh/.codex/AGENTS.md"
  if [ "$(head -1 "$a")" != "operator text" ] || ! grep -q 'mimisbrunnr-owned-global-instructions' "$a" \
     || ! grep -q 'end of mimisbrunnr install-skills global rules' "$a" || ! grep -qF "$store/rules/test-rule.instructions.md" "$a" \
     || ! grep -q 'Read at the start of every task' "$a" || ! grep -qF 'When editing files matching `**/*.cs`' "$a"; then
    no "[$r] Codex gets a delimited index block after the operator's text, grouped by scope" "$(head -12 "$a")"; return
  fi
  o="$gh/.config/opencode/opencode.json"
  if [ "$(pj "$o" "(d['keep'], d['instructions'][0], sorted(d['instructions'][1:]))")" != "(1, 'mine.md', ['$store/rules/sub/other.instructions.md', '$store/rules/test-rule.instructions.md'])" ]; then
    no "[$r] OpenCode gets one entry per rule beside the operator's" "$(pj "$o" "d")"; return
  fi
  if [ "$(cat "$(ls "$a".smooth-mimisbrunnr-[0-9]*)")" != "operator text" ] || ! cmp -s "$o".smooth-mimisbrunnr-[0-9]* "$scratch/orig-opencode-$r"; then
    no "[$r] the pre-existing AGENTS.md and opencode.json are snapshotted once, unmodified"; return
  fi
  sa="$(sum_of "$a")"; so="$(sum_of "$o")"
  run_gx "$r" "$gh" "$g_rules" "" -g -r || { no "[$r] -g -r re-run" "$(err_tail)"; return; }
  if [ "$(sum_of "$a")" != "$sa" ] || [ "$(sum_of "$o")" != "$so" ]; then no "[$r] -g -r re-run changes nothing"; return; fi
  if [ "$(count_glob "$a".smooth-mimisbrunnr-[0-9]*)" != 1 ] || [ "$(count_glob "$o".smooth-mimisbrunnr-[0-9]*)" != 1 ]; then
    no "[$r] a re-run never snapshots our own block"; return
  fi
  ok "[$r] rules: stored, linked for Claude, indexed for Codex, listed per file for OpenCode; snapshotted once; re-run is a no-op"
}

check_g_rule_flag() {
  local r="$1" gh store
  gh="$(g_home ruleflag "$r")"; store="$gh/store"
  printf '{\n  "instructions": []\n}\n' >"$gh/.config/opencode/opencode.json"
  run_gx "$r" "$gh" "$g_rules" "" -g -r || { no "[$r] -g -r for the rule flag case" "$(err_tail)"; return; }
  set_flag "$store/installation.json" rules sub/other.instructions.md false
  run_gx "$r" "$gh" "$g_rules" "" -g -r || { no "[$r] -g -r with a rule off" "$(err_tail)"; return; }
  if [ -e "$gh/.claude/rules/sub/other.instructions.md" ] || grep -qF 'other.instructions' "$gh/.codex/AGENTS.md" \
     || [ "$(pj "$gh/.config/opencode/opencode.json" "len(d['instructions'])")" != 1 ]; then
    no "[$r] installed:false rule is unwired from Claude, Codex and OpenCode"; return
  fi
  if [ ! -f "$store/rules/sub/other.instructions.md" ]; then no "[$r] installed:false rule keeps its store copy"; return; fi
  set_flag "$store/installation.json" rules sub/other.instructions.md true
  run_gx "$r" "$gh" "$g_rules" "" -g -r || { no "[$r] -g -r with the rule back on" "$(err_tail)"; return; }
  if [ ! -L "$gh/.claude/rules/sub/other.instructions.md" ] || ! grep -qF 'other.instructions' "$gh/.codex/AGENTS.md" \
     || [ "$(pj "$gh/.config/opencode/opencode.json" "len(d['instructions'])")" != 2 ]; then
    no "[$r] re-enabling a rule rewires it"; return
  fi
  ok "[$r] a rule switched off is unwired everywhere and keeps its store copy; back on rewires it"
}

check_g_hooks() {
  local r="$1" gh store h before
  gh="$(g_home hooks "$r")"; store="$gh/store"; h="$gh/.codex/hooks.json"
  printf '{\n  "hooks": {\n    "UserPromptSubmit": [\n      {\n        "hooks": [\n          {\n            "command": "/opt/gk/hook.sh",\n            "type": "command"\n          }\n        ]\n      }\n    ]\n  }\n}\n' >"$h"
  cp "$h" "$scratch/orig-hooks-$r"
  run_gx "$r" "$gh" "" "$g_hooks" -g -h || { no "[$r] -g -h with hooks" "$(err_tail)"; return; }
  if [ ! -x "$store/hooks/test-hook.sh" ] && [ "$r" = sh ]; then no "[$r] a stored hook keeps its exec bit"; return; fi
  if [ "$(pj "$h" "len(d['hooks']['UserPromptSubmit'])")" != 2 ] \
     || [ "$(pj "$h" "d['hooks']['UserPromptSubmit'][0]['hooks'][0]['command']")" != "/opt/gk/hook.sh" ]; then
    no "[$r] the operator's own hook group is kept first and untouched" "$(pj "$h" "d")"; return
  fi
  if [ "$(pj "$h" "sorted(x['command'] for x in d['hooks']['UserPromptSubmit'][1]['hooks'])")" != "['$store/hooks/other-hook.sh', '$store/hooks/test-hook.sh']" ] \
     || [ "$(pj "$h" "'matcher' in d['hooks']['UserPromptSubmit'][1]")" != "False" ]; then
    no "[$r] hooks register as absolute store commands under UserPromptSubmit, no matcher" "$(pj "$h" "d")"; return
  fi
  if ! cmp -s "$h".smooth-mimisbrunnr-[0-9]* "$scratch/orig-hooks-$r"; then no "[$r] hooks.json is snapshotted once, unmodified"; return; fi
  before="$(sum_of "$h")"
  run_gx "$r" "$gh" "" "$g_hooks" -g -h || { no "[$r] -g -h re-run" "$(err_tail)"; return; }
  if [ "$(sum_of "$h")" != "$before" ] || [ "$(count_glob "$h".smooth-mimisbrunnr-[0-9]*)" != 1 ]; then
    no "[$r] hooks re-run adds no duplicate and no second snapshot"; return
  fi
  set_flag "$store/installation.json" hooks other-hook.sh false
  run_gx "$r" "$gh" "" "$g_hooks" -g -h || { no "[$r] -g -h with a hook off" "$(err_tail)"; return; }
  if [ "$(pj "$h" "[x['command'] for x in d['hooks']['UserPromptSubmit'][1]['hooks']]")" != "['$store/hooks/test-hook.sh']" ] \
     || [ ! -f "$store/hooks/other-hook.sh" ]; then
    no "[$r] installed:false hook is unregistered and keeps its store copy"; return
  fi
  set_harness "$store/installation.json" codex false
  run_gx "$r" "$gh" "" "$g_hooks" -g -h || { no "[$r] -g -h with codex off" "$(err_tail)"; return; }
  if [ "$(pj "$h" "(len(d['hooks']['UserPromptSubmit']), d['hooks']['UserPromptSubmit'][0]['hooks'][0]['command'])")" != "(1, '/opt/gk/hook.sh')" ]; then
    no "[$r] codex off removes every store hook and keeps the operator's" "$(pj "$h" "d")"; return
  fi
  ok "[$r] hooks: absolute UserPromptSubmit commands, operator group kept, one snapshot, flags and codex-off unregister"
}

check_g_uninstall_edits() {
  local r="$1" gh store
  # (a) the files existed before the install: their operator content must come back.
  gh="$(g_home unedit "$r")"; store="$gh/store"
  printf 'operator text\n' >"$gh/.codex/AGENTS.md"
  printf '{\n  "keep": 1,\n  "instructions": [\n    "mine.md"\n  ]\n}\n' >"$gh/.config/opencode/opencode.json"
  printf '{\n  "hooks": {\n    "UserPromptSubmit": [\n      {\n        "hooks": [\n          {\n            "command": "/opt/gk/hook.sh",\n            "type": "command"\n          }\n        ]\n      }\n    ]\n  }\n}\n' >"$gh/.codex/hooks.json"
  cp "$gh/.codex/AGENTS.md" "$scratch/u-a-$r"; cp "$gh/.config/opencode/opencode.json" "$scratch/u-o-$r"; cp "$gh/.codex/hooks.json" "$scratch/u-h-$r"
  run_gx "$r" "$gh" "$g_rules" "$g_hooks" -g || { no "[$r] -g with rules and hooks before uninstall" "$(err_tail)"; return; }
  run_gx "$r" "$gh" "$g_rules" "$g_hooks" UNINSTALL -g || { no "[$r] --uninstall after rules and hooks" "$(err_tail)"; return; }
  if ! cmp -s "$gh/.codex/AGENTS.md" "$scratch/u-a-$r"; then no "[$r] --uninstall returns AGENTS.md to the operator's bytes" "$(cat "$gh/.codex/AGENTS.md")"; return; fi
  if [ "$(pj "$gh/.config/opencode/opencode.json" "d")" != "$(pj "$scratch/u-o-$r" "d")" ] || [ "$(pj "$gh/.codex/hooks.json" "d")" != "$(pj "$scratch/u-h-$r" "d")" ]; then
    no "[$r] --uninstall returns opencode.json and hooks.json to the operator's content" "$(pj "$gh/.config/opencode/opencode.json" "d") / $(pj "$gh/.codex/hooks.json" "d")"; return
  fi
  if [ "$(count_glob "$gh"/.config/opencode/opencode.json.smooth-mimisbrunnr-uninstall-* "$gh"/.codex/hooks.json.smooth-mimisbrunnr-uninstall-*)" != 2 ] \
     || [ "$(count_glob "$gh"/.codex/AGENTS.md.smooth-mimisbrunnr-uninstall-*)" != 0 ]; then
    no "[$r] --uninstall snapshots the two JSON files it edits, and not AGENTS.md"; return
  fi
  if [ -n "$(find "$gh/.claude/rules" -type l 2>/dev/null)" ]; then no "[$r] --uninstall removes the Claude rule links"; return; fi
  # (b) the files did not exist: the installer created them, so uninstall removes them.
  gh="$(g_home uncreate "$r")"; store="$gh/store"; rm -rf "$gh/.config/opencode"; mkdir -p "$gh/.config/opencode"
  run_gx "$r" "$gh" "$g_rules" "$g_hooks" -g || { no "[$r] -g creating AGENTS.md and hooks.json" "$(err_tail)"; return; }
  if [ ! -f "$gh/.codex/AGENTS.md" ] || [ ! -f "$gh/.codex/hooks.json" ]; then no "[$r] -g creates AGENTS.md and hooks.json when there are none"; return; fi
  run_gx "$r" "$gh" "$g_rules" "$g_hooks" UNINSTALL -g || { no "[$r] --uninstall of created files" "$(err_tail)"; return; }
  if [ -e "$gh/.codex/AGENTS.md" ] || [ -e "$gh/.codex/hooks.json" ] || [ "$(count_glob "$gh"/.codex/*.smooth-mimisbrunnr-*)" != 0 ]; then
    no "[$r] --uninstall removes files the installer created, with no litter" "$(ls -a "$gh/.codex" | tr '\n' ' ')"; return
  fi
  # (c) an AGENTS.md that existed but was empty is still the operator's: removing our block leaves it empty
  # and it must stay, because the installer did not create it.
  gh="$(g_home unempty "$r")"; : >"$gh/.codex/AGENTS.md"
  run_gx "$r" "$gh" "$g_rules" "" -g -r || { no "[$r] -g -r over an empty AGENTS.md" "$(err_tail)"; return; }
  run_gx "$r" "$gh" "$g_rules" "" UNINSTALL -g || { no "[$r] --uninstall over an empty AGENTS.md" "$(err_tail)"; return; }
  if [ ! -f "$gh/.codex/AGENTS.md" ] || [ -s "$gh/.codex/AGENTS.md" ]; then
    no "[$r] --uninstall keeps an operator's empty AGENTS.md as an empty file"; return
  fi
  ok "[$r] --uninstall: operator bytes/content restored, created files removed, an empty operator file kept, no litter"
}

check_g_coexist() {
  local r="$1" gh store tdv
  gh="$(g_home coexist "$r")"; store="$gh/store"; tdv="$gh/.smooth-ai-devex"
  mkdir -p "$tdv/skills/foreign-skill" "$tdv/hooks" "$tdv/rules"
  ln -s "$tdv/skills/foreign-skill" "$gh/.claude/skills/foreign-skill" 2>/dev/null || { mkdir -p "$gh/.claude/skills"; ln -s "$tdv/skills/foreign-skill" "$gh/.claude/skills/foreign-skill"; }
  # What smooth-devex-template's installer leaves behind: its own block, snapshot, hook and instruction.
  printf 'operator\n\n<!-- installer-owned-global-instructions (added by install-skills): regenerated on every run -->\ntemplate index\n<!-- end of install-skills global rules -->\n' >"$gh/.codex/AGENTS.md"
  printf 'template snapshot\n' >"$gh/.codex/AGENTS.md.smooth-devex-20260101-000000"
  printf '{\n  "hooks": {\n    "UserPromptSubmit": [\n      {\n        "hooks": [\n          {\n            "command": "%s/hooks/t.sh",\n            "type": "command"\n          }\n        ]\n      }\n    ]\n  }\n}\n' "$tdv" >"$gh/.codex/hooks.json"
  printf '{\n  "instructions": [\n    "%s/rules/t.md"\n  ]\n}\n' "$tdv" >"$gh/.config/opencode/opencode.json"
  cp "$gh/.codex/AGENTS.md" "$scratch/co-a-$r"; cp "$gh/.codex/hooks.json" "$scratch/co-h-$r"; cp "$gh/.config/opencode/opencode.json" "$scratch/co-o-$r"
  run_gx "$r" "$gh" "$g_rules" "$g_hooks" -g || { no "[$r] -g beside the template installer's edits" "$(err_tail)"; return; }
  if [ "$(count_glob "$gh"/.codex/AGENTS.md.smooth-mimisbrunnr-[0-9]*)" != 1 ]; then
    no "[$r] the template's snapshot does not suppress ours"; return
  fi
  if ! grep -q 'template index' "$gh/.codex/AGENTS.md" || ! grep -q 'mimisbrunnr-owned-global-instructions' "$gh/.codex/AGENTS.md"; then
    no "[$r] both installers' blocks coexist in AGENTS.md"; return
  fi
  set_harness "$store/installation.json" claude-code false
  run_gx "$r" "$gh" "$g_rules" "$g_hooks" -g || { no "[$r] -g with claude off beside a foreign store" "$(err_tail)"; return; }
  if [ "$(readlink "$gh/.claude/skills/foreign-skill")" != "$tdv/skills/foreign-skill" ]; then
    no "[$r] turning a tool off never purges links into another installer's store"; return
  fi
  run_gx "$r" "$gh" "$g_rules" "$g_hooks" UNINSTALL -g || { no "[$r] --uninstall beside the template installer" "$(err_tail)"; return; }
  if ! cmp -s "$gh/.codex/AGENTS.md" "$scratch/co-a-$r" || ! cmp -s "$gh/.codex/hooks.json" "$scratch/co-h-$r" \
     || [ "$(pj "$gh/.config/opencode/opencode.json" "d")" != "$(pj "$scratch/co-o-$r" "d")" ]; then
    no "[$r] --uninstall leaves the template installer's block, hook and instruction exactly as they were"; return
  fi
  ok "[$r] coexists with the template installer: own markers/snapshots, never purges its links, uninstall leaves its edits intact"
}

check_g_invalid_json() {
  local r="$1" gh o h bad
  gh="$(g_home badjson "$r")"; o="$gh/.config/opencode/opencode.json"; h="$gh/.codex/hooks.json"
  bad='{ // comment
  "instructions": []
}'
  printf '%s\n' "$bad" >"$o"
  if run_gx "$r" "$gh" "$g_rules" "" -g -r; then no "[$r] -g -r refuses an opencode.json that does not parse"; return; fi
  if ! grep -q 'opencode.json' "$scratch/err" || [ "$(cat "$o")" != "$bad" ] || [ "$(count_glob "$o".smooth-mimisbrunnr-*)" != 0 ]; then
    no "[$r] an unparseable opencode.json is named in the error, untouched and not snapshotted"; return
  fi
  rm "$o"; printf '{ not json' >"$h"
  if run_gx "$r" "$gh" "" "$g_hooks" -g -h; then no "[$r] -g -h refuses a hooks.json that does not parse"; return; fi
  if ! grep -q 'hooks.json' "$scratch/err" || [ "$(cat "$h")" != "{ not json" ]; then
    no "[$r] an unparseable hooks.json is named in the error and untouched"; return
  fi
  ok "[$r] an unparseable opencode.json or hooks.json is a named hard error and is never overwritten"
}

check_g_per_repo_switch() {
  local r="$1" root rc=0
  root="$(make_root "perrepo-$r" mimisbrunnr-demo-skill)"
  (
    cd "$scratch"
    export SKILLS_ROOT="$root" SKILLS_REPO="$g_source" SKILLS_BRANCH=main
    if [ "$r" = sh ]; then bash "$installer_sh" -s; else pwsh -NoProfile -File "$installer_ps1" -s; fi
  ) >"$scratch/out" 2>"$scratch/err" || rc=$?
  if [ "$rc" != 0 ] || [ ! -f "$root/_mimisbrunnr-shared/installation.json" ]; then no "[$r] per-repo -s still works" "$(err_tail)"; return; fi
  ok "[$r] per-repo mode is unchanged and accepts -s"
}

check_g_store_spelling() {
  local r="$1" gh store
  gh="$(g_home slash "$r")"; store="$gh/store"
  G_STORE="$store/"
  if ! run_g "$r" "$gh" -g; then G_STORE=""; no "[$r] -g with a trailing slash on the store exits 0" "$(err_tail)"; return; fi
  G_STORE=""
  if ! run_g "$r" "$gh" -g; then no "[$r] -g re-run without the trailing slash exits 0" "$(err_tail)"; return; fi
  if logged | grep -q WARNING; then
    no "[$r] one spelling of the store: no 'somewhere else' warning after a trailing-slash install" "$(logged | grep WARNING | head -1)"; return
  fi
  if [ "$(readlink "$gh/.claude/skills/ai-brain-dump")" != "$store/skills/ai-brain-dump" ]; then
    no "[$r] links are created through the normalised store path" "$(readlink "$gh/.claude/skills/ai-brain-dump")"; return
  fi
  set_harness "$store/installation.json" claude-code false
  run_g "$r" "$gh" -g || { no "[$r] -g with claude off after a trailing-slash install" "$(err_tail)"; return; }
  if [ -n "$(find "$gh/.claude/skills" -maxdepth 1 -type l 2>/dev/null)" ]; then
    no "[$r] a tool-off purge reaches links made through a trailing-slash store"; return
  fi
  ok "[$r] the store has one spelling: a trailing slash neither warns on re-run nor hides links from a purge"
}

check_g_relative_store() {
  local r="$1" gh
  gh="$(g_home relstore "$r")"
  G_STORE="relative/store"
  if run_g "$r" "$gh" -g; then G_STORE=""; no "[$r] a relative SMOOTH_MIMISBRUNNR_HOME is refused"; return; fi
  G_STORE=""
  if ! logged | grep -q SMOOTH_MIMISBRUNNR_HOME || [ -e "$gh/relative" ] || [ -e "$gh/store" ]; then
    no "[$r] the refusal names the variable and writes nothing" "$(err_tail)"; return
  fi
  ok "[$r] a relative store path is refused, naming the variable, and nothing is written"
}

check_g_tool_off_rules() {
  local r="$1" gh store o a
  gh="$(g_home offrules "$r")"; store="$gh/store"; o="$gh/.config/opencode/opencode.json"; a="$gh/.codex/AGENTS.md"
  printf 'operator text\n' >"$a"
  printf '{\n  "instructions": [\n    "mine.md"\n  ]\n}\n' >"$o"
  cp "$a" "$scratch/or-a-$r"
  run_gx "$r" "$gh" "$g_rules" "" -g -r || { no "[$r] -g -r before switching tools off" "$(err_tail)"; return; }
  set_harness "$store/installation.json" claude-code false
  run_gx "$r" "$gh" "$g_rules" "" -g -r || { no "[$r] -g -r with claude off" "$(err_tail)"; return; }
  if [ -n "$(find "$gh/.claude/rules" -type l 2>/dev/null)" ] || ! grep -q 'mimisbrunnr-owned-global-instructions' "$a" \
     || [ "$(pj "$o" "len(d['instructions'])")" != 3 ]; then
    no "[$r] claude off removes its rule links and leaves Codex and OpenCode wired"; return
  fi
  set_harness "$store/installation.json" opencode false
  run_gx "$r" "$gh" "$g_rules" "" -g -r || { no "[$r] -g -r with opencode off" "$(err_tail)"; return; }
  if [ "$(pj "$o" "d['instructions']")" != "['mine.md']" ] || ! grep -q 'mimisbrunnr-owned-global-instructions' "$a"; then
    no "[$r] opencode off removes only our entries and leaves Codex wired" "$(pj "$o" "d")"; return
  fi
  set_harness "$store/installation.json" codex false
  run_gx "$r" "$gh" "$g_rules" "" -g -r || { no "[$r] -g -r with codex off" "$(err_tail)"; return; }
  if ! cmp -s "$a" "$scratch/or-a-$r"; then no "[$r] codex off removes our block and leaves the operator's AGENTS.md byte-identical" "$(cat "$a")"; return; fi
  set_harness "$store/installation.json" claude-code true
  set_harness "$store/installation.json" opencode true
  set_harness "$store/installation.json" codex true
  run_gx "$r" "$gh" "$g_rules" "" -g -r || { no "[$r] -g -r with every tool back on" "$(err_tail)"; return; }
  if [ ! -L "$gh/.claude/rules/test-rule.instructions.md" ] || ! grep -q 'mimisbrunnr-owned-global-instructions' "$a" \
     || [ "$(pj "$o" "len(d['instructions'])")" != 3 ]; then
    no "[$r] switching every tool back on rewires rules for all three"; return
  fi
  ok "[$r] each tool switched off unwires only its own rules (links, index block, instructions); back on rewires all"
}

check_g_preserves_values() {
  local r="$1" gh o want
  gh="$(g_home values "$r")"; o="$gh/.config/opencode/opencode.json"
  printf '{\n  "stamp": "2026-01-01T10:00:00+02:00",\n  "day": "2026-01-01",\n  "big": 12345678901234567890,\n  "name": "Zoë ✓",\n  "nested": {"list": [1, [2], {"a": null}], "flag": false},\n  "instructions": ["mine.md"]\n}\n' >"$o"
  run_gx "$r" "$gh" "$g_rules" "" -g -r || { no "[$r] -g -r over an opencode.json with varied values" "$(err_tail)"; return; }
  want="('2026-01-01T10:00:00+02:00', '2026-01-01', 12345678901234567890, 'Zoë ✓', {'list': [1, [2], {'a': None}], 'flag': False}, 'mine.md')"
  if [ "$(pj "$o" "(d['stamp'], d['day'], d['big'], d['name'], d['nested'], d['instructions'][0])")" != "$want" ]; then
    no "[$r] an edit keeps every operator value exactly (offset timestamps, big integers, unicode, nesting)" "$(pj "$o" "d")"; return
  fi
  ok "[$r] editing opencode.json preserves the operator's values exactly (offset timestamp, big integer, unicode, nesting)"
}

# The owned rule/hook lists live in both ports. Owning a real name means adding it to each, and nothing
# else would notice one port missing it - the production lists are empty, so the synthetic-name cases
# above cannot tell.
check_owned_lists_parity() {
  local verdict
  verdict="$(python3 - "$installer_sh" "$installer_ps1" <<'PYEOF'
import re, sys
sh, ps = open(sys.argv[1]).read(), open(sys.argv[2]).read()

def from_sh(name):
    m = re.search(r'^' + name + r'=\(([^)]*)\)', sh, re.M)
    if not m:
        return None
    return sorted(w for line in m.group(1).splitlines() for w in line.split('#')[0].split())

def from_ps(name):
    m = re.search(r'^\$script:' + name + r' = @\(([^)]*)\)', ps, re.M)
    if not m:
        return None
    return sorted(re.findall(r"'([^']+)'", m.group(1)))

bad = []
for sh_name, ps_name in (('OWNED_RULES', 'OwnedRules'), ('OWNED_HOOKS', 'OwnedHooks')):
    a, b = from_sh(sh_name), from_ps(ps_name)
    if a is None or b is None or a != b:
        bad.append('%s: sh=%s ps1=%s' % (sh_name, a, b))
print('; '.join(bad) if bad else 'ok')
PYEOF
)"
  if [ "$verdict" != ok ]; then no "the owned rule and hook lists are identical in both ports" "$verdict"; return; fi
  ok "the owned rule and hook lists are identical in both ports"
}

for g_r in $g_runners; do
  check_g_first_run "$g_r"
  check_g_idempotent "$g_r"
  check_g_default_leaves_config "$g_r"
  check_g_absent_tool "$g_r"
  check_g_foreign "$g_r"
  check_g_refusals "$g_r"
  check_g_corrupt_manifest "$g_r"
  check_g_skill_flag "$g_r"
  check_g_tool_off "$g_r"
  check_g_uninstall "$g_r"
  check_g_categories "$g_r"
  check_g_shared_resolves "$g_r"
  check_g_rules "$g_r"
  check_g_rule_flag "$g_r"
  check_g_hooks "$g_r"
  check_g_uninstall_edits "$g_r"
  check_g_coexist "$g_r"
  check_g_invalid_json "$g_r"
  check_g_per_repo_switch "$g_r"
  check_g_store_spelling "$g_r"
  check_g_relative_store "$g_r"
  check_g_tool_off_rules "$g_r"
  check_g_preserves_values "$g_r"
done
check_owned_lists_parity
[ "$have_pwsh" = 1 ] || echo "  skip pwsh not installed — the global install ran on the bash port only (not a failure)"

# Both ports must leave the same bytes, not merely the same shape: the same machine can be installed by
# either one, and a re-run by the other must find nothing to change.
check_g_port_parity() {
  local ghs ghp gh sub sa
  ghs="$(g_home paritysh sh)"; ghp="$(g_home paritysh ps1)"
  for gh in "$ghs" "$ghp"; do
    printf 'operator text\n' >"$gh/.codex/AGENTS.md"
    printf '{\n  "keep": 1,\n  "instructions": [\n    "mine.md"\n  ]\n}\n' >"$gh/.config/opencode/opencode.json"
  done
  run_gx sh "$ghs" "$g_rules" "$g_hooks" -g || { no "[parity] bash port install" "$(err_tail)"; return; }
  run_gx ps1 "$ghp" "$g_rules" "$g_hooks" -g || { no "[parity] ps1 port install" "$(err_tail)"; return; }
  if [ "$(sed "s#$ghs#@H#g" "$ghs/.codex/AGENTS.md")" != "$(sed "s#$ghp#@H#g" "$ghp/.codex/AGENTS.md")" ]; then
    no "[parity] both ports write the same AGENTS.md block"; return
  fi
  for sub in .config/opencode/opencode.json .codex/hooks.json store/installation.json; do
    if [ "$(norm_json "$ghs/$sub" "$ghs")" != "$(norm_json "$ghp/$sub" "$ghp")" ]; then
      no "[parity] both ports write the same $sub"; return
    fi
  done
  # A re-run by the other port finds nothing to change.
  sa="$(sum_of "$ghs/.codex/AGENTS.md")"
  run_gx ps1 "$ghs" "$g_rules" "$g_hooks" -g || { no "[parity] ps1 re-run over a bash install" "$(err_tail)"; return; }
  if [ "$(sum_of "$ghs/.codex/AGENTS.md")" != "$sa" ]; then no "[parity] a re-run by the other port rewrites AGENTS.md"; return; fi
  ok "[parity] bash and PowerShell write the same AGENTS.md, opencode.json, hooks.json and manifest, and re-run each other's installs unchanged"
}
if [ "$have_pwsh" = 1 ]; then check_g_port_parity; fi

# ---------------------------------------------------------------------------------------------

echo
echo "passed: $passed  failed: $failed"
[ "$failed" -eq 0 ]