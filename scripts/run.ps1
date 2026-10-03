#requires -Version 7.0
<#
.SYNOPSIS
    Standalone launcher for the published Mímisbrunnr release controller (PowerShell 7 / Windows).

.DESCRIPTION
    Self-contained on purpose: copy this one file out of the repository and run it. It reads nothing
    from the checkout and needs no .NET SDK - only PowerShell 7 and the docker CLI. The optional
    version-drift report is the only thing that looks for a git checkout, and it degrades to a plain
    image digest when there is none.

    Starts ONE container - ghcr.io/...-apphost - which carries the Aspire AppHost, DCP and dashboard.
    That controller then starts mimisbrunnr-<id>-{host,postgres,blob-well,seq} through the mounted
    socket. Do not start those four here: the controller owns them, and its ownership, health and
    lifecycle rules live in its own entrypoint. This script starts and replaces that one container.

    Running it again stops the running controller (which removes the four workloads), pulls the newest
    image, and starts a fresh controller. Data volumes and the data-root folders are preserved; only
    the containers are replaced. An upgrade is a migration - the new image applies its own migrations
    at startup, so snapshot a corpus you care about before running this over it.

    Same contract as run.sh, with the two differences Windows forces:

      * the engine socket is the named pipe \\.\pipe\docker_engine, not /var/run/docker.sock;
      * the data root defaults OFF. Docker Desktop on Windows reports a bind's host path as
        /run/desktop/mnt/host/c/Users/..., and the controller's entrypoint only normalises the
        /host_mnt prefix that macOS and Linux Desktop report. A data root here would therefore fail
        its backing check. Pass -DataRoot to opt in; that path is unverified on Windows.

.PARAMETER Verb
    up (default, and a restart when one is already running), env, status, stop, logs.

.PARAMETER Id
    Installation id. Names every resource: mimisbrunnr-<id>-*, group smooth-mímisbrunnr-release-<id>.

.PARAMETER DataRoot
    Host folder for the corpus. Layout: <root>/<id>/{postgres-data,blob-well-data,seq-data,
    host-context,controller-state} plus <root>/../controller.env for the secrets.

.EXAMPLE
    ./run.ps1
    ./run.ps1 -Verb status
    ./run.ps1 -Id scratch -DataRoot "$HOME\.mimisbrunnr"
