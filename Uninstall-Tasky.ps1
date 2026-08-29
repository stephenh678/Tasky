<#
.SYNOPSIS
    Uninstalls Tasky - removes the application files, settings/Google Drive sign-in cache, and
    (optionally) your task data.
#>

$ErrorActionPreference = 'Stop'

function Write-Section([string]$text) {
    Write-Host ""
    Write-Host $text -ForegroundColor Cyan
}

function Test-CanWrite([string]$path) {
    $probe = Join-Path $path (".uninstall-write-test-{0}.tmp" -f ([Guid]::NewGuid()))
    try {
        [IO.File]::WriteAllText($probe, "")
        Remove-Item -LiteralPath $probe -Force -ErrorAction SilentlyContinue
        return $true
    } catch {
        return $false
    }
}

# --- Key locations -------------------------------------------------------------

$AppFolder = $PSScriptRoot
$AppDataFolder = Join-Path $env:APPDATA "Tasky"
$SettingsPath = Join-Path $AppDataFolder "settings.json"
$DocumentsFolder = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::MyDocuments)) "Tasky"
# Self-update staging cache (Services/UpdateService.cs) - a previously-downloaded/extracted
# update can leave a near-full second copy of the app here, distinct from %APPDATA%\Tasky above.
$LocalAppDataFolder = Join-Path $env:LOCALAPPDATA "Tasky"
# "Start with Windows" toggle (Services/StartupService.cs) - app registration, not user data,
# so like $AppDataFolder this is removed unconditionally rather than gated on $keepData.
$RunKeyPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$RunValueName = "Tasky"

# Exact files the release zip ships (Tasky.exe + its native DLLs, and this uninstaller). Named
# explicitly rather than wiping $AppFolder wholesale - this can only ever remove files it
# recognizes, so it can never take out something unrelated that happens to share the folder,
# whether that's from running it somewhere unexpected or anything else already sitting there.
$KnownAppFiles = @(
    "Tasky.exe",
    "Tasky.pdb",
    "D3DCompiler_47_cor3.dll",
    "PenImc_cor3.dll",
    "PresentationNative_cor3.dll",
    "vcruntime140_cor3.dll",
    "wpfgfx_cor3.dll",
    "README.md",
    "Uninstall-Tasky.ps1",
    "Uninstall Tasky.bat"
)

# --- Confirm this is actually a Tasky installation folder before doing anything -------

if (-not (Test-Path (Join-Path $AppFolder "Tasky.exe"))) {
    Write-Host "This doesn't look like a Tasky installation folder - Tasky.exe wasn't found" -ForegroundColor Red
    Write-Host "next to this script ($AppFolder). Stopping without deleting anything." -ForegroundColor Red
    Read-Host "Press Enter to close"
    exit 1
}

# --- Elevate first if needed, before any prompts are shown ---------------------
# Don't assume admin rights are required - Tasky has no installer, so it could be
# sitting anywhere from Program Files to the Desktop. Only ask for elevation if a
# real write test against the app's own folder actually fails.

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin -and -not (Test-CanWrite $AppFolder)) {
    Write-Host "Tasky is installed somewhere that needs administrator rights to remove. Requesting elevation..." -ForegroundColor Yellow
    # $PSCommandPath needs to be its own quoted token, not a bare array element - Start-Process
    # -ArgumentList doesn't reliably re-quote elements containing spaces when building the actual
    # command line (e.g. "C:\Program Files\Tasky\..."), so an unquoted path here gets silently
    # truncated at the space and the relaunch fails instantly with no visible error.
    $quotedScriptPath = '"' + $PSCommandPath + '"'
    try {
        Start-Process -FilePath "powershell.exe" -ArgumentList @(
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $quotedScriptPath
        ) -Verb RunAs -ErrorAction Stop
    } catch {
        # Most commonly: the user clicked "No" on the UAC prompt. Without this, the exception
        # would go uncaught and this window (launched via Uninstall Tasky.bat, which has no
        # trailing pause) would just vanish with no explanation and nothing removed.
        Write-Host ""
        Write-Host "Elevation was cancelled or failed, so nothing was removed." -ForegroundColor Yellow
        Read-Host "Press Enter to close"
    }
    exit
}

