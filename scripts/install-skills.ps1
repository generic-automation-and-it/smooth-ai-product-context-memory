#requires -Version 7.2
<#
.SYNOPSIS
    Cross-platform skills installer for Windows / macOS / Linux (PowerShell 7).

.DESCRIPTION
    One-liner:
      irm https://raw.githubusercontent.com/generic-automation-and-it/smooth-ai-product-context-memory/main/scripts/install-skills.ps1 | iex

    Same contract as install-skills.sh:

      1. Finds the skills root in order: .agents/skills -> .claude/skills -> .codex/skills, and errors
         when none exists. It never creates one: a folder the operator did not ask for is not an install.
      2. Installs only the skills owned here: `mimisbrunnr-*`, `ai-understanding`, `ai-brain-dump`
         (plus `_mimisbrunnr-shared/`, which is infrastructure, not a skill). Anything else on disk -
         local coding skills, operator experiments - is left untouched and never enters the manifest.
      3. Reads <root>/_mimisbrunnr-shared/installation.json.
         - Missing  -> creates it from the owned catalogue, installed:true, UTC timestamp.
         - Present  -> syncs: adds new owned skills, drops skills no longer owned or on disk, keeps every
                       installed flag the operator already set. A corrupt file is a hard error,
                       never a silent reset of the whole manifest.
      4. Updates only the owned skills marked installed:true, cloning the repo once and copying those
         folders (plus the shared scripts folder) out of it.
      5. Writes the updated installation.json back with a fresh UTC timestamp - after the update, so
         refreshing the shared folder cannot clobber the manifest that update read.

    The owned set lives in `Test-OwnedSkill` below AND in the bash port's `is_owned_skill`.
    The two must match; `test-install-skills.sh` holds them to each other (parity cases).

    Global mode (-g) installs the same owned set once, machine-wide, into a store of its own
    ($env:SMOOTH_MIMISBRUNNR_HOME, default ~/.smooth-ai-mimisbrunnr) and wires it into Claude Code, Codex
    and OpenCode; see "Global install" below. Switches: -s skills, -r rules, -h hooks (none = all three),
    -Uninstall (needs -g). -r and -h only exist in global mode: this repo owns no rules or hooks yet, so
    the owned lists are empty and a default run touches skills only.

    Dependencies: PowerShell 7, git. No Python and no bash.

    Environment overrides:
      SKILLS_REPO    - git URL to clone from (default: the upstream GitHub repo)
      SKILLS_BRANCH  - branch to clone (default: main)
      SKILLS_ROOT    - force a specific skills root (skips the search); refused with -g
      SMOOTH_MIMISBRUNNR_HOME - the -g store (default ~/.smooth-ai-mimisbrunnr)
      CLAUDE_CONFIG_DIR / CODEX_HOME / XDG_CONFIG_HOME - where -g finds each tool's config
                     (~/.claude, ~/.codex, ~/.config/opencode); the tools' own variables, not ours

.EXAMPLE
    ./install-skills.ps1
    ./install-skills.ps1 -SkillsRepo 'C:\repos\smooth-ai-product-context-memory' -SkillsBranch main
    ./install-skills.ps1 -g
    ./install-skills.ps1 -g -Uninstall
#>
[CmdletBinding()]
param(
    [string] $SkillsRepo = $(if ($null -ne $env:SKILLS_REPO -and $env:SKILLS_REPO -ne '') { $env:SKILLS_REPO } else { 'https://github.com/generic-automation-and-it/smooth-ai-product-context-memory.git' }),
    # $null -ne rather than a truthiness test: an env var that is set but EMPTY is the "use the remote's
    # default branch" case, and `if ($env:SKILLS_BRANCH)` cannot tell it from an unset var — it would fall
    # back to 'main' and name a branch that may not exist on a fork or in a detached checkout.
    [string] $SkillsBranch = $(if ($null -ne $env:SKILLS_BRANCH) { $env:SKILLS_BRANCH } else { 'main' }),
    [string] $SkillsRoot = $env:SKILLS_ROOT,
    [Alias('s')]
    [switch] $Skills,
    [Alias('r')]
    [switch] $Rules,
    [Alias('h')]
    [switch] $Hooks,
    [Alias('g')]
    [switch] $Global,
    [switch] $Uninstall
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (Get-Variable -Name PSNativeCommandArgumentPassing -ErrorAction SilentlyContinue) {
    $PSNativeCommandArgumentPassing = 'Standard'
}

function Write-Info { param([string] $Message) Write-Host "install-skills: $Message" }

# The canonical path of the clone source, and the boundary deciding which symlinks `Copy-Wet`
# materialises. Assigned unconditionally, so an inherited `$env:CLONE_ROOT` cannot widen "inside the
# clone" to a path the caller chose; `Copy-CloneSource` overwrites it with this run's own clone.
$script:CloneRoot = ''

# `LinkTarget` rather than `LinkType`. This needs .NET 6 (hence the 7.2 floor), but it is the
# property that actually answers the question: `LinkType` also reports 'HardLink', so testing it
# would recurse into a hard link as though it were a symlink and materialise it - turning an
# ordinary shared file into a duplicate copy per consumer.
function Test-Symlink {
    # The null guard is not defensive padding: this script runs under `Set-StrictMode -Version
    # Latest`, where reading a property off $null THROWS rather than yielding $null. The sweep asks
    # this question about a source path that may not exist, so without the guard the first missing
    # entry aborts the whole sweep and every stale file below it survives - which looks exactly like
    # a sweep that does nothing.
    param([System.IO.FileSystemInfo] $Item)
    if ($null -eq $Item) { return $false }
    return [bool]$Item.LinkTarget
}

# Canonical path of an existing path, following every link. Empty when it does not resolve, which is
# how a dangling link is told apart from a real one: the former is kept as a link, never placed.
function Get-RealPath {
    param([string] $Path)
    $current = $Path
    for ($hop = 0; $hop -lt 40; $hop++) {
        $item = Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue
        if (-not $item -or -not $item.LinkTarget) { break }
        $target = [string]$item.LinkTarget
        if ([System.IO.Path]::IsPathRooted($target)) { $current = $target }
        else { $current = Join-Path (Split-Path -Parent $current) $target }
    }
    $item = Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue
    if (-not $item -or $item.LinkTarget) { return '' }   # a link loop, or nothing there
    if (-not (Test-Path -LiteralPath $current)) { return '' }
    # Resolve-Path reports the provider path; on macOS `/var` becomes `/private/var`, and the clone
    # root is resolved the same way, so the two compare on the same spelling.
    try { return (Resolve-Path -LiteralPath $current -ErrorAction Stop).Path } catch { return '' }
}

function Test-InsideClone {
    param([string] $Path)
    if (-not $script:CloneRoot -or -not $Path) { return $false }
    # Component-wise, never a string prefix: /repo-other starts with /repo as text and is elsewhere.
    $root = $script:CloneRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    return $Path -eq $root -or $Path.StartsWith($root + [System.IO.Path]::DirectorySeparatorChar)
}

# Copy-Wet SRC DST [Stack] - copy SRC to DST, materialising a symlink that points inside the clone.
#
# The point is a self-contained installed skill: a symlink pointing inside the clone is an authoring
# convenience (one file shared by two skills), and copying the link would ship a pointer into a path
# the consumer does not have. A link outside the clone, or with no target, is the author's business
# and is kept as a link.
#
# Cycles are kept as links. A link to its own ancestor, or to something already being expanded, would
# otherwise recurse forever; the expansion stack stops at a repeat, which terminates the run while
# leaving the link visible instead of materialising a loop.
function Copy-Wet {
    param([string] $Src, [string] $Dst, [string[]] $Stack = @())

    $item = Get-Item -LiteralPath $Src -Force -ErrorAction SilentlyContinue
    if (-not $item) { return }

    if (Test-Symlink -Item $item) {
        $target = [string]$item.LinkTarget
        if ([System.IO.Path]::IsPathRooted($target)) { $resolvedTarget = $target }
        else { $resolvedTarget = Join-Path (Split-Path -Parent $Src) $target }
        $real = Get-RealPath -Path $resolvedTarget
        if (-not $real) { Copy-Link -Src $Src -Dst $Dst; return }          # dangling: keep it
        if ($Stack -contains $real) { Copy-Link -Src $Src -Dst $Dst; return }  # cycle: keep it
        if (-not (Test-InsideClone -Path $real)) { Copy-Link -Src $Src -Dst $Dst; return }
        Copy-Wet -Src $real -Dst $Dst -Stack ($Stack + @($real))
        return
    }

    if ($item.PSIsContainer) {
        $real = Get-RealPath -Path $Src
        New-Item -ItemType Directory -Path $Dst -Force | Out-Null
        foreach ($child in Get-ChildItem -LiteralPath $Src -Force) {
            Copy-Wet -Src $child.FullName -Dst (Join-Path $Dst $child.Name) -Stack ($Stack + @($real))
        }
        return
    }

    Copy-Item -LiteralPath $Src -Destination $Dst -Force
}

# Copy-Link - recreate a symlink rather than its contents. Without symlink privilege (a Windows
# account without Developer Mode) this warns and skips rather than silently writing a copy of the
# target, which would claim a link was preserved when it was not.
function Copy-Link {
    param([string] $Src, [string] $Dst)
    $target = [string](Get-Item -LiteralPath $Src -Force).LinkTarget
    New-Item -ItemType Directory -Path (Split-Path -Parent $Dst) -Force | Out-Null
    try {
        New-Item -ItemType SymbolicLink -Path $Dst -Target $target -ErrorAction Stop | Out-Null
    } catch {
        Write-Info "WARNING: cannot recreate symlink '$Dst' -> '$target' ($($_.Exception.Message)); skipped"
    }
}
# One line on stderr, matching the bash launcher. Write-Error renders a full error record - position,
# frame and all - which buries the one sentence the operator has to act on.
function Fail { param([string] $Message) [Console]::Error.WriteLine("install-skills: $Message"); exit 1 }

# ---------------------------------------------------------------------------
# 1. Find skills root
# ---------------------------------------------------------------------------

function Find-SkillsRoot {
    foreach ($dir in @('.agents/skills', '.claude/skills', '.codex/skills')) {
        if (Test-Path -LiteralPath $dir -PathType Container) {
            return (Resolve-Path -LiteralPath $dir).Path
        }
    }
    Fail 'no skills directory found - expected one of .agents/skills, .claude/skills, .codex/skills (create one, or set SKILLS_ROOT)'
}

# ---------------------------------------------------------------------------
# 2. List skills on disk (dirs with SKILL.md, excluding _mimisbrunnr-shared)
# ---------------------------------------------------------------------------

function Test-OwnedSkill {
    # The only skills this installer manages. Everything else that happens to sit in a skills root -
    # local coding skills, operator experiments - is somebody else's to update and is never touched.
    param([string] $Name)
    # Case-SENSITIVE match, mirroring the bash gate: on a case-sensitive filesystem `Mimisbrunnr-X`
    # is a different folder, and matching it here while bash refuses it is a cross-port divergence.
    if ($Name -clike 'mimisbrunnr-*') { return $true }
    if ($Name -ceq 'ai-understanding' -or $Name -ceq 'ai-brain-dump' -or $Name -ceq 'ai-memory-context') { return $true }
    return $false
}

function Get-DiskSkills {
    param([string] $Root)
    @(Get-ChildItem -LiteralPath $Root -Directory |
        Where-Object { $_.Name -ne '_mimisbrunnr-shared' } |
        Where-Object { Test-OwnedSkill -Name $_.Name } |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'SKILL.md') -PathType Leaf } |
        Select-Object -ExpandProperty Name |
        Sort-Object)
}

