param([string]$ProjectRoot = (Resolve-Path "$PSScriptRoot\.."))
$ErrorActionPreference = 'Stop'

Write-Host 'Running PowerShell 5.1 syntax preflight...' -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'Test-PowerShellSyntax.ps1') -ToolsRoot $PSScriptRoot -BuildOnly

function Remove-KashtrixPathBestEffort {
    param([Parameter(Mandatory=$true)][string]$Path, [string]$Label = 'cache')
    if (-not (Test-Path -LiteralPath $Path)) { return $true }

    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            Get-ChildItem -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue | ForEach-Object {
                try { $_.Attributes = 'Normal' } catch { }
            }
            Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
            return $true
        } catch {
            if ($attempt -lt 3) { Start-Sleep -Milliseconds (250 * $attempt) }
            else {
                Write-Host ("Skipping locked {0}: {1}" -f $Label, $Path) -ForegroundColor DarkYellow
                Write-Host ("  " + $_.Exception.Message) -ForegroundColor DarkGray
                return $false
            }
        }
    }
    return $false
}

# A previous interactive Kashtrix process can keep FFmpeg/CEF/native DLLs loaded from the
# old external build root. Stop only Kashtrix-owned executables; never kill arbitrary dotnet,
# ffmpeg, browser or third-party processes.
$kashtrixProcessNames = @(
    'Kashtrix.Playout','Kashtrix.CGController','Kashtrix.CGEditor','Kashtrix.ChannelController',
    'Kashtrix.FileManager','Kashtrix.IngestServer','Kashtrix.MAM','Kashtrix.Multiview',
    'Kashtrix.PlaylistEditor','Kashtrix.QCController','Kashtrix.Scheduler','Kashtrix.Settings',
    'Kashtrix.NRCS','Kashtrix.Prompter','Kashtrix.HAController','Kashtrix.ApiGateway',
    'Kashtrix.OutputEngine'
)
$stopped = 0
foreach ($name in $kashtrixProcessNames) {
    $procs = @(Get-Process -Name $name -ErrorAction SilentlyContinue)
    foreach ($proc in $procs) {
        try {
            Write-Host ("Closing running Kashtrix process: {0} (PID {1})" -f $proc.ProcessName, $proc.Id) -ForegroundColor Yellow
            Stop-Process -Id $proc.Id -Force -ErrorAction Stop
            $stopped++
        } catch {
            Write-Host ("Could not stop PID {0}; isolated build session will bypass its locked cache." -f $proc.Id) -ForegroundColor DarkYellow
        }
    }
}
if ($stopped -gt 0) { Start-Sleep -Milliseconds 600 }

# Legacy in-tree bin/obj folders are no longer the active build output. Their deletion is
# housekeeping only and must never block setup if antivirus or a running process holds a file.
$projectDir = Join-Path $ProjectRoot 'src\BroadcastPlayout.App'
foreach ($path in @((Join-Path $projectDir 'bin'), (Join-Path $projectDir 'obj'))) {
    if (Test-Path -LiteralPath $path) {
        Write-Host "Cleaning legacy build folder (best effort): $path" -ForegroundColor DarkGray
        [void](Remove-KashtrixPathBestEffort -Path $path -Label 'legacy build folder')
    }
}

$kashtrixLocal = Join-Path $env:LOCALAPPDATA 'KashtrixPlayout'
$legacyExternal = Join-Path $kashtrixLocal 'Build'
if (Test-Path -LiteralPath $legacyExternal) {
    Write-Host "Cleaning legacy shared build cache (best effort): $legacyExternal" -ForegroundColor DarkGray
    [void](Remove-KashtrixPathBestEffort -Path $legacyExternal -Label 'legacy shared build cache')
}

# Single active build root under BuildSessions without manual or timestamp/ID folders.
# Every new build automatically cleans/deletes the previous build output in active to guarantee an up-to-date overwrite.
$sessionsRoot = Join-Path $kashtrixLocal 'BuildSessions'
New-Item -ItemType Directory -Force -Path $sessionsRoot | Out-Null
$sessionRoot = Join-Path $sessionsRoot 'active'

if (Test-Path -LiteralPath $sessionRoot) {
    Write-Host "Cleaning previous build output: $sessionRoot" -ForegroundColor DarkGray
    [void](Remove-KashtrixPathBestEffort -Path $sessionRoot -Label 'previous build output')
}
New-Item -ItemType Directory -Force -Path $sessionRoot | Out-Null
$env:KASHTRIX_BUILD_ROOT = $sessionRoot

$activeMarker = Join-Path $kashtrixLocal 'active-build-root.txt'
[System.IO.File]::WriteAllText($activeMarker, $sessionRoot, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Active single build root: $sessionRoot" -ForegroundColor Green

# Remove any stale manual or timestamp/ID-based session directories
Get-ChildItem -LiteralPath $sessionsRoot -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -ne 'active' } | ForEach-Object {
    [void](Remove-KashtrixPathBestEffort -Path $_.FullName -Label 'stale legacy session')
}

Write-Host 'Build-cache preparation completed. Locked stale caches are non-fatal in FIX31.' -ForegroundColor Green
