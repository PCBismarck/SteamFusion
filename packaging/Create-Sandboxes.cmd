@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Create-Sandboxes.ps1" %*
pause
