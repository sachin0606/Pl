using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
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

public partial class CgControllerWindow : Window, INotifyPropertyChanged, IDisposable
{
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _renderClock = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(20) };
    private readonly CgCompositor _previewMonitorCompositor = new();
    private readonly CgCompositor _programMonitorCompositor = new();
    private readonly CgGraphicsOutputEngine _cgOutput = new();
    private readonly VideoFrameData _monitorBaseFrame = CreateMonitorBaseFrame();
    private WriteableBitmap? _previewMonitorBitmap;
    private WriteableBitmap? _programMonitorBitmap;
    private VideoFrameData? _latestPlayoutPreviewFrame;
    // Program/base frames come from reusable cadence/decoder buffers, so copy them on receipt.
    private readonly BroadcastPlayout.Engine.FrameMailbox _latestPlayoutProgramFrame = new(30);
    private VideoFrameData? _latestPlayoutPreviewBaseFrame;
    private readonly BroadcastPlayout.Engine.FrameMailbox _latestPlayoutProgramBaseFrame = new(30);
    private CgProject? _programSnapshot;
    private CgProject? _programPreviousSnapshot;
    private readonly Dictionary<int, (CgProject Project, DateTime StartedUtc)> _programLayerSnapshots = new();
    private DateTime _previewStartedUtc = DateTime.UtcNow;
    private DateTime _programStartedUtc = DateTime.UtcNow;
    private DateTime _programTransitionStartedUtc = DateTime.MinValue;
    private string _programTransitionMode = "None";
    private double _programTransitionSeconds = 0.45;
    private string _takeTransition = "None";
    private double _takeTransitionSeconds = 0.45;
    private ChannelControllerOperator? _operator;
    private CgProject? _selectedProject;
    private CgLayer? _selectedLayer;
    private string _connectionStatus = "DISCONNECTED";
    private string _lastOperation = "Ready";
    private string _lastDetail = "Select target channel(s), then choose a project and layer. Same-PC Playout works even when Channel Controller is offline.";
    private string _previewProjectName = "No CG in preview";
    private string _previewLayerName = "PREVIEW CLEAR";
    private string _previewLayerText = "Select a project and press PLAY PREVIEW · complete composition.";
    private string _programProjectName = "No CG on program";
    private string _programLayerName = "PROGRAM CLEAR";
    private string _programLayerText = "Select a layer, preview it, then TAKE it to program.";
    private CgProject? _previewSnapshot;
    private string _cgOutputStatus = "CG OUTPUT · OFF";
    private IReadOnlyList<DeckLinkDeviceOption> _cgDeckLinkDevices = [];
    private bool _disposed;
    private string _catalogViewMode = "List";
    private CgCatalogCard? _selectedCatalogCard;
    private string _quickEditTextValue = "";
    private string _quickEditFillValue = "#FFFFFFFF";
    private string _quickEditBgValue = "#FF0B1728";
    private string _quickEditFontSizeValue = "32";
    private string _quickEditOpacityValue = "1.0";
    private string _quickEditXValue = "0";
    private string _quickEditYValue = "0";
    private string _quickEditWidthValue = "900";
    private string _quickEditHeightValue = "120";
    private string _quickEditBorderColorValue = "#00000000";
    private string _quickEditBorderWidthValue = "0";
    private string _quickEditCornerRadiusValue = "0";
    private string _quickEditSourceValue = "";

    public ObservableCollection<CgProject> Projects { get; } = [];
    public ObservableCollection<CgCatalogCard> CatalogCards { get; } = [];
    public ObservableCollection<CgCatalogGroup> CatalogGroups { get; } = [];
    public ObservableCollection<CgLayer> Layers { get; } = [];
    public ObservableCollection<CgTargetRow> Channels { get; } = [];
    public ObservableCollection<CgScheduleItem> Schedules { get; } = [];
    private string _newScheduleProjectName = "";
    private string _newScheduleStartTime = "21:00:00";
    private string _newScheduleStopTime = "22:00:00";
    private int _newScheduleLayer = 0;
    private string _newScheduleDays = "Daily";

    public string NewScheduleProjectName { get => _newScheduleProjectName; set { _newScheduleProjectName = value; Raise(); } }
    public string NewScheduleStartTime { get => _newScheduleStartTime; set { _newScheduleStartTime = value; Raise(); } }
    public string NewScheduleStopTime { get => _newScheduleStopTime; set { _newScheduleStopTime = value; Raise(); } }
    public int NewScheduleLayer { get => _newScheduleLayer; set { _newScheduleLayer = value; Raise(); } }
    public string NewScheduleDays { get => _newScheduleDays; set { _newScheduleDays = value; Raise(); } }
    public IReadOnlyList<string> ScheduleDaysOptions { get; } = ["Daily", "Mon-Fri", "Sat-Sun", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];
    public string ConnectionStatus { get => _connectionStatus; private set { _connectionStatus = value; Raise(); Raise(nameof(StatusLine)); } }
    public string LastOperation { get => _lastOperation; private set { _lastOperation = value; Raise(); } }
    public string LastDetail { get => _lastDetail; private set { _lastDetail = value; Raise(); } }
    public string PreviewProjectName { get => _previewProjectName; private set { _previewProjectName = value; Raise(); } }
    public string PreviewLayerName { get => _previewLayerName; private set { _previewLayerName = value; Raise(); } }
    public string PreviewLayerText { get => _previewLayerText; private set { _previewLayerText = value; Raise(); } }
    public string ProgramProjectName { get => _programProjectName; private set { _programProjectName = value; Raise(); } }
    public string ProgramLayerName { get => _programLayerName; private set { _programLayerName = value; Raise(); } }
    public string ProgramLayerText { get => _programLayerText; private set { _programLayerText = value; Raise(); } }
    public string Endpoint => $"{_vm.ControllerHost}:{_vm.ControllerPort}";
    public string StatusLine => $"{ConnectionStatus} · {Channels.Count(x => x.IsSelected)} target(s) · {Projects.Count} project(s)";
    public CgProject? SelectedProject
    {
        get => _selectedProject;
        set
        {
            _selectedProject = value;
            Raise();
            LoadLayers();
            if (value is not null)
            {
                var activeKvp = _programLayerSnapshots.FirstOrDefault(kv =>
                    kv.Value.Project.Id == value.Id ||
                    string.Equals(kv.Value.Project.Name, value.Name, StringComparison.OrdinalIgnoreCase));
                if (activeKvp.Key > 0)
                {
                    CurrentOverlayNumber = activeKvp.Key;
                }
            }
        }
    }
    public CgLayer? SelectedLayer
    {
        get => _selectedLayer;
        set
        {
            _selectedLayer = value;
            Raise();
            Raise(nameof(IsTextLayer));
            Raise(nameof(IsShapeLayer));
            Raise(nameof(IsMediaLayer));
            Raise(nameof(SelectedLayerTypeBadge));
            Raise(nameof(TextSectionVisibility));
            Raise(nameof(ShapeSectionVisibility));
            Raise(nameof(MediaSectionVisibility));
            Raise(nameof(LayerSelectedVisibility));
            Raise(nameof(NoLayerSelectedVisibility));
            if (value is not null)
            {
                QuickEditTextValue = value.Text ?? "";
                QuickEditFillValue = !string.IsNullOrWhiteSpace(value.Fill) ? value.Fill : "#FFFFFFFF";
                QuickEditBgValue = !string.IsNullOrWhiteSpace(value.Background) ? value.Background : "#FF0B1728";
                QuickEditFontSizeValue = value.FontSize > 0 ? value.FontSize.ToString("0.##") : "32";
                QuickEditOpacityValue = value.Opacity.ToString("0.##");
                QuickEditXValue = value.X.ToString("0.##");
                QuickEditYValue = value.Y.ToString("0.##");
                QuickEditWidthValue = value.Width.ToString("0.##");
                QuickEditHeightValue = value.Height.ToString("0.##");
                QuickEditBorderColorValue = !string.IsNullOrWhiteSpace(value.BorderColor) ? value.BorderColor : "#00000000";
                QuickEditBorderWidthValue = value.BorderWidth.ToString("0.##");
                QuickEditCornerRadiusValue = value.CornerRadius.ToString("0.##");
                QuickEditSourceValue = value.Source ?? "";
            }
        }
    }
    public bool IsTextLayer => SelectedLayer is not null && (
        string.Equals(SelectedLayer.Type, "Text", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(SelectedLayer.Type, "Ticker", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(SelectedLayer.Type, "DigitalClock", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(SelectedLayer.Type, "NepaliDate", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(SelectedLayer.Type, "DateTime", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(SelectedLayer.Type, "Timecode", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(SelectedLayer.Type, "Roll", StringComparison.OrdinalIgnoreCase));

    public bool IsShapeLayer => SelectedLayer is not null && (
        string.Equals(SelectedLayer.Type, "Shape", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(SelectedLayer.Type, "Line", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(SelectedLayer.Type, "Ellipse", StringComparison.OrdinalIgnoreCase));

    public bool IsMediaLayer => SelectedLayer is not null && (
        string.Equals(SelectedLayer.Type, "Image", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(SelectedLayer.Type, "ImageSequence", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(SelectedLayer.Type, "Video", StringComparison.OrdinalIgnoreCase));

    public string SelectedLayerTypeBadge => SelectedLayer is null ? "" : $"[{SelectedLayer.Type.ToUpperInvariant()} LAYER]";
    public Visibility TextSectionVisibility => IsTextLayer ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ShapeSectionVisibility => IsShapeLayer ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MediaSectionVisibility => IsMediaLayer ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LayerSelectedVisibility => SelectedLayer is not null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoLayerSelectedVisibility => SelectedLayer is null ? Visibility.Visible : Visibility.Collapsed;

    public string QuickEditTextValue { get => _quickEditTextValue; set { _quickEditTextValue = value; Raise(); } }
    public string QuickEditFillValue { get => _quickEditFillValue; set { _quickEditFillValue = value; Raise(); } }
    public string QuickEditBgValue { get => _quickEditBgValue; set { _quickEditBgValue = value; Raise(); } }
    public string QuickEditFontSizeValue { get => _quickEditFontSizeValue; set { _quickEditFontSizeValue = value; Raise(); } }
    public string QuickEditOpacityValue { get => _quickEditOpacityValue; set { _quickEditOpacityValue = value; Raise(); } }
    public string QuickEditXValue { get => _quickEditXValue; set { _quickEditXValue = value; Raise(); } }
    public string QuickEditYValue { get => _quickEditYValue; set { _quickEditYValue = value; Raise(); } }
    public string QuickEditWidthValue { get => _quickEditWidthValue; set { _quickEditWidthValue = value; Raise(); } }
    public string QuickEditHeightValue { get => _quickEditHeightValue; set { _quickEditHeightValue = value; Raise(); } }
    public string QuickEditBorderColorValue { get => _quickEditBorderColorValue; set { _quickEditBorderColorValue = value; Raise(); } }
    public string QuickEditBorderWidthValue { get => _quickEditBorderWidthValue; set { _quickEditBorderWidthValue = value; Raise(); } }
    public string QuickEditCornerRadiusValue { get => _quickEditCornerRadiusValue; set { _quickEditCornerRadiusValue = value; Raise(); } }
    public string QuickEditSourceValue { get => _quickEditSourceValue; set { _quickEditSourceValue = value; Raise(); } }
    public CgCatalogCard? SelectedCatalogCard
    {
        get => _selectedCatalogCard;
        set { _selectedCatalogCard=value; Raise(); if(value is not null && !ReferenceEquals(SelectedProject,value.Project)) SelectedProject=value.Project; }
    }
    public Visibility CatalogListVisibility => _catalogViewMode == "List" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CatalogGridVisibility => _catalogViewMode == "Grid" ? Visibility.Visible : Visibility.Collapsed;
    public IReadOnlyList<string> TakeTransitions { get; } = ["None", "Fade", "Wipe", "Wipe Left", "Wipe Right", "Zoom"];
    public string TakeTransition { get => _takeTransition; set { _takeTransition = string.IsNullOrWhiteSpace(value) ? "None" : value; Raise(); } }
    public double TakeTransitionSeconds { get => _takeTransitionSeconds; set { _takeTransitionSeconds = Math.Clamp(value, 0.05, 5.0); Raise(); } }
    public ObservableCollection<int> ActiveOverlayLayers { get; } = [1, 2, 3];
    private int _currentOverlayNumber = 1;
    public int CurrentOverlayNumber
    {
        get => _currentOverlayNumber;
        set { _currentOverlayNumber = Math.Max(1, value); Raise(); Raise(nameof(CurrentOverlayTitle)); }
    }
    public string CurrentOverlayTitle => $"ACTIVE: OVERLAY {CurrentOverlayNumber}";

    private void SelectOverlayLayer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: int layerNum })
        {
            CurrentOverlayNumber = layerNum;
            LastOperation = $"SELECTED OVERLAY {layerNum}";
            LastDetail = $"Routing commands to Overlay {layerNum}.";
        }
    }

    private void AddOverlayLayer_Click(object sender, RoutedEventArgs e)
    {
        var next = ActiveOverlayLayers.Count == 0 ? 1 : (ActiveOverlayLayers.Max() + 1);
        ActiveOverlayLayers.Add(next);
        CurrentOverlayNumber = next;
        LastOperation = $"ADDED OVERLAY {next}";
        LastDetail = $"Overlay {next} added and set active.";
    }

    private void ClearAllOverlays_Click(object sender, RoutedEventArgs e)
    {
        var targets = Channels.Where(x => x.IsSelected).Select(x => x.ChannelId)
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (targets.Length == 0) targets = [_vm.ChannelId];

        var command = new CgRemoteCommand
        {
            CommandId = Guid.NewGuid().ToString("N"),
            Action = "CLEAR",
            Bus = "PROGRAM",
            Layer = -1
        };
        try { LocalCgCommandBus.Publish(targets, command); } catch { }
        _programLayerSnapshots.Clear();
        _programSnapshot = null;
        _programPreviousSnapshot = null;
        ProgramCgImage.Source = null;
        _programMonitorBitmap = null;
        ProgramProjectName = "All Overlays Cleared";
        ProgramLayerName = "ALL CLEARED";
        ProgramLayerText = "All program overlay layers cleared.";
        _cgOutput.ClearProgram();
        LastOperation = "ALL OVERLAYS CLEARED";
    }

    public IReadOnlyList<string> DeckLinkKeyerModes { get; } = ["Off", "Internal", "External"];
    public IReadOnlyList<double> CgFrameRates => OutputProfile.FrameRates;
    public IReadOnlyList<string> CgOutputPresets => OutputProfile.Presets.Where(x => !x.Equals("Source", StringComparison.OrdinalIgnoreCase)).ToArray();
    public IReadOnlyList<string> CgScanModes => OutputProfile.ScanModes;
    public IReadOnlyList<string> CgAlphaModes => OutputProfile.AlphaModes;
    public IReadOnlyList<DeckLinkDeviceOption> CgDeckLinkDevices => _cgDeckLinkDevices;
    public bool EnableCgNdiAlphaOutput { get => _vm.Settings.EnableCgNdiAlphaOutput; set { _vm.Settings.EnableCgNdiAlphaOutput = value; Raise(); } }
    public string CgNdiAlphaSourceName { get => _vm.Settings.CgNdiAlphaSourceName; set { _vm.Settings.CgNdiAlphaSourceName = value ?? ""; Raise(); } }
    public bool EnableCgDeckLinkKeyFill { get => _vm.Settings.EnableCgDeckLinkKeyFill; set { _vm.Settings.EnableCgDeckLinkKeyFill = value; Raise(); } }
    public int CgDeckLinkDeviceIndex
    {
        get => _vm.Settings.CgDeckLinkDeviceIndex;
        set
        {
            _vm.Settings.CgDeckLinkDeviceIndex = Math.Max(0, value);
            _vm.Settings.CgDeckLinkDeviceName = CgDeckLinkDevices.FirstOrDefault(x => x.Index == _vm.Settings.CgDeckLinkDeviceIndex)?.PersistentName ?? string.Empty;
            Raise(); Raise(nameof(CgDeckLinkDeviceName));
        }
    }
    public string CgDeckLinkDeviceName => CgDeckLinkDevices.FirstOrDefault(x => x.Index == CgDeckLinkDeviceIndex)?.Label ?? "DeckLink hardware scan unavailable";
    public string CgDeckLinkKeyerMode { get => _vm.Settings.CgDeckLinkKeyerMode; set { _vm.Settings.CgDeckLinkKeyerMode = value ?? "External"; Raise(); } }
    public int CgDeckLinkKeyerLevel { get => _vm.Settings.CgDeckLinkKeyerLevel; set { _vm.Settings.CgDeckLinkKeyerLevel = Math.Clamp(value, 0, 255); Raise(); } }
    public string CgOutputPreset { get => _vm.Settings.CgOutputProfile.Preset; set { _vm.Settings.CgOutputProfile.Preset = value ?? "1920x1080"; Raise(); Raise(nameof(CgOutputSummary)); } }
    public double CgOutputFrameRate { get => _vm.Settings.CgOutputProfile.FramesPerSecond; set { _vm.Settings.CgOutputProfile.FramesPerSecond = value; Raise(); Raise(nameof(CgOutputSummary)); } }
    public string CgOutputScanMode { get => _vm.Settings.CgOutputProfile.ScanMode; set { _vm.Settings.CgOutputProfile.ScanMode = value ?? "Progressive"; Raise(); Raise(nameof(CgOutputSummary)); } }
    public string CgOutputAlphaMode { get => _vm.Settings.CgOutputProfile.AlphaMode; set { _vm.Settings.CgOutputProfile.AlphaMode = value ?? "Straight (Unmatted)"; Raise(); } }
    public int CgOutputPrerollFrames { get => _vm.Settings.CgOutputProfile.PrerollFrames; set { _vm.Settings.CgOutputProfile.PrerollFrames = value; Raise(); } }
    public string CgOutputSummary => _vm.Settings.CgOutputProfile.Summary;
    public string CgOutputStatus { get => _cgOutputStatus; private set { _cgOutputStatus = value; Raise(); } }

    public CgControllerWindow(MainViewModel vm)
    {
        InitializeComponent();
        WindowChromeActions.ApplyCleanBorder(this);
        _vm = vm;
        _cgDeckLinkDevices = DeckLinkOutputAdapter.EnumerateDevices();
        var savedDevice = _cgDeckLinkDevices.FirstOrDefault(x => !string.IsNullOrWhiteSpace(_vm.Settings.CgDeckLinkDeviceName) && x.PersistentName.Equals(_vm.Settings.CgDeckLinkDeviceName, StringComparison.OrdinalIgnoreCase));
        if (savedDevice is not null) _vm.Settings.CgDeckLinkDeviceIndex = savedDevice.Index;
        else _vm.Settings.CgDeckLinkDeviceIndex = Math.Clamp(_vm.Settings.CgDeckLinkDeviceIndex, 0, Math.Max(0, _cgDeckLinkDevices.Count - 1));
        _vm.Settings.CgDeckLinkDeviceName = _cgDeckLinkDevices.FirstOrDefault(x => x.Index == _vm.Settings.CgDeckLinkDeviceIndex)?.PersistentName ?? string.Empty;
        DataContext = this;
        _cgOutput.StatusChanged += status => Dispatcher.BeginInvoke(() => CgOutputStatus = status);
        _cgOutput.Configure(_vm.Settings);
        // Preserve saved/imported CG projects and user database
        ReloadProjects();
        _vm.CgProjectsChanged += OnCgProjectsChanged;
        _vm.PreviewFrameReady += OnPlayoutPreviewFrame;
        _vm.PreviewBaseFrameReady += OnPlayoutPreviewBaseFrame;
        _vm.PreviewCleared += () => Interlocked.Exchange(ref _latestPlayoutPreviewFrame, null);
        _vm.VideoFrameReady += OnPlayoutProgramFrame;
        _vm.ProgramBaseFrameReady += OnPlayoutProgramBaseFrame;
        LoadRegistryChannels();
        LoadSchedules();
        _clock.Tick += Clock_Tick;
        _clock.Start();
        _renderClock.Tick += RenderClock_Tick;
        _renderClock.Start();
        Clock_Tick(null, EventArgs.Empty);
        Closed += (_, _) => Dispose();
        Loaded += async (_, _) => await ConnectAndRefreshAsync();
    }

    private void Clock_Tick(object? sender, EventArgs e)
    {
        ClockText.Text = DateTime.Now.ToString("HH:mm:ss");
        CheckSchedulesTick();
    }

    private void RenderClock_Tick(object? sender, EventArgs e)
    {
        var playoutPreview = Volatile.Read(ref _latestPlayoutPreviewFrame);
        if (_previewSnapshot is not null)
        {
            var baseFrame = Volatile.Read(ref _latestPlayoutPreviewBaseFrame) ?? _monitorBaseFrame;
            var frame = _previewMonitorCompositor.Composite(baseFrame, _previewSnapshot, MonitorElapsed(_previewStartedUtc, _previewSnapshot));
            _previewMonitorBitmap = PresentMonitorFrame(PreviewCgImage, _previewMonitorBitmap, frame);
        }
        else if (playoutPreview is not null)
        {
            _previewMonitorBitmap = PresentMonitorFrame(PreviewCgImage, _previewMonitorBitmap, playoutPreview);
        }

        var playoutProgram = _latestPlayoutProgramFrame.Latest();
        if (_programLayerSnapshots.Count > 1)
        {
            var frame = _latestPlayoutProgramBaseFrame.Latest() ?? _monitorBaseFrame;
            foreach (var kvp in _programLayerSnapshots.OrderBy(x => x.Key))
            {
                var snap = kvp.Value.Project;
                var elapsed = MonitorElapsed(kvp.Value.StartedUtc, snap);
                frame = _programMonitorCompositor.Composite(frame, snap, elapsed);
            }
            _programMonitorBitmap = PresentMonitorFrame(ProgramCgImage, _programMonitorBitmap, frame);
        }
        else if (_programSnapshot is not null)
        {
            var baseFrame = _latestPlayoutProgramBaseFrame.Latest() ?? _monitorBaseFrame;
            VideoFrameData frame;
            var transitionElapsed = _programTransitionStartedUtc == DateTime.MinValue ? double.MaxValue : (DateTime.UtcNow - _programTransitionStartedUtc).TotalSeconds;
            if (_programPreviousSnapshot is not null && !_programTransitionMode.Equals("None", StringComparison.OrdinalIgnoreCase) && transitionElapsed < _programTransitionSeconds)
            {
                var progress = Math.Clamp(transitionElapsed / Math.Max(.05, _programTransitionSeconds), 0, 1);
                frame = _programMonitorCompositor.CompositeTransition(
                    baseFrame,
                    _programPreviousSnapshot,
                    _programSnapshot,
                    MonitorElapsed(_programStartedUtc, _programSnapshot),
                    MonitorElapsed(_programStartedUtc, _programSnapshot),
                    progress,
                    _programTransitionMode);
            }
            else
            {
                _programPreviousSnapshot = null;
                _programTransitionMode = "None";
                frame = _programMonitorCompositor.Composite(baseFrame, _programSnapshot, MonitorElapsed(_programStartedUtc, _programSnapshot));
            }
            _programMonitorBitmap = PresentMonitorFrame(ProgramCgImage, _programMonitorBitmap, frame);
        }
        else if (playoutProgram is not null)
        {
            _programMonitorBitmap = PresentMonitorFrame(ProgramCgImage, _programMonitorBitmap, playoutProgram);
        }
    }

    // Keep only the newest confidence frame. The 25 fps render timer consumes it,
    // preventing dispatcher backlog/frame-loss while showing the real Playout buses.
    private void OnPlayoutPreviewFrame(VideoFrameData frame) => Interlocked.Exchange(ref _latestPlayoutPreviewFrame, frame);
    private void OnPlayoutProgramFrame(VideoFrameData frame) => _latestPlayoutProgramFrame.Post(frame);
    private void OnPlayoutPreviewBaseFrame(VideoFrameData frame) => Interlocked.Exchange(ref _latestPlayoutPreviewBaseFrame, frame);
    private void OnPlayoutProgramBaseFrame(VideoFrameData frame) => _latestPlayoutProgramBaseFrame.Post(frame);

    private static double MonitorElapsed(DateTime startedUtc, CgProject project)
    {
        var raw = Math.Max(0, (DateTime.UtcNow - startedUtc).TotalSeconds);
        var fps = Math.Clamp(project.FrameRate > 0 ? project.FrameRate : 50.0, 1.0, 120.0);
        return Math.Floor(raw * fps + 0.0001) / fps;
    }

    private static VideoFrameData CreateMonitorBaseFrame()
    {
        const int width = 1920, height = 1080, stride = width * 4;
        var bgra = new byte[stride * height];
        for (var i = 3; i < bgra.Length; i += 4) bgra[i] = 255;
        return new VideoFrameData(bgra, width, height, stride, 0, 50, 1);
    }

    private static WriteableBitmap PresentMonitorFrame(System.Windows.Controls.Image target, WriteableBitmap? bitmap, VideoFrameData frame)
    {
        if (bitmap is null || bitmap.PixelWidth != frame.Width || bitmap.PixelHeight != frame.Height)
        {
            bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
            target.Source = bitmap;
        }
        bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Bgra, frame.Stride, 0);
        return bitmap;
    }
    private void ReloadProjects()
    {
        var selectedId = SelectedProject?.Id ?? Guid.Empty;
        _vm.ReloadCgProjectsFromDatabase();
        Projects.Clear();

        foreach (var p in _vm.CgProjects.OrderBy(x => x.Name))
        {
            Projects.Add(p);
        }
        CatalogGroups.Clear();
        foreach (var group in Projects.GroupBy(ProjectCategory).OrderBy(x => x.Key))
            CatalogGroups.Add(new CgCatalogGroup { Name = group.Key, Projects = new ObservableCollection<CgProject>(group.OrderBy(x => x.Name)) });
        CatalogCards.Clear();
        foreach(var project in Projects) CatalogCards.Add(new CgCatalogCard { Project=project, Category=ProjectCategory(project), Thumbnail=CreateCatalogThumbnail(project) });
        // Loading/reloading the catalog must not silently arm the first graphic. An
        // operator selection is required before PLAY PREVIEW or PLAY PROGRAM can run.
        SelectedProject = selectedId == Guid.Empty ? null : Projects.FirstOrDefault(x => x.Id == selectedId);
        SelectedCatalogCard = SelectedProject is null ? null : CatalogCards.FirstOrDefault(x=>x.Project.Id==SelectedProject.Id);
    }
    private static string ProjectCategory(CgProject project)
    {
        if (project.IsImported || string.Equals(project.Category, "IMPORTED", StringComparison.OrdinalIgnoreCase))
            return "IMPORTED";

        var cat = (project.Category ?? string.Empty).Trim().ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(cat) && !cat.Equals("CUSTOM", StringComparison.OrdinalIgnoreCase) && !cat.Equals("GENERAL", StringComparison.OrdinalIgnoreCase))
            return cat;

        var n = (project.Name ?? string.Empty).ToUpperInvariant();
        if (n.Contains("AP1") || n.Contains("AP 1")) return "AP1 HD";
        if (n.Contains("PRIME")) return "PRIME HD";
        if (n.Contains("SPACE")) return "SPACE 4K";
        if (n.Contains("AAJ") || n.Contains("TAK")) return "AAJ TAK";
        if (n.Contains("ABP")) return "ABP NEWS";
        if (n.Contains("INDIA") || n.Contains("TV")) return "INDIA TV";
        if (n.Contains("BREAKING")) return "BREAKING NEWS";
        if (n.Contains("WEATHER")) return "WEATHER";
        if (n.Contains("SPORT") || n.Contains("CRICKET") || n.Contains("FOOTBALL") || n.Contains("MATCH")) return "SPORTS";
        if (n.Contains("FINANCE") || n.Contains("STOCK") || n.Contains("MARKET") || n.Contains("SENSEX") || n.Contains("CRYPTO")) return "FINANCE";
        if (n.Contains("ELECTION") || n.Contains("VOTE") || n.Contains("POLL")) return "ELECTION";
        if (n.Contains("MUSIC") || n.Contains("SONG") || n.Contains("TRACK")) return "MUSIC";
        if (n.Contains("TICKER")) return "TICKERS";
        if (n.Contains("LOWER THIRD") || n.Contains("STRAP") || n.Contains("NAME CARD") || n.Contains("SUPER")) return "LOWER THIRDS";
        if (n.Contains("GRAPHICS PACK") || n.Contains("GFX PACK") || n.Contains("PACK ")) return "GRAPHICS PACK";

        return !string.IsNullOrWhiteSpace(project.Category) ? project.Category.ToUpperInvariant() : "BROADCAST PACK";
    }

    private void QuickColorSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string hex })
        {
            QuickEditFillValue = hex;
        }
    }

    private void QuickBgSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string hex })
        {
            QuickEditBgValue = hex;
        }
    }

    private void ApplyQuickEditToLayer()
    {
        if (SelectedLayer is null || SelectedProject is null) return;

        if (IsTextLayer)
        {
            SelectedLayer.Text = QuickEditTextValue;
            SelectedLayer.Fill = QuickEditFillValue;
            SelectedLayer.Background = QuickEditBgValue;

            if (double.TryParse(QuickEditFontSizeValue, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var fs) && fs > 0)
                SelectedLayer.FontSize = fs;
        }
        else if (IsShapeLayer)
        {
            SelectedLayer.Background = QuickEditBgValue;
            SelectedLayer.BorderColor = QuickEditBorderColorValue;

            if (double.TryParse(QuickEditBorderWidthValue, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var bw))
                SelectedLayer.BorderWidth = Math.Max(0, bw);

            if (double.TryParse(QuickEditCornerRadiusValue, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var cr))
                SelectedLayer.CornerRadius = Math.Max(0, cr);
        }
        else if (IsMediaLayer)
        {
            if (!string.IsNullOrWhiteSpace(QuickEditSourceValue))
                SelectedLayer.Source = QuickEditSourceValue;
        }

        // Common transform properties
        if (double.TryParse(QuickEditWidthValue, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var w) && w > 0)
            SelectedLayer.Width = w;

        if (double.TryParse(QuickEditHeightValue, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var h) && h > 0)
            SelectedLayer.Height = h;

        if (double.TryParse(QuickEditOpacityValue, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var op))
            SelectedLayer.Opacity = Math.Clamp(op, 0.0, 1.0);

        if (double.TryParse(QuickEditXValue, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var x))
            SelectedLayer.X = x;

        if (double.TryParse(QuickEditYValue, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var y))
            SelectedLayer.Y = y;

        try
        {
            _vm.SaveCgProjects();
        }
        catch { }

        try
        {
            var demoDir = CgDemoFactory.GetCgDemoDirectory();
            var sanitized = string.Concat(SelectedProject.Name.Split(System.IO.Path.GetInvalidFileNameChars())).Trim();
            if (!string.IsNullOrEmpty(sanitized))
            {
                var filePath = System.IO.Path.Combine(demoDir, sanitized + ".kcg");
                KashtrixCgFileService.SaveComposition(filePath, SelectedProject);
            }
        }
        catch { }

        LoadLayers();
        LastOperation = $"UPDATED '{SelectedLayer.Name}'";
        LastDetail = $"Saved edits to layer '{SelectedLayer.Name}' in project '{SelectedProject.Name}'.";
    }

    private void QuickEditSave_Click(object sender, RoutedEventArgs e)
    {
        ApplyQuickEditToLayer();
    }

    private async void QuickEditSaveAndPreview_Click(object sender, RoutedEventArgs e)
    {
        ApplyQuickEditToLayer();
        if (SelectedProject is not null)
        {
            var snapshot = BuildFullProjectSnapshot(SelectedProject);
            if (await RouteAsync("PLAY", "PREVIEW", snapshot))
            {
                snapshot.OnAir = true;
                _previewSnapshot = snapshot;
                _previewStartedUtc = DateTime.UtcNow;
                CopyProjectToPreviewMonitor(snapshot);
            }
        }
    }

    private async void QuickEditSaveAndProgram_Click(object sender, RoutedEventArgs e)
    {
        ApplyQuickEditToLayer();
        if (SelectedProject is not null)
        {
            var snapshot = BuildFullProjectSnapshot(SelectedProject);
            var targetLayer = CurrentOverlayNumber;
            var existingLayer = _programLayerSnapshots.FirstOrDefault(kv =>
                kv.Value.Project.Id == SelectedProject.Id ||
                string.Equals(kv.Value.Project.Name, SelectedProject.Name, StringComparison.OrdinalIgnoreCase)).Key;

            if (existingLayer > 0)
            {
                targetLayer = existingLayer;
                CurrentOverlayNumber = targetLayer;
            }
            else if (_programLayerSnapshots.TryGetValue(targetLayer, out var occupant) &&
                     occupant.Project is not null &&
                     occupant.Project.Id != SelectedProject.Id &&
                     !string.Equals(occupant.Project.Name, SelectedProject.Name, StringComparison.OrdinalIgnoreCase))
            {
                var freeLayer = 1;
                while (_programLayerSnapshots.ContainsKey(freeLayer)) freeLayer++;
                targetLayer = freeLayer;
                if (!ActiveOverlayLayers.Contains(targetLayer)) ActiveOverlayLayers.Add(targetLayer);
                CurrentOverlayNumber = targetLayer;
            }

            if (await RouteAsync("PLAY", "PROGRAM", snapshot, layerOverride: targetLayer))
            {
                snapshot.OnAir = true;
                snapshot.ExternalLayer = targetLayer;
                _programPreviousSnapshot = null;
                _programTransitionMode = "None";
                _programSnapshot = snapshot;
                _programStartedUtc = DateTime.UtcNow;
                _programLayerSnapshots[targetLayer] = (snapshot, DateTime.UtcNow);
                CopyProjectToProgramMonitor(snapshot);
                _cgOutput.SetProgramLayer(targetLayer, snapshot);
            }
        }
    }
    private void CatalogTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if(e.NewValue is CgProject project)
        {
            SelectedProject=project;
            SelectedCatalogCard=CatalogCards.FirstOrDefault(x=>x.Project.Id==project.Id);
        }
        else if(e.NewValue is CgCatalogGroup group && group.Projects.Count > 0)
        {
            // A category node is navigation, not a CG item selection.
            SelectedProject = null;
            SelectedCatalogCard = null;
        }
    }
    private void ImportCg_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Import Kashtrix CG Composition",
                Filter = "Kashtrix CG Files (*.kcg;*.kashtrixcg;*.cgx)|*.kcg;*.kashtrixcg;*.cgx|All Supported CG Files (*.kcg;*.kashtrixcg;*.cgx;*.json)|*.kcg;*.kashtrixcg;*.cgx;*.json",
                Multiselect = true
            };
            if (dlg.ShowDialog() != true) return;

            int importedCount = 0;
            foreach (var file in dlg.FileNames)
            {
                var project = KashtrixCgFileService.LoadComposition(file);
                if (project != null)
                {
                    if (string.IsNullOrWhiteSpace(project.Name))
                        project.Name = System.IO.Path.GetFileNameWithoutExtension(file);
                    project.Id = Guid.NewGuid();
                    project.IsImported = true;
                    project.Category = "IMPORTED";

                    var existing = _vm.CgProjects.FirstOrDefault(x => x.Name.Equals(project.Name, StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                    {
                        project.Name = $"{project.Name} (Imported)";
                    }

                    _vm.CgProjects.Add(project);
                    importedCount++;
                }
            }

            if (importedCount > 0)
            {
                _vm.SaveCgProjects();
                ReloadProjects();
                LastOperation = $"IMPORTED {importedCount} CG PROJECT(S)";
                LastDetail = $"Successfully imported {importedCount} graphic composition(s) from CG Editor.";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to import CG project: {ex.Message}", "Kashtrix Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    private void ReloadCatalog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _vm.RebuildCgDemoPack();
            _vm.SaveCgProjects();
            ReloadProjects();
            LastOperation = "CATALOG RELOADED";
            LastDetail = $"Reloaded {Projects.Count} CG templates across {CatalogGroups.Count} categories from latest definitions.";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to reload catalog: {ex.Message}", "Kashtrix CG", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
    private void CatalogList_Click(object sender,RoutedEventArgs e){_catalogViewMode="List";Raise(nameof(CatalogListVisibility));Raise(nameof(CatalogGridVisibility));}
    private void CatalogGrid_Click(object sender,RoutedEventArgs e){_catalogViewMode="Grid";Raise(nameof(CatalogListVisibility));Raise(nameof(CatalogGridVisibility));}
    private static ImageSource CreateCatalogThumbnail(CgProject project)
    {
        const double width=320,height=180;
        var visual=new DrawingVisual();
        using(var dc=visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(4,8,13)),null,new Rect(0,0,width,height));
            var sx=width/Math.Max(1,project.Width); var sy=height/Math.Max(1,project.Height);
            foreach(var layer in (project.Layers??[]).Where(x=>x.Visible).Take(18))
            {
                var colorText=layer.Type.Equals("Shape",StringComparison.OrdinalIgnoreCase)?layer.Background:layer.Fill;
                Brush brush;
                try{brush=(Brush)new BrushConverter().ConvertFromString(colorText??"#667B3EFF")!;}catch{brush=new SolidColorBrush(Color.FromArgb(150,123,62,255));}
                var x=double.IsFinite(layer.X)?layer.X*sx:0;var y=double.IsFinite(layer.Y)?layer.Y*sy:0;
                var w=double.IsFinite(layer.Width)?Math.Max(2,layer.Width*sx):20;var h=double.IsFinite(layer.Height)?Math.Max(2,layer.Height*sy):12;
                dc.PushOpacity(double.IsFinite(layer.Opacity)?Math.Clamp(layer.Opacity,.15,1):1);
                dc.DrawRectangle(brush,null,new Rect(x,y,w,h));dc.Pop();
            }
            dc.DrawRectangle(null,new Pen(new SolidColorBrush(Color.FromRgb(62,74,86)),1),new Rect(.5,.5,width-1,height-1));
        }
        var bitmap=new RenderTargetBitmap((int)width,(int)height,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);bitmap.Freeze();return bitmap;
    }

    private void LoadLayers()
    {
        Layers.Clear();
        if (SelectedProject is not null)
        {
            foreach (var l in SelectedProject.Layers)
                Layers.Add(l);
        }
        SelectedLayer = Layers.FirstOrDefault();
    }
    private void LoadRegistryChannels()
    {
        Channels.Clear();
        foreach (var c in ChannelRegistryStore.Load().Where(x => x.Enabled))
            Channels.Add(new CgTargetRow { ChannelId = c.ChannelId, ChannelName = c.ChannelName, Format = c.Format, State = "REGISTERED", IsSelected = c.ChannelId.Equals(_vm.ChannelId, StringComparison.OrdinalIgnoreCase) });

        // A single-workstation operator must always be able to target the local Playout
        // even before a Channel Controller registry has been configured.
        if (!Channels.Any(x => x.ChannelId.Equals(_vm.ChannelId, StringComparison.OrdinalIgnoreCase)))
            Channels.Insert(0, new CgTargetRow { ChannelId = _vm.ChannelId, ChannelName = _vm.ChannelName, Format = _vm.PlayoutPreset, State = "LOCAL PLAYOUT", IsSelected = true });
        else if (!Channels.Any(x => x.IsSelected))
            Channels.First(x => x.ChannelId.Equals(_vm.ChannelId, StringComparison.OrdinalIgnoreCase)).IsSelected = true;
    }
    private async Task ConnectAndRefreshAsync()
    {
        try
        {
            _operator?.Dispose();
            _operator = new ChannelControllerOperator();
            ConnectionStatus = "CONNECTING";
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await _operator.ConnectAsync(_vm.ControllerHost, _vm.ControllerPort, cts.Token);
            ConnectionStatus = "CONNECTED";
            var online = await _operator.GetChannelsAsync(cts.Token);
            Channels.Clear();
            foreach (var c in online.OrderBy(x => x.ChannelName))
                Channels.Add(new CgTargetRow { ChannelId=c.ChannelId, ChannelName=c.ChannelName, Format=c.Format, State=c.State, IsSelected=true });
            if (!Channels.Any(x => x.ChannelId.Equals(_vm.ChannelId, StringComparison.OrdinalIgnoreCase)))
                Channels.Insert(0, new CgTargetRow { ChannelId=_vm.ChannelId, ChannelName=_vm.ChannelName, Format=_vm.PlayoutPreset, State="LOCAL PLAYOUT", IsSelected=true });
            LastDetail = online.Count == 0 ? "Controller connected; local Playout remains available through direct CG transport." : $"Connected. {online.Count} network playout client(s) online; local direct transport is also available.";
        }
        catch (Exception ex)
        {
            ConnectionStatus = "OFFLINE / REGISTRY ONLY";
            _operator?.Dispose(); _operator = null;
            if (Channels.Count == 0) LoadRegistryChannels();
            LastDetail = "Channel Controller is not reachable: " + ex.Message;
        }
    }

    private async Task<bool> RouteAsync(string action, string bus, CgProject? projectOverride = null, string transition = "None", double transitionSeconds = 0.45, int? layerOverride = null, bool targetAll = false)
    {
        var targets = Channels.Where(x => x.IsSelected).Select(x => x.ChannelId)
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (targets.Length == 0) targets = [_vm.ChannelId];

        CgProject? quickProject = null;
        if (projectOverride is not null)
        {
            quickProject = projectOverride;
        }
        else if (!action.Equals("STOP", StringComparison.OrdinalIgnoreCase) && !action.Equals("CLEAR", StringComparison.OrdinalIgnoreCase))
        {
            if (SelectedProject is null)
            {
                LastOperation = $"{bus} {action} NOT SENT";
                LastDetail = "Select a graphic project.";
                return false;
            }
            quickProject = BuildFullProjectSnapshot(SelectedProject);
        }
        else if (!targetAll && (action.Equals("STOP", StringComparison.OrdinalIgnoreCase) || action.Equals("CLEAR", StringComparison.OrdinalIgnoreCase)) && SelectedProject is not null)
        {
            quickProject = SelectedProject;
        }

        var command = new CgRemoteCommand
        {
            CommandId = Guid.NewGuid().ToString("N"),
            Action = action,
            Bus = bus,
            Layer = targetAll ? -1 : layerOverride ?? CurrentOverlayNumber,
            Project = quickProject,
            Transition = string.IsNullOrWhiteSpace(transition) ? "None" : transition,
            TransitionSeconds = Math.Clamp(transitionSeconds, .05, 5.0)
        };
        var localDelivered = 0;
        Exception? localError = null;
        try { localDelivered = LocalCgCommandBus.Publish(targets, command); }
        catch (Exception ex) { localError = ex; }

        var networkDelivered = false;
        Exception? networkError = null;
        if (_operator is not null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _operator.SendCgAsync(targets, command, cts.Token);
                networkDelivered = true;
            }
            catch (Exception ex) { networkError = ex; }
        }

        if (localDelivered > 0 || networkDelivered)
        {
            ConnectionStatus = networkDelivered ? (localDelivered > 0 ? "NETWORK + LOCAL" : "NETWORK") : "LOCAL DIRECT";
            LastOperation = $"{bus} · {action} · {targets.Length} TARGET(S)";
            var transitionText = !string.Equals(command.Transition, "None", StringComparison.OrdinalIgnoreCase)
                ? $" · {command.Transition} {command.TransitionSeconds:0.00}s"
                : string.Empty;
            LastDetail = action is "STOP" or "CLEAR"
                ? $"{bus} CG clear sent ({ConnectionStatus})."
                : $"{quickProject!.Name} sent to {bus} on {string.Join(", ", targets)} ({ConnectionStatus}){transitionText}.";
            Raise(nameof(StatusLine));
            AuditLogService.Write("CG_COMMAND", quickProject?.Name ?? "CG", $"{bus} {action} -> {string.Join(", ", targets)} · {ConnectionStatus}");
            RuntimeDiagnosticsService.Write("CG_ROUTE", LastOperation, new { bus, action, targets, project = quickProject?.Name ?? "", connection = ConnectionStatus });
            return true;
        }

        LastOperation = $"{bus} {action} FAILED";
        LastDetail = networkError?.Message ?? localError?.Message ?? "No target accepted the CG command.";
        ConnectionStatus = "ERROR";
        AuditLogService.Write("CG_COMMAND_ERROR", quickProject?.Name ?? "CG", $"{bus} {action}: {LastDetail}");
        RuntimeDiagnosticsService.Write("CG_ROUTE_ERROR", LastOperation, new { bus, action, targets, project = quickProject?.Name ?? "", error = LastDetail });
        return false;
    }

    private static CgProject BuildFullProjectSnapshot(CgProject source)
    {
        // CG Controller PLAY is template/project based, not layer based. Clone the complete
        // project so every authored layer, group, keyframe, data binding and HTML source
        // travels together to PREVIEW/PROGRAM while leaving the editor/database object untouched.
        CgProject snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<CgProject>(JsonSerializer.Serialize(source))
                       ?? throw new InvalidOperationException("CG project clone returned null.");
        }
        catch
        {
            // The CG model is JSON-serializable; this fallback keeps routing available if a
            // future optional property cannot be serialized. Collection references are only
            // read by the compositor/transport in this path.
            snapshot = new CgProject
            {
                Id = source.Id,
                Name = source.Name,
                Width = source.Width,
                Height = source.Height,
                FrameRate = source.FrameRate,
                DurationSeconds = source.DurationSeconds,
                Layers = source.Layers,
                Groups = source.Groups,
                DataSources = source.DataSources,
                HtmlSources = source.HtmlSources
            };
        }

        snapshot.OnAir = true;
        var lastLayerEnd = snapshot.Layers.Where(x => x.Visible).Select(x => x.EndSeconds).DefaultIfEmpty(0.5).Max();
        snapshot.DurationSeconds = Math.Max(snapshot.DurationSeconds, Math.Max(0.5, lastLayerEnd));

        // For ticker projects with an excessively large DurationSeconds (e.g. 3600),
        // auto-compute a compact value. The compositor manages ticker content via
        // 3-phase lifecycle (in-animation → hold/ticker-cycle → out-animation); the
        // project DurationSeconds only needs to cover the non-ticker layer animations.
        var hasTicker = snapshot.Layers.Any(l => l.Visible &&
            string.Equals(l.Type, "Ticker", StringComparison.OrdinalIgnoreCase));
        if (hasTicker)
        {
            var tickerCycleDuration = CgDataSourceService.ResolveEffectiveTickerCycleDuration(snapshot);
            if (tickerCycleDuration > 0.5)
            {
                var maxOut = snapshot.Layers.Where(x => x.Visible)
                    .Select(x => x.AnimationOutSeconds).DefaultIfEmpty(0.5).Max();
                var holdPt = CgDataSourceService.ResolveEffectiveHoldPoint(snapshot);
                var compact = Math.Max(2.0, holdPt + tickerCycleDuration + maxOut + 0.5);
                if (snapshot.DurationSeconds > compact * 3)
                {
                    snapshot.DurationSeconds = Math.Max(2.0, compact);
                }
            }
            else if (snapshot.DurationSeconds > 120)
            {
                // Data not loaded yet — reduce to a reasonable value.
                var maxIn = snapshot.Layers.Where(l => l.Visible)
                    .Select(l => l.StartSeconds + Math.Max(0.3, l.AnimationInSeconds)).DefaultIfEmpty(1.0).Max();
                snapshot.DurationSeconds = Math.Max(2.0, maxIn + 1.0);
            }
        }

        return snapshot;
    }

    private async void Connect_Click(object s, RoutedEventArgs e) => await ConnectAndRefreshAsync();
    private async void Refresh_Click(object s, RoutedEventArgs e) { ReloadProjects(); await ConnectAndRefreshAsync(); }
    private async void PreviewPlay_Click(object s, RoutedEventArgs e)
    {
        if (SelectedProject is null)
        {
            LastOperation = "PREVIEW PLAY NOT SENT";
            LastDetail = "Select a graphic project.";
            return;
        }

        var snapshot = BuildFullProjectSnapshot(SelectedProject);
        snapshot.OnAir = true;
        _previewSnapshot = snapshot;
        _previewStartedUtc = DateTime.UtcNow;
        CopyProjectToPreviewMonitor(snapshot);

        if (!await RouteAsync("PLAY", "PREVIEW", snapshot))
        {
            _previewSnapshot = null;
            PreviewCgImage.Source = null;
            _previewMonitorBitmap = null;
        }
    }
    private async void PreviewStop_Click(object s, RoutedEventArgs e)
    {
        var targetProj = SelectedProject;
        await RouteAsync("STOP", "PREVIEW", targetProj);
        LastOperation = "PREVIEW STOP (OUT ANIMATION)";
        LastDetail = targetProj != null
            ? $"Triggered out animation for {targetProj.Name} in Preview."
            : "Triggered out animation for active Preview graphics.";
        if (_previewSnapshot != null && (targetProj == null || _previewSnapshot.Id == targetProj.Id || string.Equals(_previewSnapshot.Name, targetProj.Name, StringComparison.OrdinalIgnoreCase)))
        {
            _previewSnapshot.IsStopping = true;
            _previewSnapshot.StopRequestedTimelineSeconds = Math.Max(0, (DateTime.UtcNow - _previewStartedUtc).TotalSeconds);
            var maxOut = _previewSnapshot.Layers.Where(l => l.Visible).Select(l => l.AnimationOutSeconds).DefaultIfEmpty(0.0).Max();
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(maxOut + 0.15)).ConfigureAwait(false);
                await Dispatcher.InvokeAsync(() =>
                {
                    _previewSnapshot = null;
                    Interlocked.Exchange(ref _latestPlayoutPreviewFrame, null);
                    PreviewCgImage.Source = null;
                    _previewMonitorBitmap = null;
                    PreviewProjectName = "No CG in preview";
                    PreviewLayerName = "PREVIEW STOPPED";
                    PreviewLayerText = "Preview CG stopped.";
                });
            });
        }
    }
    private async void PreviewClear_Click(object s, RoutedEventArgs e)
    {
        if (await RouteAsync("CLEAR", "PREVIEW"))
        {
            _previewSnapshot = null;
            Interlocked.Exchange(ref _latestPlayoutPreviewFrame, null);
            PreviewCgImage.Source = null;
            _previewMonitorBitmap = null;
            PreviewProjectName = "No CG in preview";
            PreviewLayerName = "PREVIEW CLEAR";
            PreviewLayerText = "Preview CG bus is clear.";
        }
    }
    private async void ProgramPlay_Click(object s, RoutedEventArgs e)
    {
        if (SelectedProject is null) return;
        var snapshot = BuildFullProjectSnapshot(SelectedProject);

        var targetLayer = CurrentOverlayNumber;
        var existingLayer = _programLayerSnapshots.FirstOrDefault(kv =>
            kv.Value.Project.Id == SelectedProject.Id ||
            string.Equals(kv.Value.Project.Name, SelectedProject.Name, StringComparison.OrdinalIgnoreCase)).Key;

        if (existingLayer > 0)
        {
            targetLayer = existingLayer;
            CurrentOverlayNumber = targetLayer;
        }
        else if (_programLayerSnapshots.TryGetValue(targetLayer, out var occupant) &&
                 occupant.Project is not null &&
                 occupant.Project.Id != SelectedProject.Id &&
                 !string.Equals(occupant.Project.Name, SelectedProject.Name, StringComparison.OrdinalIgnoreCase))
        {
            var freeLayer = 1;
            while (_programLayerSnapshots.ContainsKey(freeLayer)) freeLayer++;
            targetLayer = freeLayer;
            if (!ActiveOverlayLayers.Contains(targetLayer)) ActiveOverlayLayers.Add(targetLayer);
            CurrentOverlayNumber = targetLayer;
        }

        snapshot.OnAir = true;
        snapshot.ExternalLayer = targetLayer;
        _programPreviousSnapshot = null;
        _programTransitionMode = "None";
        _programSnapshot = snapshot;
        _programStartedUtc = DateTime.UtcNow;
        _programLayerSnapshots[targetLayer] = (snapshot, DateTime.UtcNow);
        CopyProjectToProgramMonitor(snapshot);
        _cgOutput.SetProgramLayer(targetLayer, snapshot);

        if (!await RouteAsync("PLAY", "PROGRAM", snapshot, layerOverride: targetLayer))
        {
            _programLayerSnapshots.Remove(targetLayer);
            _cgOutput.RemoveProgramLayer(targetLayer);
            if (_programSnapshot == snapshot)
            {
                _programSnapshot = _programLayerSnapshots.Count > 0 ? _programLayerSnapshots.Values.Last().Project : null;
            }
            if (_programSnapshot == null)
            {
                _cgOutput.ClearProgram();
            }
        }
    }
    private async void ProgramStop_Click(object s, RoutedEventArgs e)
    {
        var targetLayer = CurrentOverlayNumber;
        CgProject? targetProj = null;

        // If a project is selected that matches an active program overlay, target that specific layer
        if (SelectedProject is not null)
        {
            var match = _programLayerSnapshots.FirstOrDefault(kv =>
                kv.Value.Project.Id == SelectedProject.Id ||
                string.Equals(kv.Value.Project.Name, SelectedProject.Name, StringComparison.OrdinalIgnoreCase));
            if (match.Value.Project is not null)
            {
                targetLayer = match.Key;
                targetProj = match.Value.Project;
            }
        }

        if (targetProj is null && _programLayerSnapshots.TryGetValue(targetLayer, out var active))
        {
            targetProj = active.Project;
        }
        else if (targetProj is null && SelectedProject is not null)
        {
            targetProj = SelectedProject;
        }

        await RouteAsync("STOP", "PROGRAM", targetProj, layerOverride: targetLayer);
        LastOperation = "PROGRAM STOP (OUT ANIMATION)";
        LastDetail = targetProj != null
            ? $"Triggered out animation for {targetProj.Name} (Overlay {targetLayer})."
            : $"Triggered out animation for active Program graphics (Overlay {targetLayer}).";

        if (_programLayerSnapshots.TryGetValue(targetLayer, out var stopping))
        {
            stopping.Project.IsStopping = true;
            stopping.Project.StopRequestedTimelineSeconds = Math.Max(0, (DateTime.UtcNow - stopping.StartedUtc).TotalSeconds);
            var maxOut = stopping.Project.Layers.Where(l => l.Visible).Select(l => l.AnimationOutSeconds).DefaultIfEmpty(0.5).Max();
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(maxOut + 0.15)).ConfigureAwait(false);
                await Dispatcher.InvokeAsync(() =>
                {
                    _programLayerSnapshots.Remove(targetLayer);
                    _cgOutput.RemoveProgramLayer(targetLayer);
                    // Only stop the specific layer. If other layers remain on program, keep them on-air!
                    if (_programLayerSnapshots.Count > 0)
                    {
                        _programSnapshot = _programLayerSnapshots.Values.Last().Project;
                        ProgramProjectName = _programSnapshot.Name;
                        ProgramLayerName = $"OVERLAY {_programSnapshot.ExternalLayer} ACTIVE";
                        ProgramLayerText = $"{_programLayerSnapshots.Count} layer(s) active on program.";
                    }
                    else
                    {
                        _programSnapshot = null;
                        ProgramCgImage.Source = null;
                        _programMonitorBitmap = null;
                        ProgramProjectName = "No CG on program";
                        ProgramLayerName = "PROGRAM CLEAR";
                        ProgramLayerText = "Program CG bus is clear.";
                        _cgOutput.ClearProgram();
                    }
                });
            });
        }
        else if (_programSnapshot != null && (targetProj == null || _programSnapshot.Id == targetProj.Id || string.Equals(_programSnapshot.Name, targetProj.Name, StringComparison.OrdinalIgnoreCase)))
        {
            _programSnapshot.IsStopping = true;
            _programSnapshot.StopRequestedTimelineSeconds = Math.Max(0, (DateTime.UtcNow - _programStartedUtc).TotalSeconds);
        }
    }
    private async void ProgramUpdate_Click(object s, RoutedEventArgs e)
    {
        if (SelectedProject is null) return;
        var snapshot = BuildFullProjectSnapshot(SelectedProject);
        var targetLayer = CurrentOverlayNumber;
        var existingLayer = _programLayerSnapshots.FirstOrDefault(kv =>
            kv.Value.Project.Id == SelectedProject.Id ||
            string.Equals(kv.Value.Project.Name, SelectedProject.Name, StringComparison.OrdinalIgnoreCase)).Key;
        if (existingLayer > 0) targetLayer = existingLayer;

        if (await RouteAsync("UPDATE", "PROGRAM", snapshot, layerOverride: targetLayer))
        {
            snapshot.OnAir = true;
            snapshot.ExternalLayer = targetLayer;
            _programPreviousSnapshot = null;
            _programTransitionMode = "None";
            _programTransitionStartedUtc = DateTime.MinValue;
            _programSnapshot = snapshot;
            if (_programStartedUtc == default) _programStartedUtc = DateTime.UtcNow;
            _programLayerSnapshots[targetLayer] = (snapshot, _programStartedUtc);
            CopyProjectToProgramMonitor(snapshot);
            _cgOutput.SetProgramLayer(targetLayer, snapshot);
        }
    }
    private async void ProgramClear_Click(object s, RoutedEventArgs e)
    {
        // CLEAR is the emergency/instant bus clear: remove every CG layer. STOP above
        // remains the scoped operation and only animates the selected/current item out.
        if (await RouteAsync("CLEAR", "PROGRAM", layerOverride: -1, targetAll: true))
        {
            _programLayerSnapshots.Clear();
            _programSnapshot = null;
            _programPreviousSnapshot = null;
            _programTransitionMode = "None";
            _programTransitionStartedUtc = DateTime.MinValue;
            ProgramCgImage.Source = null;
            _programMonitorBitmap = null;
            ProgramProjectName = "No CG on program";
            ProgramLayerName = "PROGRAM CLEAR";
            ProgramLayerText = "All Program CG layers are clear.";
            _cgOutput.ClearProgram();
            LastOperation = "PROGRAM BUS CLEARED";
            LastDetail = "All CG items and overlay layers were cleared instantly.";
        }
    }
    private async void TakeToProgram_Click(object s, RoutedEventArgs e) => await TakePreviewToProgramAsync(TakeTransition);
    private async void CutToProgram_Click(object s, RoutedEventArgs e) => await TakePreviewToProgramAsync("None");
    private async void AutoToProgram_Click(object s, RoutedEventArgs e)
    {
        var transition=string.IsNullOrWhiteSpace(TakeTransition)||TakeTransition.Equals("None",StringComparison.OrdinalIgnoreCase)?"Fade":TakeTransition;
        await TakePreviewToProgramAsync(transition);
    }
    private async Task TakePreviewToProgramAsync(string? requestedTransition)
    {
        if (_previewSnapshot is null)
        {
            LastOperation = "TAKE NOT SENT";
            LastDetail = "PLAY PREVIEW first. TAKE always transfers the latched preview composition to PROGRAM.";
            return;
        }
        var transition = string.IsNullOrWhiteSpace(requestedTransition) ? "None" : requestedTransition;
        var transitionSeconds = Math.Clamp(TakeTransitionSeconds, .05, 5.0);

        var targetLayer = CurrentOverlayNumber;
        var existingLayer = _programLayerSnapshots.FirstOrDefault(kv =>
            kv.Value.Project.Id == _previewSnapshot.Id ||
            string.Equals(kv.Value.Project.Name, _previewSnapshot.Name, StringComparison.OrdinalIgnoreCase)).Key;

        if (existingLayer > 0)
        {
            targetLayer = existingLayer;
            CurrentOverlayNumber = targetLayer;
        }
        else if (_programLayerSnapshots.TryGetValue(targetLayer, out var occupant) &&
                 occupant.Project is not null &&
                 occupant.Project.Id != _previewSnapshot.Id &&
                 !string.Equals(occupant.Project.Name, _previewSnapshot.Name, StringComparison.OrdinalIgnoreCase))
        {
            var freeLayer = 1;
            while (_programLayerSnapshots.ContainsKey(freeLayer)) freeLayer++;
            targetLayer = freeLayer;
            if (!ActiveOverlayLayers.Contains(targetLayer)) ActiveOverlayLayers.Add(targetLayer);
            CurrentOverlayNumber = targetLayer;
        }

        if (await RouteAsync("TAKE", "PROGRAM", _previewSnapshot, transition, transitionSeconds, layerOverride: targetLayer))
        {
            _previewSnapshot.OnAir = true;
            _previewSnapshot.ExternalLayer = targetLayer;
            _programLayerSnapshots[targetLayer] = (_previewSnapshot, DateTime.UtcNow);
            _programPreviousSnapshot = !transition.Equals("None", StringComparison.OrdinalIgnoreCase) ? _programSnapshot : null;
            _programSnapshot = _previewSnapshot; _programStartedUtc = DateTime.UtcNow;
            _programTransitionMode = transition; _programTransitionSeconds = transitionSeconds;
            _programTransitionStartedUtc = transition.Equals("None", StringComparison.OrdinalIgnoreCase) ? DateTime.MinValue : DateTime.UtcNow;
            CopyProjectToProgramMonitor(_previewSnapshot);
            _cgOutput.SetProgramLayer(targetLayer, _previewSnapshot, transition, transitionSeconds);
            LastOperation = transition.Equals("None", StringComparison.OrdinalIgnoreCase) ? "CUT PREVIEW -> PROGRAM" : $"TAKE PREVIEW -> PROGRAM · {transition.ToUpperInvariant()}";
        }
    }
    private void CopyProjectToPreviewMonitor(CgProject project)
    {
        var visible = project.Layers.Where(x => x.Visible).ToArray();
        PreviewProjectName = project.Name;
        PreviewLayerName = $"ALL LAYERS · {visible.Length}";
        PreviewLayerText = visible.Length == 0 ? "No visible native layers" : string.Join(" · ", visible.Take(4).Select(x => x.Name)) + (visible.Length > 4 ? " …" : string.Empty);
    }
    private void CopyProjectToProgramMonitor(CgProject project)
    {
        var visible = project.Layers.Where(x => x.Visible).ToArray();
        ProgramProjectName = project.Name;
        ProgramLayerName = $"ALL LAYERS · {visible.Length}";
        ProgramLayerText = visible.Length == 0 ? "No visible native layers" : string.Join(" · ", visible.Take(4).Select(x => x.Name)) + (visible.Length > 4 ? " …" : string.Empty);
    }

    private void ApplyCgOutput_Click(object s, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_vm.Settings.CgNdiAlphaSourceName)) _vm.Settings.CgNdiAlphaSourceName = "KASHTRIX-CG-ALPHA";
        _vm.Settings.CgDeckLinkKeyerLevel = Math.Clamp(_vm.Settings.CgDeckLinkKeyerLevel, 0, 255);
        _vm.SaveSettings();
        _cgOutput.Configure(_vm.Settings);
        if (_programLayerSnapshots.Count > 0)
        {
            foreach (var kvp in _programLayerSnapshots)
                _cgOutput.SetProgramLayer(kvp.Key, kvp.Value.Project);
        }
        else if (_programSnapshot is not null)
        {
            _cgOutput.SetProgram(_programSnapshot);
        }
        LastOperation = "CG OUTPUT APPLIED";
        LastDetail = $"{CgOutputSummary} · {CgDeckLinkDeviceName} · NDI alpha {(EnableCgNdiAlphaOutput ? "ON" : "OFF")} · DeckLink key/fill {(EnableCgDeckLinkKeyFill ? CgDeckLinkKeyerMode.ToUpperInvariant() : "OFF")}.";
    }

    private void RefreshDeckLinkCards_Click(object s, RoutedEventArgs e)
    {
        var current = _vm.Settings.CgDeckLinkDeviceName;
        _cgDeckLinkDevices = DeckLinkOutputAdapter.EnumerateDevices();
        var selected = _cgDeckLinkDevices.FirstOrDefault(x => !string.IsNullOrWhiteSpace(current) && x.PersistentName.Equals(current, StringComparison.OrdinalIgnoreCase))
                       ?? _cgDeckLinkDevices.FirstOrDefault();
        if (selected is not null)
        {
            _vm.Settings.CgDeckLinkDeviceIndex = selected.Index;
            _vm.Settings.CgDeckLinkDeviceName = selected.PersistentName;
        }
        Raise(nameof(CgDeckLinkDevices)); Raise(nameof(CgDeckLinkDeviceIndex)); Raise(nameof(CgDeckLinkDeviceName));
        LastOperation = "DECKLINK CARDS REFRESHED";
        LastDetail = $"{_cgDeckLinkDevices.Count} card/port(s) discovered · {CgDeckLinkDeviceName}";
    }

    private void SelectAll_Click(object s, RoutedEventArgs e) { foreach (var c in Channels) c.IsSelected=true; Raise(nameof(StatusLine)); }
    private void Clear_Click(object s, RoutedEventArgs e) { foreach (var c in Channels) c.IsSelected=false; Raise(nameof(StatusLine)); }
    private void OpenDesigner_Click(object s, RoutedEventArgs e) { if (!StandaloneAppLauncher.Launch("CGEditor", out var error)) MessageBox.Show(error, "Kashtrix"); }

    private void ToggleControllerPanel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem item || item.Tag is not string panel) return;
        SetControllerPanelVisible(panel, item.IsChecked);
    }

    private void RestoreControllerPanels_Click(object sender, RoutedEventArgs e)
    {
        foreach (var panel in new[] { "Preview", "Switcher", "Program", "Catalog", "Layers", "Targets" })
            SetControllerPanelVisible(panel, true);
        PreviewPanelMenu.IsChecked = SwitcherPanelMenu.IsChecked = ProgramPanelMenu.IsChecked = true;
        CatalogPanelMenu.IsChecked = LayersPanelMenu.IsChecked = TargetsPanelMenu.IsChecked = true;
    }

    private void SetControllerPanelVisible(string panel, bool visible)
    {
        var show = visible ? Visibility.Visible : Visibility.Collapsed;
        switch (panel)
        {
            case "Preview":
                PreviewBusPanel.Visibility = show;
                PreviewColumn.MinWidth = visible ? 280 : 0;
                PreviewColumn.Width = visible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
                break;
            case "Switcher":
                SwitcherPanel.Visibility = show;
                SwitcherColumn.MinWidth = visible ? 190 : 0;
                SwitcherColumn.Width = visible ? new GridLength(245) : new GridLength(0);
                break;
            case "Program":
                ProgramBusPanel.Visibility = show;
                ProgramColumn.MinWidth = visible ? 280 : 0;
                ProgramColumn.Width = visible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
                break;
            case "Catalog":
                CatalogPanel.Visibility = show;
                CatalogColumn.MinWidth = visible ? 260 : 0;
                CatalogColumn.Width = visible ? new GridLength(1.35, GridUnitType.Star) : new GridLength(0);
                break;
            case "Layers":
                LayersPanel.Visibility = show;
                LayersColumn.MinWidth = visible ? 240 : 0;
                LayersColumn.Width = visible ? new GridLength(1.1, GridUnitType.Star) : new GridLength(0);
                break;
            case "Targets":
                TargetsPanel.Visibility = show;
                TargetsColumn.MinWidth = visible ? 260 : 0;
                TargetsColumn.Width = visible ? new GridLength(1.3, GridUnitType.Star) : new GridLength(0);
                break;
        }
        UpdateControllerPanelRows();
    }

    private void UpdateControllerPanelRows()
    {
        var topVisible = PreviewBusPanel.Visibility == Visibility.Visible ||
                         SwitcherPanel.Visibility == Visibility.Visible ||
                         ProgramBusPanel.Visibility == Visibility.Visible;
        var lowerVisible = CatalogPanel.Visibility == Visibility.Visible ||
                           LayersPanel.Visibility == Visibility.Visible ||
                           TargetsPanel.Visibility == Visibility.Visible;

        SwitcherGrid.Visibility = topVisible ? Visibility.Visible : Visibility.Collapsed;
        SwitcherRow.MinHeight = topVisible ? 380 : 0;
        SwitcherRow.Height = topVisible ? new GridLength(1.35, GridUnitType.Star) : new GridLength(0);
        ControllerRowSplitter.Height = topVisible && lowerVisible ? new GridLength(10) : new GridLength(0);

        LowerPanelsGrid.Visibility = lowerVisible ? Visibility.Visible : Visibility.Collapsed;
        LowerRow.MinHeight = lowerVisible ? 180 : 0;
        LowerRow.Height = lowerVisible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

        CatalogLayerGap.Width = CatalogPanel.Visibility == Visibility.Visible && LayersPanel.Visibility == Visibility.Visible
            ? new GridLength(8) : new GridLength(0);
        LayersTargetsGap.Width = LayersPanel.Visibility == Visibility.Visible && TargetsPanel.Visibility == Visibility.Visible
            ? new GridLength(8) : new GridLength(0);
    }

    private void TitleBar_MouseLeftButtonDown(object s, MouseButtonEventArgs e) => WindowChromeActions.Drag(this, e);
    private void Minimize_Click(object s, RoutedEventArgs e) => WindowState=WindowState.Minimized;
    private void Maximize_Click(object s, RoutedEventArgs e) => WindowChromeActions.ToggleMaximize(this);
    private void Close_Click(object s, RoutedEventArgs e) => Close();

    public event PropertyChangedEventHandler? PropertyChanged;
    #region CG Scheduler Engine
    private void CheckSchedulesTick()
    {
        if (Schedules.Count == 0) return;
        var now = DateTime.Now;
        var nowTime = now.TimeOfDay;

        foreach (var schedule in Schedules)
        {
            if (!schedule.Enabled)
            {
                if (schedule.Status != "DISABLED") schedule.Status = "DISABLED";
                continue;
            }

            if (!MatchesDay(schedule.Days, now.DayOfWeek))
            {
                if (schedule.Status != "OFF DAY") schedule.Status = "OFF DAY";
                continue;
            }

            if (TryParseScheduleTime(schedule.StartTime, out var startTime) &&
                TryParseScheduleTime(schedule.StopTime, out var stopTime))
            {
                bool inRange = stopTime > startTime
                    ? (nowTime >= startTime && nowTime < stopTime)
                    : (nowTime >= startTime || nowTime < stopTime);

                if (inRange)
                {
                    if (!schedule.IsPlaying)
                    {
                        schedule.IsPlaying = true;
                        schedule.Status = $"ON-AIR (Ends {schedule.StopTime})";
                        schedule.LastTriggerDate = now;
                        TriggerSchedulePlay(schedule);
                    }
                }
                else
                {
                    if (schedule.IsPlaying)
                    {
                        schedule.IsPlaying = false;
                        schedule.Status = $"COMPLETED ({schedule.StopTime})";
                        TriggerScheduleStop(schedule);
                    }
                    else if (schedule.Status.StartsWith("ON-AIR", StringComparison.OrdinalIgnoreCase))
                    {
                        schedule.Status = "IDLE";
                    }
                }
            }
        }
    }

    private static bool TryParseScheduleTime(string input, out TimeSpan time)
    {
        if (TimeSpan.TryParse(input, out time)) return true;
        if (DateTime.TryParse(input, out var dt))
        {
            time = dt.TimeOfDay;
            return true;
        }
        time = TimeSpan.Zero;
        return false;
    }

    private static bool MatchesDay(string days, DayOfWeek dow)
    {
        if (string.IsNullOrWhiteSpace(days) || days.Equals("Daily", StringComparison.OrdinalIgnoreCase)) return true;
        if (days.Equals("Mon-Fri", StringComparison.OrdinalIgnoreCase))
            return dow is DayOfWeek.Monday or DayOfWeek.Tuesday or DayOfWeek.Wednesday or DayOfWeek.Thursday or DayOfWeek.Friday;
        if (days.Equals("Sat-Sun", StringComparison.OrdinalIgnoreCase))
            return dow is DayOfWeek.Saturday or DayOfWeek.Sunday;
        return days.Equals(dow.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private async void TriggerSchedulePlay(CgScheduleItem item)
    {
        try
        {
            var proj = _vm.CgProjects.FirstOrDefault(x => x.Name.Equals(item.ProjectName, StringComparison.OrdinalIgnoreCase))
                ?? Projects.FirstOrDefault(x => x.Name.Equals(item.ProjectName, StringComparison.OrdinalIgnoreCase));
            if (proj != null)
            {
                var savedLayer = CurrentOverlayNumber;
                CurrentOverlayNumber = item.OverlayLayer;
                await RouteAsync("PLAY", "PROGRAM", proj, layerOverride: item.OverlayLayer);
                CurrentOverlayNumber = savedLayer;
                LastOperation = $"SCHEDULER PLAY: {item.ProjectName}";
                LastDetail = $"Triggered scheduled play at {DateTime.Now:HH:mm:ss} on layer {item.OverlayLayer}";
            }
        }
        catch (Exception ex)
        {
            item.Status = "PLAY ERROR: " + ex.Message;
        }
    }

    private async void TriggerScheduleStop(CgScheduleItem item)
    {
        try
        {
            var proj = _vm.CgProjects.FirstOrDefault(x => x.Name.Equals(item.ProjectName, StringComparison.OrdinalIgnoreCase))
                ?? Projects.FirstOrDefault(x => x.Name.Equals(item.ProjectName, StringComparison.OrdinalIgnoreCase));
            var savedLayer = CurrentOverlayNumber;
            CurrentOverlayNumber = item.OverlayLayer;
            await RouteAsync("STOP", "PROGRAM", proj, layerOverride: item.OverlayLayer);
            CurrentOverlayNumber = savedLayer;
            LastOperation = $"SCHEDULER STOP: {item.ProjectName}";
            LastDetail = $"Triggered scheduled stop/clear at {DateTime.Now:HH:mm:ss} on layer {item.OverlayLayer}";
        }
        catch (Exception ex)
        {
            item.Status = "STOP ERROR: " + ex.Message;
        }
    }

    private void AddSchedule_Click(object sender, RoutedEventArgs e)
    {
        var projName = NewScheduleProjectName;
        if (string.IsNullOrWhiteSpace(projName) && SelectedProject != null)
        {
            projName = SelectedProject.Name;
        }

        if (string.IsNullOrWhiteSpace(projName))
        {
            MessageBox.Show("Please select a graphic project template to schedule.", "CG Scheduler", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var startTime = string.IsNullOrWhiteSpace(NewScheduleStartTime) ? "21:00:00" : NewScheduleStartTime.Trim();
        var stopTime = string.IsNullOrWhiteSpace(NewScheduleStopTime) ? "22:00:00" : NewScheduleStopTime.Trim();
        var days = string.IsNullOrWhiteSpace(NewScheduleDays) ? "Daily" : NewScheduleDays.Trim();

        var item = new CgScheduleItem
        {
            ProjectName = projName,
            OverlayLayer = NewScheduleLayer,
            StartTime = startTime,
            StopTime = stopTime,
            Days = days,
            Enabled = true,
            Status = "IDLE"
        };

        Schedules.Add(item);
        SaveSchedules();
    }

    private void DeleteSchedule_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is CgScheduleItem item)
        {
            if (item.IsPlaying)
            {
                TriggerScheduleStop(item);
            }
            Schedules.Remove(item);
            SaveSchedules();
        }
    }

    private void TriggerScheduleNow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is CgScheduleItem item)
        {
            item.IsPlaying = true;
            item.Status = $"MANUAL PLAY ({DateTime.Now:HH:mm:ss})";
            TriggerSchedulePlay(item);
        }
    }

    private void StopScheduleNow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is CgScheduleItem item)
        {
            item.IsPlaying = false;
            item.Status = $"MANUAL STOP ({DateTime.Now:HH:mm:ss})";
            TriggerScheduleStop(item);
        }
    }

    private void LoadSchedules()
    {
        try
        {
            var path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cg-schedules.json");
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var items = JsonSerializer.Deserialize<List<CgScheduleItem>>(json);
                if (items != null)
                {
                    Schedules.Clear();
                    foreach (var it in items)
                    {
                        it.IsPlaying = false;
                        it.Status = "IDLE";
                        Schedules.Add(it);
                    }
                }
            }
            if (Schedules.Count == 0)
            {
                Schedules.Add(new CgScheduleItem
                {
                    ProjectName = CgUniqueDemoFactory.PrimeBreakingDemoName,
                    OverlayLayer = 0,
                    StartTime = "21:00:00",
                    StopTime = "22:00:00",
                    Days = "Daily",
                    Enabled = true,
                    Status = "IDLE"
                });
                SaveSchedules();
            }
        }
        catch { }
    }

    private void SaveSchedules()
    {
        try
        {
            var path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cg-schedules.json");
            var json = JsonSerializer.Serialize(Schedules.ToList(), new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch { }
    }
    #endregion

    private void Raise([CallerMemberName] string? n=null) => PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(n));
    private void OnCgProjectsChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(ReloadProjects);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _vm.CgProjectsChanged -= OnCgProjectsChanged;
        _vm.PreviewFrameReady -= OnPlayoutPreviewFrame;
        _vm.PreviewBaseFrameReady -= OnPlayoutPreviewBaseFrame;
        _vm.VideoFrameReady -= OnPlayoutProgramFrame;
        _vm.ProgramBaseFrameReady -= OnPlayoutProgramBaseFrame;
        Interlocked.Exchange(ref _latestPlayoutPreviewFrame, null);
        _latestPlayoutProgramFrame.Clear();
        _latestPlayoutProgramBaseFrame.Clear();
        _clock.Stop();
        _renderClock.Stop();
        _clock.Tick -= Clock_Tick;
        _renderClock.Tick -= RenderClock_Tick;
        _operator?.Dispose(); _operator = null;
        _previewMonitorCompositor.Dispose();
        _programMonitorCompositor.Dispose();
        _cgOutput.Dispose();
    }
}

