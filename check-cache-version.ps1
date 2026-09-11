$ErrorActionPreference = 'Stop'
$docsDir = Join-Path $PSScriptRoot "docs"
$csprojPath = Join-Path $PSScriptRoot "TodoApp.csproj"
$configPath = Join-Path $docsDir "js\config.js"

$files = Get-ChildItem -Path $docsDir -Recurse -File | Where-Object { $_.Extension -in '.html', '.js', '.css' }

$found = @()
$versionRe = [regex]'\?v=(\d+)'

foreach ($file in $files) {
    $rel = $file.FullName.Substring($PSScriptRoot.Length + 1)
    $lines = [System.IO.File]::ReadAllLines($file.FullName, [System.Text.Encoding]::UTF8)
    for ($i = 0; $i -lt $lines.Length; $i++) {
        $matches = $versionRe.Matches($lines[$i])
        foreach ($m in $matches) {
            $found += [pscustomobject]@{
                File = $rel
                Line = $i + 1
                Version = $m.Groups[1].Value
            }
        }
    }
}

$ok = $true
if ($found.Count -eq 0) {
    Write-Host "No '?v=NN' occurrences found under docs/." -ForegroundColor Red
    $ok = $false
} else {
    $uniqueVersions = $found.Version | Select-Object -Unique
    if (@($uniqueVersions).Count -eq 1) {
        Write-Host "OK: all $($found.Count) occurrence(s) of '?v=' agree on v$(@($uniqueVersions)[0])." -ForegroundColor Green
    } else {
        Write-Host "MISMATCH: found $($uniqueVersions.Count) different cache-bust versions across $($found.Count) occurrence(s):" -ForegroundColor Red
        foreach ($item in $found) {
            Write-Host "  v$($item.Version)  $($item.File):$($item.Line)" -ForegroundColor Yellow
        }
        $ok = $false
    }
}

# Check Desktop version
$csproj = [System.IO.File]::ReadAllText($csprojPath, [System.Text.Encoding]::UTF8)
$csprojMatch = [regex]::Match($csproj, '<Version>([^<]+)</Version>')
$csprojVer = if ($csprojMatch.Success) { $csprojMatch.Groups[1].Value.Trim() } else { "UNKNOWN" }

$config = [System.IO.File]::ReadAllText($configPath, [System.Text.Encoding]::UTF8)
$configMatch = [regex]::Match($config, "DESKTOP_VERSION = '([^']+)'")
$configVer = if ($configMatch.Success) { $configMatch.Groups[1].Value.Trim() } else { "UNKNOWN" }

if ($csprojVer -eq $configVer) {
    Write-Host "OK: docs/js/config.js's DESKTOP_VERSION ($configVer) matches TodoApp.csproj." -ForegroundColor Green
} else {
    Write-Host "MISMATCH: docs/js/config.js's DESKTOP_VERSION is '$configVer' but TodoApp.csproj is '$csprojVer'." -ForegroundColor Red
    $ok = $false
}

if (-not $ok) { exit 1 }
exit 0
