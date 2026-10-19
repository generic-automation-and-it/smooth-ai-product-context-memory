# Skills installers

One-command installers for the **agent skills** — the folders under `.agents/skills/` that Claude Code,
Codex and Copilot discover.

| Script | Platform | Interpreter |
|---|---|---|
| [`install-skills.sh`](install-skills.sh) | macOS, Linux | bash, `git`, `python3` |
| [`install-skills.ps1`](install-skills.ps1) | Windows, or anywhere `pwsh` 7 runs | PowerShell 7.2 or later, `git` |

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

**2. Reconcile the manifest — for the owned skills only.** The installer manages exactly ten skills:
the seven `mimisbrunnr-*` skills plus `ai-understanding`, `ai-brain-dump` and `ai-memory-context`. The catalogue is the
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

**3a. Symlinks are resolved on the way in.** A symlink pointing **inside** the clone is copied as the
file or folder it points at, so an installed skill is **self-contained** — it can be zipped and
imported into a runtime with no repository behind it. A link pointing **outside** the repo, one with
no target, and one that would form a cycle are copied as links, so the consumer's machine keeps the
author's intent and the run still terminates. Practical effect: a skill can share one file between two
names without shipping a dangling pointer.

Only symlinks matter for the bundled skills — none ships one today, so this changes nothing you can
observe until someone adopts symlink authoring.

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

## Global install (`-g`)

```bash
scripts/install-skills.sh -g              # install the owned skills once, machine-wide
scripts/install-skills.sh -g --uninstall  # remove exactly what was wired
./scripts/install-skills.ps1 -g           # PowerShell: -g, -Uninstall
```

The per-repo install above puts skills in the repo you stand in. `-g` installs the same owned set **once**
into a store of its own and wires that store into every AI tool found on the machine, so the skills are
available in every project. Per-repo mode is unchanged and is still what a bare run does.

| | |
|---|---|
| Store | `~/.smooth-ai-mimisbrunnr/{skills,rules,hooks}` and `installation.json` (`SMOOTH_MIMISBRUNNR_HOME` overrides; must be absolute) |
| Switches | `-s` skills, `-r` rules, `-h` hooks (none = all three), `--uninstall` (`-Uninstall` in PowerShell). `-r`, `-h` and `--uninstall` are refused without `-g`; `-g` is refused with `SKILLS_ROOT` |
| Dependencies | the same as the per-repo install |

### Where each tool keeps its config

| Tool | macOS / Linux | Windows | Override |
|---|---|---|---|
| Claude Code | `~/.claude` | `%USERPROFILE%\.claude` | `CLAUDE_CONFIG_DIR` |
| Codex | `~/.codex` | `%USERPROFILE%\.codex` | `CODEX_HOME` |
| OpenCode | `~/.config/opencode` | `%USERPROFILE%\.config\opencode` — **not verified**; OpenCode documents `~/.config/opencode` and a managed path under `%ProgramData%` only | `XDG_CONFIG_HOME` |

A tool whose config folder does not exist is **skipped, never created**. The overrides are the tools' own
variables; this installer invents none, because `OPENCODE_CONFIG` in particular means "path to a config
file" to OpenCode.

### What is wired where

| | Skills | Rules | Hooks |
|---|---|---|---|
| Claude Code | symlink each into `~/.claude/skills` | symlink into `~/.claude/rules`, subfolders kept | not wired (its hooks live in `settings.json`, which this installer does not edit) |
| Codex | symlink each into `~/.codex/skills`, beside the tool's own `.system/` (never touched) | a delimited **index** block in `~/.codex/AGENTS.md` | JSON-merged into `~/.codex/hooks.json` under `UserPromptSubmit` only, absolute commands |
| OpenCode | symlink each into `~/.config/opencode/skills` | one absolute path per rule in `opencode.json` `instructions` | not wired |

- **`_mimisbrunnr-shared` is linked too.** A skill resolves `../_mimisbrunnr-shared` against the folder it
  was found in, so each tool's `skills/` folder needs the shared folder beside the skills, not only the store.
- **Codex gets an index, not the rules.** Codex has no `@path` import and caps combined `AGENTS.md` content at
  32 KiB, so the block lists each rule's path, scope and one-line description and tells the model to open the
  ones that apply. That is documented behaviour, not observed against a live Codex session.
- **Codex `.rules` files are not instructions** (they are `prefix_rule()` command-approval policy), and there is
  no policy in this repo to derive one from, so none is written.