public sealed class CgCatalogCard
{
    public required CgProject Project { get; init; }
    public string Category { get; init; } = "OTHER";
    public ImageSource? Thumbnail { get; init; }
    public string Details => $"{Project.Width}×{Project.Height} · {Project.Layers.Count} layers";
}

public sealed class CgCatalogGroup
{
    public string Name { get; set; } = "OTHER";
    public ObservableCollection<CgProject> Projects { get; set; } = [];
    public override string ToString() => Name;
}

public sealed class CgTargetRow : INotifyPropertyChanged
{
    private bool _isSelected;
    public string ChannelId { get; set; } = "";
    public string ChannelName { get; set; } = "";
    public string Format { get; set; } = "";
    public string State { get; set; } = "";
    public bool IsSelected { get => _isSelected; set { if(_isSelected==value)return;_isSelected=value;PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(IsSelected))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public class CgScheduleItem : INotifyPropertyChanged
{
    private string _projectName = "";
    private int _overlayLayer = 0;
    private string _startTime = "21:00:00";
    private string _stopTime = "22:00:00";
    private string _days = "Daily";
    private bool _enabled = true;
    private string _status = "IDLE";
    private DateTime _lastTriggerDate = DateTime.MinValue;
    private bool _isPlaying = false;

    public string ProjectName { get => _projectName; set { _projectName = value; OnPropertyChanged(); } }
    public int OverlayLayer { get => _overlayLayer; set { _overlayLayer = value; OnPropertyChanged(); } }
    public string StartTime { get => _startTime; set { _startTime = value; OnPropertyChanged(); } }
    public string StopTime { get => _stopTime; set { _stopTime = value; OnPropertyChanged(); } }
    public string Days { get => _days; set { _days = value; OnPropertyChanged(); } }
    public bool Enabled { get => _enabled; set { _enabled = value; OnPropertyChanged(); } }
    public string Status { get => _status; set { _status = value; OnPropertyChanged(); } }
    public DateTime LastTriggerDate { get => _lastTriggerDate; set { _lastTriggerDate = value; OnPropertyChanged(); } }
    public bool IsPlaying { get => _isPlaying; set { _isPlaying = value; OnPropertyChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
