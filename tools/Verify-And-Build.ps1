param(
    [string]$ProjectRoot = (Resolve-Path "$PSScriptRoot\.."),
    [ValidateSet('Debug','Release')][string]$Configuration = 'Debug'
)
$ErrorActionPreference = 'Stop'

Write-Host 'Checking PowerShell 5.1 script syntax...' -ForegroundColor Cyan
$syntaxFailures = @()
$diagnosticNames = @('Live-Debug.ps1','Debug-Outputs.ps1','Test-NDI-Sender.ps1','Test-Chromium-CG.ps1')
Get-ChildItem -Path $PSScriptRoot -Filter '*.ps1' -File | Where-Object { $diagnosticNames -notcontains $_.Name } | ForEach-Object {
    $tokens = $null
    $parseErrors = $null
    [System.Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$tokens, [ref]$parseErrors) | Out-Null
    if ($parseErrors -and $parseErrors.Count -gt 0) {
        $syntaxFailures += ($_.Name + ': ' + (($parseErrors | ForEach-Object { $_.Message }) -join ' | '))
    }
}
if ($syntaxFailures.Count -gt 0) {
    throw ('PowerShell syntax guard failed: ' + ($syntaxFailures -join '; '))
}
Write-Host '  Setup/build tool scripts parse cleanly with the Windows PowerShell parser.' -ForegroundColor Green


# FIX31 direct-run safety: Setup-All normally creates the session root in Clean-BuildCache.ps1,
# but Verify-And-Build.ps1 can also be launched directly by an operator or CI job.
if ([string]::IsNullOrWhiteSpace($env:KASHTRIX_BUILD_ROOT)) {
    $sessionsRoot = Join-Path $env:LOCALAPPDATA 'KashtrixPlayout\BuildSessions'
    New-Item -ItemType Directory -Force -Path $sessionsRoot | Out-Null
    $env:KASHTRIX_BUILD_ROOT = Join-Path $sessionsRoot 'active'
    New-Item -ItemType Directory -Force -Path $env:KASHTRIX_BUILD_ROOT | Out-Null
    Write-Host ('  Using single active build root: ' + $env:KASHTRIX_BUILD_ROOT) -ForegroundColor DarkGreen
} else {
    New-Item -ItemType Directory -Force -Path $env:KASHTRIX_BUILD_ROOT | Out-Null
    Write-Host ('  Using active build root: ' + $env:KASHTRIX_BUILD_ROOT) -ForegroundColor DarkGreen
}

$activeMarker = Join-Path (Join-Path $env:LOCALAPPDATA 'KashtrixPlayout') 'active-build-root.txt'
[System.IO.File]::WriteAllText($activeMarker, $env:KASHTRIX_BUILD_ROOT, (New-Object System.Text.UTF8Encoding($false)))


$project = Join-Path $ProjectRoot 'src\BroadcastPlayout.App\BroadcastPlayout.App.csproj'
$solution = Join-Path $ProjectRoot 'BroadcastPlayout.sln'
$ffmpeg = Join-Path $ProjectRoot 'src\BroadcastPlayout.App\native\ffmpeg'
$required = @('avcodec-63.dll','avformat-63.dll','avutil-61.dll','swscale-10.dll','swresample-7.dll')

$ndiLocal = Join-Path $ProjectRoot 'src\BroadcastPlayout.App\native\ndi\Processing.NDI.Lib.x64.dll'
$ndiSource = Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Outputs\NdiSender.cs'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET SDK was not found. Install the .NET 10 SDK / .NET desktop development workload in Visual Studio 2026.'
}

Write-Host 'Checking .NET SDK...' -ForegroundColor Cyan
$sdks = & dotnet --list-sdks
$net10 = $sdks | Where-Object { $_ -match '^10\.' }
if (-not $net10) { throw '.NET 10 SDK is not installed.' }
$net10 | Select-Object -Last 1 | ForEach-Object { Write-Host "  $_" -ForegroundColor Green }

Write-Host 'Checking FFmpeg native runtime...' -ForegroundColor Cyan
$missing = $required | Where-Object { -not (Test-Path (Join-Path $ffmpeg $_)) }
if ($missing) {
    throw "Missing FFmpeg DLLs: $($missing -join ', '). Run .\tools\Setup-FFmpeg9.ps1 first."
}
Write-Host '  FFmpeg 9 ABI files found.' -ForegroundColor Green

Write-Host 'Checking Windows live-input modules...' -ForegroundColor Cyan
$mediaExe = Join-Path $ffmpeg 'ffmpeg.exe'
if (Test-Path $mediaExe) {
    $deviceOutput = (& $mediaExe -hide_banner -devices 2>&1 | Out-String)
    if ($deviceOutput -notmatch '(?m)^\s*D\S*\s+dshow\s') { throw 'Live-input guard failed: Windows capture-device module (dshow) is unavailable.' }
    if ($deviceOutput -notmatch '(?m)^\s*D\S*\s+gdigrab\s') { throw 'Live-input guard failed: Windows desktop-capture module (gdigrab) is unavailable.' }
    Write-Host '  Device/card/webcam and desktop capture modules are available.' -ForegroundColor Green

    $encoderOutput = (& $mediaExe -hide_banner -encoders 2>&1 | Out-String)
    $h264Candidates = @('libx264','h264_mf','libopenh264','h264_qsv','h264_amf','h264_nvenc')
    $hasH264 = $false
    foreach ($candidate in $h264Candidates) {
        if ($encoderOutput -match ('(?m)^\s*V\S*\s+' + [regex]::Escape($candidate) + '\s')) { $hasH264 = $true; break }
    }
    if (-not $hasH264) { throw 'Ingest encoder guard failed: no supported H.264 encoder is available in the installed media runtime.' }
    Write-Host '  H.264 ingest encoder availability passed.' -ForegroundColor Green
} else {
    Write-Host '  Runtime executable not present; module/encoder probe skipped.' -ForegroundColor Yellow
}

Write-Host 'Checking FIX20 UI / ingest / NDI recovery guards...' -ForegroundColor Cyan
$mainUiText = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\MainWindow.xaml') -Raw
$ingestText = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\IngestServerWindow.xaml.cs') -Raw
$ndiSenderText = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Outputs\NdiSender.cs') -Raw
$setupAllText = Get-Content (Join-Path $ProjectRoot 'tools\Setup-All.ps1') -Raw
if ($mainUiText -notmatch 'Text="IN"' -or $mainUiText -notmatch 'Text="STOP"' -or $mainUiText -notmatch 'PlayPauseStateText' -or $mainUiText -notmatch 'Text="TAKE"') { throw 'FIX20 transport guard failed: labeled operator controls are incomplete.' }
# FIX48W: the duplicate bottom Program Audio rail was intentionally removed in FIX48V.
# Validate the single main Program meter/fader semantically instead of depending on obsolete
# legacy rail widths or the removed "Program PFL ..." accessibility strings.
$singleProgramAudioMeters = ($mainUiText -match 'BroadcastFaderVertical' -and
                             $mainUiText -match 'MasterOutputVolumePercent' -and
                             $mainUiText -match 'AudioLeftLevel' -and
                             $mainUiText -match 'AudioRightLevel' -and
                             $mainUiText -match 'AudioMasterLevel' -and
                             $mainUiText -match 'AudioLeftDb' -and
                             $mainUiText -match 'AudioRightDb' -and
                             $mainUiText -match 'AudioMasterDb' -and
                             $mainUiText -match 'Text="L"' -and
                             $mainUiText -match 'Text="R"' -and
                             $mainUiText -match 'Text="M"')
if (-not $singleProgramAudioMeters) {
    throw 'FIX20 audio-meter guard failed: readable single Program L/R/M meter/fader layout is missing.'
}
if ($ingestText -notmatch 'ResolveVideoEncoder' -or $ingestText -notmatch 'h264_mf' -or $ingestText -notmatch 'GetEncoderList') { throw 'FIX20 ingest guard failed: adaptive recording encoder resolution is missing.' }
if ($ndiSenderText -notmatch 'NDIlib_send_create_default' -or $ndiSenderText -notmatch 'create.clock_video = 1' -or $ndiSenderText -notmatch 'create.clock_audio = 1') { throw 'FIX20 NDI guard failed: sender retry/default-create recovery is missing.' }
if ($setupAllText -notmatch 'runtimeNeedsInstall' -or $setupAllText -notmatch 'h264_mf') { throw 'FIX20 setup guard failed: one-click H.264 runtime repair probe is missing.' }
Write-Host '  Labeled transport, readable meters, adaptive ingest encoding and NDI sender recovery guards passed.' -ForegroundColor Green

