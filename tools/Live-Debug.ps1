param(
    [string]$ProjectRoot = (Resolve-Path "$PSScriptRoot\.."),
    [int]$IntervalSeconds = 2
)

# FIX32: this is a real live diagnostic recorder, not only a health poller.
# It continuously captures process/resource state, Kashtrix runtime/audit/startup logs,
# Windows crash/hang events, NDI/Virtual Output status and Kashtrix child media processes.
# Windows PowerShell 5.1 compatible by design.
$ErrorActionPreference = 'Continue'
$IntervalSeconds = [Math]::Max(1, $IntervalSeconds)
$logRoot = Join-Path $env:LOCALAPPDATA 'KashtrixPlayout\Logs'
$sessionId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-pid' + $PID
$sessionDir = Join-Path $logRoot (Join-Path 'LiveSessions' $sessionId)
New-Item $sessionDir -ItemType Directory -Force | Out-Null
$combinedLog = Join-Path $sessionDir 'combined-live-debug.log'
$processCsv = Join-Path $sessionDir 'process-metrics.csv'
$windowsEventLog = Join-Path $sessionDir 'windows-events.log'
$manifestPath = Join-Path $sessionDir 'session-info.txt'
$script:fileOffsets = @{}
$script:processCpu = @{}
$script:processSeen = @{}
$script:eventRecords = @{}
$script:lastNdiProbeUtc = [DateTime]::MinValue
$script:lastOutputProbeUtc = [DateTime]::MinValue
$script:lastGpuProbeUtc = [DateTime]::MinValue
$script:lastWindowsEventUtc = (Get-Date).AddSeconds(-5)
$script:gpuByPid = @{}
$script:lastCycleUtc = [DateTime]::UtcNow

function Write-RawLine([string]$line) {
    try { Add-Content -Path $combinedLog -Value $line -Encoding UTF8 } catch { }
}

function Log([string]$text, [ConsoleColor]$color = [ConsoleColor]::Gray, [string]$category = 'MONITOR') {
    $stamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff'
    $line = "[$stamp] [$category] $text"
    Write-RawLine $line
    try { Write-Host $line -ForegroundColor $color } catch { Write-Host $line }
}

function Safe([scriptblock]$action, $fallback = $null) {
    try { return & $action } catch { return $fallback }
}

function Write-SessionManifest {
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('Kashtrix Live Diagnostic Session')
    $lines.Add('Session: ' + $sessionId)
    $lines.Add('Started: ' + (Get-Date -Format 'O'))
    $lines.Add('Project: ' + $ProjectRoot)
    $lines.Add('Computer: ' + $env:COMPUTERNAME)
    $lines.Add('User: ' + $env:USERNAME)
    $lines.Add('PowerShell: ' + $PSVersionTable.PSVersion.ToString())
    try {
        $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
        $lines.Add(('OS: {0} {1} build {2}' -f $os.Caption, $os.Version, $os.BuildNumber))
        $lines.Add(('RAM: {0:N1} GB' -f ([double]$os.TotalVisibleMemorySize / 1MB)))
    } catch { $lines.Add('OS probe failed: ' + $_.Exception.Message) }
    try {
        foreach ($cpu in @(Get-CimInstance Win32_Processor -ErrorAction Stop)) { $lines.Add('CPU: ' + $cpu.Name) }
    } catch { }
    try {
        foreach ($gpu in @(Get-CimInstance Win32_VideoController -ErrorAction Stop)) { $lines.Add(('GPU: {0} | driver {1}' -f $gpu.Name, $gpu.DriverVersion)) }
    } catch { }
    try {
        foreach ($snd in @(Get-CimInstance Win32_SoundDevice -ErrorAction Stop)) { $lines.Add(('AUDIO: {0} | {1}' -f $snd.Name, $snd.Status)) }
    } catch { }
    $lines.Add('Combined log: ' + $combinedLog)
    $lines.Add('Process metrics: ' + $processCsv)
    $lines.Add('Windows events: ' + $windowsEventLog)
    try { [IO.File]::WriteAllLines($manifestPath, $lines.ToArray(), (New-Object Text.UTF8Encoding($false))) } catch { }
}

function Get-KashtrixProcesses {
    $result = @()
    try {
        $result += @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like 'Kashtrix.*' })
    } catch { }
    return @($result | Sort-Object Id -Unique)
}

