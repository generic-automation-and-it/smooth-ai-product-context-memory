#!/bin/bash
# Cross-platform skills installer for macOS/Linux (bash).
#
# One-liner:
#   curl -fsSL https://raw.githubusercontent.com/generic-automation-and-it/smooth-ai-product-context-memory/main/scripts/install-skills.sh | bash
#
# Contract:
#   1. Finds the skills root in order: .agents/skills → .claude/skills → .codex/skills, and errors
#      when none exists. It never creates one: a folder the operator did not ask for is not an install.
#   2. Installs only the skills owned here: `mimisbrunnr-*`, `ai-understanding`, `ai-brain-dump`
#      (plus `_mimisbrunnr-shared/`, which is infrastructure, not a skill). Anything else on disk —
#      local coding skills, operator experiments — is left untouched and never enters the manifest.
#   3. Reads <root>/_mimisbrunnr-shared/installation.json.
#      - Missing  → creates it from the owned catalogue, installed:true, UTC timestamp.
#      - Present  → syncs: adds new owned skills, drops skills no longer owned or on disk, keeps every
#                   installed flag the operator already set. A corrupt file is a hard error,
#                   never a silent reset of the whole manifest.
#   4. Updates only the owned skills marked installed:true, cloning the repo once and copying those
#      folders (plus the shared scripts folder) out of it.
#   5. Writes the updated installation.json back with a fresh UTC timestamp — after the update, so
#      refreshing the shared folder cannot clobber the manifest that update read.
#
# Global mode (-g) installs the same owned set once, machine-wide, into a store of its own
# ($SMOOTH_MIMISBRUNNR_HOME, default ~/.smooth-ai-mimisbrunnr) and wires it into Claude Code, Codex and
# OpenCode; see "Global install" below. Switches: -s skills, -r rules, -h hooks (none = all three),
# --uninstall (needs -g). -r and -h only exist in global mode: this repo owns no rules or hooks yet,
# so the owned lists are empty and a default run touches skills only.
#
# The owned set lives in `is_owned_skill()` below AND in the PowerShell port's `Test-OwnedSkill`.
# The two must match; `test-install-skills.sh` holds them to each other (parity cases).
#
# Dependencies: bash, git, python3 (already a dependency of every mimisbrunnr-* skill).
#
# Environment overrides:
#   SKILLS_REPO    — git URL to clone from (default: the upstream GitHub repo)
#   SKILLS_BRANCH  — branch to clone (default: main)
#   SKILLS_ROOT    — force a specific skills root (skips the search); refused with -g
#   SMOOTH_MIMISBRUNNR_HOME — the -g store (default ~/.smooth-ai-mimisbrunnr)
#   CLAUDE_CONFIG_DIR / CODEX_HOME / XDG_CONFIG_HOME — where -g finds each tool's config
#                  (~/.claude, ~/.codex, ~/.config/opencode); the tools' own variables, not ours

set -euo pipefail

SKILLS_REPO="${SKILLS_REPO:-https://github.com/generic-automation-and-it/smooth-ai-product-context-memory.git}"
# `-` not `:-`, and that single character is load-bearing. `:-` substitutes the default for an EMPTY
# value as well as an unset one, so `SKILLS_BRANCH=''` — which means "whatever the remote's default is",
# and is the only honest answer in a detached checkout where there is no branch name to pass — silently
# became "main" and then failed on any source that has no main branch. `-` substitutes only when unset.
SKILLS_BRANCH="${SKILLS_BRANCH-main}"

die() { echo "install-skills: $*" >&2; exit 1; }
log() { echo "install-skills: $*" >&2; }

# The canonical path of the clone source, and the boundary that decides which symlinks `copy_wet`
# materialises. Assigned unconditionally here, and only ever set from this run's own clone: an
# inherited `CLONE_ROOT` from the environment would otherwise widen "inside the clone" to wherever
# the caller said, and a link pointing anywhere under that path would be materialised into a skill
# that has no business holding it.
CLONE_ROOT=""

# ---------------------------------------------------------------------------
# 1. Find skills root
# ---------------------------------------------------------------------------

find_skills_root() {
  local dir
  for dir in ".agents/skills" ".claude/skills" ".codex/skills"; do
    if [ -d "$dir" ]; then
      printf '%s\n' "$dir"
      return 0
    fi
  done
  return 1
}

# ---------------------------------------------------------------------------
# 2. Owned set + listing
# ---------------------------------------------------------------------------

# The only skills this installer manages. Everything else that happens to sit in a skills root —
# local coding skills, operator experiments — is somebody else's to update and is never touched.
is_owned_skill() {
  # Skill names are single path components (list_disk_skills emits basenames). A name carrying a
  # separator or `..` never matches anything real, but the update loop concatenates manifest names
  # into paths for rm/cp — so reject it here rather than letting a hand-edited manifest aim those
  # commands outside the skills root.
  case "$1" in *[/]* | *..*) return 1 ;; esac
  case "$1" in
    mimisbrunnr-*) return 0 ;;
    ai-understanding | ai-brain-dump | ai-memory-context) return 0 ;;
  esac
  return 1
}

# Rules (paths relative to .github/instructions/) and hooks (names under .agents/hooks/) this installer
# may carry. Both are empty on purpose: this repo owns neither, and the rules/hooks the checkout holds
# belong to smooth-devex-template, whose own installer ships them. The -g machinery for both is built
# and held by the harness, which extends these lists with synthetic names through the test-only
# variables below; production never sets them. Adding a real name here is the whole act of owning it.
OWNED_RULES=()
OWNED_HOOKS=()

# One name per line. `${arr[@]+...}` because bash 3.2 (macOS) treats an empty array as unset under `set -u`.
owned_rules() {
  local name
  for name in ${OWNED_RULES[@]+"${OWNED_RULES[@]}"} ${SKILLS_TEST_OWNED_RULES_EXTRA:-}; do
    printf '%s\n' "$name"
  done
}

owned_hooks() {
  local name
  for name in ${OWNED_HOOKS[@]+"${OWNED_HOOKS[@]}"} ${SKILLS_TEST_OWNED_HOOKS_EXTRA:-}; do
    printf '%s\n' "$name"
  done
}

DO_RULES=0
DO_SKILLS=0
DO_HOOKS=0
DO_GLOBAL=0
DO_UNINSTALL=0

# No category switch means everything; a switch narrows the run to exactly what was named and nothing
# about the selection is persisted. -r, -h and --uninstall have no per-repo meaning, so they are refused
# without -g rather than silently ignored.
parse_arguments() {
  local arg
  for arg in "$@"; do
    case "$arg" in
      -s) DO_SKILLS=1 ;;
      -r) DO_RULES=1 ;;
      -h) DO_HOOKS=1 ;;
      -g) DO_GLOBAL=1 ;;
      --uninstall) DO_UNINSTALL=1 ;;
      *) die "unknown option: $arg (use -s skills, -r rules, -h hooks, -g global install to ~/.smooth-ai-mimisbrunnr, --uninstall to remove it)" ;;
    esac
  done
  if [ "$DO_GLOBAL" = "0" ]; then
    [ "$DO_UNINSTALL" = "0" ] || die "--uninstall only applies to the global install — use: install-skills -g --uninstall"
    [ "$DO_RULES$DO_HOOKS" = "00" ] || die "-r and -h only apply to the global install — add -g"
  fi
  if [ "$DO_RULES$DO_SKILLS$DO_HOOKS" = "000" ]; then
    DO_RULES=1
    DO_SKILLS=1
    DO_HOOKS=1
  fi
}

# List skills on disk (dirs with SKILL.md, excluding _mimisbrunnr-shared)

