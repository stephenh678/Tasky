<#
.SYNOPSIS
    Installs Gemini CLI (if needed) and runs a code review against a throwaway COPY of this
    project, so Gemini never has write access to the real source.

.DESCRIPTION
    1. Checks Node.js/npm are present and recent enough, updates npm itself, then installs/updates
       Gemini CLI to the latest version - surfacing any "npm warn"/"npm error" lines from those
       installs explicitly instead of letting them scroll past in the raw npm output.
    2. Copies the project into a fresh folder (C:\Users\steph\OneDrive\Documents\Gemini CLI\Tasky
       by default), excluding build output (bin/obj/publish) and .git - Gemini CLI is only ever
       pointed at this copy, never at $SourceDir.
    3. Runs Gemini CLI non-interactively (-p) inside that copy with a review prompt and saves the
       response as a markdown report back in the real project folder.
    4. Leaves the copy on disk (path printed at the end) in case you want to see if Gemini touched
       anything in it; delete it manually whenever you're done - this script never deletes it for
       you, and never deletes/writes anything under $SourceDir itself.

.NOTES
    Requires either GEMINI_API_KEY set in the environment, or having already completed Gemini
    CLI's interactive Google-login flow once (run `gemini` by itself first if you haven't) -
    a script can't complete a browser-based first-time login on its own.
#>

param(
    [string]$SourceDir = "C:\Users\steph\OneDrive\Documents\Claude Code\TodoApp",
    [string]$ReviewCopyDir = "C:\Users\steph\OneDrive\Documents\Gemini CLI\Tasky",
    [string]$OutputReport = "$SourceDir\gemini-review.md"
)

$ErrorActionPreference = "Stop"

# --- 1. Prerequisites -------------------------------------------------------

# Surfaces "npm warn"/"npm error" lines from an npm command's output explicitly, since they're
# easy to miss scrolling past in verbose install output. Returns $true if any "npm error" lines
# were found (caller should treat that as fatal), $false otherwise.
function Show-NpmIssues {
    param([string[]]$Output, [string]$Label)

    $warnings = $Output | Select-String -Pattern "npm warn" -SimpleMatch:$false
    $errors = $Output | Select-String -Pattern "npm error" -SimpleMatch:$false

    if ($errors) {
        Write-Host ""
        Write-Host "npm reported errors during ${Label}:" -ForegroundColor Red
        $errors | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        return $true
    }

    if ($warnings) {
        Write-Host ""
        Write-Host "npm reported warnings during ${Label} (often safe - e.g. a deprecated transitive dependency):" -ForegroundColor Yellow
        $warnings | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
    }
    else {
        Write-Host "No npm warnings during ${Label}." -ForegroundColor Green
    }
    return $false
}

if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
    Write-Error "Node.js not found. Install it (https://nodejs.org, LTS is fine) first, then re-run this script."
    exit 1
}
if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
    Write-Error "npm not found. Reinstalling Node.js (https://nodejs.org) usually fixes this."
    exit 1
}

$nodeVersion = (node --version).TrimStart("v")
$nodeMajor = [int]($nodeVersion.Split(".")[0])
Write-Host "Node.js v$nodeVersion, npm v$(npm --version)" -ForegroundColor Cyan
if ($nodeMajor -lt 18) {
    Write-Error "Gemini CLI needs Node.js 18 or newer (found v$nodeVersion). Update Node.js from https://nodejs.org and re-run."
    exit 1
}

Write-Host "Updating npm itself..." -ForegroundColor Cyan
$npmSelfUpdateOutput = npm install -g npm@latest 2>&1
if (Show-NpmIssues -Output $npmSelfUpdateOutput -Label "the npm self-update") {
    Write-Error "npm's own update failed - see errors above."
    exit 1
}
Write-Host "npm is now v$(npm --version)" -ForegroundColor Cyan

Write-Host "Installing/updating Gemini CLI to the latest version..." -ForegroundColor Cyan
$geminiInstallOutput = npm install -g "@google/gemini-cli@latest" 2>&1
$geminiInstallFailed = Show-NpmIssues -Output $geminiInstallOutput -Label "the Gemini CLI install"
if ($geminiInstallFailed -or $LASTEXITCODE -ne 0) {
    Write-Error "Gemini CLI install/update did not complete cleanly (exit code $LASTEXITCODE) - see errors above."
    exit 1
}

if (-not (Get-Command gemini -ErrorAction SilentlyContinue)) {
    Write-Error "Gemini CLI installed but 'gemini' isn't on PATH yet - close and reopen your PowerShell session, then re-run this script."
    exit 1
}
Write-Host "Gemini CLI ready: $(gemini --version)" -ForegroundColor Green

# --- 2. Make a throwaway copy of the source ---------------------------------

if (Test-Path $ReviewCopyDir) {
    Write-Host "Removing previous review copy at $ReviewCopyDir..." -ForegroundColor Cyan
    Remove-Item -Recurse -Force $ReviewCopyDir -Confirm:$false
}
New-Item -ItemType Directory -Path $ReviewCopyDir | Out-Null

Write-Host "Copying $SourceDir -> $ReviewCopyDir (excluding bin/obj/publish/.git)..." -ForegroundColor Cyan
robocopy $SourceDir $ReviewCopyDir /E /XD bin obj publish .git /NFL /NDL /NJH /NJS /NC /NS /NP | Out-Null
# robocopy uses exit codes 0-7 for "success with various copy states"; only >=8 is a real failure.
if ($LASTEXITCODE -ge 8) {
    Write-Error "robocopy failed (exit code $LASTEXITCODE)."
    exit 1
}

Write-Host "Copy ready. Gemini CLI will run inside $ReviewCopyDir only - $SourceDir is never touched." -ForegroundColor Green

# --- 3. Run the review against the copy -------------------------------------

$prompt = @"
Review this WPF/.NET 9 C# desktop app (Tasky, a task manager). Do NOT modify, create, or delete
any files - this is a read-only review; just report findings as text.

Cover:
- Correctness bugs (null/reference safety, threading, async, resource leaks, data-loss risks)
- Security issues (unvalidated file/process launches, path handling)
- Maintainability (duplication, overly complex methods, missed reuse opportunities)
- WPF/.NET best-practice deviations

Structure your response as a markdown report with a header per category, most important findings
first, each with the file path and a one-sentence explanation of the concrete risk.
"@

Push-Location $ReviewCopyDir
try {
    Write-Host "Running Gemini CLI review (this can take a few minutes)..." -ForegroundColor Cyan
    gemini -p $prompt | Tee-Object -FilePath $OutputReport
}
finally {
    Pop-Location
}

Write-Host ""
Write-Host "Review saved to: $OutputReport" -ForegroundColor Green
Write-Host "Working copy left at: $ReviewCopyDir (delete manually whenever you're done inspecting it)" -ForegroundColor Green
