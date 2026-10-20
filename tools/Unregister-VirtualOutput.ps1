$ErrorActionPreference='Stop'
$filter = Join-Path $env:LOCALAPPDATA 'KashtrixPlayout\VirtualOutput\KashtrixVirtualOutput.ax'
if (-not (Test-Path $filter)) { throw "Virtual output filter not found: $filter" }
$p = Start-Process "$env:SystemRoot\System32\regsvr32.exe" -ArgumentList @('/s','/u',('"'+$filter+'"')) -Verb RunAs -Wait -PassThru
if ($p.ExitCode -ne 0) { throw "regsvr32 /u failed with exit code $($p.ExitCode)" }
Write-Host 'Kashtrix virtual output unregistered.' -ForegroundColor Green
