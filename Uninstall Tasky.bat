@echo off
REM Forwards any arguments through, so "Uninstall Tasky.bat -DryRun" works from a command prompt.
REM No pause on success: the script itself ends on a Read-Host the user has to answer, and both of
REM its non-zero exit paths already print the reason and wait for Enter before returning here.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall-Tasky.ps1" %*
