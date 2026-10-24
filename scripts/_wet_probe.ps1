# Harness probe: run Copy-Wet from the installer without a clone or a manifest.
#
# Extracted by scripts/test-install-skills.sh and pointed at a fixture tree, so the PowerShell WET
# path is exercised on the same inputs as the bash one. Kept as a separate file because the
# functions have to be lifted out of install-skills.ps1, which is a script rather than a library.
param([string] $CloneRoot, [string] $Source, [string] $Dest)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$text = Get-Content -Raw (Join-Path $PSScriptRoot 'install-skills.ps1')
foreach ($fn in 'Get-RealPath', 'Test-Symlink', 'Test-InsideClone', 'Copy-Link', 'Copy-Wet') {
    $i = $text.IndexOf("function $fn {")
    if ($i -lt 0) { Write-Error "function $fn not found in install-skills.ps1"; exit 1 }
    $j = $text.IndexOf("`n}", $i)
    Invoke-Expression $text.Substring($i, $j - $i + 2)
}
# The source is the "clone" for this probe.
$script:CloneRoot = $CloneRoot
Copy-Wet -Src $Source -Dst $Dest
