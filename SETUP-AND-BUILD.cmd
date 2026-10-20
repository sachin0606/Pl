@echo off
setlocal EnableExtensions
cd /d "%~dp0"
if errorlevel 1 (
  echo Unable to enter project directory: %~dp0
  pause
  exit /b 2
)
title Kashtrix Playout - One Click Setup and Build

where powershell.exe >nul 2>&1
if errorlevel 1 (
  echo Windows PowerShell was not found. This setup requires Windows PowerShell 5.1.
  pause
  exit /b 3
)

powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ".\tools\Setup-All.ps1"
set "EXITCODE=%ERRORLEVEL%"
echo.
if not "%EXITCODE%"=="0" (
  echo Kashtrix setup/build FAILED with exit code %EXITCODE%.
  echo Review the error above and the logs in %%LOCALAPPDATA%%\KashtrixPlayout\Logs
) else (
  echo Kashtrix setup/build completed successfully.
)
echo.
pause
exit /b %EXITCODE%
