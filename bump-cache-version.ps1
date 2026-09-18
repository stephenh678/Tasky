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
        # Preserve each file's existing BOM state instead of imposing one. docs/ is a mix: index.html,
        # app.js, auth.js, drive.js, sync.js and sw.js carry a BOM, editor.js and model.js do not.
        # [System.Text.Encoding]::UTF8's encoder always EMITS a BOM, so using it here added one to
        # every file that lacked it (a review caught the bump doing exactly that to editor.js) - while
        # hardcoding UTF8Encoding($false) instead would strip the six that legitimately have one.
        # Either way a routine version bump produces a diff full of unrelated encoding churn.
        # Reading with ::UTF8 is fine and stays: its DECODER strips a BOM when present.
        $firstBytes = [System.IO.File]::ReadAllBytes($file.FullName) | Select-Object -First 3
        $hadBom = $firstBytes.Count -eq 3 -and $firstBytes[0] -eq 0xEF -and $firstBytes[1] -eq 0xBB -and $firstBytes[2] -eq 0xBF
        [System.IO.File]::WriteAllText($file.FullName, $updated, (New-Object System.Text.UTF8Encoding($hadBom)))
        $count++
    }
}

Write-Host "Successfully bumped cache-bust version: v$oldVer -> v$newVer across $count file(s)." -ForegroundColor Green
