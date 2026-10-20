param(
    [string]$PreferredPath = $env:DIRECTSHOW_BASECLASSES,
    [Parameter(Mandatory=$true)][string]$MsBuildPath,
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [string]$PlatformToolset = 'v145'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Find-BaseLibrary([string]$Path, [string]$Config) {
    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path $Path)) { return $null }
    if (-not (Test-Path (Join-Path $Path 'streams.h'))) { return $null }

    $names = if ($Config -eq 'Debug') { @('strmbasd.lib','strmbase.lib') } else { @('strmbase.lib','strmbasd.lib') }
    foreach ($name in $names) {
        $preferred = Get-ChildItem $Path -Filter $name -File -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '(?i)(\\|/)(x64|amd64)(\\|/)' } |
            Select-Object -First 1
        if ($preferred) { return $preferred.FullName }

        $fallback = Get-ChildItem $Path -Filter $name -File -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '(?i)(\\|/)(win32|x86)(\\|/)' } | Select-Object -First 1
        if ($fallback) { return $fallback.FullName }
    }
    return $null
}


function Repair-BaseClassesForModernMsvc([string]$BasePath) {
    # Microsoft's Win7 DirectShow BaseClasses are the official source but contain
    # two declarations accepted by old Visual C++ compilers that modern MSVC
    # (VS 2022/2026 toolsets) rejects.  These are declaration-only syntax repairs;
    # they do not change the DirectShow ABI or behavior.
    $changes = 0

    $transip = Join-Path $BasePath 'transip.h'
    if (Test-Path $transip) {
        $text = Get-Content $transip -Raw
        $updated = $text -replace '(__out_opt\s+IMediaSample\s*\*\s*)CTransInPlaceFilter::Copy\s*\(', '${1}Copy('
        if ($updated -ne $text) {
            [System.IO.File]::WriteAllText($transip, $updated, (New-Object System.Text.UTF8Encoding($false)))
            $changes++
        }
    }

    $videoctl = Join-Path $BasePath 'videoctl.h'
    if (Test-Path $videoctl) {
        $text = Get-Content $videoctl -Raw
        $updated = $text -replace 'virtual\s+CAggDirectDraw::~CAggDirectDraw\s*\(\s*\)', 'virtual ~CAggDirectDraw()'
        if ($updated -ne $text) {
            [System.IO.File]::WriteAllText($videoctl, $updated, (New-Object System.Text.UTF8Encoding($false)))
            $changes++
        }
    }

    $marker = Join-Path $BasePath '.kashtrix-msvc-compat-v1'
    if ($changes -gt 0) {
        Write-Host "Applied $changes modern-MSVC compatibility repair(s) to DirectShow BaseClasses." -ForegroundColor Green
    }
    elseif ((Test-Path $transip) -and (Test-Path $videoctl)) {
        Write-Host 'DirectShow BaseClasses modern-MSVC compatibility is already applied.' -ForegroundColor DarkGreen
    }
    [System.IO.File]::WriteAllText($marker, "Kashtrix DirectShow BaseClasses compatibility v1`r`n", (New-Object System.Text.UTF8Encoding($false)))
}

