@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Open-SandboxSteam.ps1" -AccountId account1
if errorlevel 1 pause
