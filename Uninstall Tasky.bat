@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall-Tasky.ps1"
if errorlevel 1 (
    echo.
    echo The uninstaller did not run to completion - see any error above.
    pause
)