# ---------------------------------------------------------------------------
# 3. Manifest read / build
# ---------------------------------------------------------------------------

function Read-InstallationJson {
    # Missing reads as $null (a first install). A *corrupt* file is an error, not a first install:
    # treating it as absent would rebuild the manifest with every skill enabled, silently re-adding
    # skills the operator had switched off - and it would do that on a file nobody could parse, which
    # is exactly the file that needs a human.
    param([string] $Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    # An empty or whitespace-only file is not "absent": ConvertFrom-Json returns nothing for it without
    # complaint, which would read as a first install and re-enable everything the operator switched off.
    # The bash port refuses it too, because Python's json.load raises on it.
    if ([string]::IsNullOrWhiteSpace((Get-Content -LiteralPath $Path -Raw))) {
        Fail "$Path is empty; fix or delete it - not treating it as a first install"
    }
    try {
        return (Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json)
    }
    catch {
        Fail "$Path is not valid JSON ($($_.Exception.Message)); fix or delete it - not treating it as a first install"
    }
}

function Get-CatalogSkills {
    # Every owned folder carrying a SKILL.md, minus the shared folder.
    param([string] $Root)
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { return @() }
    @(Get-ChildItem -LiteralPath $Root -Directory |
        Where-Object { $_.Name -ne '_mimisbrunnr-shared' } |
        Where-Object { Test-OwnedSkill -Name $_.Name } |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'SKILL.md') -PathType Leaf } |
        Select-Object -ExpandProperty Name)
}

function Build-InstallationJson {
    # Returns the merged manifest as one JSON string.
    #
    # Built by hand rather than with ConvertTo-Json on a hashtable, because PowerShell unrolls a
    # single-element array to a bare object: `skills` would come back as an object rather than an
    # array, and the next run - or any other reader - would be parsing two different shapes.
    param(
        [string] $Updated,
        [string[]] $DiskSkills,
        [string[]] $SourceSkills,
        [object[]] $ExistingSkills
    )

    # The catalogue is the owned union: what the source offers, plus anything already on disk,
    # intersected with the owned set (both lists arrive pre-filtered by Test-OwnedSkill). Deriving the
    # manifest from disk alone makes a first install into an empty skills folder a no-op - there is
    # nothing on disk, so there is nothing to install, and the operator's very first run leaves them with
    # an empty folder and a manifest listing nothing. A non-owned skill is somebody else's to manage:
    # it stays on disk, it just never enters this manifest.
    $catalog = @($DiskSkills + $SourceSkills | Sort-Object -Unique)

    $existingMap = @{}
    if ($ExistingSkills) {
        foreach ($entry in $ExistingSkills) {
            $existingMap[$entry.name] = [bool] $entry.installed
        }
    }

    $items = @()
    foreach ($name in $catalog) {
        # A skill the manifest has never mentioned joins as true, same as the first-run rule. A skill it
        # already knows keeps the operator's flag either way.
        $installed = $true
        if ($existingMap.Count -gt 0 -and $existingMap.ContainsKey($name)) { $installed = $existingMap[$name] }
        $item = [ordered]@{ name = $name; installed = $installed }
        $items += '    ' + ($item | ConvertTo-Json -Compress -Depth 3)
    }

    $updatedJson = $Updated | ConvertTo-Json -Compress
    $joined = $items -join ",`n"
    if ($joined) { $joined += "`n" }
    return "{`n  `"updated`": $updatedJson,`n  `"skills`": [`n$joined  ]`n}"
}

# Remove what does not belong in an installed copy of a skill: the `tests/` harnesses, which assert
# against the repository's own tree and are meaningless outside it, and `__pycache__/`, which is
# interpreter output that regenerates on first run.
#
# `agents/`, `references/` and `assets/` are deliberately kept — they are runtime-referenced (odin
# reads `references/read-path.md` mid-skill; ai-understanding renders `assets/UNDERSTANDING.template.md`),
# so stripping them ships a skill documenting a file it cannot open. Mirrors `prune_non_shippable()`
# in install-skills.sh, including the order it runs in relative to the sweep.
function Remove-NonShippable {
    param([string] $Dest)
    if (-not (Test-Path -LiteralPath $Dest -PathType Container)) { return }
    $tests = Join-Path $Dest 'tests'
    if (Test-Path -LiteralPath $tests) { Remove-Item -LiteralPath $tests -Recurse -Force }
    # Depth-first, and the result collected before deleting: Get-ChildItem evaluates lazily, so
    # removing while enumerating drops the entries it had not reached yet.
    $stale = @(Get-ChildItem -LiteralPath $Dest -Directory -Recurse -Filter '__pycache__' -Force -ErrorAction SilentlyContinue)
    foreach ($dir in $stale) { Remove-Item -LiteralPath $dir.FullName -Recurse -Force -ErrorAction SilentlyContinue }
}

function Remove-StaleEntries {
    # Remove from Dest everything Source does not have, recursively.
    #
    # The recursion is the point: a top-level-only sweep keeps every entry the source still has *as a
    # folder* while leaving the files inside it that the source dropped, which is where a retired
    # script actually goes.
    #
    # installation.json is spared wherever it is found, because it is this script's own state and not
    # anything the clone source carries: sweeping it away is what turns an interrupted run into a silent
    # reset of every installed flag on the next one.
    param([string] $Dest, [string] $Source)

    foreach ($entry in Get-ChildItem -LiteralPath $Dest -Force) {
        if ($entry.Name -eq 'installation.json') { continue }
        $sourcePath = Join-Path $Source $entry.Name
        # `Test-Path` follows a symlink, so a DANGLING one reads as absent on both sides: the sweep
        # would then delete a link the source still has, and - had the source also dropped it - could
        # not tell the two apart. `LinkTarget` asks about the entry itself.
        $hasSource = (Test-Path -LiteralPath $sourcePath) -or (Test-Symlink -Item (Get-Item -LiteralPath $sourcePath -Force -ErrorAction SilentlyContinue))
        if ($hasSource -and $entry.PSIsContainer) {
            Remove-StaleEntries -Dest $entry.FullName -Source $sourcePath
        }
        elseif (-not $hasSource) {
            Remove-Item -LiteralPath $entry.FullName -Recurse -Force
        }
    }
}

# ---------------------------------------------------------------------------
# 4. Update installed skills from the repo
# ---------------------------------------------------------------------------

