param(
    [switch]$LaunchApps,
    [switch]$RequireRuntime,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$SuiteDir = Join-Path $env:LOCALAPPDATA 'KashtrixPlayout'
$ManifestPath = Join-Path $SuiteDir 'suite-apps.json'
$RootMarkerPath = Join-Path $SuiteDir 'suite-root.txt'
$LogDir = Join-Path $SuiteDir 'Logs'
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
$LogPath = Join-Path $LogDir ('DemoIntegration-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log')
$script:Failures = @()
$script:Warnings = @()

function Write-Step([string]$Text, [ConsoleColor]$Color = [ConsoleColor]::Gray) {
    Write-Host $Text -ForegroundColor $Color
    Add-Content -Path $LogPath -Value $Text -Encoding ASCII
}

function Fail([string]$Text) {
    $script:Failures += $Text
    Write-Step ('FAIL: ' + $Text) Red
}

function Warn([string]$Text) {
    $script:Warnings += $Text
    Write-Step ('WARN: ' + $Text) Yellow
}

function Test-Health([string]$BaseUrl) {
    try {
        $r = Invoke-RestMethod -Uri ($BaseUrl + '/api/v1/health') -Method Get -TimeoutSec 2
        return $r.ok -eq $true
    } catch { return $false }
}

function Test-GatewayCapabilities([string]$BaseUrl, [hashtable]$Headers) {
    try {
        $body = @{ jsonrpc = '2.0'; id = 1; method = 'tools/list' } | ConvertTo-Json -Compress
        $r = Invoke-RestMethod -Uri ($BaseUrl + '/mcp') -Method Post -Headers $Headers -ContentType 'application/json' -Body $body -TimeoutSec 3
        $json = $r | ConvertTo-Json -Depth 12 -Compress
        return $json -match 'demo_load_and_test'
    } catch { return $false }
}

function Load-GatewaySettings {
    $settingsPath = Join-Path $env:APPDATA 'KashtrixPlayout\api-gateway.json'
    $settings = [pscustomobject]@{ BindHost = '127.0.0.1'; HttpPort = 9080; ApiKey = 'kashtrix-local-test' }
    if (Test-Path $settingsPath) {
        try {
            $saved = Get-Content $settingsPath -Raw | ConvertFrom-Json
            if ($saved.BindHost) { $settings.BindHost = [string]$saved.BindHost }
            if ($saved.HttpPort) { $settings.HttpPort = [int]$saved.HttpPort }
            if ($saved.ApiKey) { $settings.ApiKey = [string]$saved.ApiKey }
        } catch { Warn ('Unable to parse API Gateway settings: ' + $_.Exception.Message) }
    }
    return $settings
}

function Ensure-Build {
    $needsBuild = (-not (Test-Path $ManifestPath)) -or (-not (Test-Path $RootMarkerPath))
    if (-not $needsBuild) {
        $marker = (Get-Content $RootMarkerPath -Raw).Trim()
        if (-not [string]::Equals($marker, $ProjectRoot, [StringComparison]::OrdinalIgnoreCase)) { $needsBuild = $true }
    }
    if (-not $needsBuild) {
        try {
            $currentManifest = Get-Content $ManifestPath -Raw | ConvertFrom-Json
            $gatewayProperty = $currentManifest.PSObject.Properties['Kashtrix.ApiGateway']
            $gatewayExe = if ($null -ne $gatewayProperty) { [string]$gatewayProperty.Value } else { '' }
            if ([string]::IsNullOrWhiteSpace($gatewayExe) -or -not (Test-Path $gatewayExe)) { $needsBuild = $true }
            else {
                $exeTime = (Get-Item $gatewayExe).LastWriteTimeUtc
                foreach ($relative in @('src\Kashtrix.ApiGateway\GatewayHost.cs','src\BroadcastPlayout.App\Services\DemoIntegrationService.cs','src\BroadcastPlayout.App\Services\NrcsPlatformStore.cs')) {
                    $source = Join-Path $ProjectRoot $relative
                    if ((Test-Path $source) -and (Get-Item $source).LastWriteTimeUtc -gt $exeTime) { $needsBuild = $true; break }
                }
            }
        } catch { $needsBuild = $true }
    }
    if ($needsBuild -and -not $SkipBuild) {
        Write-Step 'Build output for this source tree was not found. Running one-click setup/build first...' Cyan
        & powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Setup-All.ps1')
        if ($LASTEXITCODE -ne 0) { throw ('Setup/build failed with exit code ' + $LASTEXITCODE) }
    }
    if (-not (Test-Path $ManifestPath)) { throw 'suite-apps.json is missing. Run SETUP-AND-BUILD.cmd first.' }
    $manifest = Get-Content $ManifestPath -Raw | ConvertFrom-Json
    return $manifest
}

function Get-AppPath($Manifest, [string]$Name) {
    $p = $Manifest.PSObject.Properties[$Name]
    if ($null -eq $p) { return $null }
    $value = [string]$p.Value
    if ([string]::IsNullOrWhiteSpace($value) -or -not (Test-Path $value)) { return $null }
    return $value
}

function Ensure-AppRunning($Manifest, [string]$Name) {
    $path = Get-AppPath $Manifest $Name
    if ([string]::IsNullOrWhiteSpace($path)) { Warn ($Name + ' executable is missing from suite manifest.'); return $false }
    $processName = [IO.Path]::GetFileNameWithoutExtension($path)
    if (Get-Process -Name $processName -ErrorAction SilentlyContinue) {
        Write-Step ($Name + ' already running.') DarkGray
        return $true
    }
    try {
        Start-Process -FilePath $path | Out-Null
        Write-Step ('Launched ' + $Name) DarkGreen
        return $true
    } catch {
        Warn ('Could not launch ' + $Name + ': ' + $_.Exception.Message)
        return $false
    }
}

function Invoke-KtxApi([string]$BaseUrl, [hashtable]$Headers, [string]$Path, [string]$Method = 'GET', $Body = $null) {
    $args = @{ Uri = ($BaseUrl + $Path); Method = $Method; Headers = $Headers; TimeoutSec = 10 }
    if ($null -ne $Body) {
        $args.ContentType = 'application/json; charset=utf-8'
        # PowerShell 5.1 may otherwise encode a .NET string request body with the active
        # Windows code page. Always send explicit UTF-8 bytes so CG template/data names can
        # contain Unicode without triggering the API Gateway JSON transcoder.
        $jsonText = ($Body | ConvertTo-Json -Depth 24 -Compress)
        $args.Body = [Text.Encoding]::UTF8.GetBytes($jsonText)
    }
    return Invoke-RestMethod @args
}

Write-Step 'KASHTRIX DEMO LOAD / INTEGRATION TEST' Cyan
Write-Step $ProjectRoot DarkGray

$manifest = Ensure-Build
$gatewaySettings = Load-GatewaySettings
$hostForUrl = if ($gatewaySettings.BindHost -eq '0.0.0.0') { '127.0.0.1' } else { $gatewaySettings.BindHost }
$baseUrl = 'http://' + $hostForUrl + ':' + $gatewaySettings.HttpPort
$headers = @{ 'X-Kashtrix-Api-Key' = $gatewaySettings.ApiKey }

$gatewayPath = Get-AppPath $manifest 'Kashtrix.ApiGateway'
if ([string]::IsNullOrWhiteSpace($gatewayPath)) { throw 'Kashtrix.ApiGateway executable is missing.' }

$gatewayNeedsStart = -not (Test-Health $baseUrl)
if (-not $gatewayNeedsStart -and -not (Test-GatewayCapabilities $baseUrl $headers)) {
    Write-Step 'An older API Gateway is running. Restarting it so the demo integration endpoint is current...' Yellow
    $gatewayProcessName = [IO.Path]::GetFileNameWithoutExtension($gatewayPath)
    Get-Process -Name $gatewayProcessName -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
    $gatewayNeedsStart = $true
}
if ($gatewayNeedsStart) {
    Write-Step 'Starting Kashtrix API Gateway...' Cyan
    Start-Process -FilePath $gatewayPath -WindowStyle Minimized | Out-Null
    $ready = $false
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Milliseconds 300
        if ((Test-Health $baseUrl) -and (Test-GatewayCapabilities $baseUrl $headers)) { $ready = $true; break }
    }
    if (-not $ready) { throw ('API Gateway did not become ready with FIX48H demo tools at ' + $baseUrl) }
}
Write-Step ('API Gateway ready: ' + $baseUrl) Green

