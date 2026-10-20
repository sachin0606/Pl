@echo off
setlocal
cd /d "%~dp0"
title Kashtrix REAL Live Debug Recorder
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\tools\Live-Debug.ps1" -ProjectRoot "%CD%" -IntervalSeconds 2
pause
