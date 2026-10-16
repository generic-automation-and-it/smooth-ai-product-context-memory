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

**2. Reconcile the manifest — for the owned skills only.** The installer manages exactly nine skills:
the seven `mimisbrunnr-*` skills plus `ai-understanding` and `ai-brain-dump`. The catalogue is the
**owned union** of what the clone source offers and what is already on disk. Anything else in the
target tree — local coding skills, operator experiments — is left untouched and never enters the
manifest. Deriving the catalogue from disk alone would make a first install into an empty folder a
no-op — nothing on disk means nothing to install, and the operator's very first run would leave them
with an empty folder and a manifest listing nothing.

State lives in `<root>/_mimisbrunnr-shared/installation.json`:

```json
{
  "updated": "2026-10-15T13:10:22Z",
  "skills": [
    { "name": "ai-brain-dump", "installed": true },
    { "name": "mimisbrunnr-odin-context-memory", "installed": false }
  ]
}
```

| Skill | Result |
|---|---|
| owned, in the catalogue, new to the manifest | added as `installed: true` |
| owned, in the catalogue, already in the manifest | keeps its existing flag, `true` **or** `false` |
| in the manifest, no longer owned or on disk | removed from the manifest |
| not owned (on disk, in the source, or hand-written into the manifest) | never listed, never copied |
| manifest will not parse | **run fails**, file left untouched |
| no manifest at all | created from the catalogue, every entry `installed: true` |

**3. Update only the owned skills marked `installed: true`.** The repo is shallow-cloned once and those
folders are copied over the existing ones. A skill marked `false` is never touched, so a local edit
inside one survives every run until you set its flag back. A skill outside the owned set is never
copied even when a hand-edited manifest asks for it.

Each copied folder is then pruned: **`tests/` and `__pycache__/` are removed** (the second at any
depth), and nothing else. An installed copy carries what runs, not what tests it — a harness asserts
against this repository's own tree and is meaningless outside it, and `__pycache__/` is interpreter
output that regenerates on first use. `__pycache__/` is gitignored, so it can never arrive through the
clone; it appears when someone runs the installed scripts directly.

`agents/`, `references/` and `assets/` are **kept**, because they are runtime: odin reads
`references/read-path.md` mid-skill and ai-understanding renders `assets/UNDERSTANDING.template.md`, so
stripping them would ship a skill that documents a file it cannot open. This is a runtime rule, not a
security one — nothing in a skill reads its own tests.

Note the divergence from the npm package: `package.json`'s `files` allowlist packs selected `scripts/`
directories only, and ships no `SKILL.md` at all, while an install ships the whole skill minus the two
pruned names.

**4. Refresh `_mimisbrunnr-shared`.** That folder is not a skill and never appears in the manifest, but
every installed skill that calls a shared script is broken without it — so it refreshes on every run,
whatever the flags say. Its contents are reconciled rather than merged: an entry the source no longer has
is removed, except `installation.json` itself, which is this script's own state. The same prune then
runs, *after* the sweep rather than before it — the sweep compares the destination against the source
and needs to see the same files on both sides.

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
    if s['name'] == 'mimisbrunnr-odin-context-memory':
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

48 cases across both ports, no network and no container engine needed: the clone source is the checkout
itself (`SKILLS_REPO` pointed at the repo, `SKILLS_BRANCH` at the current branch), so a harness that
reached GitHub would be red for reasons unrelated to the code. Runs in the PR gate. Verified green on
macOS (bash 3.2 with `pwsh` present — 48/48) and on GNU/Linux in `ubuntu:24.04` (`pwsh` absent).

The `ps1` cases and the cross-port parity cases are **skipped with a stated reason** when `pwsh` is not
installed — which is the case on the `ubuntu-latest` gate runner. They are exercised locally where `pwsh`
is present. See `scripts/AGENTS.md` → Test References for what that gap does and does not cover.

Six of the cases cover the prune. Four run end-to-end (tests/ absent, runtime files present, the shared
folder still refreshed, the clone source untouched, plus a skill marked `installed: false` left
byte-identical), and two drive `prune_non_shippable` / `Remove-NonShippable` **directly** on a planted
tree. The direct pair is not redundancy: `__pycache__/` is gitignored, so a clone can never carry one and
an end-to-end assertion about it would be vacuously true forever.