function Write-ModernBaseClassesProject([string]$BasePath, [string]$Toolset) {
    $legacy = Join-Path $BasePath 'baseclasses.vcproj'
    if (-not (Test-Path $legacy)) {
        throw "Microsoft BaseClasses project metadata was not found: $legacy"
    }

    $legacyText = Get-Content $legacy -Raw
    $matches = [regex]::Matches($legacyText, 'RelativePath="\.\\([^"\r\n]+\.cpp)"', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    $cppFiles = @($matches | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
    if ($cppFiles.Count -lt 20) {
        throw "BaseClasses source discovery returned only $($cppFiles.Count) .cpp files. The official source layout may have changed."
    }

    $compileItems = ($cppFiles | ForEach-Object { '    <ClCompile Include="' + $_ + '" />' }) -join "`r`n"
    $projectPath = Join-Path $BasePath 'Kashtrix.BaseClasses.vcxproj'
    $xml = @"
<?xml version="1.0" encoding="utf-8"?>
<Project DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <ItemGroup Label="ProjectConfigurations">
    <ProjectConfiguration Include="Debug|x64"><Configuration>Debug</Configuration><Platform>x64</Platform></ProjectConfiguration>
    <ProjectConfiguration Include="Release|x64"><Configuration>Release</Configuration><Platform>x64</Platform></ProjectConfiguration>
  </ItemGroup>
  <PropertyGroup Label="Globals">
    <ProjectGuid>{E8A3F6FA-AE1C-4C8E-A0B6-9C8480324EAA}</ProjectGuid>
    <Keyword>Win32Proj</Keyword>
    <RootNamespace>KashtrixDirectShowBaseClasses</RootNamespace>
    <WindowsTargetPlatformVersion>10.0</WindowsTargetPlatformVersion>
  </PropertyGroup>
  <Import Project="`$(VCTargetsPath)\Microsoft.Cpp.Default.props" />
  <PropertyGroup Condition="'`$(Configuration)|`$(Platform)'=='Debug|x64'" Label="Configuration">
    <ConfigurationType>StaticLibrary</ConfigurationType>
    <UseDebugLibraries>true</UseDebugLibraries>
    <PlatformToolset>$Toolset</PlatformToolset>
    <CharacterSet>Unicode</CharacterSet>
  </PropertyGroup>
  <PropertyGroup Condition="'`$(Configuration)|`$(Platform)'=='Release|x64'" Label="Configuration">
    <ConfigurationType>StaticLibrary</ConfigurationType>
    <UseDebugLibraries>false</UseDebugLibraries>
    <PlatformToolset>$Toolset</PlatformToolset>
    <WholeProgramOptimization>true</WholeProgramOptimization>
    <CharacterSet>Unicode</CharacterSet>
  </PropertyGroup>
  <Import Project="`$(VCTargetsPath)\Microsoft.Cpp.props" />
  <PropertyGroup>
    <OutDir>`$(ProjectDir)kashtrix-build\`$(Platform)\`$(Configuration)\</OutDir>
    <IntDir>`$(ProjectDir)kashtrix-obj\`$(Platform)\`$(Configuration)\</IntDir>
  </PropertyGroup>
  <PropertyGroup Condition="'`$(Configuration)'=='Debug'"><TargetName>strmbasd</TargetName></PropertyGroup>
  <PropertyGroup Condition="'`$(Configuration)'=='Release'"><TargetName>strmbase</TargetName></PropertyGroup>
  <ItemDefinitionGroup Condition="'`$(Configuration)'=='Debug'">
    <ClCompile>
      <WarningLevel>Level3</WarningLevel>
      <PrecompiledHeader>NotUsing</PrecompiledHeader>
      <AdditionalIncludeDirectories>`$(ProjectDir);%(AdditionalIncludeDirectories)</AdditionalIncludeDirectories>
      <PreprocessorDefinitions>WIN32;_WINDOWS;_LIB;_DEBUG;DEBUG;UNICODE;_UNICODE;_WIN32_WINNT=0x0A00;%(PreprocessorDefinitions)</PreprocessorDefinitions>
      <DisableSpecificWarnings>4018;4100;4244;4267;4302;4311;4312;4996;%(DisableSpecificWarnings)</DisableSpecificWarnings>
    </ClCompile>
  </ItemDefinitionGroup>
  <ItemDefinitionGroup Condition="'`$(Configuration)'=='Release'">
    <ClCompile>
      <WarningLevel>Level3</WarningLevel>
      <PrecompiledHeader>NotUsing</PrecompiledHeader>
      <AdditionalIncludeDirectories>`$(ProjectDir);%(AdditionalIncludeDirectories)</AdditionalIncludeDirectories>
      <PreprocessorDefinitions>WIN32;_WINDOWS;_LIB;NDEBUG;UNICODE;_UNICODE;_WIN32_WINNT=0x0A00;%(PreprocessorDefinitions)</PreprocessorDefinitions>
      <DisableSpecificWarnings>4018;4100;4244;4267;4302;4311;4312;4996;%(DisableSpecificWarnings)</DisableSpecificWarnings>
    </ClCompile>
  </ItemDefinitionGroup>
  <ItemGroup>
$compileItems
  </ItemGroup>
  <Import Project="`$(VCTargetsPath)\Microsoft.Cpp.targets" />
</Project>
"@
    $utf8NoBom = New-Object System.Text.UTF8Encoding -ArgumentList $false
    [System.IO.File]::WriteAllText($projectPath, $xml, $utf8NoBom)
    return $projectPath
}

function Invoke-KashtrixDownload {
    param(
        [Parameter(Mandatory=$true)][string]$Uri,
        [Parameter(Mandatory=$true)][string]$OutFile
    )

    $outDir = Split-Path -Parent $OutFile
    if (-not [string]::IsNullOrWhiteSpace($outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
    if (Test-Path $OutFile) { Remove-Item $OutFile -Force -ErrorAction SilentlyContinue }

    $downloaded = $false
    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    if ($curl) {
        & $curl.Source -fL --retry 3 --retry-delay 2 --connect-timeout 20 --output $OutFile $Uri
        if ($LASTEXITCODE -eq 0 -and (Test-Path $OutFile) -and (Get-Item $OutFile).Length -gt 0) { $downloaded = $true }
    }

    if (-not $downloaded) {
        try {
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            Invoke-WebRequest -UseBasicParsing -Headers @{ 'User-Agent' = 'KashtrixPlayout-Setup' } -Uri $Uri -OutFile $OutFile
            if ((Test-Path $OutFile) -and (Get-Item $OutFile).Length -gt 0) { $downloaded = $true }
        }
        catch {
            Write-Host "PowerShell download fallback failed: $($_.Exception.Message)" -ForegroundColor DarkYellow
        }
    }

    if (-not $downloaded) { throw "Download failed: $Uri" }
    return $OutFile
}

function Download-OfficialBaseClasses([string]$Destination) {
    # Do NOT expand the complete Windows-classic-samples ZIP.  On Windows the
    # repository contains unrelated very-deep paths that can exceed legacy path
    # limits before we ever reach DirectShow.  FIX24 downloads the official
    # commit and asks bsdtar to extract ONLY the BaseClasses subtree.  A raw-file
    # fallback downloads only the required BaseClasses files if tar is missing.
    $sourceCommit = '434f6002bdf9cf9829406c3ff2b33387982d6168'
    $sourceRelative = 'Samples/Win7Samples/multimedia/directshow/baseclasses'
    $dependencyRoot = Join-Path $env:LOCALAPPDATA 'KashtrixPlayout\Dependencies'
    $workRoot = Join-Path $dependencyRoot ('DirectShowBootstrap-' + [Guid]::NewGuid().ToString('N'))
    $archivePath = Join-Path $workRoot 'windows-classic-samples.tar.gz'
    $stagePath = Join-Path $workRoot 'baseclasses'
    New-Item -ItemType Directory -Force -Path $stagePath | Out-Null

    try {
        Write-Host ''
        Write-Host 'DirectShow BaseClasses are not installed. Kashtrix will fetch the official Microsoft source automatically.' -ForegroundColor Yellow
        Write-Host "Source: microsoft/Windows-classic-samples @ $sourceCommit / DirectShow BaseClasses" -ForegroundColor DarkGray

        $ready = $false
        $tar = Get-Command tar.exe -ErrorAction SilentlyContinue
        if (-not $tar) { $tar = Get-Command tar -ErrorAction SilentlyContinue }

        if ($tar) {
            try {
                $archiveUrl = "https://codeload.github.com/microsoft/Windows-classic-samples/tar.gz/$sourceCommit"
                Write-Host 'Downloading Microsoft Windows classic samples archive...' -ForegroundColor Cyan
                Invoke-KashtrixDownload -Uri $archiveUrl -OutFile $archivePath | Out-Null

                Write-Host 'Extracting DirectShow BaseClasses subtree only (long-path safe)...' -ForegroundColor Cyan
                $archiveSubtree = "Windows-classic-samples-$sourceCommit/$sourceRelative"
                & $tar.Source '-xzf' $archivePath '-C' $stagePath '--strip-components=6' $archiveSubtree
                if ($LASTEXITCODE -eq 0 -and
                    (Test-Path (Join-Path $stagePath 'streams.h')) -and
                    (Test-Path (Join-Path $stagePath 'baseclasses.vcproj'))) {
                    $ready = $true
                }
                else {
                    Write-Host 'Selective archive extraction did not produce BaseClasses; switching to direct source-file bootstrap.' -ForegroundColor DarkYellow
                }
            }
            catch {
                Write-Host "Selective archive bootstrap failed: $($_.Exception.Message)" -ForegroundColor DarkYellow
                Write-Host 'Switching to direct source-file bootstrap.' -ForegroundColor DarkYellow
                $ready = $false
            }
        }
        else {
            Write-Host 'tar.exe is unavailable; using direct source-file bootstrap.' -ForegroundColor DarkYellow
        }

        if (-not $ready) {
            if (Test-Path $stagePath) { Remove-Item $stagePath -Recurse -Force -ErrorAction SilentlyContinue }
            New-Item -ItemType Directory -Force -Path $stagePath | Out-Null
            Write-Host 'Downloading only the DirectShow BaseClasses source files...' -ForegroundColor Cyan

            $sourceFiles = @(
                'amextra.cpp','amfilter.cpp','amvideo.cpp','arithutil.cpp','combase.cpp','cprop.cpp','ctlutil.cpp','ddmm.cpp',
                'dllentry.cpp','dllsetup.cpp','mtype.cpp','outputq.cpp','perflog.cpp','pstream.cpp','pullpin.cpp','refclock.cpp',
                'renbase.cpp','schedule.cpp','seekpt.cpp','source.cpp','strmctl.cpp','sysclock.cpp','transfrm.cpp','transip.cpp',
                'videoctl.cpp','vtrans.cpp','winctrl.cpp','winutil.cpp','wxdebug.cpp','wxlist.cpp','wxutil.cpp',
                'amextra.h','amfilter.h','cache.h','combase.h','cprop.h','ctlutil.h','ddmm.h','dllsetup.h','dxmperf.h','fourcc.h',
                'measure.h','msgthrd.h','mtype.h','outputq.h','perflog.h','perfstruct.h','pstream.h','pullpin.h','refclock.h',
                'reftime.h','renbase.h','schedule.h','seekpt.h','source.h','streams.h','strmctl.h','sysclock.h','transfrm.h',
                'transip.h','videoctl.h','vtrans.h','winctrl.h','winutil.h','wxdebug.h','wxlist.h','wxutil.h','baseclasses.vcproj'
            )
            foreach ($name in $sourceFiles) {
                $rawUrl = "https://raw.githubusercontent.com/microsoft/Windows-classic-samples/$sourceCommit/$sourceRelative/$name"
                Invoke-KashtrixDownload -Uri $rawUrl -OutFile (Join-Path $stagePath $name) | Out-Null
            }
            $ready = (Test-Path (Join-Path $stagePath 'streams.h')) -and (Test-Path (Join-Path $stagePath 'baseclasses.vcproj'))
        }

        $cppCount = @(Get-ChildItem $stagePath -Filter *.cpp -File -ErrorAction SilentlyContinue).Count
        if (-not $ready -or $cppCount -lt 20) {
            throw "Microsoft BaseClasses bootstrap is incomplete ($cppCount C++ source files found). Check internet/proxy access, or set DIRECTSHOW_BASECLASSES to a local BaseClasses folder."
        }

        # Preserve the upstream license when network access permits it.  License
        # retrieval is not allowed to break an otherwise valid build bootstrap.
        try {
            $licenseUrl = "https://raw.githubusercontent.com/microsoft/Windows-classic-samples/$sourceCommit/LICENSE"
            Invoke-KashtrixDownload -Uri $licenseUrl -OutFile (Join-Path $stagePath 'MICROSOFT-WINDOWS-CLASSIC-SAMPLES-LICENSE.txt') | Out-Null
        }
        catch {
            Write-Host 'License file download was skipped; source attribution remains in THIRD-PARTY.md.' -ForegroundColor DarkYellow
        }

        $parent = Split-Path -Parent $Destination
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
        if (Test-Path $Destination) { Remove-Item $Destination -Recurse -Force -ErrorAction Stop }
        Move-Item $stagePath $Destination -Force

        Write-Host "Microsoft DirectShow BaseClasses cached: $Destination" -ForegroundColor Green
    }
    finally {
        Remove-Item $workRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$root = Split-Path -Parent $PSScriptRoot
$cache = Join-Path $env:LOCALAPPDATA 'KashtrixPlayout\Dependencies\DirectShowBaseClasses'
$candidates = @()
if (-not [string]::IsNullOrWhiteSpace($PreferredPath)) { $candidates += $PreferredPath }
$candidates += @(
    $cache,
    (Join-Path $root 'vendor\directshow\baseclasses')
)

$basePath = $null
$baseLib = $null
foreach ($candidate in $candidates | Select-Object -Unique) {
    if (-not (Test-Path $candidate)) { continue }
    if (Test-Path (Join-Path $candidate 'streams.h')) {
        $resolvedCandidate = (Resolve-Path $candidate).Path
        $candidateLib = Find-BaseLibrary $resolvedCandidate $Configuration
        if ($candidateLib) {
            $basePath = $resolvedCandidate
            $baseLib = $candidateLib
            break
        }

        # A previous interrupted bootstrap can leave a streams.h behind without
        # the complete source tree.  Never trust that partial cache: only reuse
        # source if the legacy project metadata and a healthy source set exist.
        $candidateCppCount = @(Get-ChildItem $resolvedCandidate -Filter *.cpp -File -ErrorAction SilentlyContinue).Count
        if ((Test-Path (Join-Path $resolvedCandidate 'baseclasses.vcproj')) -and $candidateCppCount -ge 20) {
            $basePath = $resolvedCandidate
            break
        }
    }
}

if (-not $basePath) {
    Download-OfficialBaseClasses $cache
    $basePath = (Resolve-Path $cache).Path
}

Repair-BaseClassesForModernMsvc $basePath

if (-not $baseLib) {
    Write-Host "Building Microsoft DirectShow BaseClasses ($Configuration x64)..." -ForegroundColor Cyan
    $baseProject = Write-ModernBaseClassesProject $basePath $PlatformToolset
    & $MsBuildPath $baseProject /m /t:Build "/p:Configuration=$Configuration" /p:Platform=x64 | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw "DirectShow BaseClasses x64 build failed with exit code $LASTEXITCODE" }
    $baseLib = Find-BaseLibrary $basePath $Configuration
}

if (-not $baseLib) { throw "DirectShow BaseClasses built but strmbase.lib was not found under $basePath" }

Write-Host "BaseClasses: $basePath" -ForegroundColor DarkGray
Write-Host "Base library: $baseLib" -ForegroundColor Green
[PSCustomObject]@{ Path = $basePath; Library = $baseLib }
