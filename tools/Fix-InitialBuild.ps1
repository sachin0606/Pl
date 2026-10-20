$ErrorActionPreference = 'Stop'
Write-Host 'This package already contains the compile fixes. No source patch is required.' -ForegroundColor Green
Write-Host 'Run these commands instead:' -ForegroundColor Cyan
Write-Host '  .\tools\Setup-FFmpeg9.ps1'
Write-Host '  .\tools\Verify-And-Build.ps1'
