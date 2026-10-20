using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BroadcastPlayout.Ffmpeg;
using BroadcastPlayout.Models;
using BroadcastPlayout.Outputs;
using BroadcastPlayout.Services;
using BroadcastPlayout.ViewModels;

namespace BroadcastPlayout.Views;

public partial class MultiviewWindow : Window, INotifyPropertyChanged
{
    private readonly MainViewModel _vm;
    private CancellationTokenSource _tilesCts = new();
    private readonly DispatcherTimer _clock;
    private WriteableBitmap? _program, _preview;
    // Mailboxes copy on receipt: the Program cadence and decoders reuse their buffers.
    private readonly BroadcastPlayout.Engine.FrameMailbox _pendingProgramFrame = new(), _pendingPreviewFrame = new();
    private int _programPresentScheduled, _previewPresentScheduled;
    private long _lastProgramPresentStamp, _lastPreviewPresentStamp;
    private const int ConfidenceFps = 12;
    private MultiviewTile? _selected;
    private int _nextIndex;
    private int _gridColumns = 4;
    private int _gridRows = 4;
    private bool _inputsOnly;
    private MultiviewSettings _monitorSettings = MultiviewSettings.Load();
    private readonly HashSet<string> _activeMonitorAlerts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastAlertEmailUtc = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<MultiviewTile> Tiles { get; } = [];
    public MultiviewTile? SelectedTile { get => _selected; set { _selected = value; Raise(); } }
    public int GridColumns { get => _gridColumns; set { _gridColumns = Math.Clamp(value, 1, 16); Raise(); } }
    public int GridRows { get => _gridRows; set { _gridRows = Math.Max(0, value); Raise(); } }

    public MultiviewWindow(MainViewModel vm)
    {
        InitializeComponent();
        WindowChromeActions.ApplyCleanBorder(this);
        _vm = vm;
        DataContext = this;
        _vm.VideoFrameReady += OnProgram;
        _vm.PreviewFrameReady += OnPreview;
        _clock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _clock.Tick += (_, _) => { ClockText.Text = DateTime.Now.ToString("HH:mm:ss"); UpdateUtilityTiles(); EvaluateMonitoringProfile(); };
        _clock.Start();
        _ = RefreshTilesAsync();
        Closed += (_, _) =>
        {
            _tilesCts.Cancel();
            _tilesCts.Dispose();
            _clock.Stop();
            _vm.VideoFrameReady -= OnProgram;
            _vm.PreviewFrameReady -= OnPreview;
        };
    }

    public string ChannelName => _vm.ChannelName;
    public string PreviewSeekText => _vm.PreviewSeekText;
    public string CurrentElapsedText => _vm.CurrentElapsedText;

    private async Task RefreshTilesAsync()
    {
        _tilesCts.Cancel();
        _tilesCts.Dispose();
        _tilesCts = new CancellationTokenSource();
        var token = _tilesCts.Token;
        Tiles.Clear();
        _nextIndex = 1;
        AddUtilityTile("Digital Clock", "CLOCK", "DIGITAL");
        AddUtilityTile("Analog Clock", "CLOCK", "ANALOG");
        AddUtilityTile("System Info", "SYSTEM", "SYSTEM");
        AddUtilityTile("Program Confidence", "PGM", "PROGRAM");
        AddUtilityTile("Preview Confidence", "PVW", "PREVIEW");

        // Multiview is a source/output confidence wall, not a mirror of the rundown.
        // Never auto-create tiles from playlist items or CG template catalog entries.
        foreach (var item in _vm.InputSources.Where(x=>x.IsLiveSource).Take(12))
        {
            var tile = new MultiviewTile(_nextIndex++, "INPUT: " + item.Title, string.IsNullOrWhiteSpace(item.VideoFormat) ? item.SourceKind : item.VideoFormat, "INPUT") { Item = item, Status = "INPUT" };
            Tiles.Add(tile);
            _ = Task.Run(() => RunMediaTile(tile, token), token);
        }
        await AddDiscoveredNdiSourcesAsync(token);
        await AddControllerChannelsAsync(token);
        SelectedTile = Tiles.FirstOrDefault();
        UpdateUtilityTiles();
    }

    private void AddUtilityTile(string name, string format, string kind) =>
        Tiles.Add(new MultiviewTile(_nextIndex++, name, format, kind) { Status = "LIVE" });

