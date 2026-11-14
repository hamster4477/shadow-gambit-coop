@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0test-support.ps1" -Mode Collect -Role Guest
pause