function Get-RelatedNativeProcesses([int[]]$kashtrixPids) {
    $output = @()
    if ($kashtrixPids.Count -eq 0) { return $output }
    try {
        $all = @(Get-CimInstance Win32_Process -ErrorAction Stop)
        $pidSet = @{}
        foreach ($id in $kashtrixPids) { $pidSet[[int]$id] = $true }
        # Walk parent chains so FFmpeg/Chromium/helper processes remain visible even when nested.
        $changed = $true
        while ($changed) {
            $changed = $false
            foreach ($p in $all) {
                $ppid = [int]$p.ParentProcessId
                $cpid = [int]$p.ProcessId
                if ($pidSet.ContainsKey($ppid) -and -not $pidSet.ContainsKey($cpid)) {
                    $pidSet[$cpid] = $true
                    $changed = $true
                }
            }
        }
        foreach ($p in $all) {
            $cpid = [int]$p.ProcessId
            if ($pidSet.ContainsKey($cpid) -and -not ($kashtrixPids -contains $cpid)) { $output += $p }
        }
    } catch { }
    return @($output)
}

function Update-GpuCounters([int[]]$pids) {
    if (([DateTime]::UtcNow - $script:lastGpuProbeUtc).TotalSeconds -lt 5) { return }
    $script:lastGpuProbeUtc = [DateTime]::UtcNow
    $script:gpuByPid = @{}
    if ($pids.Count -eq 0) { return }
    try {
        $counter = Get-Counter '\GPU Engine(*)\Utilization Percentage' -ErrorAction Stop
        foreach ($sample in $counter.CounterSamples) {
            $path = [string]$sample.Path
            foreach ($id in $pids) {
                if ($path -match ('pid_' + $id + '_')) {
                    if (-not $script:gpuByPid.ContainsKey($id)) { $script:gpuByPid[$id] = 0.0 }
                    $script:gpuByPid[$id] = [double]$script:gpuByPid[$id] + [double]$sample.CookedValue
                    break
                }
            }
        }
    } catch { }
}

function Capture-ProcessMetrics {
    $now = [DateTime]::UtcNow
    $elapsed = [Math]::Max(0.1, ($now - $script:lastCycleUtc).TotalSeconds)
    $script:lastCycleUtc = $now
    $procs = @(Get-KashtrixProcesses)
    $ids = @($procs | ForEach-Object { [int]$_.Id })
    Update-GpuCounters $ids

    if (-not (Test-Path $processCsv)) {
        'Timestamp,Process,PID,CPUPercent,GPUPercent,WorkingSetMB,PrivateMB,Threads,Handles,Responding' | Set-Content $processCsv -Encoding UTF8
    }

    $activeNow = @{}
    foreach ($p in $procs) {
        $pidValue = [int]$p.Id
        $activeNow[$pidValue] = $true
        $cpuNow = 0.0
        try { $cpuNow = [double]$p.TotalProcessorTime.TotalSeconds } catch { }
        $cpuPercent = 0.0
        if ($script:processCpu.ContainsKey($pidValue)) {
            $delta = [Math]::Max(0.0, $cpuNow - [double]$script:processCpu[$pidValue])
            $cpuPercent = ($delta / $elapsed / [Math]::Max(1, [Environment]::ProcessorCount)) * 100.0
        }
        $script:processCpu[$pidValue] = $cpuNow
        $gpu = if ($script:gpuByPid.ContainsKey($pidValue)) { [double]$script:gpuByPid[$pidValue] } else { 0.0 }
        $ws = Safe { [Math]::Round($p.WorkingSet64 / 1MB, 1) } 0
        $priv = Safe { [Math]::Round($p.PrivateMemorySize64 / 1MB, 1) } 0
        $threads = Safe { $p.Threads.Count } 0
        $handles = Safe { $p.HandleCount } 0
        $responding = Safe { $p.Responding } $true
        if (-not $script:processSeen.ContainsKey($pidValue)) {
            $script:processSeen[$pidValue] = $p.ProcessName
            $start = Safe { $p.StartTime.ToString('O') } '?'
            Log (('START {0} pid={1} start={2}' -f $p.ProcessName,$pidValue,$start)) Green 'PROCESS'
        }
        $metrics = ('{0} pid={1} CPU={2:N1}% GPU={3:N1}% RAM={4:N1}MB private={5:N1}MB threads={6} handles={7} responding={8}' -f $p.ProcessName,$pidValue,$cpuPercent,$gpu,$ws,$priv,$threads,$handles,$responding)
        Log $metrics $(if($responding){[ConsoleColor]::DarkGray}else{[ConsoleColor]::Red}) 'PERF'
        ('"{0}","{1}",{2},{3:N2},{4:N2},{5:N1},{6:N1},{7},{8},{9}' -f (Get-Date -Format 'O'),$p.ProcessName,$pidValue,$cpuPercent,$gpu,$ws,$priv,$threads,$handles,$responding) | Add-Content $processCsv -Encoding UTF8
    }

    foreach ($knownPid in @($script:processSeen.Keys)) {
        if (-not $activeNow.ContainsKey([int]$knownPid)) {
            Log (('STOP {0} pid={1}' -f $script:processSeen[$knownPid],$knownPid)) Yellow 'PROCESS'
            $script:processSeen.Remove($knownPid)
            $script:processCpu.Remove($knownPid)
        }
    }

    $children = @(Get-RelatedNativeProcesses $ids)
    foreach ($c in $children) {
        $name = [string]$c.Name
        if ($name -match '^(ffmpeg|ffprobe|CefSharp\.BrowserSubprocess|conhost)\.exe$' -or ([string]$c.CommandLine) -match 'Kashtrix') {
            $cmd = [string]$c.CommandLine
            if ($cmd.Length -gt 900) { $cmd = $cmd.Substring(0,900) + '...' }
            Log (('CHILD {0} pid={1} parent={2} cmd={3}' -f $name,$c.ProcessId,$c.ParentProcessId,$cmd)) DarkGray 'CHILD'
        }
    }
}

