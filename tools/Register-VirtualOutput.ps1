param([switch]$NoBuild)
$ErrorActionPreference='Stop'
$root = Split-Path -Parent $PSScriptRoot
$filter = Join-Path $env:LOCALAPPDATA 'KashtrixPlayout\VirtualOutput\KashtrixVirtualOutput.ax'
if (-not (Test-Path $filter) -and -not $NoBuild) { & (Join-Path $PSScriptRoot 'Build-VirtualOutput.ps1') }
if (-not (Test-Path $filter)) { throw "Virtual output filter not found: $filter" }
Write-Host 'Registering Kashtrix Playout Virtual Output...' -ForegroundColor Cyan
$p = Start-Process "$env:SystemRoot\System32\regsvr32.exe" -ArgumentList @('/s',('"'+$filter+'"')) -Verb RunAs -Wait -PassThru
if ($p.ExitCode -ne 0) { throw "regsvr32 failed with exit code $($p.ExitCode)" }
Write-Host 'Registered in DirectShow video + audio capture categories. Restart the receiving application and select Kashtrix Playout Virtual Output / Program Video / Program Audio.' -ForegroundColor Green