- **OpenCode entries are per file, never a `rules/**` glob**: a glob cannot exclude one rule.
- **This repo owns no rules and no hooks today.** `OWNED_RULES` and `OWNED_HOOKS` are empty, so a default `-g`
  touches skills and the manifest only — it does not create, parse or snapshot `AGENTS.md`, `hooks.json` or
  `opencode.json`. The machinery is built and held by the harness through synthetic names; owning a real rule
  or hook is adding its name to the list in both ports.

### The manifest and per-item control

`~/.smooth-ai-mimisbrunnr/installation.json` carries `updated`, `harnesses`, `skills`, `rules`, `hooks` and
`links`, in that order. `skills`, `rules` and `hooks` are `{name, installed}` lists reconciled every run (a new
name joins as `true`, the operator's flag is kept, a name no longer owned is dropped). A category-only run
(`-g -r`) reconciles just its own list and carries the others forward.

- `installed: false` on a **skill** stops it being refreshed; an existing link stays.
- `installed: false` on a **rule or hook** also **unwires** it from every place it was wired. The store copy stays.
- `harnesses` is `{"claude-code": true, "codex": true, "opencode": true}`. `false` installs nothing into that
  tool and removes what is there, found **on disk** by where a link points, so a lost record cannot leave a
  live link. A real file, a directory, or a link into any other store is never touched.
- Each symlink record carries `enabled`, a status recomputed every run (is the link on disk and pointing where
  recorded). It is not a control.

### Sharing a machine with smooth-devex-template's installer

That installer keeps `~/.smooth-ai-devex` and edits the **same three config files**. The two cannot share a
store: it reconciles its manifest against its own owned set and purges every tool symlink whose target is under
its store, so a shared store would let each installer delete the other's links. They can share the config files,
which is why this installer:

- uses its **own** block markers (`mimisbrunnr-owned-global-instructions`) and snapshot suffix
  (`.smooth-mimisbrunnr-<UTC stamp>`), so neither's block or snapshot is mistaken for the other's;
- edits a file only when the edit changes something, snapshotting a pre-existing file once before the first edit;
- on `--uninstall`, **removes its own entries and leaves the rest**. It does not restore a snapshot over the
  file — that would erase whatever the other installer wrote since. Snapshots taken at install stay as a
  safety copy, and a JSON file edited at uninstall is snapshotted again as `.smooth-mimisbrunnr-uninstall-<stamp>`;
- deletes a file only if it created it and nothing else is in it.

A file that does not parse (`opencode.json` with `//` comments, a broken `hooks.json`) is a hard error naming the
file and is never overwritten — but only when there is something to write. A default run never reads them.

### Windows

Directory links fall back to a **junction** when symlinks are not permitted; git records a junction's contents
as files, so treat that as degraded. A rule is a file, which has no junction fallback, so without symlink rights
it is skipped with a warning. The PowerShell port is exercised on macOS and Linux only; replay it on Windows
before trusting it there.

## Testing

```bash
bash scripts/test-install-skills.sh
```

101 cases across both ports (50 when `pwsh` is absent), no network and no container engine needed: the clone
source is the checkout itself (`SKILLS_REPO` pointed at the repo, `SKILLS_BRANCH` at the current branch) for the
per-repo cases and a fixture repository for the `-g` cases, so a harness that reached GitHub would be red for
reasons unrelated to the code. Every `-g` case runs in a throwaway `HOME` with each tool's config location
pinned and from that directory, so a red run cannot touch the operator's real config, either store, or this
checkout. Runs in the PR gate. Verified green on macOS (bash 3.2 with `pwsh` present — 101/101) and on
GNU/Linux in `ubuntu:24.04` (`pwsh` absent — 50/50).

The `ps1` cases and the cross-port parity cases are **skipped with a stated reason** when `pwsh` is not
installed — which is the case on the `ubuntu-latest` gate runner. They are exercised locally where `pwsh`
is present. See `scripts/AGENTS.md` → Test References for what that gap does and does not cover.

Eight of the cases cover the prune. Four run end-to-end (tests/ absent, runtime files present, the shared
folder still refreshed, the clone source untouched, plus a skill marked `installed: false` left
byte-identical), and two drive `prune_non_shippable` / `Remove-NonShippable` **directly** on a planted
tree. The direct pair is not redundancy: `__pycache__/` is gitignored, so a clone can never carry one and
an end-to-end assertion about it would be vacuously true forever. The other two assert that no
*installed* document links into a pruned path — the failure the prune creates rather than the one it
prevents, and invisible in the repository because `tests/` is right there.