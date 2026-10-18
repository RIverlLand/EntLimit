@echo off
setlocal
title EntLimit - Build Windows Installer
where pwsh.exe >nul 2>nul
if errorlevel 1 (
  powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\package.ps1" %*
) else (
  pwsh.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\package.ps1" %*
)
if errorlevel 1 (
  echo.
  echo Build failed. Review the error above. The packaging script does not modify application data.
  pause
  exit /b 1
)
echo.
echo Installer ready in artifacts\installer. Share only the Setup EXE and its SHA256 file.
pause
exit /b 0