#>
[CmdletBinding()]
param(
    # 'up' is also the restart: it replaces a running controller for this installation.
    [ValidateSet('up', 'env', 'status', 'stop', 'logs')]
    [string] $Verb = 'up',

    [ValidatePattern('^[a-z0-9][a-z0-9-]{0,31}$')]
    [string] $Id = 'default',

    [string] $Image = 'ghcr.io/generic-automation-and-it/smooth-ai-product-context-memory-apphost:latest',

    # $null means engine-managed named volumes.
    [string] $DataRoot,

    [string] $EngineSocket,

    [string] $BindAddress = '127.0.0.1',

    # The documented release ports (docker.md). These are also what provision-credentials.sh writes
    # into CONTEXT_MEMORY_BASE_URL and what the context-memory client defaults to, so overriding them
    # here would break every skill and client the moment a controller was started.
    [int] $HostPort = 5141,
    [int] $PostgresPort = 5432,
    [int] $BlobPort = 9000,
    [int] $BlobConsolePort = 9001,
    [int] $SeqPort = 5341,
    [int] $DashboardPort = 15278,
    [int] $OtlpPort = 19075
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Standard passes each array element to the native command as one argument. The legacy Windows-style
# passing re-joins and re-quotes them, which mangles a --env-file path or bind source containing a
# space. Available from 7.3; the default from 7.4, set explicitly so 7.0-7.2 behave the same way.
if (Get-Variable -Name PSNativeCommandArgumentPassing -ErrorAction SilentlyContinue) {
    $PSNativeCommandArgumentPassing = 'Standard'
}

$script:ControllerName = "mimisbrunnr-$Id-controller"
$script:StateVolume = "mimisbrunnr-$Id-controller-state"
$script:GroupName = "smooth-mímisbrunnr-release-$Id"
$script:UseDataRoot = $null -ne $DataRoot
# The credential file sits in the data *home*, one level above the volumes folder - the same place
# run.sh puts it. Deriving the home as the parent of an explicitly-given -DataRoot wrote controller.env
# to $HOME\controller.env instead, so an operator moving an installation between the two launchers did
# not find the secrets where the shared documentation says they are.
$script:DataHome = if ($script:UseDataRoot) {
    # -DataRoot may be the volumes folder itself, or a home containing one. Take the parent unless the
    # folder is not itself named "volumes", which is how run.sh's default reads.
    if ((Split-Path -Leaf $DataRoot) -eq 'volumes') { Split-Path -Parent $DataRoot } else { $DataRoot }
}
else { Join-Path $HOME '.mimisbrunnr' }
$script:VolumesRoot = if ($script:UseDataRoot) { $DataRoot } else { Join-Path $script:DataHome 'volumes' }
$script:EnvFile = Join-Path $script:DataHome 'controller.env'
$script:ContainerDataRoot = '/var/lib/mimisbrunnr-data'
$script:DashboardHost = "http://localhost:$DashboardPort"
$script:OnWindows = $IsWindows
$script:TempEnvFile = $null

# Removes a half-written credential file if the run is interrupted between write and move.
trap {
    if ($script:TempEnvFile -and (Test-Path -LiteralPath $script:TempEnvFile)) {
        Remove-Item -LiteralPath $script:TempEnvFile -Force -ErrorAction SilentlyContinue
    }
}

function Write-Info { param([string] $Message) Write-Host "run.ps1: $Message" }
# One line on stderr, matching run.sh. Write-Error would render a full error record - position, line
# frame and all - which buries the one sentence the operator has to act on.
function Fail { param([string] $Message) [Console]::Error.WriteLine("run.ps1: $Message"); exit 1 }

function Invoke-Docker {
    param([Parameter(ValueFromRemainingArguments = $true)] [string[]] $Arguments)
    & docker @Arguments
    if ($LASTEXITCODE -ne 0) { Fail "docker $($Arguments -join ' ') failed (exit $LASTEXITCODE)" }
}

function Test-PortFree {
    param([int] $Port)
    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        # 400 ms is deliberate: the check is "is somebody already listening", not a health probe.
        $task = $client.ConnectAsync('127.0.0.1', $Port)
        if ($task.Wait(400) -and $client.Connected) { return $false }
        return $true
    }
    catch { return $true }
    finally { $client.Dispose() }
}

function Require-PortFree {
    param([int] $Port, [string] $Label, [string] $Override)
    if (-not (Test-PortFree -Port $Port)) {
        Fail "$Label port $Port is already in use; override it (e.g. -$Override <other-port>)"
    }
}

function Resolve-Socket {
    if ($EngineSocket) { return $EngineSocket }
    if ($script:OnWindows) { return '\\.\pipe\docker_engine' }
    return '/var/run/docker.sock'
}

# ---------------------------------------------------------------------------------------------
# credentials: process environment wins, then the stored file, then generate and persist
# ---------------------------------------------------------------------------------------------

function New-Secret {
    param([int] $Bytes = 32)
    # Hex only. The value lands in a docker --env-file and, for the token parameters, in a shell
    # whose grammar rejects those names - keep it to characters neither can reinterpret.
    [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes($Bytes)).ToLowerInvariant()
}

function Get-StoredSecret {
    param([string] $Key)
    if (-not (Test-Path -LiteralPath $script:EnvFile)) { return $null }
    foreach ($line in [System.IO.File]::ReadAllLines($script:EnvFile)) {
        if ($line.StartsWith("$Key=")) { return $line.Substring($Key.Length + 1) }
    }
    return $null
}

function Get-ProvisionedTokenPair {
    # Returns both Parameters__api-*-token values as a hashtable, or $null when the provisioned file is
    # absent or carries only one of them. All-or-nothing on purpose: a file holding a single token
    # cannot yield a matched pair, so it is treated as no pair at all rather than adopted half of.
    $candidates = @()
    if ($env:MIMIS_TOKEN_FILE) { $candidates += $env:MIMIS_TOKEN_FILE }
    $candidates += (Join-Path $PSScriptRoot '..\.context\mimisbrunnr.env.controller')

    foreach ($candidate in $candidates) {
        if (-not $candidate -or -not (Test-Path -LiteralPath $candidate)) { continue }
        $pair = @{}
        foreach ($line in [System.IO.File]::ReadAllLines($candidate)) {
            if ($line -match '^Parameters__api-(read|write)-token=(.+)$') { $pair[$Matches[1]] = $Matches[2].Trim() }
        }
        if ($pair.Count -eq 2 -and $pair['read'] -and $pair['write'] -and $pair['read'] -ne $pair['write']) {
            return @{
                'Parameters__api-read-token'  = $pair['read']
                'Parameters__api-write-token' = $pair['write']
            }
        }
    }
    return $null
}

