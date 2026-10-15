#requires -Version 7.0
<#
.SYNOPSIS
    Cross-platform skills installer for Windows / macOS / Linux (PowerShell 7).

.DESCRIPTION
    One-liner:
      irm https://raw.githubusercontent.com/generic-automation-and-it/smooth-ai-product-context-memory/main/scripts/install-skills.ps1 | iex

    Same contract as install-skills.sh:

      1. Finds the skills root in order: .agents/skills -> .claude/skills -> .codex/skills, and errors
         when none exists. It never creates one: a folder the operator did not ask for is not an install.
      2. Reads <root>/_mimisbrunnr-shared/installation.json.
         - Missing  -> creates it with every skill on disk, installed:true, UTC timestamp.
         - Present  -> syncs: adds new skills, drops skills no longer on disk, keeps every
                       installed flag the operator already set. A corrupt file is a hard error,
                       never a silent reset of the whole manifest.
      3. Updates only the skills marked installed:true, cloning the repo once and copying those folders
         (plus the shared scripts folder) out of it.
      4. Writes the updated installation.json back with a fresh UTC timestamp - after the update, so
         refreshing the shared folder cannot clobber the manifest that update read.

    Dependencies: PowerShell 7, git. No Python and no bash.

    Environment overrides:
      SKILLS_REPO    - git URL to clone from (default: the upstream GitHub repo)
      SKILLS_BRANCH  - branch to clone (default: main)
      SKILLS_ROOT    - force a specific skills root (skips the search)

.EXAMPLE
    ./install-skills.ps1
    ./install-skills.ps1 -SkillsRepo 'C:\repos\smooth-ai-product-context-memory' -SkillsBranch main
#>
[CmdletBinding()]
param(
    [string] $SkillsRepo = $(if ($null -ne $env:SKILLS_REPO -and $env:SKILLS_REPO -ne '') { $env:SKILLS_REPO } else { 'https://github.com/generic-automation-and-it/smooth-ai-product-context-memory.git' }),
    # $null -ne rather than a truthiness test: an env var that is set but EMPTY is the "use the remote's
    # default branch" case, and `if ($env:SKILLS_BRANCH)` cannot tell it from an unset var — it would fall
    # back to 'main' and name a branch that may not exist on a fork or in a detached checkout.
    [string] $SkillsBranch = $(if ($null -ne $env:SKILLS_BRANCH) { $env:SKILLS_BRANCH } else { 'main' }),
    [string] $SkillsRoot = $env:SKILLS_ROOT
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (Get-Variable -Name PSNativeCommandArgumentPassing -ErrorAction SilentlyContinue) {
    $PSNativeCommandArgumentPassing = 'Standard'
}

function Write-Info { param([string] $Message) Write-Host "install-skills: $Message" }
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

function Get-DiskSkills {
    param([string] $Root)
    @(Get-ChildItem -LiteralPath $Root -Directory |
        Where-Object { $_.Name -ne '_mimisbrunnr-shared' } |
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
    try {
        return (Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json)
    }
    catch {
        Fail "$Path is not valid JSON ($($_.Exception.Message)); fix or delete it - not treating it as a first install"
    }
}

function Get-CatalogSkills {
    # Every folder carrying a SKILL.md, minus the shared folder.
    param([string] $Root)
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { return @() }
    @(Get-ChildItem -LiteralPath $Root -Directory |
        Where-Object { $_.Name -ne '_mimisbrunnr-shared' } |
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

    # The catalogue is the union: what the source offers, plus anything already on disk. Deriving the
    # manifest from disk alone makes a first install into an empty skills folder a no-op - there is
    # nothing on disk, so there is nothing to install, and the operator's very first run leaves them with
    # an empty folder and a manifest listing nothing. A skill the operator added by hand is in neither
    # the source nor the manifest, and dropping it would delete state the operator owns.
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
        $hasSource = Test-Path -LiteralPath $sourcePath
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
        $source = Join-Path $repoSkills $name
        if (Test-Path -LiteralPath $source -PathType Container) {
            $dest = Join-Path $SkillsRoot $name
            if (Test-Path -LiteralPath $dest) { Remove-Item -LiteralPath $dest -Recurse -Force }
            Copy-Item -LiteralPath $source -Destination $dest -Recurse -Force
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
        Copy-Item -Path (Join-Path $sharedSource '*') -Destination $sharedDest -Recurse -Force
        Remove-StaleEntries -Dest $sharedDest -Source $sharedSource
        Write-Info 'Updated _mimisbrunnr-shared'
    }
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

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