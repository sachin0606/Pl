param([string]$ProjectRoot = (Resolve-Path "$PSScriptRoot\.."))
$ErrorActionPreference = 'Continue'
$logDir = Join-Path $env:LOCALAPPDATA 'KashtrixPlayout\Logs'
New-Item $logDir -ItemType Directory -Force | Out-Null
$log = Join-Path $logDir ('Output-Debug-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log')

function Log([string]$text) { $text | Tee-Object -FilePath $log -Append }
function Invoke-NativeCapture([string]$exe, [string[]]$arguments) {
    $stdout = [IO.Path]::GetTempFileName()
    $stderr = [IO.Path]::GetTempFileName()
    try {
        $p = Start-Process -FilePath $exe -ArgumentList $arguments -NoNewWindow -Wait -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
        $text = ((Get-Content $stdout -Raw -ErrorAction SilentlyContinue) + (Get-Content $stderr -Raw -ErrorAction SilentlyContinue)).TrimEnd()
        [pscustomobject]@{ ExitCode = $p.ExitCode; Text = $text }
    } finally {
        Remove-Item $stdout,$stderr -Force -ErrorAction SilentlyContinue
    }
}

Log '=== Kashtrix Output Diagnostics ==='
Log ("Time: " + (Get-Date))
Log ("Project: " + $ProjectRoot)
Log ("OS: " + [Environment]::OSVersion.VersionString)
Log ("Process arch: " + [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture)

$ndi = Join-Path $ProjectRoot 'src\BroadcastPlayout.App\native\ndi\Processing.NDI.Lib.x64.dll'
Log ''
Log '--- NDI SDK output ---'
Log ("Runtime DLL: " + $ndi)
Log ("Exists: " + (Test-Path $ndi))
if (Test-Path $ndi) {
    $f = Get-Item $ndi
    Log ("Size: " + $f.Length)
    Log ("FileVersion: " + $f.VersionInfo.FileVersion)
    Log ("ProductVersion: " + $f.VersionInfo.ProductVersion)
}
$ndiCheck = Join-Path $ProjectRoot 'tools\Check-NDI6.ps1'
if (Test-Path $ndiCheck) {
    try { (& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $ndiCheck 2>&1 | Out-String) | Tee-Object -FilePath $log -Append | Write-Host } catch { Log $_.Exception.Message }
}
Log 'Kashtrix NDI Program Output uses Processing.NDI.Lib.x64.dll directly; FFmpeg does NOT need an NDI input/output module.'
Log 'FIX46 CG Alpha Output also uses direct NDI BGRA frames; enable it in CG Controller -> CG OUTPUT / ALPHA / FILL + KEY.'

Log ''
Log '--- DeckLink CG Fill/Key ---'
$deckInterop = Join-Path $ProjectRoot 'src\BroadcastPlayout.App\vendor\decklink\Interop.DeckLinkAPI64.dll'
$deckBuild = Join-Path $ProjectRoot 'tools\Build-DeckLinkInterop.ps1'
Log ("DeckLink interop: " + $deckInterop + ' exists=' + (Test-Path $deckInterop))
Log ("Interop build helper: " + $deckBuild + ' exists=' + (Test-Path $deckBuild))
if (-not (Test-Path $deckInterop)) {
    Log 'ACTION: Install Blackmagic Desktop Video + SDK, then run tools\Build-DeckLinkInterop.ps1. Rebuild x64 after interop generation.'
} else {
    Log 'Interop is present. In CG Controller select External or Internal keyer and the correct DeckLink device index, then APPLY OUTPUT.'
}

Log ''
Log '--- Virtual Output / DirectShow ---'
$axCandidates = @(
    (Join-Path $env:LOCALAPPDATA 'KashtrixPlayout\VirtualOutput\KashtrixVirtualOutput.ax'),
    (Join-Path $ProjectRoot 'build\VirtualOutput\Release\KashtrixVirtualOutput.ax'),
    (Join-Path $ProjectRoot 'build\VirtualOutput\Debug\KashtrixVirtualOutput.ax'),
    (Join-Path $ProjectRoot 'native\Kashtrix.VirtualOutput\x64\Release\KashtrixVirtualOutput.ax'),
    (Join-Path $ProjectRoot 'native\Kashtrix.VirtualOutput\x64\Debug\KashtrixVirtualOutput.ax')
)
$foundAx = @($axCandidates | Where-Object { Test-Path $_ } | Select-Object -Unique)
if ($foundAx.Count -eq 0) { Log 'Filter binary: NOT BUILT / NOT INSTALLED' }
else { $foundAx | ForEach-Object { Log ("AX found: " + $_) } }

$registered = $false
try {
    $virtualOutputClsid = '{C7453623-A2E2-49E0-B679-8BC4B52D57D2}'
    $clsidPath = "Registry::HKEY_CLASSES_ROOT\CLSID\$virtualOutputClsid"
    $inprocPath = Join-Path $clsidPath 'InprocServer32'
    if ((Test-Path $clsidPath) -and (Test-Path $inprocPath)) {
        $friendly = (Get-ItemProperty $clsidPath -ErrorAction SilentlyContinue).'(default)'
        $binary = (Get-ItemProperty $inprocPath -ErrorAction SilentlyContinue).'(default)'
        $registered = -not [string]::IsNullOrWhiteSpace($binary)
        Log ("Registered CLSID: " + $virtualOutputClsid + " = " + $friendly)
        Log ("Registered binary: " + $binary)
    } else {
        Log ("Registered DirectShow filter: NOT FOUND (CLSID " + $virtualOutputClsid + ")")
    }
} catch { Log ("Registry check error: " + $_.Exception.Message) }

$ffmpeg = Join-Path $ProjectRoot 'src\BroadcastPlayout.App\native\ffmpeg\ffmpeg.exe'
Log ("FFmpeg: " + $ffmpeg + ' exists=' + (Test-Path $ffmpeg))
if (Test-Path $ffmpeg) {
    Log 'DirectShow devices (FFmpeg exits non-zero after enumeration because "dummy" is intentionally not a real input; that exit code is ignored):'
    $probe = Invoke-NativeCapture $ffmpeg @('-hide_banner','-list_devices','true','-f','dshow','-i','dummy')
    if ($probe.Text) { $probe.Text | Tee-Object -FilePath $log -Append | Write-Host }
    Log ("DirectShow enumeration exit code: " + $probe.ExitCode + ' (expected/non-fatal for list_devices)')
}

if (-not $registered) {
    Log ''
    if ($foundAx.Count -gt 0) {
        Log 'ACTION: filter binary exists but is not registered. Run tools\Register-VirtualOutput.ps1 from an elevated prompt.'
    } else {
        Log 'ACTION: build + register the virtual camera with INSTALL-VIRTUAL-OUTPUT.cmd (requires Visual Studio C++ and Microsoft DirectShow BaseClasses).'
    }
}

Log ''
Log '--- Shared memory bridge ---'
Log 'The bridge is created by Kashtrix.Playout only while Virtual Output is enabled.'
Log 'FIX48 DirectShow filter exposes Program Video + Program Audio; visibility requires KashtrixVirtualOutput.ax to be built AND registered in video/audio capture categories.'
Log ''
Log ("Log: " + $log)
Write-Host "`nDiagnostics complete. Log: $log" -ForegroundColor Green
