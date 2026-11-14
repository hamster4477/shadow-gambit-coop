@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0test-support.ps1" -Mode Collect -Role Host
pause