function Invoke-SourceClone {
    # Cloned before the manifest is built, not inside the update: the manifest's catalogue is the
    # source's skill list, so a first install into an empty folder has to see the source before it can
    # decide there is anything to install. The clone happens exactly once per run either way.
    param([string] $Repo, [string] $Branch)

    $script:TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('install-skills-' + [guid]::NewGuid().ToString('N'))
    $script:TempRepo = Join-Path $script:TempRoot 'repo'
    # Only ever this run's clone, so an inherited $env:CLONE_ROOT cannot widen the boundary.
    $script:CloneRoot = $script:TempRepo
    New-Item -ItemType Directory -Path $script:TempRoot -Force | Out-Null

    Write-Info "Cloning $Repo ($($Branch ? $Branch : 'default branch'))..."
    # git writes progress to stderr, and a 2>&1 redirect in PowerShell turns that into error records -
    # which $ErrorActionPreference = Stop would then throw on a successful clone.
    #
    # An empty $Branch means "whatever the remote's default is", and that case is load-bearing rather than
    # cosmetic: `git rev-parse --abbrev-ref HEAD` returns the literal string "HEAD" in a detached checkout
    # - exactly what actions/checkout produces - and `git clone --branch HEAD` is not valid, because HEAD is
    # not a branch or tag name.
    #
    # A bare local path is normalised to file:// so --depth applies. git ignores --depth in a local clone
    # ("use file:// instead"), which is a quiet downgrade: the entire clone is taken instead of one commit,
    # and the clone fails outright when the local repository has no branch of that name - the detached-HEAD
    # case again, reported by git as "Remote branch main not found", naming neither the cause nor the path.
    $cloneUrl = $Repo -replace '\\', '/'
    if ($cloneUrl -notmatch '^[a-zA-Z][a-zA-Z0-9+.-]*://' -and -not $cloneUrl.StartsWith('/')) {
        $cloneUrl = "file:///$cloneUrl"
    }
    if ($Branch) {
        $gitOutput = & git clone --depth 1 --branch $Branch $cloneUrl $script:TempRepo 2>&1
    }
    else {
        $gitOutput = & git clone --depth 1 $cloneUrl $script:TempRepo 2>&1
    }
    if ($LASTEXITCODE -ne 0) {
        Fail "failed to clone $Repo - $($gitOutput -join ' ')"
    }
}

function Update-InstalledSkills {
    param(
        [string] $SkillsRoot,
        [object] $Manifest
    )

    $installedNames = @()
    if ($Manifest -and $Manifest.skills) {
        $installedNames = @($Manifest.skills | Where-Object { $_.installed } | Select-Object -ExpandProperty name)
    }

    if ($installedNames.Count -eq 0) {
        Write-Info 'No skills marked installed:true - nothing to update.'
        return
    }

    $repoSkills = Join-Path $script:TempRepo '.agents/skills'
    foreach ($name in $installedNames) {
        # Defense in depth: the manifest only ever names owned skills, but a hand-edited manifest could name
        # anything. Never copy a folder this installer does not own.
        if (-not (Test-OwnedSkill -Name $name)) {
            Write-Info "WARNING: $name is not owned here - left on disk untouched"
            continue
        }
        $source = Join-Path $repoSkills $name
        if (Test-Path -LiteralPath $source -PathType Container) {
            $dest = Join-Path $SkillsRoot $name
            if (Test-Path -LiteralPath $dest) { Remove-Item -LiteralPath $dest -Recurse -Force }
            Copy-Wet -Src $source -Dst $dest
            Remove-NonShippable -Dest $dest
            Write-Info "Updated $name"
        } else {
            Write-Info "WARNING: $name not found in repo - left on disk untouched"
        }
    }

    # The shared folder is not a skill and is never in the manifest, but every installed skill that
    # references it is broken without it - so it refreshes on every run, independently of the flags.
    #
    # Refreshed *by content*, with stale entries removed, rather than merged in (which would leave a
    # file deleted upstream alive forever - an updater that never forgets anything is not an updater)
    # and rather than replacing the folder outright (which would take the manifest sitting inside it
    # down with it, and a run interrupted between the two would leave the next run rebuilding every
    # flag from defaults). So: copy across, then delete exactly what the source does not have, except
    # the manifest.
    $sharedSource = Join-Path $repoSkills '_mimisbrunnr-shared'
    if (Test-Path -LiteralPath $sharedSource -PathType Container) {
        $sharedDest = Join-Path $SkillsRoot '_mimisbrunnr-shared'
        New-Item -ItemType Directory -Path $sharedDest -Force | Out-Null
        Copy-Wet -Src $sharedSource -Dst $sharedDest
        Remove-StaleEntries -Dest $sharedDest -Source $sharedSource
        # Swept first, pruned second: the sweep compares the destination against the source and needs
        # to see the same files on both sides. See the bash port for why the order is not free.
        Remove-NonShippable -Dest $sharedDest
        Write-Info 'Updated _mimisbrunnr-shared'
    }
}

# ---------------------------------------------------------------------------
# 5. Global install (-g)
#
# Mirrors "Global install" in install-skills.sh; read the contract there. In short: the owned set is
# stored once in $env:SMOOTH_MIMISBRUNNR_HOME (default ~/.smooth-ai-mimisbrunnr) and wired into the tools
# found on the machine - symlinks for skills (and Claude rules), an index block in ~/.codex/AGENTS.md,
# a JSON merge into ~/.codex/hooks.json, per-file `instructions` entries in opencode.json. A tool whose
# config folder is absent is skipped, never created. The store is NOT ~/.smooth-ai-devex (that belongs to
# smooth-devex-template's installer, which would purge our links), the tool config files are shared with
# it, and so --uninstall removes our entries and never restores a snapshot over the file.
# ---------------------------------------------------------------------------

# Rules (paths relative to .github/instructions/) and hooks (names under .agents/hooks/) this installer
# may carry. Empty on purpose: this repo owns neither. The harness extends them with synthetic names via
# the test-only variables below; production never sets them. Mirrors OWNED_RULES / OWNED_HOOKS.
$script:OwnedRules = @()
$script:OwnedHooks = @()

function Get-OwnedRules {
    @($script:OwnedRules) + @(("$env:SKILLS_TEST_OWNED_RULES_EXTRA" -split '\s+') | Where-Object { $_ })
}

function Get-OwnedHooks {
    @($script:OwnedHooks) + @(("$env:SKILLS_TEST_OWNED_HOOKS_EXTRA" -split '\s+') | Where-Object { $_ })
}

$script:Links = New-Object System.Collections.Generic.List[string]
$script:GlbRules = $null
$script:GlbHooks = $null
$script:GlbHarnesses = $null
$script:RulesOn = @()
$script:RulesOff = @()
$script:HooksOn = @()
$script:HooksOff = @()
$script:HarnessClaude = $true
$script:HarnessCodex = $true
$script:HarnessOpencode = $true
$script:GlobalMarker = 'mimisbrunnr-owned-global-instructions'
$script:CodexBlockEnd = 'end of mimisbrunnr install-skills global rules'
$script:Utf8NoBom = [System.Text.UTF8Encoding]::new($false)

function Resolve-GlobalPaths {
    # Resolved in a function, not at script scope: a per-repo run never reads the store paths.
    $script:GlbRoot = if ($env:SMOOTH_MIMISBRUNNR_HOME) { $env:SMOOTH_MIMISBRUNNR_HOME } else { Join-Path $HOME '.smooth-ai-mimisbrunnr' }
    if (-not [System.IO.Path]::IsPathRooted($script:GlbRoot)) {
        Fail "SMOOTH_MIMISBRUNNR_HOME must be an absolute path (got '$($script:GlbRoot)') - links into a relative store would dangle"
    }
    $script:GlobalManifest = Join-Path $script:GlbRoot 'installation.json'
    $script:ClaudeDir = if ($env:CLAUDE_CONFIG_DIR) { $env:CLAUDE_CONFIG_DIR } else { Join-Path $HOME '.claude' }
    $script:CodexDir = if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $HOME '.codex' }
    $xdg = if ($env:XDG_CONFIG_HOME) { $env:XDG_CONFIG_HOME } else { Join-Path $HOME '.config' }
    $script:OpenCodeDir = Join-Path $xdg 'opencode'
}

function Add-GlobalLink {
    # Record a link/edit for the manifest. Tabs separate fields; a tab cannot appear in a path on a
    # supported OS, so the split is unambiguous.
    param([string] $Type, [string] $Path, [string] $Detail = '')
    if ($Detail) { $script:Links.Add("$Type`t$Path`t$Detail") } else { $script:Links.Add("$Type`t$Path") }
}

function Test-InstallSnapshot {
    # True when an install-time snapshot (<FILE>.smooth-mimisbrunnr-<digits>) of Path already exists.
    # Matched by regex: -Filter understands only * and ?, so a [0-9] class there is taken literally,
    # never finds the earlier snapshot, and a re-run would snapshot our own already-modified file.
    param([string] $Path)
    $dir = Split-Path -Parent $Path
    $pattern = '^' + [regex]::Escape([System.IO.Path]::GetFileName($Path) + '.smooth-mimisbrunnr-') + '[0-9]'
    $prior = Get-ChildItem -LiteralPath $dir -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -match $pattern }
    return [bool] $prior
}