function Drain-LogFile([string]$path) {
    if ([string]::IsNullOrWhiteSpace($path) -or -not (Test-Path -LiteralPath $path)) { return }
    if ($path.StartsWith($sessionDir, [StringComparison]::OrdinalIgnoreCase)) { return }
    try {
        $item = Get-Item -LiteralPath $path -ErrorAction Stop
        $key = $item.FullName.ToLowerInvariant()
        if (-not $script:fileOffsets.ContainsKey($key)) {
            $tail = @(Get-Content -LiteralPath $item.FullName -Tail 12 -ErrorAction SilentlyContinue)
            foreach ($line in $tail) { if ($line) { Log (('[{0}] {1}' -f $item.Name,$line)) DarkGray 'LOG-CONTEXT' } }
            $script:fileOffsets[$key] = [int64]$item.Length
            return
        }
        $offset = [int64]$script:fileOffsets[$key]
        if ($item.Length -lt $offset) { $offset = 0 }
        if ($item.Length -eq $offset) { return }
        $fs = New-Object IO.FileStream($item.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try {
            [void]$fs.Seek($offset, [IO.SeekOrigin]::Begin)
            $reader = New-Object IO.StreamReader($fs, [Text.Encoding]::UTF8, $true)
            try {
                while (($line = $reader.ReadLine()) -ne $null) {
                    if (-not [string]::IsNullOrWhiteSpace($line)) {
                        $isBad = $line -match '(?i)(exception|error|failed|fatal|crash|hang|timeout|denied|unhandled)'
                        Log (('[{0}] {1}' -f $item.Name,$line)) $(if($isBad){[ConsoleColor]::Red}else{[ConsoleColor]::Gray}) 'APP-LOG'
                    }
                }
            } finally { $reader.Dispose() }
            $script:fileOffsets[$key] = [int64]$item.Length
        } finally { $fs.Dispose() }
    } catch {
        Log (('Cannot tail {0}: {1}' -f $path,$_.Exception.Message)) Yellow 'TAIL'
    }
}

function Capture-KashtrixLogs {
    try {
        $files = @(Get-ChildItem -Path $logRoot -File -Recurse -ErrorAction SilentlyContinue | Where-Object {
            $_.FullName -notlike (Join-Path $logRoot 'LiveSessions\*') -and
            ($_.Extension -in @('.log','.jsonl','.txt')) -and
            $_.LastWriteTime -gt (Get-Date).AddDays(-2)
        } | Sort-Object FullName)
        foreach ($file in $files) { Drain-LogFile $file.FullName }
    } catch { Log ('Log discovery error: ' + $_.Exception.Message) Yellow 'TAIL' }
}

function Capture-WindowsEvents {
    $start = $script:lastWindowsEventUtc.AddSeconds(-1)
    $script:lastWindowsEventUtc = Get-Date
    try {
        $events = @(Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=$start} -ErrorAction SilentlyContinue | Where-Object {
            $_.Id -in @(1000,1001,1002,1026) -or $_.ProviderName -in @('Application Error','Application Hang','.NET Runtime','Windows Error Reporting')
        } | Sort-Object TimeCreated)
        foreach ($evt in $events) {
            if ($script:eventRecords.ContainsKey([string]$evt.RecordId)) { continue }
            $msg = [string]$evt.Message
            if ($msg -notmatch '(?i)(Kashtrix|BroadcastPlayout|KashtrixVirtualOutput)') { continue }
            $script:eventRecords[[string]$evt.RecordId] = $true
            $clean = ($msg -replace "`r?`n", ' | ')
            if ($clean.Length -gt 2200) { $clean = $clean.Substring(0,2200) + '...' }
            $line = ('{0:O} | Provider={1} | EventId={2} | RecordId={3} | {4}' -f $evt.TimeCreated,$evt.ProviderName,$evt.Id,$evt.RecordId,$clean)
            try { Add-Content $windowsEventLog $line -Encoding UTF8 } catch { }
            Log $line Red 'WINDOWS-EVENT'
        }
        if ($script:eventRecords.Count -gt 2000) { $script:eventRecords = @{} }
    } catch { }
}

