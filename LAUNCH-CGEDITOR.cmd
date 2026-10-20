@echo off
setlocal EnableExtensions
cd /d "%~dp0"
title Kashtrix CG Editor

set "APP_DATA=%LOCALAPPDATA%\KashtrixPlayout"
set "ACTIVE_ROOT_FILE=%APP_DATA%\active-build-root.txt"
set "BUILD_ROOT="

if exist "%ACTIVE_ROOT_FILE%" (
    set /p BUILD_ROOT=<"%ACTIVE_ROOT_FILE%"
)

if "%BUILD_ROOT%"=="" (
    if defined KASHTRIX_BUILD_ROOT (
        set "BUILD_ROOT=%KASHTRIX_BUILD_ROOT%"
    )
)

if not "%BUILD_ROOT%"=="" (
    set "EXE=%BUILD_ROOT%\bin\Kashtrix.CGEditor\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Kashtrix.CGEditor.exe"
)

if not exist "%EXE%" (
    set "MANIFEST=%APP_DATA%\suite-apps.json"
    if exist "%MANIFEST%" (
        for /f "usebackq delims=" %%A in (`powershell -NoProfile -Command "try { (Get-Content '%MANIFEST%' -Raw | ConvertFrom-Json).'Kashtrix.CGEditor' } catch {}"`) do (
            if exist "%%A" set "EXE=%%A"
        )
    )
)

if not exist "%EXE%" (
    echo Searching existing build sessions...
    for /f "delims=" %%F in ('dir /b /s /a:-d "%APP_DATA%\BuildSessions\Kashtrix.CGEditor.exe" 2^>nul') do (
        set "EXE=%%F"
    )
)

if not exist "%EXE%" (
    echo Kashtrix.CGEditor.exe was not found in active build root.
    echo Building CG Editor into active build session...
    if "%BUILD_ROOT%"=="" (
        set "BUILD_ROOT=%APP_DATA%\BuildSessions\active"
    )
    set "KASHTRIX_BUILD_ROOT=%BUILD_ROOT%"
    dotnet build src\Kashtrix.CGEditor\Kashtrix.CGEditor.csproj -c Debug -p:Platform=x64 -p:KashtrixBuildRoot="%BUILD_ROOT%"
    set "EXE=%BUILD_ROOT%\bin\Kashtrix.CGEditor\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Kashtrix.CGEditor.exe"
)

if exist "%EXE%" (
    echo Launching Kashtrix CG Editor from:
    echo %EXE%
    start "" "%EXE%" %*
) else (
    echo Failed to find or launch Kashtrix.CGEditor.exe
    pause
)
