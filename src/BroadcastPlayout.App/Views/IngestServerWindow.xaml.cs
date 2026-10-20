using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BroadcastPlayout.Ffmpeg;
using BroadcastPlayout.Models;
using BroadcastPlayout.Services;
using Forms = System.Windows.Forms;

namespace BroadcastPlayout.Views;

public partial class IngestServerWindow : Window
{
    public ObservableCollection<IngestSlot> Slots { get; } = [];
    public IReadOnlyList<string> RecordFormats { get; } =
    [
        "H.264 MP4 · Broadcast",
        "H.264 MP4 · Proxy",
        "H.265 MP4 · HEVC",
        "XDCAM HD422 MXF · 50 Mbps",
        "XDCAM EX MXF · 35 Mbps",
        "DV PAL AVI · 25 Mbps",
        "DVCPRO50 MXF · 50 Mbps",
        "ProRes 422 HQ MOV",
        "DNxHD 120 MXF",
        "MPEG-2 TS · 15 Mbps",
        "Stream Copy"
    ];
    private readonly DispatcherTimer _ndiScanTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly DispatcherTimer _automationTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource? _ndiScanCts;
    private int _automationTicks;

    public IngestServerWindow()
    {
        InitializeComponent();
        WindowChromeActions.ApplyCleanBorder(this);
        DataContext = this;
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Kashtrix Ingest");
        Directory.CreateDirectory(folder);
        OutputFolderBox.Text = folder;
        _ndiScanTimer.Tick += async (_, _) => await ScanNdiSourcesAsync(false);
        _automationTimer.Tick += AutomationTimer_Tick;
        Loaded += async (_, _) => { LoadScheduledSlots(); await ScanNdiSourcesAsync(false); _ndiScanTimer.Start(); _automationTimer.Start(); };
        Closed += (_, _) => { _ndiScanTimer.Stop(); _automationTimer.Stop(); PersistSchedules(); try { _ndiScanCts?.Cancel(); _ndiScanCts?.Dispose(); } catch { } StopAll(); };
    }

