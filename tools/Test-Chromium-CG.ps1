param(
    [string]$ProjectRoot = (Resolve-Path "$PSScriptRoot\.."),
    [ValidateSet('Debug','Release')][string]$Configuration = 'Debug'
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $ProjectRoot 'src\BroadcastPlayout.App\BroadcastPlayout.App.csproj'

Write-Host 'Building Kashtrix Playout x64 with Chromium CG...' -ForegroundColor Cyan
& dotnet build $project -c $Configuration -p:Platform=x64 -r win-x64
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

$exe = (& dotnet msbuild $project -nologo -getProperty:TargetPath -p:Configuration=$Configuration -p:Platform=x64 -p:RuntimeIdentifier=win-x64 | Select-Object -Last 1).Trim()
if (-not $exe -or -not (Test-Path $exe)) { throw "Executable not found. Resolved TargetPath: $exe" }

$log = Join-Path $env:TEMP 'KashtrixChromiumSelfTest.log'
Remove-Item $log -Force -ErrorAction SilentlyContinue
Write-Host "Running Chromium off-screen transparent HTML self-test from:`n  $exe" -ForegroundColor Cyan
$p = Start-Process -FilePath $exe -ArgumentList '--chromium-selftest' -Wait -PassThru
if (Test-Path $log) { Get-Content $log | ForEach-Object { Write-Host "  $_" } }
if ($p.ExitCode -ne 0) { throw "Chromium self-test failed with exit code $($p.ExitCode). See $log" }
Write-Host ''
Write-Host 'CHROMIUM CG SELF-TEST PASSED.' -ForegroundColor Green
Write-Host 'The test initialized CEF, loaded the demo HTML/CSS/JS, applied dynamic data, captured a transparent off-screen frame, and shut Chromium down.' -ForegroundColor Green