# Everything past this point runs in a window the user is actively watching (either the
# original, or the elevated relaunch) - if anything unexpected throws, make sure the window
# stays open and says why instead of just vanishing, which is exactly what silently swallowed
# the "Program Files" quoting bug this script previously hit.
try {

    # --- Make sure Tasky isn't running ------------------------------------------

    Write-Host "=== Tasky Uninstaller ===" -ForegroundColor Cyan

    while (Get-Process -Name "Tasky" -ErrorAction SilentlyContinue) {
        Write-Host ""
        Write-Host "Tasky is currently running. Please close it, then press Enter to continue (or close this window to cancel)." -ForegroundColor Yellow
        Read-Host | Out-Null
    }

    # --- Single confirmation -----------------------------------------------------

    Write-Host ""
    $confirm = Read-Host "Are you sure you want to uninstall Tasky? [y/N]"
    if ($confirm -notmatch '^[Yy]$') {
        Write-Host ""
        Write-Host "Cancelled. Nothing was removed." -ForegroundColor Yellow
        Read-Host "Press Enter to close"
        exit
    }

    $keepData = (Read-Host "Keep your existing .tasky files, backups, and attachments? [y/N]") -match '^[Yy]$'

    # --- Note any data file living outside the default Documents\Tasky folder ----
    # Save Data File As... lets a file live anywhere, and settings.json only remembers
    # the single most-recently-used path - never guess at or delete locations outside
    # the folder Tasky actually owns.

    $externalFilePath = $null
    if (Test-Path $SettingsPath) {
        try {
            $lastFile = (Get-Content -LiteralPath $SettingsPath -Raw | ConvertFrom-Json).LastFilePath
            if ($lastFile -and (Test-Path $lastFile)) {
                $lastFileDir = Split-Path -Parent $lastFile
                if ($lastFileDir -and -not $lastFileDir.StartsWith($DocumentsFolder, [StringComparison]::OrdinalIgnoreCase)) {
                    $externalFilePath = $lastFile
                }
            }
        } catch {
            # settings.json unreadable/corrupt - nothing useful to warn about
        }
    }

    # --- Remove settings / Google Drive sign-in cache -----------------------------

    Write-Section "Removing settings and Google Drive sign-in cache..."
    if (Test-Path $AppDataFolder) {
        try {
            Remove-Item -LiteralPath $AppDataFolder -Recurse -Force
            Write-Host "  Removed $AppDataFolder"
        } catch {
            Write-Host "  Could not remove $AppDataFolder ($($_.Exception.Message)) - you may need to delete it manually." -ForegroundColor Yellow
        }
    } else {
        Write-Host "  Nothing found at $AppDataFolder"
    }

    # --- Remove self-update staging cache -----------------------------------------
    # A staged-but-not-yet-applied update (or a stale one from a prior version) leaves a
    # near-full second copy of the app here - app-owned cache, unconditional like the settings
    # folder above rather than gated on $keepData.

    Write-Section "Removing self-update staging cache..."
    if (Test-Path $LocalAppDataFolder) {
        try {
            Remove-Item -LiteralPath $LocalAppDataFolder -Recurse -Force
            Write-Host "  Removed $LocalAppDataFolder"
        } catch {
            Write-Host "  Could not remove $LocalAppDataFolder ($($_.Exception.Message)) - you may need to delete it manually." -ForegroundColor Yellow
        }
    } else {
        Write-Host "  Nothing found at $LocalAppDataFolder"
    }

    # --- Remove "Start with Windows" registry entry --------------------------------
    # Settings.cs has no backing field for this - the Run key itself is the source of truth
    # (see StartupService.cs) - so if it was ever turned on, this is the only place it lives.

    Write-Section "Removing 'Start with Windows' registry entry..."
    try {
        $runKey = Get-Item -LiteralPath $RunKeyPath -ErrorAction SilentlyContinue
        if ($runKey -and $runKey.GetValue($RunValueName)) {
            Remove-ItemProperty -LiteralPath $RunKeyPath -Name $RunValueName -Force
            Write-Host "  Removed $RunKeyPath\$RunValueName"
        } else {
            Write-Host "  Nothing found at $RunKeyPath\$RunValueName"
        }
    } catch {
        Write-Host "  Could not remove $RunKeyPath\$RunValueName ($($_.Exception.Message)) - you may need to remove it manually via Task Manager's Startup tab." -ForegroundColor Yellow
    }

    # --- Remove (or keep) task data -------------------------------------------------

    if ($keepData) {
        Write-Section "Keeping your task data"
        Write-Host "  Left in place: $DocumentsFolder"
    } else {
        Write-Section "Removing task data..."
        if (Test-Path $DocumentsFolder) {
            try {
                Remove-Item -LiteralPath $DocumentsFolder -Recurse -Force
                Write-Host "  Removed $DocumentsFolder"
            } catch {
                Write-Host "  Could not remove $DocumentsFolder ($($_.Exception.Message)) - you may need to delete it manually." -ForegroundColor Yellow
            }
        } else {
            Write-Host "  Nothing found at $DocumentsFolder"
        }
    }

    # --- Final notes ---------------------------------------------------------------------

    Write-Section "One more thing"
    Write-Host "Removing the local Google Drive sign-in cache signs Tasky out on this computer, but"
    Write-Host "doesn't revoke its access on Google's side. To fully revoke it, visit:"
    Write-Host "  https://myaccount.google.com/permissions" -ForegroundColor Cyan

    if ($externalFilePath) {
        Write-Host ""
        Write-Host "Your last-used data file was outside the default Tasky folder, so it (and its" -ForegroundColor Yellow
        Write-Host "Attachments/InlineImages folders alongside it, if any) was left alone:" -ForegroundColor Yellow
        Write-Host "  $externalFilePath"
    }

    Write-Host ""
    Read-Host "Press Enter to remove the application files and finish"

    # --- Clean up toast notification registration -----------------------------------------
    # Tasky registers some registry-based COM/AUMID plumbing the first time it shows a reminder
    # notification (see ToastNotificationService.Initialize) - ask it to reverse that before its
    # exe is gone. Best-effort: failing here shouldn't block the rest of the uninstall.

    $TaskyExePath = Join-Path $AppFolder "Tasky.exe"
    if (Test-Path -LiteralPath $TaskyExePath) {
        Write-Section "Cleaning up notification registration..."
        try {
            Start-Process -FilePath $TaskyExePath -ArgumentList "--cleanup-notifications" -Wait -WindowStyle Hidden
            Write-Host "  Done"
        } catch {
            Write-Host "  Could not clean up notification registration ($($_.Exception.Message)) - harmless, skipping." -ForegroundColor Yellow
        }

        # Merely starting Tasky.exe above - even in --cleanup-notifications mode - unconditionally
        # recreates Documents\Tasky\debug.log: App's constructor logs a line before OnStartup even
        # checks for that flag (see AppLogger.cs/App.xaml.cs). If task data was already removed
        # above, this launch just silently rebuilt the folder with nothing but a log file in it -
        # clean that back up now that it's served its purpose. If data was kept, leave it: AppLogger
        # only ever appends, so this just added a few harmless lines to a log the user is keeping.
        if (-not $keepData) {
            $DebugLogPath = Join-Path $DocumentsFolder "debug.log"
            if (Test-Path -LiteralPath $DebugLogPath) {
                Remove-Item -LiteralPath $DebugLogPath -Force -ErrorAction SilentlyContinue
            }
            if ((Test-Path -LiteralPath $DocumentsFolder) -and
                -not (Get-ChildItem -LiteralPath $DocumentsFolder -Force -ErrorAction SilentlyContinue)) {
                Remove-Item -LiteralPath $DocumentsFolder -Force -ErrorAction SilentlyContinue
            }
        }
    }

    # --- Remove the known application files (including this script itself) ---------------
    # A running .ps1 can delete its own file directly - PowerShell parses the whole script
    # into memory before executing it, so it doesn't hold the file open the way a compiled
    # program would. No detached helper process needed.

    Write-Section "Removing application files..."
    foreach ($fileName in $KnownAppFiles) {
        $filePath = Join-Path $AppFolder $fileName
        if (Test-Path -LiteralPath $filePath) {
            try {
                Remove-Item -LiteralPath $filePath -Force
                Write-Host "  Removed $filePath"
            } catch {
                Write-Host "  Could not remove $filePath ($($_.Exception.Message))" -ForegroundColor Yellow
            }
        }
    }

    # This process's own working directory is $AppFolder (Explorer sets it when the .bat is
    # double-clicked from inside it) - Windows won't remove a directory that's a running
    # process's current directory, so relocate out of it first or the removal below fails with
    # a "the directory is in use" error even once it's genuinely empty of known files.
    [Environment]::CurrentDirectory = $env:TEMP
    Set-Location $env:TEMP

    # Only removes the folder itself if it's now empty - if anything not on the known-files
    # list is still in there, it's left behind untouched rather than swept away.
    try {
        Remove-Item -LiteralPath $AppFolder -ErrorAction Stop
        Write-Host "  Removed $AppFolder"
    } catch {
        Write-Host "  $AppFolder still has other files in it, so it was left in place (only the known Tasky files were removed)." -ForegroundColor Yellow
    }

} catch {
    Write-Host ""
    Write-Host "Uninstall hit an unexpected error and stopped:" -ForegroundColor Red
    Write-Host "  $($_.Exception.Message)" -ForegroundColor Red
    Read-Host "Press Enter to close"
    exit 1
}
