@echo off
setlocal
cd /d "%~dp0"
title Kashtrix Output Diagnostics
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File ".\tools\Debug-Outputs.ps1" -ProjectRoot "%CD%"
set EXITCODE=%ERRORLEVEL%
echo.
if not "%EXITCODE%"=="0" echo Output diagnostics returned exit code %EXITCODE%.
pause
exit /b %EXITCODE%
