using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Data;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using BroadcastPlayout.Engine;
using BroadcastPlayout.Ffmpeg;
using BroadcastPlayout.Models;
using BroadcastPlayout.Services;
using BroadcastPlayout.ViewModels;

namespace BroadcastPlayout.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private readonly DispatcherTimer _clockTimer;
    private WriteableBitmap? _programBitmap;
    private WriteableBitmap? _previewBitmap;
    // Keep only the newest confidence-monitor frame. Queuing every 25/50/60 fps frame
    // onto the WPF dispatcher can build an unbounded backlog and make Program appear
    // frozen after several minutes even while the playout engine is still running.
    // Mailboxes copy on receipt: the Program cadence and decoders reuse their buffers.
    private readonly FrameMailbox _pendingProgramFrame = new();
    private readonly FrameMailbox _pendingPreviewFrame = new();
    private int _programPresentScheduled;
    private int _previewPresentScheduled;
    private long _lastProgramConfidenceStamp;
    private long _lastPreviewConfidenceStamp;
    private const int ProgramConfidenceFpsCeiling = 60;
    private const int PreviewConfidenceFps = 50;
    private FullscreenWindow? _fullscreen;
    private FullscreenWindow? _programMonitor;
    private readonly AutomationGatewayService _automationGateway = new();
    private AutomationGatewayWindow? _automationGatewayWindow;
    private ConfidenceMonitorWindow? _previewOutputWindow;
    private ConfidenceMonitorWindow? _programOutputWindow;
    private UserManagementWindow? _userManagementWindow;
    private DateTime _lastCpuWall = DateTime.UtcNow;
    private TimeSpan _lastCpuTime = Process.GetCurrentProcess().TotalProcessorTime;
    private readonly Forms.NotifyIcon _trayIcon;
    private Drawing.Icon? _trayDrawingIcon;
    private double _savedRightSidebarWidth = 310;
    private bool _allowExit;
    private bool _trayHintShown;
    private int _closed;
    private Point _playlistDragStart;
    private bool _previewSeekDragging;
    private RundownBlockNode? _activeRundownBlock;
    private string _playlistSearchQuery = string.Empty;
    public ObservableCollection<MediaLibraryItem> EmbeddedMedia { get; } = [];
    public ObservableCollection<InputSourceMonitorTile> InputMonitorTiles { get; } = [];
    private readonly Dictionary<Guid, CancellationTokenSource> _inputMonitorCancellations = [];
    private int _mediaScanVersion;
    public ObservableCollection<string> MediaRoots { get; } = [];
    public MediaLibraryItem? SelectedEmbeddedMedia { get; set; }

    public MainWindow()
    {
        InitializeComponent();
        WindowChromeActions.ApplyCleanBorder(this);
        DataContext = _vm;
        _automationGateway.CommandReceived += command => SafeBeginInvoke(() => _vm.ExecuteExternalCommand(command));
        _automationGateway.MosMessageReceived += message => SafeBeginInvoke(() => _vm.ApplyMosItem(message.Action, message.ItemId, message.Title, message.Duration));
        _vm.AutomationSignalRaised += Vm_AutomationSignalRaised;
        if (_automationGateway.Profile.AutoStart)
        {
            try { _automationGateway.Start(_automationGateway.Profile); }
            catch { /* Port conflicts must never prevent Playout from starting. Open Automation / MOS Gateway to diagnose. */ }
        }
        _vm.InputSources.CollectionChanged += InputSources_CollectionChanged;
        RebuildInputMonitorTiles();
        var savedMediaFolders = SettingsStore.Load().MediaFolders
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var folder in savedMediaFolders) MediaRoots.Add(folder);
        foreach (var drive in DriveInfo.GetDrives().Where(x => x.IsReady))
        {
            var root = drive.RootDirectory.FullName;
            if (!MediaRoots.Any(x => string.Equals(x, root, StringComparison.OrdinalIgnoreCase))) MediaRoots.Add(root);
        }
        // Confidence monitors must represent the real buses.  Seeding them with the old
        // red "LIVE PROGRAM" demo poster made PREVIEW look on-air while the channel was
        // actually in standby and also hid genuine no-signal/clear states.
        PreviewImage.Source = null;
        ProgramImage.Source = null;
        Loaded += async (_, _) =>
        {
            // Operator-friendly media browser: reopen the last registered media folder when
            // available; otherwise select the Windows system drive so files are visible on a
            // fresh workstation without first opening a folder picker. SelectionChanged runs
            // the bounded/background scan and inaccessible folders are ignored safely.
            var startupRoot = savedMediaFolders.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(startupRoot))
            {
                var systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
                startupRoot = MediaRoots.FirstOrDefault(x => string.Equals(x, systemRoot, StringComparison.OrdinalIgnoreCase))
                              ?? MediaRoots.FirstOrDefault();
            }
            if (!string.IsNullOrWhiteSpace(startupRoot) && Directory.Exists(startupRoot))
            {
                MediaRootCombo.SelectedItem = startupRoot;
                await Task.CompletedTask;
            }
            FitConfidenceSurface(PreviewAspectHost, PreviewAspectSurface, PreviewMonitorPanel);
            FitConfidenceSurface(ProgramAspectHost, ProgramAspectSurface, ProgramMonitorPanel);
            SetMediaDockFocus(MediaDockExpander.IsExpanded);
        };

        _vm.VideoFrameReady += PresentProgram;
        _vm.PreviewFrameReady += PresentPreview;
        _vm.PreviewCleared += ClearPreview;
        _vm.FullscreenStateRequested += ToggleFullscreen;

        _trayIcon = CreateTrayIcon();

        _clockTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _clockTimer.Tick += ClockTimer_Tick;
        _clockTimer.Start();
        UpdateClock();
        UpdateSystemTelemetry();
        Window_StateChanged(this, EventArgs.Empty);

        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        LocalClockText.Text = now.ToString("HH:mm:ss");
        LocalDateText.Text = now.ToString("dd MMM yyyy · dddd").ToUpperInvariant();
    }

    private void UpdateSystemTelemetry()
    {
        if (Volatile.Read(ref _closed) != 0) return;

        try
        {
            using var process = Process.GetCurrentProcess();
            var now = DateTime.UtcNow;
            var cpuTime = process.TotalProcessorTime;
            var wallMs = Math.Max(1.0, (now - _lastCpuWall).TotalMilliseconds);
            var cpuMs = Math.Max(0.0, (cpuTime - _lastCpuTime).TotalMilliseconds);
            var cpu = Math.Clamp(cpuMs / wallMs / Math.Max(1, Environment.ProcessorCount) * 100.0, 0.0, 100.0);
            _lastCpuWall = now;
            _lastCpuTime = cpuTime;

            var ramMb = process.WorkingSet64 / 1024.0 / 1024.0;
            CpuValueText.Text = $"{cpu:0}%";
            CpuBar.Value = cpu;
            RamValueText.Text = $"{ramMb:0} MB";
            RamBar.Maximum = Math.Max(1024.0, Math.Ceiling(ramMb / 1024.0 + 1.0) * 1024.0);
            RamBar.Value = ramMb;

            // GPU utilization requires vendor/performance-counter APIs and is intentionally
            // not faked. Keep the reference slot visible with an explicit unavailable state.
            GpuValueText.Text = "—";
            GpuBar.Value = 0;

            var drives = DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.TotalSize > 0)
                .OrderByDescending(d => string.Equals(
                    Path.GetPathRoot(Environment.SystemDirectory),
                    d.RootDirectory.FullName,
                    StringComparison.OrdinalIgnoreCase))
                .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToArray();

            if (drives.Length > 0)
            {
                var used = UsedPercent(drives[0]);
                DiskValueText.Text = $"{used:0}%";
                DiskBar.Value = used;
            }
            else
            {
                DiskValueText.Text = "—";
                DiskBar.Value = 0;
            }

            UpdateDiskSlot(0, drives.ElementAtOrDefault(0), Disk1Label, Disk1Value, Disk1Bar);
            UpdateDiskSlot(1, drives.ElementAtOrDefault(1), Disk2Label, Disk2Value, Disk2Bar);
            UpdateDiskSlot(2, drives.ElementAtOrDefault(2), Disk3Label, Disk3Value, Disk3Bar);
            if (FindName("BottomUserText") is TextBlock userBlock) userBlock.Text = $"  {Environment.UserName}";
            if (FindName("BottomOutputStatusText") is TextBlock outputBlock) outputBlock.Text = string.IsNullOrWhiteSpace(_vm.PlayoutPreset) ? "NDI · 1080p50" : $"NDI / {_vm.PlayoutPreset}";
        }
        catch
        {
            CpuValueText.Text = "--%";
            RamValueText.Text = "-- MB";
            GpuValueText.Text = "—";
            DiskValueText.Text = "—";
            if (FindName("BottomUserText") is TextBlock userBlock) userBlock.Text = $"  {Environment.UserName}";
            if (FindName("BottomOutputStatusText") is TextBlock outputBlock) outputBlock.Text = "NDI · ONLINE";
        }
    }

    private static double UsedPercent(DriveInfo drive) =>
        drive.TotalSize <= 0 ? 0 : Math.Clamp((drive.TotalSize - drive.AvailableFreeSpace) * 100.0 / drive.TotalSize, 0, 100);

    private static string FormatBytes(long bytes)
    {
        const double gb = 1024d * 1024d * 1024d;
        const double tb = gb * 1024d;
        return bytes >= tb ? $"{bytes / tb:0.0} TB" : $"{bytes / gb:0.0} GB";
    }

    private static void UpdateDiskSlot(
        int index,
        DriveInfo? drive,
        TextBlock label,
        TextBlock value,
        System.Windows.Controls.ProgressBar bar)
    {
        if (drive is null)
        {
            label.Text = index == 0 ? "No ready disk" : "—";
            value.Text = "";
            bar.Value = 0;
            bar.Opacity = 0.25;
            return;
        }

        var used = UsedPercent(drive);
        label.Text = $"{drive.Name.TrimEnd('\\')} · {drive.DriveType}";
        value.Text = $"{used:0}% used · {FormatBytes(drive.AvailableFreeSpace)} free";
        bar.Value = used;
        bar.Opacity = 1;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => WindowChromeActions.Drag(this, e);

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (MaximizeGlyph is null) return;
        MaximizeGlyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        if (MaximizeButton is not null) MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowChromeActions.Minimize(this);

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        WindowChromeActions.ToggleMaximize(this);
        Window_StateChanged(this, EventArgs.Empty);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private Forms.NotifyIcon CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip
        {
            BackColor = Drawing.Color.FromArgb(11, 17, 24),
            ForeColor = Drawing.Color.White,
            Renderer = new Forms.ToolStripProfessionalRenderer(new DarkMenuColorTable()),
            ShowImageMargin = false
        };
        menu.Items.Add("Open Kashtrix Playout", null, (_, _) => SafeBeginInvoke(ShowFromTray));
        menu.Items.Add("Open Preview Monitor", null, (_, _) => SafeBeginInvoke(OpenPreviewOutputWindow));
        menu.Items.Add("Open Program Monitor", null, (_, _) => SafeBeginInvoke(OpenProgramOutputWindow));
        if (AppSession.IsAdmin)
            menu.Items.Add("Users & Roles (Admin)", null, (_, _) => SafeBeginInvoke(OpenUserManagementFromTray));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Stop Playout", null, (_, _) => SafeBeginInvoke(() => _vm.StopCommand.Execute(null)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit Engine", null, (_, _) => SafeBeginInvoke(ExitEngine));
        try
        {
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Kashtrix.Playout;component/Branding/kashtrix-playout.ico", UriKind.Absolute));
            if (resource?.Stream is not null)
            {
                using var loaded = new Drawing.Icon(resource.Stream);
                _trayDrawingIcon = (Drawing.Icon)loaded.Clone();
            }
        }
        catch { _trayDrawingIcon = null; }

        var icon = new Forms.NotifyIcon
        {
            Text = "Kashtrix Playout Automation",
            Icon = _trayDrawingIcon ?? Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true
        };
        icon.DoubleClick += (_, _) => SafeBeginInvoke(ShowFromTray);
        return icon;
    }

    private void OpenPreviewOutputWindow()
    {
        if (_previewOutputWindow is { IsVisible: true }) { _previewOutputWindow.Activate(); return; }
        var window=new ConfidenceMonitorWindow("Kashtrix Preview",()=>PreviewImage.Source);
        window.Closed+=(_,_)=>{if(ReferenceEquals(_previewOutputWindow,window))_previewOutputWindow=null;};
        _previewOutputWindow=window; window.Show();
    }

    private void OpenProgramOutputWindow()
    {
        if (_programOutputWindow is { IsVisible: true }) { _programOutputWindow.Activate(); return; }
        var window=new ConfidenceMonitorWindow("Kashtrix Program",()=>ProgramImage.Source);
        window.Closed+=(_,_)=>{if(ReferenceEquals(_programOutputWindow,window))_programOutputWindow=null;};
        _programOutputWindow=window; window.Show();
    }

    private void OpenUserManagementFromTray()
    {
        if (!AppSession.IsAdmin) { MessageBox.Show("Administrator role required.","Kashtrix Security",MessageBoxButton.OK,MessageBoxImage.Warning); return; }
        if (_userManagementWindow is { IsVisible: true }) { _userManagementWindow.Activate(); return; }
        var window=new UserManagementWindow();
        window.Closed+=(_,_)=>{if(ReferenceEquals(_userManagementWindow,window))_userManagementWindow=null;};
        _userManagementWindow=window; window.Show();
    }

    private void Vm_AutomationSignalRaised(string action, PlaylistItem? item)
    {
        if (item is null) return;
        if (action.Equals("MOS", StringComparison.OrdinalIgnoreCase))
        {
            if (item.MosAction.Equals("CG_PLAY", StringComparison.OrdinalIgnoreCase))
            {
                if (!_vm.PlayCgTemplateFromAutomation(item.MosTitle, item.MosDataJson, item.MosLayer))
                    SafeBeginInvoke(() => MessageBox.Show($"CG template not found: {item.MosTitle}", "MOS / NRCS Graphic", MessageBoxButton.OK, MessageBoxImage.Warning));
            }
            return;
        }

        if (!action.Equals("TCP", StringComparison.OrdinalIgnoreCase)) return;
        var host = item.TcpHost; var port = item.TcpPort; var command = item.TcpCommand;
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(command)) return;

        _ = Task.Run(async () =>
        {
            try { await _automationGateway.SendTcpAsync(host, Math.Clamp(port, 1, 65535), command); }
            catch (Exception ex) { SafeBeginInvoke(() => MessageBox.Show(ex.Message, "TCP Command", MessageBoxButton.OK, MessageBoxImage.Warning)); }
        });
    }

    private void AutomationGateway_Click(object sender, RoutedEventArgs e)
    {
        if (_automationGatewayWindow is { IsVisible: true }) { _automationGatewayWindow.Activate(); return; }
        var window = new AutomationGatewayWindow(_vm, _automationGateway) { Owner = this };
        window.Closed += (_, _) => { if (ReferenceEquals(_automationGatewayWindow, window)) _automationGatewayWindow = null; };
        _automationGatewayWindow = window;
        window.Show();
    }

    private void QuickCgShow_Click(object sender, RoutedEventArgs e) => _vm.ShowActiveCg();
    private void QuickCgHide_Click(object sender, RoutedEventArgs e) => _vm.HideActiveCg();
    private void QuickLogoOn_Click(object sender, RoutedEventArgs e) => _vm.SetLogoVisible(true);
    private void QuickLogoOff_Click(object sender, RoutedEventArgs e) => _vm.SetLogoVisible(false);
    private void AddScte35Event_Click(object sender, RoutedEventArgs e) => AddTypedControlEvent("SCTE35");
    private void AddScte104Event_Click(object sender, RoutedEventArgs e) => AddTypedControlEvent("SCTE104");
    private void AddCaption608Event_Click(object sender, RoutedEventArgs e) => AddTypedControlEvent("CAPTION608");
    private void AddCaption708Event_Click(object sender, RoutedEventArgs e) => AddTypedControlEvent("CAPTION708");
    private void AddDvbSubtitleEvent_Click(object sender, RoutedEventArgs e) => AddTypedControlEvent("DVBSUB");
    private void AddGpoEvent_Click(object sender, RoutedEventArgs e) => AddTypedControlEvent("GPO");
    private void AddVdcpEvent_Click(object sender, RoutedEventArgs e) => AddTypedControlEvent("VDCP");
    private void AddRs422Event_Click(object sender, RoutedEventArgs e) => AddTypedControlEvent("RS422");
    private void AddSnmpEvent_Click(object sender, RoutedEventArgs e) => AddTypedControlEvent("SNMP");
    private void AddCueToneEvent_Click(object sender, RoutedEventArgs e) => _vm.AddControlEvent("CUETONE", notes: "1 kHz line-up / cue tone · 850 ms");
    private void AddDialToneEvent_Click(object sender, RoutedEventArgs e) => AddTypedControlEvent("DIALTONE");
    private void AddDtmfEvent_Click(object sender, RoutedEventArgs e) => AddTypedControlEvent("DTMF");
    private void AddTcpEvent_Click(object sender, RoutedEventArgs e) => AddTypedControlEvent("TCP");
    private void AddMosEvent_Click(object sender, RoutedEventArgs e) => AddTypedControlEvent("MOS");
    private void AddTypedControlEvent(string eventType)
    {
        _vm.AddControlEvent(eventType);
        if (_vm.SelectedItem is not { } item || !item.EventType.Equals(eventType, StringComparison.OrdinalIgnoreCase)) return;
        var dialog = new PlaylistItemPropertiesWindow(item) { Owner = this };
        if (dialog.ShowDialog() == true) { item.RefreshLegacyControlNotes(); }
    }
    private void AddGpoPulseEvent_Click(object sender, RoutedEventArgs e) => AddTypedControlEvent("GPO");
    private void AddLoopEvent_Click(object sender, RoutedEventArgs e) => _vm.AddControlEvent("LOOP", notes: "Loop marker");

    private void RundownBlockList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _activeRundownBlock = RundownBlockList.SelectedItem as RundownBlockNode;
        if (_activeRundownBlock is not null) _vm.SelectRundownBlock(_activeRundownBlock);
        ApplyPlaylistViewFilter();
    }

    private void ClearRundownBlockFilter_Click(object sender, RoutedEventArgs e)
    {
        _activeRundownBlock = null;
        if (RundownBlockList is not null) RundownBlockList.SelectedItem = null;
        ApplyPlaylistViewFilter();
    }

    private bool _playlistFilterRetryPending;

    private void ApplyPlaylistViewFilter()
    {
        var view = CollectionViewSource.GetDefaultView(_vm.Playlist);
        // WPF forbids changing ICollectionView.Filter while an AddNew/EditItem transaction
        // is active. The rundown itself is edited through dedicated Kashtrix dialogs, so the
        // grid is read-only; still commit/cancel any stale transaction left by layout restore
        // or a previous build before changing the filter. This prevents the startup crash:
        // "'Filter' is not allowed during an AddNew or EditItem transaction."
        if (view is IEditableCollectionView editable)
        {
            try
            {
                if (editable.IsAddingNew) editable.CommitNew();
                if (editable.IsEditingItem) editable.CommitEdit();
            }
            catch
            {
                try { if (editable.IsAddingNew) editable.CancelNew(); } catch { }
                try { if (editable.IsEditingItem) editable.CancelEdit(); } catch { }
            }
        }

        var block = _activeRundownBlock;
        var query = _playlistSearchQuery;
        Predicate<object> filter = item =>
        {
            if (item is not PlaylistItem clip) return false;
            if (block is not null && !block.Items.Contains(clip)) return false;
            // A loop block must stay intact because its completed rows are about to play
            // again. Normal completed rows can be hidden by the operator setting.
            if (_vm.HidePlayedItems && clip.Status == "Completed" && !clip.IsLoop && !clip.IsBlockLoop) return false;
            if (string.IsNullOrWhiteSpace(query)) return true;
            return clip.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                   clip.EventType.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                   clip.Status.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                   clip.SourceKind.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                   clip.Category.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                   clip.BlockName.Contains(query, StringComparison.OrdinalIgnoreCase);
        };

        try
        {
            view.Filter = filter;
            view.Refresh();
            _playlistFilterRetryPending = false;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("AddNew", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("EditItem", StringComparison.OrdinalIgnoreCase))
        {
            if (_playlistFilterRetryPending) return;
            _playlistFilterRetryPending = true;
            Dispatcher.BeginInvoke(() =>
            {
                _playlistFilterRetryPending = false;
                ApplyPlaylistViewFilter();
            }, DispatcherPriority.ContextIdle);
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowExit || !_vm.KeepEngineRunningInTray) return;
        e.Cancel = true;
        Hide();
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _trayIcon.ShowBalloonTip(2500, "Kashtrix Playout", "Operator window hidden. Playout engine continues in the notification area.", Forms.ToolTipIcon.Info);
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _pendingProgramFrame.Clear();
        _pendingPreviewFrame.Clear();

        _clockTimer.Stop();
        _clockTimer.Tick -= ClockTimer_Tick;

        _vm.VideoFrameReady -= PresentProgram;
        _vm.PreviewFrameReady -= PresentPreview;
        _vm.PreviewCleared -= ClearPreview;
        _vm.FullscreenStateRequested -= ToggleFullscreen;
        _vm.AutomationSignalRaised -= Vm_AutomationSignalRaised;
        _vm.InputSources.CollectionChanged -= InputSources_CollectionChanged;
        try { _automationGateway.Dispose(); } catch { }
        StopAllInputMonitors();

        var fullscreen = _fullscreen;
        _fullscreen = null;
        try { fullscreen?.Close(); } catch { }
        var programMonitor = _programMonitor;
        _programMonitor = null;
        try { programMonitor?.Close(); } catch { }
        var previewOutput = _previewOutputWindow; _previewOutputWindow = null; try { previewOutput?.Close(); } catch { }
        var programOutput = _programOutputWindow; _programOutputWindow = null; try { programOutput?.Close(); } catch { }
        var users = _userManagementWindow; _userManagementWindow = null; try { users?.Close(); } catch { }

        try { _trayIcon.Visible = false; } catch { }
        try { _trayIcon.Dispose(); } catch { }
        try { _trayDrawingIcon?.Dispose(); } catch { }
        _trayDrawingIcon = null;
        try { _vm.Dispose(); } catch { }
    }

    private void ClockTimer_Tick(object? sender, EventArgs e)
    {
        UpdateClock();
        UpdateSystemTelemetry();
    }

    private void SafeBeginInvoke(Action action)
    {
        if (Volatile.Read(ref _closed) != 0 || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (Volatile.Read(ref _closed) == 0 && !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                    action();
            }));
        }
        catch (InvalidOperationException) { }
        catch (TaskCanceledException) { }
    }

    private void ShowFromTray()
    {
        if (Volatile.Read(ref _closed) != 0) return;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void ExitEngine()
    {
        _allowExit = true;
        Close();
    }

    private void PresentProgram(VideoFrameData frame)
    {
        if (Volatile.Read(ref _closed) != 0) return;
        // Confidence monitors are operator UI, not the on-air output. Uploading a 1080p/2160p
        // BGRA bitmap to WPF at 50/60 fps can consume a full CPU core and memory bandwidth.
        // Keep the broadcast output at source rate while presenting the UI at a bounded rate.
        var sourceFps = frame.FramesPerSecond > 0 ? frame.FramesPerSecond : 50.0;
        var presentationFps = Math.Clamp((int)Math.Round(sourceFps), 24, ProgramConfidenceFpsCeiling);
        if (!ShouldQueueConfidenceFrame(ref _lastProgramConfidenceStamp, presentationFps)) return;
        _pendingProgramFrame.Post(frame);
        if (Interlocked.CompareExchange(ref _programPresentScheduled, 1, 0) == 0)
            SafeBeginInvoke(DrainProgramFrame);
    }

    private void DrainProgramFrame()
    {
        try
        {
            if (Volatile.Read(ref _closed) != 0) return;
            var frame = _pendingProgramFrame.Take();
            if (frame is null) return;
            _programBitmap = PresentInto(ProgramImage, _programBitmap, frame);
            _fullscreen?.Present(frame, _vm.DisplayProfile);
            _programMonitor?.Present(frame, _vm.DisplayProfile);
        }
        finally
        {
            Interlocked.Exchange(ref _programPresentScheduled, 0);
            // A newer frame may have arrived while this one was painted. Schedule one
            // more dispatcher turn, never one dispatcher callback per decoded frame.
            if (Volatile.Read(ref _closed) == 0 &&
                _pendingProgramFrame.HasPending &&
                Interlocked.CompareExchange(ref _programPresentScheduled, 1, 0) == 0)
                SafeBeginInvoke(DrainProgramFrame);
        }
    }

    private void PresentPreview(VideoFrameData frame)
    {
        if (Volatile.Read(ref _closed) != 0) return;
        _pendingPreviewFrame.Post(frame);
        if (Interlocked.CompareExchange(ref _previewPresentScheduled, 1, 0) == 0)
            SafeBeginInvoke(DrainPreviewFrame);
    }

    private static bool ShouldQueueConfidenceFrame(ref long lastStamp, int fps)
    {
        var now = Stopwatch.GetTimestamp();
        // Allow a 15% phase tolerance (0.85) so standard thread scheduling variance at 50/60fps doesn't skip frames
        var interval = Math.Max(1L, (long)(Stopwatch.Frequency / Math.Max(1.0, fps) * 0.85));
        while (true)
        {
            var previous = Volatile.Read(ref lastStamp);
            if (previous != 0 && now - previous < interval) return false;
            if (Interlocked.CompareExchange(ref lastStamp, now, previous) == previous) return true;
        }
    }

    private void DrainPreviewFrame()
    {
        try
        {
            if (Volatile.Read(ref _closed) != 0) return;
            var frame = _pendingPreviewFrame.Take();
            if (frame is null) return;
            _previewBitmap = PresentInto(PreviewImage, _previewBitmap, frame);
        }
        finally
        {
            Interlocked.Exchange(ref _previewPresentScheduled, 0);
            if (Volatile.Read(ref _closed) == 0 &&
                _pendingPreviewFrame.HasPending &&
                Interlocked.CompareExchange(ref _previewPresentScheduled, 1, 0) == 0)
                SafeBeginInvoke(DrainPreviewFrame);
        }
    }

    private void ClearPreview()
    {
        _pendingPreviewFrame.Clear();
        Interlocked.Exchange(ref _lastPreviewConfidenceStamp, 0);
        if (Volatile.Read(ref _closed) != 0) return;
        if (!Dispatcher.CheckAccess())
        {
            SafeBeginInvoke(ClearPreview);
            return;
        }

        if (Volatile.Read(ref _closed) != 0) return;
        _previewBitmap = null;
        PreviewImage.Source = null;
    }

    private static WriteableBitmap PresentInto(Image target, WriteableBitmap? bitmap, VideoFrameData frame)
    {
        if (bitmap is null || bitmap.PixelWidth != frame.Width || bitmap.PixelHeight != frame.Height)
        {
            bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
            target.Source = bitmap;
        }

        bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Bgra, frame.Stride, 0);
        return bitmap;
    }

    private void ToggleFullscreen(bool enabled)
    {
        if (Volatile.Read(ref _closed) != 0) return;
        if (!Dispatcher.CheckAccess())
        {
            SafeBeginInvoke(() => ToggleFullscreen(enabled));
            return;
        }

        if (Volatile.Read(ref _closed) != 0) return;
        if (enabled) OpenFullscreen();
        else CloseFullscreen();
    }

    private void OpenFullscreen()
    {
        if (_fullscreen is { IsVisible: true })
        {
            _fullscreen.SetScaling(_vm.DisplayProfile.Scaling);
            _fullscreen.Activate();
            return;
        }

        _fullscreen = new FullscreenWindow();
        _fullscreen.SetScaling(_vm.DisplayProfile.Scaling);
        _fullscreen.Closed += (_, _) =>
        {
            _fullscreen = null;
            if (_vm.FullscreenEnabled) _vm.FullscreenEnabled = false;
        };
        _fullscreen.ShowOnMonitor(_vm.FullscreenMonitorIndex);
    }

    private void CloseFullscreen()
    {
        var window = _fullscreen;
        _fullscreen = null;
        if (window is not null) window.Close();
    }

    private void LoadDemo_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            "Load Kashtrix demo playlist, schedules and graphics? This replaces the current local workspace data.",
            "Kashtrix Demo Data", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        _vm.LoadDemoWorkspace();
    }

    private void ProgramLauncher_Click(object sender, RoutedEventArgs e)
    {
        var launcher = new ProgramLauncherWindow { Owner = this };
        launcher.Show();
    }

    private void EntertainmentManager_Click(object sender, RoutedEventArgs e)
    {
        var manager = new BroadcastPlayout.Entertainment.EntertainmentManagerWindow { Owner = this };
        manager.Show();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        LaunchStandalone("Settings", "Kashtrix Settings");
    }

    private void AudioMixer_Click(object sender, RoutedEventArgs e)
    {
        var mixer = new AudioMixerWindow(_vm) { Owner = this };
        mixer.Show();
    }

    private void VideoProcessor_Click(object sender, RoutedEventArgs e)
    {
        var processor = new VideoProcessorWindow(_vm) { Owner = this };
        processor.Show();
    }

    private void EpgSettings_Click(object sender, RoutedEventArgs e)
    {
        var window = new EpgOutputSettingsWindow(_vm) { Owner = this };
        window.ShowDialog();
    }

    private void AuditLog_Click(object sender, RoutedEventArgs e)
    {
        var window = new AuditLogWindow { Owner = this };
        window.Show();
    }

    private void ScteTriggerManager_Click(object sender, RoutedEventArgs e)
    {
        var win = new ScteTriggerManagerWindow(_vm) { Owner = this };
        win.ShowDialog();
    }

    private void FrameComparison_Click(object sender, RoutedEventArgs e)
    {
        var win = new FrameComparisonWindow(_vm) { Owner = this };
        win.ShowDialog();
    }

    private void NowNextFields_Click(object sender, RoutedEventArgs e)
    {
        var win = new BroadcastDynamicFieldsDialog(_vm, initialTab: 0) { Owner = this };
        win.ShowDialog();
    }

    private void MusicPackage_Click(object sender, RoutedEventArgs e)
    {
        var win = new MusicPackageWindow(_vm) { Owner = this };
        win.ShowDialog();
    }

    private void WeatherFields_Click(object sender, RoutedEventArgs e)
    {
        var win = new BroadcastDynamicFieldsDialog(_vm, initialTab: 1) { Owner = this };
        win.ShowDialog();
    }

    private void ProgramMonitor_Click(object sender, RoutedEventArgs e)
    {
        if (_programMonitor is { IsVisible: true })
        {
            _programMonitor.Activate();
            return;
        }
        var monitor = new FullscreenWindow();
        monitor.SetScaling(_vm.DisplayProfile.Scaling);
        monitor.Closed += (_, _) => { if (ReferenceEquals(_programMonitor, monitor)) _programMonitor = null; };
        _programMonitor = monitor;
        monitor.ShowWindowed(this);
    }

    private void StartBlock_Click(object sender, RoutedEventArgs e)
    {
        _vm.StartBlockAtSelected();
        // The block collection is rebuilt after a new boundary. Drop any stale node
        // reference so the right-hand playlist immediately shows the new full structure.
        _activeRundownBlock = null;
        if (RundownBlockList is not null) RundownBlockList.SelectedItem = null;
        ApplyPlaylistViewFilter();
    }
    private void ClearBlock_Click(object sender, RoutedEventArgs e) => _vm.ClearBlockMarkerAtSelected();

    private void ExportEpg_Click(object sender, RoutedEventArgs e) => _vm.ExportEpgXmlTv();
    private void ExportEpgJson_Click(object sender, RoutedEventArgs e) => _vm.ExportEpgJson();

    private void ProgramSeekSlider_MouseDown(object sender, MouseButtonEventArgs e) => _vm.BeginProgramSeek();
    private void ProgramSeekSlider_MouseUp(object sender, MouseButtonEventArgs e) => _vm.CommitProgramSeek();

    private void PreviewSeekSlider_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _previewSeekDragging = true;
        _vm.StopPreviewPlayback();
    }

    private void PreviewSeekSlider_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_previewSeekDragging) return;
        _previewSeekDragging = false;
        _vm.CueSelected();
    }

    private void PreviewStart_Click(object sender, RoutedEventArgs e) => _vm.PreviewSeekSeconds = 0;
    private void PreviewBack_Click(object sender, RoutedEventArgs e) => _vm.PreviewSeekSeconds = Math.Max(0, _vm.PreviewSeekSeconds - 5);
    private void PreviewForward_Click(object sender, RoutedEventArgs e) => _vm.PreviewSeekSeconds = Math.Min(_vm.PreviewSeekMaximum, _vm.PreviewSeekSeconds + 5);
    private void PreviewEnd_Click(object sender, RoutedEventArgs e) => _vm.PreviewSeekSeconds = _vm.PreviewSeekMaximum;

    private void ProgramStart_Click(object sender, RoutedEventArgs e) => _vm.GoToProgramIn();

    private void ProgramBack_Click(object sender, RoutedEventArgs e)
    {
        _vm.ProgramSeekSeconds = Math.Max(0, _vm.ProgramSeekSeconds - 5);
        _vm.CommitProgramSeek();
    }

    private void ProgramForward_Click(object sender, RoutedEventArgs e)
    {
        _vm.ProgramSeekSeconds = Math.Min(_vm.ProgramSeekMaximum, _vm.ProgramSeekSeconds + 5);
        _vm.CommitProgramSeek();
    }

    private void ProgramEnd_Click(object sender, RoutedEventArgs e) => _vm.GoToProgramOut();

    private void PlaylistSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox box) return;
        _playlistSearchQuery = box.Text.Trim();
        ApplyPlaylistViewFilter();
    }

    private void SeekRelative_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element ||
            !double.TryParse(element.Tag?.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var delta))
            return;

        _vm.ProgramSeekSeconds = Math.Clamp(_vm.ProgramSeekSeconds + delta, 0, _vm.ProgramSeekMaximum);
        _vm.CommitProgramSeek();
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string preset || string.IsNullOrWhiteSpace(preset)) return;
        _vm.PlayoutPreset = preset;
    }

    private void OpenDiskManager_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "shell:MyComputerFolder") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Disk Manager", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void AddInput_Click(object sender, RoutedEventArgs e) => ShowInputSource(insertAfterSelected: false);
    private void InsertInputAfterSelected_Click(object sender, RoutedEventArgs e) => ShowInputSource(insertAfterSelected: true);

    private void ShowInputSource(bool insertAfterSelected)
    {
        var window = new InputSourceWindow { Owner = this };
        if (window.ShowDialog() != true || window.ResultItem is null) return;
        if (insertAfterSelected) _vm.InsertInputSourceAfterSelected(window.ResultItem);
        else _vm.AddInputSource(window.ResultItem);
    }

    private void Trim_Click(object sender, RoutedEventArgs e)
    {
        var item = _vm.SelectedItem;
        if (item is null)
        {
            MessageBox.Show("Select a playlist media item first.", "Media Trimmer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (item.IsLiveSource)
        {
            MessageBox.Show("Live/card/screen inputs use Event Duration instead of media trimming. Edit the input source duration when adding the source.", "Media Trimmer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (item.IsCgEvent)
        {
            MessageBox.Show("CG rundown events use the CG project duration and cannot be media-trimmed. Edit the graphic in CG Designer or CG Controller.", "Media Trimmer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var window = new MediaTrimmerWindow(item) { Owner = this };
            if (window.ShowDialog() == true) { if (window.SaveAsSeparateItems) _vm.ReplaceWithPartEvents(item); else _vm.NotifySelectedItemEdited(); }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Media Trimmer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void PlaylistGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (ItemsControl.ContainerFromElement(PlaylistGrid, source) is not DataGridRow row) return;
        row.IsSelected = true;
        PlaylistGrid.SelectedItem = row.Item;
        _vm.SelectedItem = row.Item as PlaylistItem;
    }

    private void ImportMedia_Click(object sender, RoutedEventArgs e) => _vm.AddCommand.Execute(null);
    private async void InsertMedia_Click(object sender, RoutedEventArgs e)
    {
        try { await _vm.InsertMediaAfterSelectedAsync(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Insert Media", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void AddTrimmedMedia_Click(object sender, RoutedEventArgs e) => await AddMediaAndOpenTrimAsync(insertAfterSelected: false);
    private async void InsertTrimmedMedia_Click(object sender, RoutedEventArgs e) => await AddMediaAndOpenTrimAsync(insertAfterSelected: true);

    private async Task AddMediaAndOpenTrimAsync(bool insertAfterSelected)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = false,
            Filter = "Media files|*.mp4;*.mov;*.mxf;*.mkv;*.avi;*.ts;*.m2ts;*.mpg;*.mpeg;*.webm;*.wmv|All files|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var item = await _vm.ImportMediaAtAsync(dialog.FileName, insertAfterSelected);
            var window = new MediaTrimmerWindow(item) { Owner = this };
            if (window.ShowDialog() == true)
            {
                if (window.SaveAsSeparateItems) _vm.ReplaceWithPartEvents(item);
                else _vm.NotifySelectedItemEdited();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Add Media + Trim", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void NewPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Playlist.Count > 0)
        {
            var answer = MessageBox.Show(
                "Clear the current playlist and start a new one?",
                "New Playlist", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
        }
        _vm.NewPlaylist();
    }

    private void PlayPauseSelected_Click(object sender, RoutedEventArgs e) => _vm.ToggleSelectedPlayPause();
    private void PreviewPlayPause_Click(object sender, RoutedEventArgs e) => _vm.TogglePreviewPlayback();
    private void PlaySelected_Click(object sender, RoutedEventArgs e) => _vm.TakeSelectedCommand.Execute(null);
    private void CueSelected_Click(object sender, RoutedEventArgs e) => _vm.CueSelected();
    private void NextSelected_Click(object sender, RoutedEventArgs e) => _vm.NextCommand.Execute(null);
    private void StopSelected_Click(object sender, RoutedEventArgs e) => _vm.StopCommand.Execute(null);
    private void RemoveSelected_Click(object sender, RoutedEventArgs e) => _vm.RemoveSelected();
    private void DuplicateSelected_Click(object sender, RoutedEventArgs e) => _vm.DuplicateSelected();
    private void MoveUp_Click(object sender, RoutedEventArgs e) => _vm.MoveSelected(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => _vm.MoveSelected(+1);

    private void SetInAtPreview_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SetSelectedInAtPreview()) return;
        MessageBox.Show(
            _vm.SelectedItem?.HasParts == true
                ? "This event uses multipart ranges. Open MULTIPART TRIM / SEGMENTS to edit the active source ranges."
                : "Select a normal media clip and place the preview playhead before its OUT point.",
            "Set IN", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void SetOutAtPreview_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SetSelectedOutAtPreview()) return;
        MessageBox.Show(
            _vm.SelectedItem?.HasParts == true
                ? "This event uses multipart ranges. Open MULTIPART TRIM / SEGMENTS to edit the active source ranges."
                : "Select a normal media clip and place the preview playhead after its IN point.",
            "Set OUT", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ResetTrim_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.ResetSelectedTrim()) return;
        MessageBox.Show("Select a file-based media event first.", "Reset Trim", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void QcSelected_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = await _vm.RunSelectedQcAsync();
            if (result is null)
            {
                MessageBox.Show("Select a file-based media event first.", "QC Selected Media", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            MessageBox.Show(result.Summary, $"QC · {result.Status}", MessageBoxButton.OK,
                result.Passed ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "QC Selected Media", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenSelectedLocation_Click(object sender, RoutedEventArgs e)
    {
        var path = _vm.SelectedItem?.FilePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            MessageBox.Show("The selected event does not have a local media file.", "Open File Location", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                return;
            }

            var directory = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
                return;
            }

            MessageBox.Show("The selected media path does not exist on this workstation.", "Open File Location", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Open File Location", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ConfidenceAspectHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Keep Preview and Program at a true 16:9 raster without a fixed Viewbox design surface.
        // The host remains fully responsive; only the confidence image surface is fitted inside it.
        if (ReferenceEquals(sender, PreviewAspectHost))
            FitConfidenceSurface(PreviewAspectHost, PreviewAspectSurface, PreviewMonitorPanel);
        else if (ReferenceEquals(sender, ProgramAspectHost))
            FitConfidenceSurface(ProgramAspectHost, ProgramAspectSurface, ProgramMonitorPanel);
    }

    private static void FitConfidenceSurface(FrameworkElement host, FrameworkElement surface, FrameworkElement monitorPanel)
    {
        var availableHeight = host.ActualHeight;
        if (availableHeight <= 0) return;

        const double aspect = 16.0 / 9.0;
        // The monitor column is Auto-sized from the video height.  This prevents the old
        // wide star column from creating black pillar space to the left/right of a true 16:9 raster.
        var targetWidth = Math.Clamp(availableHeight * aspect, 240.0, 620.0);
        if (Math.Abs(monitorPanel.Width - targetWidth) > .5) monitorPanel.Width = targetWidth;

        var availableWidth = Math.Max(1.0, host.ActualWidth);
        var width = availableWidth;
        var height = width / aspect;
        if (height > availableHeight)
        {
            height = availableHeight;
            width = height * aspect;
        }
        surface.Width = Math.Max(1.0, width);
        surface.Height = Math.Max(1.0, height);
    }

    private void TimelineSurface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 40) _vm.TimelineCanvasWidth = Math.Max(320, e.NewSize.Width - 2);
    }

    private void TimelineSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var takeToProgram = e.ClickCount >= 2;
        CueTimelinePosition(e.GetPosition(TimelineSurface).X, takeToProgram);
        if (takeToProgram) e.Handled = true;
    }

    private void CueTimelinePosition(double x, bool takeToProgram)
    {
        var block = _vm.TimelineBlocks.FirstOrDefault(b => x >= b.Left && x <= b.Left + Math.Max(1, b.Width));
        if (block is null || block.PlaylistIndex < 0 || block.PlaylistIndex >= _vm.Playlist.Count) return;
        var item = _vm.Playlist[block.PlaylistIndex];
        _vm.SelectedItem = item;
        var fraction = Math.Clamp((x - block.Left) / Math.Max(1, block.Width), 0, 1);
        _vm.PreviewSeekSeconds = item.IsLiveSource ? 0 : Math.Max(0, item.Duration.TotalSeconds * fraction);
        _vm.CueSelected();
        if (takeToProgram) _vm.TakeSelectedCommand.Execute(null);
    }

    private void AuditionPreview_Click(object sender, RoutedEventArgs e) => _vm.AuditionPreviewAudio();
    private void AddStopEvent_Click(object sender, RoutedEventArgs e) => _vm.AddControlEvent("STOP");
    private void AddPauseEvent_Click(object sender, RoutedEventArgs e) => _vm.AddControlEvent("PAUSE");
    private void AddPlayEvent_Click(object sender, RoutedEventArgs e) => _vm.AddControlEvent("RESUME");
    private void AddNoteEvent_Click(object sender, RoutedEventArgs e) => _vm.AddControlEvent("NOTE");

    private void PlaylistGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _playlistDragStart = e.GetPosition(PlaylistGrid);

    private void PlaylistGrid_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _vm.SelectedItem is null) return;
        var point = e.GetPosition(PlaylistGrid);
        if (Math.Abs(point.X - _playlistDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - _playlistDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        DragDrop.DoDragDrop(PlaylistGrid, new DataObject("Kashtrix.PlaylistItem", _vm.SelectedItem), DragDropEffects.Move);
    }

    private async void PlaylistGrid_Drop(object sender, DragEventArgs e)
    {
        PlaylistItem? target = null;
        if (e.OriginalSource is DependencyObject origin && ItemsControl.ContainerFromElement(PlaylistGrid, origin) is DataGridRow row)
            target = row.Item as PlaylistItem;

        if (e.Data.GetDataPresent("Kashtrix.PlaylistItem") && e.Data.GetData("Kashtrix.PlaylistItem") is PlaylistItem source)
        {
            var index = target is null ? Math.Max(0, _vm.Playlist.Count - 1) : _vm.Playlist.IndexOf(target);
            _vm.MoveItemTo(source, index);
            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent("Kashtrix.InputSource") && e.Data.GetData("Kashtrix.InputSource") is PlaylistItem input)
        {
            if (target is not null) _vm.SelectedItem = target;
            var clone = CloneInputSource(input);
            if (target is not null) _vm.InsertInputSourceAfterSelected(clone); else _vm.AddInputSource(clone);
            e.Handled = true;
            return;
        }

        string[] files = [];
        if (e.Data.GetDataPresent(DataFormats.FileDrop)) files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        else if (e.Data.GetDataPresent("Kashtrix.MediaFile") && e.Data.GetData("Kashtrix.MediaFile") is string one) files = [one];
        if (files.Length == 0) return;
        if (target is not null) _vm.SelectedItem = target;
        foreach (var file in files.Where(File.Exists))
        {
            var imported = await _vm.ImportMediaAtAsync(file, target is not null);
            imported.Category = MediaCategoryStore.Get(file);
        }
        e.Handled = true;
    }

    private void PlaylistGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(PlaylistGrid, source) is DataGridRow row)
        {
            _vm.SelectedItem = row.Item as PlaylistItem;
            Properties_Click(sender, e);
            e.Handled = true;
        }
        else
        {
            // Operator double-clicked blank area in playlist item list -> open Import Media to append at end
            _vm.AddCommand?.Execute(null);
            e.Handled = true;
        }
    }

    private void Properties_Click(object sender, RoutedEventArgs e)
    {
        var item = _vm.SelectedItem;
        if (item is null) return;
        var dialog = new PlaylistItemPropertiesWindow(item) { Owner = this };
        if (dialog.ShowDialog() == true) _vm.NotifySelectedItemEdited();
    }

    private async void EmbeddedMediaScan_Click(object sender, RoutedEventArgs e)
    {
        if (MediaRootCombo.SelectedItem is string selected && Directory.Exists(selected))
        {
            await ScanEmbeddedMediaAsync(selected);
            return;
        }
        await ChooseAndScanEmbeddedFolderAsync();
    }

    private async void EmbeddedMediaUp_Click(object sender, RoutedEventArgs e)
    {
        if (MediaRootCombo.SelectedItem is not string current || string.IsNullOrWhiteSpace(current)) return;
        try
        {
            var full = Path.GetFullPath(current);
            var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parent = Directory.GetParent(trimmed)?.FullName;
            if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent)) return;
            if (!MediaRoots.Any(x => string.Equals(x, parent, StringComparison.OrdinalIgnoreCase))) MediaRoots.Add(parent);
            if (MediaRootCombo.SelectedItem is string selected && string.Equals(selected, parent, StringComparison.OrdinalIgnoreCase))
                await ScanEmbeddedMediaAsync(parent);
            else
                MediaRootCombo.SelectedItem = parent;
        }
        catch { }
    }

    private async void EmbeddedMediaFolder_Click(object sender, RoutedEventArgs e) => await ChooseAndScanEmbeddedFolderAsync();

    private async Task ChooseAndScanEmbeddedFolderAsync()
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = "Choose a media folder to register and scan", UseDescriptionForTitle = true };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        var folder = dialog.SelectedPath;
        if (!MediaRoots.Any(x => string.Equals(x, folder, StringComparison.OrdinalIgnoreCase))) MediaRoots.Add(folder);
        var settings = SettingsStore.Load();
        if (!settings.MediaFolders.Any(x => string.Equals(x, folder, StringComparison.OrdinalIgnoreCase))) settings.MediaFolders.Add(folder);
        SettingsStore.Save(settings);
        if (MediaRootCombo.SelectedItem is string current && string.Equals(current, folder, StringComparison.OrdinalIgnoreCase))
            await ScanEmbeddedMediaAsync(folder);
        else
            MediaRootCombo.SelectedItem = folder; // SelectionChanged scans the selected folder.
    }

    private async void MediaRootCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MediaRootCombo.SelectedItem is string folder && Directory.Exists(folder)) await ScanEmbeddedMediaAsync(folder);
    }

    private async Task ScanEmbeddedMediaAsync(string folder)
    {
        var version = Interlocked.Increment(ref _mediaScanVersion);
        void SetBusy(bool busy, int? count = null)
        {
            if (EmbeddedMediaScanButton is null) return;
            EmbeddedMediaScanButton.IsEnabled = !busy;
            EmbeddedMediaScanButton.Content = busy ? "SCAN…" : "SCAN";
            EmbeddedMediaScanButton.ToolTip = busy
                ? $"Scanning {folder}"
                : count is int n ? $"{n} media file(s) indexed from {folder}" : "Scan the currently selected drive/folder";
        }

        if (Dispatcher.CheckAccess()) SetBusy(true);
        else await Dispatcher.InvokeAsync(() => SetBusy(true));

        MediaLibraryItem[] items;
        try
        {
            items = await Task.Run(() => MediaLibraryService.ScanFolder(folder, recursive: true, maxItems: 2500).ToArray());
        }
        catch
        {
            items = [];
        }

        // A slower scan of the previously-selected drive must never overwrite a newer selection.
        if (version != Volatile.Read(ref _mediaScanVersion)) return;
        if (!Dispatcher.CheckAccess())
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (version != Volatile.Read(ref _mediaScanVersion)) return;
                ApplyEmbeddedMedia(items);
                SetBusy(false, items.Length);
            });
            return;
        }
        ApplyEmbeddedMedia(items);
        SetBusy(false, items.Length);
    }

    private void ApplyEmbeddedMedia(IEnumerable<MediaLibraryItem> items)
    {
        EmbeddedMedia.Clear();
        foreach (var item in items) EmbeddedMedia.Add(item);
        RefreshEmbeddedMediaFilter();
        foreach (var item in EmbeddedMedia.Take(36)) _ = ProbeEmbeddedMediaAsync(item);
    }

    private async Task ProbeEmbeddedMediaAsync(MediaLibraryItem item)
    {
        try { await MediaLibraryService.ProbeAsync(item); } catch { }
    }

    private void EmbeddedMediaSearch_TextChanged(object sender, TextChangedEventArgs e) => RefreshEmbeddedMediaFilter();
    private void EmbeddedCategoryFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) RefreshEmbeddedMediaFilter(); }

    private void RefreshEmbeddedMediaFilter()
    {
        var search = EmbeddedMediaSearch?.Text?.Trim() ?? string.Empty;
        var category = (EmbeddedCategoryFilter?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All";
        var view = CollectionViewSource.GetDefaultView(EmbeddedMedia);
        view.Filter = o => o is MediaLibraryItem item &&
            (category.Equals("All", StringComparison.OrdinalIgnoreCase) || item.Category.Equals(category, StringComparison.OrdinalIgnoreCase)) &&
            (search.Length == 0 || item.FileName.Contains(search, StringComparison.OrdinalIgnoreCase) || item.ParsedName.Contains(search, StringComparison.OrdinalIgnoreCase) || item.Category.Contains(search, StringComparison.OrdinalIgnoreCase));
        view.Refresh();
    }

    private void EmbeddedCategorySet_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedEmbeddedMedia is null) return;
        var category = (EmbeddedCategoryAssign.SelectedItem as ComboBoxItem)?.Content?.ToString();
        if (string.IsNullOrWhiteSpace(category)) category = SelectedEmbeddedMedia.Category;
        SelectedEmbeddedMedia.Category = category!;
        MediaCategoryStore.Set(SelectedEmbeddedMedia.FilePath, category!);
        RefreshEmbeddedMediaFilter();
    }

    private void EmbeddedMediaList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || SelectedEmbeddedMedia is null) return;
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, new[] { SelectedEmbeddedMedia.FilePath });
        data.SetData("Kashtrix.MediaFile", SelectedEmbeddedMedia.FilePath);
        DragDrop.DoDragDrop(EmbeddedMediaList, data, DragDropEffects.Copy);
    }

    private async void EmbeddedMediaList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedEmbeddedMedia is null) return;
        try
        {
            var item = await _vm.ImportMediaAtAsync(SelectedEmbeddedMedia.FilePath, insertAfterSelected: false);
            item.Category = SelectedEmbeddedMedia.Category;
            _vm.NotifySelectedItemEdited();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Embedded File Explorer", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void RegisterInputSource_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new InputSourceWindow { Owner = this };
        if (dialog.ShowDialog() != true || dialog.ResultItem is null) return;
        _vm.RegisterInputSource(dialog.ResultItem);
        InputSourceList.SelectedItem = InputMonitorTiles.FirstOrDefault(x => ReferenceEquals(x.Source, dialog.ResultItem) || x.Source.Id == dialog.ResultItem.Id);
    }

    private PlaylistItem? SelectedInputSource => (InputSourceList.SelectedItem as InputSourceMonitorTile)?.Source;

    private void AddSelectedInputToPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedInputSource is not PlaylistItem item) return;
        _vm.AddInputSource(CloneInputSource(item));
    }

    private void InputSourceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Every registered input already owns an independent monitor decoder. Selection is
        // only the operator focus for drag/drop and "ADD TO LIST".
    }

    private void InputSourceList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || SelectedInputSource is not PlaylistItem item) return;
        DragDrop.DoDragDrop(InputSourceList, new DataObject("Kashtrix.InputSource", item), DragDropEffects.Copy);
    }

    private void StopEmbeddedInputPreview_Click(object sender, RoutedEventArgs e) => RebuildInputMonitorTiles();

    private void InputSources_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Collection changes normally originate on the UI thread (for example after ADD SOURCE).
        // Rebuild immediately in that case so the newly-added tile exists before the caller tries
        // to select it. Background changes are still marshalled safely to the dispatcher.
        if (Dispatcher.CheckAccess()) RebuildInputMonitorTiles();
        else SafeBeginInvoke(RebuildInputMonitorTiles);
    }

    private void RebuildInputMonitorTiles()
    {
        StopAllInputMonitors();
        InputMonitorTiles.Clear();
        foreach (var source in _vm.InputSources)
        {
            var tile = new InputSourceMonitorTile(source);
            InputMonitorTiles.Add(tile);
            StartInputMonitor(tile);
        }
    }

    private void StopAllInputMonitors()
    {
        foreach (var cts in _inputMonitorCancellations.Values)
        {
            try { cts.Cancel(); } catch { }
            try { cts.Dispose(); } catch { }
        }
        _inputMonitorCancellations.Clear();
        foreach (var tile in InputMonitorTiles)
        {
            tile.Status = "STOPPED";
            tile.AudioPeakLevel = 0;
        }
    }

    private void StartInputMonitor(InputSourceMonitorTile tile)
    {
        var item = tile.Source;
        if (_inputMonitorCancellations.Remove(item.Id, out var previous))
        {
            try { previous.Cancel(); previous.Dispose(); } catch { }
        }

        var cts = new CancellationTokenSource();
        _inputMonitorCancellations[item.Id] = cts;
        tile.Status = "CONNECTING";

        _ = Task.Run(() =>
        {
            try
            {
                do
                {
                    using var decoder = new VideoDecoder(item);
                    var firstPts = double.NaN;
                    var clock = Stopwatch.StartNew();
                    var lastPublish = DateTime.MinValue;
                    while (!cts.IsCancellationRequested && decoder.TryRead(out var frame))
                    {
                        if (!item.IsLiveSource)
                        {
                            if (double.IsNaN(firstPts)) firstPts = frame.PtsSeconds;
                            var target = Math.Max(0, frame.PtsSeconds - firstPts);
                            while (!cts.IsCancellationRequested && clock.Elapsed.TotalSeconds + .004 < target) Thread.Sleep(4);
                        }

                        var now = DateTime.UtcNow;
                        if ((now - lastPublish).TotalMilliseconds < 160) continue;
                        lastPublish = now;
                        var image = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.Bgra, frame.Stride);
                        image.Freeze();
                        var audio = Math.Clamp(62.0 + Math.Sin(DateTime.UtcNow.Ticks / 10000000.0 * 5.0) * 16.0 + (Random.Shared.NextDouble() * 5.0), 0, 100);
                        SafeBeginInvoke(() =>
                        {
                            if (cts.IsCancellationRequested) return;
                            tile.PreviewImage = image;
                            tile.AudioPeakLevel = audio;
                            tile.Status = $"LIVE · {frame.Width}×{frame.Height} · {frame.FramesPerSecond:0.##}p";
                        });
                    }
                } while (!cts.IsCancellationRequested && !item.IsLiveSource);
            }
            catch (Exception ex)
            {
                SafeBeginInvoke(() =>
                {
                    tile.AudioPeakLevel = 0;
                    tile.Status = "NO SIGNAL · " + ShortMessage(ex.Message);
                });
            }
        });
    }

    private static string ShortMessage(string? value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "Unable to open input" : value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 72 ? text : text[..69] + "...";
    }

    private static PlaylistItem CloneInputSource(PlaylistItem item)
    {
        var clone = JsonSerializer.Deserialize<PlaylistItem>(JsonSerializer.Serialize(item)) ?? new PlaylistItem();
        clone.Id = Guid.NewGuid();
        clone.Status = "Ready";
        return clone;
    }

    private void KeyboardShortcuts_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "PLAY / PAUSE SELECTED   Ctrl+Space\nPLAY / TAKE              F5\nNEXT EVENT               F6\nSTOP                     F8\nFULLSCREEN               F11\nADD MEDIA                Insert\nINSERT AFTER             Shift+Insert\nREMOVE                   Delete\nMOVE UP                  Alt+Up\nMOVE DOWN                Alt+Down\nNEW PLAYLIST             Ctrl+N\nOPEN PLAYLIST            Ctrl+O\nSAVE PLAYLIST            Ctrl+S\nMASTER MUTE              " + _vm.MasterMuteShortcut,
            "Kashtrix Playout · Keyboard Shortcuts", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "Kashtrix Broadcast Suite\nIntegrated professional broadcast automation platform\n\nPlayout · Scheduler · Playlist Editor · CG Editor · CG Controller · Multiview · QC · File Manager · Ingest · MAM · Channel Controller · NRCS · Prompter · HA / Redundancy · API Gateway / Web MCR",
            "About Kashtrix Playout", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OpenPlaylist_Click(object sender, RoutedEventArgs e) => _vm.OpenPlaylistDocument();
    private void SavePlaylist_Click(object sender, RoutedEventArgs e) => _vm.SavePlaylistDocument();

    private void Scheduler_Click(object sender, RoutedEventArgs e) => LaunchStandalone("Scheduler", "Kashtrix Scheduler");

    private ChannelScheduleWindow? _channelScheduleWindow;

    // Opened in this (playout) process so its RESUME / CUT LIVE controls reach the running schedule.
    private void ChannelSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (_channelScheduleWindow is { IsLoaded: true })
        {
            if (_channelScheduleWindow.WindowState == WindowState.Minimized) _channelScheduleWindow.WindowState = WindowState.Normal;
            _channelScheduleWindow.Activate();
            return;
        }
        _channelScheduleWindow = new ChannelScheduleWindow(_vm) { Owner = this };
        _channelScheduleWindow.Closed += (_, _) => _channelScheduleWindow = null;
        _channelScheduleWindow.Show();
    }
    private void CgEditor_Click(object sender, RoutedEventArgs e) => LaunchStandalone("CGEditor", "Kashtrix CG Designer");
    private void CgController_Click(object sender, RoutedEventArgs e) => LaunchStandalone("CGController", "Kashtrix CG Controller");
    private void AddCgEvent_Click(object sender, RoutedEventArgs e) => AddCgEventToRundown(insertAfterSelected: false);
    private void InsertCgEvent_Click(object sender, RoutedEventArgs e) => AddCgEventToRundown(insertAfterSelected: true);

    private void AddCgEventToRundown(bool insertAfterSelected)
    {
        var project = ChooseCgProject();
        if (project is null) return;
        _vm.AddCgEvent(project, insertAfterSelected);
    }

    private CgProject? ChooseCgProject()
    {
        if (_vm.CgProjects.Count == 0)
        {
            var result = MessageBox.Show(
                "No CG projects are available. Open CG Designer to create a graphic first?",
                "Add CG Event", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (result == MessageBoxResult.Yes) LaunchStandalone("CGEditor", "Kashtrix CG Designer");
            return null;
        }

        var dialog = new Window
        {
            Title = "Add CG Event to Rundown", Owner = this, Width = 430, Height = 250,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(9, 13, 17))
        };
        var grid = new Grid { Margin = new Thickness(18) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = "Choose CG graphic / layer project", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        var list = new ListBox { ItemsSource = _vm.CgProjects, DisplayMemberPath = "Name", SelectedIndex = 0, Margin = new Thickness(0, 0, 0, 12) };
        Grid.SetRow(list, 1); grid.Children.Add(list);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "CANCEL", MinWidth = 85, Margin = new Thickness(4) };
        var add = new Button { Content = "ADD TO RUNDOWN", MinWidth = 120, Margin = new Thickness(4), IsDefault = true };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        add.Click += (_, _) => { if (list.SelectedItem is CgProject) dialog.DialogResult = true; };
        buttons.Children.Add(cancel); buttons.Children.Add(add);
        Grid.SetRow(buttons, 2); grid.Children.Add(buttons);
        dialog.Content = grid;
        return dialog.ShowDialog() == true ? list.SelectedItem as CgProject : null;
    }
    private void PlaylistEditor_Click(object sender, RoutedEventArgs e) => LaunchStandalone("PlaylistEditor", "Kashtrix Playlist Editor");
    private void Multiview_Click(object sender, RoutedEventArgs e) => LaunchStandalone("Multiview", "Kashtrix Multiview");
    private void ChannelController_Click(object sender, RoutedEventArgs e) => LaunchStandalone("ChannelController", "Kashtrix Channel Controller");
    private void FileManager_Click(object sender, RoutedEventArgs e) => LaunchStandalone("FileManager", "Kashtrix File Manager");
    private void Mam_Click(object sender, RoutedEventArgs e) => LaunchStandalone("MAM", "Kashtrix Media Asset Management");
    private void QcController_Click(object sender, RoutedEventArgs e) => LaunchStandalone("QCController", "Kashtrix QC Controller");
    private void MasterMute_Click(object sender, RoutedEventArgs e) => _vm.ToggleMasterMute();


    private void EventLogFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Fires once during InitializeComponent (IsSelected="True") before the list exists.
        if (EventLogList?.ItemsSource is null) return;
        var mode = (EventLogFilterCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "All";
        CollectionViewSource.GetDefaultView(EventLogList.ItemsSource).Filter = mode switch
        {
            "On-Air" => o => o is RecentEventEntry { Level: "OnAir" },
            "Warnings" => o => o is RecentEventEntry { Level: "Warning" or "Error" },
            _ => null
        };
    }

    private void MediaDockExpander_Expanded(object sender, RoutedEventArgs e) => SetMediaDockFocus(true);
    private void MediaDockExpander_Collapsed(object sender, RoutedEventArgs e) => SetMediaDockFocus(false);

    private void SetMediaDockFocus(bool expanded)
    {
        if (NowPlayingCard is null || MediaDockRow is null) return;
        var cardVisibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        NowPlayingCard.Visibility = cardVisibility;
        NextCard.Visibility = cardVisibility;
        UpNextCard.Visibility = cardVisibility;
        EventLogCard.Visibility = cardVisibility;
        ClipInfoPanel.Visibility = cardVisibility;

        if (expanded)
        {
            NowPlayingRow.MinHeight = NextRow.MinHeight = UpNextRow.MinHeight = EventLogRow.MinHeight = 0;
            NowPlayingRow.Height = NextRow.Height = UpNextRow.Height = EventLogRow.Height = new GridLength(0);
            MediaDockRow.MinHeight = 0;
            MediaDockRow.Height = new GridLength(1, GridUnitType.Star);
        }
        else
        {
            NowPlayingRow.MinHeight = 112; NowPlayingRow.Height = new GridLength(1.0, GridUnitType.Star);
            NextRow.MinHeight = 94; NextRow.Height = new GridLength(.82, GridUnitType.Star);
            UpNextRow.MinHeight = 94; UpNextRow.Height = new GridLength(.82, GridUnitType.Star);
            EventLogRow.MinHeight = 120; EventLogRow.Height = new GridLength(1.15, GridUnitType.Star);
            MediaDockRow.MinHeight = 170; MediaDockRow.Height = new GridLength(1.7, GridUnitType.Star);
        }
    }

    private void TogglePlayoutPanel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Tag is not string panel) return;
        SetPlayoutPanelVisible(panel, item.IsChecked);
    }

    private void RestorePlayoutPanels_Click(object sender, RoutedEventArgs e)
    {
        foreach (var panel in new[] { "TopOperator", "PreviewMonitor", "ProgramMonitor", "ProgramAudio", "TransportDeck", "Playlist", "QuickControls", "RightSidebar" })
            SetPlayoutPanelVisible(panel, true);
        SetPlayoutPanelVisible("Rundown", false);
        TopOperatorPanelMenu.IsChecked = PreviewMonitorMenu.IsChecked = ProgramMonitorMenu.IsChecked = ProgramAudioMenu.IsChecked = true;
        TransportDeckMenu.IsChecked = PlaylistPanelMenu.IsChecked = true;
        RundownPanelMenu.IsChecked = false;
        QuickControlsPanelMenu.IsChecked = RightSidebarPanelMenu.IsChecked = true;
    }

    private void ToggleRundownTimeline_Click(object sender, RoutedEventArgs e)
    {
        RundownPanelMenu.IsChecked = !RundownPanelMenu.IsChecked;
        SetPlayoutPanelVisible("Rundown", RundownPanelMenu.IsChecked);
    }

    private void ToggleBlockLoop_Click(object sender, RoutedEventArgs e)
    {
        var block = RundownBlockList.SelectedItem as RundownBlockNode;
        if (block is null && sender is FrameworkElement fe && fe.DataContext is RundownBlockNode node)
            block = node;
        if (block is null) return;

        var newLoopState = !block.IsLoop;
        foreach (var item in block.Items)
        {
            item.IsBlockLoop = newLoopState;
        }
        _vm.RefreshRundownBlocks();
        _vm.NotifySelectedItemEdited();
        _vm.AddRecentEvent($"BLOCK LOOP {(newLoopState ? "ON" : "OFF")} · {block.Name}");
    }

    private void RenameBlock_Click(object sender, RoutedEventArgs e)
    {
        var block = RundownBlockList.SelectedItem as RundownBlockNode;
        if (block is null && sender is FrameworkElement fe && fe.DataContext is RundownBlockNode node)
            block = node;
        if (block is null) return;

        var currentName = block.Name ?? "Block";
        var win = new Window
        {
            Title = "Rename Rundown Block",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(Color.FromRgb(11, 15, 20)),
            Foreground = new SolidColorBrush(Color.FromRgb(240, 243, 246)),
            ShowInTaskbar = false
        };

        var shell = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(11, 15, 20)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(53, 63, 73)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4)
        };

        var mainGrid = new Grid();
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(50) });

        var header = new Grid { Background = new SolidColorBrush(Color.FromRgb(14, 20, 27)) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        header.Children.Add(new TextBlock
        {
            Text = "RENAME RUNDOWN BLOCK",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(214, 183, 238)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0)
        });
        var closeBtn = new Button
        {
            Content = "✕",
            Width = 28,
            Height = 26,
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(150, 160, 175)),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 6, 0)
        };
        closeBtn.Click += (_, _) => { win.DialogResult = false; win.Close(); };
        Grid.SetColumn(closeBtn, 1);
        header.Children.Add(closeBtn);
        header.MouseLeftButtonDown += (_, me) => { if (me.ButtonState == MouseButtonState.Pressed) win.DragMove(); };
        Grid.SetRow(header, 0);
        mainGrid.Children.Add(header);

        var body = new StackPanel { Margin = new Thickness(16, 12, 16, 8) };
        body.Children.Add(new TextBlock
        {
            Text = "ENTER NEW NAME FOR THIS BLOCK:",
            FontSize = 9.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(140, 155, 170)),
            Margin = new Thickness(0, 0, 0, 6)
        });
        var tb = new TextBox
        {
            Text = currentName,
            Height = 30,
            Padding = new Thickness(6, 4, 6, 4),
            Background = new SolidColorBrush(Color.FromRgb(20, 26, 34)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(65, 78, 92)),
            FontSize = 12
        };
        tb.SelectAll();
        body.Children.Add(tb);
        Grid.SetRow(body, 1);
        mainGrid.Children.Add(body);

        var footer = new Border { Background = new SolidColorBrush(Color.FromRgb(9, 13, 17)), Padding = new Thickness(14, 8, 14, 8) };
        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var okBtn = new Button { Content = "RENAME", Width = 90, Height = 28, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancelBtn = new Button { Content = "CANCEL", Width = 80, Height = 28, IsCancel = true };
        okBtn.Click += (_, _) => { win.DialogResult = true; win.Close(); };
        cancelBtn.Click += (_, _) => { win.DialogResult = false; win.Close(); };
        btnPanel.Children.Add(okBtn);
        btnPanel.Children.Add(cancelBtn);
        footer.Child = btnPanel;
        Grid.SetRow(footer, 2);
        mainGrid.Children.Add(footer);

        shell.Child = mainGrid;
        win.Content = shell;

        if (win.ShowDialog() == true && !string.IsNullOrWhiteSpace(tb.Text))
        {
            var newName = tb.Text.Trim();
            if (block.Items.Count > 0)
            {
                block.Items[0].BlockName = newName;
                for (var i = 1; i < block.Items.Count; i++)
                {
                    block.Items[i].BlockName = string.Empty;
                }
            }
            _vm.RefreshRundownBlocks();
            _vm.NotifySelectedItemEdited();
            _vm.AddRecentEvent($"BLOCK RENAMED · '{currentName}' -> '{newName}'");
        }
    }

    private void AppendPlaylistBottom_Click(object sender, RoutedEventArgs e) => _vm.AppendPlaylistBottom();
    private void AppendPlaylistHere_Click(object sender, RoutedEventArgs e) => _vm.AppendPlaylistHere();

    private void SetPlayoutPanelVisible(string panel, bool visible)
    {
        var show = visible ? Visibility.Visible : Visibility.Collapsed;
        switch (panel)
        {
            case "TopOperator":
                TopOperatorPanel.Visibility = show;
                TopOperatorSplitter.Visibility = show;
                TopOperatorRow.MinHeight = visible ? 180 : 0;
                TopOperatorRow.MaxHeight = visible ? 450 : 0;
                TopOperatorRow.Height = visible ? new GridLength(275) : new GridLength(0);
                break;
            case "PreviewMonitor":
                PreviewMonitorPanel.Visibility = show;
                TopPreviewColumn.MinWidth = visible ? 200 : 0;
                TopPreviewColumn.Width = visible ? GridLength.Auto : new GridLength(0);
                break;
            case "ProgramMonitor":
                ProgramMonitorPanel.Visibility = show;
                TopProgramColumn.MinWidth = visible ? 200 : 0;
                TopProgramColumn.Width = visible ? GridLength.Auto : new GridLength(0);
                break;
            case "ProgramAudio":
                ProgramAudioPanel.Visibility = show;
                TopAudioColumn.MinWidth = visible ? 122 : 0;
                TopAudioColumn.MaxWidth = visible ? 146 : 0;
                TopAudioColumn.Width = visible ? new GridLength(132) : new GridLength(0);
                break;
            case "TransportDeck":
                TransportDeckPanel.Visibility = show;
                TopTransportColumn.MinWidth = visible ? 360 : 0;
                TopTransportColumn.Width = visible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
                break;
            case "Rundown":
                RundownTimelinePanel.Visibility = show;
                RundownSplitter.Visibility = show;
                RundownRow.MinHeight = visible ? 96 : 0;
                RundownRow.Height = visible ? new GridLength(112) : new GridLength(0);
                break;
            case "Playlist":
                PlaylistPanel.Visibility = show;
                PlaylistRow.MinHeight = visible ? 150 : 0;
                PlaylistRow.Height = visible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
                break;
            case "QuickControls":
                QuickControlsPanel.Visibility = show;
                QuickControlsSplitter.Visibility = show;
                QuickControlsRow.MinHeight = visible ? 126 : 0;
                QuickControlsRow.Height = visible ? new GridLength(178) : new GridLength(0);
                break;
            case "RightSidebar":
                if (!visible && ShellGrid.ColumnDefinitions[1].ActualWidth > 20) _savedRightSidebarWidth = ShellGrid.ColumnDefinitions[1].ActualWidth;
                RightSidebarPanel.Visibility = show;
                RightSidebarSplitter.Visibility = show;
                var col = ShellGrid.ColumnDefinitions[1];
                col.MinWidth = visible ? 260 : 0;
                col.MaxWidth = visible ? 440 : 0;
                col.Width = visible ? new GridLength(Math.Clamp(_savedRightSidebarWidth, 260, 440)) : new GridLength(0);
                break;
        }
    }

    private void TopOperatorSplitter_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (TopOperatorRow.Height.Value <= 0) return;
        var newH = Math.Clamp(TopOperatorRow.ActualHeight + e.VerticalChange, 180, 450);
        TopOperatorRow.Height = new GridLength(newH);
    }

    private void RundownSplitter_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (RundownRow.Height.Value <= 0) return;
        var newH = Math.Clamp(RundownRow.ActualHeight + e.VerticalChange, 60, 260);
        RundownRow.Height = new GridLength(newH);
    }

    private void QuickControlsSplitter_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (QuickControlsRow.Height.Value <= 0) return;
        var newH = Math.Clamp(QuickControlsRow.ActualHeight - e.VerticalChange, 100, 400);
        QuickControlsRow.Height = new GridLength(newH);
    }

    private Window? _playlistDockWindow;

    private void PlaylistDockOut_Click(object sender, RoutedEventArgs e)
    {
        if (_playlistDockWindow is { IsVisible: true }) { _playlistDockWindow.Activate(); return; }
        if (PlaylistPanel.Parent is not Grid home) return;
        home.Children.Remove(PlaylistPanel);
        var oldMargin = PlaylistPanel.Margin;
        var oldRowHeight = PlaylistRow.Height;
        var oldRowMinHeight = PlaylistRow.MinHeight;
        PlaylistRow.MinHeight = 0;
        PlaylistRow.Height = new GridLength(0);
        PlaylistPanel.Margin = new Thickness(0);
        var dock = new Window
        {
            Title = "Kashtrix Playout · Playlist", Width = 1480, Height = 720, MinWidth = 960, MinHeight = 420,
            Background = new SolidColorBrush(Color.FromRgb(9,13,17)), Content = PlaylistPanel,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, ResizeMode = ResizeMode.CanResizeWithGrip, SizeToContent = SizeToContent.Manual, ShowInTaskbar = true,
            Icon = Icon
        };
        _playlistDockWindow = dock;
        dock.Closed += (_, _) =>
        {
            dock.Content = null;
            PlaylistPanel.Margin = oldMargin;
            Grid.SetRow(PlaylistPanel, 3); Grid.SetColumn(PlaylistPanel, 0);
            home.Children.Add(PlaylistPanel);
            PlaylistRow.MinHeight = oldRowMinHeight;
            PlaylistRow.Height = oldRowHeight;
            _playlistDockWindow = null;
        };
        dock.Show();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { ExitEngine(); e.Handled = true; return; }
        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.Control && e.Key == Key.Space) { _vm.ToggleSelectedPlayPause(); e.Handled = true; return; }
        if (modifiers == ModifierKeys.Control && e.Key == Key.N) { NewPlaylist_Click(sender, e); e.Handled = true; return; }
        if (modifiers == ModifierKeys.Control && e.Key == Key.O) { _vm.OpenPlaylistDocument(); e.Handled = true; return; }
        if (modifiers == ModifierKeys.Control && e.Key == Key.S) { _vm.SavePlaylistDocument(); e.Handled = true; return; }
        if (modifiers == ModifierKeys.None && e.Key == Key.Insert) { _vm.AddCommand.Execute(null); e.Handled = true; return; }
        if (modifiers == ModifierKeys.Shift && e.Key == Key.Insert) { _ = _vm.InsertMediaAfterSelectedAsync(); e.Handled = true; return; }
        if (modifiers == ModifierKeys.None && e.Key == Key.Delete) { _vm.RemoveSelected(); e.Handled = true; return; }
        if (modifiers == ModifierKeys.Alt && e.Key == Key.Up) { _vm.MoveSelected(-1); e.Handled = true; return; }
        if (modifiers == ModifierKeys.Alt && e.Key == Key.Down) { _vm.MoveSelected(+1); e.Handled = true; return; }
        if (MatchesGesture(_vm.MasterMuteShortcut, e)) { _vm.ToggleMasterMute(); e.Handled = true; return; }
        if (MatchesGesture(_vm.MasterVolumeUpShortcut, e)) { _vm.ChangeMasterVolume(+5); e.Handled = true; return; }
        if (MatchesGesture(_vm.MasterVolumeDownShortcut, e)) { _vm.ChangeMasterVolume(-5); e.Handled = true; }
    }

    private static bool MatchesGesture(string? gestureText, KeyEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(gestureText)) return false;
        try
        {
            var converter = new KeyGestureConverter();
            return converter.ConvertFromString(gestureText) is KeyGesture gesture && gesture.Matches(null, e);
        }
        catch { return false; }
    }

    private void LaunchStandalone(string appKey, string displayName)
    {
        if (StandaloneAppLauncher.Launch(appKey, out var error)) return;
        MessageBox.Show(error, displayName, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void FocusInOut_Click(object sender, RoutedEventArgs e)
    {
        InOutPanel.BringIntoView();
        InOutPanel.Focus();
    }

    private sealed class DarkMenuColorTable : Forms.ProfessionalColorTable
    {
        private static readonly Drawing.Color Bg = Drawing.Color.FromArgb(11, 17, 24);
        private static readonly Drawing.Color Hover = Drawing.Color.FromArgb(24, 49, 73);
        private static readonly Drawing.Color Line = Drawing.Color.FromArgb(49, 67, 82);
        public override Drawing.Color ToolStripDropDownBackground => Bg;
        public override Drawing.Color ImageMarginGradientBegin => Bg;
        public override Drawing.Color ImageMarginGradientMiddle => Bg;
        public override Drawing.Color ImageMarginGradientEnd => Bg;
        public override Drawing.Color MenuItemSelected => Hover;
        public override Drawing.Color MenuItemBorder => Drawing.Color.FromArgb(139, 61, 255);
        public override Drawing.Color MenuBorder => Line;
        public override Drawing.Color SeparatorDark => Line;
        public override Drawing.Color SeparatorLight => Line;
    }


    private void OpenFloatingTimers_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var win = new BroadcastTimersClockWindow(_vm);
            win.Owner = this;
            win.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open Floating Timers: {ex.Message}", "Kashtrix Playout", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void ApplyPlayoutLayout_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not string layout) return;
        switch (layout)
        {
            case "Standard":
                RestorePlayoutPanels_Click(sender, e);
                break;
            case "Compact":
                SetPlayoutPanelVisible("TopOperator", true);
                SetPlayoutPanelVisible("PreviewMonitor", true);
                SetPlayoutPanelVisible("ProgramMonitor", true);
                SetPlayoutPanelVisible("ProgramAudio", true);
                SetPlayoutPanelVisible("TransportDeck", true);
                SetPlayoutPanelVisible("Rundown", false);
                SetPlayoutPanelVisible("Playlist", true);
                SetPlayoutPanelVisible("QuickControls", false);
                SetPlayoutPanelVisible("RightSidebar", false);
                RundownPanelMenu.IsChecked = QuickControlsPanelMenu.IsChecked = RightSidebarPanelMenu.IsChecked = false;
                TopOperatorPanelMenu.IsChecked = PreviewMonitorMenu.IsChecked = ProgramMonitorMenu.IsChecked = ProgramAudioMenu.IsChecked = TransportDeckMenu.IsChecked = PlaylistPanelMenu.IsChecked = true;
                break;
            case "MasterControl":
                SetPlayoutPanelVisible("TopOperator", true);
                SetPlayoutPanelVisible("PreviewMonitor", true);
                SetPlayoutPanelVisible("ProgramMonitor", true);
                SetPlayoutPanelVisible("ProgramAudio", true);
                SetPlayoutPanelVisible("TransportDeck", true);
                SetPlayoutPanelVisible("Rundown", true);
                SetPlayoutPanelVisible("Playlist", true);
                SetPlayoutPanelVisible("QuickControls", true);
                SetPlayoutPanelVisible("RightSidebar", false);
                RightSidebarPanelMenu.IsChecked = false;
                TopOperatorPanelMenu.IsChecked = PreviewMonitorMenu.IsChecked = ProgramMonitorMenu.IsChecked = ProgramAudioMenu.IsChecked = TransportDeckMenu.IsChecked = true;
                RundownPanelMenu.IsChecked = PlaylistPanelMenu.IsChecked = QuickControlsPanelMenu.IsChecked = true;
                break;
            case "StudioMonitor":
                SetPlayoutPanelVisible("TopOperator", true);
                SetPlayoutPanelVisible("PreviewMonitor", false);
                SetPlayoutPanelVisible("ProgramMonitor", true);
                SetPlayoutPanelVisible("ProgramAudio", true);
                SetPlayoutPanelVisible("TransportDeck", true);
                SetPlayoutPanelVisible("Rundown", false);
                SetPlayoutPanelVisible("Playlist", true);
                SetPlayoutPanelVisible("QuickControls", false);
                SetPlayoutPanelVisible("RightSidebar", false);
                PreviewMonitorMenu.IsChecked = RundownPanelMenu.IsChecked = QuickControlsPanelMenu.IsChecked = RightSidebarPanelMenu.IsChecked = false;
                TopOperatorPanelMenu.IsChecked = ProgramMonitorMenu.IsChecked = ProgramAudioMenu.IsChecked = TransportDeckMenu.IsChecked = PlaylistPanelMenu.IsChecked = true;
                break;
        }
    }


}


public sealed class InputSourceMonitorTile : INotifyPropertyChanged
{
    private BitmapSource? _previewImage;
    private string _status = "WAITING";
    private double _audioPeakLevel;

    public InputSourceMonitorTile(PlaylistItem source) => Source = source;
    public PlaylistItem Source { get; }
    public BitmapSource? PreviewImage { get => _previewImage; set { if (ReferenceEquals(_previewImage, value)) return; _previewImage = value; Raise(); } }
    public string Status { get => _status; set { if (string.Equals(_status, value, StringComparison.Ordinal)) return; _status = value; Raise(); } }
    public double AudioPeakLevel { get => _audioPeakLevel; set { if (Math.Abs(_audioPeakLevel - value) < 0.1) return; _audioPeakLevel = value; Raise(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

}
