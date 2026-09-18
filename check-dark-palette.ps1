param([switch]$Fix)
$ErrorActionPreference = 'Stop'
$cssPath = Join-Path $PSScriptRoot "docs\css\styles.css"
$css = [System.IO.File]::ReadAllText($cssPath, [System.Text.Encoding]::UTF8)

$mediaPattern = '(?s)@media\s*\(prefers-color-scheme:\s*dark\)\s*\{\r?\n\s*:root:not\(\[data-theme="light"\]\)\s*\{\r?\n(.*?)\r?\n\s*\}\r?\n\}'
$forcedPattern = '(?s)(:root\[data-theme="dark"\]\s*\{\r?\n)(.*?)(\r?\n\})'

$mediaMatch = [regex]::Match($css, $mediaPattern)
$forcedMatch = [regex]::Match($css, $forcedPattern)

if (-not $mediaMatch.Success -or -not $forcedMatch.Success) {
    Write-Error "Could not find both dark-palette blocks in docs/css/styles.css"
}

function Parse-Declarations([string]$block) {
    $decls = [ordered]@{}
    foreach ($line in ($block -split "`r?`n")) {
        $trimmed = $line.Trim()
        if ($trimmed -match '^(--[\w-]+):\s*(.+?);$') {
            $decls[$Matches[1]] = $Matches[2]
        }
    }
    return $decls
}

$source = Parse-Declarations $mediaMatch.Groups[1].Value
$forced = Parse-Declarations $forcedMatch.Groups[2].Value

$problems = @()
foreach ($key in $source.Keys) {
    if (-not $forced.Contains($key)) {
        $problems += "  $key is missing from :root[data-theme='dark']"
    } elseif ($forced[$key] -ne $source[$key]) {
        $val1 = $source[$key]
        $val2 = $forced[$key]
        $problems += "  $key : media has '$val1', forced has '$val2'"
    }
}

foreach ($key in $forced.Keys) {
    if (-not $source.Contains($key)) {
        $problems += "  $key is only in :root[data-theme='dark']"
    }
}

if ($problems.Count -eq 0) {
    Write-Host "OK: both dark-palette blocks in styles.css agree on all $($source.Count) custom properties." -ForegroundColor Green
    exit 0
}

if (-not $Fix) {
    Write-Host "MISMATCH: the two dark-palette blocks in docs/css/styles.css differ:" -ForegroundColor Red
    foreach ($p in $problems) { Write-Host $p -ForegroundColor Yellow }
    exit 1
}

$eol = if ($css.Contains("`r`n")) { "`r`n" } else { "`n" }
$lines = @("  color-scheme: dark;")
foreach ($key in $source.Keys) {
    $val = $source[$key]
    $lines += "  ${key}: ${val};"
}
$regenerated = $lines -join $eol

$updated = [regex]::Replace($css, $forcedPattern, {
    param($m)
    return $m.Groups[1].Value + $regenerated + $m.Groups[3].Value
})

# UTF8Encoding($false), not [System.Text.Encoding]::UTF8: the latter's encoder emits a BOM, so
# every rewrite silently prepended one to any file that didn't already have it (caught by a code
# review after a bump added a BOM to docs/js/editor.js). Reading with ::UTF8 stays correct - the
# reader strips a BOM when one is present.
[System.IO.File]::WriteAllText($cssPath, $updated, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Rewrote :root[data-theme='dark'] from the media block ($($source.Count) custom properties)." -ForegroundColor Green