$ndiPath = Join-Path $ProjectRoot 'src\BroadcastPlayout.App\native\ndi\Processing.NDI.Lib.x64.dll'
if (-not (Test-Path $ndiPath)) {
    # Published/root package layout fallback.
    $candidate = Get-ChildItem -Path $ProjectRoot -Filter 'Processing.NDI.Lib.x64.dll' -File -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($candidate) { $ndiPath = $candidate.FullName }
}

if (-not ('KashtrixLiveDebug.NdiProbe' -as [type])) {
$code = @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
namespace KashtrixLiveDebug {
    public static class NdiProbe {
        [DllImport("kernel32", SetLastError=true, CharSet=CharSet.Unicode)] static extern bool SetDllDirectory(string lpPathName);
        [DllImport("Processing.NDI.Lib.x64.dll", CallingConvention=CallingConvention.Cdecl)] static extern bool NDIlib_initialize();
        [DllImport("Processing.NDI.Lib.x64.dll", CallingConvention=CallingConvention.Cdecl)] static extern IntPtr NDIlib_find_create_v2(ref FindCreate settings);
        [DllImport("Processing.NDI.Lib.x64.dll", CallingConvention=CallingConvention.Cdecl)] static extern void NDIlib_find_destroy(IntPtr finder);
        [DllImport("Processing.NDI.Lib.x64.dll", CallingConvention=CallingConvention.Cdecl)] static extern bool NDIlib_find_wait_for_sources(IntPtr finder, uint timeout);
        [DllImport("Processing.NDI.Lib.x64.dll", CallingConvention=CallingConvention.Cdecl)] static extern IntPtr NDIlib_find_get_current_sources(IntPtr finder, ref uint count);
        [StructLayout(LayoutKind.Sequential)] struct FindCreate { [MarshalAs(UnmanagedType.I1)] public bool show_local_sources; public IntPtr p_groups; public IntPtr p_extra_ips; }
        [StructLayout(LayoutKind.Sequential)] struct Source { public IntPtr p_ndi_name; public IntPtr p_url_address; }
        static string Utf8(IntPtr p) {
            if (p == IntPtr.Zero) return "";
            int len=0; while(Marshal.ReadByte(p, len) != 0) len++;
            if (len == 0) return "";
            var bytes=new byte[len]; Marshal.Copy(p, bytes, 0, len); return Encoding.UTF8.GetString(bytes);
        }
        public static string[] Scan(string runtimeDirectory, int waitMs) {
            SetDllDirectory(runtimeDirectory);
            if (!NDIlib_initialize()) throw new InvalidOperationException("NDI initialize failed");
            var settings = new FindCreate { show_local_sources=true, p_groups=IntPtr.Zero, p_extra_ips=IntPtr.Zero };
            var finder = NDIlib_find_create_v2(ref settings); if (finder == IntPtr.Zero) throw new InvalidOperationException("NDI finder creation failed");
            try {
                NDIlib_find_wait_for_sources(finder, (uint)Math.Max(50, waitMs));
                uint count=0; var ptr=NDIlib_find_get_current_sources(finder, ref count); var list=new List<string>();
                var size=Marshal.SizeOf(typeof(Source));
                for(int i=0;i<count;i++) {
                    var s=(Source)Marshal.PtrToStructure(IntPtr.Add(ptr, i*size), typeof(Source));
                    var name=Utf8(s.p_ndi_name); var addr=Utf8(s.p_url_address);
                    if(name.Length>0) list.Add(addr.Length>0 ? name+" | "+addr : name);
                }
                return list.ToArray();
            } finally { NDIlib_find_destroy(finder); }
        }
    }
}
'@
    try { Add-Type -TypeDefinition $code -Language CSharp -ErrorAction Stop } catch { Log ('NDI debug helper compile error: ' + $_.Exception.Message) Red 'NDI' }
}

