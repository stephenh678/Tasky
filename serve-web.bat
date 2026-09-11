@echo off
title Tasky Web & Mobile Preview Server
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0serve-web.ps1"
pause
