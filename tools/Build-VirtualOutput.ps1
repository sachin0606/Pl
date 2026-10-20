param(
    [string]$BaseClassesPath = $env:DIRECTSHOW_BASECLASSES,
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'native\Kashtrix.VirtualOutput\Kashtrix.VirtualOutput.vcxproj'
if (-not (Test-Path $project)) { throw "Virtual output project not found: $project" }

$msbuild = Get-Command msbuild.exe -ErrorAction SilentlyContinue
if (-not $msbuild) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $path = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
        if (-not $path) { $path = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1 }
        if ($path) { $msbuild = Get-Item $path }
    }
}
if (-not $msbuild) { throw 'MSBuild/C++ tools were not found. Install the Visual Studio Desktop development with C++ workload (x64 tools + Windows SDK).' }
$msbuildPath = if ($msbuild.PSObject.Properties.Name -contains 'Source' -and $msbuild.Source) { $msbuild.Source } else { $msbuild.FullName }

$projectText = Get-Content $project -Raw
$toolsetMatch = [regex]::Match($projectText, '<PlatformToolset>([^<]+)</PlatformToolset>')
$platformToolset = if ($toolsetMatch.Success) { $toolsetMatch.Groups[1].Value } else { 'v145' }

$resolved = & (Join-Path $PSScriptRoot 'Ensure-DirectShowBaseClasses.ps1') `
    -PreferredPath $BaseClassesPath `
    -MsBuildPath $msbuildPath `
    -Configuration $Configuration `
    -PlatformToolset $platformToolset
$BaseClassesPath = $resolved.Path
$baseLib = $resolved.Library

if (-not (Test-Path (Join-Path $BaseClassesPath 'streams.h'))) { throw "DirectShow BaseClasses streams.h was not found: $BaseClassesPath" }
if (-not (Test-Path $baseLib)) { throw "DirectShow BaseClasses library was not found: $baseLib" }

Write-Host "Building Kashtrix Virtual Output ($Configuration x64, isolated short-path rebuild)..." -ForegroundColor Cyan

# FIX29: VC/MSBuild tracking files can fail on long extracted project paths even after
# link.exe has successfully generated code (MSB6003 / missing *.tlog). Keep every
# intermediate/output tracking path under LOCALAPPDATA instead of the downloaded tree.
$nativeDir = Split-Path -Parent $project
$staleObj = Join-Path $nativeDir 'obj'
$staleOut = Join-Path $root 'build\VirtualOutput'
if (Test-Path $staleObj) { Remove-Item $staleObj -Recurse -Force -ErrorAction SilentlyContinue }
if (Test-Path $staleOut) { Remove-Item $staleOut -Recurse -Force -ErrorAction SilentlyContinue }

$cacheBase = if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) { Join-Path $env:TEMP 'KashtrixPlayout' } else { Join-Path $env:LOCALAPPDATA 'KashtrixPlayout' }
$cacheRoot = Join-Path $cacheBase ("NativeBuild\VirtualOutput\" + $Configuration)
$intDir = (Join-Path $cacheRoot 'obj') + [IO.Path]::DirectorySeparatorChar
$outDir = (Join-Path $cacheRoot 'out') + [IO.Path]::DirectorySeparatorChar
if (Test-Path $cacheRoot) { Remove-Item $cacheRoot -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force -Path $intDir,$outDir | Out-Null

# Single-node C++ build + disabled file-access tracking avoids the tracked-output race
# seen in VS 18 where the linker completes and MSBuild then tries to enumerate a tlog
# directory that has already disappeared. Rebuild remains deterministic because the
# isolated cache is deleted immediately above.
$buildArgs = @(
    $project,
    '/m:1',
    '/t:Rebuild',
    "/p:Configuration=$Configuration",
    '/p:Platform=x64',
    "/p:BaseClassesPath=$BaseClassesPath",
    "/p:BaseClassesLib=$baseLib",
    "/p:IntDir=$intDir",
    "/p:OutDir=$outDir",
    '/p:TrackFileAccess=false',
    '/p:UseMultiToolTask=false'
)
& $msbuildPath @buildArgs
if ($LASTEXITCODE -ne 0) { throw "Virtual output build failed with exit code $LASTEXITCODE" }

$out = Join-Path $outDir 'KashtrixVirtualOutput.ax'
if (-not (Test-Path $out)) {
    $candidate = Get-ChildItem $outDir -Filter 'KashtrixVirtualOutput.ax' -File -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($candidate) { $out = $candidate.FullName }
}
if (-not (Test-Path $out)) { throw "Build completed but .ax was not found in the isolated output directory: $outDir" }

# Keep the traditional project build copy for diagnostics, but registration uses the
# stable LOCALAPPDATA installed copy so project-path length never affects DirectShow.
$projectOutDir = Join-Path $root ("build\VirtualOutput\" + $Configuration)
New-Item -ItemType Directory -Force -Path $projectOutDir | Out-Null
Copy-Item $out (Join-Path $projectOutDir 'KashtrixVirtualOutput.ax') -Force

$install = Join-Path $env:LOCALAPPDATA 'KashtrixPlayout\VirtualOutput'
New-Item -ItemType Directory -Force -Path $install | Out-Null
Copy-Item $out (Join-Path $install 'KashtrixVirtualOutput.ax') -Force
Write-Host "Virtual output built: $out" -ForegroundColor Green
Write-Host "Diagnostic copy: $projectOutDir\KashtrixVirtualOutput.ax" -ForegroundColor DarkGreen
Write-Host "Installed copy: $install\KashtrixVirtualOutput.ax" -ForegroundColor Green