list_disk_skills() {
  local skills_root="$1"
  local dir name
  for dir in "$skills_root"/*/; do
    [ -d "$dir" ] || continue
    name="$(basename "$dir")"
    [ "$name" = "_mimisbrunnr-shared" ] && continue
    if [ -f "$dir/SKILL.md" ]; then
      printf '%s\n' "$name"
    fi
  done
}

# ---------------------------------------------------------------------------
# 3. JSON helpers (python3 — already a repo dependency)
# ---------------------------------------------------------------------------

json_read() {
  # Missing file reads as empty (a first install). A *corrupt* file is an error, not a first install:
  # treating it as empty would rewrite the manifest to installed:true for everything, which silently
  # re-enables skills the operator had turned off.
  python3 -c "
import json, sys, os
if not os.path.exists(sys.argv[1]):
    print('')
    raise SystemExit(0)
try:
    with open(sys.argv[1]) as f:
        print(json.dumps(json.load(f)))
except json.JSONDecodeError as exc:
    sys.stderr.write('install-skills: %s is not valid JSON (%s); fix or delete it — not treating it as a first install\n' % (sys.argv[1], exc))
    raise SystemExit(2)
" "$1"
}

json_write() {
  python3 -c "
import json, sys
data = json.loads(sys.argv[2])
with open(sys.argv[1], 'w') as f:
    json.dump(data, f, indent=2)
    f.write('\n')
" "$1" "$2"
}

# ---------------------------------------------------------------------------
# 4. Sync installation.json with disk
# ---------------------------------------------------------------------------

build_installation_json() {
  # Emits the merged manifest on stdout; the caller writes it after the update.
  #
  # $2 is the clone source's skill list, NOT just what is on disk. Deriving the manifest from disk alone
  # makes a first install into an empty skills folder a no-op — there is nothing on disk, so there is
  # nothing to install, and the operator's very first run leaves them with an empty folder and a
  # manifest listing nothing. The source is the catalogue; disk is what is already there.
  local skills_root="$1" existing_json="$2" source_skills_json="$3"
  local disk_skills_json

  disk_skills_json="$(list_disk_skills "$skills_root" | while IFS= read -r name; do
    is_owned_skill "$name" && printf '%s\n' "$name"
  done | python3 -c "
import json, sys
skills = sorted(line.strip() for line in sys.stdin if line.strip())
print(json.dumps(skills))
")"

  python3 - "$disk_skills_json" "$existing_json" "$source_skills_json" <<'PYEOF'
import json, sys
from datetime import datetime, timezone

disk_skills = set(json.loads(sys.argv[1]))
existing_raw = sys.argv[2].strip()
source_skills = set(json.loads(sys.argv[3]))

# The catalogue is the owned union: what the source offers plus what is already here. Both lists arrive
# pre-filtered through `is_owned_skill` (the disk loop above and its twin at the source-listing call
# site) — that shell function is the single gate, and it is what the harness pins. A manifest entry for a
# skill that has since left the owned set drops out here because neither list can carry it anymore.
catalog = source_skills | disk_skills

now = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")

if not existing_raw:
    # First run: every skill in the catalogue is what the operator asked for, so it installs. Defaulting
    # to false here would make a first run a no-op - the operator would have to hand-edit the manifest
    # before anything could ever update.
    data = {
        "updated": now,
        "skills": [{"name": s, "installed": True} for s in sorted(catalog)],
    }
else:
    existing = json.loads(existing_raw)
    existing_map = {e["name"]: e.get("installed", False) for e in existing.get("skills", [])}
    # Drop what is gone from both the source and disk (the requirement is to remove removed skills), and
    # preserve every flag the operator already set, including false. A new skill joins as true, same as
    # the first-run rule.
    merged = []
    for name in sorted(catalog):
        merged.append({"name": name, "installed": existing_map.get(name, True)})
    data = {"updated": now, "skills": merged}

print(json.dumps(data))
PYEOF
}

# The skills the clone source offers: every folder carrying a SKILL.md, minus the shared folder. Read
# from the clone, so it is empty when the caller has not cloned yet.
list_source_skills() {
  local source_root="$1"
  local dir name
  for dir in "$source_root"/*/; do
    [ -d "$dir" ] || continue
    name="$(basename "$dir")"
    [ "$name" = "_mimisbrunnr-shared" ] && continue
    if [ -f "$dir/SKILL.md" ]; then
      printf '%s\n' "$name"
    fi
  done
}