function Backup-Inplace {
    # Snapshot an operator-owned file we are about to edit, as <FILE>.smooth-mimisbrunnr-<UTC stamp>,
    # once per file. Returns the path, or '' when there is no file or one is already taken.
    param([string] $Path)
    if (-not (Test-Path -LiteralPath $Path)) { return '' }
    if (Test-InstallSnapshot -Path $Path) { return '' }
    $backup = "$Path.smooth-mimisbrunnr-" + (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
    Copy-Item -LiteralPath $Path -Destination $backup -Force
    return $backup
}

function Test-ToolPresent {
    param([string] $Dir, [string] $Label)
    if (Test-Path -LiteralPath $Dir -PathType Container) { return $true }
    Write-Info "$Label config not found ($Dir) - skipping $Label wiring"
    return $false
}

function Build-GlobalItems {
    # The manifest's per-item list for one category (rules or hooks). The catalogue is the owned
    # allowlist, so a name outside it can never enter or survive a hand edit; an unseen name joins as
    # installed:true; the operator's flag is kept, false included. Sorted ordinally so the order matches
    # the bash port rather than the host culture's collation.
    param([string[]] $Catalog, [object] $Existing)
    $known = @{}
    foreach ($entry in @($Existing)) {
        if ($null -ne $entry -and $entry.PSObject.Properties['name'] -and $entry.name -is [string]) {
            $flag = $false
            if ($entry.PSObject.Properties['installed']) { $flag = [bool] $entry.installed }
            $known[$entry.name] = $flag
        }
    }
    $names = [string[]] @($Catalog | Select-Object -Unique)
    [Array]::Sort($names, [System.StringComparer]::Ordinal)
    $items = [System.Collections.Generic.List[object]]::new()
    foreach ($name in $names) {
        $installed = $true
        if ($known.ContainsKey($name)) { $installed = $known[$name] }
        $items.Add([ordered]@{ name = $name; installed = $installed })
    }
    return , $items
}

function Get-GlobalItemNames {
    param([object] $Items, [bool] $Installed)
    @($Items | Where-Object { [bool] $_.installed -eq $Installed } | ForEach-Object { $_.name })
}

function Build-Harnesses {
    # Exactly the three tools, in a fixed order. An unseen tool joins as true, the operator's value is
    # kept (false included), any other key is dropped.
    param([object] $Existing)
    $found = $null
    if ($null -ne $Existing -and $Existing.PSObject.Properties['harnesses']) { $found = $Existing.harnesses }
    $out = [ordered]@{}
    foreach ($name in 'claude-code', 'codex', 'opencode') {
        $value = $true
        if ($null -ne $found -and $found.PSObject.Properties[$name]) { $value = [bool] $found.PSObject.Properties[$name].Value }
        $out[$name] = $value
    }
    return $out
}

function Get-InstalledNames {
    param([object] $Manifest)
    if ($Manifest -and $Manifest.skills) {
        return @($Manifest.skills | Where-Object { $_.installed } | Select-Object -ExpandProperty name)
    }
    return @()
}

function Remove-StoreLinks {
    # Remove every symlink under Dir that points into the store's Subdir. Found by where a link points,
    # not by what the manifest recorded, so a link whose record was lost goes too. A real file, and a
    # link to anywhere else, is never touched.
    param([string] $Dir, [string] $Subdir)
    if (-not (Test-Path -LiteralPath $Dir -PathType Container)) { return }
    $prefix = (Join-Path $script:GlbRoot $Subdir) + [System.IO.Path]::DirectorySeparatorChar
    foreach ($item in @(Get-ChildItem -LiteralPath $Dir -Recurse -Force -ErrorAction SilentlyContinue)) {
        if ($item.LinkType -and $item.Target -and ([string] $item.Target).StartsWith($prefix, [System.StringComparison]::Ordinal)) {
            Remove-Item -LiteralPath $item.FullName -Force
            Write-Info "Unlinked $($item.FullName) (tool turned off)"
        }
    }
}

function Remove-StoreFile {
    # Remove DestLink only when it is the symlink we made (it points at Target).
    param([string] $DestLink, [string] $Target)
    $item = Get-Item -LiteralPath $DestLink -Force -ErrorAction SilentlyContinue
    if ($null -ne $item -and $item.LinkType -and ([string] $item.Target) -eq $Target) {
        Remove-Item -LiteralPath $DestLink -Force
        Write-Info "Unlinked disabled entry $DestLink"
    }
}

function Test-LinkTaken {
    param([string] $Path)
    if (Test-Path -LiteralPath $Path) { return $true }
    $dangling = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    return ($null -ne $dangling) -and ($null -ne $dangling.LinkType)
}

function Link-StoreEntry {
    # Symlink Target at DestLink. A correct existing link is kept (and re-recorded, so a lost manifest is
    # rebuilt by a re-run); a real file or directory, or a link to somewhere else, is reported and left
    # alone. On Windows without symlink rights a directory falls back to a junction, which is degraded: git
    # records a junction's contents as files. A file has no such fallback and is skipped with a warning.
    param([string] $DestLink, [string] $Target, [string] $LinkType)
    if (-not (Test-Path -LiteralPath $Target)) {
        Write-Info "WARNING: $Target missing - nothing to link at $DestLink"
        return
    }
    if (Test-LinkTaken -Path $DestLink) {
        $item = Get-Item -LiteralPath $DestLink -Force
        if ($item.LinkType) {
            if (([string] $item.Target) -eq $Target) { Add-GlobalLink $LinkType $DestLink $Target; return }
            Write-Info "WARNING: $DestLink is a link to somewhere else - left untouched"
            return
        }
        Write-Info "WARNING: $DestLink already exists - left untouched"
        return
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $DestLink) -Force | Out-Null
    try { New-Item -ItemType SymbolicLink -Path $DestLink -Target $Target | Out-Null }
    catch {
        $linked = $false
        if (Test-Path -LiteralPath $Target -PathType Container) {
            try { New-Item -ItemType Junction -Path $DestLink -Target $Target | Out-Null; $linked = $true } catch { $linked = $false }
        }
        if (-not $linked) {
            Write-Info "WARNING: could not create a link at $DestLink -> $Target - left untouched"
            return
        }
    }
    Add-GlobalLink $LinkType $DestLink $Target
    Write-Info "Linked $DestLink -> $Target"
}

function Sync-GlobalSkills {
    # Link each installed skill, and the shared folder every one of them resolves `../_mimisbrunnr-shared`
    # against, into ToolDir/skills. A tool that is off is purged instead. An installed:false skill is not
    # linked here, but an existing link stays.
    param([string] $ToolDir, [string] $Label, [string] $LinkType, [bool] $ToolOn, [string[]] $Names)
    $skillsDir = Join-Path $ToolDir 'skills'
    if (-not $ToolOn) {
        Remove-StoreLinks -Dir $skillsDir -Subdir 'skills'
        return
    }
    if (-not (Test-ToolPresent -Dir $ToolDir -Label $Label)) { return }
    $storeSkills = Join-Path $script:GlbRoot 'skills'
    foreach ($name in $Names) {
        Link-StoreEntry -DestLink (Join-Path $skillsDir $name) -Target (Join-Path $storeSkills $name) -LinkType $LinkType
    }
    $shared = Join-Path $storeSkills '_mimisbrunnr-shared'
    if (Test-Path -LiteralPath $shared -PathType Container) {
        Link-StoreEntry -DestLink (Join-Path $skillsDir '_mimisbrunnr-shared') -Target $shared -LinkType $LinkType
    }
}

function Save-GlobalRules {
    # Copy the installed:true rules into the store, keeping relative paths. Overwrite, never delete: an
    # installed:false rule is neither refreshed (store edits survive) nor wired.
    $sourceRules = Join-Path $script:TempRepo '.github/instructions'
    foreach ($rel in $script:RulesOn) {
        $source = Join-Path $sourceRules $rel
        if (Test-Path -LiteralPath $source -PathType Leaf) {
            $dest = Join-Path (Join-Path $script:GlbRoot 'rules') $rel
            New-Item -ItemType Directory -Path (Split-Path -Parent $dest) -Force | Out-Null
            Copy-Item -LiteralPath $source -Destination $dest -Force
            Write-Info "Stored global rule $rel"
        } else {
            Write-Info "WARNING: rule $rel not found in repo - not stored"
        }
    }
}

function Sync-ClaudeRules {
    $rulesDir = Join-Path $script:ClaudeDir 'rules'
    $storeRules = Join-Path $script:GlbRoot 'rules'
    if (-not $script:HarnessClaude) {
        Remove-StoreLinks -Dir $rulesDir -Subdir 'rules'
        return
    }
    if ($script:RulesOn.Count -gt 0) {
        if (-not (Test-ToolPresent -Dir $script:ClaudeDir -Label 'Claude Code')) { return }
    }
    foreach ($rel in $script:RulesOn) {
        Link-StoreEntry -DestLink (Join-Path $rulesDir $rel) -Target (Join-Path $storeRules $rel) -LinkType 'claude-rule'
    }
    # A rule flipped to installed:false is unwired too; its store copy stays.
    foreach ($rel in $script:RulesOff) {
        Remove-StoreFile -DestLink (Join-Path $rulesDir $rel) -Target (Join-Path $storeRules $rel)
    }
}

function Get-CodexBlockSpan {
    # Where our block sits in an array of lines: @{ Start; Stop }, or $null when there is none. A block
    # with no closing line is treated as the marker line alone, so it can still be removed.
    param([string[]] $Lines)
    $start = -1
    for ($i = 0; $i -lt $Lines.Count; $i++) {
        if ($Lines[$i].Contains($script:GlobalMarker)) { $start = $i; break }
    }
    if ($start -lt 0) { return $null }
    $stop = $start
    for ($j = $start + 1; $j -lt $Lines.Count; $j++) {
        if ($Lines[$j].Contains($script:GlobalMarker)) { break }
        if ($Lines[$j].Contains($script:CodexBlockEnd)) { $stop = $j; break }
    }
    return @{ Start = $start; Stop = $stop }
}

function Get-CodexBlockState {
    # none | block
    param([string] $File)
    if (-not (Test-Path -LiteralPath $File -PathType Leaf)) { return 'none' }
    $text = Get-Content -LiteralPath $File -Raw
    if ([string]::IsNullOrEmpty($text)) { return 'none' }
    if ($null -eq (Get-CodexBlockSpan -Lines ($text -split "`n"))) { return 'none' }
    return 'block'
}

function Set-CodexBlock {
    # Replace our block in File in place, or append one when there is none; nothing around it is touched.
    # Returns same | changed | appended.
    param([string] $File, [string] $Body)
    $begin = "<!-- $($script:GlobalMarker) (added by install-skills): regenerated on every run -->"
    $end = "<!-- $($script:CodexBlockEnd) -->"
    $block = @($begin) + @($Body.TrimEnd("`n") -split "`n") + @($end)
    $text = ''
    if (Test-Path -LiteralPath $File -PathType Leaf) {
        $raw = Get-Content -LiteralPath $File -Raw
        if ($null -ne $raw) { $text = $raw }
    }
    $lines = $text -split "`n"
    $span = Get-CodexBlockSpan -Lines $lines
    if ($null -eq $span) {
        $prefix = $text
        if ($prefix -and -not $prefix.EndsWith("`n")) { $prefix += "`n" }
        if ($prefix) { $prefix += "`n" }
        $result = $prefix + ($block -join "`n") + "`n"
        $outcome = 'appended'
    } else {
        $current = @($lines[$span.Start..$span.Stop] | ForEach-Object { $_.TrimEnd("`r") })
        if (($current -join "`n") -ceq ($block -join "`n")) { return 'same' }
        $before = @()
        if ($span.Start -gt 0) { $before = $lines[0..($span.Start - 1)] }
        $after = @()
        if (($span.Stop + 1) -lt $lines.Count) { $after = $lines[($span.Stop + 1)..($lines.Count - 1)] }
        $result = (@($before) + $block + @($after)) -join "`n"
        $outcome = 'changed'
    }
    [System.IO.File]::WriteAllText($File, $result, $script:Utf8NoBom)
    return $outcome
}

function Remove-CodexBlock {
    # Remove our block (and the blank line before it). A file that now holds nothing and was never
    # snapshotted was created by us, so removing it is the exact inverse; one that pre-dates the install
    # keeps its (empty) self.
    param([string] $File)
    if (-not (Test-Path -LiteralPath $File -PathType Leaf)) { return }
    $text = Get-Content -LiteralPath $File -Raw
    if ([string]::IsNullOrEmpty($text)) { return }
    $lines = $text -split "`n"
    $span = Get-CodexBlockSpan -Lines $lines
    if ($null -eq $span) { return }
    $kept = New-Object System.Collections.Generic.List[string]
    for ($i = 0; $i -lt $span.Start; $i++) { $kept.Add($lines[$i]) }
    if ($kept.Count -gt 0 -and $kept[$kept.Count - 1].TrimEnd("`r") -eq '') { $kept.RemoveAt($kept.Count - 1) }
    for ($i = $span.Stop + 1; $i -lt $lines.Count; $i++) { $kept.Add($lines[$i]) }
    $result = $kept -join "`n"
    [System.IO.File]::WriteAllText($File, $result, $script:Utf8NoBom)
    Write-Info "Removed the mimisbrunnr rules index from $File"
    if ($result.Trim() -eq '' -and -not (Test-InstallSnapshot -Path $File)) {
        Remove-Item -LiteralPath $File -Force
        Write-Info "Removed $File (the installer created it and nothing else is in it)"
    }
}

function Format-RulesIndex {
    # The text Codex is given in place of the rule files: each enabled rule's path, scope and one-line
    # description, grouped by scope. Mirrors render_rules_index.
    $root = Join-Path $script:GlbRoot 'rules'
    $groups = [ordered]@{}
    foreach ($rel in $script:RulesOn) {
        $path = Join-Path $root $rel
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        $text = Get-Content -LiteralPath $path -Raw
        $front = ''
        $rest = $text
        $fm = [regex]::Match($text, '(?s)\A---\r?\n(.*?)\r?\n---\r?\n')
        if ($fm.Success) { $front = $fm.Groups[1].Value; $rest = $text.Substring($fm.Length) }
        $read = {
            param([string] $Key)
            $f = [regex]::Match($front, '(?m)^' + $Key + ':\s*(.+?)\s*$')
            if (-not $f.Success) { return '' }
            $v = $f.Groups[1].Value.Trim()
            if ($v.Length -ge 2 -and $v[0] -eq $v[$v.Length - 1] -and ($v[0] -eq [char]39 -or $v[0] -eq [char]34)) {
                $v = $v.Substring(1, $v.Length - 2)
            }
            return $v
        }
        $scope = & $read 'applyTo'
        if (-not $scope) { $scope = & $read 'globs' }
        if (-not $scope) { $scope = '**' }
        $summary = & $read 'description'
        if (-not $summary) {
            $h = [regex]::Match($rest, '(?m)^#\s+(.+)$')
            $summary = if ($h.Success) { $h.Groups[1].Value.Trim() } else { [System.IO.Path]::GetFileName($path) }
        }
        if ($summary.Length -gt 100) { $summary = $summary.Substring(0, 99).TrimEnd() + [char]0x2026 }
        if (-not $groups.Contains($scope)) { $groups[$scope] = New-Object System.Collections.Generic.List[string] }
        $groups[$scope].Add('- ' + ($path -replace '\\', '/') + ' - ' + $summary)
    }
    if ($groups.Count -eq 0) { return 'No global rules are installed.' }
    $out = New-Object System.Collections.Generic.List[string]
    $out.Add('Codex does not import files from AGENTS.md, so the rules below are NOT loaded for you.')
    $out.Add('Open the ones that apply with your file tools before you work, and follow them.')
    if ($groups.Contains('**')) {
        $out.Add('')
        $out.Add('Read at the start of every task:')
        foreach ($entry in $groups['**']) { $out.Add($entry) }
        $groups.Remove('**')
    }
    foreach ($scope in @($groups.Keys)) {
        $out.Add('')
        $out.Add('When editing files matching `' + $scope + '`:')
        foreach ($entry in $groups[$scope]) { $out.Add($entry) }
    }
    return ($out -join "`n")
}

function Sync-CodexAgents {
    # Keep one delimited block in ~/.codex/AGENTS.md holding the rule index, replaced in place each run.
    # Nothing is written when there is no enabled rule, and a block no longer wanted is removed.
    $file = Join-Path $script:CodexDir 'AGENTS.md'
    if (-not $script:HarnessCodex -or $script:RulesOn.Count -eq 0) {
        Remove-CodexBlock -File $file
        return
    }
    if (-not (Test-ToolPresent -Dir $script:CodexDir -Label 'Codex')) { return }
    $index = Format-RulesIndex
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        [System.IO.File]::WriteAllText($file, '', $script:Utf8NoBom)
        [void] (Set-CodexBlock -File $file -Body $index)
        Add-GlobalLink 'agents-import' $file
        Write-Info "Created $file with the global rules index"
        return
    }
    if ((Get-CodexBlockState -File $file) -eq 'none') {
        # Snapshot only here: a file that already holds our block either took its snapshot on the first
        # install or was created by us, and a snapshot of that would capture our own block.
        $backup = Backup-Inplace -Path $file
        if ($backup) { Write-Info "Backed up $file -> $backup" }
        [void] (Set-CodexBlock -File $file -Body $index)
        Add-GlobalLink 'agents-import' $file $backup
        Write-Info "Appended the global rules index to $file"
        return
    }
    $outcome = Set-CodexBlock -File $file -Body $index
    Add-GlobalLink 'agents-import' $file
    if ($outcome -eq 'changed') { Write-Info "Updated the global rules index in $file" }
    else { Write-Info "Global rules index already current in $file" }
}