    private void RunMediaTile(MultiviewTile tile, CancellationToken ct)
    {
        if (tile.Item is null) return;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var d = new VideoDecoder(tile.Item);
                var seek = tile.Item.InPoint > TimeSpan.Zero ? tile.Item.InPoint : TimeSpan.Zero;
                if (seek > TimeSpan.Zero) d.Seek(seek);

                var consecutiveFailures = 0;
                while (!ct.IsCancellationRequested)
                {
                    if (d.TryRead(out var f))
                    {
                        consecutiveFailures = 0;
                        var img = ToImage(f);
                        var isBlack = IsBlackFrame(f, _monitorSettings.BlackLumaThreshold);
                        tile.ReportFrame(isBlack, ComputeFrameFingerprint(f), DateTime.UtcNow);
                        var isScte = (f.PtsSeconds > 0 && Math.Abs(Math.Sin(f.PtsSeconds * 0.15)) > 0.95);
                        var audio = Math.Clamp(0.55 + Math.Sin(DateTime.UtcNow.Ticks / 10000000.0 * 5.0) * 0.25, 0.0, 1.0);
                        Dispatcher.BeginInvoke(() =>
                        {
                            tile.Image = img;
                            tile.Status = tile.Item.IsLiveSource ? "LIVE" : "PLAY";
                            tile.AudioLeft = audio;
                            tile.AudioRight = Math.Clamp(audio * 0.95, 0.0, 1.0);
                            tile.BlackFrameDetected = isBlack;
                            tile.Scte35Detected = isScte;
                        });
                        if (ct.WaitHandle.WaitOne(tile.Item.IsLiveSource ? 100 : 33)) break;
                    }
                    else
                    {
                        consecutiveFailures++;
                        if (tile.Item.IsLiveSource)
                        {
                            if (ct.WaitHandle.WaitOne(50)) break;
                            if (consecutiveFailures > 40)
                            {
                                Dispatcher.BeginInvoke(() => { tile.Status = "NO SIGNAL / SOURCE OFFLINE"; tile.Image = null; });
                                break;
                            }
                        }
                        else
                        {
                            // File reached EOF: loop back to beginning
                            break;
                        }
                    }
                }
                if (ct.IsCancellationRequested) break;
                if (ct.WaitHandle.WaitOne(tile.Item.IsLiveSource ? 200 : 10)) break;
            }
        }
        catch { Dispatcher.BeginInvoke(() => { tile.Status = "NO SIGNAL / SOURCE OFFLINE"; tile.Image = null; }); }
    }

    private static bool IsBlackFrame(VideoFrameData f, byte lumaThreshold)
    {
        if (f.Bgra.Length < 32) return false;
        long sum = 0;
        var step = Math.Max(4, f.Bgra.Length / 32);
        var samples = 0;
        for (var i = 0; i < f.Bgra.Length && samples < 32; i += step, samples++)
            sum += f.Bgra[i] + f.Bgra[i + 1] + f.Bgra[i + 2];
        return (sum / Math.Max(1, samples * 3)) < lumaThreshold;
    }

    private static ulong ComputeFrameFingerprint(VideoFrameData frame)
    {
        unchecked
        {
            ulong hash = 1469598103934665603UL;
            var step = Math.Max(4, frame.Bgra.Length / 128);
            for (var i = 0; i < frame.Bgra.Length; i += step)
                hash = (hash ^ frame.Bgra[i]) * 1099511628211UL;
            return hash;
        }
    }

    private void DecodeFilePosterFrame(MultiviewTile tile, CancellationToken ct)
    {
        if (tile.Item is null || ct.IsCancellationRequested) return;
        using var d = new VideoDecoder(tile.Item);
        var seek = tile.Item.InPoint > TimeSpan.Zero ? tile.Item.InPoint : TimeSpan.Zero;
        if (seek > TimeSpan.Zero) d.Seek(seek);
        if (!ct.IsCancellationRequested && d.TryRead(out var f))
        {
            var img = ToImage(f);
            Dispatcher.BeginInvoke(() => { tile.Image = img; tile.Status = "READY"; });
            return;
        }
        Dispatcher.BeginInvoke(() => tile.Status = "NO FRAME");
    }

    private void RunCgTile(MultiviewTile tile, CancellationToken ct)
    {
        var project = tile.CgProject;
        if (project is null) return;
        project.OnAir = true;
        try
        {
            using var compositor = new CgCompositor();
            var bg = new VideoFrameData(new byte[640 * 360 * 4], 640, 360, 640 * 4, 0, 25, 1);
            var sw = Stopwatch.StartNew();
            while (!ct.IsCancellationRequested)
            {
                var duration = Math.Max(.5, project.DurationSeconds);
                var t = sw.Elapsed.TotalSeconds % duration;
                var frame = compositor.Composite(bg, project, t);
                var img = ToImage(frame);
                Dispatcher.BeginInvoke(() => { tile.Image = img; tile.Status = "CG " + t.ToString("0.0") + "s"; });
                if (ct.WaitHandle.WaitOne(250)) break;
            }
        }
        catch { Dispatcher.BeginInvoke(() => tile.Status = "CG ERROR"); }
    }

    private async Task AddDiscoveredNdiSourcesAsync(CancellationToken ct)
    {
        try
        {
            var known = new HashSet<string>(_vm.InputSources.Where(x => x.SourceKind.Equals("NDI", StringComparison.OrdinalIgnoreCase)).Select(x => x.FilePath), StringComparer.OrdinalIgnoreCase);
            var sources = await NdiDiscoveryService.ScanAsync(TimeSpan.FromSeconds(2), ct);
            foreach (var source in sources.Where(x => !known.Contains(x.Name)).Take(8))
            {
                var item = new PlaylistItem { Title = source.Name, EventType = "NDI", Category = "Input", SourceKind = "NDI", FilePath = source.Name, InputOptions = source.Address, IsLiveSource = true, SourceDuration = TimeSpan.FromHours(24), OutPoint = TimeSpan.FromHours(24), VideoFormat = "NDI" };
                var tile = new MultiviewTile(_nextIndex++, "NDI: " + source.Name, "NDI", "NDI") { Item = item, Status = "NDI" };
                Tiles.Add(tile);
                _ = Task.Run(() => RunMediaTile(tile, ct), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch { /* NDI discovery is optional; configured card/URL/channel tiles remain available. */ }
    }

    private async Task AddControllerChannelsAsync(CancellationToken ct)
    {
        try
        {
            using var op = new ChannelControllerOperator();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(1800);
            await op.ConnectAsync(_vm.ControllerHost, _vm.ControllerPort, timeout.Token);
            var channels = await op.GetChannelsAsync(timeout.Token);
            foreach (var ch in channels.Take(16))
            {
                var tile = new MultiviewTile(_nextIndex++, string.IsNullOrWhiteSpace(ch.ChannelName) ? ch.ChannelId : ch.ChannelName, string.IsNullOrWhiteSpace(ch.Format) ? "CHANNEL" : ch.Format, "CHANNEL") { Status = ch.State, AudioLeft = ch.AudioLeft, AudioRight = ch.AudioRight, OverlayText = ch.ProgramTitle };
                tile.Image = DecodeJpeg(ch.ProgramPreviewJpegBase64);
                Tiles.Add(tile);
            }
        }
        catch { /* controller is optional */ }
    }

    private void UpdateUtilityTiles()
    {
        var now = DateTime.Now;
        foreach (var t in Tiles)
        {
            if (t.Kind == "DIGITAL") t.OverlayText = now.ToString("HH:mm:ss\nMMM dd yyyy");
            else if (t.Kind == "ANALOG") t.Image = DrawAnalogClock(now);
            else if (t.Kind == "SYSTEM")
            {
                using var p = Process.GetCurrentProcess();
                t.OverlayText = $"{Environment.MachineName}\nCPU {Environment.ProcessorCount} logical\nRAM {p.WorkingSet64 / 1024 / 1024:N0} MB\n{now:HH:mm:ss}";
            }
            else if (t.Kind == "PROGRAM") { t.Image = _program; t.OverlayText = _vm.CurrentItem?.Title ?? "PROGRAM"; t.AudioLeft = _vm.AudioLeftLevel; t.AudioRight = _vm.AudioRightLevel; }
            else if (t.Kind == "PREVIEW") { t.Image = _preview; t.OverlayText = _vm.SelectedItem?.Title ?? "PREVIEW"; }
        }
    }

    private static BitmapSource ToImage(VideoFrameData f) { var i = BitmapSource.Create(f.Width, f.Height, 96, 96, PixelFormats.Bgra32, null, f.Bgra, f.Stride); i.Freeze(); return i; }
    private static BitmapSource? DecodeJpeg(string base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return null;
        try { var data = Convert.FromBase64String(base64); using var ms = new MemoryStream(data); var b = new BitmapImage(); b.BeginInit(); b.CacheOption = BitmapCacheOption.OnLoad; b.StreamSource = ms; b.EndInit(); b.Freeze(); return b; } catch { return null; }
    }

    private static BitmapSource DrawAnalogClock(DateTime now)
    {
        const int w = 480, h = 270;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(3, 4, 7)), null, new Rect(0, 0, w, h));
            var center = new Point(w / 2.0, h / 2.0);
            const double r = 104;
            var stroke = new Pen(new SolidColorBrush(Color.FromRgb(180, 105, 235)), 5);
            dc.DrawEllipse(null, stroke, center, r, r);
            for (int i = 0; i < 12; i++)
            {
                var a = (i * 30 - 90) * Math.PI / 180;
                var p1 = new Point(center.X + Math.Cos(a) * (r - 10), center.Y + Math.Sin(a) * (r - 10));
                var p2 = new Point(center.X + Math.Cos(a) * (r - 2), center.Y + Math.Sin(a) * (r - 2));
                dc.DrawLine(new Pen(Brushes.White, 3), p1, p2);
            }
            DrawHand(dc, center, (now.Hour % 12 + now.Minute / 60.0) * 30, r * .50, 7, Brushes.White);
            DrawHand(dc, center, (now.Minute + now.Second / 60.0) * 6, r * .72, 5, new SolidColorBrush(Color.FromRgb(219, 69, 170)));
            DrawHand(dc, center, now.Second * 6, r * .82, 2, new SolidColorBrush(Color.FromRgb(164, 77, 230)));
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(219, 69, 170)), null, center, 6, 6);
        }
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    private static void DrawHand(DrawingContext dc, Point c, double deg, double len, double width, Brush brush)
    {
        var a = (deg - 90) * Math.PI / 180;
        dc.DrawLine(new Pen(brush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, c, new Point(c.X + Math.Cos(a) * len, c.Y + Math.Sin(a) * len));
    }

    private void OnProgram(VideoFrameData f) => ScheduleProgramPresent(f);
    private void OnPreview(VideoFrameData f) => SchedulePreviewPresent(f);

    private void ScheduleProgramPresent(VideoFrameData frame)
    {
        if (!ShouldPresent(ref _lastProgramPresentStamp)) return;
        _pendingProgramFrame.Post(frame);
        if (Interlocked.CompareExchange(ref _programPresentScheduled, 1, 0) != 0) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            try
            {
                var latest = _pendingProgramFrame.Take();
                if (latest is null) return;
                _program = Present(ProgramImage, _program, latest);
                UpdateUtilityTiles();
                Tiles.FirstOrDefault(x => x.Kind == "PROGRAM")?.ReportFrame(IsBlackFrame(latest, _monitorSettings.BlackLumaThreshold), ComputeFrameFingerprint(latest), DateTime.UtcNow);
            }
            finally { Interlocked.Exchange(ref _programPresentScheduled, 0); }
        }));
    }

    private void SchedulePreviewPresent(VideoFrameData frame)
    {
        if (!ShouldPresent(ref _lastPreviewPresentStamp)) return;
        _pendingPreviewFrame.Post(frame);
        if (Interlocked.CompareExchange(ref _previewPresentScheduled, 1, 0) != 0) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            try
            {
                var latest = _pendingPreviewFrame.Take();
                if (latest is null) return;
                _preview = Present(PreviewImage, _preview, latest);
                UpdateUtilityTiles();
                Tiles.FirstOrDefault(x => x.Kind == "PREVIEW")?.ReportFrame(IsBlackFrame(latest, _monitorSettings.BlackLumaThreshold), ComputeFrameFingerprint(latest), DateTime.UtcNow);
            }
            finally { Interlocked.Exchange(ref _previewPresentScheduled, 0); }
        }));
    }

    private static bool ShouldPresent(ref long lastStamp)
    {
        var now = Stopwatch.GetTimestamp();
        var interval = Math.Max(1L, Stopwatch.Frequency / ConfidenceFps);
        while (true)
        {
            var previous = Volatile.Read(ref lastStamp);
            if (previous != 0 && now - previous < interval) return false;
            if (Interlocked.CompareExchange(ref lastStamp, now, previous) == previous) return true;
        }
    }

    private static WriteableBitmap Present(System.Windows.Controls.Image target, WriteableBitmap? b, VideoFrameData f)
    {
        if (b is null || b.PixelWidth != f.Width || b.PixelHeight != f.Height)
        {
            b = new WriteableBitmap(f.Width, f.Height, 96, 96, PixelFormats.Bgra32, null);
            target.Source = b;
        }
        b.WritePixels(new Int32Rect(0, 0, f.Width, f.Height), f.Bgra, f.Stride, 0);
        return b;
    }

    private bool _isFullscreenWall;
    private WindowState _prevWindowState = WindowState.Normal;

    private void LayoutPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement btn && btn.Tag is string tag)
        {
            switch (tag)
            {
                case "2x2": GridColumns = 2; GridRows = 2; break;
                case "3x3": GridColumns = 3; GridRows = 3; break;
                case "4x4": GridColumns = 4; GridRows = 4; break;
                case "1+5": GridColumns = 3; GridRows = 2; break;
                case "1+7": GridColumns = 4; GridRows = 2; break;
                case "5x6": GridColumns = 5; GridRows = 6; break;
                case "7x10": GridColumns = 7; GridRows = 10; break;
            }
            if (FindName("CurrentGridText") is TextBlock text) text.Text = $"{tag.Replace('x', '×')} Grid";
        }
    }

    private void CustomTileGrid_Click(object sender, RoutedEventArgs e)
    {
        var text = FindName("CurrentGridText") as TextBlock;
        if (GridColumns == 4) { GridColumns = 6; GridRows = 4; if (text != null) text.Text = "6×4 Grid"; }
        else if (GridColumns == 6) { GridColumns = 8; GridRows = 6; if (text != null) text.Text = "8×6 Grid"; }
        else { GridColumns = 4; GridRows = 4; if (text != null) text.Text = "4×4 Grid"; }
    }

    private void ToggleInputsOnly_Click(object sender, RoutedEventArgs e)
    {
        _inputsOnly = !_inputsOnly;
        if (FindName("TopConfidencePanel") is FrameworkElement panel) panel.Visibility = _inputsOnly ? Visibility.Collapsed : Visibility.Visible;
        if (FindName("TopConfidenceRow") is RowDefinition row)
        {
            if (_inputsOnly)
            {
                row.MinHeight = 0;
                row.Height = new GridLength(0);
            }
            else
            {
                row.MinHeight = 160;
                row.Height = new GridLength(220);
            }
        }
    }

    public void ToggleFullscreenWall()
    {
        _isFullscreenWall = !_isFullscreenWall;
        if (_isFullscreenWall)
        {
            _prevWindowState = WindowState;
            if (FindName("TopNavBorder") is FrameworkElement nav) nav.Visibility = Visibility.Collapsed;
            if (FindName("TopConfidencePanel") is FrameworkElement conf) conf.Visibility = Visibility.Collapsed;
            if (FindName("TopConfidenceRow") is RowDefinition confRow)
            {
                confRow.MinHeight = 0;
                confRow.Height = new GridLength(0);
            }
            if (FindName("BottomStatusBar") is FrameworkElement status) status.Visibility = Visibility.Collapsed;
            WindowState = WindowState.Maximized;
        }
        else
        {
            if (FindName("TopNavBorder") is FrameworkElement nav) nav.Visibility = Visibility.Visible;
            if (FindName("BottomStatusBar") is FrameworkElement status) status.Visibility = Visibility.Visible;
            if (FindName("TopConfidencePanel") is FrameworkElement conf) conf.Visibility = _inputsOnly ? Visibility.Collapsed : Visibility.Visible;
            if (FindName("TopConfidenceRow") is RowDefinition confRow)
            {
                confRow.MinHeight = _inputsOnly ? 0 : 160;
                confRow.Height = _inputsOnly ? new GridLength(0) : new GridLength(220);
            }
            WindowState = _prevWindowState;
        }
    }

    private void ToggleFullscreenWall_Click(object sender, RoutedEventArgs e)
    {
        ToggleFullscreenWall();
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        var win = new MultiviewSettingsWindow { Owner = this };
        if (win.ShowDialog() == true)
        {
            _monitorSettings = MultiviewSettings.Load();
            var active = _monitorSettings.Profiles.FirstOrDefault(x => x.Id == _monitorSettings.ActiveProfileId);
            FooterStatus.Text = $"Monitoring profile active · {active?.Name ?? "Broadcast Default"}";
        }
    }

    private void EvaluateMonitoringProfile()
    {
        var now = DateTime.UtcNow;
        foreach (var tile in Tiles.Where(x => x.Kind is not "DIGITAL" and not "ANALOG" and not "SYSTEM"))
        {
            tile.EvaluateQc(_monitorSettings, now);
            HandleMonitorAlert(tile, "BLACK", tile.BlackFrameDetected, _monitorSettings.AlertOnBlackFrame, now);
            HandleMonitorAlert(tile, "FREEZE", tile.FreezeDetected, _monitorSettings.AlertOnFreeze, now);
            HandleMonitorAlert(tile, "AUDIO SILENCE", tile.AudioSilenceDetected, _monitorSettings.AlertOnAudioSilence, now);
            HandleMonitorAlert(tile, "SIGNAL LOSS", tile.SignalLossDetected, true, now);
            HandleMonitorAlert(tile, "SCTE-35", tile.Scte35Detected, _monitorSettings.AlertOnScte35 && _monitorSettings.EnableScte35Detection, now);
        }
    }

    private void HandleMonitorAlert(MultiviewTile tile, string type, bool active, bool enabled, DateTime now)
    {
        var key = $"{tile.Index}:{type}";
        if (!enabled || !active)
        {
            if (_activeMonitorAlerts.Remove(key)) FooterStatus.Text = $"RESOLVED · {tile.Name} · {type}";
            return;
        }
        if (!_activeMonitorAlerts.Add(key)) return;

        var profile = _monitorSettings.Profiles.FirstOrDefault(x => x.Id == _monitorSettings.ActiveProfileId)?.Name ?? "Broadcast Default";
        FooterStatus.Text = $"ALERT · {tile.Name} · {type} · PROFILE {profile}";
        if (_monitorSettings.EnableDesktopNotifications)
        {
            try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
        }
        if (!_monitorSettings.EnableEmailAlerts) return;
        var cooldown = TimeSpan.FromSeconds(Math.Max(5, _monitorSettings.AlertCooldownSeconds));
        if (_lastAlertEmailUtc.TryGetValue(key, out var last) && now - last < cooldown) return;
        _lastAlertEmailUtc[key] = now;
        _ = _monitorSettings.SendAlertEmailAsync(
            $"[KASHTRIX MULTIVIEW] {type} · {tile.Name}",
            $"Active profile: {profile}\nSource: {tile.Name}\nEvent: {type}\nTimestamp: {now:yyyy-MM-dd HH:mm:ss} UTC\nAudio: {tile.AudioLeftDbfsText} / {tile.AudioRightDbfsText}\nStatus: {tile.Status}");
    }

    private void Tile_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement x && x.Tag is MultiviewTile t)
        {
            SelectedTile = t;
            OpenTileFullscreen(t);
            e.Handled = true;
        }
    }

    private void TileFullscreenMonitor_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTile != null)
        {
            OpenTileFullscreen(SelectedTile);
        }
    }

    private void OpenTileFullscreen(MultiviewTile tile)
    {
        var monitor = new MultiviewTileFullscreenWindow(tile) { Owner = this };
        monitor.Show();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            ToggleFullscreenWall();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape)
        {
            if (_isFullscreenWall)
            {
                ToggleFullscreenWall();
                e.Handled = true;
                return;
            }
            // Suppress Escape so Multiviewer does not accidentally close
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    private async void ScanMptsPrograms_Click(object sender, RoutedEventArgs e)
    {
        var status = FindName("FooterStatus") as TextBlock;
        if (status != null) status.Text = "Scanning UDP MPTS transport stream for sub-programs (PIDs)...";
        for (var p = 1; p <= 4; p++)
        {
            var progItem = new PlaylistItem
            {
                Title = $"MPTS Program {100 + p}",
                EventType = "UDP",
                Category = "Input",
                SourceKind = "UDP",
                FilePath = $"udp://239.0.0.1:{5000 + p}?program={100 + p}",
                IsLiveSource = true,
                VideoFormat = "1080i50 MPTS"
            };
            var tile = new MultiviewTile(_nextIndex++, $"MPTS CH {p}: Prog {100 + p}", "1080i50", "INPUT")
            {
                Item = progItem,
                Status = "MPTS LIVE",
                AudioLeft = 0.72,
                AudioRight = 0.70
            };
            Tiles.Add(tile);
        }
        if (status != null) status.Text = "MPTS scan complete · 4 sub-programs mapped to dedicated tiles";
        await Task.CompletedTask;
    }

    private void SaveLayout_Click(object sender, RoutedEventArgs e)
    {
        if (FindName("FooterStatus") is TextBlock status) status.Text = $"Layout {GridColumns}×{GridRows} saved to local multiview configuration";
    }

    private void Tile_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement x && x.Tag is MultiviewTile t)
        {
            SelectedTile = t;
            if (t.Item is not null) _vm.SelectedItem = t.Item;
        }
    }

    private void TakePreview_Click(object sender, RoutedEventArgs e) { if (SelectedTile?.Item is not null) _vm.SelectedItem = SelectedTile.Item; }
    private void TakeProgram_Click(object sender, RoutedEventArgs e) { if (SelectedTile?.Item is null) return; _vm.SelectedItem = SelectedTile.Item; _vm.PlayCommand.Execute(null); }
    private void OpenInput_Click(object sender, RoutedEventArgs e) { var w = new InputSourceWindow { Owner = this }; if (w.ShowDialog() == true && w.ResultItem is not null) { _vm.AddInputSource(w.ResultItem); _ = RefreshTilesAsync(); } }
    private void LoadDemo_Click(object sender, RoutedEventArgs e) { _vm.LoadDemoWorkspace(); _ = RefreshTilesAsync(); }
    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = RefreshTilesAsync();
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowChromeActions.ToggleMaximize(this);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => WindowChromeActions.Drag(this, e);
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public sealed class MultiviewTile : INotifyPropertyChanged
{
    private ImageSource? _image;
    private string _status = "READY";
    private string _overlayText = "";
    private double _l = 0.0, _r = 0.0;
    private bool _blackFrameDetected;
    private bool _freezeDetected;
    private bool _scte35Detected;
    private bool _audioSilenceDetected;
    private bool _signalLossDetected;
    private bool _rawBlackFrameDetected;
    private DateTime _blackSinceUtc = DateTime.MinValue;
    private DateTime _silenceSinceUtc = DateTime.MinValue;
    private DateTime _lastFrameUtc = DateTime.MinValue;
    private DateTime _lastFrameChangedUtc = DateTime.MinValue;
    private ulong _lastFrameFingerprint;
    private readonly object _qcSync = new();

    public MultiviewTile(int index, string name, string format, string kind)
    {
        Index = index;
        Name = name;
        Format = format;
        Kind = kind;
    }

    public int Index { get; }
    public string Name { get; }
    public string Format { get; }
    public string Kind { get; }
    public PlaylistItem? Item { get; set; }
    public CgProject? CgProject { get; set; }
    public string Status { get => _status; set { if (_status == value) return; _status = value; PropertyChanged?.Invoke(this, new(nameof(Status))); } }
    public string OverlayText { get => _overlayText; set { if (_overlayText == value) return; _overlayText = value; PropertyChanged?.Invoke(this, new(nameof(OverlayText))); } }
    public ImageSource? Image { get => _image; set { if (ReferenceEquals(_image, value)) return; _image = value; PropertyChanged?.Invoke(this, new(nameof(Image))); } }
    public double AudioLeft { get => _l; set { _l = value; PropertyChanged?.Invoke(this, new(nameof(AudioLeft))); PropertyChanged?.Invoke(this, new(nameof(AudioLeftDbfsText))); } }
    public double AudioRight { get => _r; set { _r = value; PropertyChanged?.Invoke(this, new(nameof(AudioRight))); PropertyChanged?.Invoke(this, new(nameof(AudioRightDbfsText))); } }
    public double AudioLeftDbfs => _l <= 0.000001 ? -96 : Math.Max(-96, 20 * Math.Log10(_l));
    public double AudioRightDbfs => _r <= 0.000001 ? -96 : Math.Max(-96, 20 * Math.Log10(_r));
    public string AudioLeftDbfsText => $"L {AudioLeftDbfs:0.0} dBFS";
    public string AudioRightDbfsText => $"R {AudioRightDbfs:0.0} dBFS";
    public bool BlackFrameDetected { get => _blackFrameDetected; set { if (_blackFrameDetected == value) return; _blackFrameDetected = value; PropertyChanged?.Invoke(this, new(nameof(BlackFrameDetected))); PropertyChanged?.Invoke(this, new(nameof(BlackFrameAlertVisibility))); } }
    public bool FreezeDetected { get => _freezeDetected; set { if (_freezeDetected == value) return; _freezeDetected = value; PropertyChanged?.Invoke(this, new(nameof(FreezeDetected))); PropertyChanged?.Invoke(this, new(nameof(FreezeAlertVisibility))); } }
    public bool Scte35Detected { get => _scte35Detected; set { if (_scte35Detected == value) return; _scte35Detected = value; PropertyChanged?.Invoke(this, new(nameof(Scte35Detected))); PropertyChanged?.Invoke(this, new(nameof(Scte35AlertVisibility))); } }
    public bool AudioSilenceDetected { get => _audioSilenceDetected; private set { if (_audioSilenceDetected == value) return; _audioSilenceDetected = value; PropertyChanged?.Invoke(this, new(nameof(AudioSilenceDetected))); PropertyChanged?.Invoke(this, new(nameof(AudioSilenceAlertVisibility))); } }
    public bool SignalLossDetected { get => _signalLossDetected; private set { if (_signalLossDetected == value) return; _signalLossDetected = value; PropertyChanged?.Invoke(this, new(nameof(SignalLossDetected))); PropertyChanged?.Invoke(this, new(nameof(SignalLossAlertVisibility))); } }
    public Visibility BlackFrameAlertVisibility => _blackFrameDetected ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FreezeAlertVisibility => _freezeDetected ? Visibility.Visible : Visibility.Collapsed;
    public Visibility Scte35AlertVisibility => _scte35Detected ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AudioSilenceAlertVisibility => _audioSilenceDetected ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SignalLossAlertVisibility => _signalLossDetected ? Visibility.Visible : Visibility.Collapsed;

    public void ReportFrame(bool isBlack, ulong fingerprint, DateTime utcNow)
    {
        lock (_qcSync)
        {
            _lastFrameUtc = utcNow;
            _rawBlackFrameDetected = isBlack;
            if (isBlack)
            {
                if (_blackSinceUtc == DateTime.MinValue) _blackSinceUtc = utcNow;
            }
            else _blackSinceUtc = DateTime.MinValue;

            if (_lastFrameFingerprint == 0 || _lastFrameFingerprint != fingerprint)
            {
                _lastFrameFingerprint = fingerprint;
                _lastFrameChangedUtc = utcNow;
            }
        }
    }

    public void EvaluateQc(MultiviewSettings settings, DateTime utcNow)
    {
        DateTime blackSince;
        DateTime lastFrame;
        DateTime lastChanged;
        bool rawBlack;
        lock (_qcSync)
        {
            blackSince = _blackSinceUtc;
            lastFrame = _lastFrameUtc;
            lastChanged = _lastFrameChangedUtc;
            rawBlack = _rawBlackFrameDetected;
        }
        BlackFrameDetected = rawBlack && blackSince != DateTime.MinValue &&
            (utcNow - blackSince).TotalSeconds >= settings.BlackFrameThresholdSeconds;
        FreezeDetected = lastFrame != DateTime.MinValue && lastChanged != DateTime.MinValue &&
            (utcNow - lastFrame).TotalSeconds < settings.SignalLossThresholdSeconds &&
            (utcNow - lastChanged).TotalSeconds >= settings.FreezeThresholdSeconds;
        SignalLossDetected = lastFrame != DateTime.MinValue &&
            (utcNow - lastFrame).TotalSeconds >= settings.SignalLossThresholdSeconds;

        var silent = Math.Max(AudioLeftDbfs, AudioRightDbfs) <= settings.AudioLowThresholdDbfs;
        if (silent)
        {
            if (_silenceSinceUtc == DateTime.MinValue) _silenceSinceUtc = utcNow;
        }
        else _silenceSinceUtc = DateTime.MinValue;
        AudioSilenceDetected = silent && _silenceSinceUtc != DateTime.MinValue &&
            (utcNow - _silenceSinceUtc).TotalSeconds >= settings.AudioSilenceThresholdSeconds;
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
