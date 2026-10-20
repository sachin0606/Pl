@echo off
setlocal EnableExtensions
cd /d "%~dp0"
title Kashtrix Playout - Entertainment Edition

set "APP_DATA=%LOCALAPPDATA%\KashtrixPlayout"
set "EXE=%APP_DATA%\BuildSessions\active\bin\BroadcastPlayout.App\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Kashtrix.Playout.exe"

if exist "%EXE%" (
    echo Launching Kashtrix Playout from: %EXE%
    start "" "%EXE%" %*
) else (
    echo Failed to find %EXE%
    pause
)