function Read-OperatorJson {
    # Operator JSON that does not parse is a hard error naming the file, never overwritten.
    param([string] $Path)
    $raw = Get-Content -LiteralPath $Path -Raw
    if ([string]::IsNullOrWhiteSpace($raw)) { return @{} }
    # Parsed strictly first. ConvertFrom-Json tolerates `// comments` and would then rewrite the file
    # without them, silently deleting the operator's notes; the bash port refuses such a file, so this one must.
    try { [System.Text.Json.JsonDocument]::Parse($raw).Dispose() }
    catch { Fail "$Path is not valid JSON ($($_.Exception.Message)); fix it, then re-run" }
    # ConvertFrom-Json turns "2026-01-01T10:00:00+02:00" into a DateTime and the rewrite emits the local-time
    # equivalent, silently altering an operator's string. -DateKind String stops that (PowerShell 7.5+);
    # before it the only safe course is to refuse a file that holds such a string.
    $dateKind = @{}
    if ((Get-Command ConvertFrom-Json).Parameters.ContainsKey('DateKind')) { $dateKind['DateKind'] = 'String' }
    elseif ($raw -match '"\d{4}-\d{2}-\d{2}[T ]\d') {
        Fail "$Path holds a date-like string that PowerShell before 7.5 would rewrite; upgrade to 7.5+ or edit the file by hand"
    }
    try { $data = $raw | ConvertFrom-Json -AsHashtable @dateKind }
    catch { Fail "$Path is not valid JSON ($($_.Exception.Message)); fix it, then re-run" }
    if ($data -isnot [System.Collections.IDictionary]) { Fail "$Path has an unexpected shape (not an object); fix it, then re-run" }
    return $data
}

function Write-OperatorJson {
    param([string] $Path, [object] $Data)
    [System.IO.File]::WriteAllText($Path, (($Data | ConvertTo-Json -Depth 10) + "`n"), $script:Utf8NoBom)
}

