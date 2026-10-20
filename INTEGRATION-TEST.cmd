@echo off
setlocal EnableExtensions
cd /d "%~dp0"
title Kashtrix Live Integration Test
echo.
echo KASHTRIX LIVE INTEGRATION TEST
echo This test uses the CG PREVIEW bus only. It does not take graphics to PROGRAM.
echo Make sure Playout login is complete and the main Playout window is open.
echo.
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File ".\tools\DemoLoad-And-Test.ps1" -RequireRuntime
set "EXITCODE=%ERRORLEVEL%"
echo.
if not "%EXITCODE%"=="0" (
  echo Live integration test FAILED with exit code %EXITCODE%.
) else (
  echo Live integration test PASSED.
)
echo.
pause
exit /b %EXITCODE%