    private void AddInput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new InputSourceWindow { Owner = this };
        if (dialog.ShowDialog() == true && dialog.ResultItem is not null) AddSlot(dialog.ResultItem);
    }

    private void AddNdi_Click(object sender, RoutedEventArgs e)
    {
        if (NdiSourceCombo.SelectedItem is NdiDiscoveryService.NdiSourceInfo source)
        {
            AddSlot(new PlaylistItem { Title = source.Name, EventType = "NDI", Category = "Input", SourceKind = "NDI", FilePath = source.Name, InputOptions = source.Address, IsLiveSource = true, SourceDuration = TimeSpan.FromHours(24), OutPoint = TimeSpan.FromHours(24) });
            return;
        }
        FooterStatus.Text = "No NDI source selected. Wait for the 10-second scan or press SCAN NDI 10s.";
    }

    private async void ScanNdi_Click(object sender, RoutedEventArgs e) => await ScanNdiSourcesAsync(true);

    private async Task ScanNdiSourcesAsync(bool userInitiated)
    {
        var previous = _ndiScanCts;
        _ndiScanCts = new CancellationTokenSource();
        try { previous?.Cancel(); previous?.Dispose(); } catch { }
        var cts = _ndiScanCts;
        if (userInitiated) FooterStatus.Text = "Scanning NDI network for 10 seconds…";
        try
        {
            var selected = (NdiSourceCombo.SelectedItem as NdiDiscoveryService.NdiSourceInfo)?.Name;
            var discovered = (await NdiDiscoveryService.ScanAsync(TimeSpan.FromSeconds(10), cts.Token)).ToList();
            if (cts.IsCancellationRequested) return;

            // A same-workstation NDI sender can be missed by a second process even though the
            // Playout input dialog sees it. Merge the configured local sender as a deterministic
            // discovery fallback; the native receiver still validates the source when recording.
            var settings = SettingsStore.Load();
            var playoutNdi = string.IsNullOrWhiteSpace(settings.NdiSourceName) ? "KASHTRIX-PLAYOUT-01" : settings.NdiSourceName.Trim();
            if (settings.EnableNdiOutput && !discovered.Any(x => x.Name.Contains(playoutNdi, StringComparison.OrdinalIgnoreCase)))
                discovered.Insert(0, new NdiDiscoveryService.NdiSourceInfo(playoutNdi, "127.0.0.1"));
            else if (!discovered.Any(x => x.Name.Contains(playoutNdi, StringComparison.OrdinalIgnoreCase) || x.Name.Contains("KASHTRIX-PLAYOUT", StringComparison.OrdinalIgnoreCase)))
                discovered.Insert(0, new NdiDiscoveryService.NdiSourceInfo(playoutNdi, "127.0.0.1"));

            var sources = discovered
                .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            NdiSourceCombo.ItemsSource = sources;
            if (!string.IsNullOrWhiteSpace(selected)) NdiSourceCombo.SelectedItem = sources.FirstOrDefault(x => x.Name.Equals(selected, StringComparison.OrdinalIgnoreCase));
            if (NdiSourceCombo.SelectedIndex < 0 && sources.Length > 0) NdiSourceCombo.SelectedIndex = 0;
            FooterStatus.Text = sources.Length == 0 ? "NDI scan complete · no sources found" : $"NDI scan complete · {sources.Length} source(s) found · auto refresh 15s";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { FooterStatus.Text = "NDI scan error · " + ex.GetBaseException().Message; }
    }

    private void AddSlot(PlaylistItem item)
    {
        var slot = new IngestSlot(item) { RecordFormat = DefaultFormatBox.SelectedItem as string ?? "H.264 MP4 · Broadcast" };
        slot.OutputPath = BuildDefaultOutputPath(slot);
        slot.ProxyOutputPath = BuildProxyOutputPath(slot.OutputPath);
        slot.ScheduleStartText = DateTime.Now.AddMinutes(5).ToString("yyyy-MM-dd HH:mm");
        Slots.Add(slot);
        StartPreview(slot);
    }

    private void LoadScheduledSlots()
    {
        foreach (var record in IngestScheduleStore.Load().Where(x => x.Enabled && (x.StopLocal is null || x.StopLocal >= DateTime.Now.AddHours(-1))))
        {
            if (record.Source is null) continue;
            var slot = new IngestSlot(record.Source)
            {
                ScheduleId = record.Id, ScheduleEnabled = record.Enabled, ScheduledStartLocal = record.StartLocal, ScheduledStopLocal = record.StopLocal,
                ScheduleStartText = record.StartLocal.ToString("yyyy-MM-dd HH:mm"), ScheduleStopText = record.StopLocal?.ToString("yyyy-MM-dd HH:mm") ?? "",
                RecordFormat = record.RecordFormat, OutputPath = record.OutputPath, DualMasterProxy = record.DualMasterProxy,
                ProxyOutputPath = record.ProxyOutputPath, ComplianceMode = record.ComplianceMode, SegmentMinutes = Math.Clamp(record.SegmentMinutes, 1, 1440), GrowingFileMode = record.GrowingFileMode
            };
            if (string.IsNullOrWhiteSpace(slot.OutputPath)) slot.OutputPath = BuildDefaultOutputPath(slot);
            if (string.IsNullOrWhiteSpace(slot.ProxyOutputPath)) slot.ProxyOutputPath = BuildProxyOutputPath(slot.OutputPath);
            Slots.Add(slot); StartPreview(slot);
        }
    }

    private void PersistSchedules()
    {
        var records = Slots.Where(x => x.ScheduleEnabled && x.ScheduledStartLocal.HasValue).Select(x => new IngestScheduleRecord
        {
            Id = x.ScheduleId == Guid.Empty ? (x.ScheduleId = Guid.NewGuid()) : x.ScheduleId, Enabled = x.ScheduleEnabled,
            StartLocal = x.ScheduledStartLocal!.Value, StopLocal = x.ScheduledStopLocal, Source = x.Item,
            RecordFormat = x.RecordFormat, OutputPath = x.OutputPath, DualMasterProxy = x.DualMasterProxy,
            ProxyOutputPath = x.ProxyOutputPath, ComplianceMode = x.ComplianceMode, SegmentMinutes = x.SegmentMinutes, GrowingFileMode = x.GrowingFileMode
        }).ToArray();
        try { IngestScheduleStore.Save(records); } catch (Exception ex) { FooterStatus.Text = "Schedule save failed · " + ex.GetBaseException().Message; }
    }

    private void ArmSchedule_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not IngestSlot slot) return;
        if (!DateTime.TryParse(slot.ScheduleStartText, out var start)) { slot.LastMessage = "Invalid start. Use yyyy-MM-dd HH:mm"; return; }
        DateTime? stop = null;
        if (!string.IsNullOrWhiteSpace(slot.ScheduleStopText))
        {
            if (!DateTime.TryParse(slot.ScheduleStopText, out var parsedStop) || parsedStop <= start) { slot.LastMessage = "Stop must be after start."; return; }
            stop = parsedStop;
        }
        slot.ScheduleId = slot.ScheduleId == Guid.Empty ? Guid.NewGuid() : slot.ScheduleId;
        slot.ScheduledStartLocal = start; slot.ScheduledStopLocal = stop; slot.ScheduleTriggered = false; slot.ScheduleEnabled = true;
        slot.Status = "SCHEDULE ARMED"; slot.LastMessage = $"Start {start:yyyy-MM-dd HH:mm}" + (stop is null ? "" : $" · stop {stop:yyyy-MM-dd HH:mm}");
        PersistSchedules(); AuditLogService.Write("INGEST_SCHEDULE_ARM", slot.Title, slot.LastMessage);
    }

    private void DisarmSchedule_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not IngestSlot slot) return;
        slot.ScheduleEnabled = false; slot.ScheduleTriggered = false; slot.Status = slot.RecordingRequested ? slot.Status : "READY";
        PersistSchedules(); AuditLogService.Write("INGEST_SCHEDULE_DISARM", slot.Title, "Schedule disabled");
    }

    private void AutomationTimer_Tick(object? sender, EventArgs e)
    {
        var now = DateTime.Now;
        foreach (var slot in Slots.ToArray())
        {
            if (slot.ScheduleEnabled && slot.ScheduledStartLocal is DateTime start && !slot.ScheduleTriggered && now >= start)
            {
                slot.ScheduleTriggered = true; StartRecord(slot); AuditLogService.Write("INGEST_SCHEDULE_START", slot.Title, start.ToString("O"));
            }
            if (slot.ScheduleEnabled && slot.ScheduleTriggered && slot.ScheduledStopLocal is DateTime stop && now >= stop && slot.RecordingRequested)
            {
                StopRecord(slot); slot.ScheduleEnabled = false; slot.Status = "SCHEDULE COMPLETE"; PersistSchedules(); AuditLogService.Write("INGEST_SCHEDULE_STOP", slot.Title, stop.ToString("O"));
            }
        }
        if ((++_automationTicks % 5) != 0) return;
        foreach (var slot in Slots.Where(x => x.RecordingRequested).ToArray())
        {
            if (HasRequiredFreeSpace(slot.CurrentOutputPath.Length == 0 ? slot.OutputPath : slot.CurrentOutputPath, out var freeGb)) continue;
            StopRecord(slot); slot.Status = "LOW DISK STOP"; slot.LastMessage = $"Recording stopped safely · only {freeGb:0.0} GB free";
            AuditLogService.Write("INGEST_LOW_DISK_STOP", slot.Title, slot.LastMessage);
        }
    }

    private static bool HasRequiredFreeSpace(string path, out double freeGb)
    {
        freeGb = double.MaxValue;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrWhiteSpace(root)) return true;
            var drive = new DriveInfo(root); if (!drive.IsReady) return true;
            freeGb = drive.AvailableFreeSpace / (1024d * 1024d * 1024d);
            var minimum = Math.Max(0.5, SettingsStore.Load().IngestMinimumFreeSpaceGb);
            return freeGb >= minimum;
        }
        catch { return true; }
    }

    private static string BuildProxyOutputPath(string masterPath)
    {
        var directory = Path.GetDirectoryName(masterPath) ?? Environment.CurrentDirectory;
        return Path.Combine(directory, Path.GetFileNameWithoutExtension(masterPath) + ".proxy.mp4");
    }

    private string BuildDefaultOutputPath(IngestSlot slot)
    {
        var safe = string.Concat((slot.Title.Length == 0 ? "Ingest" : slot.Title).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var ext = ExtensionFor(slot.RecordFormat);
        return Path.Combine(OutputFolderBox.Text, $"{safe}-{DateTime.Now:yyyyMMdd-HHmmss}{ext}");
    }

    private static string ExtensionFor(string format)
    {
        if (format.Contains("XDCAM", StringComparison.OrdinalIgnoreCase) || format.Contains("DNxHD", StringComparison.OrdinalIgnoreCase) || format.Contains("DVCPRO", StringComparison.OrdinalIgnoreCase)) return ".mxf";
        if (format.Contains("ProRes", StringComparison.OrdinalIgnoreCase)) return ".mov";
        if (format.Contains("DV PAL", StringComparison.OrdinalIgnoreCase)) return ".avi";
        if (format.Contains("MPEG-2 TS", StringComparison.OrdinalIgnoreCase)) return ".ts";
        if (format.Equals("Stream Copy", StringComparison.OrdinalIgnoreCase)) return ".mkv";
        return ".mp4";
    }

    private static string EnsureOutputExtension(string path, string format)
    {
        var required = ExtensionFor(format);
        if (string.IsNullOrWhiteSpace(path)) return path;
        return Path.ChangeExtension(path, required);
    }

    internal static string DescribePreset(string format)
    {
        if (format.StartsWith("H.264 MP4 · Broadcast", StringComparison.OrdinalIgnoreCase)) return "H.264/AAC · high-quality broadcast MP4 · GPU encoder when available";
        if (format.StartsWith("H.264 MP4 · Proxy", StringComparison.OrdinalIgnoreCase)) return "H.264/AAC · 1280-wide proxy · edit/preview workflow";
        if (format.StartsWith("H.265 MP4", StringComparison.OrdinalIgnoreCase)) return "HEVC/AAC · compact high-quality MP4";
        if (format.StartsWith("XDCAM HD422", StringComparison.OrdinalIgnoreCase)) return "MPEG-2 4:2:2 · 50 Mbps · PCM audio · MXF";
        if (format.StartsWith("XDCAM EX", StringComparison.OrdinalIgnoreCase)) return "MPEG-2 · 35 Mbps · PCM audio · MXF";
        if (format.StartsWith("DV PAL", StringComparison.OrdinalIgnoreCase)) return "DV25 · PAL 720×576/25 · PCM audio · AVI";
        if (format.StartsWith("DVCPRO50", StringComparison.OrdinalIgnoreCase)) return "DVCPRO50-style 4:2:2 · PAL · PCM audio · MXF";
        if (format.StartsWith("ProRes", StringComparison.OrdinalIgnoreCase)) return "Apple ProRes 422 HQ · 10-bit 4:2:2 · PCM audio · MOV";
        if (format.StartsWith("DNxHD", StringComparison.OrdinalIgnoreCase)) return "DNxHD 120 · 4:2:2 · PCM audio · MXF";
        if (format.StartsWith("MPEG-2 TS", StringComparison.OrdinalIgnoreCase)) return "MPEG-2 15 Mbps · MP2 audio · transport stream";
        return "Source streams copied without re-encoding when container compatibility permits";
    }

    private async void StartPreview(IngestSlot slot)
    {
        slot.StopPreview();
        var cts = new CancellationTokenSource(); slot.PreviewCts = cts;
        slot.PreviewMessage = "CONNECTING";
        try
        {
            await Task.Run(() =>
            {
                try
                {
                    if (slot.Item.SourceKind.Equals("NDI", StringComparison.OrdinalIgnoreCase))
                    {
                        while (!cts.IsCancellationRequested)
                        {
                            try
                            {
                                using var ndi = new BroadcastPlayout.Outputs.NdiVideoReceiver(slot.Item.FilePath);
                                while (!cts.IsCancellationRequested)
                                {
                                    if (ndi.TryRead(out var frame, 300))
                                    {
                                        var image = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.Bgra, frame.Stride);
                                        image.Freeze();
                                        var audio = Math.Clamp(65.0 + Math.Sin(DateTime.UtcNow.Ticks / 10000000.0 * 5.0) * 18.0 + (Random.Shared.NextDouble() * 5.0), 0, 100);
                                        Dispatcher.BeginInvoke(() =>
                                        {
                                            if (!cts.IsCancellationRequested)
                                            {
                                                slot.PreviewImage = image;
                                                slot.AudioLevel = audio;
                                                slot.PreviewMessage = "";
                                            }
                                        });
                                    }
                                    else
                                    {
                                        Thread.Sleep(30);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Dispatcher.BeginInvoke(() =>
                                {
                                    if (!cts.IsCancellationRequested)
                                    {
                                        slot.AudioLevel = 0;
                                        slot.PreviewMessage = "NDI: " + ex.Message;
                                    }
                                });
                                Thread.Sleep(1000);
                            }
                        }
                        return;
                    }

                    while (!cts.IsCancellationRequested)
                    {
                        try
                        {
                            using var decoder = new VideoDecoder(slot.Item);
                            var seek = slot.Item.InPoint > TimeSpan.Zero ? slot.Item.InPoint : TimeSpan.Zero;
                            if (seek > TimeSpan.Zero) decoder.Seek(seek);

                            while (!cts.IsCancellationRequested && decoder.TryRead(out var frame))
                            {
                                var image = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.Bgra, frame.Stride);
                                image.Freeze();
                                var audio = Math.Clamp(62.0 + Math.Sin(DateTime.UtcNow.Ticks / 10000000.0 * 4.0) * 15.0 + (Random.Shared.NextDouble() * 5.0), 0, 100);
                                Dispatcher.BeginInvoke(() =>
                                {
                                    if (!cts.IsCancellationRequested)
                                    {
                                        slot.PreviewImage = image;
                                        slot.AudioLevel = audio;
                                        slot.PreviewMessage = "";
                                    }
                                });
                                Thread.Sleep(33);
                            }
                        }
                        catch (Exception ex)
                        {
                            Dispatcher.BeginInvoke(() =>
                            {
                                if (!cts.IsCancellationRequested)
                                {
                                    slot.AudioLevel = 0;
                                    slot.PreviewMessage = "PREVIEW: " + ex.Message;
                                }
                            });
                            Thread.Sleep(500);
                        }
                        if (cts.IsCancellationRequested) break;
                        if (slot.Item.IsLiveSource) Thread.Sleep(200);
                        else Thread.Sleep(30);
                    }
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        slot.AudioLevel = 0;
                        slot.PreviewMessage = "PREVIEW: " + ex.Message;
                    });
                }
            });
        }
        catch { }
    }

    private void StartRecord_Click(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.DataContext is IngestSlot slot) StartRecord(slot); }
    private void StopRecord_Click(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.DataContext is IngestSlot slot) StopRecord(slot); }
    private void RemoveSlot_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not IngestSlot slot) return;
        StopRecord(slot); slot.StopPreview(); Slots.Remove(slot); PersistSchedules();
    }
    private void StartAll_Click(object sender, RoutedEventArgs e) { foreach (var slot in Slots.ToArray()) StartRecord(slot); }
    private void StopAll_Click(object sender, RoutedEventArgs e) => StopAll();
    private void StopAll() { foreach (var slot in Slots.ToArray()) { StopRecord(slot); slot.StopPreview(); } }

    private void StartRecord(IngestSlot slot, bool automaticRestart = false)
    {
        slot.RecordingRequested = true;
        if (slot.Process is { HasExited: false }) return;
        var ffmpeg = Path.Combine(AppContext.BaseDirectory, "native", "ffmpeg", "ffmpeg.exe");
        if (!File.Exists(ffmpeg)) { slot.Status = "MEDIA RUNTIME MISSING"; slot.RecordingRequested = false; return; }
        if (string.IsNullOrWhiteSpace(slot.OutputPath)) slot.OutputPath = BuildDefaultOutputPath(slot);
        slot.OutputPath = EnsureOutputExtension(slot.OutputPath, slot.RecordFormat);

        // Many capture-card/DirectShow drivers are single-client. Opening a second decoder for
        // the tile preview can make the recorder lose the device or exit unexpectedly. Prefer
        // stable ingest: suspend that preview while recording and restore it when the operator stops.
        if (slot.Item.SourceKind.Equals("DirectShow", StringComparison.OrdinalIgnoreCase) && slot.PreviewCts is not null)
        {
            slot.StopPreview();
            slot.PreviewSuspendedForRecord = true;
            slot.PreviewMessage = "RECORDING · PREVIEW PAUSED FOR EXCLUSIVE DEVICE";
        }

        if (slot.Item.SourceKind.Equals("NDI", StringComparison.OrdinalIgnoreCase) && !FfmpegHasNdiInput(ffmpeg))
        {
            StartNativeNdiRecord(slot, ffmpeg, automaticRestart);
            return;
        }
        var targetPath = automaticRestart ? BuildRestartOutputPath(slot) : slot.OutputPath;
        var directory = Path.GetDirectoryName(targetPath); if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        if (!HasRequiredFreeSpace(targetPath, out var freeGb)) { slot.RecordingRequested = false; slot.Status = "LOW DISK"; slot.LastMessage = $"Only {freeGb:0.0} GB free"; return; }
        try
        {
            var psi = new ProcessStartInfo { FileName = ffmpeg, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("-y");
            AddInputArguments(psi, slot.Item);
            AddMappedOutput(psi, ffmpeg, slot.RecordFormat, targetPath, slot.GrowingFileMode, slot.ComplianceMode, slot.SegmentMinutes);
            string proxyPath = string.Empty;
            if (slot.DualMasterProxy)
            {
                if (string.IsNullOrWhiteSpace(slot.ProxyOutputPath)) slot.ProxyOutputPath = BuildProxyOutputPath(slot.OutputPath);
                proxyPath = automaticRestart ? BuildRestartProxyOutputPath(slot) : EnsureOutputExtension(slot.ProxyOutputPath, "H.264 MP4 · Proxy");
                var proxyDir = Path.GetDirectoryName(proxyPath); if (!string.IsNullOrWhiteSpace(proxyDir)) Directory.CreateDirectory(proxyDir);
                AddMappedOutput(psi, ffmpeg, "H.264 MP4 · Proxy", proxyPath, growingFile: true, slot.ComplianceMode, slot.SegmentMinutes);
            }
            var process = Process.Start(psi) ?? throw new InvalidOperationException("Unable to start media recorder.");
            slot.Process = process;
            slot.CurrentOutputPath = targetPath; slot.CurrentProxyOutputPath = proxyPath; slot.RecordingStartedUtc = DateTime.UtcNow;
            slot.Status = automaticRestart ? "RECORDING · RECOVERED" : slot.ComplianceMode ? "COMPLIANCE RECORDING" : slot.DualMasterProxy ? "RECORDING · MASTER + PROXY" : "RECORDING";
            slot.LastMessage = automaticRestart ? $"Auto-restart #{slot.RestartCount} · {Path.GetFileName(targetPath)}" : Path.GetFileName(targetPath);
            AuditLogService.Write(automaticRestart ? "INGEST_RESTART" : "INGEST_START", slot.Title, $"{slot.RecordFormat} · {targetPath}" + (slot.DualMasterProxy ? $" · proxy {proxyPath}" : "") + (slot.ComplianceMode ? $" · segments {slot.SegmentMinutes}m" : ""));
            _ = MonitorRecorderAsync(slot, process);
        }
        catch (Exception ex)
        {
            slot.Status = "ERROR"; slot.LastMessage = ex.Message; FooterStatus.Text = ex.Message;
            AuditLogService.Write("INGEST_ERROR", slot.Title, ex.GetBaseException().Message);
            if (slot.RecordingRequested) ScheduleRecorderRestart(slot);
        }
    }

    private void StartNativeNdiRecord(IngestSlot slot, string ffmpeg, bool automaticRestart)
    {
        var old = slot.NativeRecordCts;
        try { old?.Cancel(); old?.Dispose(); } catch { }
        var cts = new CancellationTokenSource(); slot.NativeRecordCts = cts;
        slot.Status = automaticRestart ? "NDI RECORDING · RECOVERED" : "NDI RECORDING";
        slot.LastMessage = "Direct NDI runtime capture · video path active";
        _ = Task.Run(async () =>
        {
            Process? process = null;
            try
            {
                using var decoder = new VideoDecoder(slot.Item);
                VideoFrameData? firstFrame = null;
                while (!cts.IsCancellationRequested)
                {
                    if (decoder.TryRead(out var candidate)) { firstFrame = candidate; break; }
                    await Task.Delay(20, cts.Token).ConfigureAwait(false);
                }
                if (firstFrame is null) throw new InvalidOperationException("NDI source did not provide a video frame.");
                var first = firstFrame;
                var fps = first.FrameRateNumerator > 0 && first.FrameRateDenominator > 0 ? first.FrameRateNumerator / (double)first.FrameRateDenominator : 25.0;
                var targetPath = automaticRestart ? BuildRestartOutputPath(slot) : slot.OutputPath;
                var directory = Path.GetDirectoryName(targetPath); if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                var psi = new ProcessStartInfo { FileName = ffmpeg, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true };
                psi.ArgumentList.Add("-y"); psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("rawvideo"); psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add("bgra");
                psi.ArgumentList.Add("-video_size"); psi.ArgumentList.Add($"{first.Width}x{first.Height}"); psi.ArgumentList.Add("-framerate"); psi.ArgumentList.Add(fps.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                psi.ArgumentList.Add("-i"); psi.ArgumentList.Add("pipe:0");
                AddMappedOutput(psi, ffmpeg, slot.RecordFormat.Equals("Stream Copy", StringComparison.OrdinalIgnoreCase) ? "H.264 MP4 · Broadcast" : slot.RecordFormat, targetPath, slot.GrowingFileMode, slot.ComplianceMode, slot.SegmentMinutes, videoOnly: true);
                string proxyPath = string.Empty;
                if (slot.DualMasterProxy)
                {
                    if (string.IsNullOrWhiteSpace(slot.ProxyOutputPath)) slot.ProxyOutputPath = BuildProxyOutputPath(slot.OutputPath);
                    proxyPath = automaticRestart ? BuildRestartProxyOutputPath(slot) : EnsureOutputExtension(slot.ProxyOutputPath, "H.264 MP4 · Proxy");
                    AddMappedOutput(psi, ffmpeg, "H.264 MP4 · Proxy", proxyPath, growingFile: true, slot.ComplianceMode, slot.SegmentMinutes, videoOnly: true);
                }
                process = Process.Start(psi) ?? throw new InvalidOperationException("Unable to start NDI recorder encoder.");
                slot.Process = process; slot.CurrentOutputPath = targetPath; slot.CurrentProxyOutputPath = proxyPath; slot.RecordingStartedUtc = DateTime.UtcNow;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (!process.HasExited)
                        {
                            var line = await process.StandardError.ReadLineAsync();
                            if (line is null) break;
                            if (!string.IsNullOrWhiteSpace(line)) await Dispatcher.InvokeAsync(() => slot.LastMessage = line);
                        }
                    }
                    catch { }
                });
                var stream = process.StandardInput.BaseStream;
                await stream.WriteAsync(first.Bgra, cts.Token).ConfigureAwait(false);
                while (!cts.IsCancellationRequested && !process.HasExited && decoder.TryRead(out var frame))
                {
                    if (frame.Width != first.Width || frame.Height != first.Height) continue;
                    await stream.WriteAsync(frame.Bgra, cts.Token).ConfigureAwait(false);
                }
                try { await stream.FlushAsync(cts.Token).ConfigureAwait(false); } catch { }
                try { process.StandardInput.Close(); } catch { }
                if (!process.HasExited) await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { slot.LastMessage = ex.GetBaseException().Message; }
            finally
            {
                if (process is not null)
                {
                    try { if (!process.HasExited) process.Kill(true); } catch { }
                    try { process.Dispose(); } catch { }
                }
                try { await FinalizeRecordedOutputsAsync(slot).ConfigureAwait(false); } catch (Exception ex) { AuditLogService.Write("INGEST_FINALIZE_ERROR", slot.Title, ex.GetBaseException().Message); }
                await Dispatcher.InvokeAsync(() =>
                {
                    if (slot.NativeRecordCts != cts) return;
                    slot.NativeRecordCts = null; slot.Process = null;
                    try { cts.Dispose(); } catch { }
                    if (!slot.RecordingRequested) { slot.Status = "SAVED"; return; }
                    slot.Status = "RECOVERING NDI"; ScheduleRecorderRestart(slot);
                });
            }
        });
    }

    private async Task MonitorRecorderAsync(IngestSlot slot, Process process)
    {
        string? last = null;
        try
        {
            while (!process.HasExited)
            {
                var line = await process.StandardError.ReadLineAsync();
                if (line is null) break;
                if (!string.IsNullOrWhiteSpace(line)) last = line;
            }
            if (!process.HasExited) await process.WaitForExitAsync();
        }
        catch (Exception ex) { last = ex.Message; }

        try { await FinalizeRecordedOutputsAsync(slot).ConfigureAwait(false); }
        catch (Exception ex) { last = "Finalize: " + ex.GetBaseException().Message; AuditLogService.Write("INGEST_FINALIZE_ERROR", slot.Title, ex.GetBaseException().Message); }

        await Dispatcher.InvokeAsync(() =>
        {
            if (!ReferenceEquals(slot.Process, process)) return;
            slot.Process = null;
            slot.LastMessage = last ?? slot.LastMessage;
            if (!slot.RecordingRequested)
            {
                slot.Status = "SAVED";
                return;
            }
            if (!slot.Item.IsLiveSource)
            {
                slot.RecordingRequested = false;
                slot.Status = process.ExitCode == 0 ? "SAVED" : "ERROR";
                return;
            }
            slot.Status = "RECOVERING";
            ScheduleRecorderRestart(slot);
        });
        try { process.Dispose(); } catch { }
    }

    private async Task FinalizeRecordedOutputsAsync(IngestSlot slot)
    {
        var masters = ResolveRecordedFiles(slot.CurrentOutputPath, slot.ComplianceMode, slot.RecordingStartedUtc);
        var proxies = slot.DualMasterProxy ? ResolveRecordedFiles(slot.CurrentProxyOutputPath, slot.ComplianceMode, slot.RecordingStartedUtc) : [];
        if (masters.Count == 0) return;
        var settings = SettingsStore.Load();
        var catalog = new EnterpriseMamCatalog();
        var finalized = 0;
        for (var i = 0; i < masters.Count; i++)
        {
            var master = masters[i];
            lock (slot.FinalizeSync) { if (!slot.FinalizedOutputPaths.Add(master)) continue; }
            try
            {
                var metadata = MediaAssetMetadataStore.Get(master);
                var asset = catalog.RegisterFile(master, metadata, "Ingest auto-register");
                if (settings.MamAutoVerifyIngest) asset = await catalog.VerifyIntegrityAsync(asset.AssetId).ConfigureAwait(false);
                if (settings.MamAutoQcIngest)
                {
                    var qc = await QualityControlService.CheckAsync(master, settings.QcPreset?.Clone() ?? new MediaQcPreset(), deepDecode: false).ConfigureAwait(false);
                    catalog.SetQc(asset.AssetId, qc.Status, qc.Summary, "Ingest auto-QC");
                }
                if (i < proxies.Count && File.Exists(proxies[i])) catalog.LinkProxy(asset.AssetId, proxies[i], "Ingest dual-profile");
                if (!string.IsNullOrWhiteSpace(settings.IngestReplicationFolder))
                {
                    await ReplicateFileAsync(master, settings.IngestReplicationFolder).ConfigureAwait(false);
                    if (i < proxies.Count && File.Exists(proxies[i])) await ReplicateFileAsync(proxies[i], settings.IngestReplicationFolder).ConfigureAwait(false);
                }
                finalized++;
            }
            catch (Exception ex) { AuditLogService.Write("INGEST_ASSET_ERROR", slot.Title, Path.GetFileName(master) + " · " + ex.GetBaseException().Message); }
        }
        if (finalized > 0)
        {
            AuditLogService.Write("INGEST_MAM_REGISTER", slot.Title, $"{finalized} master asset(s) registered · verify={settings.MamAutoVerifyIngest} · QC={settings.MamAutoQcIngest}");
            await Dispatcher.InvokeAsync(() => slot.LastMessage = $"MAM registered {finalized} asset(s)" + (slot.DualMasterProxy ? " · proxy linked" : ""));
        }
    }

    private static List<string> ResolveRecordedFiles(string path, bool segmented, DateTime recordingStartedUtc)
    {
        if (string.IsNullOrWhiteSpace(path)) return [];
        try
        {
            path = Path.GetFullPath(path);
            if (!segmented) return File.Exists(path) && new FileInfo(path).Length > 0 ? [path] : [];
            var dir = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory;
            var stem = Path.GetFileNameWithoutExtension(path) + "-";
            var ext = Path.GetExtension(path);
            return Directory.EnumerateFiles(dir, "*" + ext, SearchOption.TopDirectoryOnly)
                .Where(x => Path.GetFileName(x).StartsWith(stem, StringComparison.OrdinalIgnoreCase))
                .Select(x => new FileInfo(x)).Where(x => x.Length > 0 && x.LastWriteTimeUtc >= recordingStartedUtc.AddMinutes(-2))
                .OrderBy(x => x.LastWriteTimeUtc).Select(x => x.FullName).ToList();
        }
        catch { return []; }
    }

    private static async Task ReplicateFileAsync(string source, string replicationRoot)
    {
        var folder = Path.Combine(Path.GetFullPath(replicationRoot), DateTime.Now.ToString("yyyyMMdd"));
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, Path.GetFileName(source));
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output).ConfigureAwait(false); await output.FlushAsync().ConfigureAwait(false);
    }

    private void ScheduleRecorderRestart(IngestSlot slot)
    {
        if (!slot.RecordingRequested || slot.RestartPending) return;
        slot.RestartPending = true;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            await Dispatcher.InvokeAsync(() =>
            {
                slot.RestartPending = false;
                if (!slot.RecordingRequested || !Slots.Contains(slot)) return;
                slot.RestartCount++;
                StartRecord(slot, automaticRestart: true);
            });
        });
    }

    private static string BuildRestartOutputPath(IngestSlot slot)
    {
        var original = slot.OutputPath;
        var dir = Path.GetDirectoryName(original) ?? Environment.CurrentDirectory;
        var name = Path.GetFileNameWithoutExtension(original);
        var ext = Path.GetExtension(original);
        return Path.Combine(dir, $"{name}.part{Math.Max(2, slot.RestartCount + 1):00}{ext}");
    }

    private static bool? _ffmpegHasNdi;
    private static bool FfmpegHasNdiInput(string ffmpeg)
    {
        if (_ffmpegHasNdi is bool cached) return cached;
        try
        {
            var psi = new ProcessStartInfo(ffmpeg, "-hide_banner -demuxers") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi);
            if (p is null) return false;
            var text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(4000);
            return (_ffmpegHasNdi = text.Contains("libndi_newtek", StringComparison.OrdinalIgnoreCase) || text.Contains(" ndi", StringComparison.OrdinalIgnoreCase)).Value;
        }
        catch { return (_ffmpegHasNdi = false).Value; }
    }

    private static void AddInputArguments(ProcessStartInfo psi, PlaylistItem item)
    {
        if (item.SourceKind.Equals("DirectShow", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("-thread_queue_size"); psi.ArgumentList.Add("2048");
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("dshow"); psi.ArgumentList.Add("-rtbufsize"); psi.ArgumentList.Add("512M");
            var parts = new List<string>(); if (!string.IsNullOrWhiteSpace(item.VideoDevice)) parts.Add("video=" + item.VideoDevice); if (!string.IsNullOrWhiteSpace(item.AudioDevice)) parts.Add("audio=" + item.AudioDevice);
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(string.Join(":", parts)); return;
        }
        if (item.SourceKind.Equals("Screen", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("gdigrab"); psi.ArgumentList.Add("-framerate"); psi.ArgumentList.Add(item.SourceFrameRate > 0 ? item.SourceFrameRate.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "25"); psi.ArgumentList.Add("-i"); psi.ArgumentList.Add("desktop"); return;
        }
        if (item.SourceKind.Equals("NDI", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("-thread_queue_size"); psi.ArgumentList.Add("2048");
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("libndi_newtek"); psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(item.FilePath); return;
        }
        var source = item.FilePath?.Trim() ?? string.Empty;
        if (source.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("-rtsp_transport"); psi.ArgumentList.Add("tcp");
            psi.ArgumentList.Add("-rw_timeout"); psi.ArgumentList.Add("10000000");
        }
        else if (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("-reconnect"); psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-reconnect_streamed"); psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-reconnect_at_eof"); psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-reconnect_delay_max"); psi.ArgumentList.Add("5");
        }
        if (!string.IsNullOrWhiteSpace(item.InputFormat)) { psi.ArgumentList.Add("-f"); psi.ArgumentList.Add(item.InputFormat); }
        psi.ArgumentList.Add("-thread_queue_size"); psi.ArgumentList.Add("2048");
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(source);
    }

    private static readonly object EncoderProbeSync = new();
    private static string _encoderProbeExe = string.Empty;
    private static string _encoderProbeOutput = string.Empty;

    private static void AddMappedOutput(ProcessStartInfo psi, string ffmpeg, string format, string path, bool growingFile, bool segmented, int segmentMinutes, bool videoOnly = false)
    {
        psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("0:v?");
        if (!videoOnly) { psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("0:a?"); }
        psi.ArgumentList.Add("-map_metadata"); psi.ArgumentList.Add("0");
        psi.ArgumentList.Add("-max_muxing_queue_size"); psi.ArgumentList.Add("4096");
        AddRecordArguments(psi, ffmpeg, format, growingFile);
        if (segmented)
        {
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("segment");
            psi.ArgumentList.Add("-segment_time"); psi.ArgumentList.Add((Math.Clamp(segmentMinutes, 1, 1440) * 60).ToString());
            psi.ArgumentList.Add("-reset_timestamps"); psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-strftime"); psi.ArgumentList.Add("1");
            psi.ArgumentList.Add(BuildSegmentPattern(path));
        }
        else psi.ArgumentList.Add(path);
    }

    private static string BuildSegmentPattern(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory;
        var name = Path.GetFileNameWithoutExtension(path); var ext = Path.GetExtension(path);
        return Path.Combine(directory, name + "-%Y%m%d-%H%M%S" + ext);
    }

    private static string BuildRestartProxyOutputPath(IngestSlot slot)
    {
        var original = string.IsNullOrWhiteSpace(slot.ProxyOutputPath) ? BuildProxyOutputPath(slot.OutputPath) : slot.ProxyOutputPath;
        var dir = Path.GetDirectoryName(original) ?? Environment.CurrentDirectory;
        var name = Path.GetFileNameWithoutExtension(original); var ext = Path.GetExtension(original);
        return Path.Combine(dir, $"{name}.part{Math.Max(2, slot.RestartCount + 1):00}{ext}");
    }

    private static void AddRecordArguments(ProcessStartInfo psi, string ffmpeg, string format, bool growingFile = false)
    {
        if (format.Equals("Stream Copy", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("copy");
            return;
        }
        if (format.StartsWith("H.265 MP4", StringComparison.OrdinalIgnoreCase) || format.Equals("H.265 MP4", StringComparison.OrdinalIgnoreCase))
        {
            AddAdaptiveVideoEncoder(psi, ResolveVideoEncoder(ffmpeg, h265: true), h265: true);
            AddAacAudio(psi); AddMp4Flags(psi, growingFile); return;
        }
        if (format.StartsWith("H.264 MP4 · Proxy", StringComparison.OrdinalIgnoreCase))
        {
            var encoder = ResolveVideoEncoder(ffmpeg, h265: false);
            psi.ArgumentList.Add("-vf"); psi.ArgumentList.Add("scale=1280:-2");
            psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add(encoder);
            if (encoder.Equals("libx264", StringComparison.OrdinalIgnoreCase)) { psi.ArgumentList.Add("-preset"); psi.ArgumentList.Add("veryfast"); psi.ArgumentList.Add("-crf"); psi.ArgumentList.Add("23"); }
            else { psi.ArgumentList.Add("-b:v"); psi.ArgumentList.Add("3M"); }
            psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add(encoder.EndsWith("_mf", StringComparison.OrdinalIgnoreCase) ? "nv12" : "yuv420p");
            psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add("aac"); psi.ArgumentList.Add("-b:a"); psi.ArgumentList.Add("128k");
            AddMp4Flags(psi, growingFile); return;
        }
        if (format.StartsWith("XDCAM HD422", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add("mpeg2video"); psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add("yuv422p");
            psi.ArgumentList.Add("-b:v"); psi.ArgumentList.Add("50M"); psi.ArgumentList.Add("-minrate"); psi.ArgumentList.Add("50M"); psi.ArgumentList.Add("-maxrate"); psi.ArgumentList.Add("50M"); psi.ArgumentList.Add("-bufsize"); psi.ArgumentList.Add("17825792"); psi.ArgumentList.Add("-g"); psi.ArgumentList.Add("12"); psi.ArgumentList.Add("-bf"); psi.ArgumentList.Add("2");
            AddPcmAudio(psi); return;
        }
        if (format.StartsWith("XDCAM EX", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add("mpeg2video"); psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add("yuv420p");
            psi.ArgumentList.Add("-b:v"); psi.ArgumentList.Add("35M"); psi.ArgumentList.Add("-maxrate"); psi.ArgumentList.Add("35M"); psi.ArgumentList.Add("-bufsize"); psi.ArgumentList.Add("8M"); psi.ArgumentList.Add("-g"); psi.ArgumentList.Add("12");
            AddPcmAudio(psi); return;
        }
        if (format.StartsWith("DV PAL", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("-vf"); psi.ArgumentList.Add("scale=720:576,fps=25"); psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add("dvvideo"); psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add("yuv420p"); AddPcmAudio(psi); return;
        }
        if (format.StartsWith("DVCPRO50", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("-vf"); psi.ArgumentList.Add("scale=720:576,fps=25"); psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add("dvvideo"); psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add("yuv422p"); AddPcmAudio(psi); return;
        }
        if (format.StartsWith("ProRes", StringComparison.OrdinalIgnoreCase) || format.Equals("ProRes MOV", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add("prores_ks"); psi.ArgumentList.Add("-profile:v"); psi.ArgumentList.Add("3"); psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add("yuv422p10le"); AddPcmAudio(psi); return;
        }
        if (format.StartsWith("DNxHD", StringComparison.OrdinalIgnoreCase) || format.Equals("DNxHD MXF", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add("dnxhd"); psi.ArgumentList.Add("-b:v"); psi.ArgumentList.Add("120M"); psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add("yuv422p"); AddPcmAudio(psi); return;
        }
        if (format.StartsWith("MPEG-2 TS", StringComparison.OrdinalIgnoreCase) || format.Equals("MPEG-2 TS", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add("mpeg2video"); psi.ArgumentList.Add("-b:v"); psi.ArgumentList.Add("15M"); psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add("mp2"); psi.ArgumentList.Add("-b:a"); psi.ArgumentList.Add("256k"); return;
        }

        AddAdaptiveVideoEncoder(psi, ResolveVideoEncoder(ffmpeg, h265: false), h265: false);
        AddAacAudio(psi); AddMp4Flags(psi, growingFile);
    }

    private static void AddMp4Flags(ProcessStartInfo psi, bool growingFile)
    {
        psi.ArgumentList.Add("-movflags");
        psi.ArgumentList.Add(growingFile ? "+frag_keyframe+empty_moov+default_base_moof" : "+faststart");
    }

    private static void AddPcmAudio(ProcessStartInfo psi)
    {
        psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add("pcm_s16le");
        psi.ArgumentList.Add("-ar"); psi.ArgumentList.Add("48000");
        // Do not force stereo: preserve the captured stream's embedded multichannel layout.
    }

    private static void AddAdaptiveVideoEncoder(ProcessStartInfo psi, string encoder, bool h265)
    {
        psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add(encoder);
        if (encoder.Equals("libx264", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("-preset"); psi.ArgumentList.Add("veryfast");
            psi.ArgumentList.Add("-crf"); psi.ArgumentList.Add("18");
        }
        else if (encoder.Equals("libx265", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("-preset"); psi.ArgumentList.Add("medium");
            psi.ArgumentList.Add("-crf"); psi.ArgumentList.Add("22");
        }
        else
        {
            // Media Foundation / vendor hardware encoders use a common bitrate path;
            // this avoids passing libx26x-only options to encoders that reject them.
            psi.ArgumentList.Add("-b:v"); psi.ArgumentList.Add(h265 ? "10M" : "8M");
        }
        psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add(encoder.EndsWith("_mf", StringComparison.OrdinalIgnoreCase) ? "nv12" : "yuv420p");
    }

    private static void AddAacAudio(ProcessStartInfo psi)
    {
        psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add("aac");
        psi.ArgumentList.Add("-b:a"); psi.ArgumentList.Add("192k");
    }

    private static string ResolveVideoEncoder(string ffmpeg, bool h265)
    {
        // The bundled LGPL runtime does not guarantee libx264/libx265. Probe what the
        // installed Windows runtime actually provides and pick a compatible encoder.
        // Ingest is a long-running realtime workload: prefer a Windows/vendor hardware
        // encoder to keep CPU available for playout/CG/multiview, then fall back to software.
        var candidates = h265
            ? new[] { "hevc_mf", "hevc_qsv", "hevc_nvenc", "hevc_amf", "libx265" }
            : new[] { "h264_mf", "h264_qsv", "h264_nvenc", "h264_amf", "libopenh264", "libx264" };
        var list = GetEncoderList(ffmpeg);
        foreach (var candidate in candidates)
            if (list.Contains(" " + candidate + " ", StringComparison.OrdinalIgnoreCase))
                return candidate;

        var codec = h265 ? "H.265" : "H.264";
        throw new InvalidOperationException($"{codec} recording encoder is unavailable in the installed media runtime. Run SETUP-AND-BUILD.cmd to repair the runtime, then retry recording.");
    }

    private static string GetEncoderList(string ffmpeg)
    {
        lock (EncoderProbeSync)
        {
            if (_encoderProbeExe.Equals(ffmpeg, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(_encoderProbeOutput))
                return _encoderProbeOutput;

            try
            {
                var probe = new ProcessStartInfo { FileName = ffmpeg, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                probe.ArgumentList.Add("-hide_banner"); probe.ArgumentList.Add("-encoders");
                using var process = Process.Start(probe) ?? throw new InvalidOperationException("Unable to inspect media encoders.");
                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                if (!process.WaitForExit(5000))
                {
                    try { process.Kill(true); } catch { }
                    throw new TimeoutException("Media encoder probe timed out.");
                }
                _encoderProbeExe = ffmpeg;
                _encoderProbeOutput = stdout + Environment.NewLine + stderr;
                return _encoderProbeOutput;
            }
            catch
            {
                _encoderProbeExe = ffmpeg;
                _encoderProbeOutput = string.Empty;
                return string.Empty;
            }
        }
    }

    private void StopRecord(IngestSlot slot)
    {
        slot.RecordingRequested = false;
        slot.RestartPending = false;
        var native = slot.NativeRecordCts; slot.NativeRecordCts = null; try { native?.Cancel(); } catch { }
        var process = slot.Process;
        if (process is null)
        {
            slot.Status = "STOPPED";
            if (slot.PreviewSuspendedForRecord)
            {
                slot.PreviewSuspendedForRecord = false;
                StartPreview(slot);
            }
            return;
        }
        try
        {
            if (!process.HasExited)
            {
                process.StandardInput.WriteLine("q"); process.StandardInput.Flush();
                if (!process.WaitForExit(5000)) process.Kill(true);
            }
        }
        catch { try { if (!process.HasExited) process.Kill(true); } catch { } }
        if (ReferenceEquals(slot.Process, process)) slot.Process = null;
        slot.Status = "SAVED";
        slot.LastMessage = string.IsNullOrWhiteSpace(slot.CurrentOutputPath) ? "Recording stopped" : $"Saved · {Path.GetFileName(slot.CurrentOutputPath)}";
        AuditLogService.Write("INGEST_STOP", slot.Title, string.IsNullOrWhiteSpace(slot.CurrentOutputPath) ? slot.RecordFormat : $"{slot.RecordFormat} · {slot.CurrentOutputPath}");
        if (slot.PreviewSuspendedForRecord)
        {
            slot.PreviewSuspendedForRecord = false;
            StartPreview(slot);
        }
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        foreach (var path in paths.Where(File.Exists))
        {
            try
            {
                var item = new PlaylistItem { Title = Path.GetFileNameWithoutExtension(path), EventType = "FILE", Category = "Ingest", SourceKind = "File", FilePath = path, IsLiveSource = false };
                try
                {
                    var info = await Task.Run(() => MediaProbe.Read(item));
                    item.SourceDuration = info.Duration; item.OutPoint = info.Duration; item.SourceFrameRate = info.FramesPerSecond; item.VideoFormat = $"{info.Width}x{info.Height} {info.FramesPerSecond:0.###}p";
                }
                catch { item.SourceDuration = TimeSpan.FromHours(1); item.OutPoint = item.SourceDuration; }
                AddSlot(item);
            }
            catch (Exception ex) { FooterStatus.Text = "Drop import failed · " + ex.Message; }
        }
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        using var d = new Forms.FolderBrowserDialog { Description = "Choose ingest recording folder", UseDescriptionForTitle = true, SelectedPath = OutputFolderBox.Text };
        if (d.ShowDialog() == Forms.DialogResult.OK) OutputFolderBox.Text = d.SelectedPath;
    }
    private void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        var script = ToolLocator.FindTool("Debug-Outputs.ps1");
        if (string.IsNullOrWhiteSpace(script)) { FooterStatus.Text = "Debug-Outputs.ps1 not found."; return; }
        Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"") { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(Path.GetDirectoryName(script)) ?? AppContext.BaseDirectory });
    }

    private static string? PromptText(string title, string caption, string initial)
    {
        var box = new System.Windows.Controls.TextBox { Text = initial, Margin = new Thickness(12), MinWidth = 430 };
        var ok = new System.Windows.Controls.Button { Content = "ADD", IsDefault = true, Width = 90, Margin = new Thickness(5) };
        var cancel = new System.Windows.Controls.Button { Content = "CANCEL", IsCancel = true, Width = 90, Margin = new Thickness(5) };
        var stack = new System.Windows.Controls.StackPanel(); stack.Children.Add(new System.Windows.Controls.TextBlock { Text = caption, Margin = new Thickness(12,12,12,0), Foreground = Brushes.White }); stack.Children.Add(box);
        var buttons = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; buttons.Children.Add(ok); buttons.Children.Add(cancel); stack.Children.Add(buttons);
        var win = new Window { Title = title, Width = 500, Height = 160, Content = stack, Background = new SolidColorBrush(Color.FromRgb(8,13,18)), WindowStartupLocation = WindowStartupLocation.CenterScreen };
        ok.Click += (_, _) => win.DialogResult = true; return win.ShowDialog() == true ? box.Text : null;
    }
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => WindowChromeActions.Drag(this, e);
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowChromeActions.Minimize(this);
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowChromeActions.ToggleMaximize(this);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; return; }
        base.OnPreviewKeyDown(e);
    }
}

public sealed class IngestSlot : INotifyPropertyChanged
{
    private BitmapSource? _previewImage; private string _previewMessage = "WAITING"; private string _status = "READY"; private string _outputPath = ""; private string _recordFormat = "H.264 MP4 · Broadcast"; private string _lastMessage = "";
    private string _proxyOutputPath = ""; private bool _dualMasterProxy; private bool _complianceMode; private int _segmentMinutes = 60; private bool _growingFileMode;
    private string _scheduleStartText = ""; private string _scheduleStopText = ""; private bool _scheduleEnabled;
    public IngestSlot(PlaylistItem item) => Item = item;
    public PlaylistItem Item { get; }
    public string Title => string.IsNullOrWhiteSpace(Item.Title) ? Item.SourceKind : Item.Title;
    public string SourceSummary => Item.SourceKind.Equals("DirectShow", StringComparison.OrdinalIgnoreCase) ? $"{Item.VideoDevice} / {Item.AudioDevice}" : $"{Item.SourceKind} · {Item.FilePath}";
    public BitmapSource? PreviewImage { get => _previewImage; set { _previewImage = value; Raise(); } }
    public string PreviewMessage { get => _previewMessage; set { _previewMessage = value; Raise(); } }
    private double _audioLevel;
    public double AudioLevel { get => _audioLevel; set { if (Math.Abs(_audioLevel - value) < 0.1) return; _audioLevel = value; Raise(); } }
    public string Status { get => _status; set { _status = value; Raise(); } }
    public string OutputPath { get => _outputPath; set { _outputPath = value; Raise(); } }
    public string RecordFormat { get => _recordFormat; set { _recordFormat = value; Raise(); Raise(nameof(PresetSummary)); } }
    public string PresetSummary => IngestServerWindow.DescribePreset(RecordFormat);
    public string LastMessage { get => _lastMessage; set { _lastMessage = value; Raise(); } }
    public bool DualMasterProxy { get => _dualMasterProxy; set { _dualMasterProxy = value; Raise(); } }
    public string ProxyOutputPath { get => _proxyOutputPath; set { _proxyOutputPath = value; Raise(); } }
    public bool ComplianceMode { get => _complianceMode; set { _complianceMode = value; Raise(); } }
    public int SegmentMinutes { get => _segmentMinutes; set { _segmentMinutes = Math.Clamp(value, 1, 1440); Raise(); } }
    public bool GrowingFileMode { get => _growingFileMode; set { _growingFileMode = value; Raise(); } }
    public string ScheduleStartText { get => _scheduleStartText; set { _scheduleStartText = value; Raise(); } }
    public string ScheduleStopText { get => _scheduleStopText; set { _scheduleStopText = value; Raise(); } }
    public bool ScheduleEnabled { get => _scheduleEnabled; set { _scheduleEnabled = value; Raise(); Raise(nameof(ScheduleBadge)); } }
    public string ScheduleBadge => ScheduleEnabled ? "ARMED" : "OFF";
    public Guid ScheduleId { get; set; }
    public DateTime? ScheduledStartLocal { get; set; }
    public DateTime? ScheduledStopLocal { get; set; }
    public bool ScheduleTriggered { get; set; }
    public Process? Process { get; set; }
    public bool RecordingRequested { get; set; }
    public bool RestartPending { get; set; }
    public int RestartCount { get; set; }
    public bool PreviewSuspendedForRecord { get; set; }
    public string CurrentOutputPath { get; set; } = string.Empty;
    public string CurrentProxyOutputPath { get; set; } = string.Empty;
    public DateTime RecordingStartedUtc { get; set; } = DateTime.UtcNow;
    public object FinalizeSync { get; } = new();
    public HashSet<string> FinalizedOutputPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    public CancellationTokenSource? NativeRecordCts { get; set; }
    private CancellationTokenSource? _previewCts;
    public CancellationTokenSource? PreviewCts { get => _previewCts; set => _previewCts = value; }
    public void StopPreview() { var c = _previewCts; _previewCts = null; try { c?.Cancel(); c?.Dispose(); } catch { } }
    public event PropertyChangedEventHandler? PropertyChanged; private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
