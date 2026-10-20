@echo off
setlocal EnableExtensions
cd /d "%~dp0"
title Kashtrix Demo Load and Integration Seed
echo.
echo KASHTRIX DEMO LOAD
echo Loads 200 CG templates, NRCS demo rundown, MOS XML, Playout playlist and Prompter live state.
echo.
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File ".\tools\DemoLoad-And-Test.ps1" -LaunchApps
set "EXITCODE=%ERRORLEVEL%"
echo.
if not "%EXITCODE%"=="0" (
  echo Demo load/integration seed FAILED with exit code %EXITCODE%.
) else (
  echo Demo load/integration seed PASSED.
  echo Sign in to Playout if prompted, then run INTEGRATION-TEST.cmd.
)
echo.
pause
exit /b %EXITCODE%
