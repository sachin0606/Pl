@echo off
setlocal EnableExtensions
cd /d "%~dp0"
title Kashtrix Playout - Build Installer
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File ".\installer\Build-Installer.ps1" -CopyToDesktop %*
set "EXITCODE=%ERRORLEVEL%"
echo.
if not "%EXITCODE%"=="0" (
  echo Installer build FAILED with exit code %EXITCODE%.
) else (
  echo Installer built and copied to your Desktop.
)
echo.
pause
exit /b %EXITCODE%
