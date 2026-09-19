@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Open-SandboxSteam.ps1" -AccountId account2
if errorlevel 1 pause
