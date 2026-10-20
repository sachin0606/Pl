param([string]$ProjectRoot = (Resolve-Path "$PSScriptRoot\.."))
$ErrorActionPreference = 'Stop'
Set-Location $ProjectRoot
Write-Host 'KASHTRIX ONE-CLICK SETUP / BUILD' -ForegroundColor Cyan
Write-Host $ProjectRoot -ForegroundColor DarkGray

# FIX48A: fail fast before any dependency download. This catches Windows PowerShell 5.1
# syntax/encoding problems immediately instead of after a large FFmpeg download.
Write-Host 'Running early PowerShell 5.1 setup/build preflight...' -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'Test-PowerShellSyntax.ps1') -ToolsRoot $PSScriptRoot -BuildOnly

# FIX29 extraction/path preflight. FIX28's reference PNG sequence reached the classic
# 260-character Windows path boundary when extracted under the long release folder name.
# The sequence is now stored under demos\ref\brk, and we verify it before downloading
# dependencies so an incomplete Explorer extraction fails immediately and clearly.
$rootText = [string]$ProjectRoot
if ($rootText.Length -gt 170) {
    Write-Host ('Project path is long (' + $rootText.Length + ' chars). Kashtrix uses short asset/native build paths, but C:\Kashtrix\FIX31 is still recommended.') -ForegroundColor DarkYellow
}
$breakingFrames = Join-Path $ProjectRoot 'src\BroadcastPlayout.App\demos\ref\brk\primeBack'
if (-not (Test-Path $breakingFrames)) { throw "Demo asset preflight failed: $breakingFrames was not extracted. Use the FIX31 full ZIP and extract again." }
$breakingCount = @(Get-ChildItem $breakingFrames -Filter '*.png' -File -ErrorAction Stop).Count
if ($breakingCount -lt 195) { throw "Demo asset preflight failed: only $breakingCount/195 BreakingSequence frames are present. Extract the FIX31 full ZIP again." }
Write-Host "BreakingSequence extraction verified: $breakingCount frames." -ForegroundColor Green

$ffmpegDir = Join-Path $ProjectRoot 'src\BroadcastPlayout.App\native\ffmpeg'
$required = @('avcodec-63.dll','avformat-63.dll','avutil-61.dll','swscale-10.dll','swresample-7.dll','ffmpeg.exe','ffprobe.exe')
$missing = @($required | Where-Object { -not (Test-Path (Join-Path $ffmpegDir $_)) })
$runtimeNeedsInstall = $missing.Count -gt 0
if (-not $runtimeNeedsInstall) {
    $mediaExe = Join-Path $ffmpegDir 'ffmpeg.exe'
    try {
        $encoderOutput = (& $mediaExe -hide_banner -encoders 2>&1 | Out-String)
        $h264Candidates = @('libx264','h264_mf','libopenh264','h264_qsv','h264_amf','h264_nvenc')
        $hasH264 = $false
        foreach ($candidate in $h264Candidates) {
            if ($encoderOutput -match ('(?m)^\s*V\S*\s+' + [regex]::Escape($candidate) + '\s')) { $hasH264 = $true; break }
        }
        if (-not $hasH264) { $runtimeNeedsInstall = $true }
    } catch {
        $runtimeNeedsInstall = $true
    }
}
if ($runtimeNeedsInstall) {
    Write-Host 'Media runtime is missing/incomplete or lacks H.264 recording support. Installing...' -ForegroundColor Yellow
    & (Join-Path $PSScriptRoot 'Setup-FFmpeg9.ps1') -ProjectRoot $ProjectRoot
} else {
    Write-Host 'Media runtime with H.264 recording support already present - skipping download.' -ForegroundColor Green
}

if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable('OPENWEATHER_API_KEY','User'))) {
    Write-Host 'OpenWeather key is not configured. Optional weather setup will open now.' -ForegroundColor Yellow
    & (Join-Path $PSScriptRoot 'Set-OpenWeather-Key.ps1')
} else {
    Write-Host 'OPENWEATHER_API_KEY already configured - skipping.' -ForegroundColor Green
}

& (Join-Path $PSScriptRoot 'Clean-BuildCache.ps1') -ProjectRoot $ProjectRoot
Write-Host 'Running full verification/build...' -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'Verify-And-Build.ps1') -ProjectRoot $ProjectRoot

Write-Host ''
Write-Host 'Kashtrix setup and build completed.' -ForegroundColor Green
$effectiveBuildRoot = $env:KASHTRIX_BUILD_ROOT
if ([string]::IsNullOrWhiteSpace($effectiveBuildRoot)) { $effectiveBuildRoot = Join-Path $env:LOCALAPPDATA 'KashtrixPlayout\BuildSessions\active' }
Write-Host ('Build root: ' + $effectiveBuildRoot) -ForegroundColor Green
Write-Host 'Diagnostics are intentionally separate. Run LIVE-DEBUG.cmd or OUTPUT-DEBUG.cmd after the build.' -ForegroundColor DarkGray