function Initialize-Credentials {
    # A symlink here would send this run's credentials wherever the link points, and a directory would
    # fail the write after the values were already chosen. Checked here rather than at the top of the
    # script, because the guard needs Fail, which is only defined once the function definitions above
    # have run.
    if (Test-Path -LiteralPath $script:EnvFile) {
        $item = Get-Item -LiteralPath $script:EnvFile -Force
        if ($item.LinkType) { Fail "$($script:EnvFile) is a $($item.LinkType); refusing to write secrets through it" }
        if ($item.PSIsContainer) { Fail "$($script:EnvFile) is a directory; refusing to write secrets into it" }
    }

    $keys = @(
        'PostgresConfiguration__Password',
        'BlobConfiguration__AccessKey',
        'BlobConfiguration__SecretKey',
        'Parameters__api-read-token',
        'Parameters__api-write-token'
    )

    $effective = @{}
    $persisted = [ordered]@{}
    $generated = 0
    $fromEnvironment = 0
    $missing = 0
    $adoptedCount = 0

    # scripts/provision-credentials.sh writes the *same* two token values into
    # .context/mimisbrunnr.env (as CONTEXT_MEMORY_*, which skills read) and
    # .context/mimisbrunnr.env.controller (as Parameters__*, which a container reads via --env-file).
    # Minting this launcher's own pair instead leaves the controller and the skills holding different
    # credentials, and every call is a 403. Read as a pair or not at all.
    $adopted = Get-ProvisionedTokenPair

    foreach ($key in $keys) {
        # [Environment]::GetEnvironmentVariable takes the name as a string, so the hyphenated
        # parameter names work where `export Parameters__api-read-token=...` would be a syntax error.
        $fromEnv = [Environment]::GetEnvironmentVariable($key)
        $stored = Get-StoredSecret -Key $key

        if ($fromEnv) {
            $effective[$key] = $fromEnv
            $fromEnvironment++
        }
        elseif ($stored) {
            $effective[$key] = $stored
        }
        elseif ($null -ne $adopted -and $adopted.ContainsKey($key)) {
            # Adopted from the provisioner's controller env file. Adopting the *pair* as a unit is the
            # point: resolving each key against its own source could adopt one token and mint the other,
            # producing exactly the split pair the adoption exists to prevent.
            $effective[$key] = $adopted[$key]
            $adoptedCount++
        }
        else {
            $effective[$key] = New-Secret
            $generated++
        }

        # Only ever fill gaps. An earlier draft persisted just the values this run selected, so
        # exporting one variable silently dropped the stored token for it - and the next run without
        # that variable minted a different one, 403ing a Host already running with the old value.
        if ($stored) { $persisted[$key] = $stored } else { $persisted[$key] = $effective[$key]; $missing++ }
    }

    $read = $effective['Parameters__api-read-token']
    $write = $effective['Parameters__api-write-token']
    if ($read -eq $write) { Fail 'read and write tokens are identical; the read/write capability split would be void' }

    if ($missing -gt 0) {
        New-Item -ItemType Directory -Force -Path $script:DataHome | Out-Null
        $lines = @(
            "# Release-controller secrets for installation '$Id'.",
            '# Values already present here are reused verbatim. Anything supplied through the',
            '# environment is deliberately absent.',
            "# Generated by run.ps1 on $(Get-Date -Format o)."
        ) + ($persisted.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" })

        # Write to a sibling then move, so an interrupted run cannot leave a half-written secret file.
        $temp = "$($script:EnvFile).tmp$PID"
        $script:TempEnvFile = $temp
        [System.IO.File]::WriteAllLines($temp, $lines, [System.Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temp -Destination $script:EnvFile -Force
        $script:TempEnvFile = $null
        Write-Info "wrote $($script:EnvFile) - $generated generated, $adoptedCount adopted from provision-credentials.sh, $missing new"
        if ($adoptedCount -gt 0) {
            Write-Info '  tokens match the CONTEXT_MEMORY_* values your skills hold, so they will not 403'
        }
    }

    # Re-assert the mode on every run, not only when writing. A file widened by a backup restore or a
    # careless copy would otherwise stay group-readable for the life of the installation, and reuse
    # is the quiet path no test would notice. A no-op on Windows, where the file inherits the profile
    # directory's ACL instead - restrict that folder if the machine is shared.
    if (-not $script:OnWindows) {
        & chmod 600 $script:EnvFile
        if ($LASTEXITCODE -ne 0) { Fail "cannot restrict permissions on $($script:EnvFile)" }
    }

    if ($fromEnvironment -gt 0) {
        Write-Info "NOTE: $fromEnvironment secret(s) came from your environment and are not stored."
        Write-Info '      Re-export them next run, or the running Host''s tokens change and every skill 403s.'
    }

    foreach ($key in $keys) {
        if (-not $effective[$key]) { Fail "$key resolved empty; the controller would start and never become ready" }
    }

    return $effective
}

# ---------------------------------------------------------------------------------------------
# data root
# ---------------------------------------------------------------------------------------------

function Initialize-DataRoot {
    if (-not $script:UseDataRoot) { return }
    New-Item -ItemType Directory -Force -Path (Join-Path $script:VolumesRoot "$Id/controller-state") | Out-Null
    Write-Info "data root: $(Join-Path $script:VolumesRoot $Id)"
    if ($script:OnWindows) {
        Write-Warning 'A data root on Windows is unverified: Docker Desktop reports the bind as /run/desktop/mnt/host/c/..., which the controller''s backing check does not recognise.'
    }
}

# The entrypoint refuses a volume not bound to the folder it expects, before changing anything.
# Catch it here for the reason, and never resolve it by deleting a volume on the operator's behalf.
function Test-VolumeBacking {
    if (-not $script:UseDataRoot) { return }
    foreach ($suffix in @('postgres-data', 'blob-well-data', 'seq-data', 'host-context')) {
        $volume = "mimisbrunnr-$Id-$suffix"
        $device = & docker volume inspect --format '{{index .Options "device"}}' $volume 2>$null
        if ($LASTEXITCODE -ne 0 -or -not $device) { continue }
        # Desktop reports one folder as /Users/... or /host_mnt/Users/...; treat them as equal, the
        # way the entrypoint does, rather than inventing a stricter rule here.
        if ($device.StartsWith('/host_mnt/')) { $device = $device.Substring('/host_mnt'.Length) }
        $expected = (Join-Path (Join-Path $script:VolumesRoot $Id) $suffix) -replace '\\', '/'
        if ($device -ne $expected) {
            Fail @"
'$volume' is bound to '$device', not '$expected'.
  Adopting a data root is a fresh start, not a move - the controller will refuse before changing
  anything. Pick one:

    new installation     -Id <new-id>
    keep this corpus     omit -DataRoot          # engine-managed volumes, as it was created
    adopt the data root  snapshot + verify, reset the installation, start on the data root, then
                         restore --force into it
"@
        }
    }
}

# ---------------------------------------------------------------------------------------------
# preflight
# ---------------------------------------------------------------------------------------------

function Test-Preflight {
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { Fail 'docker is not on PATH' }
    & docker info *> $null
    if ($LASTEXITCODE -ne 0) { Fail 'the container engine is unreachable' }

    # Known open defect (issue #159, docs/wiki/docker.md:53-56): a second controller tears down every
    # other installation's running workloads. Volumes survive, so it looks survivable. Refuse instead.
    $running = @(& docker ps --format '{{.Names}}' | Where-Object { $_ -match '^mimisbrunnr-.*-controller$' })
    if ($running.Count -gt 0) {
        Fail "another controller is running: $($running -join ', '). Stop it first: docker stop <name>; docker rm <name>"
    }

    # Always pull a mutable tag; "pull only when absent" reuses a stale local copy forever.
    if ($Image -like '*@sha256:*') {
        & docker image inspect $Image *> $null
        if ($LASTEXITCODE -ne 0) { Write-Info "pulling pinned $Image"; Invoke-Docker pull $Image }
    }
    else {
        Write-Info "pulling $Image"
        Invoke-Docker pull $Image
    }

    Write-VersionDrift

    Require-PortFree $DashboardPort 'Dashboard' 'DashboardPort'
    Require-PortFree $OtlpPort 'OTLP' 'OtlpPort'
    Require-PortFree $HostPort 'API' 'HostPort'
    Require-PortFree $PostgresPort 'PostgreSQL' 'PostgresPort'
    Require-PortFree $BlobPort 'blob S3' 'BlobPort'
    Require-PortFree $BlobConsolePort 'blob console' 'BlobConsolePort'
    Require-PortFree $SeqPort 'Seq' 'SeqPort'
}

# The controller bakes its API image in at build time and the pipeline publishes only on main pushes,
# so `latest` lags both the checkout and any run still in flight. Say so rather than let it read as
# current: the API container is what the operator is actually testing.
function Write-VersionDrift {
    $env = @(& docker image inspect --format '{{range .Config.Env}}{{println .}}{{end}}' $Image)
    $apiImage = ($env | Where-Object { $_ -like 'HostConfiguration__Image=*' } | Select-Object -First 1) -replace '^HostConfiguration__Image=', ''
    $version = ($env | Where-Object { $_ -like 'ReleaseConfiguration__Version=*' } | Select-Object -First 1) -replace '^ReleaseConfiguration__Version=', ''

    if (-not $apiImage -or $apiImage -like '*@sha256:0000*') {
        Fail "$Image has no usable baked API image (got '$apiImage'); build it locally with --build-arg API_IMAGE=<repo>@sha256:<digest>"
    }
    Write-Info "API image: $apiImage"

    # The checkout, if this file is being run from one. A copied-out script has none, and reports the
    # digest alone rather than failing.
    $repoRoot = $null
    & git -C $PSScriptRoot rev-parse --show-toplevel *> $null
    if ($LASTEXITCODE -eq 0) { $repoRoot = (& git -C $PSScriptRoot rev-parse --show-toplevel) }

    if (-not $repoRoot) { return }
    $head = (& git -C $repoRoot rev-parse --short HEAD 2>$null)
    if ($LASTEXITCODE -ne 0 -or -not $head) { return }
    if ($version -match '^main-([0-9a-f]{7})$') {
        $baked = $Matches[1]
        if ($baked -eq $head) { Write-Info "controller image matches HEAD ($head)"; return }
        & git -C $repoRoot cat-file -e "$baked^{commit}" 2>$null
        if ($LASTEXITCODE -eq 0) {
            $behind = (& git -C $repoRoot rev-list --count "$baked..$head" 2>$null)
            Write-Warning "image is $behind commit(s) behind HEAD ($baked -> $head). The API container runs the older code, not your checkout."
        }
        else {
            Write-Info "image reports main-$baked; that commit is not in this checkout."
        }
    }
    else {
        Write-Info "image reports version '$version'; cannot compare it to HEAD $head."
    }
}

# ---------------------------------------------------------------------------------------------
# verbs
# ---------------------------------------------------------------------------------------------

# Replaces any running controller for this installation, so a plain `up` is a restart rather than a
# failure. The graceful stop is the controller's own: its shutdown trap tears down the four workloads
# it owns, which is what removes mimisbrunnr-<id>-host, and preserves every volume. Only the controller
# container is then deleted - never a volume, and never a container this installation does not own.
function Remove-ExistingController {
    & docker container inspect $script:ControllerName *> $null
    if ($LASTEXITCODE -ne 0) { return }

    $state = (& docker container inspect --format '{{.State.Status}}' $script:ControllerName)
    if ($state -eq 'running') {
        Write-Info "replacing the running $($script:ControllerName) (graceful stop; volumes preserved)"
        # The controller needs time to stop its workloads: 180 s matches its own --stop-timeout.
        & docker stop --time 180 $script:ControllerName *> $null
    }
    else {
        Write-Info "removing the stopped $($script:ControllerName)"
    }
    & docker rm $script:ControllerName *> $null

    # A hard kill can leave workloads behind with no controller owning them. They carry this
    # installation's ownership label, so remove exactly those and nothing else; a foreign container
    # occupying one of the names is refused by the controller's own preflight rather than deleted here.
    foreach ($suffix in @('host', 'postgres', 'blob-well', 'seq')) {
        $name = "mimisbrunnr-$Id-$suffix"
        & docker container inspect $name *> $null
        if ($LASTEXITCODE -ne 0) { continue }
        $owner = (& docker container inspect --format '{{index .Config.Labels "io.smooth-mimisbrunnr.installation"}}' $name)
        if ($owner -ne $Id) { continue }
        Write-Info "removing orphaned workload $name"
        & docker stop --time 60 $name *> $null
        & docker rm $name *> $null
    }

    # The ports stay bound for a moment after the container goes; wait rather than fail the free-port
    # check on a race that resolves itself in under a second.
    for ($waited = 0; $waited -lt 15; $waited++) {
        if ((Test-PortFree -Port $DashboardPort) -and (Test-PortFree -Port $HostPort)) { return }
        Start-Sleep -Seconds 1
    }
}

function Start-Controller {
    if (-not $script:UseDataRoot) { New-Item -ItemType Directory -Force -Path $script:DataHome | Out-Null }
    Initialize-DataRoot
    Initialize-Credentials | Out-Null
    # Check the data-root binding BEFORE stopping anything. A mismatch is an operator mistake that no
    # amount of restarting fixes, and tearing the running stack down first would leave the machine with
    # no service at all when the run then refuses.
    Test-VolumeBacking
    Remove-ExistingController
    Test-Preflight

    $socket = Resolve-Socket
    $arguments = @(
        'run', '-d',
        '--name', $script:ControllerName,
        # Must equal smooth-mímisbrunnr-release-<id> exactly: the controller cannot label itself, so a
        # mismatch leaves the dashboard's container ungrouped in Docker Desktop.
        '--label', "com.docker.compose.project=$($script:GroupName)",
        '--label', "com.docker.compose.service=$($script:ControllerName)",
        '--stop-timeout', '180',
        '-v', "$socket`:/var/run/docker.sock",
        '--env-file', $script:EnvFile,
        '-e', "InstallationConfiguration__Id=$Id",
        '-e', "EngineConfiguration__BindAddress=$BindAddress",
        '-e', "HostConfiguration__Port=$HostPort",
        '-e', "PostgresConfiguration__Port=$PostgresPort",
        '-e', "BlobConfiguration__Port=$BlobPort",
        '-e', "BlobConfiguration__ConsolePort=$BlobConsolePort",
        '-e', "SeqConfiguration__Port=$SeqPort",
        '-p', "127.0.0.1:${DashboardPort}:15278",
        '-p', "127.0.0.1:${OtlpPort}:19075"
    )

    if ($script:UseDataRoot) {
        $arguments += @('-v', "$($script:VolumesRoot)`:$($script:ContainerDataRoot)", '-e', "ControllerConfiguration__DataRootMount=$($script:ContainerDataRoot)")
        $arguments += @('-v', "$(Join-Path (Join-Path $script:VolumesRoot $Id) 'controller-state')`:/var/lib/mimisbrunnr")
    }
    else {
        & docker volume create $script:StateVolume *> $null
        if ($LASTEXITCODE -ne 0) { Fail "could not create volume $($script:StateVolume)" }
        # Mount it, not merely create it: without the -v the image's own VOLUME directive satisfies
        # /var/lib/mimisbrunnr from an anonymous volume and this named one stays empty.
        # docs/wiki/docker.md:38 documents this mount.
        $arguments += @('-v', "$($script:StateVolume)`:/var/lib/mimisbrunnr")
    }

    $arguments += $Image
    Write-Info "starting $($script:ControllerName)"
    Invoke-Docker @arguments

    Write-Host ''
    Write-Host "  dashboard   $($script:DashboardHost)   (login URL below)"
    Write-Host "  API         http://localhost:$HostPort"
    Write-Host "  PostgreSQL  127.0.0.1:$PostgresPort   blob 127.0.0.1:$BlobPort/$BlobConsolePort   Seq 127.0.0.1:$SeqPort"
    Write-Host "  group       $($script:GroupName)"
    if (-not $script:UseDataRoot) { Write-Host '  volumes     engine-managed named volumes (pass -DataRoot for a host folder)' }
    Write-Host ''
    Write-Host '  A bare visit to the dashboard redirects to /login; this URL carries the token:'

    # The dashboard prints its login URL seconds after start, so poll rather than print a blank line
    # the operator would read as a failure.
    $loginUrl = $null
    foreach ($attempt in 1..12) {
        $logs = & docker logs $script:ControllerName 2>&1 | Out-String
        $match = [regex]::Match($logs, 'http://[^ ]*/login\?t=[^ ]*')
        if ($match.Success) { $loginUrl = $match.Value; break }
        Start-Sleep -Seconds 5
    }
    if ($loginUrl) {
        # Reuse only the path and token: Aspire prints the in-container host:port, the operator
        # reaches the published one.
        Write-Host "    $($script:DashboardHost)$($loginUrl.Substring($loginUrl.IndexOf('/')))"
    }
    else {
        Write-Host "    not printed yet - read it with: $Verb logs"
    }
    Write-Host ''
    Write-Host '  Run it again to pull a newer release and restart. Stop with: -Verb stop'

    if (-not (Wait-ForApi)) { exit 1 }
    Write-Host "  API         healthy at http://localhost:$HostPort"
}

# An earlier draft returned immediately, so `up` printed the API URL and exited 0 while the API
# container was crash-looping on a database it could not authenticate to. "The script ran" and "the app
# started" looked identical, which is what turned one bad password into three session restarts.
function Wait-ForApi {
    $waitSeconds = if ($env:P_WAIT_SECONDS) { [int]$env:P_WAIT_SECONDS } else { 180 }
    Write-Info "waiting for the API to answer on 127.0.0.1:$HostPort"
    $waited = 0
    $code = 'no response'
    while ($waited -lt $waitSeconds) {
        try {
            $response = Invoke-WebRequest -Uri "http://127.0.0.1:$HostPort/health" -TimeoutSec 4 -UseBasicParsing
            $code = $response.StatusCode
        }
        catch { $code = 'no response' }
        if ($code -eq 200) { return $true }
        Start-Sleep -Seconds 3
        $waited += 3
    }

    Write-Host ''
    Write-Info "the API did not become healthy within ${waitSeconds}s (last status '$code')."
    Write-Host '  the containers are up, so the fault is inside one of them. Most likely, in order:'
    Write-Host ''
    Write-Host '    1. postgres rejected the password - this data root was initialised under a different one.'
    Write-Host '       PostgreSQL fixes its password on first init and ignores it forever after, so a'
    Write-Host '       freshly generated one can never match. Check:'
    Write-Host "         docker logs mimisbrunnr-$Id-postgres 2>&1 | Select-String -Pattern auth | Select-Object -Last 3"
    Write-Host "    2. the token pair drifted - ./run.ps1 -Verb logs, then look for 403."
    Write-Host "    3. migrations are pending - ./run.ps1 -Verb logs, look for 'Migration'."
    Write-Host ''
    Write-Host "  full stack state: ./run.ps1 -Verb status"
    return $false
}

function Show-Status {
    & docker container ls --all --filter "label=com.docker.compose.project=$($script:GroupName)" `
        --format 'table {{.Names}}`t{{.Status}}`t{{.Ports}}'
    Write-Host ''
    & docker container inspect $script:ControllerName --format 'controller {{.Name}} state={{.State.Status}} exit={{.State.ExitCode}}' 2>$null
    if ($script:UseDataRoot) {
        Write-Host ''
        Write-Host "data root: $(Join-Path $script:VolumesRoot $Id)"
        Write-Host "credentials: $($script:EnvFile)"
    }
}

function Stop-Controller {
    & docker container inspect $script:ControllerName *> $null
    if ($LASTEXITCODE -ne 0) { Write-Info "no controller named $($script:ControllerName)"; return }
    # Graceful stop removes owned workloads and preserves every data volume and folder.
    Write-Info "stopping $($script:ControllerName) (volumes preserved)"
    Invoke-Docker stop --time 180 $script:ControllerName
    Invoke-Docker rm $script:ControllerName
    Write-Info "removed; run again to pull the newest release and restart: ./run.ps1 -Id $Id"
}

switch ($Verb) {
    'up' { Start-Controller }
    'env' {
        if (-not $script:UseDataRoot) { New-Item -ItemType Directory -Force -Path $script:DataHome | Out-Null }
        Initialize-Credentials | Out-Null
    }
    'status' { Show-Status }
    'stop' { Stop-Controller }
    'logs' { & docker logs --tail 200 $script:ControllerName }
}