Write-Step 'Loading 200 CG demos, NRCS professional rundown, MOS XML, playout playlist and prompter live state...' Cyan
$load = Invoke-KtxApi $baseUrl $headers '/api/v1/demo/load' 'POST'
if ($null -eq $load.data) { throw 'Demo load endpoint returned no data.' }
$demo = $load.data

foreach ($check in $demo.checks) {
    if ($check.ok) { Write-Step ('PASS: ' + $check.name + ' - ' + $check.detail) Green }
    else { Fail ($check.name + ' - ' + $check.detail) }
}

try {
    $cg = Invoke-KtxApi $baseUrl $headers '/api/v1/cg/templates'
    $cgCount = @($cg.data).Count
    if ($cgCount -ge 20) { Write-Step ('PASS: API exposes ' + $cgCount + ' CG templates.') Green }
    else { Fail ('API exposes only ' + $cgCount + ' CG templates.') }
} catch { Fail ('CG template API check: ' + $_.Exception.Message) }

try {
    $prompter = Invoke-KtxApi $baseUrl $headers ('/api/v1/prompter/' + [Uri]::EscapeDataString([string]$demo.rundownId))
    $storyCount = @($prompter.data.stories).Count
    if ($storyCount -ge 8) { Write-Step ('PASS: Prompter API reads ' + $storyCount + ' rundown stories.') Green }
    else { Fail ('Prompter rundown contains only ' + $storyCount + ' stories.') }
} catch { Fail ('Prompter API check: ' + $_.Exception.Message) }

