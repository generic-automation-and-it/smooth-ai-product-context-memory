# Skills installers

One-command installers for the **agent skills** — the folders under `.agents/skills/` that Claude Code,
Codex and Copilot discover.

| Script | Platform | Interpreter |
|---|---|---|
| [`install-skills.sh`](install-skills.sh) | macOS, Linux | bash, `git`, `python3` |
| [`install-skills.ps1`](install-skills.ps1) | Windows, or anywhere `pwsh` 7 runs | PowerShell 7.0 or later, `git` |

The bash script needs `python3` only to read and write the manifest JSON; it is already a dependency of
every `mimisbrunnr-*` skill, so nothing new is introduced. The PowerShell port has no such dependency —
`ConvertFrom-Json` and `ConvertTo-Json` are built in.

## One-liners

```bash
curl -fsSL https://raw.githubusercontent.com/generic-automation-and-it/smooth-ai-product-context-memory/main/scripts/install-skills.sh | bash
```

```powershell
irm https://raw.githubusercontent.com/generic-automation-and-it/smooth-ai-product-context-memory/main/scripts/install-skills.ps1 | iex
```

Or from a checkout: `scripts/install-skills.sh`, `./scripts/install-skills.ps1`.

Both take the same three environment overrides, so a local clone or a fork works the same way:

| Variable | Default | Purpose |
|---|---|---|
| `SKILLS_REPO` | the upstream GitHub repo | the clone source |
| `SKILLS_BRANCH` | `main` | the branch to clone |
| `SKILLS_ROOT` | *(unset — searched)* | force a skills root, skipping the search |

## What a run does

**1. Find the skills root, in this order.** `.agents/skills` → `.claude/skills` → `.codex/skills`. The
first that exists is used. If none exists the run **fails and creates nothing** — a folder the operator
never asked for is not an install. Set `SKILLS_ROOT` to install somewhere else.

In this repository `.claude/skills` and `.codex/skills` are symlinks to `.agents/skills`, so all three
paths resolve to the same tree and the first match is the real one.

**2. Reconcile the manifest.** The catalogue is the **union** of the skills the clone source offers and
the skills already on disk. Deriving it from disk alone would make a first install into an empty folder a
no-op — nothing on disk means nothing to install, and the operator's very first run would leave them with
an empty folder and a manifest listing nothing.

State lives in `<root>/_mimisbrunnr-shared/installation.json`:

```json
{
  "updated": "2026-10-15T13:10:22Z",
  "skills": [
    { "name": "ai-terse", "installed": true },
    { "name": "git-sync", "installed": false }
  ]
}
```

| Skill | Result |
|---|---|
| in the catalogue, new to the manifest | added as `installed: true` |
| in the catalogue, already in the manifest | keeps its existing flag, `true` **or** `false` |
| in the manifest, in neither the source nor disk | removed from the manifest |
| manifest will not parse | **run fails**, file left untouched |
| no manifest at all | created from the catalogue, every entry `installed: true` |

**3. Update only the `installed: true` skills.** The repo is shallow-cloned once and those folders are
copied over the existing ones. A skill marked `false` is never touched, so a local edit inside one
survives every run until you set its flag back.

**4. Refresh `_mimisbrunnr-shared`.** That folder is not a skill and never appears in the manifest, but
every installed skill that calls a shared script is broken without it — so it refreshes on every run,
whatever the flags say. Its contents are reconciled rather than merged: an entry the source no longer has
is removed, except `installation.json` itself, which is this script's own state.

**5. Write the manifest last**, stamped with the current UTC time. That ordering is deliberate — see
[Write ordering](#write-ordering-is-load-bearing).

## Turning a skill off

Edit the manifest and re-run. The flag is read, never guessed:

```bash
# one skill off
python3 - <<'EOF'
import json
p = '.agents/skills/_mimisbrunnr-shared/installation.json'
d = json.load(open(p))
for s in d['skills']:
    if s['name'] == 'git-sync':
        s['installed'] = False
json.dump(d, open(p, 'w'), indent=2)
EOF
scripts/install-skills.sh
```

The flag only governs **updates**. It does not remove or disable anything: a `false` skill stays on disk
and stays discoverable, it is simply never overwritten again. Delete the folder if you want it gone — the
next run drops it from the manifest.

## Write ordering is load-bearing

The manifest is written **after** the update, not before. The manifest lives inside
`_mimisbrunnr-shared/`, and step 4 rewrites that folder — so writing it first would have the refresh
clobber it. The update also reads the *merged* manifest, so on a first install the run that was supposed
to install the skills can see which ones it just decided to install.

Both ports are held to this by `scripts/test-install-skills.sh`, including the case where a manifest
already exists and one skill's flag is `false`.

## Overwriting local work

An `installed: true` skill is **replaced** on every run, so an edit inside one is lost on the next
install. That is the point of an updater, but it is a real hazard: either set the flag to `false` first,
or move the change somewhere the installer does not own.

## Testing

```bash
bash scripts/test-install-skills.sh
```

38 cases across both ports, no network and no container engine needed: the clone source is the checkout
itself (`SKILLS_REPO` pointed at the repo, `SKILLS_BRANCH` at the current branch), so a harness that
reached GitHub would be red for reasons unrelated to the code. Runs in the PR gate. Verified green on
macOS (bash 3.2 with `pwsh` present — 38/38) and on GNU/Linux in `ubuntu:24.04` (18/18, `pwsh` absent).

The `ps1` cases and the cross-port parity cases are **skipped with a stated reason** when `pwsh` is not
installed — which is the case on the `ubuntu-latest` gate runner. They are exercised locally where `pwsh`
is present. See `scripts/AGENTS.md` → Test References for what that gap does and does not cover.