# sweep_stale_entries DEST SOURCE — remove from DEST everything SOURCE does not have, recursively.
#
# `installation.json` is spared wherever it is found, because it is this script's own state and not
# anything the clone source carries: sweeping it away is what turns an interrupted run into a silent
# reset of every installed flag on the next one.
sweep_stale_entries() {
  local dest="$1" source="$2"
  local entry name
  for entry in "$dest"/* "$dest"/.[!.]*; do
    # `-L` as well as `-e`: a DANGLING symlink fails `-e` because that test follows the link, so
    # testing only `-e` skipped every broken link — which kept the ones the source still has (right)
    # and equally the ones the source deleted (a retired link living forever, which is the exact
    # bug this sweep exists to prevent, just with a symlink instead of a file).
    [ -e "$entry" ] || [ -L "$entry" ] || continue
    name="$(basename "$entry")"
    [ "$name" = "installation.json" ] && continue
    if [ -d "$source/$name" ] && [ -d "$entry" ]; then
      sweep_stale_entries "$entry" "$source/$name"
    elif [ ! -e "$source/$name" ] && [ ! -L "$source/$name" ]; then
      rm -rf "$entry"
    fi
  done
}

# real_path PATH — the canonical absolute path, following every link, or '' when it does not resolve.
#
# `readlink -f` is GNU-only; BSD ships `realpath` but macOS's only gained a default in 13, and this
# installer supports macOS bash 3.2. So the resolution is done by hand with `cd -P`, which is the
# same primitive both platforms agree on. A dangling target yields '' rather than a partial path,
# because a link with no target is a link to keep, not a path to place.
real_path() {
  local path="$1" hops=0 target dir
  # Follow the final component while it is a symlink. Bounded, so a link that points at itself
  # through a chain of names cannot spin here; the hop count is generous and never reached in
  # practice, but its absence would turn a malformed link into a hang.
  while [ -L "$path" ] && [ "$hops" -lt 40 ]; do
    hops=$((hops + 1))
    target="$(readlink "$path")"
    case "$target" in
      /*) path="$target" ;;
      *)  path="$(dirname "$path")/$target" ;;
    esac
  done
  # Still a link after 40 hops: a loop, so there is no canonical path to report.
  [ -L "$path" ] && return 0
  # A link with no target is a link to keep, not a path to place.
  [ -e "$path" ] || return 0
  # `cd -P` into a directory resolves every component including a trailing `.`, which is what makes
  # a `self -> .` link canonicalise to the folder itself and therefore match the expansion stack.
  # Without it the path keeps a `/.` tail, never compares equal to its own directory, and a cycle
  # is materialised instead of being kept as a link.
  if [ -d "$path" ]; then
    (cd -P "$path" 2>/dev/null && pwd) && return 0
    return 0
  fi
  dir="$(cd -P "$(dirname "$path")" 2>/dev/null && pwd)" || return 0
  printf '%s/%s\n' "$dir" "$(basename "$path")"
}

# inside_clone PATH — true when PATH resolves to somewhere under the clone source.
#
# Compared canonically and component-wise, never as a string prefix: `/repo-other/x` starts with
# `/repo` as text and is a different repository. The trailing separator is what stops that.
inside_clone() {
  local candidate="$1"
  [ -n "$CLONE_ROOT" ] || return 1
  case "$candidate" in
    "$CLONE_ROOT"/*) return 0 ;;
    "$CLONE_ROOT") return 0 ;;
  esac
  return 1
}

# copy_wet SRC DST [STACK] — copy SRC to DST, materialising a symlink whose target is inside the clone.
#
# The point is a *self-contained* installed skill. A symlink that points inside the clone is an
# authoring convenience — one file shared by two skills — and copying the link would ship a pointer
# into a path the consumer does not have, so it is copied as what it points at. A link pointing
# outside the clone, or one with no target, is the author's business and is copied as a link.
#
# Cycles are copied as links. A link to its own ancestor, or to something already being expanded,
# would otherwise recurse forever; recording the chain we are inside and stopping at a repeat makes
# the run terminate while leaving the link visible rather than silently materialising a loop.
copy_wet() {
  local src="$1" dst="$2" stack="${3:-}"
  local target real name entry

  if [ -L "$src" ]; then
    target="$(readlink "$src")"
    case "$target" in
      /*) real="$(real_path "$target")" ;;
      *)  real="$(real_path "$(dirname "$src")/$target")" ;;
    esac

    # No target: keep the link.
    if [ -z "$real" ]; then
      cp -P "$src" "$dst"
      return 0
    fi
    # Already being expanded: a cycle. Keep the link so the run terminates.
    case ":$stack:" in
      *":$real:"*) cp -P "$src" "$dst"; return 0 ;;
    esac
    # Outside the clone: the consumer may legitimately want a link to their own machine.
    if ! inside_clone "$real"; then
      cp -P "$src" "$dst"
      return 0
    fi
    copy_wet "$real" "$dst" "$stack:$real"
    return 0
  fi

  if [ -d "$src" ]; then
    real="$(real_path "$src")"
    mkdir -p "$dst"
    for entry in "$src"/* "$src"/.[!.]*; do
      [ -e "$entry" ] || [ -L "$entry" ] || continue
      name="$(basename "$entry")"
      copy_wet "$entry" "$dst/$name" "$stack:$real"
    done
    return 0
  fi

  [ -f "$src" ] || return 0
  cp -p "$src" "$dst"
}

# prune_non_shippable DEST — remove what does not belong in an installed copy of a skill.
#
# Copy-then-remove, deliberately, rather than `tar --exclude` or a copy filter. Every alternative
# buys its portability somewhere else: `tar --exclude` differs between GNU and BSD in flag spelling,
# and `rsync --exclude` is a third dependency for the same job. `cp -R` is already here and means the
# same thing on both platforms, and the prune is a plain `rm -rf` on two known names.
#
# The list is short because it is a *runtime* list. `agents/`, `references/` and `assets/` are
# runtime-referenced — odin reads `references/read-path.md` and `write-path.md` mid-skill, and
# ai-understanding renders `assets/UNDERSTANDING.template.md` — so stripping them ships a skill that
# documents a file it cannot open. Only two names are excluded:
#
#   tests/       Harnesses. A test asserts against the repository's own tree (paths, the shared
#                original behind a WET copy), so it is meaningless outside it and is the bulk of what
#                an installed skill does not need. This is not a security boundary: nothing in a
#                skill reads its own tests at runtime, and the exclusion exists to keep an installed
#                copy down to what runs rather than to hide anything.
#   __pycache__/ Interpreter output, never tracked, regenerated on first run. Removed recursively,
#                because a previous run — or an operator running the scripts directly inside their
#                install — leaves it at any depth. The npm `files` allowlist already excludes it for
#                the shared folder for the same reason.
#
# Only the destination is pruned. The clone source stays byte-identical, which is what keeps
# `sweep_stale_entries` able to compare against it.
prune_non_shippable() {
  local dest="$1" dir
  [ -d "$dest" ] || return 0
  rm -rf "$dest/tests"
  find "$dest" -type d -name __pycache__ -prune -exec rm -rf {} + 2>/dev/null || true
}

# ---------------------------------------------------------------------------
# 5. Update installed skills from the repo
# ---------------------------------------------------------------------------

clone_source() {
  # Cloned before the manifest is built, not inside the update: the manifest's catalogue is the source's
  # skill list, so a first install into an empty folder has to see the source before it can decide there
  # is anything to install. The clone happens exactly once per run either way.
  #
  # The temp dir is a global, not a local, because the EXIT trap runs after this function has returned:
  # a trap closing over a `local` reads it under `set -u` and aborts the script on the way out, after
  # every real work had already succeeded.
  INSTALL_TMP="$(mktemp -d)"
  trap 'rm -rf "${INSTALL_TMP:-}"' EXIT INT TERM HUP
  # Assigned after the temp dir exists, so it can only ever name this run's clone.
  CLONE_ROOT="$(cd -P "$INSTALL_TMP" && pwd)/repo"

  log "Cloning $SKILLS_REPO (${SKILLS_BRANCH:-default branch})…"
  # SKILLS_BRANCH is optional, and that is not a convenience: `git rev-parse --abbrev-ref HEAD` returns
  # the literal string "HEAD" in a detached checkout, which is exactly what actions/checkout produces.
  # `git clone --branch HEAD` is not valid — HEAD is not a branch or tag name — so a harness or an
  # operator that passed the obvious-looking value got a run that died at the clone with the branch name
  # printed in its own error. An empty branch means "whatever the remote's default is", which is what
  # HEAD means everywhere else in this comment.
  # A bare local path is normalised to file:// so --depth applies. git ignores --depth in a local clone
  # ("use file:// instead"), which is a quiet downgrade — the whole clone is taken instead of one commit —
  # and the clone fails outright when the local repository has no branch of that name, which is exactly the
  # detached-HEAD case this whole branch of the logic exists to handle.
  clone_url="$SKILLS_REPO"
  case "$clone_url" in
    *://* | /*) ;;
    *) clone_url="file://$PWD/$clone_url" ;;
  esac

  if [ -n "$SKILLS_BRANCH" ]; then
    git clone --depth 1 --branch "$SKILLS_BRANCH" "$clone_url" "$INSTALL_TMP/repo" >/dev/null 2>&1 \
      || die "Failed to clone $SKILLS_REPO (branch '$SKILLS_BRANCH' — set SKILLS_BRANCH empty for the default)"
  else
    git clone --depth 1 "$clone_url" "$INSTALL_TMP/repo" >/dev/null 2>&1 \
      || die "Failed to clone $SKILLS_REPO"
  fi
}

# manifest_installed_names MANIFEST_JSON — the skills carrying installed:true, one per line.
manifest_installed_names() {
  printf '%s' "$1" | python3 -c "
import json, sys
for s in json.load(sys.stdin).get('skills', []):
    if s.get('installed'):
        print(s['name'])
"
}

update_installed_skills() {
  local installation_json="$1" skills_root="$2"
  local installed_names

  installed_names="$(manifest_installed_names "$installation_json")"

  [ -n "$installed_names" ] || { log "No skills marked installed:true — nothing to update."; return 0; }

  local name
  while IFS= read -r name; do
    [ -n "$name" ] || continue
    # Defense in depth: the manifest only ever names owned skills, but a hand-edited manifest could name
    # anything. Never copy a folder this installer does not own.
    if ! is_owned_skill "$name"; then
      log "WARNING: $name is not owned here — left on disk untouched"
      continue
    fi
    if [ -d "$INSTALL_TMP/repo/.agents/skills/$name" ]; then
      rm -rf "$skills_root/$name"
      copy_wet "$INSTALL_TMP/repo/.agents/skills/$name" "$skills_root/$name"
      prune_non_shippable "$skills_root/$name"
      log "Updated $name"
    else
      log "WARNING: $name not found in repo — left on disk untouched"
    fi
  done <<< "$installed_names"

  # The shared folder is not a skill and is never in the manifest, but every installed skill that
  # references it is broken without it — so it refreshes on every run, independently of the flags.
  #
  # Refreshed *by content*, with stale entries removed, rather than merged in (which would leave a file
  # deleted upstream alive forever — an updater that never forgets anything is not an updater) and
  # rather than replacing the folder outright (which would take the manifest sitting inside it down with
  # it, and a run interrupted between the two would leave the next run rebuilding every flag from
  # defaults). So: copy across, then delete exactly what the source does not have, except the manifest.
  local shared_source="$INSTALL_TMP/repo/.agents/skills/_mimisbrunnr-shared"
  if [ -d "$shared_source" ]; then
    local shared_dest="$skills_root/_mimisbrunnr-shared"
    mkdir -p "$shared_dest"
    copy_wet "$shared_source" "$shared_dest"
    # Recursive, and that recursion is the point: a top-level-only sweep keeps every entry the source
    # still has *as a folder* while leaving the files inside it that the source dropped, which is where
    # a retired script actually goes.
    sweep_stale_entries "$shared_dest" "$shared_source"
    # Swept first, pruned second, and that order is deliberate. `sweep_stale_entries` compares the
    # destination against the source and needs to see the same files on both sides; pruning first
    # would leave the destination missing a `__pycache__` the source still had, and the sweep would
    # read that as an upstream deletion — harmless here, but it makes the two steps look
    # interchangeable when they are not.
    prune_non_shippable "$shared_dest"
    log "Updated _mimisbrunnr-shared"
  fi
}

# ---------------------------------------------------------------------------
# 6. Global install (-g)
#
# The per-repo install above scopes everything to the skills folder of the repo you are standing in. -g
# installs the same owned set once, machine-wide, into a store of its own and wires that store into the
# AI tools found on the machine:
#
#   Claude Code  skills -> symlinks in ~/.claude/skills; rules -> symlinks in ~/.claude/rules;
#                hooks are not wired (its hooks live in settings.json, which this installer does not edit)
#   Codex        skills -> symlinks in ~/.codex/skills beside Codex's own .system/ (never touched);
#                rules  -> a delimited index block in ~/.codex/AGENTS.md (Codex has no @-import and caps
#                          combined AGENTS.md at 32 KiB, so an index, not the rule bodies);
#                hooks  -> JSON-merged into ~/.codex/hooks.json under UserPromptSubmit only
#   OpenCode     skills -> symlinks in ~/.config/opencode/skills;
#                rules  -> one absolute path per rule in opencode.json `instructions`; hooks not wired
#
# A tool whose config folder is absent is skipped, never created. ~/.codex/rules/*.rules files are a
# command-approval policy, not instructions, and there is no policy in this repo to derive one from,
# so none is written.
#
# The store is NOT ~/.smooth-ai-devex. That folder belongs to smooth-devex-template's installer, which
# reconciles its manifest against its own owned set and purges every tool symlink whose target is under
# its store, so sharing it would let each installer delete the other's links. The tool config files are
# shared though (~/.codex/AGENTS.md, hooks.json, opencode.json), hence three rules for editing them:
#   - our own block markers and snapshot suffix (.smooth-mimisbrunnr-<stamp>), distinct from the template's
#   - an edit is made only when it changes something, and a pre-existing file is snapshotted once first
#   - --uninstall removes our entries and leaves the rest; it never restores a snapshot over the file,
#     which would wipe whatever the other installer wrote since
# ---------------------------------------------------------------------------

LINKS=()
GLB_RULES_JSON=""
GLB_HOOKS_JSON=""
GLB_HARNESSES_JSON=""
RULES_ON=""
RULES_OFF=""
HOOKS_ON=""
HOOKS_OFF=""
HARNESS_CLAUDE=1
HARNESS_CODEX=1
HARNESS_OPENCODE=1

GLOBAL_MARKER="mimisbrunnr-owned-global-instructions"
CODEX_BLOCK_END="end of mimisbrunnr install-skills global rules"

# Resolved in a function, not at script scope: a per-repo run never reads $HOME, and `set -u` would
# abort it on a machine where HOME is unset.
resolve_global_paths() {
  GLB_ROOT="${SMOOTH_MIMISBRUNNR_HOME:-$HOME/.smooth-ai-mimisbrunnr}"
  case "$GLB_ROOT" in
    /*) ;;
    *) die "SMOOTH_MIMISBRUNNR_HOME must be an absolute path (got '$GLB_ROOT') — symlinks into a relative store would dangle" ;;
  esac
  # One spelling only: links are recorded and matched as strings, so `store/` then `store` would read every
  # link as pointing somewhere else and a tool-off purge would miss the ones made through `store//`.
  while [ "${GLB_ROOT%/}" != "$GLB_ROOT" ] && [ "$GLB_ROOT" != "/" ]; do GLB_ROOT="${GLB_ROOT%/}"; done
  GLOBAL_MANIFEST="$GLB_ROOT/installation.json"
  CLAUDE_DIR="${CLAUDE_CONFIG_DIR:-$HOME/.claude}"
  CODEX_DIR="${CODEX_HOME:-$HOME/.codex}"
  OPENCODE_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/opencode"
}

# record_link TYPE PATH [DETAIL] — append a link/edit record for the manifest. A tab cannot appear in a
# path on a supported OS, so the tab split is unambiguous.
record_link() {
  if [ -n "${3:-}" ]; then
    LINKS+=("$1	$2	$3")
  else
    LINKS+=("$1	$2")
  fi
}

# backup_inplace FILE — snapshot an operator-owned file we are about to edit, as
# <FILE>.smooth-mimisbrunnr-<UTC YYYYMMDD-HHMMSS>, once per file. Only the numeric install-time
# snapshots count as "already backed up": an uninstall-time one must not suppress a fresh install
# snapshot. Echoes the path, or nothing when there was no file or one is already taken.
backup_inplace() {
  local file="$1" b stamp
  [ -e "$file" ] || return 0
  for b in "$file".smooth-mimisbrunnr-[0-9]*; do
    [ -e "$b" ] && return 0
  done
  stamp="$(date -u +%Y%m%d-%H%M%S)"
  cp "$file" "$file.smooth-mimisbrunnr-$stamp"
  printf '%s\n' "$file.smooth-mimisbrunnr-$stamp"
}

ensure_global_store() {
  mkdir -p "$GLB_ROOT/skills" "$GLB_ROOT/rules" "$GLB_ROOT/hooks"
}

# tool_present DIR LABEL — a tool is wired only when its config folder already exists.
tool_present() {
  [ -d "$1" ] && return 0
  log "$2 config not found ($1) — skipping $2 wiring"
  return 1
}

# reconcile_global_items KEY EXISTING_JSON NAMES — the manifest's per-item list for one category, as
# [{name, installed}]. NAMES is the owned catalogue, newline-separated, so a name outside it can never
# enter or survive a hand edit; an unseen name joins as true; an existing flag is kept, false included.
reconcile_global_items() {
  python3 - "$1" "$2" "$3" <<'PYEOF'
import json, sys
key, raw, names = sys.argv[1], sys.argv[2], sys.argv[3]
catalog = sorted(set(n for n in names.splitlines() if n))
known = {}
if raw.strip():
    data = json.loads(raw)
    items = data.get(key, []) if isinstance(data, dict) else []
    if isinstance(items, list):
        for entry in items:
            if isinstance(entry, dict) and isinstance(entry.get("name"), str):
                known[entry["name"]] = bool(entry.get("installed", False))
print(json.dumps([{"name": n, "installed": known.get(n, True)} for n in catalog]))
PYEOF
}

# global_items_flagged ITEMS_JSON true|false — names carrying that installed flag, one per line.
global_items_flagged() {
  printf '%s' "$1" | python3 -c "
import json, sys
want = sys.argv[1] == 'true'
for entry in json.load(sys.stdin):
    if bool(entry.get('installed')) == want:
        print(entry['name'])
" "$2"
}

# reconcile_harnesses EXISTING_JSON — exactly the three tools, in a fixed order. An unseen tool joins as
# true, the operator's value is kept (false included), any other key is dropped.
reconcile_harnesses() {
  python3 - "$1" <<'PYEOF'
import json, sys
raw = sys.argv[1]
known = {}
if raw.strip():
    data = json.loads(raw)
    found = data.get("harnesses") if isinstance(data, dict) else None
    if isinstance(found, dict):
        known = found
print(json.dumps({h: bool(known.get(h, True)) for h in ("claude-code", "codex", "opencode")}))
PYEOF
}

# harness_flag HARNESSES_JSON NAME — prints 1 when the tool is on, 0 when off.
harness_flag() {
  printf '%s' "$1" | python3 -c "
import json, sys
print(1 if json.load(sys.stdin).get(sys.argv[1], True) else 0)
" "$2"
}

# purge_store_links DIR SUBDIR — remove every symlink under DIR that points into the store's SUBDIR.
# Found by where a link points, not by what the manifest recorded, so a link whose record was lost goes
# too. A real file, and a link to anywhere else, is never touched.
purge_store_links() {
  local dir="$1" subdir="$2" link
  [ -d "$dir" ] || return 0
  while IFS= read -r link; do
    [ -n "$link" ] || continue
    case "$(readlink "$link")" in
      "$GLB_ROOT/$subdir/"*)
        rm -f "$link"
        log "Unlinked $link (tool turned off)"
        ;;
    esac
  done < <(find -H "$dir" -type l 2>/dev/null)
}

# unlink_store_file DESTLINK TARGET — remove DESTLINK only when it is the symlink we made.
unlink_store_file() {
  local destlink="$1" target="$2"
  if [ -L "$destlink" ] && [ "$(readlink "$destlink")" = "$target" ]; then
    rm -f "$destlink"
    log "Unlinked disabled entry $destlink"
  fi
}

# link_store_entry DESTLINK TARGET LINKTYPE — symlink TARGET at DESTLINK. A correct existing link is
# kept (and re-recorded, so a lost manifest is rebuilt by a re-run); a real file or directory, or a link
# to somewhere else, is reported and left alone. One failed link never aborts the run.
link_store_entry() {
  local destlink="$1" target="$2" linktype="$3"
  if [ ! -e "$target" ]; then
    log "WARNING: $target missing — nothing to link at $destlink"
    return 0
  fi
  if [ -L "$destlink" ]; then
    if [ "$(readlink "$destlink")" = "$target" ]; then
      record_link "$linktype" "$destlink" "$target"
      return 0
    fi
    log "WARNING: $destlink is a symlink to somewhere else — left untouched"
    return 0
  fi
  if [ -e "$destlink" ]; then
    log "WARNING: $destlink already exists — left untouched"
    return 0
  fi
  mkdir -p "$(dirname "$destlink")"
  if ! ln -s "$target" "$destlink"; then
    log "WARNING: could not create symlink $destlink -> $target — left untouched"
    return 0
  fi
  record_link "$linktype" "$destlink" "$target"
  log "Linked $destlink -> $target"
}

# wire_global_skills TOOL_DIR LABEL LINKTYPE TOOL_ON NAMES — link each installed skill, and the shared
# folder every one of them resolves `../_mimisbrunnr-shared` against, into TOOL_DIR/skills. A tool that is
# off is purged instead. An installed:false skill is not linked here, but an existing link stays.
wire_global_skills() {
  local tool_dir="$1" label="$2" linktype="$3" tool_on="$4" names="$5" name
  if [ "$tool_on" != "1" ]; then
    purge_store_links "$tool_dir/skills" skills
    return 0
  fi
  tool_present "$tool_dir" "$label" || return 0
  while IFS= read -r name; do
    [ -n "$name" ] || continue
    link_store_entry "$tool_dir/skills/$name" "$GLB_ROOT/skills/$name" "$linktype"
  done <<< "$names"
  if [ -d "$GLB_ROOT/skills/_mimisbrunnr-shared" ]; then
    link_store_entry "$tool_dir/skills/_mimisbrunnr-shared" "$GLB_ROOT/skills/_mimisbrunnr-shared" "$linktype"
  fi
}

# store_global_rules — copy the installed:true rules into the store, keeping relative paths. Overwrite,
# never delete: an installed:false rule is neither refreshed (store edits survive) nor wired.
store_global_rules() {
  local source_rules="$INSTALL_TMP/repo/.github/instructions" rel
  while IFS= read -r rel; do
    [ -n "$rel" ] || continue
    if [ -f "$source_rules/$rel" ]; then
      mkdir -p "$(dirname "$GLB_ROOT/rules/$rel")"
      cp "$source_rules/$rel" "$GLB_ROOT/rules/$rel"
      log "Stored global rule $rel"
    else
      log "WARNING: rule $rel not found in repo — not stored"
    fi
  done <<< "$RULES_ON"
}

wire_claude_rules() {
  local rel
  if [ "$HARNESS_CLAUDE" != "1" ]; then
    purge_store_links "$CLAUDE_DIR/rules" rules
    return 0
  fi
  if [ -n "$RULES_ON" ]; then
    tool_present "$CLAUDE_DIR" "Claude Code" || return 0
  fi
  while IFS= read -r rel; do
    [ -n "$rel" ] || continue
    link_store_entry "$CLAUDE_DIR/rules/$rel" "$GLB_ROOT/rules/$rel" "claude-rule"
  done <<< "$RULES_ON"
  # A rule flipped to installed:false is unwired too, so the flag turns it off rather than only
  # stopping updates. The store copy stays.
  while IFS= read -r rel; do
    [ -n "$rel" ] || continue
    unlink_store_file "$CLAUDE_DIR/rules/$rel" "$GLB_ROOT/rules/$rel"
  done <<< "$RULES_OFF"
}

# codex_agents_block FILE MODE [ARG] — edit only our block in FILE, never anything around it.
#   state          print none | block
#   apply  BODY    replace our block in place (or append one) with BODY; print same | changed | appended
#   remove         delete our block and the blank line before it; print none | removed | removed-empty
codex_agents_block() {
  python3 - "$1" "$GLOBAL_MARKER" "$CODEX_BLOCK_END" "$2" "${3:-}" <<'PYEOF'
import os, sys
path, marker, end_token, mode, arg = sys.argv[1:6]
begin = '<!-- ' + marker + ' (added by install-skills): regenerated on every run -->'
end = '<!-- ' + end_token + ' -->'
text = open(path, encoding='utf-8').read() if os.path.exists(path) else ''
lines = text.split('\n')

start = next((i for i, line in enumerate(lines) if marker in line), None)
stop = None
if start is not None:
    for j in range(start + 1, len(lines)):
        if marker in lines[j]:
            break
        if end_token in lines[j]:
            stop = j
            break
    if stop is None:
        stop = start

if mode == 'state':
    print('none' if start is None else 'block')
elif mode == 'apply':
    block = [begin] + arg.rstrip('\n').split('\n') + [end]
    if start is None:
        prefix = text
        if prefix and not prefix.endswith('\n'):
            prefix += '\n'
        if prefix:
            prefix += '\n'
        result, outcome = prefix + '\n'.join(block) + '\n', 'appended'
    elif lines[start:stop + 1] == block:
        result, outcome = text, 'same'
    else:
        result, outcome = '\n'.join(lines[:start] + block + lines[stop + 1:]), 'changed'
    if outcome != 'same':
        with open(path, 'w', encoding='utf-8') as f:
            f.write(result)
    print(outcome)
elif mode == 'remove':
    if start is None:
        print('none')
    else:
        kept = lines[:start]
        if kept and kept[-1] == '':
            kept.pop()
        kept += lines[stop + 1:]
        result = '\n'.join(kept)
        with open(path, 'w', encoding='utf-8') as f:
            f.write(result)
        print('removed-empty' if result.strip() == '' else 'removed')
PYEOF
}

# strip_codex_block FILE — remove our block. A file that now holds nothing and was never snapshotted was
# created by us, so removing it is the exact inverse; one that pre-dates the install keeps its (empty) self.
strip_codex_block() {
  local file="$1" outcome
  [ -f "$file" ] || return 0
  outcome="$(codex_agents_block "$file" remove)"
  case "$outcome" in
    removed*) log "Removed the mimisbrunnr rules index from $file" ;;
  esac
  if [ "$outcome" = "removed-empty" ] && ! ls "$file".smooth-mimisbrunnr-[0-9]* >/dev/null 2>&1; then
    rm -f "$file"
    log "Removed $file (the installer created it and nothing else is in it)"
  fi
}

# render_rules_index — the text Codex is given in place of the rule files: each enabled rule's path,
# scope and one-line description, grouped by scope.
render_rules_index() {
  python3 - "$GLB_ROOT/rules" "$RULES_ON" <<'PYEOF'
import os, re, sys
root, enabled = sys.argv[1], [r for r in sys.argv[2].splitlines() if r]


def unquote(value):
    value = value.strip()
    if len(value) >= 2 and value[0] == value[-1] and value[0] in '\'"':
        value = value[1:-1]
    return value


def describe(path):
    text = open(path, encoding='utf-8').read()
    match = re.match(r'^---\r?\n(.*?)\r?\n---\r?\n', text, re.S)
    front = match.group(1) if match else ''

    def field(key):
        found = re.search(r'^%s:\s*(.+?)\s*$' % key, front, re.M)
        return unquote(found.group(1)) if found else ''

    scope = field('applyTo') or field('globs') or '**'
    summary = field('description')
    if not summary:
        heading = re.search(r'^#\s+(.+)$', text[match.end():] if match else text, re.M)
        summary = heading.group(1).strip() if heading else os.path.basename(path)
    if len(summary) > 100:
        summary = summary[:99].rstrip() + '…'
    return scope, summary


groups = {}
for rel in enabled:
    path = os.path.join(root, rel)
    if os.path.isfile(path):
        scope, summary = describe(path)
        groups.setdefault(scope, []).append('- %s - %s' % (path, summary))

if not groups:
    print('No global rules are installed.')
    raise SystemExit(0)
print('Codex does not import files from AGENTS.md, so the rules below are NOT loaded for you.')
print('Open the ones that apply with your file tools before you work, and follow them.')
if '**' in groups:
    print('\nRead at the start of every task:')
    print('\n'.join(groups.pop('**')))
for scope, entries in groups.items():
    print('\nWhen editing files matching `%s`:' % scope)
    print('\n'.join(entries))
PYEOF
}

# wire_codex_agents — keep one delimited block in ~/.codex/AGENTS.md holding the rule index, replaced
# in place each run. Nothing is written when there is no enabled rule (a machine with nothing to say does
# not get an empty block appended to its AGENTS.md), and a block that is no longer wanted is removed.
wire_codex_agents() {
  local file="$CODEX_DIR/AGENTS.md" index backup outcome
  if [ "$HARNESS_CODEX" != "1" ] || [ -z "$RULES_ON" ]; then
    strip_codex_block "$file"
    return 0
  fi
  tool_present "$CODEX_DIR" Codex || return 0
  index="$(render_rules_index)"
  if [ ! -f "$file" ]; then
    : > "$file"
    codex_agents_block "$file" apply "$index" >/dev/null
    record_link "agents-import" "$file"
    log "Created $file with the global rules index"
    return 0
  fi
  if [ "$(codex_agents_block "$file" state)" = "none" ]; then
    # Snapshot only here: a file that already holds our block either took its snapshot on the first
    # install or was created by us, and a snapshot of that would capture our own block.
    backup="$(backup_inplace "$file")"
    if [ -n "$backup" ]; then
      log "Backed up $file -> $backup"
    fi
    codex_agents_block "$file" apply "$index" >/dev/null
    record_link "agents-import" "$file" "$backup"
    log "Appended the global rules index to $file"
    return 0
  fi
  outcome="$(codex_agents_block "$file" apply "$index")"
  record_link "agents-import" "$file"
  if [ "$outcome" = "changed" ]; then
    log "Updated the global rules index in $file"
  else
    log "Global rules index already current in $file"
  fi
}

# edit_opencode_instructions CONFIG WANTED SNAPSHOT — make opencode.json `instructions` hold exactly the
# WANTED rules (newline-separated, relative to the store's rules dir) among the entries under the store,
# one absolute path per rule. Per-file entries, never a glob: a glob cannot exclude one rule. Entries the
# operator put there are kept. Prints `changed<TAB>present<TAB>snapshot`. A file that does not parse is a
# hard error and is never overwritten.
edit_opencode_instructions() {
  python3 - "$1" "$GLB_ROOT" "$2" "$3" <<'PYEOF'
import glob, json, os, shutil, sys
from datetime import datetime, timezone
path, glb_root, wanted_raw, snap = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4] == '1'
enabled = [rel for rel in wanted_raw.splitlines() if rel]
try:
    data = json.load(open(path, encoding='utf-8'))
except json.JSONDecodeError as exc:
    sys.stderr.write('install-skills: %s is not valid JSON (%s); fix it, then re-run\n' % (path, exc))
    raise SystemExit(2)
if not isinstance(data, dict) or not isinstance(data.get('instructions', []), list):
    sys.stderr.write('install-skills: %s has an unexpected shape (instructions is not a list); fix it, then re-run\n' % path)
    raise SystemExit(2)
inst = data.get('instructions', [])
prefix = glb_root + '/rules/'
wanted = [prefix + rel for rel in enabled]
kept = [g for g in inst if not (isinstance(g, str) and g.startswith(prefix) and g not in wanted)]
added = [w for w in wanted if w not in kept]
changed = kept != inst or bool(added)
backup = ''
if changed:
    if snap and not glob.glob(path + '.smooth-mimisbrunnr-[0-9]*'):
        backup = path + '.smooth-mimisbrunnr-' + datetime.now(timezone.utc).strftime('%Y%m%d-%H%M%S')
        shutil.copyfile(path, backup)
    result = kept + added
    if result:
        data['instructions'] = result
    else:
        data.pop('instructions', None)
    with open(path, 'w', encoding='utf-8') as f:
        json.dump(data, f, indent=2, ensure_ascii=False)
        f.write('\n')
present = any(isinstance(g, str) and g.startswith(prefix) for g in kept + added)
print('%s\t%s\t%s' % ('1' if changed else '0', '1' if present else '0', backup))
PYEOF
}

wire_opencode_instructions() {
  local config="$OPENCODE_DIR/opencode.json" wanted="$RULES_ON" result changed present backup
  if [ ! -f "$config" ]; then
    if [ -n "$RULES_ON" ]; then
      log "opencode.json not found ($config) — skipping global rules instructions"
    fi
    return 0
  fi
  if [ "$HARNESS_OPENCODE" != "1" ]; then
    wanted=""
  fi
  # Nothing to add and nothing of ours in the file: do not even parse the operator's opencode.json.
  if [ -z "$wanted" ] && ! grep -qF "$GLB_ROOT/rules/" "$config"; then
    return 0
  fi
  result="$(edit_opencode_instructions "$config" "$wanted" 1)" || die "cannot update $config"
  IFS=$'\t' read -r changed present backup <<< "$result"
  if [ -n "${backup:-}" ]; then
    log "Backed up $config -> $backup"
  fi
  if [ "$changed" = "1" ]; then
    log "Updated global rules instructions in $config"
  fi
  if [ "$present" = "1" ]; then
    record_link "opencode-config" "$config" "${backup:-}"
  fi
}

# store_global_hooks — copy the installed:true hooks into the store, keeping the exec bit.
store_global_hooks() {
  local source_hooks="$INSTALL_TMP/repo/.agents/hooks" name
  while IFS= read -r name; do
    [ -n "$name" ] || continue
    if [ -e "$source_hooks/$name" ]; then
      rm -rf "$GLB_ROOT/hooks/$name"
      cp -R "$source_hooks/$name" "$GLB_ROOT/hooks/$name"
      log "Stored global hook $name"
    else
      log "WARNING: hook $name not found in repo — not stored"
    fi
  done <<< "$HOOKS_ON"
}

# edit_codex_hooks HOOKS_FILE ENABLED DISABLED PURGE_PREFIX SNAPSHOT — make UserPromptSubmit carry the
# ENABLED commands (newline-separated, absolute), dropping DISABLED ones and every command under
# PURGE_PREFIX; deduped by exact command, the operator's own hooks kept. Prints
# `added<TAB>removed<TAB>present<TAB>deleted<TAB>snapshot`. A file left empty and never snapshotted was ours
# and is deleted.
edit_codex_hooks() {
  python3 - "$1" "$2" "$3" "$4" "$5" <<'PYEOF'
import glob, json, os, shutil, sys
from datetime import datetime, timezone
path = sys.argv[1]
cmds = [c for c in sys.argv[2].splitlines() if c]
disabled = set(c for c in sys.argv[3].splitlines() if c)
purge_prefix, snap = sys.argv[4], sys.argv[5] == '1'


def is_dropped(entry):
    if not isinstance(entry, dict):
        return False
    command = entry.get('command')
    if command in disabled:
        return True
    return bool(purge_prefix) and isinstance(command, str) and command.startswith(purge_prefix)


data = {}
existed = os.path.exists(path)
if existed:
    raw = open(path, encoding='utf-8').read()
    if raw.strip():
        try:
            data = json.loads(raw)
        except json.JSONDecodeError as exc:
            sys.stderr.write('install-skills: %s is not valid JSON (%s); fix it, then re-run\n' % (path, exc))
            raise SystemExit(2)
if not isinstance(data, dict):
    sys.stderr.write('install-skills: %s has an unexpected shape (not an object); fix it, then re-run\n' % path)
    raise SystemExit(2)
hooks = data.get('hooks')
if not isinstance(hooks, dict):
    hooks = {}
ups = hooks.get('UserPromptSubmit')
if not isinstance(ups, list):
    ups = []
# Drop disabled/purged commands. Only a group this empties is dropped; a group the operator shares with
# other commands keeps them.
removed = 0
pruned = []
for group in ups:
    if isinstance(group, dict) and isinstance(group.get('hooks'), list):
        keep = [h for h in group['hooks'] if not is_dropped(h)]
        dropped = len(group['hooks']) - len(keep)
        if dropped:
            removed += dropped
            if not keep:
                continue
            group = dict(group, hooks=keep)
    pruned.append(group)
existing = set()
for group in pruned:
    if isinstance(group, dict) and isinstance(group.get('hooks'), list):
        for h in group['hooks']:
            if isinstance(h, dict) and 'command' in h:
                existing.add(h['command'])
new = [{'command': c, 'type': 'command'} for c in cmds if c not in existing]
changed = bool(new) or removed > 0
backup = ''
deleted = 0
if changed:
    # Snapshot a pre-existing file once, before our first edit. On a re-run the dedupe above makes
    # `new` empty, so our own earlier hooks are never snapshotted.
    if existed and snap and not glob.glob(path + '.smooth-mimisbrunnr-[0-9]*'):
        backup = path + '.smooth-mimisbrunnr-' + datetime.now(timezone.utc).strftime('%Y%m%d-%H%M%S')
        shutil.copyfile(path, backup)
    if new:
        # UserPromptSubmit ignores `matcher`, so the new group omits it.
        pruned.append({'hooks': new})
    if pruned:
        hooks['UserPromptSubmit'] = pruned
    else:
        hooks.pop('UserPromptSubmit', None)
    if hooks:
        data['hooks'] = hooks
    else:
        data.pop('hooks', None)
    if not data and not glob.glob(path + '.smooth-mimisbrunnr-[0-9]*'):
        os.remove(path)
        deleted = 1
    else:
        with open(path, 'w', encoding='utf-8') as f:
            json.dump(data, f, indent=2, ensure_ascii=False)
            f.write('\n')
ours = set(cmds)
present = any(
    isinstance(g, dict) and isinstance(g.get('hooks'), list)
    and any(isinstance(h, dict) and h.get('command') in ours for h in g['hooks'])
    for g in pruned
)
# The snapshot path goes last: `read` with a tab IFS collapses an empty middle field and shifts the rest.
print('%s\t%s\t%s\t%s\t%s' % (len(new), removed, '1' if present else '0', deleted, backup))
PYEOF
}

# wire_codex_hooks — register the installed:true hooks in ~/.codex/hooks.json with absolute store
# commands. UserPromptSubmit only: a hook registered on an event it was not written for fires wrongly.
wire_codex_hooks() {
  local hooks_file="$CODEX_DIR/hooks.json" enabled="" disabled="" purge_prefix="" name
  while IFS= read -r name; do
    [ -n "$name" ] || continue
    if [ -f "$GLB_ROOT/hooks/$name" ]; then
      enabled="$enabled$GLB_ROOT/hooks/$name"$'\n'
    else
      log "WARNING: owned hook $name not in store — not registered"
    fi
  done <<< "$HOOKS_ON"
  while IFS= read -r name; do
    [ -n "$name" ] || continue
    disabled="$disabled$GLB_ROOT/hooks/$name"$'\n'
  done <<< "$HOOKS_OFF"
  # Codex off: register nothing and remove every command under the store's hooks dir, so a hook that
  # is no longer owned goes too.
  if [ "$HARNESS_CODEX" != "1" ]; then
    enabled=""
    purge_prefix="$GLB_ROOT/hooks/"
  fi
  if [ -z "$enabled" ] && [ -z "$disabled" ] && [ -z "$purge_prefix" ]; then
    log "No owned hooks to register globally"
    return 0
  fi
  if [ ! -d "$CODEX_DIR" ]; then
    if [ -n "$enabled" ]; then
      log "Codex config not found ($CODEX_DIR) — skipping global Codex hooks"
    fi
    return 0
  fi
  # Nothing to add and no file of ours to clean: do not even parse the operator's hooks.json.
  if [ -z "$enabled" ] && ! { [ -f "$hooks_file" ] && grep -qF "$GLB_ROOT/hooks/" "$hooks_file"; }; then
    return 0
  fi
  local result added removed present deleted backup
  result="$(edit_codex_hooks "$hooks_file" "$enabled" "$disabled" "$purge_prefix" 1)" || die "cannot update $hooks_file"
  IFS=$'\t' read -r added removed present deleted backup <<< "$result"
  if [ -n "${backup:-}" ]; then
    log "Backed up $hooks_file -> $backup"
  fi
  if [ "${added:-0}" -gt 0 ]; then
    log "Registered $added owned hook(s) in $hooks_file"
  fi
  if [ "${removed:-0}" -gt 0 ]; then
    log "Unregistered $removed hook(s) from $hooks_file"
  fi
  if [ "${present:-0}" = "1" ]; then
    record_link "hooks-merge" "$hooks_file" "${backup:-}"
  fi
  if [ "$HARNESS_CODEX" = "1" ] && [ "${added:-0}" -gt 0 ]; then
    log "Trust the newly registered Codex hooks with: /hooks"
  fi
}

# write_global_manifest SKILLS_JSON RULES_JSON HOOKS_JSON HARNESSES_JSON — write the global manifest. A
# category this run did not cover passes empty and keeps the list the previous run recorded. Links are
# merged by (type, path) so a re-run that links nothing new does not wipe the history uninstall reads, and
# an upsert without a `detail` keeps the earlier one.
write_global_manifest() {
  local skills_json="$1" rules_json="${2:-}" hooks_json="${3:-}" harnesses_json="${4:-}"
  local links_json
  links_json="$(python3 -c '
import json, sys
links = []
for line in sys.argv[1:]:
    if line == "":
        continue
    parts = line.split("\t")
    entry = {"type": parts[0], "path": parts[1]}
    if len(parts) > 2 and parts[2]:
        entry["detail"] = parts[2]
    links.append(entry)
print(json.dumps(links))
' ${LINKS[@]+"${LINKS[@]}"})" || links_json="[]"
  python3 - "$GLOBAL_MANIFEST" "$skills_json" "$links_json" "$rules_json" "$hooks_json" "$harnesses_json" <<'PYEOF'
import json, sys, os
from datetime import datetime, timezone
path, skills_json, links_json = sys.argv[1], sys.argv[2], sys.argv[3]
rules_json, hooks_json, harnesses_json = sys.argv[4], sys.argv[5], sys.argv[6]
SYMLINK_TYPES = ('codex-skill', 'claude-skill', 'opencode-skill', 'claude-rule')
KEY_ORDER = ('updated', 'harnesses', 'skills', 'rules', 'hooks', 'links')
new_links = json.loads(links_json)
now = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
existing = {}
if os.path.exists(path):
    with open(path, encoding='utf-8') as f:
        raw = f.read()
    if raw.strip():
        loaded = json.loads(raw)
        if isinstance(loaded, dict):
            existing = loaded
data = json.loads(skills_json) if skills_json else dict(existing)
if not isinstance(data, dict):
    data = {}
if 'skills' not in data:
    data['skills'] = existing.get('skills', [])
for key, raw in (('rules', rules_json), ('hooks', hooks_json)):
    if raw:
        data[key] = json.loads(raw)
    elif key not in data and key in existing:
        data[key] = existing[key]
if harnesses_json:
    data['harnesses'] = json.loads(harnesses_json)
elif 'harnesses' not in data and 'harnesses' in existing:
    data['harnesses'] = existing['harnesses']
merged = []
seen = set()
for link in existing.get('links', []):
    merged.append(link)
    seen.add((link.get('type'), link.get('path')))
for link in new_links:
    key = (link.get('type'), link.get('path'))
    if key in seen:
        for i, m in enumerate(merged):
            if (m.get('type'), m.get('path')) == key:
                if 'detail' not in link and 'detail' in m:
                    link = dict(link, detail=m['detail'])
                merged[i] = link
                break
    else:
        merged.append(link)
        seen.add(key)
# `enabled` is a status recomputed every run, not a control: true exactly when the symlink is on disk and
# points where the record says. Config-edit records carry no such field.
for link in merged:
    if link.get('type') in SYMLINK_TYPES:
        link_path = link.get('path', '')
        link['enabled'] = os.path.islink(link_path) and os.readlink(link_path) == link.get('detail')
data['updated'] = now
data['links'] = merged
data = dict([(k, data[k]) for k in KEY_ORDER if k in data] + [(k, v) for k, v in data.items() if k not in KEY_ORDER])
with open(path, 'w') as f:
    json.dump(data, f, indent=2)
    f.write('\n')
PYEOF
  log "Wrote global manifest $GLOBAL_MANIFEST"
}

# run_global_install — build the store, wire the tools, write the manifest last.
run_global_install() {
  local existing_json new_json="" installed_names
  ensure_global_store
  clone_source

  # A corrupt global manifest is a hard error in every category run, never a silent reset from defaults.
  existing_json="$(json_read "$GLOBAL_MANIFEST")" \
    || die "cannot read $GLOBAL_MANIFEST — fix or delete it, then re-run"

  # Resolved whatever categories this run covers: an off tool is not installed into and is cleaned of
  # what this installer put there.
  GLB_HARNESSES_JSON="$(reconcile_harnesses "$existing_json")"
  HARNESS_CLAUDE="$(harness_flag "$GLB_HARNESSES_JSON" claude-code)"
  HARNESS_CODEX="$(harness_flag "$GLB_HARNESSES_JSON" codex)"
  HARNESS_OPENCODE="$(harness_flag "$GLB_HARNESSES_JSON" opencode)"
  if [ "$HARNESS_CLAUDE" != "1" ]; then log "Tool claude-code is off — not installing, removing what is wired"; fi
  if [ "$HARNESS_CODEX" != "1" ]; then log "Tool codex is off — not installing, removing what is wired"; fi
  if [ "$HARNESS_OPENCODE" != "1" ]; then log "Tool opencode is off — not installing, removing what is wired"; fi

  if [ "$DO_RULES" = "1" ]; then
    GLB_RULES_JSON="$(reconcile_global_items rules "$existing_json" "$(owned_rules)")"
    RULES_ON="$(global_items_flagged "$GLB_RULES_JSON" true)"
    RULES_OFF="$(global_items_flagged "$GLB_RULES_JSON" false)"
  fi
  if [ "$DO_HOOKS" = "1" ]; then
    GLB_HOOKS_JSON="$(reconcile_global_items hooks "$existing_json" "$(owned_hooks)")"
    HOOKS_ON="$(global_items_flagged "$GLB_HOOKS_JSON" true)"
    HOOKS_OFF="$(global_items_flagged "$GLB_HOOKS_JSON" false)"
  fi

  if [ "$DO_SKILLS" = "1" ]; then
    log "Syncing global installation.json…"
    new_json="$(build_installation_json "$GLB_ROOT/skills" "$existing_json" \
      "$(list_source_skills "$INSTALL_TMP/repo/.agents/skills" | while IFS= read -r name; do
        is_owned_skill "$name" && printf '%s\n' "$name"
      done | python3 -c "
import json, sys
print(json.dumps(sorted(line.strip() for line in sys.stdin if line.strip())))
")")"
    log "Storing global skills…"
    update_installed_skills "$new_json" "$GLB_ROOT/skills"
    installed_names="$(manifest_installed_names "$new_json")"
    wire_global_skills "$CLAUDE_DIR" "Claude Code" claude-skill "$HARNESS_CLAUDE" "$installed_names"
    wire_global_skills "$CODEX_DIR" Codex codex-skill "$HARNESS_CODEX" "$installed_names"
    wire_global_skills "$OPENCODE_DIR" OpenCode opencode-skill "$HARNESS_OPENCODE" "$installed_names"
  fi

  if [ "$DO_RULES" = "1" ]; then
    store_global_rules
    wire_claude_rules
    wire_codex_agents
    wire_opencode_instructions
  fi

  if [ "$DO_HOOKS" = "1" ]; then
    store_global_hooks
    wire_codex_hooks
  fi

  # Written last: the skills refresh above must not be able to clobber the manifest it read.
  if [ "$DO_SKILLS" = "1" ]; then
    write_global_manifest "$new_json" "$GLB_RULES_JSON" "$GLB_HOOKS_JSON" "$GLB_HARNESSES_JSON"
  else
    write_global_manifest "$existing_json" "$GLB_RULES_JSON" "$GLB_HOOKS_JSON" "$GLB_HARNESSES_JSON"
  fi
}

# unglobalize — remove exactly what the manifest recorded, and nothing the other tools or the operator
# put there: Codex's .system/, other installers' hooks and blocks, the operator's own entries. A content
# file is snapshotted (<file>.smooth-mimisbrunnr-uninstall-<stamp>) before it is edited, only when we are
# about to change it. The store and the operator's skills/rules/hooks flags stay; the link history is cleared.
unglobalize() {
  local records type path detail seen="" stamp snapshot result deleted
  [ -f "$GLOBAL_MANIFEST" ] || { log "No global install manifest at $GLOBAL_MANIFEST — nothing to uninstall"; return 0; }
  json_read "$GLOBAL_MANIFEST" >/dev/null || die "cannot read $GLOBAL_MANIFEST — fix or delete it, then re-run"
  records="$(python3 - "$GLOBAL_MANIFEST" <<'PYEOF'
import json, sys
for link in json.load(open(sys.argv[1], encoding='utf-8')).get('links', []):
    print('%s\t%s\t%s' % (link.get('type', ''), link.get('path', ''), link.get('detail', '')))
PYEOF
)"
  stamp="$(date -u +%Y%m%d-%H%M%S)"
  while IFS=$'\t' read -r type path detail; do
    [ -n "$type" ] || continue
    case "$seen" in *"|$type $path|"*) continue ;; esac
    seen="$seen|$type $path|"
    snapshot="$path.smooth-mimisbrunnr-uninstall-$stamp"
    case "$type" in
      claude-skill | codex-skill | opencode-skill | claude-rule)
        if [ -L "$path" ] && [ "$(readlink "$path")" = "$detail" ]; then
          rm -f "$path"
          log "Removed link $path"
        fi
        ;;
      agents-import)
        # Only our delimited block is removed, so there is nothing of the operator's to snapshot.
        strip_codex_block "$path"
        ;;
      opencode-config)
        if [ -f "$path" ] && grep -qF "$GLB_ROOT/rules/" "$path"; then
          cp "$path" "$snapshot"
          edit_opencode_instructions "$path" "" 0 >/dev/null || die "cannot update $path"
          log "Removed the store rules from $path (previous content kept at $snapshot)"
        fi
        ;;
      hooks-merge)
        if [ -f "$path" ] && grep -qF "$GLB_ROOT/hooks/" "$path"; then
          cp "$path" "$snapshot"
          result="$(edit_codex_hooks "$path" "" "" "$GLB_ROOT/hooks/" 0)" || die "cannot update $path"
          IFS=$'\t' read -r _ _ _ deleted _ <<< "$result"
          # A hooks.json we created and have now emptied is gone, so a snapshot of it is just litter.
          if [ "${deleted:-0}" = "1" ]; then
            rm -f "$snapshot"
            log "Removed $path (the installer created it and nothing else is in it)"
          else
            log "Removed the store hooks from $path (previous content kept at $snapshot)"
          fi
        fi
        ;;
    esac
  done <<< "$records"
  python3 - "$GLOBAL_MANIFEST" <<'PYEOF'
import json, sys
from datetime import datetime, timezone
path = sys.argv[1]
data = json.load(open(path, encoding='utf-8'))
data['links'] = []
data['updated'] = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
with open(path, 'w') as f:
    json.dump(data, f, indent=2)
    f.write('\n')
PYEOF
  log "Uninstalled the global install — store left at $GLB_ROOT (remove it to reclaim space)"
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

main() {
  local skills_root json_path existing_json new_json

  parse_arguments "$@"

  if [ "$DO_GLOBAL" = "1" ]; then
    [ -z "${SKILLS_ROOT:-}" ] || die "-g cannot be combined with SKILLS_ROOT — the global install owns its own store; drop one of them"
    resolve_global_paths
    if [ "$DO_UNINSTALL" = "1" ]; then
      unglobalize
    else
      run_global_install
    fi
    log "Done."
    return 0
  fi

  if [ -n "${SKILLS_ROOT:-}" ]; then
    skills_root="$SKILLS_ROOT"
    [ -d "$skills_root" ] || die "SKILLS_ROOT '$skills_root' does not exist"
  else
    skills_root="$(find_skills_root)" \
      || die "no skills directory found — expected one of .agents/skills, .claude/skills, .codex/skills (create one, or set SKILLS_ROOT)"
  fi

  json_path="$skills_root/_mimisbrunnr-shared/installation.json"
  mkdir -p "$skills_root/_mimisbrunnr-shared"

  log "Skills root: $skills_root"

  # A non-zero status here is the corrupt-manifest case; json_read has already said why. Swallowing it
  # would rebuild the manifest from defaults and re-enable skills the operator had switched off.
  existing_json="$(json_read "$json_path")" \
    || die "cannot read $json_path — fix or delete it, then re-run"

  clone_source

  log "Syncing installation.json…"
  new_json="$(build_installation_json "$skills_root" "$existing_json" \
    "$(list_source_skills "$INSTALL_TMP/repo/.agents/skills" | while IFS= read -r name; do
      is_owned_skill "$name" && printf '%s\n' "$name"
    done | python3 -c "
import json, sys
print(json.dumps(sorted(line.strip() for line in sys.stdin if line.strip())))
")")"

  log "Updating installed skills…"
  update_installed_skills "$new_json" "$skills_root"

  # Written last: the shared-folder refresh happens during the update, and the manifest lives inside it.
  json_write "$json_path" "$new_json"

  log "Done. State: $json_path"
}

main "$@"