try {
    $live = Invoke-KtxApi $baseUrl $headers ('/api/v1/nrcs/rundowns/' + [Uri]::EscapeDataString([string]$demo.rundownId) + '/live')
    if ($live.data.onAir -eq $true -and $live.data.storyId) { Write-Step ('PASS: NRCS live state shared with Prompter. Story ' + $live.data.storyId) Green }
    else { Fail 'NRCS live state is not active.' }
} catch { Fail ('NRCS live-state API check: ' + $_.Exception.Message) }

if ($demo.mosXmlPath -and (Test-Path ([string]$demo.mosXmlPath))) {
    Write-Step ('PASS: MOS XML created: ' + $demo.mosXmlPath) Green
} else {
    Fail 'MOS XML outbox file was not created.'
}

if ($LaunchApps) {
    Write-Step 'Launching operator applications...' Cyan
    [void](Ensure-AppRunning $manifest 'Kashtrix.NRCS')
    [void](Ensure-AppRunning $manifest 'Kashtrix.Prompter')
    [void](Ensure-AppRunning $manifest 'Kashtrix.CGEditor')
    [void](Ensure-AppRunning $manifest 'Kashtrix.CGController')
    [void](Ensure-AppRunning $manifest 'Kashtrix.Playout')
    Write-Step 'Playout may show the login window. Sign in, then run INTEGRATION-TEST.cmd for live command validation.' Yellow
}

