@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Shared-Library.ps1" %*
pause
