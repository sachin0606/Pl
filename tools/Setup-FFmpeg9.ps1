param([string]$ProjectRoot = (Resolve-Path "$PSScriptRoot\.."))
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# FFmpeg 9.x x64 LGPL shared build from BtbN. The FFmpeg 9 ABI used by
# FFmpeg.AutoGen 9.0.1.1 is avcodec 63 / avformat 63 / avutil 61 /
# swscale 10 / swresample 7.
$url = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n9.0-latest-win64-lgpl-shared-9.0.zip'
$tempRoot = Join-Path $env:TEMP ('BroadcastPlayout-FFmpeg9-' + [Guid]::NewGuid().ToString('N'))
$zip = Join-Path $tempRoot 'ffmpeg9-shared.zip'
$extract = Join-Path $tempRoot 'extract'
$dest = Join-Path $ProjectRoot 'src\BroadcastPlayout.App\native\ffmpeg'

function Download-File([string]$Uri, [string]$OutFile) {
    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    if ($curl) {
        Write-Host 'Using Windows curl.exe downloader...' -ForegroundColor DarkGray
        & $curl.Source -L --fail --retry 3 --connect-timeout 30 -o $OutFile $Uri
        if ($LASTEXITCODE -ne 0) { throw "curl.exe failed with exit code $LASTEXITCODE" }
        return
    }

    Write-Host 'Using PowerShell downloader...' -ForegroundColor DarkGray
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $Uri -OutFile $OutFile -UseBasicParsing
}

try {
    New-Item $tempRoot -ItemType Directory -Force | Out-Null
    New-Item $extract -ItemType Directory -Force | Out-Null
    New-Item $dest -ItemType Directory -Force | Out-Null

    Write-Host 'Downloading FFmpeg 9.x x64 shared build...' -ForegroundColor Cyan
    Download-File $url $zip
    if (-not (Test-Path $zip) -or (Get-Item $zip).Length -lt 1000000) {
        throw 'FFmpeg archive download is missing or unexpectedly small.'
    }

    Write-Host 'Extracting FFmpeg...' -ForegroundColor Cyan
    Expand-Archive -Path $zip -DestinationPath $extract -Force

    $codec = Get-ChildItem $extract -Filter 'avcodec-63.dll' -Recurse -File | Select-Object -First 1
    if (-not $codec) {
        throw 'Downloaded archive does not contain avcodec-63.dll. This application requires the FFmpeg 9 ABI.'
    }

    $bin = $codec.Directory.FullName
    Remove-Item (Join-Path $dest '*') -Recurse -Force -ErrorAction SilentlyContinue
    Copy-Item (Join-Path $bin '*.dll') $dest -Force

    foreach ($exeName in @('ffmpeg.exe','ffprobe.exe')) {
        $exe = Get-ChildItem $extract -Filter $exeName -Recurse -File | Select-Object -First 1
        if ($exe) { Copy-Item $exe.FullName $dest -Force }
    }

    $required = @('avcodec-63.dll','avformat-63.dll','avutil-61.dll','swscale-10.dll','swresample-7.dll')
    $missing = $required | Where-Object { -not (Test-Path (Join-Path $dest $_)) }
    if ($missing) { throw "Missing required DLLs after extraction: $($missing -join ', ')" }

    # Validate that the downloaded Windows build actually exposes the live-capture
    # inputs used by Kashtrix (capture cards/webcams and desktop capture).
    $ffmpegExe = Join-Path $dest 'ffmpeg.exe'
    if (Test-Path $ffmpegExe) {
        $deviceOutput = (& $ffmpegExe -hide_banner -devices 2>&1 | Out-String)
        if ($deviceOutput -notmatch '(?m)^\s*D\S*\s+dshow\s') {
            throw 'Downloaded media runtime does not expose the Windows capture-device module (dshow).'
        }
        if ($deviceOutput -notmatch '(?m)^\s*D\S*\s+gdigrab\s') {
            throw 'Downloaded media runtime does not expose the Windows desktop-capture module (gdigrab).'
        }
        Write-Host 'Live capture modules verified (device + desktop).' -ForegroundColor Green

        $encoderOutput = (& $ffmpegExe -hide_banner -encoders 2>&1 | Out-String)
        $h264Candidates = @('libx264','h264_mf','libopenh264','h264_qsv','h264_amf','h264_nvenc')
        $h264Encoder = $h264Candidates | Where-Object { $encoderOutput -match ('(?m)^\s*V\S*\s+' + [regex]::Escape($_) + '\s') } | Select-Object -First 1
        if (-not $h264Encoder) {
            throw 'Downloaded media runtime does not expose a usable H.264 recording encoder.'
        }
        Write-Host 'H.264 recording encoder verified.' -ForegroundColor Green

        $h265Candidates = @('libx265','hevc_mf','hevc_qsv','hevc_amf','hevc_nvenc')
        $h265Encoder = $h265Candidates | Where-Object { $encoderOutput -match ('(?m)^\s*V\S*\s+' + [regex]::Escape($_) + '\s') } | Select-Object -First 1
        if ($h265Encoder) {
            Write-Host 'H.265 recording encoder verified.' -ForegroundColor Green
        } else {
            Write-Host 'H.265 encoder is not available in this runtime; H.264 and mezzanine recording remain available.' -ForegroundColor Yellow
        }
    }

    Write-Host ''
    Write-Host 'FFmpeg runtime is ready:' -ForegroundColor Green
    Write-Host $dest -ForegroundColor Green
    Get-ChildItem $dest -Filter '*.dll' | Sort-Object Name | Select-Object Name, Length | Format-Table -AutoSize
}
finally {
    Remove-Item $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
