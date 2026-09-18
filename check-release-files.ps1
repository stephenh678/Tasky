<#
.SYNOPSIS
    Verifies Uninstall-Tasky.ps1's $KnownAppFiles list still matches what `dotnet publish` actually
    produces, and that the two uninstaller files the release zip needs are present.

.DESCRIPTION
    Tasky has no installer and no release job - the zip is assembled by hand (see README's
    "Building from source"), and the uninstaller deletes only files it recognizes by name. That
    list is therefore a hand-maintained mirror of the publish output with nothing checking it.

    When it drifts, nothing fails loudly: a native DLL added by a future dependency just gets left
    behind on every uninstall, and the folder removal then reports the vague "still has other files
    in it". This closes that gap in CI.

    Run with no arguments to publish into a temp folder and compare. Pass -PublishDir to check an
    existing publish output instead.
#>
[CmdletBinding()]
param(
    [string]$PublishDir
)

$ErrorActionPreference = 'Stop'

# Files that ship in the zip but are not build output - copied in from the repo root by hand.
$RepoExtras = @("README.md", "Uninstall-Tasky.ps1", "Uninstall Tasky.bat")

# --- Parse $KnownAppFiles out of the uninstaller -------------------------------------
# Read rather than dot-source: the uninstaller starts deleting things on load.

$uninstaller = Join-Path $PSScriptRoot "Uninstall-Tasky.ps1"
if (-not (Test-Path -LiteralPath $uninstaller)) {
    Write-Error "Uninstall-Tasky.ps1 not found next to this script."
    exit 1
}

$text = [System.IO.File]::ReadAllText($uninstaller, [System.Text.Encoding]::UTF8)
$match = [regex]::Match($text, '\$KnownAppFiles\s*=\s*@\((?<body>[^)]*)\)')
if (-not $match.Success) {
    Write-Error "Could not find the `$KnownAppFiles array in Uninstall-Tasky.ps1."
    exit 1
}
$known = [regex]::Matches($match.Groups['body'].Value, '"(?<name>[^"]+)"') |
    ForEach-Object { $_.Groups['name'].Value }

# --- Make sure the hand-copied extras exist -------------------------------------------

$missingExtras = @()
foreach ($extra in $RepoExtras) {
    if ($known -notcontains $extra) { $missingExtras += "$extra (not in `$KnownAppFiles)" }
    if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot $extra))) { $missingExtras += "$extra (not in the repo)" }
}

# --- Publish and compare ---------------------------------------------------------------

$temp = $null
if (-not $PublishDir) {
    $temp = Join-Path ([IO.Path]::GetTempPath()) ("tasky-release-check-{0}" -f ([Guid]::NewGuid()))
    Write-Host "Publishing to $temp ..."
    dotnet publish (Join-Path $PSScriptRoot "TodoApp.csproj") -c Release -r win-x64 -o $temp --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Error "dotnet publish failed."; exit 1 }
    $PublishDir = $temp
}

$published = Get-ChildItem -LiteralPath $PublishDir -File -Recurse |
    ForEach-Object { $_.FullName.Substring($PublishDir.Length).TrimStart('\', '/') }

# Everything published must be listed, or the uninstaller leaves it behind.
$unlisted = @($published | Where-Object { $known -notcontains $_ })
# Everything listed must either be published or be one of the hand-copied extras, or the list has
# stale entries that quietly do nothing.
$stale = @($known | Where-Object { $published -notcontains $_ -and $RepoExtras -notcontains $_ })

if ($temp) { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }

$failed = $false
if ($unlisted.Count -gt 0) {
    Write-Host "FAIL: dotnet publish produces files that Uninstall-Tasky.ps1 would leave behind:" -ForegroundColor Red
    $unlisted | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    Write-Host "  Add them to `$KnownAppFiles." -ForegroundColor Red
    $failed = $true
}
if ($stale.Count -gt 0) {
    Write-Host "FAIL: `$KnownAppFiles lists files that are neither published nor repo extras:" -ForegroundColor Red
    $stale | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    $failed = $true
}
if ($missingExtras.Count -gt 0) {
    Write-Host "FAIL: release extras are missing:" -ForegroundColor Red
    $missingExtras | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    $failed = $true
}

if ($failed) { exit 1 }

Write-Host "OK: `$KnownAppFiles matches dotnet publish ($($published.Count) published file(s)) plus $($RepoExtras.Count) repo extra(s)." -ForegroundColor Green
