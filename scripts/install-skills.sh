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
# The owned set lives in `is_owned_skill()` below AND in the PowerShell port's `Test-OwnedSkill`.
# The two must match; `test-install-skills.sh` holds them to each other (parity cases).
#
# Dependencies: bash, git, python3 (already a dependency of every mimisbrunnr-* skill).
#
# Environment overrides:
#   SKILLS_REPO    — git URL to clone from (default: the upstream GitHub repo)
#   SKILLS_BRANCH  — branch to clone (default: main)
#   SKILLS_ROOT    — force a specific skills root (skips the search)

set -euo pipefail

SKILLS_REPO="${SKILLS_REPO:-https://github.com/generic-automation-and-it/smooth-ai-product-context-memory.git}"
# `-` not `:-`, and that single character is load-bearing. `:-` substitutes the default for an EMPTY
# value as well as an unset one, so `SKILLS_BRANCH=''` — which means "whatever the remote's default is",
# and is the only honest answer in a detached checkout where there is no branch name to pass — silently
# became "main" and then failed on any source that has no main branch. `-` substitutes only when unset.
SKILLS_BRANCH="${SKILLS_BRANCH-main}"

die() { echo "install-skills: $*" >&2; exit 1; }
log() { echo "install-skills: $*" >&2; }

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
  case "$1" in
    mimisbrunnr-*) return 0 ;;
    ai-understanding | ai-brain-dump) return 0 ;;
  esac
  return 1
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
    [ -e "$entry" ] || continue
    name="$(basename "$entry")"
    [ "$name" = "installation.json" ] && continue
    if [ -d "$source/$name" ] && [ -d "$entry" ]; then
      sweep_stale_entries "$entry" "$source/$name"
    elif [ ! -e "$source/$name" ]; then
      rm -rf "$entry"
    fi
  done
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

update_installed_skills() {
  local installation_json="$1" skills_root="$2"
  local installed_names

  installed_names="$(printf '%s' "$installation_json" | python3 -c "
import json, sys
data = json.load(sys.stdin)
for s in data.get('skills', []):
    if s.get('installed'):
        print(s['name'])
")"

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
      cp -R "$INSTALL_TMP/repo/.agents/skills/$name" "$skills_root/$name"
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
    cp -R "$shared_source/." "$shared_dest"
    # Recursive, and that recursion is the point: a top-level-only sweep keeps every entry the source
    # still has *as a folder* while leaving the files inside it that the source dropped, which is where
    # a retired script actually goes.
    sweep_stale_entries "$shared_dest" "$shared_source"
    log "Updated _mimisbrunnr-shared"
  fi
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

main() {
  local skills_root json_path existing_json new_json

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
