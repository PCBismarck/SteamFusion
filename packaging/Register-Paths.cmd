@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Register-Paths.ps1" %*
pause