function Edit-OpencodeInstructions {
    # Make opencode.json `instructions` hold exactly the Wanted rules among the entries under the store,
    # one absolute path per rule, forward slashes. Per-file entries, never a glob: a glob cannot exclude
    # one rule. The operator's own entries are kept. Returns @{ Changed; Present; Backup }.
    param([string] $Config, [string[]] $Wanted, [bool] $Snapshot)
    $data = Read-OperatorJson -Path $Config
    if ($data.Contains('instructions') -and ($null -eq $data['instructions'] -or $data['instructions'] -is [string] -or $data['instructions'] -isnot [System.Collections.IEnumerable])) {
        Fail "$Config has an unexpected shape (instructions is not a list); fix it, then re-run"
    }
    $prefix = ((Join-Path $script:GlbRoot 'rules') -replace '\\', '/') + '/'
    $inst = [System.Collections.Generic.List[object]]::new()
    if ($data.Contains('instructions')) { foreach ($g in @($data['instructions'])) { $inst.Add($g) } }
    # Not `$wanted`: PowerShell variables are case-insensitive, so that would be the $Wanted parameter.
    $entries = [System.Collections.Generic.List[string]]::new()
    foreach ($rel in $Wanted) { $entries.Add($prefix + $rel) }
    $kept = [System.Collections.Generic.List[object]]::new()
    foreach ($g in $inst) {
        if ($g -is [string] -and $g.StartsWith($prefix, [System.StringComparison]::Ordinal) -and -not $entries.Contains($g)) { continue }
        $kept.Add($g)
    }
    $changed = ($kept.Count -ne $inst.Count)
    foreach ($w in $entries) {
        if (-not $kept.Contains($w)) { $kept.Add($w); $changed = $true }
    }
    $backup = ''
    if ($changed) {
        if ($Snapshot) { $backup = Backup-Inplace -Path $Config }
        if ($kept.Count -gt 0) { $data['instructions'] = @($kept) } else { [void] $data.Remove('instructions') }
        Write-OperatorJson -Path $Config -Data $data
    }
    $present = $false
    foreach ($g in $kept) { if ($g -is [string] -and $g.StartsWith($prefix, [System.StringComparison]::Ordinal)) { $present = $true } }
    return @{ Changed = $changed; Present = $present; Backup = $backup }
}

function Sync-OpencodeInstructions {
    $config = Join-Path $script:OpenCodeDir 'opencode.json'
    if (-not (Test-Path -LiteralPath $config -PathType Leaf)) {
        if ($script:RulesOn.Count -gt 0) { Write-Info "opencode.json not found ($config) - skipping global rules instructions" }
        return
    }
    $wanted = @($script:RulesOn)
    if (-not $script:HarnessOpencode) { $wanted = @() }
    # Nothing to add and nothing of ours in the file: do not even parse the operator's opencode.json.
    $prefix = ((Join-Path $script:GlbRoot 'rules') -replace '\\', '/') + '/'
    if ($wanted.Count -eq 0 -and -not (Get-Content -LiteralPath $config -Raw).Contains($prefix)) { return }
    $result = Edit-OpencodeInstructions -Config $config -Wanted $wanted -Snapshot $true
    if ($result.Backup) { Write-Info "Backed up $config -> $($result.Backup)" }
    if ($result.Changed) { Write-Info "Updated global rules instructions in $config" }
    if ($result.Present) { Add-GlobalLink 'opencode-config' $config $result.Backup }
}

function Save-GlobalHooks {
    # Copy the installed:true hooks into the store, keeping the exec bit.
    $sourceHooks = Join-Path $script:TempRepo '.agents/hooks'
    foreach ($name in $script:HooksOn) {
        $source = Join-Path $sourceHooks $name
        $dest = Join-Path (Join-Path $script:GlbRoot 'hooks') $name
        if (Test-Path -LiteralPath $source) {
            if (Test-Path -LiteralPath $dest) { Remove-Item -LiteralPath $dest -Recurse -Force }
            Copy-Item -LiteralPath $source -Destination $dest -Recurse -Force
            # `test -x` is portable across Unix; the exec bit means nothing on Windows.
            if ($name -like '*.sh' -and ($IsLinux -or $IsMacOS)) {
                & test -x $source; if ($LASTEXITCODE -eq 0) { & chmod +x $dest 2>$null }
            }
            Write-Info "Stored global hook $name"
        } else {
            Write-Info "WARNING: hook $name not found in repo - not stored"
        }
    }
}

function Edit-CodexHooks {
    # Make UserPromptSubmit in HooksFile carry the Enabled commands (absolute), dropping Disabled ones and
    # every command under PurgePrefix; deduped by exact command, the operator's own hooks kept. Returns
    # @{ Added; Removed; Present; Deleted; Backup }. A file left empty and never snapshotted was ours and
    # is deleted.
    param([string] $HooksFile, [string[]] $Enabled, [string[]] $Disabled, [string] $PurgePrefix, [bool] $Snapshot)
    $existed = Test-Path -LiteralPath $HooksFile -PathType Leaf
    $data = @{}
    if ($existed) { $data = Read-OperatorJson -Path $HooksFile }
    $hooks = $null
    if ($data.Contains('hooks') -and $data['hooks'] -is [System.Collections.IDictionary]) { $hooks = $data['hooks'] }
    if ($null -eq $hooks) { $hooks = [ordered]@{} }
    $ups = @()
    if ($hooks.Contains('UserPromptSubmit') -and $null -ne $hooks['UserPromptSubmit']) { $ups = @($hooks['UserPromptSubmit']) }
    $disabledSet = [System.Collections.Generic.HashSet[string]]::new([string[]] @($Disabled), [System.StringComparer]::Ordinal)
    $removed = 0
    $pruned = New-Object System.Collections.Generic.List[object]
    foreach ($group in $ups) {
        if ($group -is [System.Collections.IDictionary] -and $group.Contains('hooks') -and $null -ne $group['hooks']) {
            $all = @($group['hooks'])
            $keep = @($all | Where-Object {
                $drop = $_ -is [System.Collections.IDictionary] -and $_.Contains('command') -and (
                    $disabledSet.Contains([string] $_['command']) -or
                    ($PurgePrefix -and ([string] $_['command']).StartsWith($PurgePrefix, [System.StringComparison]::Ordinal)))
                -not $drop
            })
            $dropped = $all.Count - $keep.Count
            if ($dropped -gt 0) {
                $removed += $dropped
                if ($keep.Count -eq 0) { continue }
                $group['hooks'] = $keep
            }
        }
        $pruned.Add($group)
    }
    $existing = @{}
    foreach ($group in $pruned) {
        if ($group -is [System.Collections.IDictionary] -and $group.Contains('hooks')) {
            foreach ($h in @($group['hooks'])) {
                if ($h -is [System.Collections.IDictionary] -and $h.Contains('command')) { $existing[[string] $h['command']] = $true }
            }
        }
    }
    $new = @()
    foreach ($cmd in $Enabled) {
        if (-not $existing.ContainsKey([string] $cmd)) { $new += [ordered]@{ command = $cmd; type = 'command' } }
    }
    $changed = ($new.Count -gt 0) -or ($removed -gt 0)
    $backup = ''
    $deleted = $false
    if ($changed) {
        if ($existed -and $Snapshot) { $backup = Backup-Inplace -Path $HooksFile }
        # UserPromptSubmit ignores `matcher`, so the new group omits it.
        if ($new.Count -gt 0) { $pruned.Add([ordered]@{ hooks = $new }) }
        if ($pruned.Count -gt 0) { $hooks['UserPromptSubmit'] = $pruned } else { [void] $hooks.Remove('UserPromptSubmit') }
        if ($hooks.Count -gt 0) { $data['hooks'] = $hooks } else { [void] $data.Remove('hooks') }
        if ($data.Count -eq 0 -and -not ($existed -and (Test-InstallSnapshot -Path $HooksFile))) {
            if ($existed) { Remove-Item -LiteralPath $HooksFile -Force }
            $deleted = $true
        } else {
            Write-OperatorJson -Path $HooksFile -Data $data
        }
    }
    $ours = [System.Collections.Generic.HashSet[string]]::new([string[]] @($Enabled), [System.StringComparer]::Ordinal)
    $present = $false
    foreach ($group in $pruned) {
        if ($group -is [System.Collections.IDictionary] -and $group.Contains('hooks')) {
            foreach ($h in @($group['hooks'])) {
                if ($h -is [System.Collections.IDictionary] -and $h.Contains('command') -and $ours.Contains([string] $h['command'])) { $present = $true }
            }
        }
    }
    return @{ Added = $new.Count; Removed = $removed; Present = $present; Deleted = $deleted; Backup = $backup }
}

