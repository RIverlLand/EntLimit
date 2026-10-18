@echo off
title EntLimit Emergency Stop
if exist "%~dp0EntLimit.EmergencyStop.exe" (
  start "" /wait "%~dp0EntLimit.EmergencyStop.exe"
  exit /b
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0emergency-stop.ps1"
if errorlevel 1 echo Emergency stop reported a problem. Please keep this window open for diagnosis.
pause
