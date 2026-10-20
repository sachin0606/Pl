<#
.SYNOPSIS
  Builds the Kashtrix Playout installer (Kashtrix-Playout-Setup-<version>.exe).

.DESCRIPTION
  1. Publishes all 17 apps (Release, self-contained win-x64) into ONE folder, so the target PC needs
     no .NET install and every app finds the others beside Playout.
  2. Adds the CG demo designs (cg-demo) and, when present, the broadcast demo packages
     (cgdemo-templates, optional component).
  3. Bundles the Visual C++ 2015-2022 runtime (from Visual Studio, else downloaded); the installer
     runs it only when the PC does not have it.
  4. Compiles installer\KashtrixSetup.iss with Inno Setup 6.

  Windows PowerShell 5.1 compatible. Work files go to %LOCALAPPDATA%\KashtrixPlayout\Installer.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File installer\Build-Installer.ps1
  powershell -ExecutionPolicy Bypass -File installer\Build-Installer.ps1 -NoDemos -CopyToDesktop
#>
param(
    [string]$Version = "",
    [switch]$NoDemos,
    [switch]$SkipPublish,
    [switch]$CopyToDesktop
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$work = Join-Path $env:LOCALAPPDATA "KashtrixPlayout\Installer"
$stage = Join-Path $work "stage"
$output = Join-Path $work "output"
New-Item -ItemType Directory -Force -Path $work, $output | Out-Null

function Step([string]$text) { Write-Host ""; Write-Host "=== $text" -ForegroundColor Cyan }

function Find-Git {
    $cmd = Get-Command git -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $vs = Get-ChildItem "$env:ProgramFiles\Microsoft Visual Studio\*\*\Common7\IDE\CommonExtensions\Microsoft\TeamFoundation\Team Explorer\Git\cmd\git.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($vs) { return $vs.FullName }
    return $null
}

# ---- Version ------------------------------------------------------------------------------------
$git = Find-Git
$commit = "local"
if ($git) {
    $hash = & $git -C $repo rev-parse --short HEAD 2>$null
    if ($LASTEXITCODE -eq 0 -and $hash) { $commit = $hash.Trim() }
    if (-not $Version) {
        $count = & $git -C $repo rev-list --count HEAD 2>$null
        if ($LASTEXITCODE -eq 0 -and $count) { $Version = "1.0." + $count.Trim() }
    }
}
if (-not $Version) { $Version = "1.0.0" }
Write-Host "Kashtrix Playout installer $Version ($commit)"

# ---- 1. Publish every app into one self-contained folder ------------------------------------
if (-not $SkipPublish) {
    Step "Publishing the suite (Release, self-contained win-x64)"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    # Satellite apps first, Playout last, so Playout's own files (exe, config) are the final copy.
    $projects = @(
        "Kashtrix.CGEditor", "Kashtrix.Scheduler", "Kashtrix.PlaylistEditor", "Kashtrix.Settings", "Kashtrix.Multiview",
        "Kashtrix.ChannelController", "Kashtrix.QCController", "Kashtrix.FileManager", "Kashtrix.CGController",
        "Kashtrix.IngestServer", "Kashtrix.MAM", "Kashtrix.NRCS", "Kashtrix.Prompter", "Kashtrix.HAController",
        "Kashtrix.ApiGateway", "Kashtrix.OutputEngine", "BroadcastPlayout.App")
    foreach ($name in $projects) {
        $proj = Join-Path $repo "src\$name\$name.csproj"
        Write-Host "  publish $name"
        & dotnet publish $proj -c Release -r win-x64 --self-contained true -p:Platform=x64 -o $stage -nologo -v q
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $name (exit $LASTEXITCODE)" }
    }
    foreach ($exe in @("Kashtrix.Playout.exe", "Kashtrix.CGEditor.exe", "Kashtrix.Multiview.exe", "Kashtrix.OutputEngine.exe")) {
        if (-not (Test-Path (Join-Path $stage $exe))) { throw "Published suite is missing $exe" }
    }
    # CG demo designs (.kcg) ship with the suite; they are small.
    $cgDemo = Join-Path $repo "cg-demo"
    if (Test-Path $cgDemo) { Copy-Item $cgDemo -Destination (Join-Path $stage "cg-demo") -Recurse -Force }
    # Debug symbols are not needed on air PCs.
    Get-ChildItem $stage -Recurse -Filter *.pdb | Remove-Item -Force
}
if (-not (Test-Path (Join-Path $stage "Kashtrix.Playout.exe"))) { throw "No published suite in $stage (run without -SkipPublish)" }
$stageInfo = Get-ChildItem $stage -Recurse -File | Measure-Object Length -Sum
Write-Host ("  suite: {0:N0} files, {1:N0} MB" -f $stageInfo.Count, ($stageInfo.Sum / 1MB))

# ---- 2. Visual C++ runtime ------------------------------------------------------------------
Step "Visual C++ 2015-2022 runtime"
$vcRedist = Get-ChildItem "$env:ProgramFiles\Microsoft Visual Studio\*\*\VC\Redist\MSVC\*\vc_redist.x64.exe" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $vcRedist) {
    $vcRedist = Join-Path $work "vc_redist.x64.exe"
    if (-not (Test-Path $vcRedist)) {
        Write-Host "  downloading from Microsoft"
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri "https://aka.ms/vs/17/release/vc_redist.x64.exe" -OutFile $vcRedist -UseBasicParsing
    }
}
Write-Host "  $vcRedist"

# ---- 3. Inno Setup ----------------------------------------------------------------------------
Step "Compiling the installer"
$iscc = @(
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 was not found. Install it from https://jrsoftware.org/isdl.php (winget install JRSoftware.InnoSetup)." }

$defines = @("/DAppVersion=$Version", "/DCommit=$commit", "/DStageDir=$stage", "/DVcRedist=$vcRedist", "/DOutputDir=$output")
$demos = Join-Path $repo "cgdemo-templates"
if (-not $NoDemos -and (Test-Path $demos)) {
    $defines += "/DDemoDir=$demos"
    Write-Host "  including optional demo CG packages from $demos"
}
& $iscc /Q @defines (Join-Path $PSScriptRoot "KashtrixSetup.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed (exit $LASTEXITCODE)" }

$setup = Join-Path $output "Kashtrix-Playout-Setup-$Version.exe"
if (-not (Test-Path $setup)) { throw "Installer was not produced: $setup" }
Write-Host ""
Write-Host ("INSTALLER READY: {0} ({1:N0} MB)" -f $setup, ((Get-Item $setup).Length / 1MB)) -ForegroundColor Green
if ($CopyToDesktop) {
    $desktop = [Environment]::GetFolderPath("Desktop")
    Copy-Item $setup -Destination $desktop -Force
    Write-Host "Copied to $desktop"
}