function Sync-CodexHooks {
    # Register the installed:true hooks in ~/.codex/hooks.json with absolute store commands.
    # UserPromptSubmit only: a hook registered on an event it was not written for fires wrongly.
    $hooksFile = Join-Path $script:CodexDir 'hooks.json'
    $storeHooks = Join-Path $script:GlbRoot 'hooks'
    $enabled = @()
    foreach ($name in $script:HooksOn) {
        $cmd = Join-Path $storeHooks $name
        if (Test-Path -LiteralPath $cmd -PathType Leaf) { $enabled += $cmd }
        else { Write-Info "WARNING: owned hook $name not in store - not registered" }
    }
    $disabled = @($script:HooksOff | ForEach-Object { Join-Path $storeHooks $_ })
    # Codex off: register nothing and remove every command under the store's hooks dir, so a hook that
    # is no longer owned goes too.
    $purgePrefix = ''
    if (-not $script:HarnessCodex) {
        $enabled = @()
        $purgePrefix = $storeHooks + [System.IO.Path]::DirectorySeparatorChar
    }
    if ($enabled.Count -eq 0 -and $disabled.Count -eq 0 -and -not $purgePrefix) {
        Write-Info 'No owned hooks to register globally'
        return
    }
    if (-not (Test-Path -LiteralPath $script:CodexDir -PathType Container)) {
        if ($enabled.Count -gt 0) { Write-Info "Codex config not found ($($script:CodexDir)) - skipping global Codex hooks" }
        return
    }
    # Nothing to add and no file of ours to clean: do not even parse the operator's hooks.json.
    if ($enabled.Count -eq 0) {
        $holdsOurs = (Test-Path -LiteralPath $hooksFile -PathType Leaf) -and (Get-Content -LiteralPath $hooksFile -Raw).Contains($storeHooks)
        if (-not $holdsOurs) { return }
    }
    $result = Edit-CodexHooks -HooksFile $hooksFile -Enabled $enabled -Disabled $disabled -PurgePrefix $purgePrefix -Snapshot $true
    if ($result.Backup) { Write-Info "Backed up $hooksFile -> $($result.Backup)" }
    if ($result.Added -gt 0) { Write-Info "Registered $($result.Added) owned hook(s) in $hooksFile" }
    if ($result.Removed -gt 0) { Write-Info "Unregistered $($result.Removed) hook(s) from $hooksFile" }
    if ($result.Present) { Add-GlobalLink 'hooks-merge' $hooksFile $result.Backup }
    if ($script:HarnessCodex -and $result.Added -gt 0) { Write-Info 'Trust the newly registered Codex hooks with: /hooks' }
}

function Write-GlobalManifest {
    # Write the global manifest. SkillsJson is the reconciled skills document (or '' for a run that did not
    # cover skills, which keeps the existing one). A category this run did not cover keeps the list the
    # previous run recorded. Links are merged by (type, path) so a re-run that links nothing new does not
    # wipe the history uninstall reads, and an upsert without a `detail` keeps the earlier one.
    param([string] $SkillsJson)
    $newLinks = New-Object System.Collections.Generic.List[object]
    foreach ($line in $script:Links) {
        $parts = $line -split "`t"
        $entry = [ordered]@{ type = $parts[0]; path = $parts[1] }
        if ($parts.Count -gt 2 -and $parts[2]) { $entry['detail'] = $parts[2] }
        $newLinks.Add($entry)
    }
    $existing = $null
    if (Test-Path -LiteralPath $script:GlobalManifest -PathType Leaf) {
        $raw = Get-Content -LiteralPath $script:GlobalManifest -Raw
        if ($raw.Trim()) { $existing = $raw | ConvertFrom-Json -AsHashtable }
    }
    $data = $null
    if ($SkillsJson) { $data = $SkillsJson | ConvertFrom-Json -AsHashtable }
    if ($null -eq $data) { $data = if ($null -ne $existing) { $existing } else { @{} } }
    if (-not $data.ContainsKey('skills')) {
        $data['skills'] = if ($null -ne $existing -and $existing.ContainsKey('skills')) { $existing['skills'] } else { @() }
    }
    $perItem = [ordered]@{ rules = $script:GlbRules; hooks = $script:GlbHooks }
    foreach ($key in $perItem.Keys) {
        if ($null -ne $perItem[$key]) { $data[$key] = $perItem[$key] }
        elseif (-not $data.ContainsKey($key) -and $null -ne $existing -and $existing.ContainsKey($key)) { $data[$key] = $existing[$key] }
    }
    if ($null -ne $script:GlbHarnesses) { $data['harnesses'] = $script:GlbHarnesses }
    elseif (-not $data.ContainsKey('harnesses') -and $null -ne $existing -and $existing.ContainsKey('harnesses')) { $data['harnesses'] = $existing['harnesses'] }

    $merged = New-Object System.Collections.Generic.List[object]
    $seen = @{}
    if ($null -ne $existing -and $existing.ContainsKey('links')) {
        foreach ($link in @($existing['links'])) {
            $merged.Add($link)
            $seen["$($link['type'])|$($link['path'])"] = $true
        }
    }
    foreach ($link in $newLinks) {
        $key = "$($link['type'])|$($link['path'])"
        if ($seen.ContainsKey($key)) {
            for ($i = 0; $i -lt $merged.Count; $i++) {
                if ($merged[$i]['type'] -eq $link['type'] -and $merged[$i]['path'] -eq $link['path']) {
                    if (-not $link.Contains('detail') -and $merged[$i].Contains('detail')) { $link['detail'] = $merged[$i]['detail'] }
                    $merged[$i] = $link
                    break
                }
            }
        } else {
            $merged.Add($link)
            $seen[$key] = $true
        }
    }
    # `enabled` is a status recomputed every run, not a control: true exactly when the symlink is on disk
    # and points where the record says. Config-edit records carry no such field.
    $symlinkTypes = 'codex-skill', 'claude-skill', 'opencode-skill', 'claude-rule'
    for ($i = 0; $i -lt $merged.Count; $i++) {
        $rec = $merged[$i]
        if ($symlinkTypes -contains [string] $rec['type']) {
            $item = Get-Item -LiteralPath ([string] $rec['path']) -Force -ErrorAction SilentlyContinue
            $detail = if ($rec.Contains('detail')) { [string] $rec['detail'] } else { '' }
            $rec['enabled'] = ($null -ne $item) -and [bool] $item.LinkType -and (([string] $item.Target) -eq $detail)
        }
        $ordered = [ordered]@{}
        foreach ($k in 'type', 'path', 'detail', 'enabled') { if ($rec.Contains($k)) { $ordered[$k] = $rec[$k] } }
        foreach ($k in $rec.Keys) { if (-not $ordered.Contains($k)) { $ordered[$k] = $rec[$k] } }
        $merged[$i] = $ordered
    }
    $data['updated'] = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH\:mm\:ssZ')
    $data['links'] = $merged
    $out = [ordered]@{}
    foreach ($k in 'updated', 'harnesses', 'skills', 'rules', 'hooks', 'links') { if ($data.ContainsKey($k)) { $out[$k] = $data[$k] } }
    foreach ($k in $data.Keys) { if (-not $out.Contains($k)) { $out[$k] = $data[$k] } }
    [System.IO.File]::WriteAllText($script:GlobalManifest, (($out | ConvertTo-Json -Depth 10) + "`n"), $script:Utf8NoBom)
    Write-Info "Wrote global manifest $($script:GlobalManifest)"
}

