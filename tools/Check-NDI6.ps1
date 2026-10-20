$ErrorActionPreference='Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$local = Join-Path $projectRoot 'src\BroadcastPlayout.App\native\ndi\Processing.NDI.Lib.x64.dll'
$license = Join-Path $projectRoot 'src\BroadcastPlayout.App\native\ndi\Processing.NDI.Lib.Licenses.txt'

$dll = $local
if (-not (Test-Path $dll)) {
    $dir=$env:NDI_RUNTIME_DIR_V6
    if ([string]::IsNullOrWhiteSpace($dir)) {
        throw 'No app-local NDI runtime found and NDI_RUNTIME_DIR_V6 is not set.'
    }
    $dll=Join-Path $dir 'Processing.NDI.Lib.x64.dll'
    if (-not (Test-Path $dll)) { throw "NDI 6 runtime DLL not found: $dll" }
}

$bytes = [IO.File]::ReadAllBytes($dll)
if ($bytes.Length -lt 512 -or $bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) {
    throw 'NDI runtime is not a valid Windows PE binary.'
}
$peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
$machine = [BitConverter]::ToUInt16($bytes, $peOffset + 4)
if ($machine -ne 0x8664) {
    throw ("NDI runtime is not x64. PE machine=0x{0:X4}" -f $machine)
}

$info = [Diagnostics.FileVersionInfo]::GetVersionInfo($dll)
Write-Host 'NDI runtime check passed.' -ForegroundColor Green
Write-Host "  Path: $dll"
Write-Host '  Architecture: x64 (AMD64)'
if ($info.FileVersion) { Write-Host "  File version: $($info.FileVersion)" }
if ($info.ProductVersion) { Write-Host "  Product version: $($info.ProductVersion)" }

if ((Resolve-Path $dll).Path -eq (Resolve-Path $local -ErrorAction SilentlyContinue).Path) {
    if (-not (Test-Path $license)) {
        Write-Warning 'The app-local NDI runtime exists but the accompanying NDI license notice is missing.'
    } else {
        Write-Host '  License notice: present' -ForegroundColor Green
    }
}
