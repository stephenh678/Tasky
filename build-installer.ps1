<#
.SYNOPSIS
    Publishes Tasky and builds the release artifacts: Tasky-Setup-<version>.exe and the
    Tasky-<version>-win-x64.zip that the in-app updater downloads.

.DESCRIPTION
    One command for a release. The version comes from TodoApp.csproj so there is no second place
    to bump, and the zip is produced from the same publish output as the installer so the two can
    never disagree about what's in the build.

    Both artifacts ship:
      * Tasky-Setup-<version>.exe - what people download, and what the in-app updater runs to
        upgrade an existing install.
      * Tasky-<version>-win-x64.zip - the legacy asset. Copies of Tasky from before the installer
        existed look for a "*-win-x64.zip" asset by name (see UpdateService.CheckForUpdateAsync);
        drop it and every one of those installs silently stops finding updates forever.

.PARAMETER Configuration
    Build configuration. Release by default.

.PARAMETER OutputDir
    Where the finished artifacts land. Defaults to .\installer-output.

.PARAMETER SkipZip
    Build only the installer. Do not use for a real release while any pre-installer copies of
    Tasky are still in the wild - see above.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutputDir,
    [switch]$SkipZip
)

$ErrorActionPreference = 'Stop'

$RepoRoot = $PSScriptRoot
$Project = Join-Path $RepoRoot 'TodoApp.csproj'
$PublishDir = Join-Path $RepoRoot 'publish'
if (-not $OutputDir) { $OutputDir = Join-Path $RepoRoot 'installer-output' }

function Write-Section([string]$text) {
    Write-Host ""
    Write-Host $text -ForegroundColor Cyan
}

# --- Version ---------------------------------------------------------------------------

[xml]$csproj = Get-Content -LiteralPath $Project
$version = ($csproj.Project.PropertyGroup.Version | Where-Object { $_ }) | Select-Object -First 1
if (-not $version) { throw "Couldn't read <Version> from $Project." }
$version = "$version".Trim()
Write-Host "=== Building Tasky $version ===" -ForegroundColor Cyan

# --- Locate the Inno Setup compiler -------------------------------------------------------
# Inno installs per-user by default on modern Windows, so %LOCALAPPDATA%\Programs has to be
# checked too - not just the Program Files paths older guides assume.

$isccCandidates = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
)
$iscc = $isccCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $iscc) {
    throw "Inno Setup 6 not found. Install it with:  winget install --id JRSoftware.InnoSetup"
}

# --- Publish ------------------------------------------------------------------------------

Write-Section "Publishing..."
if (Test-Path -LiteralPath $PublishDir) { Remove-Item -LiteralPath $PublishDir -Recurse -Force }
dotnet publish $Project -c $Configuration -r win-x64 -o $PublishDir --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

$publishedExe = Join-Path $PublishDir 'Tasky.exe'
if (-not (Test-Path -LiteralPath $publishedExe)) { throw "Publish produced no Tasky.exe." }

# Assert, don't just report: $version names the installer and drives Inno's AppVersion and the
# DisplayVersion shown in Apps & Features, while $publishedVersion is what the app actually reports
# to the update check. If a Directory.Build.props override or a stale obj/ ever makes them diverge,
# the release ships an installer whose name, registration and contents disagree - and the update
# check would then offer the "new" version to a machine already running it.
$publishedVersion = (Get-Item -LiteralPath $publishedExe).VersionInfo.FileVersion
$normalized = ([version]$publishedVersion).ToString(3)
if ($normalized -ne ([version]$version).ToString(3)) {
    throw "Version mismatch: TodoApp.csproj says $version but the published Tasky.exe reports $publishedVersion."
}
Write-Host "  Published Tasky.exe $publishedVersion"

if (-not (Test-Path -LiteralPath $OutputDir)) { New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null }

# --- Installer -----------------------------------------------------------------------------

Write-Section "Building installer..."
& $iscc "/DAppVersion=$version" "/DPublishDir=$PublishDir" "/O$OutputDir" (Join-Path $RepoRoot 'installer\Tasky.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed." }

$setupExe = Join-Path $OutputDir "Tasky-Setup-$version.exe"
if (-not (Test-Path -LiteralPath $setupExe)) { throw "Expected $setupExe but it wasn't produced." }

# --- Legacy update zip -----------------------------------------------------------------------

if (-not $SkipZip) {
    Write-Section "Building the legacy update zip..."
    $zipPath = Join-Path $OutputDir "Tasky-$version-win-x64.zip"
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    Compress-Archive -Path (Join-Path $PublishDir '*') -DestinationPath $zipPath
    Write-Host "  $zipPath"
}

# --- Summary -----------------------------------------------------------------------------------

Write-Section "Done"
Get-ChildItem -LiteralPath $OutputDir -File | ForEach-Object {
    Write-Host ("  {0}  ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB))
}
Write-Host ""
Write-Host "Upload both files to the GitHub release tagged v$version." -ForegroundColor Green
