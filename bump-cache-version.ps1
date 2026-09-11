$ErrorActionPreference = 'Stop'
$docsDir = Join-Path $PSScriptRoot "docs"
$files = Get-ChildItem -Path $docsDir -Recurse -File | Where-Object { $_.Extension -in '.html', '.js', '.css' }

$versions = [System.Collections.Generic.HashSet[string]]::new()
$versionRe = [regex]'\?v=(\d+)'

foreach ($file in $files) {
    $text = [System.IO.File]::ReadAllText($file.FullName, [System.Text.Encoding]::UTF8)
    foreach ($m in $versionRe.Matches($text)) {
        [void]$versions.Add($m.Groups[1].Value)
    }
}

if ($versions.Count -ne 1) {
    Write-Error "Cache-bust versions are not consistent yet (found: $($versions -join ', '))"
    exit 1
}

$oldVer = [int]([string[]]$versions)[0]
$newVer = $oldVer + 1

$bumpPattern = "\?v=$oldVer\b"
$count = 0

foreach ($file in $files) {
    $text = [System.IO.File]::ReadAllText($file.FullName, [System.Text.Encoding]::UTF8)
    $updated = [regex]::Replace($text, $bumpPattern, "?v=$newVer")
    if ($updated -ne $text) {
        [System.IO.File]::WriteAllText($file.FullName, $updated, [System.Text.Encoding]::UTF8)
        $count++
    }
}

Write-Host "Successfully bumped cache-bust version: v$oldVer -> v$newVer across $count file(s)." -ForegroundColor Green
