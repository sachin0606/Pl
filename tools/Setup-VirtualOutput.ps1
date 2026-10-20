param(
    [string]$ProjectRoot = (Resolve-Path "$PSScriptRoot\.."),
    [string]$BaseClassesPath = $env:DIRECTSHOW_BASECLASSES,
    [switch]$RegisterOnly,
    [switch]$ForceRebuild
)
$ErrorActionPreference='Stop'
Set-Location $ProjectRoot
Write-Host 'KASHTRIX VIRTUAL OUTPUT SETUP' -ForegroundColor Cyan
Write-Host 'This installs the optional x64 DirectShow source named Kashtrix Playout Virtual Output.' -ForegroundColor DarkGray

$installed = Join-Path $env:LOCALAPPDATA 'KashtrixPlayout\VirtualOutput\KashtrixVirtualOutput.ax'
if ($ForceRebuild -and (Test-Path $installed)) {
    Write-Host 'Removing stale Virtual Output registration/build...' -ForegroundColor DarkGray
    try {
        $u = Start-Process "$env:SystemRoot\System32\regsvr32.exe" -ArgumentList @('/s','/u',('"'+$installed+'"')) -Verb RunAs -Wait -PassThru
        if ($u.ExitCode -ne 0) { Write-Host "Previous filter unregister returned $($u.ExitCode); continuing with clean rebuild." -ForegroundColor DarkYellow }
    } catch { Write-Host 'Previous filter could not be unregistered; clean rebuild will continue.' -ForegroundColor DarkYellow }
    Remove-Item $installed -Force -ErrorAction SilentlyContinue
}

if (-not $RegisterOnly -and -not (Test-Path $installed)) {
    if ([string]::IsNullOrWhiteSpace($BaseClassesPath)) {
        Write-Host ''
        Write-Host 'DirectShow BaseClasses are not configured. Kashtrix will download the official Microsoft source and build the x64 library automatically.' -ForegroundColor Yellow
        Write-Host 'No DIRECTSHOW_BASECLASSES environment variable is required for the normal setup path.' -ForegroundColor DarkGray
    }
    & (Join-Path $PSScriptRoot 'Build-VirtualOutput.ps1') -BaseClassesPath $BaseClassesPath -Configuration Release
}

if (-not (Test-Path $installed)) { throw "Virtual output filter was not produced: $installed" }
Write-Host "Filter: $installed" -ForegroundColor Green
Write-Host 'Windows will now ask for elevation to register the DirectShow filter.' -ForegroundColor Cyan
# Register-VirtualOutput.ps1 invokes elevated regsvr32 via Start-Process and throws on a
# non-zero native exit code.  A successful *PowerShell script* invocation does not reliably
# set $LASTEXITCODE (it can remain $null or contain an unrelated earlier native exit code),
# so never use it as the registration result.  If this call returns, regsvr32 succeeded; the
# CLSID + InprocServer32 checks below are the authoritative registration verification.
try {
    & (Join-Path $PSScriptRoot 'Register-VirtualOutput.ps1') -NoBuild
} catch {
    throw ("Virtual Output registration failed: " + $_.Exception.Message)
}

# Verify the exact CLSID registration after the elevated regsvr32 returns. This is
# faster and more deterministic than scanning every CLSID and gives INSTALL a real
# success/failure result before diagnostics run.
$clsid = '{C7453623-A2E2-49E0-B679-8BC4B52D57D2}'
$clsidPath = "Registry::HKEY_CLASSES_ROOT\CLSID\$clsid"
$inprocPath = Join-Path $clsidPath 'InprocServer32'
if (-not (Test-Path $clsidPath) -or -not (Test-Path $inprocPath)) {
    throw "Virtual Output registration did not create the expected DirectShow CLSID $clsid. Re-run INSTALL-VIRTUAL-OUTPUT.cmd and approve the UAC prompt."
}
$registeredBinary = (Get-ItemProperty $inprocPath -ErrorAction Stop).'(default)'
if ([string]::IsNullOrWhiteSpace($registeredBinary) -or -not (Test-Path $registeredBinary)) {
    throw "Virtual Output CLSID exists but its registered binary is missing: $registeredBinary"
}
Write-Host "Verified DirectShow CLSID: $clsid" -ForegroundColor Green
Write-Host "Registered binary: $registeredBinary" -ForegroundColor DarkGreen

# Verify that a fresh DirectShow client can actually enumerate the capture-category name.
# This catches cases where regsvr32 succeeds but the filter was not inserted into VideoInputDeviceCategory.
$ffmpeg = Join-Path $ProjectRoot 'src\BroadcastPlayout.App\native\ffmpeg\ffmpeg.exe'
if (Test-Path $ffmpeg) {
    Write-Host 'Verifying DirectShow device enumeration...' -ForegroundColor Cyan
    # ffmpeg intentionally writes DirectShow device enumeration to STDERR and exits non-zero
    # because "dummy" is not a real capture source. With $ErrorActionPreference='Stop',
    # invoking it directly makes PowerShell 5.1 throw NativeCommandError before we can parse
    # the device list. Redirect through Start-Process so the expected non-zero exit is benign.
    $verifyRoot = Join-Path $env:TEMP ("Kashtrix-DShow-Verify-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $verifyRoot | Out-Null
    $stdout = Join-Path $verifyRoot 'stdout.txt'
    $stderr = Join-Path $verifyRoot 'stderr.txt'
    try {
        $p = Start-Process -FilePath $ffmpeg -ArgumentList @('-hide_banner','-list_devices','true','-f','dshow','-i','dummy') -NoNewWindow -Wait -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
        $deviceText = ((Get-Content $stdout -Raw -ErrorAction SilentlyContinue) + [Environment]::NewLine + (Get-Content $stderr -Raw -ErrorAction SilentlyContinue))
        Write-Host "DirectShow enumeration exit code: $($p.ExitCode) (expected/non-fatal for list_devices)" -ForegroundColor DarkGray
        if ($deviceText -notmatch [regex]::Escape('Kashtrix Playout Virtual Output')) {
            throw 'The CLSID registered, but Windows DirectShow did not enumerate "Kashtrix Playout Virtual Output". Restart the receiving application and rerun INSTALL-VIRTUAL-OUTPUT.cmd. If it still fails after a Windows restart, the registration/category entry is incomplete.'
        }
        Write-Host 'DirectShow enumeration: Kashtrix Playout Virtual Output FOUND.' -ForegroundColor Green
    } finally {
        Remove-Item $verifyRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ''
Write-Host 'Re-running output diagnostics...' -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'Debug-Outputs.ps1') -ProjectRoot $ProjectRoot
