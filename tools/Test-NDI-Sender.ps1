param(
    [string]$ProjectRoot = (Resolve-Path "$PSScriptRoot\.."),
    [ValidateSet('Debug','Release')][string]$Configuration = 'Debug'
)
$ErrorActionPreference = 'Stop'

$project = Join-Path $ProjectRoot 'src\BroadcastPlayout.App\BroadcastPlayout.App.csproj'
$log = Join-Path $env:TEMP 'KashtrixNdiSelfTest.log'

Write-Host 'Building Kashtrix Playout x64...' -ForegroundColor Cyan
& dotnet build $project -c $Configuration -p:Platform=x64 -r win-x64
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }

$target = (& dotnet msbuild $project -nologo -getProperty:TargetPath -p:Configuration=$Configuration -p:Platform=x64 -p:RuntimeIdentifier=win-x64 | Select-Object -Last 1).Trim()
if (-not $target -or -not (Test-Path $target)) { throw "Built executable was not found. Resolved TargetPath: $target" }

Remove-Item $log -Force -ErrorAction SilentlyContinue
Write-Host "Running isolated NDI video + audio ABI self-test from:`n  $target" -ForegroundColor Cyan
$process = Start-Process -FilePath $target -ArgumentList '--ndi-selftest' -Wait -PassThru

if (Test-Path $log) {
    Write-Host ''
    Get-Content $log | ForEach-Object { Write-Host "  $_" }
    Write-Host ''
}

if ($process.ExitCode -ne 0) {
    throw "NDI self-test failed or the native process crashed. Exit code: $($process.ExitCode). See $log"
}

Write-Host 'NDI SELF-TEST PASSED.' -ForegroundColor Green
Write-Host 'The test exercised sender creation, BGRA video, interleaved 16-bit stereo audio, and sender destruction.' -ForegroundColor Green
