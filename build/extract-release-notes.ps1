<#
.SYNOPSIS
  Extracts the section of CHANGELOG.md for a version and appends the installation notes,
  producing the body of the GitHub release.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Version,
    [Parameter(Mandatory = $true)][string] $Output
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$changelog = Get-Content (Join-Path $root 'CHANGELOG.md') -Raw

$pattern = "(?ms)^## \[$([regex]::Escape($Version))\][^\n]*\n(.*?)(?=^## \[|\z)"
$match = [regex]::Match($changelog, $pattern)
$section = if ($match.Success) { $match.Groups[1].Value.Trim() } else { "See [CHANGELOG.md](https://github.com/stormdaemon/ForgeDesk/blob/v$Version/CHANGELOG.md)." }

$install = Get-Content (Join-Path $PSScriptRoot 'release-install-notes.md') -Raw

New-Item -ItemType Directory -Force -Path (Split-Path $Output) | Out-Null
Set-Content -Path $Output -Encoding utf8 -Value ($section + "`n`n" + $install.Trim() + "`n")
Write-Host "Release notes written to $Output"
