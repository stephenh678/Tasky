<#
.SYNOPSIS
    Uninstalls Tasky - removes the application files, settings/Google Drive sign-in cache, and
    (optionally) your task data.

.PARAMETER DryRun
    Print everything that would be removed without deleting anything.

.PARAMETER AppFilesOnly
    Internal. Set on the elevated relaunch (see Invoke-ElevatedAppFileRemoval): that stage deletes
    only the application files, because every profile-scoped item was already handled by the
    original, non-elevated run.
#>
[CmdletBinding()]
param(
    [switch]$DryRun,
    [switch]$AppFilesOnly
)

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

# Every deletion in this script goes through here, so -DryRun is honoured everywhere and a failure
# is always reported the same way (a warning the user can act on, never a hard stop).
function Remove-Target {
    param([string]$Path, [switch]$Recurse)

    if (-not (Test-Path -LiteralPath $Path)) {
        Write-Host "  Nothing found at $Path"
        return
    }
    if ($DryRun) {
        Write-Host "  [dry run] Would remove $Path" -ForegroundColor DarkGray
        return
    }
    try {
        Remove-Item -LiteralPath $Path -Force -Recurse:$Recurse
        Write-Host "  Removed $Path"
    } catch {
        Write-Host "  Could not remove $Path ($($_.Exception.Message)) - you may need to delete it manually." -ForegroundColor Yellow
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
#
# check-release-files.ps1 diffs this list against a real `dotnet publish` in CI, so a future
# dependency that adds a native DLL fails the build instead of silently leaving a file behind here.
$KnownAppFiles = @(
    "Tasky.exe",
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

if (-not (Test-Path -LiteralPath (Join-Path $AppFolder "Tasky.exe"))) {
    Write-Host "This doesn't look like a Tasky installation folder - Tasky.exe wasn't found" -ForegroundColor Red
    Write-Host "next to this script ($AppFolder). Stopping without deleting anything." -ForegroundColor Red
    Read-Host "Press Enter to close"
    exit 1
}

# --- Application-file removal ---------------------------------------------------------
# Shared by the normal path and by the elevated -AppFilesOnly relaunch.

function Remove-AppFiles {
    Write-Section "Removing application files..."
    foreach ($fileName in $KnownAppFiles) {
        $filePath = Join-Path $AppFolder $fileName
        if (Test-Path -LiteralPath $filePath) {
            Remove-Target -Path $filePath
        }
    }

    # This process's own working directory is $AppFolder (Explorer sets it when the .bat is
    # double-clicked from inside it) - Windows won't remove a directory that's a running
    # process's current directory, so relocate out of it first or the removal below fails with
    # a "the directory is in use" error even once it's genuinely empty of known files.
    [Environment]::CurrentDirectory = $env:TEMP
    Set-Location $env:TEMP

    # Only removes the folder itself if it's now empty - if anything not on the known-files list is
    # still in there, it's left behind untouched rather than swept away.
    #
    # Check emptiness explicitly and delete via [IO.Directory]::Delete rather than
    # `Remove-Item $AppFolder`. Remove-Item on a NON-empty directory without -Recurse does not
    # fail: it prompts ("The item ... has children..."), and anything that answers yes - a user
    # hitting Enter, or any redirected stdin - deletes the unrecognized files too. That quietly
    # broke this script's entire guarantee of only ever removing files it recognizes.
    # Directory.Delete throws on a non-empty directory instead, and can never prompt.
    $leftover = @(Get-ChildItem -LiteralPath $AppFolder -Force -ErrorAction SilentlyContinue)
    # On a dry run the known files are all still on disk, so discount the ones a real run would
    # have just deleted - otherwise every dry run reports the folder as non-empty.
    if ($DryRun) {
        $leftover = @($leftover | Where-Object { $KnownAppFiles -notcontains $_.Name })
    }
    if ($leftover.Count -gt 0) {
        Write-Host "  $AppFolder still has other files in it, so it was left in place (only the known Tasky files were removed):" -ForegroundColor Yellow
        $leftover | ForEach-Object { Write-Host "    $($_.Name)" -ForegroundColor Yellow }
        return
    }
    if ($DryRun) {
        Write-Host "  [dry run] Would remove $AppFolder (nothing unrecognized left in it)" -ForegroundColor DarkGray
        return
    }
    try {
        [IO.Directory]::Delete($AppFolder)
        Write-Host "  Removed $AppFolder"
    } catch {
        Write-Host "  Could not remove $AppFolder ($($_.Exception.Message)) - you may need to delete it manually." -ForegroundColor Yellow
    }
}

# The elevated stage: application files only, no prompts, no profile-scoped work.
if ($AppFilesOnly) {
    try {
        Write-Host "=== Tasky Uninstaller (application files) ===" -ForegroundColor Cyan
        Remove-AppFiles
        Write-Host ""
        Read-Host "Press Enter to close"
        exit 0
    } catch {
        Write-Host ""
        Write-Host "Removing the application files failed:" -ForegroundColor Red
        Write-Host "  $($_.Exception.Message)" -ForegroundColor Red
        Read-Host "Press Enter to close"
        exit 1
    }
}

# Relaunches JUST the file deletion as administrator.
#
# The whole script used to elevate up front, before doing anything else. That was wrong in a way
# that was invisible on a single-user machine: if the signed-in user is NOT an administrator,
# `-Verb RunAs` prompts for an admin's CREDENTIALS and the relaunched script runs as that admin.
# $env:APPDATA, MyDocuments and HKCU: then all resolved against the ADMIN's profile, so the real
# user's settings, Drive sign-in cache, Run key and task data were silently left behind - and
# reported as "Nothing found at ...", as if the machine were already clean. If the admin happened
# to use Tasky too, it deleted THEIR data instead. Running Tasky.exe --cleanup-notifications from
# that context cleaned the wrong registry hive for the same reason.
#
# Only the application folder can need administrator rights (Program Files); everything else lives
# in the current user's own profile. So the profile work now always runs as the real user, and only
# this last step elevates.
function Invoke-ElevatedAppFileRemoval {
    Write-Section "The application folder needs administrator rights - requesting elevation..."
    # $PSCommandPath needs to be its own quoted token, not a bare array element - Start-Process
    # -ArgumentList doesn't reliably re-quote elements containing spaces when building the actual
    # command line (e.g. "C:\Program Files\Tasky\..."), so an unquoted path here gets silently
    # truncated at the space and the relaunch fails instantly with no visible error.
    $quotedScriptPath = '"' + $PSCommandPath + '"'
    $arguments = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $quotedScriptPath, "-AppFilesOnly")
    if ($DryRun) { $arguments += "-DryRun" }
    try {
        Start-Process -FilePath "powershell.exe" -ArgumentList $arguments -Verb RunAs -ErrorAction Stop -Wait
        Write-Host "  Application files handled in the elevated window."
    } catch {
        # Most commonly: the user clicked "No" on the UAC prompt.
        Write-Host ""
        Write-Host "  Elevation was cancelled or failed, so the application files are still in place:" -ForegroundColor Yellow
        Write-Host "  $AppFolder" -ForegroundColor Yellow
        Write-Host "  Everything outside that folder was already removed - you can delete it by hand." -ForegroundColor Yellow
    }
}

# Everything past this point runs in a window the user is actively watching - if anything
# unexpected throws, make sure the window stays open and says why instead of just vanishing, which
# is exactly what silently swallowed the "Program Files" quoting bug this script previously hit.
try {

    Write-Host "=== Tasky Uninstaller ===" -ForegroundColor Cyan
    if ($DryRun) {
        Write-Host "Dry run: nothing will actually be deleted." -ForegroundColor DarkGray
    }

    # --- Make sure Tasky isn't running ------------------------------------------
    # Closing the window is NOT enough when Settings > "Minimize to system tray when closed (X)"
    # is on (MainWindow.xaml.cs's OnClosing) - the process keeps running behind the tray icon, so
    # a user who dutifully clicks X lands back here with no idea why. Say so explicitly.

    while (Get-Process -Name "Tasky" -ErrorAction SilentlyContinue) {
        Write-Host ""
        Write-Host "Tasky is still running. Close it, then press Enter to continue (or close this window to cancel)." -ForegroundColor Yellow
        Write-Host "If it minimizes to the system tray when you click X, right-click its tray icon and choose Exit." -ForegroundColor Yellow
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
    if (Test-Path -LiteralPath $SettingsPath) {
        try {
            $lastFile = (Get-Content -LiteralPath $SettingsPath -Raw | ConvertFrom-Json).LastFilePath
            # -LiteralPath, like every other path test here: a data file whose path contains [ or ]
            # is otherwise read as a wildcard pattern, Test-Path returns false for a file that does
            # exist, and the warning below silently never fires.
            if ($lastFile -and (Test-Path -LiteralPath $lastFile)) {
                $lastFileDir = Split-Path -Parent $lastFile
                # Compare against the folder WITH a trailing separator. Without it, a sibling like
                # Documents\Tasky2 starts with "Documents\Tasky" and was treated as living inside
                # the folder about to be deleted, so its owner was never warned.
                $ownedPrefix = $DocumentsFolder.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
                $candidate = $lastFileDir.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
                if ($candidate -and -not $candidate.StartsWith($ownedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                    $externalFilePath = $lastFile
                }
            }
        } catch {
            # settings.json unreadable/corrupt - nothing useful to warn about
        }
    }

    # --- Remove settings / Google Drive sign-in cache -----------------------------

    Write-Section "Removing settings and Google Drive sign-in cache..."
    Remove-Target -Path $AppDataFolder -Recurse

    # --- Remove self-update staging cache -----------------------------------------
    # A staged-but-not-yet-applied update (or a stale one from a prior version) leaves a
    # near-full second copy of the app here - app-owned cache, unconditional like the settings
    # folder above rather than gated on $keepData.

    Write-Section "Removing self-update staging cache..."
    Remove-Target -Path $LocalAppDataFolder -Recurse

    # --- Remove "Start with Windows" registry entry --------------------------------
    # Settings.cs has no backing field for this - the Run key itself is the source of truth
    # (see StartupService.cs) - so if it was ever turned on, this is the only place it lives.
    # HKCU: is correct here precisely because this stage always runs as the real user now.

    Write-Section "Removing 'Start with Windows' registry entry..."
    try {
        $runKey = Get-Item -LiteralPath $RunKeyPath -ErrorAction SilentlyContinue
        if ($runKey -and $runKey.GetValue($RunValueName)) {
            if ($DryRun) {
                Write-Host "  [dry run] Would remove $RunKeyPath\$RunValueName" -ForegroundColor DarkGray
            } else {
                Remove-ItemProperty -LiteralPath $RunKeyPath -Name $RunValueName -Force
                Write-Host "  Removed $RunKeyPath\$RunValueName"
            }
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
        Remove-Target -Path $DocumentsFolder -Recurse
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
    # exe is gone. Runs as the real user, so it cleans the right HKCU hive. Best-effort: failing
    # here shouldn't block the rest of the uninstall.

    $TaskyExePath = Join-Path $AppFolder "Tasky.exe"
    if ((Test-Path -LiteralPath $TaskyExePath) -and -not $DryRun) {
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
    } elseif ($DryRun) {
        Write-Section "Cleaning up notification registration..."
        Write-Host "  [dry run] Would run Tasky.exe --cleanup-notifications" -ForegroundColor DarkGray
    }

    # --- Remove the known application files (including this script itself) ---------------
    # A running .ps1 can delete its own file directly - PowerShell parses the whole script
    # into memory before executing it, so it doesn't hold the file open the way a compiled
    # program would. No detached helper process needed.
    #
    # Don't assume admin rights are required - Tasky has no installer, so it could be sitting
    # anywhere from Program Files to the Desktop. Only elevate if a real write test actually fails.

    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin -and -not (Test-CanWrite $AppFolder)) {
        Invoke-ElevatedAppFileRemoval
    } else {
        Remove-AppFiles
    }

    Write-Host ""
    if ($DryRun) {
        Write-Host "Dry run complete - nothing was deleted." -ForegroundColor DarkGray
        Read-Host "Press Enter to close"
    }

} catch {
    Write-Host ""
    Write-Host "Uninstall hit an unexpected error and stopped:" -ForegroundColor Red
    Write-Host "  $($_.Exception.Message)" -ForegroundColor Red
    Read-Host "Press Enter to close"
    exit 1
}
