param(
    [Parameter(Mandatory = $true)][string]$ProjectDir,
    [Parameter(Mandatory = $true)][ValidateSet('prepare', 'bump')][string]$Mode
)

$ErrorActionPreference = 'Stop'
$versionFile = Join-Path $ProjectDir 'art-version.txt'
$displayFile = Join-Path $ProjectDir 'art-display.txt'
$assemblyFile = Join-Path $ProjectDir 'art-assembly.txt'
$semverFile = Join-Path $ProjectDir 'art-semver.txt'

if (-not (Test-Path $versionFile)) {
    [System.IO.File]::WriteAllText($versionFile, '0')
}

$raw = [System.IO.File]::ReadAllText($versionFile).Trim()
$n = 0
[void][int]::TryParse($raw, [ref]$n)

if ($Mode -eq 'prepare') {
    if ($n -eq 0) {
        $display = '1.0'
        $asm = '1.0.0.0'
        $semver = '1.0.0'
    }
    else {
        $display = '1.{0:D2}' -f $n
        $asm = '1.{0}.0.0' -f $n
        $semver = '1.{0}.0' -f $n
    }

    [System.IO.File]::WriteAllText($displayFile, $display)
    [System.IO.File]::WriteAllText($assemblyFile, $asm)
    [System.IO.File]::WriteAllText($semverFile, $semver)
    Write-Host "ART-Monitor version: $display (counter $n)"
    return
}

$next = $n + 1
[System.IO.File]::WriteAllText($versionFile, "$next")
Write-Host "ART-Monitor version counter -> $next"
