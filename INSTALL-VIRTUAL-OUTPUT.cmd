@echo off
setlocal
cd /d "%~dp0"
title Kashtrix Playout - Install Virtual Output
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File ".\tools\Setup-VirtualOutput.ps1" -ForceRebuild
set EXITCODE=%ERRORLEVEL%
echo.
if not "%EXITCODE%"=="0" (
  echo Virtual Output setup FAILED with exit code %EXITCODE%.
  echo Kashtrix now installs the Microsoft DirectShow BaseClasses automatically.
  echo If it still failed, check the message above for internet/proxy access or the Visual Studio C++ workload.
) else (
  echo Virtual Output setup completed. Restart OBS/vMix/receiving apps before checking the device list.
)
echo.
pause
exit /b %EXITCODE%
