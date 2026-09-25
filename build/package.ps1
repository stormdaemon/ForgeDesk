<#
.SYNOPSIS
  Builds, tests and packages ForgeDesk for Windows (installer + portable zip) with Velopack.

.EXAMPLE
  ./build/package.ps1 -Version 1.0.0
  ./build/package.ps1 -Version 1.0.0 -SkipTests -Runtime win-arm64

.NOTES
  Output: artifacts/Releases (ForgeDesk-win-Setup.exe, ForgeDesk-win-Portable.zip, update packages)
  Requires the .NET SDK pinned in global.json. Installs the 'vpk' tool locally if missing.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Version,
    [string] $Runtime = 'win-x64',
    [string] $Configuration = 'Release',
    [string] $ReleaseNotes,
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$artifacts = Join-Path $root 'artifacts'
$publishDir = Join-Path $artifacts "publish/$Runtime"
$releasesDir = Join-Path $artifacts 'Releases'
$vpkVersion = '1.2.158'

if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
    throw "Version '$Version' is not a valid semantic version (e.g. 1.2.3 or 1.2.3-beta.1)."
}

function Invoke-Step([string] $Title, [scriptblock] $Action) {
    Write-Host "==> $Title" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "$Title failed with exit code $LASTEXITCODE." }
}

Push-Location $root
try {
    Remove-Item $publishDir, $releasesDir -Recurse -Force -ErrorAction SilentlyContinue

    Invoke-Step 'Restore' { dotnet restore ForgeDesk.slnx }

    if (-not $SkipTests) {
        Invoke-Step 'Unit tests' {
            dotnet test --project tests/ForgeDesk.Core.Tests -c $Configuration --no-restore
            if ($LASTEXITCODE -ne 0) { return }
            dotnet test --project tests/ForgeDesk.Presentation.Tests -c $Configuration --no-restore
        }
    }

    Invoke-Step "Publish ($Runtime, self-contained)" {
        dotnet publish src/ForgeDesk.App/ForgeDesk.App.csproj `
            -c $Configuration -r $Runtime --self-contained true `
            -p:Version=$Version -p:PublishReadyToRun=true `
            -o $publishDir
    }

    if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
        Invoke-Step 'Install vpk' { dotnet tool install --global vpk --version $vpkVersion }
    }

    $packArgs = @(
        'pack',
        '--packId', 'ForgeDesk',
        '--packVersion', $Version,
        '--packDir', $publishDir,
        '--mainExe', 'ForgeDesk.exe',
        '--packTitle', 'ForgeDesk',
        '--packAuthors', 'ForgeDesk contributors',
        '--runtime', $Runtime,
        '--shortcuts', 'Desktop,StartMenuRoot',
        '--outputDir', $releasesDir
    )
    $icon = Join-Path $root 'src/ForgeDesk.App/Assets/ForgeDesk.ico'
    if (Test-Path $icon) { $packArgs += @('--icon', $icon) }
    if ($ReleaseNotes) { $packArgs += @('--releaseNotes', $ReleaseNotes) }

    Invoke-Step 'Package (Velopack)' { vpk @packArgs }

    Get-ChildItem $releasesDir | ForEach-Object { Write-Host ("   {0,-50} {1,10:N0} KB" -f $_.Name, ($_.Length / 1KB)) }
}
finally {
    Pop-Location
}