if ($RequireRuntime) {
    Write-Step 'Running live integration checks against the logged-in Playout channel...' Cyan
    $channel = $null
    try {
        $channels = Invoke-KtxApi $baseUrl $headers '/api/v1/playout/channels'
        $now = [DateTime]::UtcNow
        $fresh = @($channels.data | Where-Object {
            try { ($now - ([DateTime]$_.updatedUtc).ToUniversalTime()).TotalSeconds -lt 20 } catch { $false }
        })
        $channel = $fresh | Where-Object { $_.channelId -eq $demo.channelId } | Select-Object -First 1
        if ($null -eq $channel) { $channel = $fresh | Select-Object -First 1 }
    } catch { Fail ('Playout status query failed: ' + $_.Exception.Message) }

    if ($null -eq $channel) {
        Fail 'No fresh Playout channel status. Open Kashtrix.Playout, complete login, wait for the main window, then rerun INTEGRATION-TEST.cmd.'
    } else {
        Write-Step ('Runtime channel: ' + $channel.channelId + ' / ' + $channel.channelName + ' / engine ' + $channel.engine) Green
        try {
            $reload = Invoke-KtxApi $baseUrl $headers ('/api/v1/playout/channels/' + [Uri]::EscapeDataString([string]$channel.channelId) + '/commands') 'POST' @{ action = 'reload_playlist' }
            if ($reload.ok -eq $true) { Write-Step 'PASS: Playout reload_playlist command acknowledged by running channel.' Green }
            else { Fail ('Playout command rejected: ' + $reload.code + ' ' + $reload.message) }
        } catch { Fail ('Playout runtime command: ' + $_.Exception.Message) }

        try {
            $templates = Invoke-KtxApi $baseUrl $headers '/api/v1/cg/templates'
            $template = @($templates.data | Where-Object { $_.name -like '*Crystal Headline Lower Third*' } | Select-Object -First 1)[0]
            if ($null -eq $template) { $template = @($templates.data | Select-Object -First 1)[0] }
            if ($null -eq $template) { throw 'No CG template available.' }
            $safe = -join (([string]$channel.channelId).ToUpperInvariant().ToCharArray() | ForEach-Object { if ([char]::IsLetterOrDigit($_) -or $_ -eq '-' -or $_ -eq '_') { $_ } else { '_' } })
            $inbox = Join-Path $env:LOCALAPPDATA ('KashtrixPlayout\LocalCg\' + $safe)
            $before = @()
            if (Test-Path $inbox) { $before = @(Get-ChildItem $inbox -Filter '*.json' -File | Select-Object -ExpandProperty FullName) }
            $play = Invoke-KtxApi $baseUrl $headers '/api/v1/cg-controller/play' 'POST' @{ targets = @([string]$channel.channelId); template = [string]$template.name; bus = 'PREVIEW'; layer = 90; data = @{ headline = 'KASHTRIX INTEGRATION TEST'; subheadline = 'NRCS MOS CG PLAYOUT PROMPTER' } }
            Start-Sleep -Milliseconds 1200
            $after = @()
            if (Test-Path $inbox) { $after = @(Get-ChildItem $inbox -Filter '*.json' -File | Select-Object -ExpandProperty FullName) }
            $newPending = @($after | Where-Object { $before -notcontains $_ })
            if ($play.ok -eq $true -and $newPending.Count -eq 0) { Write-Step ('PASS: CG PREVIEW command consumed by running Playout: ' + $template.name) Green }
            elseif ($play.ok -eq $true) { Fail ('CG command was queued but not consumed. Pending files: ' + $newPending.Count) }
            else { Fail 'CG controller API did not accept PREVIEW command.' }
            [void](Invoke-KtxApi $baseUrl $headers '/api/v1/cg-controller/clear' 'POST' @{ targets = @([string]$channel.channelId); bus = 'PREVIEW'; layer = 90 })
        } catch { Fail ('CG runtime integration: ' + $_.Exception.Message) }
    }

    foreach ($appName in @('Kashtrix.NRCS','Kashtrix.Prompter','Kashtrix.CGEditor','Kashtrix.CGController')) {
        $path = Get-AppPath $manifest $appName
        if ($path) {
            $pn = [IO.Path]::GetFileNameWithoutExtension($path)
            if (Get-Process -Name $pn -ErrorAction SilentlyContinue) { Write-Step ('PASS: ' + $appName + ' process is running.') Green }
            else { Warn ($appName + ' is not running; database/API integration passed but UI process was not active.') }
        }
    }
}

Write-Step ''
Write-Step ('Log: ' + $LogPath) DarkGray
if ($script:Warnings.Count -gt 0) { Write-Step ('Warnings: ' + $script:Warnings.Count) Yellow }
if ($script:Failures.Count -gt 0) {
    Write-Step ('INTEGRATION FAILED: ' + $script:Failures.Count + ' check(s) failed.') Red
    exit 20
}
Write-Step 'KASHTRIX DEMO / INTEGRATION CHECK PASSED.' Green
exit 0