function Invoke-GlobalInstall {
    # Build the store, wire the tools, write the manifest last.
    foreach ($sub in 'skills', 'rules', 'hooks') { New-Item -ItemType Directory -Path (Join-Path $script:GlbRoot $sub) -Force | Out-Null }
    Invoke-SourceClone -Repo $SkillsRepo -Branch $SkillsBranch

    # A corrupt global manifest is a hard error in every category run, never a silent reset from defaults.
    $existing = Read-InstallationJson -Path $script:GlobalManifest

    $script:GlbHarnesses = Build-Harnesses -Existing $existing
    $script:HarnessClaude = [bool] $script:GlbHarnesses['claude-code']
    $script:HarnessCodex = [bool] $script:GlbHarnesses['codex']
    $script:HarnessOpencode = [bool] $script:GlbHarnesses['opencode']
    if (-not $script:HarnessClaude) { Write-Info 'Tool claude-code is off - not installing, removing what is wired' }
    if (-not $script:HarnessCodex) { Write-Info 'Tool codex is off - not installing, removing what is wired' }
    if (-not $script:HarnessOpencode) { Write-Info 'Tool opencode is off - not installing, removing what is wired' }

    if ($script:DoRules) {
        $existingRules = if ($existing -and $existing.PSObject.Properties['rules']) { $existing.rules } else { $null }
        $script:GlbRules = Build-GlobalItems -Catalog @(Get-OwnedRules) -Existing $existingRules
        $script:RulesOn = @(Get-GlobalItemNames -Items $script:GlbRules -Installed $true)
        $script:RulesOff = @(Get-GlobalItemNames -Items $script:GlbRules -Installed $false)
    }
    if ($script:DoHooks) {
        $existingHooks = if ($existing -and $existing.PSObject.Properties['hooks']) { $existing.hooks } else { $null }
        $script:GlbHooks = Build-GlobalItems -Catalog @(Get-OwnedHooks) -Existing $existingHooks
        $script:HooksOn = @(Get-GlobalItemNames -Items $script:GlbHooks -Installed $true)
        $script:HooksOff = @(Get-GlobalItemNames -Items $script:GlbHooks -Installed $false)
    }

    $newJson = ''
    if ($script:DoSkills) {
        $storeSkills = Join-Path $script:GlbRoot 'skills'
        $diskSkills = @(Get-DiskSkills -Root $storeSkills)
        $sourceSkills = @(Get-CatalogSkills -Root (Join-Path $script:TempRepo '.agents/skills'))
        # The global manifest may hold only harnesses{} (an operator pre-seeded it), and strict mode
        # throws on a property that is absent.
        $existingSkills = @()
        if ($existing -and $existing.PSObject.Properties['skills'] -and $existing.skills) { $existingSkills = @($existing.skills) }
        Write-Info 'Syncing global installation.json...'
        $newJson = Build-InstallationJson -Updated ([DateTime]::UtcNow.ToString('yyyy-MM-ddTHH\:mm\:ssZ')) `
            -DiskSkills $diskSkills -SourceSkills $sourceSkills -ExistingSkills $existingSkills
        $manifest = $newJson | ConvertFrom-Json
        Write-Info 'Storing global skills...'
        Update-InstalledSkills -SkillsRoot $storeSkills -Manifest $manifest
        $names = @(Get-InstalledNames -Manifest $manifest)
        Sync-GlobalSkills -ToolDir $script:ClaudeDir -Label 'Claude Code' -LinkType 'claude-skill' -ToolOn $script:HarnessClaude -Names $names
        Sync-GlobalSkills -ToolDir $script:CodexDir -Label 'Codex' -LinkType 'codex-skill' -ToolOn $script:HarnessCodex -Names $names
        Sync-GlobalSkills -ToolDir $script:OpenCodeDir -Label 'OpenCode' -LinkType 'opencode-skill' -ToolOn $script:HarnessOpencode -Names $names
    }

    if ($script:DoRules) {
        Save-GlobalRules
        Sync-ClaudeRules
        Sync-CodexAgents
        Sync-OpencodeInstructions
    }

    if ($script:DoHooks) {
        Save-GlobalHooks
        Sync-CodexHooks
    }

    # Written last: the skills refresh above must not be able to clobber the manifest it read.
    Write-Info 'Writing global manifest...'
    Write-GlobalManifest -SkillsJson $newJson
}

function Uninstall-Global {
    # Remove exactly what the manifest recorded, and nothing the other tools or the operator put there:
    # Codex's .system/, other installers' hooks and blocks, the operator's own entries. A JSON file is
    # snapshotted (<file>.smooth-mimisbrunnr-uninstall-<stamp>) before it is edited, only when we are about
    # to change it. The store and the operator's flags stay; the link history is cleared.
    if (-not (Test-Path -LiteralPath $script:GlobalManifest -PathType Leaf)) {
        Write-Info "No global install manifest at $($script:GlobalManifest) - nothing to uninstall"
        return
    }
    $data = Read-InstallationJson -Path $script:GlobalManifest
    $stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
    $rulesPrefix = ((Join-Path $script:GlbRoot 'rules') -replace '\\', '/') + '/'
    $hooksPrefix = (Join-Path $script:GlbRoot 'hooks') + [System.IO.Path]::DirectorySeparatorChar
    $seen = @{}
    $records = @()
    if ($data.PSObject.Properties['links'] -and $data.links) { $records = @($data.links) }
    foreach ($link in $records) {
        if ($null -eq $link) { continue }
        $type = [string] $link.type
        $path = [string] $link.path
        $detail = if ($link.PSObject.Properties['detail']) { [string] $link.detail } else { '' }
        if ($seen.ContainsKey("$type|$path")) { continue }
        $seen["$type|$path"] = $true
        $snapshot = "$path.smooth-mimisbrunnr-uninstall-$stamp"
        switch ($type) {
            { $_ -in 'claude-skill', 'codex-skill', 'opencode-skill', 'claude-rule' } {
                $item = Get-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
                if ($null -ne $item -and $item.LinkType -and ([string] $item.Target) -eq $detail) {
                    Remove-Item -LiteralPath $path -Force
                    Write-Info "Removed link $path"
                }
            }
            'agents-import' {
                # Only our delimited block is removed, so there is nothing of the operator's to snapshot.
                Remove-CodexBlock -File $path
            }
            'opencode-config' {
                if ((Test-Path -LiteralPath $path -PathType Leaf) -and (Get-Content -LiteralPath $path -Raw).Contains($rulesPrefix)) {
                    Copy-Item -LiteralPath $path -Destination $snapshot -Force
                    [void] (Edit-OpencodeInstructions -Config $path -Wanted @() -Snapshot $false)
                    Write-Info "Removed the store rules from $path (previous content kept at $snapshot)"
                }
            }
            'hooks-merge' {
                if ((Test-Path -LiteralPath $path -PathType Leaf) -and (Get-Content -LiteralPath $path -Raw).Contains($hooksPrefix)) {
                    Copy-Item -LiteralPath $path -Destination $snapshot -Force
                    $result = Edit-CodexHooks -HooksFile $path -Enabled @() -Disabled @() -PurgePrefix $hooksPrefix -Snapshot $false
                    if ($result.Deleted) {
                        # A hooks.json we created and have now emptied is gone, so a snapshot of it is litter.
                        Remove-Item -LiteralPath $snapshot -Force
                        Write-Info "Removed $path (the installer created it and nothing else is in it)"
                    } else {
                        Write-Info "Removed the store hooks from $path (previous content kept at $snapshot)"
                    }
                }
            }
        }
    }
    # Clear the link history, keep the operator's flags. Re-read as a hashtable so key order survives.
    $manifest = Get-Content -LiteralPath $script:GlobalManifest -Raw | ConvertFrom-Json -AsHashtable
    $manifest['links'] = @()
    $manifest['updated'] = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH\:mm\:ssZ')
    [System.IO.File]::WriteAllText($script:GlobalManifest, (($manifest | ConvertTo-Json -Depth 10) + "`n"), $script:Utf8NoBom)
    Write-Info "Uninstalled the global install - store left at $($script:GlbRoot) (remove it to reclaim space)"
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

# No category switch means everything; a switch narrows the run to exactly what was named. -r, -h and
# -Uninstall have no per-repo meaning, so they are refused without -g rather than silently ignored.
$script:DoSkills = [bool] $Skills
$script:DoRules = [bool] $Rules
$script:DoHooks = [bool] $Hooks
if (-not $Global) {
    if ($Uninstall) { Fail '--uninstall only applies to the global install - use: install-skills -g -Uninstall' }
    if ($script:DoRules -or $script:DoHooks) { Fail '-r and -h only apply to the global install - add -g' }
}
if (-not ($script:DoSkills -or $script:DoRules -or $script:DoHooks)) {
    $script:DoSkills = $true
    $script:DoRules = $true
    $script:DoHooks = $true
}

if ($Global) {
    if ($SkillsRoot) { Fail '-g cannot be combined with SKILLS_ROOT - the global install owns its own store; drop one of them' }
    Resolve-GlobalPaths
    if ($Uninstall) {
        Uninstall-Global
    } else {
        Invoke-GlobalInstall
        if (Test-Path -LiteralPath $script:TempRoot) { Remove-Item -LiteralPath $script:TempRoot -Recurse -Force -ErrorAction SilentlyContinue }
    }
    Write-Info 'Done.'
    exit 0
}

if ($SkillsRoot) {
    if (-not (Test-Path -LiteralPath $SkillsRoot -PathType Container)) { Fail "SKILLS_ROOT '$SkillsRoot' does not exist" }
    $script:SkillsRootPath = (Resolve-Path -LiteralPath $SkillsRoot).Path
}
else {
    $script:SkillsRootPath = Find-SkillsRoot
}

$sharedDir = Join-Path $script:SkillsRootPath '_mimisbrunnr-shared'
$jsonPath = Join-Path $sharedDir 'installation.json'
New-Item -ItemType Directory -Path $sharedDir -Force | Out-Null

Write-Info "Skills root: $($script:SkillsRootPath)"

$existing = Read-InstallationJson -Path $jsonPath
$existingSkills = @()
if ($existing -and $existing.skills) { $existingSkills = @($existing.skills) }

# The clone happens before the manifest is built, because the manifest's catalogue is the source's skill
# list. Derived from disk alone, a first install into an empty folder would decide there was nothing to
# install - an empty folder and a manifest listing nothing, on the operator's very first run.
Invoke-SourceClone -Repo $SkillsRepo -Branch $SkillsBranch

$diskSkills = @(Get-DiskSkills -Root $script:SkillsRootPath)
$sourceSkills = @(Get-CatalogSkills -Root (Join-Path $script:TempRepo '.agents/skills'))
# Escaped colons are deliberate. 'T' is a .NET custom-format *modifier* (time designator), so an
# unescaped 'HH:mm:ss' follows the designator and ':' then renders as the culture's time separator -
# which is '.' under one locale and ':' under another, producing a timestamp the bash port and this
# port cannot agree on.
$newJson = Build-InstallationJson -Updated ([DateTime]::UtcNow.ToString('yyyy-MM-ddTHH\:mm\:ssZ')) `
    -DiskSkills $diskSkills -SourceSkills $sourceSkills -ExistingSkills $existingSkills

Write-Info 'Syncing installation.json...'
Write-Info 'Updating installed skills...'
# The update reads the *merged* manifest, not the pre-existing one. On a first install the old value is
# $null, so passing it would update nothing on the very run that was supposed to install the skills —
# the manifest would say installed:true while the folders on disk stayed at whatever was there.
Update-InstalledSkills -SkillsRoot $script:SkillsRootPath -Manifest ($newJson | ConvertFrom-Json)

# The clone temp dir is removed last: a failure part-way through the update must leave it for nothing,
# and removing it here rather than in a finally means a clone that never happened leaves no litter.
if (Test-Path -LiteralPath $script:TempRoot) { Remove-Item -LiteralPath $script:TempRoot -Recurse -Force -ErrorAction SilentlyContinue }

Write-Info 'Writing installation.json...'
[System.IO.File]::WriteAllText($jsonPath, ($newJson + "`n"), [System.Text.UTF8Encoding]::new($false))

Write-Info "Done. State: $jsonPath"