Write-Host 'Checking NDI native runtime / ABI guard...' -ForegroundColor Cyan
if (Test-Path $ndiLocal) {
    & (Join-Path $ProjectRoot 'tools\Check-NDI6.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'NDI runtime check failed.' }
} else {
    Write-Host '  App-local NDI runtime not present; NDI output will require NDI_RUNTIME_DIR_V6.' -ForegroundColor Yellow
}

$ndiText = Get-Content $ndiSource -Raw
$referencePos = $ndiText.IndexOf('public int reference_level;')
$dataPos = $ndiText.IndexOf('public IntPtr p_data;', $referencePos)
if ($referencePos -lt 0 -or $dataPos -lt 0 -or $referencePos -gt $dataPos) {
    throw 'NDI interleaved audio ABI guard failed: reference_level must precede p_data.'
}
Write-Host '  NDI interop source ABI guard passed.' -ForegroundColor Green


Write-Host 'Checking seek/cancellation regression guard...' -ForegroundColor Cyan
$engineSource = Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Engine\PlayoutEngine.cs'
$engineText = Get-Content $engineSource -Raw
if ($engineText -match 'ThrowIfCancellationRequested\s*\(') {
    throw 'Seek regression guard failed: PlayoutEngine must not throw for normal operator cancellation.'
}
Write-Host '  Cooperative non-throwing seek cancellation guard passed.' -ForegroundColor Green


Write-Host 'Checking Studio UI / persistence regression guards...' -ForegroundColor Cyan
$mainXaml = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\MainWindow.xaml') -Raw
$appXaml = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\App.xaml') -Raw
$sharedThemePath = Join-Path $ProjectRoot 'src\Kashtrix.Shared\KashtrixTheme.xaml'
$sharedTheme = Get-Content $sharedThemePath -Raw
$projectText = Get-Content $project -Raw
# The playlist title beside CURRENT PLAYLIST: is sample text that follows the reference UI;
# validate the layout labels, not one particular demo rundown name.
if ($mainXaml -notmatch 'CURRENT PLAYLIST:' -or $mainXaml -notmatch 'EVENT LOG') { throw 'UI guard failed: AirBox/Easy-OnAir style program, rundown and event-log layout is incomplete.' }
$legacyTimeDeckComplete = ($mainXaml -match 'CLIP ELAPSED' -and $mainXaml -match 'CLIP REMAINING' -and $mainXaml -match 'BLOCK REMAINING')
$referenceTimeDeckComplete = ($mainXaml -match 'Current Played Time' -and $mainXaml -match 'Current Remaining Time' -and $mainXaml -match 'CurrentProgressPercent' -and $mainXaml -match 'Playlist Played Time' -and $mainXaml -match 'Playlist Remaining Time')
if (-not $legacyTimeDeckComplete -and -not $referenceTimeDeckComplete) { throw 'UI guard failed: operator timecode deck is incomplete.' }
if ($mainXaml -notmatch 'NOW PLAYING' -or $mainXaml -notmatch 'UP NEXT' -or $mainXaml -notmatch 'FILE EXPLORER / MEDIA DOCK') { throw 'UI guard failed: right-side now/next/file-explorer workflow is incomplete.' }
if ($mainXaml -notmatch 'QUICK CONTROLS' -or $mainXaml -notmatch 'CG' -or $mainXaml -notmatch 'AUDIO MIXER') { throw 'UI guard failed: bottom quick-control strip is incomplete.' }
if ($mainXaml -notmatch 'x:Name="ProgramImage"' -or $mainXaml -notmatch 'x:Name="PreviewImage"') { throw 'UI guard failed: program and cue preview surfaces are missing.' }
if ($mainXaml -match '<Viewbox[^>]*Stretch="Uniform"' -or $mainXaml -match '<Border Width="1672" Height="941"') { throw 'UI guard failed: fixed Viewbox design surface still causes letterboxing/cropping.' }
if ($mainXaml -notmatch 'GridSplitter' -or $mainXaml -notmatch 'MinWidth="780"') { throw 'UI guard failed: responsive/resizable playout layout markers are missing.' }
if ($mainXaml -notmatch 'ItemsSource="{Binding TimelineBlocks}"' -or $mainXaml -notmatch 'TimelinePlayheadX') { throw 'UI guard failed: rundown timeline is not data-driven/synchronized with playout.' }
# Validate the right-click workflow by handler wiring instead of brittle display labels.
# Operator-facing wording may evolve, but these handlers are the functional contract.
if ($mainXaml -notmatch 'PreviewMouseRightButtonDown="PlaylistGrid_PreviewMouseRightButtonDown"' -or
    $mainXaml -notmatch 'Click="ImportMedia_Click"' -or
    $mainXaml -notmatch 'Click="InsertMedia_Click"' -or
    $mainXaml -notmatch 'Click="AddTrimmedMedia_Click"' -or
    $mainXaml -notmatch 'Click="InsertTrimmedMedia_Click"' -or
    $mainXaml -notmatch 'Click="AddInput_Click"' -or
    $mainXaml -notmatch 'Click="InsertInputAfterSelected_Click"' -or
    $mainXaml -notmatch 'Click="AddCgEvent_Click"' -or
    $mainXaml -notmatch 'Click="InsertCgEvent_Click"' -or
    $mainXaml -notmatch 'Click="Trim_Click"' -or
    $mainXaml -notmatch 'Click="QcSelected_Click"') {
    throw 'UI guard failed: professional rundown right-click workflow is incomplete.'
}
if ($appXaml -notmatch 'Themes/KashtrixTheme.xaml') { throw 'UI guard failed: the main app must use the single shared Kashtrix theme dictionary.' }
if ($sharedTheme -notmatch 'x:Name="PART_Popup"') { throw 'UI guard failed: dark ComboBox popup template missing.' }
if ($sharedTheme -notmatch 'x:Name="PART_EditableTextBox"') { throw 'UI guard failed: editable ComboBox text template missing; selected input devices would not be visible.' }
if ($sharedTheme -notmatch 'Style TargetType="MenuItem"' -or $sharedTheme -notmatch '<Setter Property="BorderThickness" Value="0"' -or $sharedTheme -notmatch '<Setter Property="Foreground" Value="#F4F6F8"') { throw 'UI guard failed: clean borderless white-text menu styling is missing.' }
if ($projectText -notmatch 'Microsoft.Data.Sqlite') { throw 'Persistence guard failed: SQLite package missing.' }
if ($projectText -notmatch 'Microsoft.Data.Sqlite\" Version=\"10\.0\.11\"') { throw 'Persistence guard failed: Microsoft.Data.Sqlite 10.0.11 is required.' }
if (-not (Test-Path (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml'))) { throw 'CG Editor window missing.' }
if (-not (Test-Path (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\SchedulerWindow.xaml'))) { throw 'Scheduler window missing.' }
Write-Host '  Professional playout layout, responsive no-crop scaling, complete rundown menus/right-click workflow, clean menu theme and persistence guards passed.' -ForegroundColor Green

Write-Host 'Checking FIX12 operator-workflow guards...' -ForegroundColor Cyan
$mainCode = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\MainWindow.xaml.cs') -Raw
$playlistEditorXaml = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\PlaylistEditorWindow.xaml') -Raw
$playlistEditorCode = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\PlaylistEditorWindow.xaml.cs') -Raw
$fileManagerXaml = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\FileManagerWindow.xaml') -Raw
$fileManagerCode = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\FileManagerWindow.xaml.cs') -Raw
$schedulerXaml = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\SchedulerWindow.xaml') -Raw
$schedulerCode = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\SchedulerWindow.xaml.cs') -Raw
$propertiesXaml = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\PlaylistItemPropertiesWindow.xaml') -Raw
$customMessageBox = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\MessageBox.cs') -Raw
$demoCgText = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\CgDemoFactory.cs') -Raw
$qcText = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\QualityControlService.cs') -Raw
$buildTargets = Get-Content (Join-Path $ProjectRoot 'Directory.Build.targets') -Raw
if ($mainXaml -notmatch 'EmbeddedMediaList' -or $mainXaml -notmatch 'EmbeddedCategoryAssign' -or $mainXaml -notmatch 'INPUT SOURCES' -or $mainXaml -notmatch 'InputMonitorTiles') { throw 'FIX12 guard failed: embedded file explorer/input-source multiview dock is incomplete.' }
if ($mainCode -notmatch 'Kashtrix\.MediaFile' -or $mainCode -notmatch 'StartInputMonitor' -or $mainCode -notmatch 'MediaCategoryStore\.Set') { throw 'FIX12 guard failed: media drag/category/input multiview code is incomplete.' }
if ($playlistEditorXaml -notmatch 'PlaylistGrid_Drop' -or $playlistEditorXaml -notmatch 'PlaylistGrid_MouseDoubleClick' -or $playlistEditorXaml -notmatch '\+ STOP' -or $playlistEditorCode -notmatch 'PlaylistItemPropertiesWindow') { throw 'FIX12 guard failed: playlist-editor reorder/properties/control events are incomplete.' }
if ($fileManagerXaml -notmatch 'SAVE CATEGORY' -or $fileManagerXaml -notmatch 'MediaList_PreviewMouseMove' -or $fileManagerCode -notmatch 'MediaCategoryStore\.Set' -or $fileManagerCode -notmatch 'SaveAsSeparateItems') { throw 'FIX12 guard failed: File Manager category/drag/multipart workflow is incomplete.' }
if ($schedulerXaml -notmatch 'Custom days' -or $schedulerXaml -notmatch 'Hard Start' -or $schedulerXaml -notmatch 'ScheduleGrid_Drop' -or $schedulerCode -notmatch 'AddControlSchedule') { throw 'FIX12 guard failed: dynamic scheduler workflow is incomplete.' }
if (($propertiesXaml -notmatch 'MEDIA / CG METADATA' -and $propertiesXaml -notmatch 'EPG METADATA') -or $propertiesXaml -notmatch 'Row color') { throw 'FIX12 guard failed: playlist-item properties metadata/color editor is incomplete.' }
if ($customMessageBox -notmatch 'WindowStyle = WindowStyle.None' -or $customMessageBox -notmatch 'ShowCore') { throw 'FIX12 guard failed: custom themed alert implementation is incomplete.' }
if ($demoCgText -notmatch 'MusicNowPlaying' -or $demoCgText -notmatch 'MusicComingUp' -or $demoCgText -notmatch 'NewsNowPlaying' -or $demoCgText -notmatch 'NewsComingUp') { throw 'FIX12 guard failed: music/news demo graphics are missing.' }
if ($qcText -notmatch '\[Header\]' -or $qcText -notmatch '\[Decode\]' -or $qcText -notmatch '\[Warning\]') { throw 'FIX12 guard failed: MediaTime-style QC diagnostics are incomplete.' }
if ($buildTargets -notmatch 'EnsureKashtrixSharedProjectBuilt' -or $projectText -notmatch 'ProduceReferenceAssembly>false') { throw 'FIX12 guard failed: Visual Studio CS0006 build hardening is missing.' }
Write-Host '  FIX12 file explorer, input dock, editor reordering, metadata, scheduler, auto-CG, QC, themed alerts and direct-project build guards passed.' -ForegroundColor Green


Write-Host 'Checking FIX23 performance / hardware decode guards...' -ForegroundColor Cyan
$videoDecoderText = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Ffmpeg\VideoDecoder.cs') -Raw
$mainWindowCodePerf = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\MainWindow.xaml.cs') -Raw
$multiviewCodePerf = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\MultiviewWindow.xaml.cs') -Raw
$outputProcessorPerf = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\VideoOutputProcessor.cs') -Raw
if ($videoDecoderText -notmatch 'av_hwdevice_ctx_create' -or $videoDecoderText -notmatch 'av_hwframe_transfer_data' -or $videoDecoderText -notmatch 'CPU\+GPU Hybrid') { throw 'FIX23 performance guard failed: hardware-accelerated decoder/fallback path is incomplete.' }
if ($mainWindowCodePerf -notmatch 'ShouldQueueConfidenceFrame' -or $mainWindowCodePerf -notmatch 'ProgramConfidenceFps') { throw 'FIX23 performance guard failed: operator confidence-monitor throttling is missing.' }
if ($multiviewCodePerf -notmatch 'DecodeFilePosterFrame' -or $multiviewCodePerf -notmatch 'ScheduleProgramPresent') { throw 'FIX23 performance guard failed: multiview decode/UI load shedding is missing.' }
if ($outputProcessorPerf -notmatch 'BuildGradeLut' -or $outputProcessorPerf -notmatch 'simpleColorPath') { throw 'FIX23 performance guard failed: final-output color processor LUT fast path is missing.' }
Write-Host '  Hardware decode with safe CPU fallback, confidence-monitor throttling, multiview load shedding and color-processing fast path guards passed.' -ForegroundColor Green

Write-Host 'Checking FIX12 final timeline / chrome / controller guards...' -ForegroundColor Cyan
$mainVmFinal = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\ViewModels\MainViewModel.cs') -Raw
$diagText = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\StandaloneAppDiagnostics.cs') -Raw
$channelControllerCodeFinal = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\ChannelControllerWindow.xaml.cs') -Raw
$cgControllerCodeFinal = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgControllerWindow.xaml.cs') -Raw
if ($mainVmFinal -notmatch '_currentItemTimelineStart' -or $mainVmFinal -notmatch '_timelinePauseStartedUtc' -or $mainVmFinal -notmatch 'FormatRundownStartTime' -or $mainVmFinal -notmatch 'TimelineCanvasWidth') { throw 'FIX12 final guard failed: resizable pause-safe timeline synchronization is incomplete.' }
if ($mainXaml -notmatch 'TimelineSurface_SizeChanged' -or $mainXaml -notmatch 'ResizeDirection="Rows"') { throw 'FIX12 final guard failed: timeline/operator rows are not resize-aware.' }
if ($diagText -notmatch 'Keyboard.PreviewKeyDownEvent' -or $diagText -notmatch 'Key.Escape') { throw 'FIX12 final guard failed: global ESC-close handling is missing.' }
if ($channelControllerCodeFinal -notmatch 'ids.Add\(_vm.ChannelId\)' -or $channelControllerCodeFinal -notmatch 'LocalCgCommandBus\.Publish') { throw 'FIX12 final guard failed: Channel Controller does not auto-include/direct-route local Playout.' }
if ($cgControllerCodeFinal -notmatch 'NETWORK \+ LOCAL' -or $cgControllerCodeFinal -notmatch 'LocalCgCommandBus\.Publish') { throw 'FIX12 final guard failed: CG Controller redundant local/network routing is missing.' }
if ($sharedTheme -notmatch 'WindowMaximizeGlyph' -or $sharedTheme -notmatch 'E922' -or $sharedTheme -notmatch 'E923') { throw 'FIX12 final guard failed: authentic maximize/restore glyph switching is missing.' }
Write-Host '  Pause-safe dynamic timeline, resize splitters, ESC close, authentic chrome and redundant local/network controller routing guards passed.' -ForegroundColor Green


Write-Host 'Checking XAML StaticResource scope guard...' -ForegroundColor Cyan
$themeForScope = Get-Content (Join-Path $ProjectRoot 'src\Kashtrix.Shared\KashtrixTheme.xaml') -Raw
$themeKeys = @{}
foreach ($m in [regex]::Matches($themeForScope, 'x:Key="([^"]+)"')) { $themeKeys[$m.Groups[1].Value] = $true }
$xamlScopeFiles = Get-ChildItem (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views') -Filter *.xaml -File
foreach ($xamlScopeFile in $xamlScopeFiles) {
    $scopeText = Get-Content $xamlScopeFile.FullName -Raw
    $localKeys = @{}
    foreach ($m in [regex]::Matches($scopeText, 'x:Key="([^"]+)"')) { $localKeys[$m.Groups[1].Value] = $true }
    foreach ($m in [regex]::Matches($scopeText, '\{StaticResource\s+([A-Za-z_][A-Za-z0-9_.-]*)\}')) {
        $key = $m.Groups[1].Value
        if (-not $themeKeys.ContainsKey($key) -and -not $localKeys.ContainsKey($key)) {
            throw "StaticResource scope guard failed in $($xamlScopeFile.FullName): resource '$key' is not defined in the shared theme or this XAML file."
        }
    }
}
Write-Host '  All named StaticResource references resolve to shared or local resources.' -ForegroundColor Green

Write-Host 'Checking XAML resource duplicate-key guard...' -ForegroundColor Cyan
$resourceFiles = @(
    (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\App.xaml'),
    $sharedThemePath
)
foreach ($resourceFile in $resourceFiles) {
    $resourceText = Get-Content $resourceFile -Raw
    $explicitKeys = [regex]::Matches($resourceText, 'x:Key="([^"]+)"') | ForEach-Object { $_.Groups[1].Value }
    $duplicateExplicit = $explicitKeys | Group-Object | Where-Object { $_.Count -gt 1 }
    if ($duplicateExplicit) { throw "XAML resource guard failed in $($resourceFile): duplicate x:Key(s): $($duplicateExplicit.Name -join ', ')" }
    $implicitStyles = [regex]::Matches($resourceText, '<Style(?![^>]*x:Key=)[^>]*TargetType="([^"]+)"[^>]*>') | ForEach-Object { $_.Groups[1].Value }
    $duplicateImplicit = $implicitStyles | Group-Object | Where-Object { $_.Count -gt 1 }
    if ($duplicateImplicit) { throw "XAML resource guard failed in $($resourceFile): duplicate implicit Style TargetType(s): $($duplicateImplicit.Name -join ', ')" }
}
Write-Host '  No duplicate explicit or implicit resource keys found.' -ForegroundColor Green


Write-Host 'Checking XAML compile-safety guards...' -ForegroundColor Cyan
$xamlFiles = Get-ChildItem -Path (Join-Path $ProjectRoot 'src') -Recurse -Filter '*.xaml' -File
$standardControlsWithoutCornerRadius = @('Button','TextBox','ComboBox','CheckBox','RadioButton','DataGrid','DataGridRow','DataGridCell','ListBox','ListBoxItem','TabControl','TabItem','ProgressBar','Slider','MenuItem','Window')
foreach ($xamlFile in $xamlFiles) {
    $xamlText = Get-Content $xamlFile.FullName -Raw
    try {
        [xml]$parsedXaml = $xamlText
    } catch {
        throw "XAML XML parse guard failed in $($xamlFile.FullName): $($_.Exception.Message)"
    }

    $styleMatches = [regex]::Matches($xamlText, '(?s)<Style\b[^>]*TargetType="([^"]+)"[^>]*>(.*?)</Style>')
    foreach ($styleMatch in $styleMatches) {
        $targetType = $styleMatch.Groups[1].Value
        $styleBody = $styleMatch.Groups[2].Value
        if ($standardControlsWithoutCornerRadius -contains $targetType -and $styleBody -match '<Setter\s+Property="CornerRadius"') {
            throw "XAML compile guard failed in $($xamlFile.FullName): $targetType does not expose a CornerRadius dependency property. Put CornerRadius on the Border inside the control template instead."
        }
    }

    if ($xamlText -match '<(?:Button|TextBox|ComboBox|CheckBox|RadioButton|ProgressBar|Slider)\b[^>]*\bCornerRadius\s*=') {
        throw "XAML compile guard failed in $($xamlFile.FullName): CornerRadius is being set directly on a standard WPF control that does not expose that property."
    }

    if ($xamlText -match '<(?:CheckBox|Button|ComboBox|Slider|ProgressBar)\b[^>]*\bTextWrapping\s*=') {
        throw "XAML compile guard failed in $($xamlFile.FullName): TextWrapping is being set directly on a WPF control that does not expose that property. Put a TextBlock with TextWrapping inside the control Content instead."
    }

    $progressBindings = [regex]::Matches($xamlText, '(?s)<ProgressBar\b[^>]*\bValue="\{Binding[^"]+\}"[^>]*>')
    foreach ($progressBinding in $progressBindings) {
        if ($progressBinding.Value -notmatch 'Mode\s*=\s*(OneWay|OneTime)') {
            throw "WPF binding guard failed in $($xamlFile.FullName): ProgressBar.Value bindings are display-only and must explicitly use Mode=OneWay (or OneTime)."
        }
    }

    $runTextBindings = [regex]::Matches($xamlText, '(?s)<Run\b[^>]*\bText="\{Binding[^"]+\}"[^>]*/?>')
    foreach ($runTextBinding in $runTextBindings) {
        if ($runTextBinding.Value -notmatch 'Mode\s*=\s*(OneWay|OneTime)') {
            throw "WPF binding guard failed in $($xamlFile.FullName): Run.Text is display-only and must explicitly use Mode=OneWay (or OneTime)."
        }
    }

    # Computed/getter-only display values must never inherit a target property's TwoWay default.
    # This catches runtime-only WPF failures such as PlaylistItem.DurationText being used by Run.Text
    # or a DataGridTextColumn without an explicit one-way mode.
    $computedReadOnlyLeaves = @('DurationText','RundownDurationText','PartsSummary','PlaylistDurationText','SourceDurationText','MediaDetails','StatusText','SizeText')
    $bindingExpressions = [regex]::Matches($xamlText, '\{Binding\s+([^},\s]+)([^}]*)\}')
    foreach ($bindingExpression in $bindingExpressions) {
        $bindingPath = $bindingExpression.Groups[1].Value
        $bindingOptions = $bindingExpression.Groups[2].Value
        $bindingLeaf = ($bindingPath -split '\.')[-1]
        if ($computedReadOnlyLeaves -contains $bindingLeaf -and $bindingOptions -notmatch 'Mode\s*=\s*(OneWay|OneTime)') {
            throw "WPF read-only binding guard failed in $($xamlFile.FullName): '$bindingPath' is a computed display property and must explicitly use Mode=OneWay (or OneTime)."
        }
    }
}

# File-specific computed columns whose names overlap writable properties in other models.
$schedulerXamlText = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\SchedulerWindow.xaml') -Raw
if ($schedulerXamlText -notmatch 'Binding="\{Binding Status, Mode=OneWay\}"') { throw 'WPF read-only binding guard failed: ScheduleEntry.Status must be OneWay.' }
$qcXamlText = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\QcControllerWindow.xaml') -Raw
if ($qcXamlText -notmatch 'Binding="\{Binding FileName, Mode=OneWay\}"' -or $qcXamlText -notmatch 'Binding="\{Binding Status, Mode=OneWay\}"') { throw 'WPF read-only binding guard failed: QC FileName/Status columns must be OneWay.' }
Write-Host '  XAML XML, standard-control property, display-only Run/ProgressBar and computed read-only binding guards passed.' -ForegroundColor Green

Write-Host 'Checking playlist trim / input-source integration guards...' -ForegroundColor Cyan
$playlistModel = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Models\PlaylistItem.cs') -Raw
$inputSource = Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\InputSourceWindow.xaml'
$trimmer = Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\MediaTrimmerWindow.xaml'
$ffmpegInput = Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Ffmpeg\FfmpegInput.cs'
if ($mainXaml -notmatch 'Click="Trim_Click"') { throw 'Media trim guard failed: playlist TRIM action missing.' }
if ($mainXaml -notmatch 'Click="AddInput_Click"') { throw 'Input source guard failed: playlist INPUT action missing.' }
if (-not (Test-Path $inputSource)) { throw 'Input source guard failed: InputSourceWindow missing.' }
if (-not (Test-Path $trimmer)) { throw 'Media trim guard failed: MediaTrimmerWindow missing.' }
if (-not (Test-Path $ffmpegInput)) { throw 'Input source guard failed: FFmpeg input adapter missing.' }
if ($playlistModel -notmatch 'SourceKind' -or $playlistModel -notmatch 'SourceDuration') { throw 'Playlist source/trim metadata missing.' }
Write-Host '  Media trimming and URL/DirectShow/screen/custom input guards passed.' -ForegroundColor Green


Write-Host 'Checking multipart / QC / master-audio / virtual-output feature guards...' -ForegroundColor Cyan
$mediaPart = Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Models\MediaPart.cs'
$qcSource = Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\QualityControlService.cs'
$virtualBridge = Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Outputs\VirtualOutputBridge.cs'
$settingsModelText = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Models\AppSettings.cs') -Raw
$trimmerText = Get-Content $trimmer -Raw
if (-not (Test-Path $mediaPart) -or $playlistModel -notmatch 'ObservableCollection<MediaPart> Parts' -or $playlistModel -notmatch 'GetPlaybackParts') { throw 'Multipart guard failed: one-source/many-ranges playlist model is incomplete.' }
if ($trimmerText -notmatch 'MULTIPART MEDIA TRIMMER' -or $trimmerText -notmatch 'ADD PART') { throw 'Multipart guard failed: operator multipart trim UI is incomplete.' }
if (-not (Test-Path $qcSource) -or $settingsModelText -notmatch 'MediaQcPreset QcPreset') { throw 'QC guard failed: preset-driven media QC is incomplete.' }
if ($settingsModelText -notmatch 'MasterOutputVolumePercent' -or $settingsModelText -notmatch 'MasterMuteShortcut') { throw 'Master-audio guard failed: volume/mute shortcut settings are missing.' }
if (-not (Test-Path $virtualBridge) -or $settingsModelText -notmatch 'EnableVirtualOutput') { throw 'Virtual-output guard failed: formatted shared-memory output is incomplete.' }
$virtualSetup = Join-Path $ProjectRoot 'tools\Setup-VirtualOutput.ps1'
$virtualBuild = Join-Path $ProjectRoot 'tools\Build-VirtualOutput.ps1'
$virtualDeps = Join-Path $ProjectRoot 'tools\Ensure-DirectShowBaseClasses.ps1'
if (-not (Test-Path $virtualSetup) -or -not (Test-Path $virtualBuild) -or -not (Test-Path $virtualDeps)) { throw 'Virtual-output auto-setup guard failed: setup/build/dependency bootstrap scripts are incomplete.' }
$virtualSetupText = Get-Content $virtualSetup -Raw
$virtualBuildText = Get-Content $virtualBuild -Raw
$virtualDepsText = Get-Content $virtualDeps -Raw
if ($virtualSetupText -notmatch 'download the official Microsoft source' -or $virtualBuildText -notmatch 'Ensure-DirectShowBaseClasses\.ps1' -or $virtualDepsText -notmatch 'codeload\.github\.com/microsoft/Windows-classic-samples/tar\.gz' -or $virtualDepsText -notmatch '--strip-components=6' -or $virtualDepsText -notmatch 'raw\.githubusercontent\.com/microsoft/Windows-classic-samples' -or $virtualDepsText -notmatch 'Kashtrix\.BaseClasses\.vcxproj') { throw 'Virtual-output auto-setup guard failed: long-path-safe Microsoft BaseClasses bootstrap/build flow is missing.' }
Write-Host '  Virtual Output automatic DirectShow BaseClasses bootstrap guard passed.' -ForegroundColor Green
if ($sharedTheme -notmatch 'x:Key="DigitalClockText"' -or $sharedTheme -notmatch 'Digital-7') { throw 'Clock guard failed: Digital-7 time style is missing.' }
if ($mainXaml -notmatch 'Click="FileManager_Click"' -or $mainXaml -notmatch 'Click="QcController_Click"' -or $mainXaml -notmatch 'Click="CgController_Click"') { throw 'Operator UI guard failed: separate File Manager / QC / CG Controller launch actions are missing.' }
Write-Host '  Multipart trims, preset QC, Digital-7 clocks, master audio shortcuts and formatted virtual output guards passed.' -ForegroundColor Green

Write-Host 'Checking Chromium HTML CG integration guards...' -ForegroundColor Cyan
$cgModels = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Models\CgModels.cs') -Raw
$chromiumSource = Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\ChromiumCgRuntime.cs'
$cgEditorXaml = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml') -Raw
if ($projectText -notmatch 'CefSharp.OffScreen.NETCore') { throw 'Chromium guard failed: CefSharp.OffScreen.NETCore package missing.' }
if ($cgModels -notmatch 'List<CgHtmlSource> HtmlSources') { throw 'Chromium guard failed: HTML sources are not separated from native timeline layers.' }
if (-not (Test-Path $chromiumSource)) { throw 'Chromium guard failed: ChromiumCgRuntime.cs missing.' }
if ($cgEditorXaml -notmatch 'HTML / WEB') { throw 'Web graphics guard failed: HTML/web import UI missing.' }
if ($cgEditorXaml -notmatch '\.KASHGFX') { throw 'Chromium guard failed: .kashgfx import UI missing.' }
Write-Host '  Off-screen Chromium, no-timeline HTML sources, URL/folder/.kashgfx import guards passed.' -ForegroundColor Green

Write-Host 'Checking CefSharp x64 runtime guard...' -ForegroundColor Cyan
if ($projectText -notmatch '<RuntimeIdentifier>win-x64</RuntimeIdentifier>') { throw 'CefSharp runtime guard failed: RuntimeIdentifier must be win-x64.' }
if ($projectText -notmatch '<RuntimeIdentifiers>win-x64</RuntimeIdentifiers>') { throw 'CefSharp runtime guard failed: RuntimeIdentifiers must be restricted to win-x64.' }
if ($projectText -notmatch '<CefSharpRuntimeIdentifierOverride>win-x64</CefSharpRuntimeIdentifierOverride>') { throw 'CefSharp runtime guard failed: CefSharp runtime override must be win-x64.' }
Write-Host '  CefSharp Chromium runtime locked to win-x64; ARM64 copy path disabled.' -ForegroundColor Green

Write-Host 'Checking C# regex/source regression guards...' -ForegroundColor Cyan
$cgCompositor = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\CgCompositor.cs') -Raw
if ($cgCompositor -match 'Regex\.Replace\([^\r\n]*, \"[^\"]*\\s') { throw 'C# regex guard failed: use verbatim @ strings for regex backslash escapes.' }
if ($cgCompositor -notmatch '@\"<script\[\\s\\S\]\*\?</script>\"') { throw 'C# regex guard failed: HTML script stripping regex is not verbatim-safe.' }
# System.Drawing.Graphics has no DrawImage(Image, RectangleF, int, int, int, int, GraphicsUnit) overload.
# Keep destination rectangles integral for the integer-source-rectangle overload used by the CG compositor.
if ($cgCompositor -match 'DrawImage\(srcBitmap,\s*new\s+RectangleF') {
    throw 'CG compositor compile guard failed: RectangleF cannot be used with the integer-source DrawImage overload; use Rectangle.Round(...).'
}
if ($cgCompositor -notmatch 'DrawImage\(srcBitmap,\s*Rectangle\.Round\(new\s+RectangleF') {
    throw 'CG compositor compile guard failed: squeezed-program DrawImage destination must use Rectangle.Round(RectangleF).'
}
Write-Host '  C# regex and System.Drawing overload guards passed.' -ForegroundColor Green

Write-Host 'Checking WPF namespace ambiguity guards...' -ForegroundColor Cyan
$cgEditorCode = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml.cs') -Raw
if ($cgEditorCode -match 'using System\.Windows\.Shapes;' -and $cgEditorCode -match '(?m)(?<!IO)\bPath\.(GetFileName|GetFileNameWithoutExtension|GetExtension|DirectorySeparatorChar)') {
    throw 'WPF namespace guard failed: qualify System.IO.Path when System.Windows.Shapes.Path is in scope.'
}
if ($cgEditorCode -notmatch 'using IOPath = System\.IO\.Path;') { throw 'WPF namespace guard failed: IOPath alias missing in CG Editor.' }
Write-Host '  System.IO.Path / WPF Shapes namespace guard passed.' -ForegroundColor Green

Write-Host 'Checking Visual Studio long-path build guard...' -ForegroundColor Cyan
$buildPropsPath = Join-Path $ProjectRoot 'Directory.Build.props'
if (-not (Test-Path $buildPropsPath)) { throw 'Visual Studio build guard failed: Directory.Build.props is missing.' }
$buildProps = Get-Content $buildPropsPath -Raw
if ($buildProps -notmatch 'KashtrixPlayout\\Build') { throw 'Visual Studio build guard failed: short external build root is not configured.' }
# Directory.Build.props intentionally puts conditions on these properties so WPF-generated
# *_wpftmp projects keep their injected parent paths. Accept attributes on the XML elements;
# checking for an exact '<BaseIntermediateOutputPath>' tag incorrectly rejects that valid form.
if ($buildProps -notmatch '<BaseIntermediateOutputPath(?:\s+[^>]*)?>') { throw 'Visual Studio build guard failed: BaseIntermediateOutputPath is not configured.' }
if ($buildProps -notmatch '<BaseOutputPath(?:\s+[^>]*)?>') { throw 'Visual Studio build guard failed: BaseOutputPath is not configured.' }
if ($buildProps -notmatch '<BaseIntermediateOutputPath(?:\s+[^>]*)?>\s*\$\(KashtrixBuildRoot\)\\obj\\') { throw 'Visual Studio build guard failed: BaseIntermediateOutputPath must use the short Kashtrix build root.' }
if ($buildProps -notmatch '<BaseOutputPath(?:\s+[^>]*)?>\s*\$\(KashtrixBuildRoot\)\\bin\\') { throw 'Visual Studio build guard failed: BaseOutputPath must use the short Kashtrix build root.' }
Write-Host '  MSBuild obj/bin redirected to a short LOCALAPPDATA path for Visual Studio/CefSharp.' -ForegroundColor Green

Write-Host 'Checking NDI receiver model namespace guard...' -ForegroundColor Cyan
$ndiReceiverCode = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Outputs\NdiVideoReceiver.cs') -Raw
if ($ndiReceiverCode -notmatch 'using\s+BroadcastPlayout\.Models;') { throw 'NDI receiver compile guard failed: BroadcastPlayout.Models namespace import is missing for VideoFrameData.' }
if ($ndiReceiverCode -notmatch 'TryRead\s*\(out\s+VideoFrameData') { throw 'NDI receiver compile guard failed: VideoFrameData receive signature is missing.' }
Write-Host '  NDI receiver VideoFrameData namespace/import guard passed.' -ForegroundColor Green

Write-Host 'Checking media-frame / multi-app compile guards...' -ForegroundColor Cyan
$mediaFrames = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Models\MediaFrames.cs') -Raw
$multiviewCode = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\MultiviewWindow.xaml.cs') -Raw
$mainVmText = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\ViewModels\MainViewModel.cs') -Raw
if ($mediaFrames -notmatch 'public double FramesPerSecond') { throw 'Media frame guard failed: VideoFrameData.FramesPerSecond convenience property missing.' }
if ($multiviewCode -match 'class MultiviewTile\s*\(') { throw 'Multiview guard failed: avoid primary-constructor capture warning in MultiviewTile.' }
if ($mainVmText -match '_isFillerPlayback') { throw 'Filler guard failed: stale unused _isFillerPlayback field remains.' }
if ($mainVmText -match '_liveFirstPts') { throw 'Warning cleanup guard failed: stale unused _liveFirstPts field remains.' }
$expectedProjects = @('Kashtrix.Playout','Kashtrix.CGEditor','Kashtrix.Scheduler','Kashtrix.PlaylistEditor','Kashtrix.Settings','Kashtrix.Multiview','Kashtrix.ChannelController','Kashtrix.CGController','Kashtrix.FileManager','Kashtrix.QCController','Kashtrix.IngestServer','Kashtrix.MAM','Kashtrix.NRCS','Kashtrix.Prompter','Kashtrix.HAController','Kashtrix.ApiGateway','Kashtrix.OutputEngine')
$solutionText = Get-Content $solution -Raw
foreach ($name in $expectedProjects) {
    if ($solutionText -notmatch [regex]::Escape('"' + $name + '"')) { throw "Solution guard failed: $name is missing." }
}
Write-Host '  Media frame API, warning cleanup and all sixteen application projects are present.' -ForegroundColor Green

Write-Host 'Checking FIX12 WPF source compile guards...' -ForegroundColor Cyan
$schedulerCode = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\SchedulerWindow.xaml.cs') -Raw
$playlistEditorCode = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\PlaylistEditorWindow.xaml.cs') -Raw
$fileManagerXaml = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\FileManagerWindow.xaml') -Raw
if ($schedulerCode -notmatch 'using System\.Windows\.Controls;' -or $playlistEditorCode -notmatch 'using System\.Windows\.Controls;') { throw 'WPF compile guard failed: Scheduler/Playlist Editor require System.Windows.Controls for ItemsControl/DataGridRow.' }
if ($fileManagerXaml -notmatch 'x:Name="MaxGlyph"') { throw 'WPF compile guard failed: File Manager MaxGlyph field is missing from XAML.' }
Write-Host '  Scheduler/Playlist Editor WPF control namespaces and File Manager chrome fields are compile-safe.' -ForegroundColor Green

Write-Host 'Checking standalone WPF application namespace guards...' -ForegroundColor Cyan
$standaloneProjects = @('Kashtrix.CGEditor','Kashtrix.Scheduler','Kashtrix.PlaylistEditor','Kashtrix.Settings','Kashtrix.Multiview','Kashtrix.ChannelController','Kashtrix.CGController','Kashtrix.FileManager','Kashtrix.QCController','Kashtrix.IngestServer','Kashtrix.MAM','Kashtrix.NRCS','Kashtrix.Prompter','Kashtrix.HAController','Kashtrix.ApiGateway')
foreach ($standaloneName in $standaloneProjects) {
    $standaloneDir = Join-Path $ProjectRoot ('src\' + $standaloneName)
    $appCodePath = Join-Path $standaloneDir 'App.xaml.cs'
    $standaloneProjectPath = Join-Path $standaloneDir ($standaloneName + '.csproj')
    $appCode = Get-Content $appCodePath -Raw
    $standaloneProjectText = Get-Content $standaloneProjectPath -Raw
    if ($appCode -match 'partial\s+class\s+App\s*:\s*Application\b') {
        throw "WPF namespace guard failed: $standaloneName uses ambiguous Application base type."
    }
    if ($appCode -notmatch 'partial\s+class\s+App\s*:\s*System\.Windows\.Application') {
        throw "WPF namespace guard failed: $standaloneName must explicitly inherit System.Windows.Application."
    }
    $standaloneSourceText = (Get-ChildItem $standaloneDir -Recurse -Filter *.cs -File | ForEach-Object { Get-Content $_.FullName -Raw }) -join "`n"
    $usesWinFormsApi = $standaloneSourceText -match 'System\.Windows\.Forms'
    $winFormsEnabled = $standaloneProjectText -match '<UseWindowsForms>true</UseWindowsForms>'
    if ($usesWinFormsApi -and -not $winFormsEnabled) {
        throw "WPF namespace guard failed: $standaloneName uses System.Windows.Forms APIs but UseWindowsForms=true is missing."
    }
    if (-not $usesWinFormsApi -and $winFormsEnabled) {
        throw "WPF namespace guard failed: $standaloneName enables WinForms without using System.Windows.Forms APIs."
    }
    if ($usesWinFormsApi -and $standaloneSourceText -notmatch 'using\s+Forms\s*=\s*System\.Windows\.Forms\s*;') {
        throw "WPF namespace guard failed: $standaloneName must alias System.Windows.Forms as Forms to avoid WPF namespace ambiguity."
    }
    if ($standaloneProjectText -notmatch '<ImplicitUsings>disable</ImplicitUsings>') {
        throw "WPF namespace guard failed: $standaloneName must keep ImplicitUsings disabled to prevent WinForms/WPF namespace collisions."
    }
}
Write-Host '  Standalone WPF apps use explicit WPF Application types; WinForms is enabled only where the source actually requires it.' -ForegroundColor Green

Write-Host 'Checking window chrome partial-class regression guard...' -ForegroundColor Cyan
$windowChromePath = Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\WindowChromeHandlers.cs'
$windowChromeText = Get-Content $windowChromePath -Raw
if ($windowChromeText -match 'partial\s+class\s+') {
    throw 'Window chrome guard failed: WindowChromeHandlers.cs must contain shared helpers only; RoutedEvent handlers belong in each window code-behind.'
}
$chromeWindows = @('CgEditorWindow.xaml.cs','SchedulerWindow.xaml.cs','PlaylistEditorWindow.xaml.cs','SettingsWindow.xaml.cs','MultiviewWindow.xaml.cs','ChannelControllerWindow.xaml.cs')
foreach ($chromeFile in $chromeWindows) {
    $chromeCode = Get-Content (Join-Path $ProjectRoot ('src\BroadcastPlayout.App\Views\' + $chromeFile)) -Raw
    $minCount = ([regex]::Matches($chromeCode, 'Minimize_Click\s*\(')).Count
    $maxCount = ([regex]::Matches($chromeCode, 'Maximize_Click\s*\(')).Count
    if ($minCount -ne 1 -or $maxCount -ne 1) {
        throw "Window chrome guard failed: $chromeFile must define exactly one Minimize_Click and one Maximize_Click handler."
    }
}
Write-Host '  Window chrome handlers are defined exactly once per partial window.' -ForegroundColor Green

Write-Host 'Checking runtime binding / program-launcher regression guards...' -ForegroundColor Cyan
if ($mainXaml -notmatch 'AudioLeftLevel,\s*Mode=OneWay' -or $mainXaml -notmatch 'AudioRightLevel,\s*Mode=OneWay') {
    throw 'WPF binding guard failed: read-only program audio meters must use Mode=OneWay.'
}
if ($mainXaml -match 'Value="\{Binding AudioLeftLevel\}"' -or $mainXaml -match 'Value="\{Binding AudioRightLevel\}"') {
    throw 'WPF binding guard failed: default ProgressBar binding mode would try to write to read-only audio meter properties.'
}
$programLauncherXamlPath = Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\ProgramLauncherWindow.xaml'
$programLauncherCodePath = Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\ProgramLauncherWindow.xaml.cs'
if (-not (Test-Path $programLauncherXamlPath) -or -not (Test-Path $programLauncherCodePath)) {
    throw 'Program Launcher guard failed: launcher window source is missing.'
}
$programLauncherXaml = Get-Content $programLauncherXamlPath -Raw
$programLauncherCode = Get-Content $programLauncherCodePath -Raw
if ($mainXaml -notmatch 'Click="ProgramLauncher_Click"') { throw 'Program Launcher guard failed: Playout APPS action is missing.' }
foreach ($launcherKey in @('CGEditor','CGController','Scheduler','PlaylistEditor','Multiview','ChannelController','FileManager','QCController','Settings')) {
    if ($programLauncherXaml -notmatch ('Tag="' + [regex]::Escape($launcherKey) + '"')) {
        throw "Program Launcher guard failed: $launcherKey action is missing."
    }
}
if ($programLauncherCode -notmatch 'StandaloneAppLauncher\.Launch') { throw 'Program Launcher guard failed: modules are not launched through the standalone executable launcher.' }
Write-Host '  Read-only audio meter bindings are OneWay and Program Launcher actions target standalone executables.' -ForegroundColor Green

Write-Host 'Checking playout operator workflow guards...' -ForegroundColor Cyan
$mainWindowCode = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\MainWindow.xaml.cs') -Raw
$engineText = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Engine\PlayoutEngine.cs') -Raw
$playlistItemText = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Models\PlaylistItem.cs') -Raw
if ($mainWindowCode -notmatch 'ModifierKeys\.Control.*Key\.Space' -or $mainWindowCode -notmatch 'ToggleSelectedPlayPause') {
    throw 'Operator workflow guard failed: Ctrl+Space selected-item play/pause is missing.'
}
if ($mainVmText -notmatch 'ToggleSelectedPlayPause\(\)[\s\S]{0,700}if \(_engine\.IsPlaying\)') {
    throw 'Operator workflow guard failed: Ctrl+Space must pause/resume Program instead of taking another preview row while playout is active.'
}
if ($mainXaml -notmatch 'x:Name="PreviewImage"' -or $mainXaml -notmatch 'x:Name="ProgramImage"' -or $mainXaml -notmatch 'Click="ImportMedia_Click"' -or $mainXaml -notmatch 'Click="InsertMedia_Click"') {
    throw 'Operator workflow guard failed: Preview/Program buses or rundown add/insert menus are incomplete.'
}
if ($mainXaml -notmatch 'AddCgEvent_Click' -or $mainXaml -notmatch 'InsertCgEvent_Click' -or $mainXaml -notmatch 'AddTrimmedMedia_Click' -or $mainXaml -notmatch 'InsertInputAfterSelected_Click') {
    throw 'Operator workflow guard failed: CG/live/media-trim insert actions are incomplete.'
}
if ($engineText -notmatch 'public void Pause\(\)' -or $engineText -notmatch 'public void Resume\(\)' -or $engineText -notmatch 'PlayCgEventAsync') {
    throw 'Operator workflow guard failed: engine pause/resume or CG rundown event support is missing.'
}
if ($playlistItemText -notmatch 'CgProjectId' -or $playlistItemText -notmatch 'IsCgEvent') {
    throw 'Operator workflow guard failed: CG rundown metadata is missing.'
}
Write-Host '  Ctrl+Space play/pause, dual Preview/Program, add/insert menus, trim/live and CG rundown guards passed.' -ForegroundColor Green

Write-Host 'Checking independent CG PREVIEW / PROGRAM bus guards...' -ForegroundColor Cyan
$cgControllerXaml = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgControllerWindow.xaml') -Raw
$cgControllerCode = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgControllerWindow.xaml.cs') -Raw
$channelModelsText = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Models\ChannelModels.cs') -Raw
if ($channelModelsText -notmatch 'string Bus' -or $channelModelsText -notmatch 'PREVIEW, PROGRAM') { throw 'CG bus guard failed: CgRemoteCommand.Bus is missing.' }
if ($cgControllerXaml -notmatch 'PREVIEW BUS' -or $cgControllerXaml -notmatch 'PROGRAM BUS' -or $cgControllerXaml -notmatch 'TAKE.*PROGRAM') { throw 'CG bus guard failed: switcher-style Preview/Program UI is incomplete.' }
$cgControllerHasBusAssignment = $cgControllerCode -match 'Bus\s*=\s*bus'
$cgControllerHasPreviewPlay = $cgControllerCode -match 'RouteAsync\(\s*"PLAY"\s*,\s*"PREVIEW"\s*(?:,|\))'
$cgControllerHasProgramPlay = $cgControllerCode -match 'RouteAsync\(\s*"PLAY"\s*,\s*"PROGRAM"\s*(?:,|\))'
if (-not $cgControllerHasBusAssignment -or -not $cgControllerHasPreviewPlay -or -not $cgControllerHasProgramPlay) { throw 'CG bus guard failed: controller routing does not separate PREVIEW and PROGRAM.' }
if ($cgControllerCode -notmatch '_previewSnapshot' -or $cgControllerCode -notmatch 'projectOverride' -or $cgControllerCode -notmatch 'TakePreviewToProgramAsync' -or $cgControllerCode -notmatch 'RouteAsync\(\s*"TAKE"\s*,\s*"PROGRAM"\s*,\s*_previewSnapshot') { throw 'CG bus guard failed: TAKE must use the latched PREVIEW state rather than the current selector.' }
if ($mainVmText -notmatch 'PreviewCgProject' -or $mainVmText -notmatch 'bus == "PREVIEW"') { throw 'CG bus guard failed: playout compositor does not maintain an independent preview CG state.' }
if ($mainVmText -notmatch 'command\.Project\.OnAir = true;[\s\S]*PreviewCgProject = command\.Project') { throw 'CG bus guard failed: PREVIEW project is not render-enabled for the compositor.' }
if ($mainVmText -notmatch '_previewCgCompositor' -or $mainVmText -notmatch '_previewCgCompositor\.Composite') { throw 'CG bus guard failed: PREVIEW and PROGRAM must not share the same stateful compositor instance.' }
$localCgBus = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\LocalCgCommandBus.cs') -Raw
if ($mainVmText -notmatch 'StartLocalCgCommandWatcher' -or $cgControllerCode -notmatch 'LocalCgCommandBus\.Publish' -or $localCgBus -notmatch 'LocalCg') { throw 'CG bus guard failed: same-workstation direct CG transport is missing.' }
$legacyCgRouting = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgChannelRoutingWindow.xaml.cs') -Raw
$channelControllerRouting = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\ChannelControllerWindow.xaml.cs') -Raw
if ($legacyCgRouting -notmatch 'SendAsync\("PLAY", "PREVIEW"\)' -or $legacyCgRouting -notmatch 'Bus = bus') { throw 'CG bus guard failed: CG routing dialog can cross-route preview onto program.' }
if ($channelControllerRouting -notmatch 'RouteAsync\("PLAY", "PREVIEW"\)' -or $channelControllerRouting -notmatch 'Bus = bus') { throw 'CG bus guard failed: Channel Controller can cross-route preview onto program.' }
Write-Host '  CG Controller, legacy CG routing and Channel Controller keep PREVIEW and PROGRAM buses independent.' -ForegroundColor Green

Write-Host 'Checking connected standalone-app / demo / routing guards...' -ForegroundColor Cyan
$sharedTheme = Get-Content $sharedThemePath -Raw
$launcherText = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\StandaloneAppLauncher.cs') -Raw
$mainWindowCode = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\MainWindow.xaml.cs') -Raw
$inputXamlText = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\InputSourceWindow.xaml') -Raw
$inputCodeText = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\InputSourceWindow.xaml.cs') -Raw
$controllerCode = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\ChannelControllerWindow.xaml.cs') -Raw
$controllerXaml = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\ChannelControllerWindow.xaml') -Raw
$cgRoutingCode = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgChannelRoutingWindow.xaml.cs') -Raw
$demoFactory = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\DemoDataFactory.cs') -Raw
$channelManager = Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\ChannelManagerWindow.xaml'
if ($sharedTheme -notmatch 'PART_EditableTextBox') { throw 'Input selection guard failed: shared editable ComboBox template is missing.' }
if ($launcherText -notmatch 'Kashtrix\.CGEditor\.exe' -or $launcherText -notmatch 'Kashtrix\.ChannelController\.exe' -or $launcherText -notmatch 'Kashtrix\.CGController\.exe' -or $launcherText -notmatch 'Kashtrix\.FileManager\.exe' -or $launcherText -notmatch 'Kashtrix\.QCController\.exe' -or $launcherText -notmatch 'Kashtrix\.MAM\.exe') { throw 'Standalone app guard failed: module executable launcher map is incomplete.' }
if ($launcherText -notmatch 'suite-apps\.json' -or $launcherText -notmatch 'WaitForExit\(900\)') { throw 'Standalone app guard failed: deterministic suite manifest / startup-failure detection is missing.' }
if ($mainWindowCode -match 'new\s+(SettingsWindow|SchedulerWindow|CgEditorWindow|PlaylistEditorWindow|MultiviewWindow|ChannelControllerWindow|CgControllerWindow|FileManagerWindow|QcControllerWindow|MediaAssetManagementWindow)') { throw 'Standalone app guard failed: Playout is still opening a module as an owned in-process window.' }
if ($inputXamlText -notmatch 'DEMO SOURCE' -or $inputCodeText -notmatch 'Selected video:') { throw 'Input guard failed: demo source / visible selected-device status is missing.' }
if ($controllerCode -notmatch 'HashSet<string> _targetIds' -or $cgRoutingCode -notmatch 'HashSet<string> _targetIds') { throw 'CG routing guard failed: target selection persistence is missing.' }
if ($controllerXaml -notmatch 'MANAGE CHANNELS' -or -not (Test-Path $channelManager)) { throw 'Channel Controller guard failed: channel management UI is missing.' }
if ($demoFactory -notmatch 'CreatePlaylist' -or $demoFactory -notmatch 'CreateSchedules' -or $demoFactory -notmatch 'CreateChannels') { throw 'Demo-data guard failed: playlist/schedule/channel demo factories are incomplete.' }
foreach ($standaloneName in $standaloneProjects) {
    $appXamlPath = Join-Path $ProjectRoot ('src\' + $standaloneName + '\App.xaml')
    $appXamlText = Get-Content $appXamlPath -Raw
    if ($appXamlText -notmatch 'ShutdownMode="OnMainWindowClose"') { throw "Standalone app guard failed: $standaloneName must exit independently when its main window closes." }
}
$visibleCoreTerms = Get-ChildItem (Join-Path $ProjectRoot 'src') -Recurse -Filter *.xaml | Select-String -Pattern 'FFmpeg|CefSharp|Chromium|avformat|libav' -CaseSensitive:$false
if ($visibleCoreTerms) {
    $visibleCoreTerms | ForEach-Object { Write-Host ("  Internal engine term: {0}:{1} {2}" -f $_.Path, $_.LineNumber, $_.Line.Trim()) -ForegroundColor Red }
    throw 'UI branding guard failed: internal media-engine names are visible in XAML.'
}
Write-Host '  Standalone modules, visible device selection, demo data, channel management and persistent CG target routing guards passed.' -ForegroundColor Green

Write-Host 'Checking FIX14C resolver / CG-preview / media-scan guards...' -ForegroundColor Cyan
$ytResolverText = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\YtDlpResolver.cs') -Raw
$ffmpegInputText = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Ffmpeg\FfmpegInput.cs') -Raw
$mediaLibraryText = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\MediaLibraryService.cs') -Raw
$cgControllerXamlText = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgControllerWindow.xaml') -Raw
if ($ytResolverText -notmatch 'bv\*\+ba/b' -or $ytResolverText -notmatch 'AudioUrl') { throw 'FIX14C guard failed: split video/audio yt-dlp resolution is missing.' }
if ($ffmpegInputText -notmatch 'AlternateAudioUrl') { throw 'FIX14C guard failed: separate web-audio URL is not routed into the audio decoder.' }
if ($mediaLibraryText -notmatch 'ReparsePoint' -or $mediaLibraryText -notmatch 'maxItems') { throw 'FIX14C guard failed: fault-tolerant drive/folder scanning is missing.' }
if ($cgControllerXamlText -notmatch 'x:Name="PreviewCgImage"' -or $cgControllerCodeFinal -notmatch 'RenderClock_Tick') { throw 'FIX14C guard failed: actual animated CG confidence preview is missing.' }
Write-Host '  Split A/V web resolver, multi-input monitors, safe media scanning and animated CG confidence rendering guards passed.' -ForegroundColor Green

Write-Host 'Checking FIX17 weather / CG compile-safety guards...' -ForegroundColor Cyan
$cgData17 = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\CgDataSourceService.cs') -Raw
$cgComp17 = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\CgCompositor.cs') -Raw
$cgUnique17 = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\CgUniqueDemoFactory.cs') -Raw
$cgEditor17 = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml') -Raw
if ($cgData17 -notmatch 'OPENWEATHER' -or $cgData17 -notmatch 'ParseOneCallDaily' -or $cgData17 -notmatch 'ParseFiveDayForecast') { throw 'FIX17 guard failed: OpenWeather 7-day/fallback data source is incomplete.' }
if ($cgComp17 -notmatch 'WEATHERICON' -or $cgComp17 -notmatch 'DrawWeatherIcon' -or $cgComp17 -notmatch 'GlowRadius' -or $cgComp17 -notmatch 'MotionBlurSamples') { throw 'FIX17 guard failed: animated weather icon / text effects are incomplete.' }
if ($cgUnique17 -notmatch 'Nepali Shiny Fullframe' -or $cgUnique17 -notmatch 'Six Bullet Slate' -or $cgUnique17 -notmatch 'Seven Day Forecast') { throw 'FIX17 guard failed: reference-style and weather demos are missing.' }
if ($cgEditor17 -notmatch 'AddWeatherData_Click' -or $cgEditor17 -notmatch 'Credential env var' -or $cgEditor17 -match '<CheckBox[^>]+TextWrapping=') { throw 'FIX17 guard failed: CG weather UI or CheckBox compile fix is incomplete.' }
Write-Host '  FIX17 OpenWeather, animated weather icons, reference graphics and CheckBox TextWrapping compile guard passed.' -ForegroundColor Green

Write-Host 'Checking FIX17D runtime-resource / native-demo / block-filter guards...' -ForegroundColor Cyan
$theme17d = Get-Content (Join-Path $ProjectRoot 'src\Kashtrix.Shared\KashtrixTheme.xaml') -Raw
$mainXaml17d = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\MainWindow.xaml') -Raw
$mainCode17d = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\MainWindow.xaml.cs') -Raw
$cgEditorXaml17d = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml') -Raw
$cgEditorCode17d = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml.cs') -Raw
$cgDemo17d = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\CgUniqueDemoFactory.cs') -Raw
if ($theme17d -match 'x:Key="GoodBrush" Color="\{StaticResource Green\}"') { throw 'FIX17D guard failed: critical base palette still depends on startup StaticResource color indirection.' }
if ($mainXaml17d -match 'HierarchicalDataTemplate[^>]+RundownBlockNode' -or $mainXaml17d -notmatch 'RundownBlockList_SelectionChanged' -or $mainCode17d -notmatch 'ApplyPlaylistViewFilter') { throw 'FIX17D guard failed: block-only navigator / right-side rundown filter is incomplete.' }
if ($cgEditorXaml17d -notmatch 'RemainingText' -or $cgEditorCode17d -notmatch 'TimelineXToTime' -or $cgEditorCode17d -notmatch '_dragMode == "playhead"' -or $cgEditorCode17d -notmatch 'Tag = "PLAYHEAD"' -or $cgEditorCode17d -notmatch 'Stroke = Brushes.Red') { throw 'FIX17D guard failed: draggable CG timeline playhead/timer is incomplete.' }
if ($cgDemo17d -match '\.HtmlSources\.Add' -or $cgDemo17d -notmatch 'private static CgLayer Shape' -or $cgDemo17d -notmatch 'private static CgLayer Text') { throw 'FIX17D guard failed: built-in demos must remain native editable layers.' }
Write-Host '  Runtime palette, block-only filtering, draggable CG timeline and native reference demos passed.' -ForegroundColor Green

Write-Host 'Checking project-level RuntimeIdentifiers...' -ForegroundColor Cyan
$projectMap = @(
    @{ Name = 'Kashtrix.Playout'; Path = 'src\BroadcastPlayout.App\BroadcastPlayout.App.csproj' },
    @{ Name = 'Kashtrix.CGEditor'; Path = 'src\Kashtrix.CGEditor\Kashtrix.CGEditor.csproj' },
    @{ Name = 'Kashtrix.Scheduler'; Path = 'src\Kashtrix.Scheduler\Kashtrix.Scheduler.csproj' },
    @{ Name = 'Kashtrix.PlaylistEditor'; Path = 'src\Kashtrix.PlaylistEditor\Kashtrix.PlaylistEditor.csproj' },
    @{ Name = 'Kashtrix.Settings'; Path = 'src\Kashtrix.Settings\Kashtrix.Settings.csproj' },
    @{ Name = 'Kashtrix.Multiview'; Path = 'src\Kashtrix.Multiview\Kashtrix.Multiview.csproj' },
    @{ Name = 'Kashtrix.ChannelController'; Path = 'src\Kashtrix.ChannelController\Kashtrix.ChannelController.csproj' },
    @{ Name = 'Kashtrix.CGController'; Path = 'src\Kashtrix.CGController\Kashtrix.CGController.csproj' },
    @{ Name = 'Kashtrix.FileManager'; Path = 'src\Kashtrix.FileManager\Kashtrix.FileManager.csproj' },
    @{ Name = 'Kashtrix.QCController'; Path = 'src\Kashtrix.QCController\Kashtrix.QCController.csproj' },
    @{ Name = 'Kashtrix.IngestServer'; Path = 'src\Kashtrix.IngestServer\Kashtrix.IngestServer.csproj' },
    @{ Name = 'Kashtrix.MAM'; Path = 'src\Kashtrix.MAM\Kashtrix.MAM.csproj' },
    @{ Name = 'Kashtrix.NRCS'; Path = 'src\Kashtrix.NRCS\Kashtrix.NRCS.csproj' },
    @{ Name = 'Kashtrix.Prompter'; Path = 'src\Kashtrix.Prompter\Kashtrix.Prompter.csproj' },
    @{ Name = 'Kashtrix.HAController'; Path = 'src\Kashtrix.HAController\Kashtrix.HAController.csproj' },
    @{ Name = 'Kashtrix.ApiGateway'; Path = 'src\Kashtrix.ApiGateway\Kashtrix.ApiGateway.csproj' },
    @{ Name = 'Kashtrix.OutputEngine'; Path = 'src\Kashtrix.OutputEngine\Kashtrix.OutputEngine.csproj' }
)
foreach ($entry in $projectMap) {
    $projectPath = Join-Path $ProjectRoot $entry.Path
    if (-not (Test-Path $projectPath)) { throw "Project missing: $($entry.Name) ($projectPath)" }
    $text = Get-Content $projectPath -Raw
    if ($text -notmatch '<RuntimeIdentifier>win-x64</RuntimeIdentifier>') {
        throw "Runtime guard failed: $($entry.Name) must declare RuntimeIdentifier=win-x64 at project level."
    }
}
Write-Host '  All sixteen projects declare win-x64 at project level.' -ForegroundColor Green

Write-Host 'Checking FIX16 CG data/keyframe/unique-demo guards...' -ForegroundColor Cyan
$cgModels16 = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Models\CgModels.cs') -Raw
$cgData16 = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\CgDataSourceService.cs') -Raw
$cgAnim16 = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\CgAnimationEngine.cs') -Raw
$cgUnique16 = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\CgUniqueDemoFactory.cs') -Raw
$cgEditor16 = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml') -Raw
if ($cgModels16 -notmatch 'CgDataSource' -or $cgModels16 -notmatch 'CgKeyframe' -or $cgModels16 -notmatch 'CgGroup' -or $cgModels16 -notmatch 'SequenceAdvanceDataItem') { throw 'FIX16 guard failed: CG data/group/keyframe models are incomplete.' }
if ($cgData16 -notmatch 'URLJSON' -or $cgData16 -notmatch 'RSS' -or $cgData16 -notmatch 'XLSX' -or $cgData16 -notmatch 'ParseText' -or $cgData16 -notmatch 'ResolveSequenceFrame') { throw 'FIX16 guard failed: CG data-source or PNG-sequence runtime is incomplete.' }
if ($cgAnim16 -notmatch 'elastic' -or $cgAnim16 -notmatch 'bounce' -or $cgAnim16 -notmatch 'back' -or $cgAnim16 -notmatch 'steps') { throw 'FIX16 guard failed: GSAP-style native easing vocabulary is incomplete.' }
if ($cgUnique16 -notmatch 'for \(var i = 0; i < Names\.Length; i\+\+\)' -or $cgUnique16 -notmatch 'Election Flash' -or $cgUnique16 -notmatch 'RSS Story Rotator') { throw 'FIX16 guard failed: 200-project unique CG catalog is incomplete.' }
if ($cgEditor16 -notmatch 'DATA / GROUPS' -or $cgEditor16 -notmatch 'ANIMATION / KEYFRAMES' -or $cgEditor16 -notmatch 'ADD @ PLAYHEAD' -or $cgEditor16 -notmatch 'IMAGE SEQUENCE') { throw 'FIX16 guard failed: CG Editor data/keyframe/sequence authoring UI is incomplete.' }
Write-Host '  FIX16 data sources, grouped keyframes, sequence/data cycling, native easing and 200 unique CG demo guards passed.' -ForegroundColor Green

Write-Host 'Checking WPF temporary-project recursion guard...' -ForegroundColor Cyan
$directoryPropsText = Get-Content (Join-Path $ProjectRoot 'Directory.Build.props') -Raw
$directoryTargetsText = Get-Content (Join-Path $ProjectRoot 'Directory.Build.targets') -Raw
if ($directoryPropsText -notmatch 'IsWpfTempProject' -or $directoryPropsText -notmatch '_wpftmp\.csproj') {
    throw 'WPF temp-project guard failed: Directory.Build.props must identify generated *_wpftmp.csproj projects.'
}
if ($directoryPropsText -notmatch 'IsKashtrixWrapperProject' -or $directoryTargetsText -notmatch '\$\(IsKashtrixWrapperProject\).{0,40}true') {
    throw 'WPF temp-project guard failed: EnsureKashtrixSharedProjectBuilt must be limited to exact standalone wrapper projects.'
}
if ($directoryTargetsText -notmatch '\$\(IsWpfTempProject\).{0,40}true' -or $directoryTargetsText -notmatch 'KashtrixSharedBuildInProgress=true') {
    throw 'WPF temp-project guard failed: recursive shared-project build is not protected from WPF temporary target assemblies.'
}
if ($directoryPropsText -notmatch 'BaseIntermediateOutputPath[^>]+IsWpfTempProject') {
    throw 'WPF temp-project guard failed: Directory.Build.props must not overwrite the temporary project intermediate path.'
}
Write-Host '  WPF *_wpftmp recursive-build guard passed.' -ForegroundColor Green

Write-Host 'Checking FIX18 operator/output/ingest guards...' -ForegroundColor Cyan
$mainXaml18 = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\MainWindow.xaml') -Raw
$mainCode18 = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\MainWindow.xaml.cs') -Raw
$settings18 = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\SettingsWindow.xaml') -Raw
$ndi18 = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Outputs\NdiSender.cs') -Raw
$audioRouter18 = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Ffmpeg\AudioChannelRouter.cs') -Raw
$audioDecoder18 = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Ffmpeg\AudioDecoder.cs') -Raw
$cgCode18 = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml.cs') -Raw
$ingestXaml18 = Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\IngestServerWindow.xaml'
$ingestCode18 = Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\IngestServerWindow.xaml.cs'
$setupCmd18 = Join-Path $ProjectRoot 'SETUP-AND-BUILD.cmd'
$setupPs18 = Join-Path $ProjectRoot 'tools\Setup-All.ps1'
$debugPs18 = Join-Path $ProjectRoot 'tools\Debug-Outputs.ps1'
if ($mainXaml18 -match 'Text="\{Binding DurationText\}"') { throw 'FIX18 guard failed: DurationText display binding still lacks Mode=OneWay.' }
if ($mainCode18 -notmatch 'systemRoot' -or $mainCode18 -notmatch 'MediaRootCombo\.SelectedItem') { throw 'FIX18 guard failed: automatic Windows drive media browser startup is missing.' }
if ($settings18 -notmatch 'IsChecked="\{Binding NdiEnabled, Mode=TwoWay\}"' -or $settings18 -notmatch 'IsChecked="\{Binding DeckLinkEnabled, Mode=TwoWay\}"') { throw 'FIX18 guard failed: NDI/DeckLink outputs are not operator-selectable.' }
if ($ndi18 -notmatch 'NDIlib_send_get_no_connections' -or $ndi18 -notmatch 'VideoFramesSent' -or $ndi18 -notmatch 'DebugSummary') { throw 'FIX18 guard failed: NDI sender diagnostics are incomplete.' }
if ($audioRouter18 -notmatch 'Ch 7/8' -or $audioRouter18 -notmatch 'Mono Sum' -or $audioDecoder18 -notmatch 'AudioChannelRouter\.ToStereo') { throw 'FIX18 guard failed: multichannel audio remapping is incomplete.' }
if ($cgCode18 -notmatch 'PreviewCanvas_MouseMove' -or $cgCode18 -notmatch 'Window_PreviewKeyDown' -or $cgCode18 -notmatch 'AddPreviewHandle') { throw 'FIX18 guard failed: CG canvas mouse/keyboard transform controls are incomplete.' }
if (-not (Test-Path $ingestXaml18) -or -not (Test-Path $ingestCode18) -or $solutionText -notmatch 'Kashtrix\.IngestServer') { throw 'FIX18 guard failed: Ingest Server application is missing.' }
if (-not (Test-Path $setupCmd18) -or -not (Test-Path $setupPs18) -or -not (Test-Path $debugPs18)) { throw 'FIX18 guard failed: one-click setup/output diagnostics tools are missing.' }
Write-Host '  FIX18 manual outputs, NDI diagnostics, channel routing, CG canvas transforms, auto media browser, Ingest Server and one-click setup guards passed.' -ForegroundColor Green

Write-Host 'Checking MAM startup initialization guard...' -ForegroundColor Cyan
$mamWindow = Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\MediaAssetManagementWindow.xaml.cs'
$mamSource = Get-Content -Raw $mamWindow
if ($mamSource -notmatch 'AssetsView\s*=\s*CollectionViewSource\.GetDefaultView\(Assets\);[\s\S]{0,800}InitializeComponent\(\)') { throw 'MAM startup guard failed: AssetsView must be initialized before InitializeComponent.' }
if ($mamSource -notmatch 'private bool _uiReady;' -or $mamSource -notmatch 'if \(!_uiReady\) return;') { throw 'MAM startup guard failed: initialization-time UI events are not gated.' }
Write-Host '  MAM constructor/event initialization guard passed.' -ForegroundColor Green

Write-Host 'Checking FIX19 ingest / NDI discovery / MAM / operator-time guards...' -ForegroundColor Cyan
$ndiDiscovery19 = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\NdiDiscoveryService.cs') -Raw
$ndiReceiver19 = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Outputs\NdiVideoReceiver.cs') -Raw
$ingest19 = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\IngestServerWindow.xaml.cs') -Raw
$mamXaml19 = Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\MediaAssetManagementWindow.xaml'
$mamCode19 = Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\MediaAssetManagementWindow.xaml.cs'
$mamProject19 = Join-Path $ProjectRoot 'src\Kashtrix.MAM\Kashtrix.MAM.csproj'
$liveDebug19 = Join-Path $ProjectRoot 'tools\Live-Debug.ps1'
$liveDebugCmd19 = Join-Path $ProjectRoot 'LIVE-DEBUG.cmd'
$liveDebugText19 = Get-Content $liveDebug19 -Raw
if ($ndiDiscovery19 -notmatch 'TimeSpan\.FromSeconds\(10\)' -or $ndiDiscovery19 -notmatch 'NDIlib_find_create_v2') { throw 'FIX19 guard failed: 10-second native NDI discovery is incomplete.' }
if ($ndiReceiver19 -notmatch 'NDIlib_recv_create_v3' -or $ndiReceiver19 -notmatch 'NDIlib_recv_capture_v3') { throw 'FIX19 guard failed: native NDI video receive path is missing.' }
if ($ingest19 -notmatch 'RecordingRequested' -or $ingest19 -notmatch 'RECOVERING' -or $ingest19 -notmatch 'ScheduleRecorderRestart' -or $ingest19 -notmatch 'StartNativeNdiRecord' -or $ingest19 -notmatch 'PreviewSuspendedForRecord') { throw 'FIX19 guard failed: resilient ingest auto-restart/native NDI/exclusive-device record path is incomplete.' }
if (-not (Test-Path $mamXaml19) -or -not (Test-Path $mamCode19) -or -not (Test-Path $mamProject19) -or $solutionText -notmatch 'Kashtrix\.MAM') { throw 'FIX19 guard failed: Media Asset Management application is missing.' }
if (-not (Test-Path $liveDebug19) -or -not (Test-Path $liveDebugCmd19)) { throw 'FIX19 guard failed: continuous live debug tools are missing.' }
if ($liveDebugText19 -match 'PtrToStringUTF8') { throw 'FIX19 guard failed: live debugger uses a .NET Core-only Marshal API and is not Windows PowerShell 5.1 compatible.' }
if ($liveDebugText19 -notmatch 'Encoding.UTF8.GetString') { throw 'FIX19 guard failed: Windows PowerShell 5.1-compatible NDI UTF-8 decoding is missing.' }
if ($mainXaml18 -match '<TextBlock Text="LIVE" Foreground="White" FontWeight="Bold"') { throw 'FIX19 guard failed: PROGRAM monitor still shows the LIVE badge.' }
if ($mainXaml18 -notmatch 'Current Played Time' -or $mainXaml18 -notmatch 'Playlist Played Time' -or $mainXaml18 -notmatch 'Selected Remaining') { throw 'FIX19 guard failed: reference-style operator timing deck is incomplete.' }
Write-Host '  Resilient ingest, native NDI discovery/input, MAM, continuous diagnostics and reference timing deck guards passed.' -ForegroundColor Green

Write-Host 'Checking FIX19E continuous-program / on-air / multi-block guards...' -ForegroundColor Cyan
$engine19e = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Engine\PlayoutEngine.cs') -Raw
$mainVm19e = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\ViewModels\MainViewModel.cs') -Raw
$mainCode19e = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\MainWindow.xaml.cs') -Raw
$mainXaml19e = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\MainWindow.xaml') -Raw
if ($engine19e -notmatch 'InvokeSafely<T>' -or $engine19e -notmatch 'Task\.WhenAny\(audioTask, Task\.Delay\(750\)\)') { throw 'FIX19E guard failed: playout subscriber isolation / bounded audio-tail shutdown is missing.' }
if ($mainVm19e -notmatch 'private void EvaluateAutoCg\(double elapsedSeconds\)' -or $mainVm19e -notmatch 'EvaluateAutoCg[\s\S]{0,5000}Ui\(\(\) =>' -or $mainVm19e -notmatch 'usedNames[\s\S]{0,300}ToHashSet') { throw 'FIX19E guard failed: decoder-thread Auto-CG UI marshaling or repeatable unique block creation is missing.' }
if ($mainCode19e -notmatch '_pendingProgramFrame' -or $mainCode19e -notmatch 'DrainProgramFrame' -or $mainCode19e -notmatch '_programPresentScheduled') { throw 'FIX19E guard failed: WPF Program monitor frame coalescing is missing.' }
if ($mainXaml19e -notmatch 'Value="On Air"' -or $mainXaml19e -notmatch '#45D889' -or $mainXaml19e -notmatch 'Start a new block at the selected rundown item') { throw 'FIX19E guard failed: on-air row highlight or block creation control is missing.' }
Write-Host '  Continuous Program stability, coalesced confidence monitor, on-air row highlight and repeatable block creation guards passed.' -ForegroundColor Green

Write-Host 'Checking FIX22 EPG / per-item media / audit / multiview / CG / setup guards...' -ForegroundColor Cyan
$fix22FileManager = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\FileManagerWindow.xaml') -Raw
$fix22BaseSetup = Get-Content (Join-Path $ProjectRoot 'tools\Ensure-DirectShowBaseClasses.ps1') -Raw
$fix22ItemModel = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Models\PlaylistItem.cs') -Raw
$fix22ItemProps = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\PlaylistItemPropertiesWindow.xaml') -Raw
$fix22Epg = Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\EpgOutputSettingsWindow.xaml'
$fix22EpgGen = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\EpgGenerator.cs') -Raw
$fix22Audit = Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\AuditLogService.cs'
$fix22Multi = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\MultiviewWindow.xaml.cs') -Raw
$fix22MainVm = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\ViewModels\MainViewModel.cs') -Raw
$fix22CgValidator = Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\CgDemoValidator.cs'
if ($fix22FileManager -notmatch 'Binding SizeText, Mode=OneWay') { throw 'FIX22 guard failed: File Manager SizeText binding is not explicitly OneWay.' }
if ($fix22BaseSetup -notmatch '--strip-components=6' -or $fix22BaseSetup -notmatch 'DirectShow BaseClasses subtree only' -or $fix22BaseSetup -match 'System\.IO\.Compression\.ZipFile|(?m)^\s*Expand-Archive\b') { throw 'FIX22/FIX24 guard failed: long-path-safe DirectShow dependency bootstrap is missing.' }
if ($fix22ItemModel -notmatch 'AudioChannelMode' -or $fix22ItemModel -notmatch 'AudioDelayMs' -or $fix22ItemModel -notmatch 'AudioTrackIndex' -or $fix22ItemModel -notmatch 'ScaleMode' -or $fix22ItemModel -notmatch 'AspectRatioMode' -or $fix22ItemModel -notmatch 'InterlaceMode' -or $fix22ItemModel -notmatch 'DecoderPreference') { throw 'FIX22 guard failed: per-item media/audio properties are incomplete.' }
if ($fix22ItemProps -notmatch 'Mono to Stereo' -or $fix22ItemProps -notmatch 'GPU Preferred' -or $fix22ItemProps -notmatch 'Deinterlace') { throw 'FIX22 guard failed: per-item media property editor controls are incomplete.' }
if (-not (Test-Path $fix22Epg) -or $fix22EpgGen -notmatch 'BuildPreview' -or $fix22EpgGen -notmatch 'ProviderInfoName') { throw 'FIX22 guard failed: custom EPG output settings/preview are incomplete.' }
if (-not (Test-Path $fix22Audit) -or $fix22MainVm -notmatch 'AuditLogService\.Write\("OPERATOR_EVENT"') { throw 'FIX22 guard failed: append-only audit log wiring is incomplete.' }
if ($fix22Multi -notmatch 'Digital Clock' -or $fix22Multi -notmatch 'Analog Clock' -or $fix22Multi -notmatch 'System Info' -or $fix22Multi -notmatch 'ChannelControllerOperator' -or $fix22Multi -notmatch 'RunCgTile') { throw 'FIX22 guard failed: expanded multiview source types are incomplete.' }
if (-not (Test-Path $fix22CgValidator) -or $fix22MainVm -notmatch '_activeCgStartedUtc = DateTime\.UtcNow' -or $fix22MainVm -notmatch 'current\?\.IsCgEvent == true \? elapsed') { throw 'FIX22 guard failed: CG demo timeline reset/normalization is incomplete.' }
Write-Host '  FIX22 custom EPG, live/per-item media controls, audit log, multiview sources, CG demo timing and setup-recovery guards passed.' -ForegroundColor Green

Write-Host 'Checking FIX25 Virtual Output / bounded startup-smoke recovery guards...' -ForegroundColor Cyan
$fix25VoHeader = Get-Content (Join-Path $ProjectRoot 'native\Kashtrix.VirtualOutput\KashtrixVirtualOutput.h') -Raw
$fix25VoCpp = Get-Content (Join-Path $ProjectRoot 'native\Kashtrix.VirtualOutput\KashtrixVirtualOutput.cpp') -Raw
$fix25BaseSetup = Get-Content (Join-Path $ProjectRoot 'tools\Ensure-DirectShowBaseClasses.ps1') -Raw
$fix25App = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\App.xaml.cs') -Raw
if ($fix25VoHeader -notmatch 'NOMINMAX' -or $fix25VoHeader -notmatch 'DECLARE_IUNKNOWN') { throw 'FIX25 guard failed: Virtual Output modern C++/COM compatibility declarations are missing.' }
if ($fix25VoCpp -notmatch 'PROGRAM_VIDEO_PIN_NAME' -or $fix25VoCpp -notmatch 'FILTER_FRIENDLY_NAME') { throw 'FIX25 guard failed: DirectShow registration still relies on const string literals for legacy LPWSTR fields.' }
if ($fix25BaseSetup -notmatch 'Repair-BaseClassesForModernMsvc' -or $fix25BaseSetup -notmatch 'CTransInPlaceFilter::Copy' -or $fix25BaseSetup -notmatch 'CAggDirectDraw::~CAggDirectDraw') { throw 'FIX25 guard failed: modern MSVC repair for official DirectShow BaseClasses is missing.' }
if ($fix25App -notmatch 'if \(startupSmoke\)[\s\S]{0,900}StandaloneAppDiagnostics\.Ready\("Kashtrix\.Playout"\)[\s\S]{0,300}Shutdown\(0\)') { throw 'FIX25 guard failed: bounded Playout startup smoke path is missing.' }
Write-Host '  Virtual Output C++/BaseClasses compatibility and bounded Playout startup smoke guards passed.' -ForegroundColor Green

Write-Host 'Checking FIX26 Virtual Output linker / AX identity guards...' -ForegroundColor Cyan
$fix26VoHeader = Get-Content (Join-Path $ProjectRoot 'native\Kashtrix.VirtualOutput\KashtrixVirtualOutput.h') -Raw
$fix26VoCpp = Get-Content (Join-Path $ProjectRoot 'native\Kashtrix.VirtualOutput\KashtrixVirtualOutput.cpp') -Raw
$fix26VoDef = Get-Content (Join-Path $ProjectRoot 'native\Kashtrix.VirtualOutput\KashtrixVirtualOutput.def') -Raw
if ($fix26VoHeader -notmatch 'EXTERN_C const CLSID CLSID_KashtrixVirtualOutput' -or $fix26VoHeader -match 'DEFINE_GUID\(CLSID_KashtrixVirtualOutput') { throw 'FIX26 guard failed: Virtual Output CLSID header must be declaration-only.' }
if ($fix26VoCpp -notmatch 'EXTERN_C const CLSID CLSID_KashtrixVirtualOutput\s*=') { throw 'FIX26 guard failed: Virtual Output CLSID has no single storage definition; LNK2001 would return.' }
if ($fix26VoDef -notmatch 'LIBRARY\s+"?KashtrixVirtualOutput\.ax"?') { throw 'FIX26 guard failed: module definition does not identify the .ax output.' }
$fix26InstallCmd = Get-Content (Join-Path $ProjectRoot 'INSTALL-VIRTUAL-OUTPUT.cmd') -Raw
if ($fix26InstallCmd -notmatch 'Setup-VirtualOutput\.ps1" -ForceRebuild') { throw 'FIX26 guard failed: installer does not force rebuilding the current .ax source.' }
Write-Host '  DirectShow CLSID storage definition and .ax module identity guards passed.' -ForegroundColor Green

Write-Host 'Checking FIX27 operator audio / complete-feature guards...' -ForegroundColor Cyan
$fix27Vm = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\ViewModels\MainViewModel.cs') -Raw
$fix27MainXaml = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\MainWindow.xaml') -Raw
$fix27SettingsXaml = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\SettingsWindow.xaml') -Raw
$fix27AudioXaml = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\AudioMixerWindow.xaml') -Raw
$fix27Props = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\PlaylistItemPropertiesWindow.xaml') -Raw
$fix27Item = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Models\PlaylistItem.cs') -Raw
$fix27Db = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\BroadcastDatabase.cs') -Raw
$fix27Ingest = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\IngestServerWindow.xaml.cs') -Raw
$fix27Multi = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\MultiviewWindow.xaml.cs') -Raw
$fix27CgEditor = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml.cs') -Raw
$fix27CgController = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgControllerWindow.xaml.cs') -Raw
$fix27CgControllerXaml = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgControllerWindow.xaml') -Raw
if ($fix27Vm -notmatch '_engine\.AudioMeterFrame \+= OnAudioMeter' -or $fix27Vm -notmatch 'OnAudioMeter\(AudioChunk chunk\)' -or $fix27Vm -notmatch 'MeasureStereoPcm16\(chunk\.Pcm16Stereo48k\)' -or $fix27Vm -notmatch '_engine\.AudioMeterFrame -= OnAudioMeter') { throw 'FIX28 guard failed: Program audio meters are not wired to the raw pre-item/pre-master meter bus.' }
if ($fix27AudioXaml -notmatch 'PRE-MASTER-FADER' -or $fix27AudioXaml -notmatch 'ACTIVE ITEM PROPERTIES') { throw 'FIX27 guard failed: Audio Mixer operator meter/item controls are incomplete.' }
if ($fix27MainXaml -notmatch 'FontSize="{Binding PlaylistFontSize}"' -or $fix27SettingsXaml -notmatch 'PLAYLIST FONT SIZE' -or $fix27Item -notmatch 'RowTextColor' -or $fix27Props -notmatch 'Row text color') { throw 'FIX27 guard failed: playlist font/row/text-color controls are incomplete.' }
if ($fix27Db -notmatch 'item_json' -or $fix27Db -notmatch 'JsonSerializer.Serialize\(item, Json\)') { throw 'FIX27 guard failed: complete per-item properties are not persisted in rundown recovery.' }
if ($fix27MainXaml -notmatch 'EPG / Auto Graphics Output' -or $fix27MainXaml -notmatch 'Audit Log') { throw 'FIX27 guard failed: EPG/audit operator shortcuts are missing.' }
if ($fix27Ingest -notmatch 'XDCAM HD422 MXF' -or $fix27Ingest -notmatch 'DV PAL AVI' -or $fix27Ingest -notmatch 'ProRes 422 HQ MOV' -or $fix27Ingest -notmatch 'prefer a Windows/vendor hardware') { throw 'FIX27 guard failed: professional ingest recording preset catalog/hardware path is incomplete.' }
if ($fix27Multi -notmatch '_vm\.InputSources\.Where\(x=>x\.IsLiveSource\)\.Take\(12\)' -or $fix27Multi -notmatch 'NdiDiscoveryService\.ScanAsync' -or $fix27Multi -notmatch 'channels\.Take\(16\)' -or $fix27Multi -match '_vm\.Playlist\.Take\(' -or $fix27Multi -match '_vm\.CgProjects\.Take\(') { throw 'FIX27 guard failed: Multiview must show live inputs/NDI/channel outputs without rundown or CG-catalog auto tiles.' }
if ($fix27CgEditor -notmatch 'Stopwatch _playbackClock' -or $fix27CgEditor -notmatch 'first PLAY must') { throw 'FIX27 guard failed: CG Editor real-time animation clock is missing.' }
if ($fix27CgController -notmatch 'BuildFullProjectSnapshot' -or $fix27CgController -match 'BuildSingleLayerProject' -or $fix27CgController -notmatch 'Layers = source.Layers' -or $fix27CgController -notmatch 'Groups = source.Groups' -or $fix27CgController -notmatch 'DataSources = source.DataSources' -or $fix27CgController -notmatch 'HtmlSources = source.HtmlSources' -or $fix27CgControllerXaml -notmatch 'complete selected CG project as one composition') { throw 'FIX27 guard failed: CG Controller PLAY must route the complete project, not a single selected layer.' }
$iconProjects = @('Kashtrix.CGController','Kashtrix.CGEditor','Kashtrix.ChannelController','Kashtrix.FileManager','Kashtrix.IngestServer','Kashtrix.MAM','Kashtrix.Multiview','Kashtrix.PlaylistEditor','Kashtrix.QCController','Kashtrix.Scheduler','Kashtrix.Settings','Kashtrix.NRCS','Kashtrix.Prompter','Kashtrix.HAController','Kashtrix.ApiGateway')
foreach ($iconProject in $iconProjects) { $iconCsproj = Get-Content (Join-Path $ProjectRoot ("src\$iconProject\$iconProject.csproj")) -Raw; if ($iconCsproj -notmatch '<ApplicationIcon>') { throw "FIX27 guard failed: executable icon missing for $iconProject." } }
Write-Host '  Pre-fader audio metering, playlist appearance, EPG/audit access, ingest presets, multiview expansion, whole-project CG playback, CG timing and executable icons passed.' -ForegroundColor Green

Write-Host 'Checking FIX28 complete integration guards...' -ForegroundColor Cyan
$fix28Engine = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Engine\PlayoutEngine.cs') -Raw
$fix28Settings = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\SettingsWindow.xaml') -Raw
$fix28SettingsCs = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\SettingsWindow.xaml.cs') -Raw
$fix28Launcher = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\ProgramLauncherWindow.xaml') -Raw
$fix28Main = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\MainWindow.xaml') -Raw
$fix28BuildVo = Get-Content (Join-Path $ProjectRoot 'tools\Build-VirtualOutput.ps1') -Raw
$fix28SetupVo = Get-Content (Join-Path $ProjectRoot 'tools\Setup-VirtualOutput.ps1') -Raw
if ($fix28Engine -notmatch 'AudioMeterFrame' -or $fix28Engine -notmatch 'EmitAudioMeter\(chunk\);[\s\S]{0,180}AudioMasterProcessor\.Apply') { throw 'FIX28 guard failed: audio PFL/raw meter event is not emitted before per-item gain/mute.' }
if ($fix28Settings -notmatch 'EPG / AUTO GRAPHICS' -or $fix28Settings -notmatch 'Settings\.Epg\.ProviderInfoName' -or $fix28Settings -notmatch 'OPEN FULL EPG EDITOR' -or $fix28SettingsCs -notmatch 'SaveEpg_Click') { throw 'FIX28 guard failed: EPG settings are not directly integrated into Kashtrix Settings.' }
if ($fix28Settings -notmatch 'REBUILD 200 CG DEMOS' -or $fix27Vm -notmatch 'RebuildCgDemoPack') { throw 'FIX28 guard failed: canonical 200 CG demo recovery control is missing.' }
if ($fix28Main -match '<Setter Property="Height" Value="27"') { throw 'FIX28 guard failed: hard-coded DataGridRow height still overrides the playlist row-height setting.' }
if ($fix28Launcher -notmatch 'kashtrix-cgcontroller.png' -or $fix28Launcher -notmatch 'kashtrix-filemanager.png' -or $fix28Launcher -notmatch 'kashtrix-channelcontroller.png' -or $fix28Launcher -notmatch 'kashtrix-settings.png') { throw 'FIX28 guard failed: Program Launcher does not expose the distinct application branding set.' }
$distinctIcons = @{
 'Kashtrix.CGController'='kashtrix-cgcontroller.ico'; 'Kashtrix.CGEditor'='kashtrix-cgeditor.ico'; 'Kashtrix.ChannelController'='kashtrix-channelcontroller.ico';
 'Kashtrix.FileManager'='kashtrix-filemanager.ico'; 'Kashtrix.IngestServer'='kashtrix-ingest.ico'; 'Kashtrix.MAM'='kashtrix-mam.ico'; 'Kashtrix.Multiview'='kashtrix-multiview.ico';
 'Kashtrix.PlaylistEditor'='kashtrix-playlisteditor.ico'; 'Kashtrix.QCController'='kashtrix-qc.ico'; 'Kashtrix.Scheduler'='kashtrix-scheduler.ico'; 'Kashtrix.Settings'='kashtrix-settings.ico';
 'Kashtrix.NRCS'='kashtrix-nrcs.ico'; 'Kashtrix.Prompter'='kashtrix-prompter.ico'; 'Kashtrix.HAController'='kashtrix-ha.ico'; 'Kashtrix.ApiGateway'='kashtrix-api.ico'
}
foreach ($pair in $distinctIcons.GetEnumerator()) {
 $projText = Get-Content (Join-Path $ProjectRoot ("src\{0}\{0}.csproj" -f $pair.Key)) -Raw
 if ($projText -notmatch [regex]::Escape($pair.Value) -or -not (Test-Path (Join-Path $ProjectRoot ("src\BroadcastPlayout.App\Branding\{0}" -f $pair.Value)))) { throw "FIX28 guard failed: distinct icon missing/wrong for $($pair.Key)." }
}
if ($fix28BuildVo -notmatch '/t:Rebuild' -or $fix28BuildVo -notmatch 'staleObj' -or $fix28SetupVo -notmatch 'DirectShow device enumeration' -or $fix28SetupVo -notmatch 'Kashtrix Playout Virtual Output.*FOUND') { throw 'FIX28 guard failed: Virtual Output clean-rebuild/enumeration verification is incomplete.' }
Write-Host '  Raw audio metering, direct EPG settings, row sizing, CG demo recovery, distinct app branding/launcher and Virtual Output clean-rebuild guards passed.' -ForegroundColor Green


Write-Host 'Checking FIX29 Windows path / Virtual Output build-cache guards...' -ForegroundColor Cyan
$fix29DemoFactory = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\CgUniqueDemoFactory.cs') -Raw
$fix29PlayoutProject = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\BroadcastPlayout.App.csproj') -Raw
$fix29DemoDir = Join-Path $ProjectRoot 'src\BroadcastPlayout.App\demos\ref\brk\primeBack'
$fix29LegacyDemoDir = Join-Path $ProjectRoot 'src\BroadcastPlayout.App\demos\reference\BreakingSequence\primeBack'
$fix29BuildVo = Get-Content (Join-Path $ProjectRoot 'tools\Build-VirtualOutput.ps1') -Raw
if (-not (Test-Path $fix29DemoDir)) { throw 'FIX29 guard failed: legacy BreakingSequence recovery assets are missing from the short Windows-safe path.' }
if (Test-Path $fix29LegacyDemoDir) { Write-Host '  Legacy FIX28 demo folder detected; csproj exclusion will ignore it.' -ForegroundColor DarkYellow }
if ($fix29PlayoutProject -notmatch 'None Remove="demos\\reference\\BreakingSequence\\\*\*\\\*"') { throw 'FIX29 guard failed: legacy long BreakingSequence folder is not excluded from MSBuild content.' }
$fix29FrameCount = @(Get-ChildItem $fix29DemoDir -Filter '*.png' -File -ErrorAction Stop).Count
if ($fix29FrameCount -lt 195) { throw "FIX29 guard failed: BreakingSequence frame set is incomplete ($fix29FrameCount/195)." }
if ($fix29BuildVo -notmatch 'NativeBuild\\VirtualOutput' -or $fix29BuildVo -notmatch '/p:IntDir=' -or $fix29BuildVo -notmatch '/p:OutDir=' -or $fix29BuildVo -notmatch 'TrackFileAccess=false' -or $fix29BuildVo -notmatch '/m:1') { throw 'FIX29 guard failed: Virtual Output is not isolated into the short LOCALAPPDATA native build cache.' }
Write-Host '  Short demo assets and isolated LOCALAPPDATA C++ build cache guards passed.' -ForegroundColor Green

Write-Host 'Checking FIX30 startup / operator / CG / Virtual Output guards...' -ForegroundColor Cyan
$mainXaml = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\MainWindow.xaml') -Raw
$mainCs = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\MainWindow.xaml.cs') -Raw
$vmCs = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\ViewModels\MainViewModel.cs') -Raw
$cgFactory = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\CgUniqueDemoFactory.cs') -Raw
$virtualSetup = Get-Content (Join-Path $ProjectRoot 'tools\Setup-VirtualOutput.ps1') -Raw
if ($mainXaml -notmatch 'CanUserAddRows="False"' -or $mainCs -notmatch 'IEditableCollectionView' -or $mainCs -notmatch 'ContextIdle') { throw 'FIX30 guard failed: startup playlist filter transaction recovery is missing.' }
if ($mainXaml -notmatch 'FULLSCREEN' -or $mainXaml -notmatch 'Header="PLAYBACK"' -or $mainXaml -notmatch 'Header="ADD EVENT"') { throw 'FIX30 guard failed: quick fullscreen or grouped rundown context menu is missing.' }
if ($vmCs -notmatch 'UpdateAudioMeters' -or $vmCs -notmatch 'lastRawMeterUtc' -or $vmCs -notmatch 'Auto-enable processing') { throw 'FIX30 guard failed: resilient audio metering or live video-processing enable path is missing.' }
if ($cgFactory -notmatch 'name\.StartsWith\("Sports"' -or $cgFactory -notmatch 'private static void Roll' -or $cgFactory -notmatch 'private static void Promo') { throw 'FIX30 guard failed: regenerated semantic 200-CG demo pack is incomplete.' }
if ($virtualSetup -notmatch 'RedirectStandardError' -or $virtualSetup -notmatch 'expected/non-fatal for list_devices') { throw 'FIX30 guard failed: DirectShow enumeration still risks PowerShell NativeCommandError.' }
Write-Host '  Startup filter recovery, fullscreen/grouped controls, live meters/FX, semantic CG demos and DirectShow verification guards passed.' -ForegroundColor Green


Write-Host 'Checking FIX31 locked-cache / isolated-build guards...' -ForegroundColor Cyan
$fix31Clean = Get-Content (Join-Path $ProjectRoot 'tools\Clean-BuildCache.ps1') -Raw
$fix31Props = Get-Content (Join-Path $ProjectRoot 'Directory.Build.props') -Raw
if ($fix31Clean -notmatch 'BuildSessions' -or $fix31Clean -notmatch 'KASHTRIX_BUILD_ROOT' -or $fix31Clean -notmatch 'Remove-KashtrixPathBestEffort' -or $fix31Clean -notmatch 'Locked stale caches are non-fatal') { throw 'FIX31 guard failed: locked-cache recovery / isolated build-session setup is incomplete.' }
if ($fix31Props -notmatch 'KASHTRIX_BUILD_ROOT' -or ($fix31Props -notmatch 'BuildSessions\\active' -and $fix31Props -notmatch 'BuildSessions\\Manual')) { throw 'FIX31 guard failed: Directory.Build.props does not consume the isolated setup build root.' }
if ([string]::IsNullOrWhiteSpace($env:KASHTRIX_BUILD_ROOT) -or $env:KASHTRIX_BUILD_ROOT -notmatch 'KashtrixPlayout\\BuildSessions\\') { throw 'FIX31 guard failed: current build is not running inside an isolated BuildSessions root.' }
Write-Host ('  Locked old caches are non-fatal; active isolated root: ' + $env:KASHTRIX_BUILD_ROOT) -ForegroundColor Green

Write-Host 'Checking FIX32 real live-debug capture guards...' -ForegroundColor Cyan
$fix32Live = Get-Content (Join-Path $ProjectRoot 'tools\Live-Debug.ps1') -Raw
$fix32Runtime = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\RuntimeDiagnosticsService.cs') -Raw
$fix32Standalone = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\StandaloneAppDiagnostics.cs') -Raw
$fix32Audit = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\AuditLogService.cs') -Raw
$fix32Vm = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\ViewModels\MainViewModel.cs') -Raw
if ($fix32Live -notmatch 'Capture-ProcessMetrics' -or $fix32Live -notmatch 'Capture-KashtrixLogs' -or $fix32Live -notmatch 'Capture-WindowsEvents' -or $fix32Live -notmatch 'Get-Counter.*GPU Engine' -or $fix32Live -notmatch 'Export-SessionBundle') { throw 'FIX32 guard failed: real live-debug recorder does not capture process/resources/logs/events/bundle.' }
if ($fix32Runtime -notmatch 'PROCESS_HEARTBEAT' -or $fix32Runtime -notmatch 'WriteException' -or $fix32Runtime -notmatch 'Runtime.*jsonl') { throw 'FIX32 guard failed: app-side runtime diagnostics journal is incomplete.' }
if ($fix32Standalone -notmatch 'RuntimeDiagnosticsService.Initialize' -or $fix32Audit -notmatch 'RuntimeDiagnosticsService.Write\("AUDIT"') { throw 'FIX32 guard failed: standalone/audit events are not connected to runtime diagnostics.' }
if ($fix32Vm -notmatch 'PLAYOUT_STATE' -or $fix32Vm -notmatch 'leftDb = AudioLeftDb' -or $fix32Vm -notmatch 'videoProcessing = new' -or $fix32Vm -notmatch 'virtualOutput = VirtualOutputEnabled') { throw 'FIX32 guard failed: playout live state snapshot is incomplete.' }
Write-Host '  Real session capture, operator/audit tailing, runtime state, resource metrics and crash-event collection guards passed.' -ForegroundColor Green

Write-Host 'Checking FIX33 audio-meter / broadcast-CG / automation-signaling guards...' -ForegroundColor Cyan
$fix33Theme = Get-Content (Join-Path $ProjectRoot 'src\Kashtrix.Shared\KashtrixTheme.xaml') -Raw
$fix33Main = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\MainWindow.xaml') -Raw
$fix33Engine = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Engine\PlayoutEngine.cs') -Raw
$fix33Gateway = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\AutomationGatewayService.cs') -Raw
$fix33GatewayUi = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\AutomationGatewayWindow.xaml') -Raw
$fix33Cg = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\CgUniqueDemoFactory.cs') -Raw
$fix33ProgramMeters = ($fix33Main -match 'BroadcastBlueAudioMeter' -and
                       $fix33Main -match 'AudioLeftLevel' -and
                       $fix33Main -match 'AudioRightLevel' -and
                       $fix33Main -match 'AudioMasterLevel' -and
                       $fix33Main -match 'MasterOutputVolumePercent')
if ($fix33Theme -notmatch 'MeterFill' -or $fix33Theme -notmatch 'ScaleY="\{Binding Value' -or -not $fix33ProgramMeters -or $fix33Main -match 'BroadcastBlueAudioMeter}" Height="148"') { throw 'FIX33 guard failed: deterministic Program meter rendering / stretch-height layout is incomplete.' }
if ($fix33Engine -notmatch 'case "DIALTONE"' -or $fix33Engine -notmatch '350, 440' -or $fix33Engine -notmatch 'PlayDiagnosticDtmfAsync' -or $fix33Engine -notmatch '1633') { throw 'FIX33 guard failed: DTMF/dial/line signaling generator is incomplete.' }
if ($fix33Gateway -notmatch 'MosLowerPort' -or $fix33Gateway -notmatch '= 10540' -or $fix33Gateway -notmatch 'MosUpperPort' -or $fix33Gateway -notmatch '= 10541' -or $fix33Gateway -notmatch 'TryTakeMosDocument' -or $fix33Gateway -notmatch 'SendMosAsync' -or $fix33GatewayUi -notmatch 'DTMF KEYPAD' -or $fix33GatewayUi -notmatch 'DIAL 350\+440' -or $fix33GatewayUi -notmatch 'MOS TEST') { throw 'FIX33 guard failed: MOS/TCP/DTMF operator gateway is incomplete.' }
if ($fix33Cg -notmatch 'private static void Music' -or $fix33Cg -notmatch 'private static void Leaderboard' -or $fix33Cg -notmatch '"Ellipse"' -or $fix33Cg -notmatch 'UseGradient') { throw 'FIX33 guard failed: redesigned semantic 200-CG demo pack is incomplete.' }
Write-Host '  Deterministic meters, aligned fader, semantic CG redesign, MOS/TCP and DTMF/dial-tone guards passed.' -ForegroundColor Green

Write-Host 'Checking FIX34 build-guard / Virtual Output registration regression guards...' -ForegroundColor Cyan
$fix34SetupVo = Get-Content (Join-Path $ProjectRoot 'tools\Setup-VirtualOutput.ps1') -Raw
if ($fix34SetupVo -match '\$LASTEXITCODE\s*-ne\s*0[^\r\n]*Virtual Output registration failed') { throw 'FIX34 guard failed: Setup-VirtualOutput still relies on stale LASTEXITCODE after a PowerShell registration script.' }
if ($fix34SetupVo -notmatch 'Register-VirtualOutput.ps1' -or $fix34SetupVo -notmatch 'Verified DirectShow CLSID' -or $fix34SetupVo -notmatch 'InprocServer32') { throw 'FIX34 guard failed: deterministic Virtual Output registration/CLSID verification is incomplete.' }
if ($mainUiText -notmatch 'AudioLeftLevel' -or $mainUiText -notmatch 'AudioRightLevel' -or $mainUiText -notmatch 'AudioMasterLevel' -or
    $mainUiText -notmatch 'AudioLeftDb' -or $mainUiText -notmatch 'AudioRightDb' -or $mainUiText -notmatch 'AudioMasterDb' -or
    $mainUiText -notmatch 'MasterOutputVolumePercent') { throw 'FIX34 guard failed: deterministic single Program audio meter/fader is missing.' }
Write-Host '  Backward-compatible meter guard and deterministic Virtual Output registration verification passed.' -ForegroundColor Green

Write-Host 'Checking FIX35 CG demo C# literal regression guard...' -ForegroundColor Cyan
$fix35Cg = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\CgUniqueDemoFactory.cs') -Raw
if ($fix35Cg -notmatch 'PRODUCER   KASHTRIX\\nDIRECTOR   BROADCAST TEAM') { throw 'FIX35 guard failed: CG roll multiline text is not encoded as escaped C# newlines.' }
if ($fix35Cg -match 'Text\("Roll",\s*"PRODUCER   KASHTRIX\r?\nDIRECTOR') { throw 'FIX35 guard failed: raw newline remains inside a normal C# string literal in CgUniqueDemoFactory.' }
Write-Host '  CG demo multiline string-literal compile regression guard passed.' -ForegroundColor Green

Write-Host 'Checking FIX36 broadcast workflow / CG-motion guards...' -ForegroundColor Cyan
# FIX38: always resolve FIX36 source guards from the repository root.  $project is the
# BroadcastPlayout.App.csproj FILE and must never be used as a directory base.
$fix36AppRoot = Join-Path $ProjectRoot 'src\BroadcastPlayout.App'
$fix36Item = Get-Content (Join-Path $fix36AppRoot 'Models\PlaylistItem.cs') -Raw
$fix36Vm = Get-Content (Join-Path $fix36AppRoot 'ViewModels\MainViewModel.cs') -Raw
$fix36Cg = Get-Content (Join-Path $fix36AppRoot 'Services\CgUniqueDemoFactory.cs') -Raw
$fix36CgEditor = Get-Content (Join-Path $fix36AppRoot 'Views\CgEditorWindow.xaml') -Raw
$fix36CgEditorCs = Get-Content (Join-Path $fix36AppRoot 'Views\CgEditorWindow.xaml.cs') -Raw
$fix36Settings = Get-Content (Join-Path $fix36AppRoot 'Views\SettingsWindow.xaml') -Raw
$fix36Output = Get-Content (Join-Path $fix36AppRoot 'Outputs\MultiNdiOutputManager.cs') -Raw
if ($fix36Item -notmatch 'DtmfSequence' -or $fix36Item -notmatch 'DialToneMilliseconds' -or $fix36Item -notmatch 'TcpCommand' -or $fix36Item -notmatch 'MosAction') { throw 'FIX36 guard failed: typed signaling properties are incomplete.' }
if ($fix36Vm -notmatch 'IsEditorialItem' -or $fix36Vm -notmatch 'AutoCgHoldSeconds' -or $fix36Vm -notmatch '_controllerProgramStack' -or $fix36Vm -notmatch '_controllerProgramStack\.Add\(incoming\)' -or $fix36Vm -notmatch '_controllerProgramStack\.Clear\(\)') { throw 'FIX36 guard failed: editorial Now/Next filtering, timed auto-CG or additive CG stack is incomplete.' }
if ($fix36Cg -match 'Name\s*=\s*\$"Demo\s+\{i\s*\+\s*1') { throw 'FIX36 guard failed: CG catalog still uses generic Demo ### project names.' }
if ($fix36Cg -match 'Demo Family Bug|Demo Family Label') { throw 'FIX36 guard failed: generic family badge remains in CG demos.' }
if ($fix36Cg -notmatch 'TextAnimationUnit' -or $fix36Cg -notmatch 'Tracking Reveal' -or $fix36Cg -notmatch 'Rise Cascade') { throw 'FIX36 guard failed: semantic text motion design is incomplete.' }
if ($fix36CgEditor -notmatch 'ONE-CLICK TEXT ANIMATION' -or $fix36CgEditorCs -notmatch 'TimelineKeyframeHit' -or $fix36CgEditorCs -notmatch '_dragKeyframe') { throw 'FIX36 guard failed: text presets or draggable keyframes are incomplete.' }
if ($fix36Settings -notmatch 'PlaylistDefaultRowColor' -or $fix36Settings -notmatch 'SelectedValue="{Binding Settings.Epg.DefaultProgramTemplate' -or $fix36Settings -notmatch 'DECKLINK') { throw 'FIX36 guard failed: global appearance, CG template dropdowns or hardware output settings are incomplete.' }
if ($fix36Output -notmatch 'DeckLinkWorker' -or $fix36Output -notmatch 'NdiWorker') { throw 'FIX36 guard failed: multiple NDI/hardware output workers are incomplete.' }
Write-Host '  Typed signaling, editorial timing, additive CG, semantic 200-demo motion, hardware outputs and global appearance guards passed.' -ForegroundColor Green

Write-Host 'Checking FIX37 verifier-regression guards...' -ForegroundColor Cyan
if ($mainVm19e -notmatch 'private void EvaluateAutoCg\(double elapsedSeconds\)' -or $mainVm19e -notmatch 'Ui\(\(\) =>' -or $mainVm19e -notmatch 'usedNames[\s\S]{0,300}ToHashSet') { throw 'FIX37 guard failed: current Auto-CG UI marshaling / unique-block implementation is missing.' }
if ($mainVm19e -match 'automatic CG timer fired') { Write-Host '  Legacy Auto-CG diagnostic phrase is present; semantic safety checks remain authoritative.' -ForegroundColor DarkYellow }
Write-Host '  Semantic Auto-CG UI-marshaling and repeatable-block guards passed without relying on obsolete log text.' -ForegroundColor Green

Write-Host 'Checking FIX38 verifier path-root regression guards...' -ForegroundColor Cyan
$fix38VerifierText = Get-Content $PSCommandPath -Raw
if ($fix38VerifierText -match 'Join-Path\s+\$project\s+''src\\BroadcastPlayout\.App') { throw 'FIX38 guard failed: a verifier source check is still rooted at the .csproj file path.' }
if ($fix38VerifierText -notmatch '\$fix36AppRoot\s*=\s*Join-Path\s+\$ProjectRoot') { throw 'FIX38 guard failed: FIX36 source checks are not anchored to ProjectRoot.' }
$obsoleteStackToken = '_controllerProgram' + 'CgStack'
if ($fix38VerifierText -match [regex]::Escape($obsoleteStackToken)) { throw 'FIX38 guard failed: obsolete CG stack identifier remains in verifier.' }
$fix38RequiredSources = @(
    'Models\PlaylistItem.cs',
    'ViewModels\MainViewModel.cs',
    'Services\CgUniqueDemoFactory.cs',
    'Views\CgEditorWindow.xaml',
    'Views\CgEditorWindow.xaml.cs',
    'Views\SettingsWindow.xaml',
    'Outputs\MultiNdiOutputManager.cs'
)
foreach ($relativeSource in $fix38RequiredSources) {
    $candidate = Join-Path $fix36AppRoot $relativeSource
    if (-not (Test-Path $candidate -PathType Leaf)) { throw "FIX38 guard failed: expected source file missing: $candidate" }
}
Write-Host '  Verifier source paths are repository-rooted and all FIX36 guard inputs exist.' -ForegroundColor Green

Write-Host 'Checking FIX39 C# compiler-regression guards...' -ForegroundColor Cyan
$fix39Cg = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\CgUniqueDemoFactory.cs') -Raw
$fix39MainCs = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\MainWindow.xaml.cs') -Raw
$fix39Vm = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\ViewModels\MainViewModel.cs') -Raw
if ($fix39Cg -match '\%\s*[0-9]+\s+switch') { throw 'FIX39 guard failed: modulo switch-expression receiver must be parenthesized before switch.' }
if ($fix39MainCs -match '_vm\.SavePlaylist\s*\(') { throw 'FIX39 guard failed: MainWindow calls nonexistent MainViewModel.SavePlaylist().' }
if ($fix39MainCs -notmatch 'item\.RefreshLegacyControlNotes\(\)') { throw 'FIX39 guard failed: typed control-event properties are not normalized after editing.' }
if ($fix39Vm -notmatch 'private void PersistPlaylist\(\)' -or $fix39Vm -notmatch 'OnPlaylistItemPropertyChanged') { throw 'FIX39 guard failed: automatic playlist persistence path is missing.' }
Write-Host '  Switch-expression precedence, typed-event persistence and ViewModel call-site compile regressions passed.' -ForegroundColor Green

Write-Host 'Checking FIX40 audio / timeline / inspector / demo-catalog guards...' -ForegroundColor Cyan
$fix40Engine = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Engine\PlayoutEngine.cs') -Raw
$fix40Theme = Get-Content (Join-Path $ProjectRoot 'src\Kashtrix.Shared\KashtrixTheme.xaml') -Raw
$fix40Editor = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml') -Raw
$fix40EditorCs = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml.cs') -Raw
$fix40Models = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Models\CgModels.cs') -Raw
$fix40Compositor = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\CgCompositor.cs') -Raw
$fix40Settings = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\SettingsWindow.xaml') -Raw
if ($fix40Engine -notmatch 'CancelCurrentSource_NoThrow' -or $fix40Engine -notmatch 'ObjectDisposedException') { throw 'FIX40 guard failed: disposed CancellationTokenSource race fix is missing.' }
if ($fix40Theme -notmatch 'MeterFill' -or $fix40Theme -notmatch 'LinearGradientBrush' -or $fix40Theme -notmatch 'BroadcastFaderVertical') { throw 'FIX40 guard failed: clean visible meter/fader theme is missing.' }
if ($fix40Models -notmatch 'CornerRadiusTopLeft' -or $fix40Models -notmatch 'CornerRadiusBottomRight' -or $fix40Compositor -notmatch 'AddRoundedRectangle') { throw 'FIX40 guard failed: independent CG corner radii are incomplete.' }
if ($fix40Editor -notmatch 'PICK COLOR' -or $fix40Editor -notmatch 'PREDEFINED LOOKS' -or $fix40Editor -notmatch 'ONE-CLICK TEXT ANIMATION' -or $fix40EditorCs -notmatch 'TextAnimationPreset_Click' -or $fix40EditorCs -notmatch 'TimelineLayerHit' -or $fix40EditorCs -notmatch 'ProjectDuration_LostFocus') { throw 'FIX40 guard failed: contextual CG inspector/color/animation/timeline controls are incomplete.' }
if ($fix40Settings -notmatch 'Maximum="96"' -or $fix40Settings -notmatch 'Maximum="28"' -or $fix40Settings -notmatch 'UpdateSourceTrigger=PropertyChanged') { throw 'FIX40 guard failed: live playlist appearance settings are incomplete.' }
Write-Host '  FIX40 runtime race, clean audio controls, contextual CG editor, timeline resizing and appearance guards passed.' -ForegroundColor Green

Write-Host 'Checking FIX42 professional broadcast I/O / HA guards...' -ForegroundColor Cyan
$fix42Root = Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Professional'
$fix42Required = @(
    'ProfessionalBroadcastSettings.cs','ProfessionalBroadcastRuntime.cs','St2110Runtime.cs','PtpNmosHa.cs',
    'Scte35Transport.cs','AncillaryData.cs','DvbSubtitleTransport.cs','DeviceControl.cs'
)
foreach ($name in $fix42Required) {
    $path = Join-Path $fix42Root $name
    if (-not (Test-Path $path)) { throw "FIX42 guard failed: missing $name" }
}
$fix42All = ($fix42Required | ForEach-Object { Get-Content (Join-Path $fix42Root $_) -Raw }) -join "`n"
$fix42Main = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\ViewModels\MainViewModel.cs') -Raw
$fix42Playlist = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Models\PlaylistItem.cs') -Raw
$fix42Settings = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Models\AppSettings.cs') -Raw
$requiredTokens = @('St2110Runtime','EnableSt2022_7','PtpMonitor','NmosSenderServer','Scte35TsSender','BuildScte104','BuildCea608','BuildCea708Cdp','DvbSubtitleTsSender','GpioSerialController','Rs422Transport','VdcpClient','SnmpV2Agent','IDolbyCodecAdapter','HighAvailabilityService')
foreach ($token in $requiredTokens) {
    if ($fix42All -notmatch [regex]::Escape($token)) { throw "FIX42 guard failed: $token implementation signature is missing." }
}
if ($fix42Main -notmatch 'ProfessionalOutputArmed' -or $fix42Main -notmatch 'SendSnmpTrapAsync') { throw 'FIX42 guard failed: output interlock / SNMP execution wiring is incomplete.' }
if ($fix42Playlist -match '"DUMMY"') { throw 'FIX42 guard failed: DUMMY control event is still present.' }
if ($fix42Settings -notmatch 'ProfessionalBroadcastSettings') { throw 'FIX42 guard failed: professional settings are not persisted.' }
# FIX42B: compiler regressions found by the first real Windows Debug x64 build.
# C# iterators may not use ref/in/out parameters, so packetizers must materialize lists.
if ($fix42All -match '(?s)IEnumerable\s*<\s*byte\[\]\s*>\s+\w+\s*\([^)]*\b(ref|out|in)\b[^)]*\).*?yield\s+return') {
    throw 'FIX42B guard failed: iterator method still uses ref/in/out parameters.'
}
if ($fix42All -match 'var\s+n\s*=\s*b\[p\+\+\]\s*;\s*if\s*\(\(n\s*&\s*0x80\)') {
    throw 'FIX42B guard failed: SNMP BER length accumulator is inferred as byte instead of int.'
}
if ($fix42All -match '\(autoReturn\s*\?\s*0x80\s*:\s*0\)\s*\|\s*\(\(dur\s*>>\s*32\)') {
    throw 'FIX42B guard failed: SCTE duration high-bit expression can trigger signed bitwise promotion warning.'
}
Write-Host '  FIX42/FIX42B professional I/O, HA and Windows compiler-regression guards passed.' -ForegroundColor Green
# FIX43C: regressions found by the 18-Sep Windows Debug x64 build.
$fix43cScte = Get-Content (Join-Path $fix42Root 'Scte35Transport.cs') -Raw
$fix43cDvb = Get-Content (Join-Path $fix42Root 'DvbSubtitleTransport.cs') -Raw
if ($fix43cScte -match 'var\s+duration\s*=\s*item\.ScteDurationSeconds[^;]*:\s*null\s*;') {
    throw 'FIX43C guard failed: SCTE optional break duration must be explicitly nullable TimeSpan?.'
}
if ($fix43cScte -notmatch 'TimeSpan\?\s+duration\s*=') {
    throw 'FIX43C guard failed: SCTE nullable duration declaration is missing.'
}
if ($fix43cScte -match '\(autoReturn\s*\?\s*0x80\s*:\s*0x00\)\s*\|\s*0x7E\s*\|\s*\(\(dur\s*>>') {
    throw 'FIX43C guard failed: SCTE 33-bit duration header still mixes signed long in bitwise OR.'
}
if ($fix43cDvb -match '\(byte\)\s*(width|height|x|y)\b' -and $fix43cDvb -notmatch '\(byte\)\s*\((width|height|x|y)\s*&\s*0xFF\)') {
    throw 'FIX43C guard failed: DVB 16-bit coordinate/size low-byte packing is unsafe.'
}
if ($fix43cDvb -notmatch '\(byte\)\(width\s*&\s*0xFF\)' -or $fix43cDvb -notmatch '\(byte\)\(y\s*&\s*0xFF\)') {
    throw 'FIX43C guard failed: DVB subtitle width/Y low-byte masks are missing.'
}
Write-Host '  FIX43C nullable SCTE, 33-bit packing and DVB byte-range compiler guards passed.' -ForegroundColor Green

Write-Host 'Checking FIX45 integrated newsroom / API / HA suite guards...' -ForegroundColor Cyan
$fix45Launcher = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\StandaloneAppLauncher.cs') -Raw
$fix45Vm = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\ViewModels\MainViewModel.cs') -Raw
$fix45Ha = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Professional\PtpNmosHa.cs') -Raw
$fix45Coordinator = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Professional\HaCoordinator.cs') -Raw
$fix45Bus = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\PlatformControlBus.cs') -Raw
$fix45Nrcs = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\NrcsPlatformStore.cs') -Raw
$fix45HaBus = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\HaControlBus.cs') -Raw
$fix45HaUi = Get-Content (Join-Path $ProjectRoot 'src\Kashtrix.HAController\MainWindow.xaml.cs') -Raw
$fix45SettingsXaml = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\SettingsWindow.xaml') -Raw
foreach ($required in @('Kashtrix.NRCS','Kashtrix.Prompter','Kashtrix.HAController','Kashtrix.ApiGateway')) { if ($fix45Launcher -notmatch [regex]::Escape($required)) { throw "FIX45 guard failed: launcher mapping missing $required." } }
if ($fix45Vm -notmatch 'StartPlatformControlWatcher' -or $fix45Vm -notmatch 'SynchronizeWarmStandby') { throw 'FIX45 guard failed: common playout command bus or warm standby synchronization is not wired.' }
if ($fix45Ha -notmatch 'HaDirective' -or $fix45Ha -notmatch 'HaControlPort' -or $fix45Ha -notmatch 'SyncStateReceived') { throw 'FIX45 guard failed: HA coordinator directive/control/warm-sync path is incomplete.' }
if ($fix45Coordinator -notmatch 'AUTO N\+M' -or $fix45Coordinator -notmatch 'Capacity') { throw 'FIX45 guard failed: N+M capacity coordinator is missing.' }
if ($fix45Bus -notmatch 'SubmitAndWaitAsync' -or $fix45Bus -notmatch 'PublishStatus') { throw 'FIX45 guard failed: platform command/status bus is incomplete.' }
if ($fix45Nrcs -notmatch 'PublishToPlayout' -or $fix45Nrcs -notmatch 'PublishMosXml' -or $fix45Nrcs -notmatch 'PublishMosAsync' -or $fix45Nrcs -notmatch 'MosDataJson' -or $fix45Nrcs -notmatch 'MosLayer') { throw 'FIX45 guard failed: NRCS playout/MOS publishing is incomplete.' }
$fix45Main = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\MainWindow.xaml.cs') -Raw
$fix45Playlist = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Models\PlaylistItem.cs') -Raw
if ($fix45Main -notmatch 'PlayCgTemplateFromAutomation' -or $fix45Playlist -notmatch 'MosDataJson' -or $fix45Playlist -notmatch 'MosLayer') { throw 'FIX45 guard failed: NRCS/MOS CG data is not wired into real CG execution.' }
$fix45Api = Get-Content (Join-Path $ProjectRoot 'src\Kashtrix.ApiGateway\GatewayHost.cs') -Raw
if ($fix45Api -notmatch 'WebSocketSession' -or $fix45Api -notmatch 'TcpLoop' -or $fix45Api -notmatch 'UdpLoop' -or $fix45Api -notmatch 'HandleMcp') { throw 'FIX45 guard failed: REST/WebSocket/TCP/UDP/MCP gateway paths are incomplete.' }
foreach ($apiGuard in @('/api/v1/channel-controller/commands','/api/v1/cg-controller/','/api/v1/ha/nodes','/api/v1/ha/failover','/api/v1/nrcs/rundowns','/api/v1/prompter/')) { if ($fix45Api -notmatch [regex]::Escape($apiGuard)) { throw "FIX45 guard failed: API route missing $apiGuard." } }
if ($fix45HaBus -notmatch 'SubmitAndWaitAsync' -or $fix45HaBus -notmatch 'PublishSnapshot') { throw 'FIX45 guard failed: HA control IPC/snapshot bus is incomplete.' }
if ($fix45HaUi -notmatch 'StartControlWatcher' -or $fix45HaUi -notmatch 'force_active' -or $fix45HaUi -notmatch 'HaControlBus.PublishSnapshot') { throw 'FIX45 guard failed: HA Controller is not executing coordinator commands or publishing state.' }
foreach ($settingsGuard in @('PROFESSIONAL I/O / HA','HaMode','HaCapacity','OPEN HA CONTROLLER','OPEN API GATEWAY','OPEN NRCS','OPEN PROMPTER')) { if ($fix45SettingsXaml -notmatch [regex]::Escape($settingsGuard)) { throw "FIX45 guard failed: Professional I/O/HA settings control missing $settingsGuard." } }
foreach ($standalone45 in @('Kashtrix.NRCS','Kashtrix.Prompter','Kashtrix.HAController','Kashtrix.ApiGateway')) { if (-not (Test-Path (Join-Path $ProjectRoot "src\$standalone45\GlobalUsings.cs"))) { throw "FIX45 guard failed: GlobalUsings missing for $standalone45." } }
$wrapperProps45 = Get-Content (Join-Path $ProjectRoot 'Directory.Build.props') -Raw
foreach ($wrapper in @('Kashtrix.NRCS','Kashtrix.Prompter','Kashtrix.HAController','Kashtrix.ApiGateway')) { if ($wrapperProps45 -notmatch [regex]::Escape($wrapper)) { throw "FIX45 guard failed: wrapper-project build recursion protection missing $wrapper." } }
Write-Host '  FIX45 NRCS, Prompter, MOS-to-CG, HA 1+1/N+M, API Gateway and common command-bus guards passed.' -ForegroundColor Green

Write-Host 'Checking FIX46 CG auto-key / alpha output / enterprise MAM-ingest guards...' -ForegroundColor Cyan
$fix46CgEditorXaml = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml') -Raw
$fix46CgEditor = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml.cs') -Raw
$fix46Anim = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\CgAnimationEngine.cs') -Raw
$fix46CgOutput = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Outputs\CgGraphicsOutputEngine.cs') -Raw
$fix46Deck = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Outputs\DeckLinkOutputAdapter.cs') -Raw
$fix46Ndi = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Outputs\NdiSender.cs') -Raw
$fix46Mam = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\EnterpriseMamCatalog.cs') -Raw
$fix46Ingest = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\IngestServerWindow.xaml.cs') -Raw
$fix46Api = Get-Content (Join-Path $ProjectRoot 'src\Kashtrix.ApiGateway\GatewayHost.cs') -Raw
$fix46Playout = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Engine\PlayoutEngine.cs') -Raw
# FIX48T: FIX46 originally expected the layer ScrollViewer to disable horizontal scrolling.
# Later timeline zoom work intentionally made the layer rows horizontally scrollable and synchronizes
# the sticky ruler to that offset. Validate the actual separated/synchronized architecture instead.
if ($fix46CgEditorXaml -notmatch 'x:Name="TimelineRulerScroll"[^>]*Grid.Row="1"[^>]*HorizontalScrollBarVisibility="Hidden"[^>]*VerticalScrollBarVisibility="Disabled"' -or
    $fix46CgEditorXaml -notmatch 'x:Name="TimelineRulerCanvas"' -or
    $fix46CgEditorXaml -notmatch 'x:Name="TimelineLayerScroll"[^>]*Grid.Row="2"[^>]*HorizontalScrollBarVisibility="Auto"[^>]*VerticalScrollBarVisibility="Auto"[^>]*ScrollChanged="TimelineLayerScroll_ScrollChanged"' -or
    $fix46CgEditor -notmatch 'TimelineLayerScroll_ScrollChanged' -or
    $fix46CgEditor -notmatch 'TimelineRulerScroll\?\.ScrollToHorizontalOffset\(e\.HorizontalOffset\)') {
    throw 'FIX46 guard failed: sticky timeline ruler / synchronized scrolling architecture is incomplete.'
}
if ($fix46CgEditor -notmatch 'EnsureAutoKeyframe' -or $fix46CgEditor -notmatch '_previewEditKeyframe' -or $fix46Anim -notmatch 'EvaluateLayerLocal') { throw 'FIX46 guard failed: playhead auto-key move/resize workflow is incomplete.' }
if ($fix46CgOutput -notmatch 'alphaSurface:\s*true' -or $fix46Ndi -notmatch 'FourCC_BGRA') { throw 'FIX46 guard failed: transparent BGRA NDI-alpha output path is incomplete.' }
if ($fix46Deck -notmatch 'IDeckLinkKeyer' -or $fix46Deck -notmatch 'DeckLinkKeyerMode\.External' -or $fix46Deck -notmatch 'DeckLinkKeyerMode\.Internal') { throw 'FIX46 guard failed: DeckLink internal/external keyer path is incomplete.' }
foreach ($mamGuard in @('CREATE TABLE IF NOT EXISTS assets','asset_versions','asset_history','VerifyIntegrityAsync','ArchiveAsync','RestoreAsync','AssertMayUse')) { if ($fix46Mam -notmatch [regex]::Escape($mamGuard)) { throw "FIX46 guard failed: MAM capability missing $mamGuard." } }
foreach ($ingestGuard in @('IngestScheduleStore','DualMasterProxy','ComplianceMode','GrowingFileMode','FinalizeRecordedOutputsAsync','IngestMinimumFreeSpaceGb','ReplicateFileAsync')) { if ($fix46Ingest -notmatch [regex]::Escape($ingestGuard)) { throw "FIX46 guard failed: ingest capability missing $ingestGuard." } }
foreach ($apiGuard in @('/api/v1/mam/assets','VerifyIntegrityAsync','UpdateWorkflow')) { if ($fix46Api -notmatch [regex]::Escape($apiGuard)) { throw "FIX46 guard failed: MAM REST route/workflow missing $apiGuard." } }
if ($fix46Playout -notmatch 'AssertMayUse' -or $fix46Playout -notmatch 'RestoreAsync') { throw 'FIX46 guard failed: playout MAM rights enforcement / archive restore is not wired at the engine boundary.' }
Write-Host '  FIX46 CG sticky ruler, auto-key, NDI alpha, DeckLink keyer, MAM and ingest guards passed.' -ForegroundColor Green


Write-Host 'Checking FIX47 professional NRCS / Prompter guards...' -ForegroundColor Cyan
$fix47NrcsXaml = Get-Content (Join-Path $ProjectRoot 'src\Kashtrix.NRCS\MainWindow.xaml') -Raw
$fix47NrcsCode = Get-Content (Join-Path $ProjectRoot 'src\Kashtrix.NRCS\MainWindow.xaml.cs') -Raw
$fix47NrcsStore = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\NrcsPlatformStore.cs') -Raw
$fix47PrompterXaml = Get-Content (Join-Path $ProjectRoot 'src\Kashtrix.Prompter\MainWindow.xaml') -Raw
$fix47Prompter = Get-Content (Join-Path $ProjectRoot 'src\Kashtrix.Prompter\MainWindow.xaml.cs') -Raw
$fix47Renderer = Get-Content (Join-Path $ProjectRoot 'src\Kashtrix.Prompter\PrompterRenderer.cs') -Raw
$fix47Remote = Get-Content (Join-Path $ProjectRoot 'src\Kashtrix.Prompter\PrompterRemoteServer.cs') -Raw
$fix47Api = Get-Content (Join-Path $ProjectRoot 'src\Kashtrix.ApiGateway\GatewayHost.cs') -Raw
foreach ($guard in @('PLANNING / ASSIGNMENTS','STORY DESK','RUNDOWN / LIVE','INTEGRATION / STATUS','RESTORE SELECTED REVISION','AssignmentFilterModeBox')) {
    if ($fix47NrcsXaml -notmatch [regex]::Escape($guard)) { throw "FIX47 guard failed: professional NRCS workspace missing $guard." }
}
foreach ($guard in @('nrcs_story_bank','nrcs_rundown_items','nrcs_assignments','nrcs_story_versions','nrcs_live_state','MigrateLegacyStories','AddStoryToRundown','SetLiveStory','PublishMosXml')) {
    if ($fix47NrcsStore -notmatch [regex]::Escape($guard)) { throw "FIX47 guard failed: newsroom persistence/workflow missing $guard." }
}
foreach ($guard in @('VersionGrid_SelectionChanged','RestoreRevision_Click','AssignmentFilter_Changed','CreateStoryFromAssignment')) {
    if ($fix47NrcsCode -notmatch [regex]::Escape($guard)) { throw "FIX47 guard failed: NRCS editor workflow missing $guard." }
}
# FLOW/CARDS are populated by code-behind so the operator list can be restored/extended
# without duplicating ComboBoxItems in XAML. Validate those modes against XAML + code,
# while visible operator controls remain XAML requirements.
foreach ($guard in @('FLOW','CARDS')) {
    if (($fix47PrompterXaml + $fix47Prompter) -notmatch [regex]::Escape($guard)) { throw "FIX47 guard failed: Prompter mode missing $guard." }
}
foreach ($guard in @('START NDI','START SDI','OPEN / UPDATE DISPLAY','LOAD EMERGENCY TXT / RTF','EXPORT SRT / CC','BOOKMARK','FOLLOW NRCS LIVE','CLOAK / UNCLOAK')) {
    if ($fix47PrompterXaml -notmatch [regex]::Escape($guard)) { throw "FIX47 guard failed: Prompter operator/output feature missing $guard." }
}
foreach ($guard in @('NdiSender','DeckLinkOutputAdapter','PrompterRenderer.RenderBgra','GetLiveState','PrompterRemoteServer','LoadEmergencyScript_Click','ExportSrt_Click','ToggleCloak_Click','PresenterPromptProfile')) {
    if ($fix47Prompter -notmatch [regex]::Escape($guard)) { throw "FIX47 guard failed: Prompter execution path missing $guard." }
}
foreach ($guard in @('MirrorHorizontal','FlipVertical','RightToLeft','CardMode','RenderBgra')) {
    if ($fix47Renderer -notmatch [regex]::Escape($guard)) { throw "FIX47 guard failed: renderer capability missing $guard." }
}
if ($fix47Remote -notmatch 'UdpClient' -or $fix47Remote -notmatch 'CommandReceived') { throw 'FIX47 guard failed: prompter UDP remote engine is incomplete.' }
foreach ($guard in @('/api/v1/nrcs/stories','/api/v1/nrcs/assignments','nrcs_list_stories','nrcs_list_assignments','nrcs_get_live','nrcs_set_live')) {
    if ($fix47Api -notmatch [regex]::Escape($guard)) { throw "FIX47 guard failed: NRCS API/MCP surface missing $guard." }
}
# Rundown live-state routing is parsed as /api/v1/nrcs/rundowns/{id}/live by URL
# segments, so do not require a monolithic '/live' literal that the router never uses.
if ($fix47Api -notmatch 'seg\[3\]=="rundowns"' -or $fix47Api -notmatch 'seg\[5\]\.Equals\("live"') {
    throw 'FIX47 guard failed: NRCS rundown live-state REST route is incomplete.'
}
Write-Host '  FIX47 separate Planning/Story/Rundown NRCS, revisions, live state, Prompter NDI/display/SDI/cards/offline/bookmarks/remote/SRT guards passed.' -ForegroundColor Green

Write-Host 'Checking FIX48 virtual A/V, newsroom demo and security guards...' -ForegroundColor Cyan
$fix48VirtualH = Get-Content (Join-Path $ProjectRoot 'native\Kashtrix.VirtualOutput\KashtrixVirtualOutput.h') -Raw
$fix48VirtualCpp = Get-Content (Join-Path $ProjectRoot 'native\Kashtrix.VirtualOutput\KashtrixVirtualOutput.cpp') -Raw
$fix48Security = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\SecurityStore.cs') -Raw
$fix48UsersXaml = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\UserManagementWindow.xaml') -Raw
$fix48Login = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\LoginWindow.xaml.cs') -Raw
$fix48NrcsStore = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\NrcsPlatformStore.cs') -Raw
$fix48NrcsXaml = Get-Content (Join-Path $ProjectRoot 'src\Kashtrix.NRCS\MainWindow.xaml') -Raw
$fix48Api = Get-Content (Join-Path $ProjectRoot 'src\Kashtrix.ApiGateway\GatewayHost.cs') -Raw
$fix48AsRun = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\AsRunLogService.cs') -Raw
$fix48MainVm = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\ViewModels\MainViewModel.cs') -Raw
$fix48SettingsXaml = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\SettingsWindow.xaml') -Raw
foreach ($guard in @('CKashtrixVirtualAudioStream','Program Audio','AUDIO_MAP','MEDIASUBTYPE_PCM','CLSID_AudioInputDeviceCategory')) { if ($fix48VirtualH + $fix48VirtualCpp -notmatch [regex]::Escape($guard)) { throw "FIX48 guard failed: virtual-output audio implementation missing $guard." } }
foreach ($guard in @('Pbkdf2','210000','CryptographicOperations.FixedTimeEquals','api_credentials','token_hash','SecurityPrincipal','HasScope')) { if ($fix48Security -notmatch [regex]::Escape($guard)) { throw "FIX48 guard failed: security capability missing $guard." } }
foreach ($guard in @('USER / API ACCESS MANAGEMENT','CREATE API TOKEN','RESET PASSWORD')) { if ($fix48UsersXaml -notmatch [regex]::Escape($guard)) { throw "FIX48 guard failed: user-management UI missing $guard." } }
if ($fix48Login -notmatch 'VerifyPassword' -or $fix48Api -notmatch 'VerifyApiToken' -or $fix48Api -notmatch 'RequiredScope') { throw 'FIX48 guard failed: interactive/API authentication is not wired.' }
foreach ($guard in @('StoryType','SeedProfessionalDemo','TOP STORY','PACKAGE','CITY UPDATE','VO','INTERVIEW','SOT','LIVE REPORTER','storyDuration')) { if ($fix48NrcsStore -notmatch [regex]::Escape($guard)) { throw "FIX48 guard failed: NRCS demo/MOS model missing $guard." } }
foreach ($guard in @('LOAD PROFESSIONAL DEMO','STORY / MOS TYPE')) { if ($fix48NrcsXaml -notmatch [regex]::Escape($guard)) { throw "FIX48 guard failed: NRCS demo controls missing $guard." } }
foreach ($guard in @('CREATE TABLE IF NOT EXISTS asrun','ExportCsv','actual_start_utc','actual_end_utc')) { if ($fix48AsRun -notmatch [regex]::Escape($guard)) { throw "FIX48 guard failed: as-run/reconciliation capability missing $guard." } }
if ($fix48MainVm -notmatch '_asRun.Start' -or $fix48MainVm -notmatch '_asRun.Complete' -or $fix48Api -notmatch '/api/v1/playout/asrun' -or $fix48SettingsXaml -notmatch 'EXPORT AS-RUN CSV') { throw 'FIX48 guard failed: as-run logging/API/operator export is not fully wired.' }
Write-Host '  FIX48 dual DirectShow A/V, NRCS PKG/VO/SOT/LIVE demo workflow, hashed user/API authentication and persistent as-run reconciliation guards passed.' -ForegroundColor Green

Write-Host 'Checking FIX48D compiler namespace regression guards...' -ForegroundColor Cyan
$haCoordinatorSource = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Professional\HaCoordinator.cs') -Raw
if ($haCoordinatorSource -notmatch 'using\s+BroadcastPlayout\.Models\s*;') {
    throw 'FIX48D guard failed: HaCoordinator must import BroadcastPlayout.Models for ProfessionalBroadcastSettings.'
}
$professionalFiles = Get-ChildItem (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Professional') -Filter '*.cs' -File
foreach ($professionalFile in $professionalFiles) {
    $professionalSource = Get-Content $professionalFile.FullName -Raw
    if ($professionalFile.Name -ne 'ProfessionalBroadcastSettings.cs' -and $professionalSource -match '\bProfessionalBroadcastSettings\b' -and $professionalSource -notmatch 'using\s+BroadcastPlayout\.Models\s*;') {
        throw "FIX48D guard failed: $($professionalFile.Name) references ProfessionalBroadcastSettings without importing BroadcastPlayout.Models."
    }
}
Write-Host '  Professional broadcast settings namespace/import guards passed.' -ForegroundColor Green

Write-Host 'Checking FIX48E API Gateway / nullable regression guards...' -ForegroundColor Cyan
$apiGatewaySource = Get-Content (Join-Path $ProjectRoot 'src\Kashtrix.ApiGateway\GatewayHost.cs') -Raw
$mamCatalogSource = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\EnterpriseMamCatalog.cs') -Raw
if ($apiGatewaySource -match 'using\s*\(\s*client\s*\)\s*\r?\n\s*using\s+var\s+stream') { throw 'FIX48E guard failed: TcpClientSession has an illegal declaration embedded under using(client).' }
if ($apiGatewaySource -notmatch 'using\s+var\s+clientLifetime\s*=\s*client\s*;' -or $apiGatewaySource -notmatch 'string\?\s+targetNode\s*=\s*forceActive') { throw 'FIX48E guard failed: API Gateway TCP lifetime/nullability recovery is incomplete.' }
if ($mamCatalogSource -notmatch 'normalizedStatus' -or $mamCatalogSource -notmatch 'normalizedDetail') { throw 'FIX48E guard failed: MAM QC history nullability normalization is missing.' }
Write-Host '  API Gateway TCP lifetime, HA nullability and MAM QC nullability guards passed.' -ForegroundColor Green

Write-Host 'Checking FIX48F login, DeckLink scan-mode and 200-template guards...' -ForegroundColor Cyan
$fix48fApp = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\App.xaml.cs') -Raw
$fix48fLoginXaml = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\LoginWindow.xaml') -Raw
$fix48fLoginCs = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\LoginWindow.xaml.cs') -Raw
$fix48fOutputProfile = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Models\OutputProfile.cs') -Raw
$fix48fDeckLink = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Outputs\DeckLinkOutputAdapter.cs') -Raw
$fix48fNdi = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Outputs\NdiSender.cs') -Raw
$fix48fCgController = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgControllerWindow.xaml') -Raw
$fix48fCgControllerCs = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgControllerWindow.xaml.cs') -Raw
$fix48fCgEditor = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml') -Raw
$fix48fCgFactoryPath = Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\CgUniqueDemoFactory.cs'
# Read UTF-8 explicitly. Windows PowerShell 5.1 otherwise decodes the middle-dot separators
# in template names through the legacy ANSI code page and can false-fail a healthy catalog.
$fix48fCgFactory = [System.IO.File]::ReadAllText($fix48fCgFactoryPath, [System.Text.Encoding]::UTF8)
$fix48fCgValidator = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\CgDemoValidator.cs') -Raw
$fix48fSettings = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\SettingsWindow.xaml') -Raw
if ($fix48fApp -notmatch 'ShutdownMode\s*=\s*ShutdownMode\.OnExplicitShutdown' -or $fix48fApp -notmatch 'ShutdownMode\s*=\s*ShutdownMode\.OnMainWindowClose' -or $fix48fApp -notmatch 'mainWindow\.Activate\(\)') { throw 'FIX48F guard failed: login-to-Playout WPF lifetime handoff is incomplete.' }
if ($fix48fLoginXaml -notmatch 'MinHeight="520"' -or $fix48fLoginXaml -notmatch 'PasswordBox[^>]+Height="42"' -or $fix48fLoginCs -notmatch 'DialogResult\s*=\s*true') { throw 'FIX48F guard failed: login/password layout or successful modal close path is incomplete.' }
foreach ($guard in @('ScanModes','Interlaced Upper First','AlphaModes','PrerollFrames','IsInterlaced')) { if ($fix48fOutputProfile -notmatch [regex]::Escape($guard)) { throw "FIX48F guard failed: output profile missing $guard." } }
if ($fix48fDeckLink -notmatch 'PersistentName' -or $fix48fDeckLink -notmatch 'GetModeFrameRate' -or $fix48fDeckLink -notmatch 'GetModeScanMode' -or $fix48fDeckLink -notmatch 'profile\.ScanMode') { throw 'FIX48F guard failed: DeckLink card-name or mode matching is incomplete.' }
if ($fix48fNdi -notmatch 'FrameFormatInterleaved' -or $fix48fNdi -notmatch 'profile\.IsInterlaced') { throw 'FIX48F guard failed: NDI progressive/interlaced signaling is incomplete.' }
if ($fix48fCgController -notmatch 'DECKLINK CARD / PORT' -or $fix48fCgController -notmatch 'SCAN / FIELD ORDER' -or $fix48fCgController -notmatch 'PREROLL FRAMES' -or $fix48fCgControllerCs -notmatch 'CgDeckLinkDeviceName') { throw 'FIX48F guard failed: CG output hardware settings UI is incomplete.' }
if ($fix48fSettings -notmatch 'DECKLINK CARD' -or $fix48fSettings -notmatch 'SCAN / FIELD ORDER') { throw 'FIX48F guard failed: Playout output progressive/interlaced settings are missing.' }
$fix48fNamesMatch = [regex]::Match($fix48fCgFactory, 'private static readonly string\[\] Names\s*=\s*\[(?<body>[\s\S]*?)\];')
$fix48fCatalogNameCount = if ($fix48fNamesMatch.Success) { [regex]::Matches($fix48fNamesMatch.Groups['body'].Value, '"(?:\\.|[^"\\])*"').Count } else { 0 }
$fix48fCatalogTokens = @('Crystal Current Conditions','Hollywood Live Lower Third','Neon Live Opener','Sunrise Opener','CrystalCard','WeatherIcon')
if ($fix48fCgValidator -notmatch 'list\.Count==200' -or $fix48fCatalogNameCount -ne 200) { throw "FIX48F guard failed: CG catalog must contain exactly 200 source template names; found $fix48fCatalogNameCount." }
foreach ($guard in $fix48fCatalogTokens) { if ($fix48fCgFactory -notmatch [regex]::Escape($guard)) { throw "FIX48F guard failed: 200-template catalog missing $guard." } }
if ($fix48fCgEditor -notmatch 'Filter the 200-template catalog' -or $fix48fCgEditor -notmatch 'TemplateCountText') { throw 'FIX48F guard failed: CG Editor 200-template browser/filter UI is missing.' }
Write-Host '  Login launch, named DeckLink cards, scan modes and 200-template CG catalog guards passed.' -ForegroundColor Green

Write-Host 'Checking FIX48G CG Editor safe-area guide guards...' -ForegroundColor Cyan
$fix48gCgEditorXaml = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml') -Raw
$fix48gCgEditorCs = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml.cs') -Raw
$fix48gSettings = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Models\AppSettings.cs') -Raw
foreach ($guard in @('SAFE AREAS','SafeAreaPresets','ShowSafeAreas','ShowCenterGuide')) { if ($fix48gCgEditorXaml -notmatch [regex]::Escape($guard)) { throw "FIX48G guard failed: CG Editor safe-area UI missing $guard." } }
foreach ($guard in @('DrawSafeAreaGuides','DrawSafeRectangle','ACTION SAFE','TITLE SAFE','IsHitTestVisible = false','PersistSafeGuideSettings')) { if ($fix48gCgEditorCs -notmatch [regex]::Escape($guard)) { throw "FIX48G guard failed: CG Editor safe-area implementation missing $guard." } }
foreach ($guard in @('CgEditorShowSafeAreas','CgEditorShowCenterGuide','CgEditorActionSafePercent','CgEditorTitleSafePercent')) { if ($fix48gSettings -notmatch [regex]::Escape($guard)) { throw "FIX48G guard failed: CG Editor safe-area persistence missing $guard." } }
Write-Host '  Action Safe, Title Safe, center guides and persisted editor-only safe margins passed.' -ForegroundColor Green

Write-Host 'Checking FIX48H demo-load and newsroom integration guards...' -ForegroundColor Cyan
$fix48hDemoService = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\DemoIntegrationService.cs') -Raw
$fix48hNrcsStore = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\NrcsPlatformStore.cs'), [System.Text.Encoding]::UTF8)
$fix48hNrcsUi = Get-Content (Join-Path $ProjectRoot 'src\Kashtrix.NRCS\MainWindow.xaml.cs') -Raw
$fix48hGateway = Get-Content (Join-Path $ProjectRoot 'src\Kashtrix.ApiGateway\GatewayHost.cs') -Raw
$fix48hDemoScriptPath = Join-Path $ProjectRoot 'tools\DemoLoad-And-Test.ps1'
$fix48hDemoCmd = Join-Path $ProjectRoot 'DEMOLOAD.cmd'
$fix48hRuntimeCmd = Join-Path $ProjectRoot 'INTEGRATION-TEST.cmd'
foreach ($requiredPath in @($fix48hDemoScriptPath,$fix48hDemoCmd,$fix48hRuntimeCmd)) { if (-not (Test-Path $requiredPath)) { throw "FIX48H guard failed: missing integration runner $requiredPath" } }
foreach ($guard in @('SeedAndValidate','CatalogLooksHealthy','PublishToPlayout','PublishMosXml','CG_PLAY','Prompter rundown','PREVIEW CG PLAY','reload_playlist command/reply','NRCS CG template references','NRCS demo media')) { if ($fix48hDemoService -notmatch [regex]::Escape($guard)) { throw "FIX48H guard failed: demo integration service missing $guard." } }
foreach ($guard in @('RebuildProfessionalDemo','ResolveDemoMediaPath','News . Crystal Headline Lower Third','Weather . Crystal Current Conditions')) { if ($fix48hNrcsStore -notmatch $guard) { throw "FIX48H guard failed: NRCS professional demo repair missing $guard." } }
if ($fix48hNrcsStore -match 'Demo . News . Now Playing') { throw 'FIX48H guard failed: NRCS demo still references the retired Demo News Now Playing CG template.' }
if ($fix48hNrcsUi -notmatch 'RebuildProfessionalDemo') { throw 'FIX48H guard failed: NRCS LOAD PROFESSIONAL DEMO does not rebuild stale demo data.' }
foreach ($guard in @('/api/v1/demo/load','demo_load_and_test','DemoIntegrationService')) { if ($fix48hGateway -notmatch [regex]::Escape($guard)) { throw "FIX48H guard failed: API Gateway demo integration missing $guard." } }
$fix48hDemoScript = Get-Content $fix48hDemoScriptPath -Raw
foreach ($guard in @('200 CG demos','/api/v1/cg/templates','/api/v1/prompter/','reload_playlist','/api/v1/cg-controller/play','PREVIEW')) { if ($fix48hDemoScript -notmatch [regex]::Escape($guard)) { throw "FIX48H guard failed: demo runner missing $guard." } }
Write-Host '  DEMOLOAD, live integration test, CG/MOS/NRCS/Playout/Prompter checks and UTF-8-safe catalog validation passed.' -ForegroundColor Green

Write-Host 'Checking FIX48J CG runtime / editor regression guards...' -ForegroundColor Cyan
$fix48jCgEditorXaml = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml') -Raw
$fix48jCgEditorCs = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml.cs') -Raw
$fix48jCgModel = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Models\CgModels.cs') -Raw
$fix48jCgEngine = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\CgAnimationEngine.cs') -Raw
$fix48jCgFactory = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\CgUniqueDemoFactory.cs'), [System.Text.Encoding]::UTF8)
$fix48jGateway = Get-Content (Join-Path $ProjectRoot 'src\Kashtrix.ApiGateway\GatewayHost.cs') -Raw
foreach ($guard in @('ONE-CLICK TEXT ANIMATION','Pop Cascade','TextAnimationUnits','ALIGN TO CANVAS','HORIZONTAL CENTER','VERTICAL CENTER')) { if ($fix48jCgEditorXaml -notmatch [regex]::Escape($guard)) { throw "FIX48J guard failed: CG Editor UI missing $guard." } }
foreach ($guard in @('TimelineKeyframeHit','_dragKeyframe','TimelineLayerHit','AlignSelectedLayers','ClearLayerSelection','_selectedLayerIds.Clear()','SyncLayerListSelectionVisuals')) { if ($fix48jCgEditorCs -notmatch [regex]::Escape($guard)) { throw "FIX48J guard failed: CG Editor interaction missing $guard." } }
if ($fix48jCgModel -notmatch 'ShadowOffsetX\s*\{\s*get;\s*set;\s*\}' -or $fix48jCgModel -notmatch 'ShadowOffsetY\s*\{\s*get;\s*set;\s*\}') { throw 'FIX48J guard failed: text shadow must remain opt-in with zero offset defaults.' }
foreach ($guard in @('EvaluateTextVisual','FADE CASCADE','RISE CASCADE','TRACKING REVEAL','POP CASCADE')) { if ($fix48jCgEngine -notmatch [regex]::Escape($guard)) { throw "FIX48J guard failed: runtime text motion missing $guard." } }
foreach ($guard in @('ApplyProjectMotionProfile','EntryMotions','ExitMotions','TextMotionPresets','ShadowOffsetX = 0','ShadowOffsetY = 0')) { if ($fix48jCgFactory -notmatch [regex]::Escape($guard)) { throw "FIX48J guard failed: 200-template motion/shadow normalization missing $guard." } }
foreach ($guard in @('LoadCgProjectsForApi','canonical-fallback','ReadJsonString','failures.Add','c.ValueKind == JsonValueKind.String')) { if ($fix48jGateway -notmatch [regex]::Escape($guard)) { throw "FIX48J guard failed: CG Controller API recovery missing $guard." } }
Write-Host '  Runtime CG API fallback, editor alignment/deselect, text motion, draggable timeline and no-default-shadow guards passed.' -ForegroundColor Green

Write-Host 'Checking FIX48K live-CG / autokey / newsroom / layout guards...' -ForegroundColor Cyan
$fix48kGateway = Get-Content (Join-Path $ProjectRoot 'src\Kashtrix.ApiGateway\GatewayHost.cs') -Raw
$fix48kDemoScript = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'tools\DemoLoad-And-Test.ps1'), [System.Text.Encoding]::UTF8)
$fix48kMainVm = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\ViewModels\MainViewModel.cs') -Raw
$fix48kCgModel = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Models\CgModels.cs') -Raw
$fix48kCgEngine = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\CgAnimationEngine.cs') -Raw
$fix48kCgEditorXaml = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml') -Raw
$fix48kCgEditorCs = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml.cs') -Raw
$fix48kSecurity = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\SecurityStore.cs') -Raw
$fix48kApp = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\App.xaml.cs') -Raw
$fix48kLogin = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\LoginWindow.xaml') -Raw
$fix48kNrcs = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\NrcsPlatformStore.cs'), [System.Text.Encoding]::UTF8)
$fix48kNrcsUi = Get-Content (Join-Path $ProjectRoot 'src\Kashtrix.NRCS\MainWindow.xaml') -Raw
$fix48kNrcsCode = Get-Content (Join-Path $ProjectRoot 'src\Kashtrix.NRCS\MainWindow.xaml.cs') -Raw
$fix48kFactory = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\CgUniqueDemoFactory.cs'), [System.Text.Encoding]::UTF8)
$fix48kValidator = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\CgDemoValidator.cs') -Raw
$fix48kMainXaml = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\MainWindow.xaml') -Raw
$fix48kMainCode = Get-Content (Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\MainWindow.xaml.cs') -Raw
foreach ($guard in @('new UTF8Encoding(false, false)','Request body is not valid JSON/UTF-8','ContentEncoding')) { if ($fix48kGateway -notmatch [regex]::Escape($guard)) { throw "FIX48K guard failed: UTF-8 REST recovery missing $guard." } }
foreach ($guard in @('application/json; charset=utf-8','Encoding]::UTF8.GetBytes')) { if ($fix48kDemoScript -notmatch [regex]::Escape($guard)) { throw "FIX48K guard failed: integration client UTF-8 body missing $guard." } }
if ($fix48kMainVm -notmatch 'PLAY is additive' -or $fix48kMainVm -notmatch 'command.Action.Equals\("UPDATE"') { throw 'FIX48K guard failed: CG Controller PROGRAM stack is not additive.' }
foreach ($guard in @('StyleJson','CaptureStyleJson','CreateVisualLayer','ApplyStyleJson')) { if (($fix48kCgModel + $fix48kCgEngine + $fix48kCgEditorCs) -notmatch [regex]::Escape($guard)) { throw "FIX48K guard failed: AUTO KEY style snapshot missing $guard." } }
foreach ($guard in @('CommitInspectorAutoKey','EnsureAutoKeyframe','Key.Left','Key.Right','Key.Up','Key.Down','ClearCanvas_Click','TimelineLayerHit')) { if ($fix48kCgEditorCs -notmatch [regex]::Escape($guard)) { throw "FIX48K guard failed: editor keyframe/keyboard/clear interaction missing $guard." } }
foreach ($guard in @('CLEAR CANVAS','TYPE ON','CASCADE','RISE','TRACKING','POP')) { if ($fix48kCgEditorXaml -notmatch [regex]::Escape($guard)) { throw "FIX48K guard failed: one-click editor UI missing $guard." } }
foreach ($guard in @('remembered_sessions','RememberSession','TryRestoreRememberedSession')) { if ($fix48kSecurity -notmatch [regex]::Escape($guard)) { throw "FIX48K guard failed: remembered login missing $guard." } }
if ($fix48kApp -notmatch 'TryRestoreRememberedSession' -or $fix48kLogin -notmatch 'RememberLoginCheck') { throw 'FIX48K guard failed: remembered login bootstrap/UI is incomplete.' }
foreach ($guard in @('roEdStart','roEdDur','storyAbstract','objID','mosAbstract','messageTimestamp')) { if ($fix48kNrcs -notmatch [regex]::Escape($guard)) { throw "FIX48K guard failed: enriched MOS output missing $guard." } }
foreach ($guard in @('CgTemplateBox','CgTemplateInfoText','CG LAYER / Z-ORDER')) { if ($fix48kNrcsUi -notmatch [regex]::Escape($guard)) { throw "FIX48K guard failed: NRCS CG chooser UI missing $guard." } }
foreach ($guard in @('RefreshCgCatalog','CgDemoFactory.CreateDefaults','UpdateCgTemplateInfo')) { if ($fix48kNrcsCode -notmatch [regex]::Escape($guard)) { throw "FIX48K guard failed: NRCS canonical CG chooser logic missing $guard." } }
if ($fix48kNrcs -notmatch 'SPORT . LEAGUE PREVIEW' -or $fix48kNrcs -notmatch 'INTERNATIONAL . ROUNDUP' -or $fix48kNrcs -notmatch 'ENTERTAINMENT . CULTURE') { throw 'FIX48K guard failed: full professional newsroom demo stories are incomplete.' }
foreach ($guard in @('ApplyUniqueDesignFingerprint','Signature Accent','Design {projectIndex + 1:000}','Layout {layout + 1:00}')) { if ($fix48kFactory -notmatch [regex]::Escape($guard)) { throw "FIX48K guard failed: unique 200-template design fingerprint missing $guard." } }
if ($fix48kValidator -notmatch 'CatalogDesignsAreDistinct' -or $fix48kValidator -notmatch 'Distinct\(StringComparer.Ordinal\)') { throw 'FIX48K guard failed: 200-template distinct-design validation missing.' }
if ($fix48kMainXaml -notmatch 'PreviewAspectHost' -or $fix48kMainXaml -notmatch 'ProgramAspectHost' -or $fix48kMainXaml -notmatch 'SizeChanged="ConfidenceAspectHost_SizeChanged"' -or $fix48kMainXaml -notmatch 'Stretch="UniformToFill"' -or $fix48kMainCode -notmatch 'FitConfidenceSurface' -or $fix48kMainCode -notmatch '16\.0 / 9\.0') { throw 'FIX48K guard failed: responsive Preview/Program 16:9 viewport guard missing.' }
Write-Host '  UTF-8 live CG, additive stack, full AUTO KEY, remembered login, full NRCS/MOS, 200 distinct designs and 16:9 playout guards passed.' -ForegroundColor Green

Write-Host 'Checking FIX48L responsive confidence-monitor regression guard...' -ForegroundColor Cyan
if ($fix48kMainXaml -match '<Viewbox[^>]*Stretch="Uniform"' -or $fix48kMainXaml -match '<Border Width="1600" Height="900"') { throw 'FIX48L guard failed: Preview/Program must not use a fixed Viewbox design surface.' }
foreach ($guard in @('PreviewAspectSurface','ProgramAspectSurface','ConfidenceAspectHost_SizeChanged','FitConfidenceSurface','16.0 / 9.0')) { if (($fix48kMainXaml + $fix48kMainCode) -notmatch [regex]::Escape($guard)) { throw "FIX48L guard failed: responsive confidence monitor missing $guard." } }
Write-Host '  Responsive 16:9 Preview/Program surfaces use dynamic host fitting without fixed Viewbox scaling.' -ForegroundColor Green

Write-Host 'Checking FIX48M compiler/newsroom compatibility guards...' -ForegroundColor Cyan
$fix48mFactory = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\CgUniqueDemoFactory.cs'), [System.Text.Encoding]::UTF8)
$fix48mNrcs = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\NrcsPlatformStore.cs'), [System.Text.Encoding]::UTF8)
if ($fix48mFactory -notmatch 'HorizontalTextAlignment\s*=\s*\(layout\s*%\s*4\)\s*switch') { throw 'FIX48M guard failed: CG catalog text-alignment switch receiver is not parenthesized.' }
$fix48mBadSwitchFiles = @()
Get-ChildItem (Join-Path $ProjectRoot 'src') -Filter '*.cs' -File -Recurse | ForEach-Object {
    $source = [System.IO.File]::ReadAllText($_.FullName, [System.Text.Encoding]::UTF8)
    if ($source -match '\%\s*[0-9]+\s+switch\s*\{') { $fix48mBadSwitchFiles += $_.FullName }
}
if ($fix48mBadSwitchFiles.Count -gt 0) { throw ('FIX48M guard failed: unparenthesized modulo switch-expression receiver remains in: ' + ($fix48mBadSwitchFiles -join '; ')) }
foreach ($guard in @('PACKAGE','CITY UPDATE','LIVE REPORTER')) { if ($fix48mNrcs -notmatch [regex]::Escape($guard)) { throw "FIX48M guard failed: professional demo compatibility label missing $guard." } }
Write-Host '  C# modulo switch-expression precedence and professional newsroom compatibility guards passed.' -ForegroundColor Green

Write-Host 'Checking FIX48N prompter automation / dockable timeline / broadcast-CG guards...' -ForegroundColor Cyan
$fix48nCgEditorXaml = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml'), [System.Text.Encoding]::UTF8)
$fix48nCgEditorCs = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml.cs'), [System.Text.Encoding]::UTF8)
$fix48nMainXaml = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\MainWindow.xaml'), [System.Text.Encoding]::UTF8)
$fix48nMainCs = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\MainWindow.xaml.cs'), [System.Text.Encoding]::UTF8)
$fix48nPrompterXaml = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\Kashtrix.Prompter\MainWindow.xaml'), [System.Text.Encoding]::UTF8)
$fix48nPrompterCs = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\Kashtrix.Prompter\MainWindow.xaml.cs'), [System.Text.Encoding]::UTF8)
$fix48nMainVm = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\ViewModels\MainViewModel.cs'), [System.Text.Encoding]::UTF8)
foreach ($guard in @('TimelinePanel','DOCK OUT','TimelineDockOut_Click','GridSplitter')) { if (($fix48nCgEditorXaml + $fix48nCgEditorCs) -notmatch [regex]::Escape($guard)) { throw "FIX48N guard failed: dockable/resizable CG timeline missing $guard." } }
foreach ($guard in @('PlaylistPanel','PlaylistDockOut_Click','Dock playlist out')) { if (($fix48nMainXaml + $fix48nMainCs) -notmatch [regex]::Escape($guard)) { throw "FIX48N guard failed: dockable Playout playlist missing $guard." } }
foreach ($guard in @('STORY PRODUCTION / MOS','CG PREVIEW','CG PROGRAM','PLAY STORY','AutoMosCheck','TriggerCgAsync','PlayStoryOnPlayoutAsync')) { if (($fix48nPrompterXaml + $fix48nPrompterCs) -notmatch [regex]::Escape($guard)) { throw "FIX48N guard failed: Prompter production/MOS workflow missing $guard." } }
$fix48nNrcsXaml = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\Kashtrix.NRCS\MainWindow.xaml'), [System.Text.Encoding]::UTF8)
$fix48nNrcsCs = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\Kashtrix.NRCS\MainWindow.xaml.cs'), [System.Text.Encoding]::UTF8)
foreach ($guard in @('AutoAirCheck','AUTO PLAYOUT + PROGRAM CG ON LIVE','TriggerStoryAutomationAsync','play_story')) { if (($fix48nNrcsXaml + $fix48nNrcsCs + $fix48nMainVm) -notmatch [regex]::Escape($guard)) { throw "FIX48N guard failed: NRCS live automation missing $guard." } }
if ($fix48nMainVm -notmatch 'case "play_story"' -or $fix48nMainVm -notmatch 'TakeSelected\(\)') { throw 'FIX48N guard failed: Prompter story command does not take the published NRCS item to Program.' }
$fix48nElectionHtml = Join-Path $ProjectRoot 'src\BroadcastPlayout.App\demos\gfx\html\election.html'
$fix48nElectionPackage = Join-Path $ProjectRoot 'src\BroadcastPlayout.App\demos\gfx\html\Kashtrix-Election-LowerThird.kashgfx'
if (-not (Test-Path $fix48nElectionHtml) -or -not (Test-Path $fix48nElectionPackage)) { throw 'FIX48N guard failed: election lower-third HTML/KASHGFX sample is missing.' }
$fix48nElectionSource = [System.IO.File]::ReadAllText($fix48nElectionHtml, [System.Text.Encoding]::UTF8)
foreach ($guard in @('LIVE RESULTS','NATIONAL ELECTION CENTER','kashtrix-data','strapIn','resultsIn')) { if ($fix48nElectionSource -notmatch [regex]::Escape($guard)) { throw "FIX48N guard failed: election lower-third motion sample missing $guard." } }
Write-Host '  Dockable CG timeline/playlist, Prompter-to-CG/Playout automation, and election broadcast motion sample guards passed.' -ForegroundColor Green

Write-Host 'Checking FIX48O FIX17D timeline regression recovery guard...' -ForegroundColor Cyan
$fix48oEditorXaml = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml'), [System.Text.Encoding]::UTF8)
$fix48oEditorCs = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml.cs'), [System.Text.Encoding]::UTF8)
foreach ($guard in @('RemainingText','TimelineRulerCanvas_MouseLeftButtonDown','TimelineRulerCanvas_MouseMove','TimelineXToTime','_dragMode == "playhead"','Tag = "PLAYHEAD"','Stroke = Brushes.Red')) { if (($fix48oEditorXaml + $fix48oEditorCs) -notmatch [regex]::Escape($guard)) { throw "FIX48O guard failed: draggable red playhead/timer regression recovery missing $guard." } }
Write-Host '  FIX17D draggable red playhead/timer signature and behavior are preserved.' -ForegroundColor Green

Write-Host 'Checking FIX48P Playout / CG Editor / Multiview basic-fix guards...' -ForegroundColor Cyan
$fix48pNdi = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\NdiDiscoveryService.cs'), [System.Text.Encoding]::UTF8)
$fix48pIngest = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\IngestServerWindow.xaml.cs'), [System.Text.Encoding]::UTF8)
$fix48pMainXaml = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\MainWindow.xaml'), [System.Text.Encoding]::UTF8)
$fix48pMainCs = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\MainWindow.xaml.cs'), [System.Text.Encoding]::UTF8)
$fix48pModels = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Models\CgModels.cs'), [System.Text.Encoding]::UTF8)
$fix48pCgXaml = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml'), [System.Text.Encoding]::UTF8)
$fix48pCgCs = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml.cs'), [System.Text.Encoding]::UTF8)
$fix48pCgEngine = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\CgAnimationEngine.cs'), [System.Text.Encoding]::UTF8)
$fix48pCgCompositor = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\CgCompositor.cs'), [System.Text.Encoding]::UTF8)
$fix48pMulti = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\MultiviewWindow.xaml.cs'), [System.Text.Encoding]::UTF8)
if ($fix48pNdi -notmatch 'BuildExtraIpList' -or $fix48pNdi -notmatch 'p_extra_ips' -or $fix48pNdi -notmatch 'StringToCoTaskMemUTF8') { throw 'FIX48P guard failed: local/same-host NDI discovery recovery is incomplete.' }
if ($fix48pIngest -notmatch 'EnableNdiOutput' -or $fix48pIngest -notmatch 'NdiSourceName' -or $fix48pIngest -notmatch '127\.0\.0\.1' -or $fix48pIngest -notmatch 'TimeSpan\.FromSeconds\(10\)') { throw 'FIX48P guard failed: standalone Ingest NDI sender fallback/scan is incomplete.' }
$fix48pCurrent16x9 = $fix48pMainXaml -match 'x:Name="PreviewAspectHost"' -and $fix48pMainXaml -match 'x:Name="ProgramAspectHost"' -and $fix48pMainXaml -match 'x:Name="PreviewImage" Stretch="Fill"' -and $fix48pMainXaml -match 'x:Name="ProgramImage" Stretch="Fill"' -and $fix48pMainCs -match 'FitConfidenceSurface\(PreviewAspectHost, PreviewAspectSurface, PreviewMonitorPanel\)' -and $fix48pMainCs -match 'FitConfidenceSurface\(ProgramAspectHost, ProgramAspectSurface, ProgramMonitorPanel\)'
$fix48pCompactInput = $fix48pMainXaml -match 'Width="176" Height="116"' -and $fix48pMainXaml -match '<RowDefinition Height="80"/><RowDefinition Height="36"/>'
if (-not $fix48pCurrent16x9 -or $fix48pMainXaml -notmatch 'EmbeddedMediaUp_Click' -or $fix48pMainCs -notmatch 'EmbeddedMediaUp_Click' -or -not $fix48pCompactInput) { throw 'FIX48P guard failed: current true-16:9/file-up/compact-input-preview recovery is incomplete.' }
if ($fix48pMainXaml -notmatch 'TargetType="DataGridCell"' -or $fix48pMainXaml -notmatch 'DataContext\.PlaylistFontSize' -or $fix48pMainXaml -match 'MediaDetails, Mode=OneWay\}" Foreground="#758089" FontSize="7\.5"') { throw 'FIX48P guard failed: Playout playlist text-size propagation is incomplete.' }
foreach ($guard in @('CornerRadiusAsPercent','CornerRadiusTopLeftPercent','ScaleZ','RotationX','RotationY','AnchorX','AnchorY','AnchorZ','Perspective','IsPrecomposition')) { if ($fix48pModels -notmatch [regex]::Escape($guard)) { throw "FIX48P guard failed: CG 3D/percent/precompose model missing $guard." } }
foreach ($guard in @('RADIUS AS % OF SHORTEST SIDE','PRECOMPOSE SELECTED','TimelineZoomIn_Click','TimelineZoomOut_Click','TimelineZoomFit_Click','TimelineLayerScroll','CgCheckerBrush','3D TRANSFORM','GRID','Anchor Z','SLIDE','SOFT REVEAL','FLIP','WIPE WORDS')) { if (($fix48pCgXaml + $fix48pCgCs) -notmatch [regex]::Escape($guard)) { throw "FIX48P guard failed: CG Editor workflow missing $guard." } }
if ($fix48pCgEngine -notmatch 'ApplyLayerTransform' -or $fix48pCgEngine -notmatch 'PerspectiveScale' -or $fix48pCgEngine -notmatch 'SLIDE LETTERS' -or $fix48pCgCompositor -notmatch 'CornerRadiusAsPercent') { throw 'FIX48P guard failed: CG runtime 3D/percent/text-motion implementation is incomplete.' }
if ($fix48pMulti -match '_vm\.Playlist\.Take\(' -or $fix48pMulti -match '_vm\.CgProjects\.Take\(' -or $fix48pMulti -notmatch '_vm\.InputSources\.Where\(x=>x\.IsLiveSource\)\.Take\(12\)' -or $fix48pMulti -notmatch 'NdiDiscoveryService\.ScanAsync' -or $fix48pMulti -notmatch 'channels\.Take\(16\)') { throw 'FIX48P guard failed: Multiview source/output wall still mirrors playlist/CG catalog or lacks live NDI/channel sources.' }
Write-Host '  NDI discovery, responsive Playout, CG 3D/precompose/timeline tools and source-only Multiview guards passed.' -ForegroundColor Green

Write-Host 'Checking FIX48Q CG live-source / one-sided squeeze / layer workflow guards...' -ForegroundColor Cyan
$fix48qModels = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Models\CgModels.cs'), [System.Text.Encoding]::UTF8)
$fix48qCgXaml = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml'), [System.Text.Encoding]::UTF8)
$fix48qCgCs = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml.cs'), [System.Text.Encoding]::UTF8)
$fix48qCompositor = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\CgCompositor.cs'), [System.Text.Encoding]::UTF8)
$fix48qDemo = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\CgDemoFactory.cs'), [System.Text.Encoding]::UTF8)
foreach ($guard in @('VideoSourceKind','VideoInputFormat','VideoInputOptions','VideoDevice','AudioDevice','VideoIsLiveSource','VideoCaptureWidth','VideoCaptureHeight','VideoSourceFrameRate','SqueezeHorizontalMode')) { if ($fix48qModels -notmatch [regex]::Escape($guard)) { throw "FIX48Q guard failed: CG live-source/squeeze model missing $guard." } }
foreach ($guard in @('VIDEO SOURCE','AddVideoSource_Click','DrawVideoSource_Click','ConfigureVideoSource_Click','PreviewCanvas_PreviewMouseRightButtonDown','SqueezeHorizontalModes','MOVE UP ONE LAYER','BRING TO FRONT','SEND TO BACK','_pendingVideoSourceLayer','_drawVideoSourceLayer')) { if (($fix48qCgXaml + $fix48qCgCs) -notmatch [regex]::Escape($guard)) { throw "FIX48Q guard failed: CG Editor live-source/layer workflow missing $guard." } }
if ($fix48qCgXaml -match 'Content="DRAW VIDEO"') { throw 'FIX48Q guard failed: obsolete duplicate DRAW VIDEO action returned; VIDEO SOURCE must own source selection + canvas draw.' }
foreach ($guard in @('InputSourceWindow','ModifierKeys.Alt','Key.Home','Key.End','ReorderSelectedLayers')) { if ($fix48qCgCs -notmatch [regex]::Escape($guard)) { throw "FIX48Q guard failed: CG Editor source chooser/keyboard ordering missing $guard." } }
foreach ($guard in @('BuildVideoSourceItem','VideoSourceSignature','new VideoDecoder(_item)','sourceKind.Equals("NDI"','"LEFT" => Math.Max(0, source.Width - tw)','"RIGHT" => 0')) { if ($fix48qCompositor -notmatch [regex]::Escape($guard)) { throw "FIX48Q guard failed: CG runtime live-source/one-sided squeeze missing $guard." } }
if ($fix48qDemo -notmatch 'SqueezeHorizontalMode="Left"') { throw 'FIX48Q guard failed: bundled L-shape demo is not configured for left-side squeeze.' }
Write-Host '  Drawable video/live sources, left/right squeeze, right-click alignment/order and keyboard layer ordering guards passed.' -ForegroundColor Green

Write-Host 'Checking FIX48R CG transitions / After Effects / blend-mode guards...' -ForegroundColor Cyan
$fix48rChannel = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Models\ChannelModels.cs'), [System.Text.Encoding]::UTF8)
$fix48rModels = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Models\CgModels.cs'), [System.Text.Encoding]::UTF8)
$fix48rControllerXaml = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgControllerWindow.xaml'), [System.Text.Encoding]::UTF8)
$fix48rControllerCs = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgControllerWindow.xaml.cs'), [System.Text.Encoding]::UTF8)
$fix48rMainVm = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\ViewModels\MainViewModel.cs'), [System.Text.Encoding]::UTF8)
$fix48rCompositor = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\CgCompositor.cs'), [System.Text.Encoding]::UTF8)
$fix48rOutput = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Outputs\CgGraphicsOutputEngine.cs'), [System.Text.Encoding]::UTF8)
$fix48rCgXaml = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml'), [System.Text.Encoding]::UTF8)
$fix48rCgCs = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml.cs'), [System.Text.Encoding]::UTF8)
$fix48rCgEngine = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\CgAnimationEngine.cs'), [System.Text.Encoding]::UTF8)
$fix48rAePath = Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\AfterEffectsImportService.cs'
if (-not (Test-Path $fix48rAePath)) { throw 'FIX48R guard failed: After Effects bridge source is missing.' }
$fix48rAe = [System.IO.File]::ReadAllText($fix48rAePath, [System.Text.Encoding]::UTF8)
foreach ($guard in @('Transition','TransitionSeconds')) { if ($fix48rChannel -notmatch [regex]::Escape($guard)) { throw "FIX48R guard failed: CG transition command model missing $guard." } }
foreach ($guard in @('TAKE TRANSITION','TakeTransitions','TakeTransitionSeconds','RouteAsync("TAKE"','Fade','Wipe','Zoom')) { if (($fix48rControllerXaml + $fix48rControllerCs) -notmatch [regex]::Escape($guard)) { throw "FIX48R guard failed: optional Controller TAKE transition missing $guard." } }
foreach ($guard in @('ControllerCgTransitionState','CompositeTransition','PLAY is additive')) { if ($fix48rMainVm -notmatch [regex]::Escape($guard)) { throw "FIX48R guard failed: Playout transition/additive routing missing $guard." } }
foreach ($guard in @('CompositeTransition','BlendBitmapIntoTarget','MULTIPLY','SCREEN','OVERLAY','COLOR DODGE','COLOR BURN','HARD LIGHT','SOFT LIGHT','DIFFERENCE','EXCLUSION','SUBTRACT','DIVIDE')) { if ($fix48rCompositor -notmatch [regex]::Escape($guard)) { throw "FIX48R guard failed: compositor transition/blend implementation missing $guard." } }
if ($fix48rOutput -notmatch 'SetProgram\(CgProject project, string transition' -or $fix48rOutput -notmatch 'CompositeTransition') { throw 'FIX48R guard failed: standalone CG alpha/key output transition path is incomplete.' }
foreach ($guard in @('BlendMode','ExternalProjectSource','ExternalComposition','ExternalProjectKind')) { if ($fix48rModels -notmatch [regex]::Escape($guard)) { throw "FIX48R guard failed: CG media/After Effects model missing $guard." } }
foreach ($guard in @('AFTER EFFECTS','Blend mode','ImportAfterEffects_Click','ImportAfterEffectsProjectAsync','BlendModes')) { if (($fix48rCgXaml + $fix48rCgCs) -notmatch [regex]::Escape($guard)) { throw "FIX48R guard failed: CG Editor AE/blend UI missing $guard." } }
if ($fix48rCgEngine -notmatch '"BlendMode"') { throw 'FIX48R guard failed: AUTO KEY does not capture BlendMode.' }
foreach ($guard in @('aerender.exe','KASHTRIX_AFTERFX_AERENDER','-project','-comp','-OMtemplate','frame_[#####].png','.aep','.aepx','.aet')) { if ($fix48rAe -notmatch [regex]::Escape($guard)) { throw "FIX48R guard failed: After Effects aerender bridge missing $guard." } }
Write-Host '  Optional TAKE transitions, AE aerender import and media blend-mode guards passed.' -ForegroundColor Green

Write-Host 'Checking FIX48S per-element mask / reveal guards...' -ForegroundColor Cyan
$fix48sModels = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Models\CgModels.cs'), [System.Text.Encoding]::UTF8)
$fix48sCgXaml = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml'), [System.Text.Encoding]::UTF8)
$fix48sCgCs = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml.cs'), [System.Text.Encoding]::UTF8)
$fix48sEngine = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\CgAnimationEngine.cs'), [System.Text.Encoding]::UTF8)
$fix48sCompositor = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\CgCompositor.cs'), [System.Text.Encoding]::UTF8)
$fix48sValidator = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\CgDemoValidator.cs'), [System.Text.Encoding]::UTF8)
foreach ($guard in @('MaskEnabled','MaskShape','MaskX','MaskY','MaskWidth','MaskHeight','MaskCornerRadius','MaskFeather','MaskInvert')) { if ($fix48sModels -notmatch [regex]::Escape($guard)) { throw "FIX48S guard failed: CG mask model missing $guard." } }
foreach ($guard in @('ELEMENT MASK','MASK FROM ELEMENT','DRAW MASK','CLEAR MASK','MaskShapes','MaskFromElement_Click','DrawMask_Click','ClearMask_Click','BuildPreviewMaskGeometry')) { if (($fix48sCgXaml + $fix48sCgCs) -notmatch [regex]::Escape($guard)) { throw "FIX48S guard failed: CG Editor mask workflow missing $guard." } }
foreach ($guard in @('MaskEnabled','MaskShape','MaskX','MaskY','MaskWidth','MaskHeight','MaskCornerRadius','MaskFeather','MaskInvert')) { if ($fix48sEngine -notmatch [regex]::Escape($guard)) { throw "FIX48S guard failed: AUTO KEY mask snapshot missing $guard." } }
foreach ($guard in @('DrawLayerCore','ApplyLayerMask','MaskEnabled','MaskInvert','MaskFeather','ROUNDED RECTANGLE','ELLIPSE','fixed project/canvas mask','BlendBitmapIntoTarget')) { if ($fix48sCompositor -notmatch [regex]::Escape($guard)) { throw "FIX48S guard failed: runtime fixed-canvas mask implementation missing $guard." } }
if ($fix48sValidator -notmatch 'NormalizeMaskShape' -or $fix48sValidator -notmatch 'MaskWidth' -or $fix48sValidator -notmatch 'MaskFeather') { throw 'FIX48S guard failed: mask normalization is incomplete.' }
if ($fix48sCompositor -notmatch 'DrawLayerCore\(og,[\s\S]*forceNormalBlend: true' -or $fix48sCompositor -notmatch 'ApplyLayerMask\(overlay, visualLayer') { throw 'FIX48S guard failed: mask must be applied after layer animation/rendering.' }
Write-Host '  Fixed-canvas per-element masks, direct draw workflow, feather/invert and post-animation visibility clipping guards passed.' -ForegroundColor Green

Write-Host 'Checking FIX48T sticky-ruler build-regression guard...' -ForegroundColor Cyan
$fix48tCgXaml = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml'), [System.Text.Encoding]::UTF8)
$fix48tCgCs = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml.cs'), [System.Text.Encoding]::UTF8)
foreach ($guard in @('TimelineRulerScroll','TimelineRulerCanvas','TimelineLayerScroll','HorizontalScrollBarVisibility="Auto"','VerticalScrollBarVisibility="Auto"','TimelineLayerScroll_ScrollChanged')) { if (($fix48tCgXaml + $fix48tCgCs) -notmatch [regex]::Escape($guard)) { throw "FIX48T guard failed: synchronized sticky timeline missing $guard." } }
if ($fix48tCgCs -notmatch 'TimelineRulerScroll\?\.ScrollToHorizontalOffset\(e\.HorizontalOffset\)') { throw 'FIX48T guard failed: timeline ruler is not synchronized to horizontal layer scrolling.' }
Write-Host '  Sticky ruler is separated from vertical layer scrolling and synchronized with timeline zoom/scroll.' -ForegroundColor Green

Write-Host 'Checking FIX48U compiler / clean-package guards...' -ForegroundColor Cyan
$fix48uCgCs = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml.cs'), [System.Text.Encoding]::UTF8)
$fix48uCompositor = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\CgCompositor.cs'), [System.Text.Encoding]::UTF8)
foreach ($guard in @('var maskPoint=e.GetPosition(PreviewCanvas)','var videoPoint=e.GetPosition(PreviewCanvas)','Math.Min(_drawMaskStart.X,maskPoint.X)','Math.Min(_drawVideoStart.X,videoPoint.X)')) { if ($fix48uCgCs -notmatch [regex]::Escape($guard)) { throw "FIX48U guard failed: CG Editor compile-shadow recovery missing $guard." } }
if ($fix48uCgCs -match 'Math\.Min\(_drawMaskStart\.X,p\.X\)' -or $fix48uCgCs -match 'Math\.Min\(_drawVideoStart\.X,p\.X\)') { throw 'FIX48U guard failed: obsolete p variable remains in draw-mask/video branches.' }
if ($fix48uCompositor -notmatch '\?\(layer\.Source\?\?string\.Empty\):\(layer\.VideoDevice\?\?string\.Empty\)') { throw 'FIX48U guard failed: DirectShow video-device nullable fallback is not compile-safe.' }
Write-Host '  CG Editor CS0136 shadowing and compositor nullable regression guards passed.' -ForegroundColor Green

Write-Host 'Checking FIX48V deep UI / CG runtime guards...' -ForegroundColor Cyan
$fix48vMainXaml = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\MainWindow.xaml'), [System.Text.Encoding]::UTF8)
$fix48vMainCs = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\MainWindow.xaml.cs'), [System.Text.Encoding]::UTF8)
$fix48vTheme = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\Kashtrix.Shared\KashtrixTheme.xaml'), [System.Text.Encoding]::UTF8)
$fix48vChrome = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\WindowChromeHandlers.cs'), [System.Text.Encoding]::UTF8)
$fix48vApp = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\App.xaml.cs'), [System.Text.Encoding]::UTF8)
$fix48vCgXaml = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml'), [System.Text.Encoding]::UTF8)
$fix48vCgCs = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml.cs'), [System.Text.Encoding]::UTF8)
$fix48vModels = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Models\CgModels.cs'), [System.Text.Encoding]::UTF8)
$fix48vCompositor = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\CgCompositor.cs'), [System.Text.Encoding]::UTF8)
$fix48vControllerXaml = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgControllerWindow.xaml'), [System.Text.Encoding]::UTF8)
$fix48vControllerCs = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgControllerWindow.xaml.cs'), [System.Text.Encoding]::UTF8)
$fix48vNrcsXaml = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\Kashtrix.NRCS\MainWindow.xaml'), [System.Text.Encoding]::UTF8)
$fix48vPrompterXaml = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\Kashtrix.Prompter\MainWindow.xaml'), [System.Text.Encoding]::UTF8)
foreach ($guard in @('PreviewMonitorPanel','ProgramMonitorPanel','Stretch="Fill"','FitConfidenceSurface(PreviewAspectHost, PreviewAspectSurface, PreviewMonitorPanel)','FitConfidenceSurface(ProgramAspectHost, ProgramAspectSurface, ProgramMonitorPanel)')) { if (($fix48vMainXaml + $fix48vMainCs) -notmatch [regex]::Escape($guard)) { throw "FIX48V guard failed: exact 16:9 Playout monitor sizing missing $guard." } }
$fix48vProgramAudioPanelCount = ([regex]::Matches($fix48vMainXaml, '<!--\s*PROGRAM AUDIO\b')).Count
if ($fix48vProgramAudioPanelCount -ne 1) { throw 'FIX48V guard failed: expected exactly one structural Program Audio panel marker.' }
foreach ($guard in @('x:Name="QuickControlsPanel"','VerticalScrollBarVisibility="Auto"','PanningMode="VerticalOnly"','<WrapPanel Orientation="Horizontal">','AddCueToneEvent_Click','AddDialToneEvent_Click','AddDtmfEvent_Click','AddTcpEvent_Click','AutomationGateway_Click','CgEditor_Click')) { if ($fix48vMainXaml -notmatch [regex]::Escape($guard)) { throw "FIX48V guard failed: complete wrapped Quick Controls missing $guard." } }
foreach ($guard in @('BroadcastFaderVertical','ControlTemplate TargetType="RepeatButton"')) { if ($fix48vTheme -notmatch [regex]::Escape($guard)) { throw "FIX48V guard failed: clean fader hover track missing $guard." } }
foreach ($guard in @('DwmwaBorderColor','DwmColorNone','DwmSetWindowAttribute','ApplyCleanBorder')) { if ($fix48vChrome -notmatch [regex]::Escape($guard)) { throw "FIX48V guard failed: clean DWM window border missing $guard." } }
if ($fix48vApp -notmatch 'RegisterClassHandler' -or $fix48vApp -notmatch 'ApplyCleanWindowBorder') { throw 'FIX48V guard failed: Playout windows do not receive global clean-border handling.' }
if ($fix48vNrcsXaml -notmatch 'WindowStyle="None"' -or $fix48vPrompterXaml -notmatch 'WindowStyle="None"') { throw 'FIX48V guard failed: NRCS/Prompter clean custom chrome is missing.' }
foreach ($guard in @('GradientStops','MULTI-COLOR GRADIENT','MaskToggle_Click','TryAdjustNumericTextBox','TimelineZoomFit_Click','TimelineZoomIn_Click','TimelineZoomOut_Click','ctrl && e.Key == Key.A','VideoPreviewElement','GetVideoLayerFrame','Save CG','TextAnimationPreset_Click')) { if (($fix48vCgXaml + $fix48vCgCs + $fix48vModels + $fix48vCompositor) -notmatch [regex]::Escape($guard)) { throw "FIX48V guard failed: CG Editor deep workflow missing $guard." } }
if ($fix48vCgXaml -match 'Content="DRAW VIDEO"') { throw 'FIX48V guard failed: duplicate DRAW VIDEO action is still exposed.' }
if ($fix48vCgXaml -match 'TimelineCanvas" Height="146" MinWidth="800"' -or $fix48vCgXaml -match 'TimelineRulerCanvas" Height="24" MinWidth="800"') { throw 'FIX48V guard failed: old fixed 800px timeline minimum still blocks zoom/FIT.' }
foreach ($guard in @('AUTO','CUT','TAKE','TakeTransitions','CatalogGroups','CatalogTree_SelectedItemChanged','PREVIEW','PROGRAM')) { if (($fix48vControllerXaml + $fix48vControllerCs) -notmatch [regex]::Escape($guard)) { throw "FIX48V guard failed: CG Controller switcher/catalog workflow missing $guard." } }
if ($fix48vControllerXaml -match 'x:Type Object') { throw 'FIX48V guard failed: invalid generic TreeView DataType remains in CG Controller XAML.' }
Write-Host '  Exact 16:9 Playout, complete wrapped quick controls, clean chrome/faders, CG Editor UX/video/gradient/selection/timeline and CG Controller switcher guards passed.' -ForegroundColor Green


Write-Host 'Checking FIX48W single Program audio-meter regression guard...' -ForegroundColor Cyan
$fix48wMain = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\MainWindow.xaml'), [System.Text.Encoding]::UTF8)
$fix48wProgramAudioCount = ([regex]::Matches($fix48wMain, '<!--\s*PROGRAM AUDIO\b')).Count
if ($fix48wProgramAudioCount -ne 1) { throw 'FIX48W guard failed: Playout must contain exactly one Program Audio panel.' }
foreach ($guard in @('BroadcastFaderVertical','MasterOutputVolumePercent','AudioLeftLevel','AudioRightLevel','AudioMasterLevel','AudioLeftDb','AudioRightDb','AudioMasterDb')) {
    if ($fix48wMain -notmatch [regex]::Escape($guard)) { throw "FIX48W guard failed: single Program audio meter/fader missing $guard." }
}
if ($fix48wMain -match 'Program PFL left' -or $fix48wMain -match 'Program PFL right' -or $fix48wMain -match 'Program PFL peak') { throw 'FIX48W guard failed: obsolete removed Program PFL rail markers returned.' }
Write-Host '  Single main Program L/R/M meters and fader are present; removed duplicate rail stays removed.' -ForegroundColor Green

Write-Host 'Checking FIX48X CG Controller compatibility / routing guards...' -ForegroundColor Cyan
$fix48xControllerXaml = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgControllerWindow.xaml'), [System.Text.Encoding]::UTF8)
$fix48xControllerCs = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgControllerWindow.xaml.cs'), [System.Text.Encoding]::UTF8)
foreach ($guard in @('PREVIEW BUS','PROGRAM BUS','TAKE PREVIEW TO PROGRAM','TAKE TRANSITION','DECKLINK CARD / PORT','SCAN / FIELD ORDER','PREROLL FRAMES','complete selected CG project as one composition')) { if ($fix48xControllerXaml -notmatch [regex]::Escape($guard)) { throw "FIX48X guard failed: CG Controller compatibility/UI marker missing $guard." } }
foreach ($guard in @('TakePreviewToProgramAsync','RouteAsync("TAKE", "PROGRAM", _previewSnapshot','BuildFullProjectSnapshot','CgOutputPrerollFrames')) { if ($fix48xControllerCs -notmatch [regex]::Escape($guard)) { throw "FIX48X guard failed: CG Controller runtime routing/output feature missing $guard." } }
Write-Host '  CG Controller PREVIEW/PROGRAM routing, full-project PLAY, TAKE transition and hardware-output compatibility guards passed.' -ForegroundColor Green

Write-Host 'Checking FIX48Y consolidated post-FIX48O compatibility guards...' -ForegroundColor Cyan
$fix48yMainXaml = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\MainWindow.xaml'), [System.Text.Encoding]::UTF8)
$fix48yMainCs = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\MainWindow.xaml.cs'), [System.Text.Encoding]::UTF8)
$fix48yCgXaml = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml'), [System.Text.Encoding]::UTF8)
$fix48yCgCs = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml.cs'), [System.Text.Encoding]::UTF8)
$fix48yChrome = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\WindowChromeHandlers.cs'), [System.Text.Encoding]::UTF8)
foreach ($guard in @('PreviewAspectHost','ProgramAspectHost','Stretch="Fill"','Width="176" Height="116"','EmbeddedMediaUp_Click')) { if (($fix48yMainXaml + $fix48yMainCs) -notmatch [regex]::Escape($guard)) { throw "FIX48Y guard failed: current Playout layout missing $guard." } }
foreach ($guard in @('VIDEO SOURCE','AddVideoSource_Click','DrawVideoSource_Click','_pendingVideoSourceLayer','_drawVideoSourceLayer')) { if (($fix48yCgXaml + $fix48yCgCs) -notmatch [regex]::Escape($guard)) { throw "FIX48Y guard failed: unified CG video-source draw workflow missing $guard." } }
if ($fix48yCgXaml -match 'Content="DRAW VIDEO"') { throw 'FIX48Y guard failed: duplicate DRAW VIDEO UI returned.' }
foreach ($guard in @('DwmwaBorderColor','DwmColorNone','DwmSetWindowAttribute','ApplyCleanBorder')) { if ($fix48yChrome -notmatch [regex]::Escape($guard)) { throw "FIX48Y guard failed: current clean-window DWM implementation missing $guard." } }
Write-Host '  Historical FIX48P/Q/V assumptions are reconciled with the current Playout/CG/chrome architecture.' -ForegroundColor Green


Write-Host 'Checking FIX48Z final pre-restore verifier consistency guard...' -ForegroundColor Cyan
$fix48zMainXaml = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\MainWindow.xaml'), [System.Text.Encoding]::UTF8)
$fix48zProgramPanelCount = ([regex]::Matches($fix48zMainXaml, '<!--\s*PROGRAM AUDIO\b')).Count
if ($fix48zProgramPanelCount -ne 1) { throw 'FIX48Z guard failed: structural Program Audio panel count must be exactly one.' }
if (([regex]::Matches($fix48zMainXaml, 'Program Audio', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)).Count -lt 1) { throw 'FIX48Z guard failed: Program Audio operator surface is missing.' }
if ($fix48zMainXaml -notmatch 'Mute Program Audio') { throw 'FIX48Z guard failed: Program Audio menu command is missing.' }
Write-Host '  Structural panel count is isolated from case-insensitive menu text; all current guards are ready for restore/build.' -ForegroundColor Green


Write-Host 'Checking FIX49A operator-dock / CG crash / icon regression guards...' -ForegroundColor Cyan
$fix49aMainXaml = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\MainWindow.xaml'), [System.Text.Encoding]::UTF8)
$fix49aMainCs = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\MainWindow.xaml.cs'), [System.Text.Encoding]::UTF8)
$fix49aCgXaml = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml'), [System.Text.Encoding]::UTF8)
$fix49aCgCs = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml.cs'), [System.Text.Encoding]::UTF8)
$fix49aModels = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Models\CgModels.cs'), [System.Text.Encoding]::UTF8)
$fix49aCompositor = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\CgCompositor.cs'), [System.Text.Encoding]::UTF8)
$fix49aControllerXaml = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgControllerWindow.xaml'), [System.Text.Encoding]::UTF8)
$fix49aControllerCs = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgControllerWindow.xaml.cs'), [System.Text.Encoding]::UTF8)
$fix49aProject = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\BroadcastPlayout.App.csproj'), [System.Text.Encoding]::UTF8)
$fix49aNewDialogPath = Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\NewCgProjectDialog.cs'
if (-not (Test-Path $fix49aNewDialogPath)) { throw 'FIX49A guard failed: New CG Design resolution/FPS dialog source is missing.' }
$fix49aNewDialog = [System.IO.File]::ReadAllText($fix49aNewDialogPath, [System.Text.Encoding]::UTF8)
foreach ($guard in @('x:Name="QuickControlsPanel"','VerticalScrollBarVisibility="Auto"','PanningMode="VerticalOnly"','<WrapPanel Orientation="Horizontal">','LINE TONE','DIAL TONE','DTMF','TCP','MOS','CG DESIGN')) { if ($fix49aMainXaml -notmatch [regex]::Escape($guard)) { throw "FIX49A guard failed: restored complete Quick Controls missing $guard." } }
$fix49aQuickClickCount = ([regex]::Matches($fix49aMainXaml, 'Style="\{StaticResource QuickButton\}"')).Count
if ($fix49aQuickClickCount -lt 24) { throw "FIX49A guard failed: Quick Controls button set is incomplete ($fix49aQuickClickCount)." }
foreach ($guard in @('MediaDockExpander_Expanded','MediaDockExpander_Collapsed','NowPlayingCard','NextCard','UpNextCard','EventLogCard','SetMediaDockFocus(true)','MediaDockRow.Height = new GridLength(1, GridUnitType.Star)')) { if (($fix49aMainXaml + $fix49aMainCs) -notmatch [regex]::Escape($guard)) { throw "FIX49A guard failed: full-height Media Dock focus workflow missing $guard." } }
foreach ($guard in @('TogglePlayoutPanel_Click','RestorePlayoutPanels_Click','TopOperatorPanel','RundownTimelinePanel','PlaylistPanel','QuickControlsPanel','RightSidebarPanel')) { if (($fix49aMainXaml + $fix49aMainCs) -notmatch [regex]::Escape($guard)) { throw "FIX49A guard failed: Playout removable/re-addable panel workflow missing $guard." } }
foreach ($guard in @('kashtrix-playout.ico','NotifyIcon','_trayDrawingIcon','ApplicationIcon>Branding\kashtrix-playout.ico')) { if (($fix49aMainCs + $fix49aProject) -notmatch [regex]::Escape($guard)) { throw "FIX49A guard failed: Playout taskbar/tray executable icon chain missing $guard." } }
foreach ($guard in @('FrameRate','double.IsFinite(value)','GradientStops')) { if ($fix49aModels -notmatch [regex]::Escape($guard)) { throw "FIX49A guard failed: finite gradient/FPS model recovery missing $guard." } }
foreach ($guard in @('SanitizeProjectForEditor','FiniteUnit','double.IsFinite(project.FrameRate)','_selectedLayerIds','ToggleLayerSelection','LayerList_PreviewMouseLeftButtonDown','SelectTimelineLayer','SelectionMode="Extended"','ToggleEditorPanel_Click','RestoreEditorPanels_Click')) { if (($fix49aCgXaml + $fix49aCgCs) -notmatch [regex]::Escape($guard)) { throw "FIX49A guard failed: CG Editor safe-load/multi-select/panel workflow missing $guard." } }
foreach ($guard in @('NewCgProjectDialog','ProjectFrameRate','ProjectWidth','ProjectHeight','23.976','59.94','3840 x 2160')) { if (($fix49aCgCs + $fix49aNewDialog) -notmatch [regex]::Escape($guard)) { throw "FIX49A guard failed: New CG Design resolution/FPS workflow missing $guard." } }
foreach ($guard in @('double.IsFinite(layer.GradientAngle)','double.IsFinite(stop.Offset)','Math.Clamp(stop.Offset, 0, 1)')) { if ($fix49aCompositor -notmatch [regex]::Escape($guard)) { throw "FIX49A guard failed: runtime non-finite gradient sanitization missing $guard." } }
foreach ($guard in @('PANELS','ToggleControllerPanel_Click','RestoreControllerPanels_Click','PreviewBusPanel','SwitcherPanel','ProgramBusPanel','CatalogPanel','LayersPanel','TargetsPanel')) { if (($fix49aControllerXaml + $fix49aControllerCs) -notmatch [regex]::Escape($guard)) { throw "FIX49A guard failed: CG Controller removable/re-addable panel workflow missing $guard." } }
if ($fix49aCgXaml -match 'Content="DRAW VIDEO"') { throw 'FIX49A guard failed: duplicate DRAW VIDEO action returned.' }
Write-Host '  Quick Controls, full-height Media Dock, independent docks/panels, safe CG gradient loading, timeline multi-select, New Design format/FPS and Playout icon guards passed.' -ForegroundColor Green

Write-Host 'Checking FIX49B New CG Design compiler-regression guard...' -ForegroundColor Cyan
$fix49bDialogPath = Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\NewCgProjectDialog.cs'
if (-not (Test-Path $fix49bDialogPath)) { throw 'FIX49B guard failed: NewCgProjectDialog.cs is missing.' }
$fix49bDialog = [System.IO.File]::ReadAllText($fix49bDialogPath, [System.Text.Encoding]::UTF8)
foreach ($guard in @('var sizeGrid = new Grid()','AddRow(root, 2, "WIDTH / HEIGHT", sizeGrid)','AddRow(Grid root, int row, string label, FrameworkElement control)')) {
    if ($fix49bDialog -notmatch [regex]::Escape($guard)) { throw "FIX49B guard failed: New CG Design compile recovery missing $guard." }
}
if ($fix49bDialog -match 'AddRow\(Grid root, int row, string label, Control control\)') { throw 'FIX49B guard failed: invalid Control-only AddRow signature returned.' }
Write-Host '  New CG Design accepts Grid/TextBox/ComboBox rows through FrameworkElement; CS1503 regression guard passed.' -ForegroundColor Green

Write-Host 'Checking FIX49C CG runtime / selection / controller-view / tray guards...' -ForegroundColor Cyan
$fix49cEditor = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml.cs'))
$fix49cEditorXaml = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml'))
$fix49cModels = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Models\CgModels.cs'))
$fix49cSanitizerPath = Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\CgProjectSanitizer.cs'
$fix49cMain = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\MainWindow.xaml.cs'))
$fix49cController = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgControllerWindow.xaml.cs'))
$fix49cControllerXaml = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgControllerWindow.xaml'))
$fix49cUsers = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\UserManagementWindow.xaml.cs'))
$fix49cConfidencePath = Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\ConfidenceMonitorWindow.cs'
if (-not (Test-Path $fix49cSanitizerPath) -or -not (Test-Path $fix49cConfidencePath)) { throw 'FIX49C guard failed: sanitizer or confidence-monitor source is missing.' }
$fix49cSanitizer = [System.IO.File]::ReadAllText($fix49cSanitizerPath)
foreach ($guard in @('CgProjectSanitizer.Sanitize(project)','double.IsFinite(value)?Math.Clamp(value,.1,12.0):1.0','SelectTimelineLayer','ModifierKeys.Control','Precomposition = child','GroupKeyframes.Clear()')) {
    if ($fix49cEditor -notmatch [regex]::Escape($guard)) { throw "FIX49C guard failed: CG Editor recovery missing $guard." }
}
foreach ($guard in @('double.IsFinite(project.FrameRate)','double.IsFinite(stop.Offset)','layer.GradientStops','group.Keyframes')) {
    if ($fix49cSanitizer -notmatch [regex]::Escape($guard)) { throw "FIX49C guard failed: persisted CG finite-value sanitizer missing $guard." }
}
if ($fix49cModels -notmatch 'DurationSeconds.*double\.IsFinite' -or $fix49cModels -notmatch 'TimeSeconds.*double\.IsFinite') { throw 'FIX49C guard failed: model finite-value setters are incomplete.' }
foreach ($guard in @('CatalogCards','CatalogListVisibility','CatalogGridVisibility','CreateCatalogThumbnail','CatalogList_Click','CatalogGrid_Click')) {
    if (($fix49cController + $fix49cControllerXaml) -notmatch [regex]::Escape($guard)) { throw "FIX49C guard failed: CG Controller list/grid catalog missing $guard." }
}
foreach ($guard in @('Open Preview Monitor','Open Program Monitor','Users & Roles (Admin)','OpenPreviewOutputWindow','OpenProgramOutputWindow','OpenUserManagementFromTray')) {
    if ($fix49cMain -notmatch [regex]::Escape($guard)) { throw "FIX49C guard failed: Playout tray workflow missing $guard." }
}
if ($fix49cUsers -notmatch 'AppSession\.IsAdmin') { throw 'FIX49C guard failed: User Management is not admin-gated.' }
Write-Host '  Finite CG loading, stable timeline zoom, nested precompose, Ctrl/Shift selection, catalog list/grid cards and admin/tray confidence windows passed.' -ForegroundColor DarkGray

Write-Host 'Checking FIX49D UI / CG manipulation / 16:9 / Program cadence guards...' -ForegroundColor Cyan
$fix49dTheme = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\Kashtrix.Shared\KashtrixTheme.xaml'), [System.Text.Encoding]::UTF8)
$fix49dMainXaml = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\MainWindow.xaml'), [System.Text.Encoding]::UTF8)
$fix49dEditorXaml = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml'), [System.Text.Encoding]::UTF8)
$fix49dEditor = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml.cs'), [System.Text.Encoding]::UTF8)
$fix49dControllerXaml = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgControllerWindow.xaml'), [System.Text.Encoding]::UTF8)
$fix49dMainCode = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\MainWindow.xaml.cs'), [System.Text.Encoding]::UTF8)
$fix49dMainVm = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\ViewModels\MainViewModel.cs'), [System.Text.Encoding]::UTF8)
$fix49dEngine = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Engine\PlayoutEngine.cs'), [System.Text.Encoding]::UTF8)
foreach ($guard in @('Padding="{TemplateBinding Padding}"','HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"','<Setter Property="MinHeight" Value="22"/>')) {
    if ($fix49dTheme -notmatch [regex]::Escape($guard)) { throw "FIX49D guard failed: shared compact/high-DPI button template missing $guard." }
}
if ($fix49dMainXaml -notmatch [regex]::Escape('<Setter Property="MinHeight" Value="22"/><Setter Property="Height" Value="30"/>')) { throw 'FIX49D guard failed: compact SmallTool height conflict returned.' }
foreach ($guard in @('UseLayoutRounding="False" SnapsToDevicePixels="False"','ApplyInteractivePreviewGeometry(project, l);','_previewLayerVisuals[layer] = el;','CompositionTarget.Rendering += InteractiveComposition_Rendering;')) {
    if (($fix49dEditorXaml + $fix49dEditor) -notmatch [regex]::Escape($guard)) { throw "FIX49D guard failed: ultra-smooth CG direct manipulation missing $guard." }
}
if (([regex]::Matches($fix49dControllerXaml, 'Width="640" Height="360"')).Count -lt 4 -or ([regex]::Matches($fix49dControllerXaml, 'HorizontalAlignment="Center" VerticalAlignment="Center"')).Count -lt 2) { throw 'FIX49D guard failed: CG Controller Preview/Program surfaces are not centered 16:9 frames.' }
foreach ($guard in @('ProgramConfidenceFpsCeiling = 60','QueueProgramOutput(programFrame)','_pendingProgramOutputFrame','SpinWait.SpinUntil','RefreshNdiStatusFromSender()')) {
    if (($fix49dMainCode + $fix49dMainVm) -notmatch [regex]::Escape($guard)) { throw "FIX49D guard failed: smooth Program output path missing $guard." }
}
foreach ($guard in @('delay > 0.006','Thread.Sleep(1)')) {
    if ($fix49dEngine -notmatch [regex]::Escape($guard)) { throw "FIX49D guard failed: fine Program frame deadline pacing missing $guard." }
}
Write-Host '  Shared buttons, direct-manipulation CG canvas, centered 16:9 controller buses and low-latency Program cadence guards passed.' -ForegroundColor Green

Write-Host 'Checking FIX49E channel-format / CG selection / safe-area / native-precomp guards...' -ForegroundColor Cyan
$fix49eMainVm = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\ViewModels\MainViewModel.cs'), [System.Text.Encoding]::UTF8)
$fix49eMainXaml = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\MainWindow.xaml'), [System.Text.Encoding]::UTF8)
$fix49eEditorXaml = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml'), [System.Text.Encoding]::UTF8)
$fix49eEditor = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml.cs'), [System.Text.Encoding]::UTF8)
$fix49eModels = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Models\CgModels.cs'), [System.Text.Encoding]::UTF8)
$fix49eCompositor = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\CgCompositor.cs'), [System.Text.Encoding]::UTF8)
$fix49eFormatPath = Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Services\ChannelFormatPreset.cs'
if (-not (Test-Path $fix49eFormatPath)) { throw 'FIX49E guard failed: ChannelFormatPreset.cs is missing.' }
$fix49eFormat = [System.IO.File]::ReadAllText($fix49eFormatPath, [System.Text.Encoding]::UTF8)
foreach ($guard in @('1080p25','1080p50','1080i50','Interlaced Upper First','nominalRate / 2.0','ChannelFormatPreset.Stamp','ProgramCadenceLoop','_programChannelScaler.Scale(sourceFrame, spec.Width, spec.Height)','ProgramFormatText')) {
    if (($fix49eFormat + $fix49eMainVm + $fix49eMainXaml) -notmatch [regex]::Escape($guard)) { throw "FIX49E guard failed: selected channel raster/cadence path missing $guard." }
}
foreach ($guard in @('Colors.LightGray','Colors.White','LayerList_PreviewMouseLeftButtonDown','ModifierKeys.Control','LayerList_MouseDoubleClick','BackToParentComposition_Click','CompositionPathText','var project = RootComposition')) {
    if (($fix49eEditorXaml + $fix49eEditor) -notmatch [regex]::Escape($guard)) { throw "FIX49E guard failed: CG Editor safe-area/selection/navigation feature missing $guard." }
}
foreach ($guard in @('public CgProject? Precomposition','Type = "Precomp"','Precomposition = child','EnterPrecomposition','RenderProjectSurface','case "PRECOMP"')) {
    if (($fix49eModels + $fix49eEditor + $fix49eCompositor) -notmatch [regex]::Escape($guard)) { throw "FIX49E guard failed: After Effects-style nested precomposition missing $guard." }
}
Write-Host '  Selected channel Program format, gray/white safe guides, explicit Ctrl layer selection and editable nested precompositions passed.' -ForegroundColor Green

Write-Host 'Checking FIX49F timeline exact-selection / real-zoom / precompose guards...' -ForegroundColor Cyan
$fix49fEditorXaml = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml'), [System.Text.Encoding]::UTF8)
$fix49fEditor = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml.cs'), [System.Text.Encoding]::UTF8)
foreach ($guard in @('_selectedLayerIds','SetLayerSelection','ToggleLayerSelection','Layers.Where(IsLayerSelected)','Mode = "row"','Ctrl-click toggles this layer','e.ClickCount >= 2','EnterPrecomposition(clickedLayer)')) {
    if ($fix49fEditor -notmatch [regex]::Escape($guard)) { throw "FIX49F guard failed: exact timeline layer selection/precomp navigation missing $guard." }
}
foreach ($guard in @('TimelineBasePixelsPerSecond','TimelineTrackWidth()','TimelineTimeToX','TimelineMajorTickSeconds','TimelineXToTime','point.X - _dragStartPoint.X) / Math.Max(1, TimelineTrackWidth())')) {
    if ($fix49fEditor -notmatch [regex]::Escape($guard)) { throw "FIX49F guard failed: real timeline zoom/geometry missing $guard." }
}
foreach ($guard in @('TimelineZoomOut_Click','TimelineZoomIn_Click','Zoom out the real timeline time scale','Zoom in the real timeline time scale','CTRL-CLICK ANY ROW/CLIP TO MULTI-SELECT')) {
    if ($fix49fEditorXaml -notmatch [regex]::Escape($guard)) { throw "FIX49F guard failed: timeline selection/zoom UI missing $guard." }
}
if ($fix49fEditor -notmatch [regex]::Escape('var insertionIndex = Math.Clamp(selectedIndices.Min(), 0, Layers.Count);')) { throw 'FIX49F guard failed: precompose insertion point is not stable for non-contiguous timeline selections.' }
Write-Host '  Whole-row Ctrl selection, operation-stable multi-selection, double-click PRECOMP navigation and real time-scale zoom passed.' -ForegroundColor Green

Write-Host 'Checking FIX49H verifier/source selection consistency guards...' -ForegroundColor Cyan
$fix49hEditorXaml = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml'), [System.Text.Encoding]::UTF8)
$fix49hEditor = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\BroadcastPlayout.App\Views\CgEditorWindow.xaml.cs'), [System.Text.Encoding]::UTF8)
$fix49hVerifier = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot 'tools\Verify-And-Build.ps1'), [System.Text.Encoding]::UTF8)
foreach ($guard in @('_selectedLayerIds','SetLayerSelection','ToggleLayerSelection','SyncLayerListSelectionVisuals','LayerList_PreviewMouseLeftButtonDown','SelectTimelineLayer','Mode = "row"','TimelineZoomIn_Click','TimelineZoomOut_Click','TimelineZoomFit_Click')) {
    if (($fix49hEditorXaml + $fix49hEditor) -notmatch [regex]::Escape($guard)) { throw "FIX49H guard failed: current CG timeline selection/zoom implementation missing $guard." }
}
$fix49hRetiredUnselect = "'LayerList." + "UnselectAll'"
$fix49hRetiredLabel = "'CTRL/SHIFT " + "MULTI-SELECT'"
if ($fix49hVerifier -match [regex]::Escape($fix49hRetiredUnselect)) { throw 'FIX49H guard failed: retired ListBox-only deselection verifier signature returned.' }
if ($fix49hVerifier -match [regex]::Escape($fix49hRetiredLabel)) { throw 'FIX49H guard failed: retired multi-select label verifier signature returned.' }
Write-Host '  Verifier selection guards now follow the current exact-selection architecture; stale FIX48J/FIX49A signatures are removed.' -ForegroundColor Green

Write-Host 'Restoring NuGet packages for the full solution...' -ForegroundColor Cyan
# NETSDK1134: do NOT pass -r/--runtime to a .sln. Every project already owns its RID.
& dotnet restore $solution -p:Platform=x64 -p:KashtrixBuildRoot="$env:KASHTRIX_BUILD_ROOT"
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE" }

Write-Host "Building the complete Kashtrix solution once ($Configuration x64)..." -ForegroundColor Cyan
& dotnet build $solution -c $Configuration -p:Platform=x64 -p:KashtrixBuildRoot="$env:KASHTRIX_BUILD_ROOT" --no-restore
if ($LASTEXITCODE -ne 0) { throw "solution build failed with exit code $LASTEXITCODE" }

Write-Host 'Validating every application output from the single solution build...' -ForegroundColor Cyan
$built = @()
$appPaths = @{}
$buildFailures = @()
foreach ($entry in $projectMap) {
    $projectPath = Join-Path $ProjectRoot $entry.Path
    $targetPath = (& dotnet msbuild $projectPath -nologo -getProperty:TargetPath -p:Configuration=$Configuration -p:Platform=x64 -p:KashtrixBuildRoot="$env:KASHTRIX_BUILD_ROOT" | Select-Object -Last 1).Trim()
    if ([string]::IsNullOrWhiteSpace($targetPath) -or -not (Test-Path $targetPath)) {
        $buildFailures += "$($entry.Name) (TargetPath missing)"
        Write-Host "     OUTPUT FAILED: TargetPath was not created for $($entry.Name)." -ForegroundColor Red
        continue
    }
    $exePath = [System.IO.Path]::ChangeExtension($targetPath, '.exe')
    if (-not (Test-Path $exePath)) {
        $buildFailures += "$($entry.Name) (EXE missing)"
        Write-Host "     OUTPUT FAILED: standalone EXE was not created ($exePath)." -ForegroundColor Red
        continue
    }
    Write-Host "     EXE: $exePath" -ForegroundColor DarkGreen
    $built += $entry.Name
    $appPaths[$entry.Name] = $exePath
}
if ($buildFailures.Count -gt 0) {
    throw ("Application build failures: " + ($buildFailures -join '; '))
}

# Publish exact app locations for the in-product Program Launcher. This prevents it from
# selecting an older executable from another configuration/framework build directory.
$suiteFolder = Join-Path $env:LOCALAPPDATA 'KashtrixPlayout'
New-Item -ItemType Directory -Force -Path $suiteFolder | Out-Null
$suiteManifest = Join-Path $suiteFolder 'suite-apps.json'
$suiteRootMarker = Join-Path $suiteFolder 'suite-root.txt'
[System.IO.File]::WriteAllText($suiteRootMarker, $ProjectRoot, (New-Object System.Text.UTF8Encoding($false)))
$manifestJson = $appPaths | ConvertTo-Json -Depth 3
[System.IO.File]::WriteAllText($suiteManifest, $manifestJson, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Suite launcher manifest: $suiteManifest" -ForegroundColor DarkGreen

Write-Host 'Running standalone application startup smoke tests...' -ForegroundColor Cyan
$startupSmokeTimeoutMs = 45000
$startupFailures = @()
foreach ($smokeEntry in $projectMap) {
    $smokeName = $smokeEntry.Name
    $exe = $appPaths[$smokeName]
    if ([string]::IsNullOrWhiteSpace($exe) -or -not (Test-Path $exe)) {
        $startupFailures += "$smokeName (executable missing)"
        Write-Host "     STARTUP FAILED: executable missing for $smokeName - continuing to collect failures." -ForegroundColor Red
        continue
    }
    Write-Host "  STARTUP -> $smokeName" -ForegroundColor DarkCyan
    try { $proc = Start-Process -FilePath $exe -ArgumentList '--startup-smoke' -PassThru }
    catch {
        $startupFailures += "$smokeName (launch failed: $($_.Exception.Message))"
        Write-Host "     STARTUP FAILED: $smokeName could not launch - continuing." -ForegroundColor Red
        continue
    }
    $startupLog = Join-Path $env:LOCALAPPDATA ("KashtrixPlayout\Logs\" + ($smokeName -replace ' ', '_') + '.startup.log')
    if (-not $proc.WaitForExit($startupSmokeTimeoutMs)) {
        try { $proc.Kill() } catch { }
        if (Test-Path $startupLog) {
            Write-Host '----- startup log tail -----' -ForegroundColor DarkYellow
            Get-Content $startupLog -Tail 80 | ForEach-Object { Write-Host $_ -ForegroundColor DarkYellow }
            Write-Host '----- end startup log -----' -ForegroundColor DarkYellow
        }
        $startupFailures += "$smokeName (timeout after $startupSmokeTimeoutMs ms)"
        Write-Host "     STARTUP FAILED: $smokeName timed out - continuing to collect failures." -ForegroundColor Red
        continue
    }
    if ($proc.ExitCode -ne 0) {
        if (Test-Path $startupLog) {
            Write-Host '----- startup log tail -----' -ForegroundColor DarkYellow
            Get-Content $startupLog -Tail 80 | ForEach-Object { Write-Host $_ -ForegroundColor DarkYellow }
            Write-Host '----- end startup log -----' -ForegroundColor DarkYellow
        }
        $startupFailures += "$smokeName (exit $($proc.ExitCode))"
        Write-Host "     STARTUP FAILED: $smokeName exit $($proc.ExitCode) - continuing to collect failures." -ForegroundColor Red
        continue
    }
    Write-Host "     STARTUP OK: $smokeName" -ForegroundColor Green
}
if ($startupFailures.Count -gt 0) {
    throw ("Startup smoke failures: " + ($startupFailures -join '; '))
}

Write-Host ''
Write-Host 'FULL SOLUTION BUILD PASSED.' -ForegroundColor Green
Write-Host ("Compiled applications: " + ($built -join ', ')) -ForegroundColor Green
Write-Host 'Open BroadcastPlayout.sln in Visual Studio 2026 and use Debug/Release x64.' -ForegroundColor Green