function Capture-NdiAndOutput {
    if (([DateTime]::UtcNow - $script:lastNdiProbeUtc).TotalSeconds -ge 10) {
        $script:lastNdiProbeUtc = [DateTime]::UtcNow
        if (Test-Path $ndiPath) {
            try {
                $ndiDir = Split-Path $ndiPath -Parent
                $sources = [KashtrixLiveDebug.NdiProbe]::Scan($ndiDir, 1000)
                Log ('NDI runtime OK - sources=' + $sources.Length) Green 'NDI'
                foreach ($source in $sources) { Log ('NDI > ' + $source) Gray 'NDI' }
            } catch { Log ('NDI scan ERROR - ' + $_.Exception.GetBaseException().Message) Red 'NDI' }
        } else { Log 'NDI runtime DLL missing.' Red 'NDI' }
    }

    if (([DateTime]::UtcNow - $script:lastOutputProbeUtc).TotalSeconds -ge 10) {
        $script:lastOutputProbeUtc = [DateTime]::UtcNow
        $registered = $false
        $registeredPath = ''
        try {
            $clsidPath = 'Registry::HKEY_CLASSES_ROOT\CLSID\{C7453623-A2E2-49E0-B679-8BC4B52D57D2}\InprocServer32'
            if (Test-Path $clsidPath) {
                $registeredPath = [string](Get-ItemProperty $clsidPath -ErrorAction Stop).'(default)'
                $registered = -not [string]::IsNullOrWhiteSpace($registeredPath)
            }
        } catch { }
        Log ('Virtual Output: ' + $(if($registered){'REGISTERED | ' + $registeredPath}else{'NOT REGISTERED'})) $(if($registered){[ConsoleColor]::Green}else{[ConsoleColor]::Yellow}) 'VIRTUAL-OUTPUT'
    }
}


function Export-SessionBundle {
    try {
        $zip = Join-Path $logRoot ('LiveDebug-' + $sessionId + '.zip')
        if (Test-Path $zip) { Remove-Item $zip -Force -ErrorAction SilentlyContinue }
        Compress-Archive -Path (Join-Path $sessionDir '*') -DestinationPath $zip -Force -ErrorAction Stop
        Log ('Diagnostic ZIP: ' + $zip) Cyan 'SESSION'
    } catch {
        Log ('Could not create diagnostic ZIP: ' + $_.Exception.Message) Yellow 'SESSION'
    }
}
Write-SessionManifest
Log 'Kashtrix REAL live diagnostics started. Use the suite normally; press Ctrl+C only when the problem has been reproduced.' Cyan 'SESSION'
Log ('Project: ' + $ProjectRoot) DarkGray 'SESSION'
Log ('Session folder: ' + $sessionDir) Cyan 'SESSION'
Log 'Capturing: app/process start-stop, CPU/GPU/RAM/threads/handles, child FFmpeg/CEF processes, Audit/operator actions, runtime telemetry, startup/error logs, Windows crash/hang events, NDI and Virtual Output state.' DarkGray 'SESSION'

$iteration = 0
try {
    while ($true) {
        $iteration++
        Log ('---- capture cycle ' + $iteration + ' ----') DarkCyan 'CYCLE'
        Capture-ProcessMetrics
        Capture-KashtrixLogs
        Capture-WindowsEvents
        Capture-NdiAndOutput
        Start-Sleep -Seconds $IntervalSeconds
    }
}
finally {
    Log 'Live diagnostic session stopping.' Yellow 'SESSION'
    Log ('Saved complete session at: ' + $sessionDir) Cyan 'SESSION'
    Export-SessionBundle
}
