using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using IOPath = System.IO.Path;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using BroadcastPlayout.Models;
using BroadcastPlayout.Services;
using BroadcastPlayout.ViewModels;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace BroadcastPlayout.Views;

public partial class CgEditorWindow : Window, INotifyPropertyChanged
{
    private readonly MainViewModel _main;
    private readonly CgCompositor _editorVideoPreviewCompositor = new();
    private readonly DispatcherTimer _timer;
    private CgProject? _selectedProject;
    private CgLayer? _selectedLayer;
    private CgHtmlSource? _selectedHtml;
    private CgDataSource? _selectedDataSource;
    private CgGroup? _selectedGroup;
    private CgKeyframe? _selectedKeyframe;
    private CgKeyframe? _selectedGroupKeyframe;
    private CgGradientStop? _selectedGradientStop;
    private double _playhead;
    private bool _loop = true;
    private bool _playing;
    private readonly Stopwatch _playbackClock = new();
    private double _playbackBaseSeconds;
    private bool _playbackPrimed;
    private CgLayer? _dragLayer;
    private CgKeyframe? _dragKeyframe;
    private CgLayer? _dragKeyframeLayer;
    private string? _dragKeyframeProperty;
    private string _dragMode = "";
    private Point _dragStartPoint;
    private double _dragStartSeconds;
    private double _dragEndSeconds;
    private double _dragAnimationStartValue;
    private CgLayer? _dragAnimationLayer;
    private CgLayer? _previewDragLayer;
    private string _previewDragMode = "";
    private Point _previewDragStart;
    private double _previewStartX, _previewStartY, _previewStartW, _previewStartH;
    private CgKeyframe? _previewEditKeyframe;
    private CgLayer? _pendingVideoSourceLayer;
    private CgLayer? _drawVideoSourceLayer;
    private Point _drawVideoStart;
    private CgLayer? _drawMaskLayer;
    private Point _drawMaskStart;
    private bool _drawingMask;
    private readonly List<Point> _penPoints = [];
    private bool _penDrawing;
    private bool _penCreatesMask;
    private CgLayer? _penMaskTarget;
    private bool _autoKey = true;
    private readonly List<CgLayer> _layerClipboard = [];
    private int _pasteGeneration;
    private int _playbackLoopIteration;
    public double ContinuousPlaybackSeconds
    {
        get
        {
            var project = SelectedProject;
            var dynamicTickerDuration = CgDataSourceService.ResolveEffectiveTickerCycleDuration(project);
            if (dynamicTickerDuration > 0.5 && project != null)
            {
                var maxOutDuration = project.Layers.Where(x => x.Visible).Select(x => x.AnimationOutSeconds).DefaultIfEmpty(0.5).Max();
                var pausePoint = Math.Min(Math.Max(0.1, ProjectDuration - maxOutDuration - 0.05), CgDataSourceService.ResolveEffectiveHoldPoint(project));
                var categoryIntroLead = Math.Min(CgDataSourceService.ResolveEffectiveCategoryIntroDuration(SelectedProject), pausePoint);
                var tickerHoldDuration = Math.Max(0.05, dynamicTickerDuration - categoryIntroLead);
                var totalCycle = pausePoint + tickerHoldDuration + maxOutDuration;
                if (_playing)
                {
                    var rawElapsed = (_playbackBaseSeconds + _playbackClock.Elapsed.TotalSeconds);
                    if (Loop && totalCycle > 0) rawElapsed %= totalCycle;
                    if (rawElapsed < pausePoint)
                        return Math.Max(0, categoryIntroLead - (pausePoint - rawElapsed));
                    if (rawElapsed < pausePoint + tickerHoldDuration)
                        return categoryIntroLead + rawElapsed - pausePoint;
                    if (rawElapsed >= pausePoint + tickerHoldDuration)
                        return dynamicTickerDuration;
                    return categoryIntroLead;
                }
                else
                {
                    if (Playhead <= pausePoint) return Math.Max(0, categoryIntroLead - (pausePoint - Playhead));
                    if (Playhead >= ProjectDuration - maxOutDuration) return dynamicTickerDuration;
                    var holdSpan = Math.Max(0.1, (ProjectDuration - maxOutDuration) - pausePoint);
                    var scrubProgress = Math.Clamp((Playhead - pausePoint) / holdSpan, 0, 1);
                    return scrubProgress * dynamicTickerDuration;
                }
            }
            return (_playbackLoopIteration * ProjectDuration) + Playhead;
        }
    }
    private string _templateCategory = "ALL";
    private bool _showSafeAreas = true;
    private bool _showCenterGuide;
    private double _actionSafePercent = 5.0;
    private double _titleSafePercent = 10.0;
    private string _safeAreaPreset = "ACTION 5% / TITLE 10%";
    private bool _showCanvasGrid = true;
    private double _timelineZoom = 1.0;
    private LayerEditSnapshot? _autoKeyEditSnapshot;
    private int _timelineSelectionAnchorIndex = -1;
    private readonly HashSet<Guid> _selectedLayerIds = [];
    private bool _selectionTransaction;
    private bool _syncingLayerListSelection;
    private const double TimelineLabelWidth = 135.0;
    private const double TimelineRightPad = 0.0;
    private const double TimelineBasePixelsPerSecond = 120.0;
    private bool _isTimelineFit = true;
    private CgTimelineEvent? _draggedPauseEvent;
    // MouseMove can arrive hundreds of times per second. Rebuilding the full CG canvas for
    // every raw input message (including video/HTML layers) caused sticky, jittery dragging.
    // Coalesce interactive redraws to the next WPF composition frame instead.
    private bool _interactivePreviewRenderPending;
    private bool _interactiveTimelineRenderPending;
    private bool _interactiveRenderHooked;
    // Fast-path references for direct manipulation. Normal move/resize gestures update only the
    // affected WPF visual and its selection chrome; expensive media/HTML layers are not rebuilt.
    private readonly Dictionary<CgLayer, FrameworkElement> _previewLayerVisuals = new();
    private Border? _previewSelectionOutline;
    private Border? _previewSelectionLabel;
    private readonly Dictionary<string, Rectangle> _previewSelectionHandles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stack<CompositionNavigationFrame> _compositionNavigation = new();
    private readonly Stack<string> _undoStack = new();
    private readonly Stack<string> _redoStack = new();
    private bool _isUndoRedoAction;

    public void RecordUndoSnapshot()
    {
        if (_isUndoRedoAction || _selectedProject is null) return;
        try
        {
            var json = JsonSerializer.Serialize(_selectedProject);
            _undoStack.Push(json);
            if (_undoStack.Count > 50)
            {
                var list = _undoStack.ToList();
                list.RemoveAt(list.Count - 1);
                _undoStack.Clear();
                for (int i = list.Count - 1; i >= 0; i--) _undoStack.Push(list[i]);
            }
            _redoStack.Clear();
        }
        catch { }
    }

    public void Undo()
    {
        if (_undoStack.Count == 0 || _selectedProject is null) return;
        try
        {
            _isUndoRedoAction = true;
            var currentJson = JsonSerializer.Serialize(_selectedProject);
            _redoStack.Push(currentJson);
            var prevJson = _undoStack.Pop();
            RestoreProjectSnapshot(prevJson);
        }
        catch { }
        finally { _isUndoRedoAction = false; }
    }

    public void Redo()
    {
        if (_redoStack.Count == 0 || _selectedProject is null) return;
        try
        {
            _isUndoRedoAction = true;
            var currentJson = JsonSerializer.Serialize(_selectedProject);
            _undoStack.Push(currentJson);
            var nextJson = _redoStack.Pop();
            RestoreProjectSnapshot(nextJson);
        }
        catch { }
        finally { _isUndoRedoAction = false; }
    }

    private void RestoreProjectSnapshot(string json)
    {
        var restored = JsonSerializer.Deserialize<CgProject>(json);
        if (restored is null || _selectedProject is null) return;
        _selectedProject.Name = restored.Name;
        _selectedProject.Width = restored.Width;
        _selectedProject.Height = restored.Height;
        _selectedProject.FrameRate = restored.FrameRate;
        _selectedProject.DurationSeconds = restored.DurationSeconds;
        _selectedProject.Layers = restored.Layers ?? [];
        _selectedProject.HtmlSources = restored.HtmlSources ?? [];
        _selectedProject.DataSources = restored.DataSources ?? [];
        _selectedProject.Groups = restored.Groups ?? [];
        LoadCompositionIntoEditor(_selectedProject, useRepresentativePlayhead: false, requestedPlayhead: Playhead);
        _main.SaveCgProjects();
        RenderAll();
    }

    private sealed record CompositionNavigationFrame(CgProject Project, CgLayer PrecompLayer, double ParentPlayhead);

    private sealed record LayerEditSnapshot(
        CgLayer Layer,double X,double Y,double Z,double Width,double Height,
        double RotationX,double RotationY,double Rotation,double Opacity,
        double ScaleX,double ScaleY,double ScaleZ,double AnchorX,double AnchorY,double AnchorZ,string StyleJson);

    private sealed class PreviewHit
    {
        public required CgLayer Layer { get; init; }
        public required string Mode { get; init; }
    }

    private sealed class TimelineKeyframeHit
    {
        public required CgLayer Layer { get; init; }
        public required CgKeyframe Keyframe { get; init; }
        public string Property { get; init; } = "All";
    }

    private sealed class TimelineLayerHit
    {
        public required CgLayer Layer { get; init; }
        public required string Mode { get; init; }
        public string Property { get; init; } = "";
    }

    private readonly HashSet<Guid> _expandedTimelineLayerIds = [];

    public CgEditorWindow(MainViewModel main)
    {
        InitializeComponent();
        WindowChromeActions.ApplyCleanBorder(this);
        _main = main;
        Projects = main.CgProjects;
        ProjectView = CollectionViewSource.GetDefaultView(Projects);
        ProjectView.Filter = FilterProject;
        Animations = ["None", "Fade", "Slide Left", "Slide Right", "Slide Up", "Slide Down", "Zoom", "Scale", "Pop", "Push Left", "Push Right", "Push Up", "Push Down", "Wipe Left", "Wipe Right", "Blur"];
        Easings = ["linear", "power1.in", "power1.out", "power1.inout", "power2.in", "power2.out", "power2.inout", "power3.in", "power3.out", "power3.inout", "power4.in", "power4.out", "power4.inout", "back.in", "back.out", "back.inout", "elastic.in", "elastic.out", "elastic.inout", "bounce.in", "bounce.out", "bounce.inout", "expo.in", "expo.out", "expo.inout", "circ.in", "circ.out", "circ.inout", "sine.in", "sine.out", "sine.inout", "steps(2).out", "steps(4).out", "steps(6).out", "steps(12).out"];
        var editorSettings = SettingsStore.Load();
        _showSafeAreas = editorSettings.CgEditorShowSafeAreas;
        _showCenterGuide = editorSettings.CgEditorShowCenterGuide;
        _actionSafePercent = Math.Clamp(editorSettings.CgEditorActionSafePercent, 1.0, 25.0);
        _titleSafePercent = Math.Clamp(editorSettings.CgEditorTitleSafePercent, 2.0, 35.0);
        if (_titleSafePercent <= _actionSafePercent) _titleSafePercent = Math.Min(35.0, _actionSafePercent + 5.0);
        _safeAreaPreset = SafePresetFromValues(_actionSafePercent, _titleSafePercent);
        DataContext = this;
        SelectedProject = main.ActiveCgProject ?? Projects.FirstOrDefault();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) =>
        {
            if (!_playing) return;
            var project = SelectedProject;
            var dynamicTickerDuration = CgDataSourceService.ResolveEffectiveTickerCycleDuration(project);
            if (dynamicTickerDuration > 0.5 && project != null)
            {
                var maxOutDuration = project.Layers.Where(x => x.Visible).Select(x => x.AnimationOutSeconds).DefaultIfEmpty(0.5).Max();
                var pausePoint = Math.Min(Math.Max(0.1, ProjectDuration - maxOutDuration - 0.05), CgDataSourceService.ResolveEffectiveHoldPoint(project));
                var categoryIntroLead = Math.Min(CgDataSourceService.ResolveEffectiveCategoryIntroDuration(SelectedProject), pausePoint);
                var tickerHoldDuration = Math.Max(0.05, dynamicTickerDuration - categoryIntroLead);
                var totalCycle = pausePoint + tickerHoldDuration + maxOutDuration;
                var rawElapsed = _playbackBaseSeconds + _playbackClock.Elapsed.TotalSeconds;

                if (rawElapsed >= totalCycle)
                {
                    if (Loop)
                    {
                        var loops = (int)Math.Floor(rawElapsed / totalCycle);
                        _playbackLoopIteration += Math.Max(1, loops);
                        rawElapsed %= totalCycle;
                        _playbackBaseSeconds = rawElapsed;
                        _playbackClock.Restart();
                        Raise(nameof(BoundDataSourceStatus));
                    }
                    else
                    {
                        _playing = false;
                        _playbackClock.Stop();
                        Playhead = ProjectDuration;
                        return;
                    }
                }

                if (rawElapsed < pausePoint)
                {
                    Playhead = rawElapsed;
                }
                else if (rawElapsed < pausePoint + tickerHoldDuration)
                {
                    Playhead = pausePoint;
                    RenderPreview();
                }
                else
                {
                    var outElapsed = rawElapsed - (pausePoint + tickerHoldDuration);
                    Playhead = Math.Min(ProjectDuration, (ProjectDuration - maxOutDuration) + outElapsed);
                }
                Raise(nameof(PlayheadText));
                Raise(nameof(RemainingText));
                Raise(nameof(BoundDataSourceStatus));
            }
            else
            {
                var duration = Math.Max(.05, ProjectDuration);
                // When the project has ticker layers bound to data sources but
                // ResolveEffectiveTickerCycleDuration returned 0 (data hasn't
                // loaded yet), avoid using a huge DurationSeconds (e.g. 3600)
                // as the effective playback cap — that freezes the UI.  Use the
                // last visible layer's EndSeconds (or a short fallback) so
                // playback loops/completes quickly and re-evaluates once the
                // data arrives.
                var hasTickerDataSource = project?.Layers?.Any(l => l.Visible &&
                    string.Equals(l.Type, "Ticker", StringComparison.OrdinalIgnoreCase) &&
                    l.DataSourceId != Guid.Empty) == true;
                if (hasTickerDataSource && duration > 60)
                {
                    var lastLayerEnd = project!.Layers.Where(l => l.Visible)
                        .Select(l => l.EndSeconds).DefaultIfEmpty(10).Max();
                    duration = Math.Clamp(lastLayerEnd, 5.0, 60.0);
                }
                var next = _playbackBaseSeconds + _playbackClock.Elapsed.TotalSeconds;
                if (next >= duration)
                {
                    if (Loop)
                    {
                        var loops = (int)Math.Floor(next / duration);
                        _playbackLoopIteration += Math.Max(1, loops);
                        next %= duration;
                        _playbackBaseSeconds = next;
                        _playbackClock.Restart();
                        Raise(nameof(BoundDataSourceStatus));
                    }
                    else
                    {
                        next = duration;
                        _playing = false;
                        _playbackClock.Stop();
                    }
                }
                Playhead = next;
            }
        };
        _timer.Start();
        Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(() => {
            InspectorPanel?.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler(Inspector_TextChanged));
            UpdateCanvasAspect();
            SanitizeProjectForEditor(SelectedProject); FitTimelineToViewport(); RenderAll();
            Dispatcher.BeginInvoke(new Action(() => { FitTimelineToViewport(); }), DispatcherPriority.Background);
        }), DispatcherPriority.Loaded);
        Closed += (_, _) => { SyncLayersToProject(); CancelInteractiveRender(); _timer.Stop(); _playbackClock.Stop(); _editorVideoPreviewCompositor.Dispose(); _main.SaveCgProjects(); };
    }

    public ObservableCollection<CgProject> Projects { get; }
    public ICollectionView ProjectView { get; }
    public IReadOnlyList<string> TemplateCategories { get; } = ["ALL", "AP1 HD", "PRIME HD", "SPACE 4K", "AAJ TAK", "ABP NEWS", "INDIA TV", "GRAPHICS PACK", "BREAKING NEWS", "NEWS", "LOWER THIRDS", "WEATHER", "SPORTS", "FINANCE", "ELECTION", "ENTERTAINMENT", "MUSIC", "TICKERS", "IMPORTED"];
    public string TemplateCategory
    {
        get => _templateCategory;
        set { _templateCategory = string.IsNullOrWhiteSpace(value) ? "ALL" : value; Raise(); ProjectView.Refresh(); Raise(nameof(TemplateCountText)); }
    }
    public string TemplateCountText => $"{ProjectView.Cast<object>().Count()} / {Projects.Count} PROJECTS";
    public ObservableCollection<CgLayer> Layers { get; } = [];
    public ObservableCollection<CgHtmlSource> HtmlSources { get; } = [];
    public ObservableCollection<CgDataSource> DataSources { get; } = [];
    public ObservableCollection<CgGroup> Groups { get; } = [];
    public ObservableCollection<CgKeyframe> Keyframes { get; } = [];
    public ObservableCollection<CgKeyframe> GroupKeyframes { get; } = [];
    public CgGradientStop? SelectedGradientStop { get => _selectedGradientStop; set { _selectedGradientStop=value; Raise(); } }
    public IReadOnlyList<string> Animations { get; }
    public IReadOnlyList<string> Easings { get; }
    public IReadOnlyList<string> DataSourceTypes { get; } = ["UrlJson", "LocalJson", "Xml", "Rss", "Excel", "Csv", "Txt", "OpenWeather"];
    public IReadOnlyList<string> StaggerFromOptions { get; } = ["Start", "End"];
    public IReadOnlyList<string> LayerRoles { get; } = ["Graphic", "Logo", "Bug", "Shape", "Data"];
    public IReadOnlyList<string> ShapeKinds { get; } = [
        "Rectangle", "Rounded Rectangle", "Ellipse", "Circle", "Line",
        "Triangle", "Inverted Triangle", "Diamond", "Star", "Pentagon",
        "Hexagon", "Octagon", "Heart", "Arrow Right", "Arrow Left",
        "Speech Bubble", "Trapezoid", "Cross / Plus"
        , "Custom Path"
    ];
    public IReadOnlyList<string> CalendarSystems { get; } = ["AD", "BS"];
    public static readonly IReadOnlyList<string> AdFormatPresets =
    [
        "dd MMM yyyy  HH:mm:ss",
        "yyyy-MM-dd  HH:mm:ss",
        "dddd, MMMM d, yyyy",
        "dd/MM/yyyy  HH:mm",
        "HH:mm:ss",
        "hh:mm:ss tt",
        "Custom"
    ];
    public IReadOnlyList<string> DateTimeFormatPresets =>
        SelectedLayer is not null && string.Equals(SelectedLayer.CalendarSystem, "BS", StringComparison.OrdinalIgnoreCase)
            ? NepaliCalendarService.StandardFormats
            : AdFormatPresets;
    public IReadOnlyList<string> NepaliFormatPresets => DateTimeFormatPresets;
    public IReadOnlyList<string> TextAlignments { get; } = ["Left", "Center", "Right"];
    public IReadOnlyList<string> VerticalAlignments { get; } = ["Top", "Center", "Bottom"];
    public IReadOnlyList<string> TextAnimationUnits { get; } = ["Whole", "Letter", "Word", "Sentence"];
    public IReadOnlyList<string> TextAnimationPresets { get; } =
    [
        "None",
        "Slide Left",
        "Slide Right",
        "Push Left",
        "Push Right",
        "Slide Up",
        "Slide Down",
        "Rise Cascade",
        "Tracking Reveal",
        "Pop Cascade",
        "Bounce / Wave",
        "Type On",
        "Typewriter + Blur",
        "Blur Dissolve",
        "Fade Cascade",
        "Slide Letters",
        "Soft Reveal",
        "Flip In",
        "Wipe Words",
        "Glow Pulse",
        "Date / Time Split",
        "Date / Time Flip",
        "Zoom",
        "Wiggle",
        "Jump",
        "Scale Down",
        "Scale Up"
    ];
    public IReadOnlyList<string> TickerModes { get; } = ["Continuous", "Push"];
    public IReadOnlyList<string> TickerDirections { get; } = ["Left", "Right", "Up", "Down"];
    public IReadOnlyList<string> VideoSourceKinds { get; } = ["File", "URL", "NDI", "DirectShow", "Screen", "Custom"];
    public IReadOnlyList<string> BlendModes { get; } = ["Normal", "Multiply", "Screen", "Overlay", "Darken", "Lighten", "Color Dodge", "Color Burn", "Linear Burn", "Linear Dodge (Add)", "Add", "Hard Light", "Soft Light", "Vivid Light", "Linear Light", "Pin Light", "Hard Mix", "Difference", "Exclusion", "Subtract", "Divide", "Hue", "Saturation", "Color", "Luminosity"];
    public IReadOnlyList<string> MaskShapes { get; } = ["Rectangle", "Rounded Rectangle", "Ellipse", "Custom Path"];
    public IReadOnlyList<string> SqueezeHorizontalModes { get; } = ["Free", "Both", "Left", "Right"];
    public IReadOnlyList<string> AspectRatioOptions { get; } = ["Free", "16:9", "4:3"];
    public IReadOnlyList<string> SafeAreaPresets { get; } = ["ACTION 5% / TITLE 10%", "ACTION 7.5% / TITLE 12.5%", "ACTION 10% / TITLE 20%"];
    public IReadOnlyList<string> SystemFontNames { get; } = InitSystemFontNames();

    private static IReadOnlyList<string> InitSystemFontNames()
    {
        var priority = new[]
        {
            "Suvadin", "Segoe UI", "Arial", "Nirmala UI", "Kantipur", "Preeti", "Ganesh",
            "Calibri", "Impact", "Montserrat", "Roboto", "Tahoma", "Trebuchet MS", "Verdana",
            "Times New Roman", "Georgia", "Courier New"
        };
        var system = Fonts.SystemFontFamilies.Select(f => f.Source).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().OrderBy(x => x);
        var combined = new List<string>(priority);
        foreach (var name in system)
        {
            if (!combined.Contains(name, StringComparer.OrdinalIgnoreCase))
                combined.Add(name);
        }
        return combined;
    }

    public static FontFamily ResolveFontFamily(string? familyName)
    {
        if (string.IsNullOrWhiteSpace(familyName)) return new FontFamily("Segoe UI");
        var trimmed = familyName.Trim();
        if (trimmed.Equals("Suvadin", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("Arab", StringComparison.OrdinalIgnoreCase))
        {
            var fontPath = ResolveBundledFont("suvadin.ttf");
            if (!string.IsNullOrEmpty(fontPath) && File.Exists(fontPath))
            {
                var dir = IOPath.GetDirectoryName(fontPath)!.Replace('\\', '/') + "/";
                return new FontFamily(new Uri(dir), "./#Suvadin");
            }
        }
        else if (trimmed.Equals("Ganesh", StringComparison.OrdinalIgnoreCase))
        {
            var fontPath = ResolveBundledFont("GANESH.TTF");
            if (!string.IsNullOrEmpty(fontPath) && File.Exists(fontPath))
            {
                var dir = IOPath.GetDirectoryName(fontPath)!.Replace('\\', '/') + "/";
                return new FontFamily(new Uri(dir), "./#GANESH");
            }
        }
        try { return new FontFamily(trimmed); }
        catch { return new FontFamily("Segoe UI"); }
    }

    public static string ResolveBundledFont(string fontName)
    {
        var candidates = new[]
        {
            IOPath.Combine(AppContext.BaseDirectory, "cgdemo-templates", "prime", "demo", "assets", "fonts", fontName),
            IOPath.Combine(AppContext.BaseDirectory, "cgdemo-templates", "space4k", "assets", "fonts", fontName),
            IOPath.Combine(AppContext.BaseDirectory, "demos", "gfx", "prime", "demo", "assets", "fonts", fontName),
            IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "cgdemo-templates", "prime", "demo", "assets", "fonts", fontName),
            IOPath.Combine(Directory.GetCurrentDirectory(), "cgdemo-templates", "prime", "demo", "assets", "fonts", fontName),
            IOPath.Combine(AppContext.BaseDirectory, fontName)
        };
        return candidates.FirstOrDefault(File.Exists) ?? string.Empty;
    }

    private string _quickJsonUrlBuffer = string.Empty;

    public string BoundDataSourceUrl
    {
        get
        {
            if (SelectedLayer is not null && SelectedLayer.DataSourceId != Guid.Empty)
            {
                var ds = SelectedProject?.DataSources?.FirstOrDefault(x => x.Id == SelectedLayer.DataSourceId)
                         ?? DataSources.FirstOrDefault(x => x.Id == SelectedLayer.DataSourceId);
                if (ds is not null && !string.IsNullOrWhiteSpace(ds.Source)) return ds.Source;
            }
            return _quickJsonUrlBuffer;
        }
        set
        {
            _quickJsonUrlBuffer = value ?? string.Empty;
            if (SelectedLayer is not null && SelectedLayer.DataSourceId != Guid.Empty)
            {
                var ds = SelectedProject?.DataSources?.FirstOrDefault(x => x.Id == SelectedLayer.DataSourceId)
                         ?? DataSources.FirstOrDefault(x => x.Id == SelectedLayer.DataSourceId);
                if (ds is not null)
                {
                    ds.Source = _quickJsonUrlBuffer;
                    SyncLayersToProject(); _main.SaveCgProjects();
                }
            }
            Raise();
            Raise(nameof(BoundDataSourceStatus));
        }
    }

    public string BoundDataSourceStatus
    {
        get
        {
            if (SelectedLayer is null || SelectedLayer.DataSourceId == Guid.Empty)
                return "No data source linked. Enter JSON URL & Key or select from list.";
            var ds = SelectedProject?.DataSources?.FirstOrDefault(x => x.Id == SelectedLayer.DataSourceId)
                     ?? DataSources.FirstOrDefault(x => x.Id == SelectedLayer.DataSourceId);
            if (ds is null) return "Data source not found.";
            var rows = CgDataRuntime.Shared.GetRows(ds);
            var key = SelectedLayer.DataField;
            if (rows.Count == 0) return $"{ds.Name} · {ds.Status}";

            if (SelectedProject != null && CgDataSourceService.TryGetActiveCategoryTiming(SelectedProject, SelectedLayer, ContinuousPlaybackSeconds, out var catName, out var catElapsed, out var catDur, out var catIdx, out _, out _))
            {
                var schedule = CgDataSourceService.BuildCategoryFeedSchedule(SelectedProject, SelectedLayer, rows);
                var totalCats = schedule.Categories.Count;
                var currentItems = schedule.Categories.ElementAtOrDefault(catIdx)?.Items?.Count ?? 0;
                return $"CATEGORY FEED · [{catName}] ({catIdx + 1}/{totalCats}) · Crawl ({catElapsed:0.0}s / {catDur:0.0}s, Total cycle: {schedule.TotalCycleDuration:0.0}s) · {currentItems} items";
            }

            var itemDur = SelectedLayer.DataItemDurationSeconds > 0.05
                ? SelectedLayer.DataItemDurationSeconds
                : Math.Max(0.5, ProjectDuration);
            var activeIndex = (int)Math.Floor(ContinuousPlaybackSeconds / itemDur) % rows.Count;
            if (activeIndex < 0) activeIndex = 0;

            var preview = string.Empty;
            if (rows[activeIndex].TryGetValue(key, out var val)) preview = val;
            else preview = rows[activeIndex].Values.FirstOrDefault() ?? string.Empty;
            if (preview.Length > 45) preview = preview[..45] + "...";
            var interval = SelectedLayer.SequenceOverlayIntervalItems > 1 ? $" · Overlay ev. {SelectedLayer.SequenceOverlayIntervalItems}" : "";
            return $"READY · {rows.Count} item(s) (Item {activeIndex + 1}/{rows.Count}){interval} · Key [{key}]: \"{preview}\"";
        }
    }

    private static bool IsCuratedBroadcastTemplate(string name)
    {
        return true;
    }

    private bool FilterProject(object item)
    {
        if (item is not CgProject p) return true;
        var n = p.Name.ToUpperInvariant();
        return TemplateCategory switch
        {
            "ALL" => true,
            "AP1 HD" => n.Contains("AP1") || n.Contains("AP 1"),
            "PRIME HD" => n.Contains("PRIME HD"),
            "SPACE 4K" => n.Contains("SPACE 4K"),
            "AAJ TAK" => n.Contains("AAJ TAK"),
            "ABP NEWS" => n.Contains("ABP NEWS"),
            "INDIA TV" => n.Contains("INDIA TV"),
            "GRAPHICS PACK" => n.Contains("GRAPHICS PACK") || n.Contains("FRACTAL"),
            "BREAKING NEWS" => n.Contains("BREAKING"),
            "NEWS" => n.Contains("NEWS") || n.Contains("HEADLINE") || n.Contains("DISPATCH"),
            "LOWER THIRDS" => n.Contains("LOWER THIRD") || n.Contains("NAME PLATE") || n.Contains("STRAP"),
            "WEATHER" => n.Contains("WEATHER") || n.Contains("FORECAST") || n.Contains("AQI"),
            "SPORTS" => n.Contains("CRICKET") || n.Contains("FOOTBALL") || n.Contains("SPORTS") || n.Contains("SCOREBUG"),
            "FINANCE" => n.Contains("FINANCE") || n.Contains("MARKET") || n.Contains("BUSINESS"),
            "ELECTION" => n.Contains("ELECTION"),
            "ENTERTAINMENT" => n.Contains("ENTERTAINMENT") || n.Contains("HOLLYWOOD") || n.Contains("CINEMA"),
            "MUSIC" => n.Contains("MUSIC") || n.Contains("NOW PLAYING"),
            "TICKERS" => n.Contains("TICKER") || n.Contains("ROLL"),
            "IMPORTED" => p.IsImported || n.Contains("IMPORTED"),
            _ => true
        };
    }

    private CgProject? RootComposition => _compositionNavigation.Count > 0 ? _compositionNavigation.Last().Project : _selectedProject;

    public CgProject? ProjectPickerSelection
    {
        get => RootComposition;
        set
        {
            if (value is null) return;
            SelectedProject = value;
        }
    }

    public CgProject? SelectedProject
    {
        get => _selectedProject;
        set
        {
            if (ReferenceEquals(_selectedProject, value) && _compositionNavigation.Count == 0) return;
            SyncLayersToProject();
            _compositionNavigation.Clear();
            _selectedProject = value;
            LoadCompositionIntoEditor(value, useRepresentativePlayhead: true);
            Raise();
            Raise(nameof(ProjectPickerSelection));
            Raise(nameof(IsInsidePrecomposition));
            Raise(nameof(CompositionPathText));
        }
    }

    public bool IsInsidePrecomposition => _compositionNavigation.Count > 0;
    public string CompositionPathText
    {
        get
        {
            if (_selectedProject is null) return "No composition";
            if (_compositionNavigation.Count == 0) return _selectedProject.Name;
            var names = _compositionNavigation.Reverse().Select(x => x.Project.Name).Append(_selectedProject.Name);
            return string.Join("  ›  ", names);
        }
    }

    private void LoadCompositionIntoEditor(CgProject? value, bool useRepresentativePlayhead, double? requestedPlayhead = null)
    {
        SanitizeProjectForEditor(value);
        _playing = false;
        _playbackLoopIteration = 0;
        _playbackClock.Reset();
        _playbackPrimed = false;
        if (value is not null && value.Name.StartsWith("Demo ", StringComparison.OrdinalIgnoreCase)) CgDemoValidator.NormalizeDemoProject(value);
        Layers.Clear();
        HtmlSources.Clear();
        DataSources.Clear();
        Groups.Clear();
        Keyframes.Clear();
        GroupKeyframes.Clear();
        if (value is not null)
        {
            if (value.Layers.Count == 0)
            {
                var canonical = CgUniqueDemoFactory.Create().FirstOrDefault(x => string.Equals(x.Name, value.Name, StringComparison.OrdinalIgnoreCase));
                if (canonical != null && canonical.Layers.Count > 0)
                {
                    value.Layers = canonical.Layers.Select(CgDemoFactory.CloneLayer).ToList();
                    value.DataSources = canonical.DataSources.ToList();
                    value.Groups = canonical.Groups.ToList();
                    value.HtmlSources = canonical.HtmlSources.ToList();
                }
            }
            foreach (var layer in value.Layers) Layers.Add(layer);
            foreach (var html in value.HtmlSources ?? []) HtmlSources.Add(html);
            foreach (var data in value.DataSources ?? []) DataSources.Add(data);
            foreach (var group in value.Groups ?? []) Groups.Add(group);
        }
        SetLayerSelection(Layers.Take(1), Layers.FirstOrDefault(), updateAnchor: true);
        SelectedHtml = HtmlSources.FirstOrDefault();
        SelectedDataSource = DataSources.FirstOrDefault();
        SelectedGroup = Groups.FirstOrDefault();
        if (requestedPlayhead.HasValue)
            _playhead = Math.Clamp(requestedPlayhead.Value, 0, ProjectDuration);
        else if (useRepresentativePlayhead)
            _playhead = value is null ? 0 : Math.Min(ProjectDuration, Math.Max(0, Math.Min(1.0, ProjectDuration * 0.12)));
        else
            _playhead = Math.Clamp(_playhead, 0, ProjectDuration);
        _timelineSelectionAnchorIndex = Layers.Count > 0 ? 0 : -1;
        UpdateCanvasAspect();
        Raise(nameof(ProjectDuration)); Raise(nameof(Playhead)); Raise(nameof(PlayheadText)); Raise(nameof(RemainingText)); Raise(nameof(CanvasInfoText));
        Raise(nameof(IsInsidePrecomposition)); Raise(nameof(CompositionPathText));
        RenderAll();
        Dispatcher.BeginInvoke(new Action(FitTimelineToViewport), DispatcherPriority.Loaded);
    }

    private void EnterPrecomposition(CgLayer layer)
    {
        if (_selectedProject is null || layer.Precomposition is null) return;
        SyncLayersToProject();
        var parent = _selectedProject;
        var parentPlayhead = Playhead;
        _compositionNavigation.Push(new CompositionNavigationFrame(parent, layer, parentPlayhead));
        _selectedProject = layer.Precomposition;
        var childPlayhead = Math.Clamp(parentPlayhead - layer.StartSeconds, 0, Math.Max(.5, layer.Precomposition.DurationSeconds));
        LoadCompositionIntoEditor(_selectedProject, useRepresentativePlayhead: false, requestedPlayhead: childPlayhead);
        Raise(nameof(SelectedProject));
        Raise(nameof(ProjectPickerSelection));
    }

    private void ExitPrecomposition()
    {
        if (_compositionNavigation.Count == 0) return;
        SyncLayersToProject();
        var childPlayhead = Playhead;
        var frame = _compositionNavigation.Pop();
        _selectedProject = frame.Project;
        var parentPlayhead = Math.Clamp(frame.PrecompLayer.StartSeconds + childPlayhead, 0, Math.Max(.5, frame.Project.DurationSeconds));
        LoadCompositionIntoEditor(_selectedProject, useRepresentativePlayhead: false, requestedPlayhead: parentPlayhead);
        if (Layers.Contains(frame.PrecompLayer))
        {
            SetLayerSelection([frame.PrecompLayer], frame.PrecompLayer, updateAnchor: true);
        }
        Raise(nameof(SelectedProject));
        Raise(nameof(ProjectPickerSelection));
    }

    private void BackToParentComposition_Click(object sender, RoutedEventArgs e) => ExitPrecomposition();

    public CgLayer? SelectedLayer
    {
        get => _selectedLayer;
        set
        {
            if (!_selectionTransaction && !_syncingLayerListSelection)
            {
                _selectedLayerIds.Clear();
                if (value is not null && Layers.Contains(value)) _selectedLayerIds.Add(value.Id);
                SyncLayerListSelectionVisuals();
            }
            if (ReferenceEquals(_selectedLayer, value)) return;
            _selectedLayer = value;
            Keyframes.Clear();
            if (value is not null) foreach (var keyframe in value.Keyframes ?? []) Keyframes.Add(keyframe);
            SelectedKeyframe = Keyframes.FirstOrDefault();
            Raise();
            Raise(nameof(BoundDataSourceUrl));
            Raise(nameof(BoundDataSourceStatus));
            RaiseInspectorVisibilities();
            RenderPreview();
            RenderTimeline();
        }
    }

    public bool IsSelectedLayerText => SelectedLayer is not null && (
        string.Equals(SelectedLayer.Type, "Text", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(SelectedLayer.Type, "Ticker", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(SelectedLayer.Type, "Roll", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(SelectedLayer.Type, "Timer", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(SelectedLayer.Type, "Countdown", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(SelectedLayer.Type, "BackIn", StringComparison.OrdinalIgnoreCase) ||
        SelectedLayer.IsTimerLayer ||
        string.Equals(SelectedLayer.Type, "DigitalClock", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(SelectedLayer.Type, "NepaliDate", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(SelectedLayer.Type, "NepaliClock", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(SelectedLayer.Type, "DateTime", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(SelectedLayer.Type, "Timecode", StringComparison.OrdinalIgnoreCase));

    public bool IsSelectedLayerTimer => SelectedLayer is not null && (
        SelectedLayer.IsTimerLayer ||
        string.Equals(SelectedLayer.Type, "Timer", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(SelectedLayer.Type, "Countdown", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(SelectedLayer.Type, "BackIn", StringComparison.OrdinalIgnoreCase) ||
        (!string.IsNullOrWhiteSpace(SelectedLayer.TimerMode) && !string.Equals(SelectedLayer.TimerMode, "None", StringComparison.OrdinalIgnoreCase)));

    public bool IsSelectedLayerNepaliDate => SelectedLayer is not null && (
        string.Equals(SelectedLayer.Type, "DateTime", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(SelectedLayer.Type, "NepaliDate", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(SelectedLayer.Type, "NepaliClock", StringComparison.OrdinalIgnoreCase) ||
        (!string.IsNullOrWhiteSpace(SelectedLayer.DataFormat) && (SelectedLayer.DataFormat.Contains("BS") || SelectedLayer.DataFormat.Contains("२०"))) ||
        (!string.IsNullOrWhiteSpace(SelectedLayer.Text) && (SelectedLayer.Text.Contains("BS") || SelectedLayer.Text.Contains("२०"))));

    public bool IsSelectedLayerShape => SelectedLayer is not null && (
        string.Equals(SelectedLayer.Type, "Shape", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(SelectedLayer.Type, "Line", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(SelectedLayer.Type, "Ellipse", StringComparison.OrdinalIgnoreCase));

    public bool IsSelectedLayerPipOrShape => SelectedLayer is not null && (
        SelectedLayer.SqueezeProgram ||
        IsSelectedLayerShape ||
        string.Equals(SelectedLayer.Type, "ProgramPip", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(SelectedLayer.Name, "Program Video PIP", StringComparison.OrdinalIgnoreCase));

    public bool IsSelectedLayerMedia => SelectedLayer is not null && (
        string.Equals(SelectedLayer.Type, "Image", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(SelectedLayer.Type, "ImageSequence", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(SelectedLayer.Type, "Video", StringComparison.OrdinalIgnoreCase));

    public bool IsSelectedLayerSequence => SelectedLayer is not null &&
        string.Equals(SelectedLayer.Type, "ImageSequence", StringComparison.OrdinalIgnoreCase);

    public Visibility CardFillOutlineVisibility => (IsSelectedLayerShape || IsSelectedLayerText) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CardShapeKindVisibility => IsSelectedLayerShape ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CardGradientVisibility => (IsSelectedLayerShape || IsSelectedLayerText) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CardTypographyVisibility => IsSelectedLayerText ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CardVariablesVisibility => (IsSelectedLayerText || (SelectedLayer is not null && string.Equals(SelectedLayer.Type, "Ticker", StringComparison.OrdinalIgnoreCase))) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CardTimerVisibility => IsSelectedLayerTimer ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CardPipVisibility => IsSelectedLayerPipOrShape ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NepaliDateSectionVisibility => IsSelectedLayerNepaliDate ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TextAnimationEffectsVisibility => IsSelectedLayerText ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MediaSectionVisibility => IsSelectedLayerMedia ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SequenceControlsVisibility => IsSelectedLayerSequence ? Visibility.Visible : Visibility.Collapsed;

    private void RaiseInspectorVisibilities()
    {
        Raise(nameof(IsSelectedLayerText));
        Raise(nameof(IsSelectedLayerTimer));
        Raise(nameof(IsSelectedLayerNepaliDate));
        Raise(nameof(IsSelectedLayerShape));
        Raise(nameof(IsSelectedLayerPipOrShape));
        Raise(nameof(IsSelectedLayerMedia));
        Raise(nameof(IsSelectedLayerSequence));
        Raise(nameof(CardFillOutlineVisibility));
        Raise(nameof(CardShapeKindVisibility));
        Raise(nameof(CardGradientVisibility));
        Raise(nameof(CardTypographyVisibility));
        Raise(nameof(CardVariablesVisibility));
        Raise(nameof(CardTimerVisibility));
        Raise(nameof(CardPipVisibility));
        Raise(nameof(NepaliDateSectionVisibility));
        Raise(nameof(TextAnimationEffectsVisibility));
        Raise(nameof(MediaSectionVisibility));
        Raise(nameof(SequenceControlsVisibility));
    }

    private bool IsLayerSelected(CgLayer layer) => _selectedLayerIds.Contains(layer.Id);

    private void SyncLayerListSelectionVisuals()
    {
        if (LayerList is null || _syncingLayerListSelection) return;
        _syncingLayerListSelection = true;
        try
        {
            LayerList.SelectedItems.Clear();
            foreach (var layer in Layers.Where(IsLayerSelected)) LayerList.SelectedItems.Add(layer);
        }
        finally { _syncingLayerListSelection = false; }
    }

    private void SetLayerSelection(IEnumerable<CgLayer> layers, CgLayer? primary = null, bool updateAnchor = false)
    {
        var selected = layers.Where(Layers.Contains).Distinct().ToArray();
        if (primary is not null && !selected.Contains(primary)) primary = selected.LastOrDefault();
        primary ??= selected.LastOrDefault();
        _selectionTransaction = true;
        try
        {
            _selectedLayerIds.Clear();
            foreach (var layer in selected) _selectedLayerIds.Add(layer.Id);
            SyncLayerListSelectionVisuals();
            SelectedLayer = primary;
        }
        finally { _selectionTransaction = false; }
        if (updateAnchor) _timelineSelectionAnchorIndex = primary is null ? -1 : Layers.IndexOf(primary);
        RenderPreview();
        RenderTimeline();
    }

    private void ToggleLayerSelection(CgLayer layer)
    {
        if (!Layers.Contains(layer)) return;
        var selected = GetSelectedLayers().ToList();
        if (selected.Contains(layer)) selected.Remove(layer); else selected.Add(layer);
        _timelineSelectionAnchorIndex = Layers.IndexOf(layer);
        SetLayerSelection(selected, selected.Contains(layer) ? layer : selected.LastOrDefault());
    }

    private void SelectLayerRange(CgLayer layer)
    {
        var index = Layers.IndexOf(layer);
        if (index < 0) return;
        if (_timelineSelectionAnchorIndex < 0 || _timelineSelectionAnchorIndex >= Layers.Count)
        {
            SetLayerSelection([layer], layer, updateAnchor: true);
            return;
        }
        var a = Math.Min(_timelineSelectionAnchorIndex, index);
        var b = Math.Max(_timelineSelectionAnchorIndex, index);
        SetLayerSelection(Layers.Skip(a).Take(b - a + 1), layer);
    }

    public CgDataSource? SelectedDataSource
    {
        get => _selectedDataSource;
        set { if (ReferenceEquals(_selectedDataSource, value)) return; _selectedDataSource = value; Raise(); }
    }

    public CgGroup? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (ReferenceEquals(_selectedGroup, value)) return;
            _selectedGroup = value;
            GroupKeyframes.Clear();
            if (value is not null) foreach (var keyframe in value.Keyframes ?? []) GroupKeyframes.Add(keyframe);
            SelectedGroupKeyframe = GroupKeyframes.FirstOrDefault();
            Raise(); RenderPreview(); RenderTimeline();
        }
    }

    public CgKeyframe? SelectedGroupKeyframe
    {
        get => _selectedGroupKeyframe;
        set { if (ReferenceEquals(_selectedGroupKeyframe, value)) return; _selectedGroupKeyframe = value; Raise(); }
    }

    public CgKeyframe? SelectedKeyframe
    {
        get => _selectedKeyframe;
        set
        {
            if (ReferenceEquals(_selectedKeyframe, value)) return;
            _selectedKeyframe = value;
            if (_selectedKeyframe is not null && SelectedLayer is not null)
            {
                SelectedLayer.X = _selectedKeyframe.X;
                SelectedLayer.Y = _selectedKeyframe.Y;
                SelectedLayer.Z = _selectedKeyframe.Z;
                SelectedLayer.Width = _selectedKeyframe.Width;
                SelectedLayer.Height = _selectedKeyframe.Height;
                SelectedLayer.ScaleX = _selectedKeyframe.ScaleX;
                SelectedLayer.ScaleY = _selectedKeyframe.ScaleY;
                SelectedLayer.ScaleZ = _selectedKeyframe.ScaleZ;
                SelectedLayer.RotationX = _selectedKeyframe.RotationX;
                SelectedLayer.RotationY = _selectedKeyframe.RotationY;
                SelectedLayer.Rotation = _selectedKeyframe.Rotation;
                SelectedLayer.AnchorX = _selectedKeyframe.AnchorX;
                SelectedLayer.AnchorY = _selectedKeyframe.AnchorY;
                SelectedLayer.AnchorZ = _selectedKeyframe.AnchorZ;
                SelectedLayer.SkewX = _selectedKeyframe.SkewX;
                SelectedLayer.SkewY = _selectedKeyframe.SkewY;
                SelectedLayer.Opacity = _selectedKeyframe.Opacity;
                if (!string.IsNullOrWhiteSpace(_selectedKeyframe.StyleJson))
                {
                    CgAnimationEngine.ApplyStyleJson(SelectedLayer, _selectedKeyframe.StyleJson);
                }
                _autoKeyEditSnapshot = CaptureLayerEditSnapshot(SelectedLayer);
                Raise(nameof(SelectedLayer));
                RenderPreview();
            }
            Raise();
        }
    }

    public CgHtmlSource? SelectedHtml
    {
        get => _selectedHtml;
        set { if (ReferenceEquals(_selectedHtml, value)) return; _selectedHtml = value; Raise(); RenderPreview(); }
    }

    public double ProjectDuration => Math.Max(.5, SelectedProject?.DurationSeconds ?? 10);
    public double Playhead
    {
        get => _playhead;
        set { var v = Math.Clamp(value, 0, ProjectDuration); if (Math.Abs(_playhead - v) < .0001) return; _playhead = v; Raise(); Raise(nameof(PlayheadText)); Raise(nameof(RemainingText)); Raise(nameof(BoundDataSourceStatus)); RenderPreview(); RenderTimeline(); }
    }
    public string PlayheadText
    {
        get
        {
            var dynamicTickerDuration = CgDataSourceService.ResolveEffectiveTickerCycleDuration(SelectedProject);
            if (dynamicTickerDuration > 0.5 && _playing && ContinuousPlaybackSeconds > 0)
                return TimeSpan.FromSeconds(ContinuousPlaybackSeconds).ToString(@"mm\:ss\.ff");
            return TimeSpan.FromSeconds(Playhead).ToString(@"mm\:ss\.ff");
        }
    }
    public string RemainingText => TimeSpan.FromSeconds(Math.Max(0, ProjectDuration - Playhead)).ToString(@"mm\:ss\.ff");
    public string CanvasInfoText => $"{SelectedProject?.Width ?? 1920} x {SelectedProject?.Height ?? 1080} · {(SelectedProject?.FrameRate ?? 25):0.###} fps design canvas";
    public bool Loop { get => _loop; set { _loop = value; Raise(); } }
    public bool AutoKey { get => _autoKey; set { if (_autoKey == value) return; _autoKey = value; Raise(); } }
    public bool ShowCanvasGrid { get => _showCanvasGrid; set { if (_showCanvasGrid == value) return; _showCanvasGrid = value; Raise(); RenderPreview(); } }
    public double TimelineZoom
    {
        get => _timelineZoom;
        private set { var v=double.IsFinite(value)?Math.Clamp(value,.1,12.0):1.0; if(Math.Abs(_timelineZoom-v)<.0001)return; _timelineZoom=v; Raise(); Raise(nameof(TimelineZoomText)); ApplyTimelineZoom(); }
    }
    public string TimelineZoomText => $"{TimelineZoom * 100:0}%";
    public bool ShowSafeAreas
    {
        get => _showSafeAreas;
        set { if (_showSafeAreas == value) return; _showSafeAreas = value; Raise(); PersistSafeGuideSettings(); RenderPreview(); }
    }
    public bool ShowCenterGuide
    {
        get => _showCenterGuide;
        set { if (_showCenterGuide == value) return; _showCenterGuide = value; Raise(); PersistSafeGuideSettings(); RenderPreview(); }
    }
    public string SafeAreaPreset
    {
        get => _safeAreaPreset;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || string.Equals(_safeAreaPreset, value, StringComparison.OrdinalIgnoreCase)) return;
            _safeAreaPreset = value;
            switch (value)
            {
                case "ACTION 7.5% / TITLE 12.5%": _actionSafePercent = 7.5; _titleSafePercent = 12.5; break;
                case "ACTION 10% / TITLE 20%": _actionSafePercent = 10.0; _titleSafePercent = 20.0; break;
                default: _actionSafePercent = 5.0; _titleSafePercent = 10.0; break;
            }
            Raise(); Raise(nameof(SafeAreaDescription)); PersistSafeGuideSettings(); RenderPreview();
        }
    }
    public string SafeAreaDescription => $"Action {_actionSafePercent:0.#}% / Title {_titleSafePercent:0.#}%";

    private static string SafePresetFromValues(double action, double title)
    {
        if (Math.Abs(action - 7.5) < .01 && Math.Abs(title - 12.5) < .01) return "ACTION 7.5% / TITLE 12.5%";
        if (Math.Abs(action - 10.0) < .01 && Math.Abs(title - 20.0) < .01) return "ACTION 10% / TITLE 20%";
        return "ACTION 5% / TITLE 10%";
    }

    private void PersistSafeGuideSettings()
    {
        try
        {
            var settings = SettingsStore.Load();
            settings.CgEditorShowSafeAreas = _showSafeAreas;
            settings.CgEditorShowCenterGuide = _showCenterGuide;
            settings.CgEditorActionSafePercent = _actionSafePercent;
            settings.CgEditorTitleSafePercent = _titleSafePercent;
            SettingsStore.Save(settings);
        }
        catch { }
    }

    private void AddLayer(CgLayer layer)
    {
        RecordUndoSnapshot();
        if (SelectedProject is null) NewProject();
        if (layer.StartSeconds < 0) layer.StartSeconds = 0;
        var dur = layer.EndSeconds > layer.StartSeconds && layer.EndSeconds < ProjectDuration
            ? (layer.EndSeconds - layer.StartSeconds)
            : 5.0;
        layer.StartSeconds = Math.Clamp(Playhead, 0, Math.Max(0, ProjectDuration - 0.5));
        layer.EndSeconds = Math.Min(ProjectDuration, layer.StartSeconds + Math.Max(0.5, dur));
        // New objects are intentionally neutral. Group membership and IN/OUT animation
        // are explicit operator choices, never hidden defaults inherited from the UI state.
        if (string.IsNullOrWhiteSpace(layer.AnimationIn) || layer.AnimationIn.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            layer.AnimationIn = "None";
            layer.AnimationInSeconds = 0;
        }
        if (string.IsNullOrWhiteSpace(layer.AnimationOut) || layer.AnimationOut.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            layer.AnimationOut = "None";
            layer.AnimationOutSeconds = 0;
        }
        Layers.Add(layer); SelectedLayer = layer; SyncLayersToProject(); RenderAll();
    }

    private void NewProject()
    {
        var project = new CgProject { Name = $"Graphic {Projects.Count + 1}", Width = 1920, Height = 1080, FrameRate = 25, DurationSeconds = 10 };
        Projects.Add(project); SelectedProject = project;
    }

    private void CreateProject(string name, int width, int height, double frameRate, double durationSeconds)
    {
        var project = new CgProject
        {
            Name = string.IsNullOrWhiteSpace(name) ? $"Graphic {Projects.Count + 1}" : name.Trim(),
            Width = Math.Clamp(width, 16, 16384), Height = Math.Clamp(height, 16, 16384),
            FrameRate = double.IsFinite(frameRate) ? Math.Clamp(frameRate, 1, 240) : 25,
            DurationSeconds = double.IsFinite(durationSeconds) ? Math.Clamp(durationSeconds, .5, 3600) : 10
        };
        Projects.Add(project); SelectedProject = project;
        _main.ActiveCgProject = project; _main.SaveCgProjects();
    }

    private void LoadDemo_Click(object sender, RoutedEventArgs e)
    {
        SyncLayersToProject();
        Projects.Clear();
        foreach (var project in CgDemoFactory.CreateDefaults(forceRefresh: true)) Projects.Add(project);
        SelectedProject = Projects.FirstOrDefault(x => x.Name.Contains("AP1 HD", StringComparison.OrdinalIgnoreCase))
            ?? Projects.FirstOrDefault(x => x.Name.Contains("Prime HD", StringComparison.OrdinalIgnoreCase) && x.Name.Contains("Breaking", StringComparison.OrdinalIgnoreCase))
            ?? Projects.FirstOrDefault();
        _main.ActiveCgProject = SelectedProject;
        _main.SaveCgProjects();
        ProjectView.Refresh();
        Raise(nameof(TemplateCountText));
        RenderAll();
    }

    private void NewProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new NewCgProjectDialog($"Graphic {Projects.Count + 1}") { Owner = this, Icon = Icon };
        if (dialog.ShowDialog() == true)
            CreateProject(dialog.ProjectName, dialog.ProjectWidth, dialog.ProjectHeight, dialog.ProjectFrameRate, dialog.ProjectDurationSeconds);
    }
    private void AddText_Click(object sender, RoutedEventArgs e) => AddLayer(new CgLayer { Name="Text", Type="Text", Text="KASHTRIX TITLE", X=180, Y=780, Width=1050, Height=100, FontSize=48, UseBackground=false, Background="#00000000", AnimationIn="None", AnimationOut="None", AnimationInSeconds=0, AnimationOutSeconds=0 });
    private void AddTimer_Click(object sender, RoutedEventArgs e) => AddLayer(new CgLayer
    {
        Name = "Broadcast Timer",
        Type = "Timer",
        TimerMode = "CountDown",
        TimerStartSeconds = 3600,
        TimerFormat = "hh:mm:ss",
        TimerPrefix = "COUNTDOWN ",
        Text = "{Timer}",
        X = 180,
        Y = 820,
        Width = 450,
        Height = 80,
        FontSize = 38,
        Fill = "#FFFBBF24",
        Bold = true,
        UseBackground = false,
        Background = "#00000000",
        AnimationIn = "None",
        AnimationOut = "None",
        AnimationInSeconds = 0,
        AnimationOutSeconds = 0
    });
    private void AddCommercialBackIn_Click(object sender, RoutedEventArgs e) => AddLayer(new CgLayer
    {
        Name = "Commercial Back In",
        Type = "BackIn",
        TimerMode = "CommercialBackIn",
        TimerStartSeconds = 60,
        TimerFormat = "mm:ss",
        TimerPrefix = "BACK IN ",
        Text = "BACK IN {BackIn}",
        X = 180,
        Y = 820,
        Width = 450,
        Height = 80,
        FontSize = 38,
        Fill = "#FFFBBF24",
        Bold = true,
        UseBackground = false,
        Background = "#00000000",
        AnimationIn = "None",
        AnimationOut = "None",
        AnimationInSeconds = 0,
        AnimationOutSeconds = 0
    });
    private void AddRectangle_Click(object sender, RoutedEventArgs e) => AddLayer(new CgLayer { Name="Rectangle", Role="Shape", Type="Shape", ShapeKind="Rectangle", X=160, Y=760, Width=920, Height=150, Background="#D916212C", CornerRadius=0, CornerRadiusAsPercent=false, CornerRadiusTopLeftPercent=0, CornerRadiusTopRightPercent=0, CornerRadiusBottomRightPercent=0, CornerRadiusBottomLeftPercent=0, BorderWidth=0, BorderColor="#00000000", AnimationIn="None", AnimationOut="None", AnimationInSeconds=0, AnimationOutSeconds=0 });
    private void AddShape_Click(object sender, RoutedEventArgs e) => AddLayer(new CgLayer { Name="Shape", Role="Shape", Type="Shape", ShapeKind="Rectangle", X=160, Y=760, Width=920, Height=150, Background="#D916212C", CornerRadius=0, CornerRadiusAsPercent=false, CornerRadiusTopLeftPercent=0, CornerRadiusTopRightPercent=0, CornerRadiusBottomRightPercent=0, CornerRadiusBottomLeftPercent=0, BorderWidth=0, BorderColor="#00000000", AnimationIn="None", AnimationOut="None", AnimationInSeconds=0, AnimationOutSeconds=0 });
    private void AddPenShape_Click(object sender, RoutedEventArgs e) => BeginPenDrawing(createMask: false);
    private void AddPenMask_Click(object sender, RoutedEventArgs e) => BeginPenDrawing(createMask: true);

    private void BeginPenDrawing(bool createMask)
    {
        if (SelectedProject is null) NewProject();
        if (createMask && SelectedLayer is null)
        {
            MessageBox.Show("Select the layer that should receive the freeform mask, then choose PEN MASK.", "Pen Mask", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _penPoints.Clear();
        _penDrawing = true;
        _penCreatesMask = createMask;
        _penMaskTarget = createMask ? SelectedLayer : null;
        PreviewCanvas.Cursor = Cursors.Cross;
        PreviewCanvas.Focus();
        RenderPreview();
    }

    private void FinishPenDrawing(bool cancel = false)
    {
        if (!_penDrawing) return;
        var points = _penPoints.ToArray();
        var createsMask = _penCreatesMask;
        var maskTarget = _penMaskTarget;
        _penDrawing = false; _penCreatesMask = false; _penMaskTarget = null; _penPoints.Clear();
        PreviewCanvas.Cursor = Cursors.Arrow;
        if (cancel || points.Length < 2) { RenderPreview(); return; }

        var minX = points.Min(p => p.X); var minY = points.Min(p => p.Y);
        var maxX = points.Max(p => p.X); var maxY = points.Max(p => p.Y);
        var width = Math.Max(2, maxX - minX); var height = Math.Max(2, maxY - minY);
        var normalized = CgPathData.EncodeNormalized(points.Select(p =>
            new CgPathData.Point((p.X - minX) / width, (p.Y - minY) / height)));

        if (createsMask && maskTarget is not null)
        {
            RecordUndoSnapshot();
            maskTarget.MaskEnabled = true;
            maskTarget.MaskShape = "Custom Path";
            maskTarget.MaskX = minX; maskTarget.MaskY = minY;
            maskTarget.MaskWidth = width; maskTarget.MaskHeight = height;
            maskTarget.MaskPathData = normalized;
            SelectedLayer = maskTarget;
        }
        else
        {
            AddLayer(new CgLayer
            {
                Name = "Pen Shape", Role = "Shape", Type = "Shape", ShapeKind = "Custom Path",
                ShapePathData = normalized, ShapePathClosed = points.Length >= 3,
                X = minX, Y = minY, Width = width, Height = height,
                Background = "#CC8F4FD4", BorderColor = "#FFFFFFFF", BorderWidth = 2,
                AnimationIn = "None", AnimationOut = "None", AnimationInSeconds = 0, AnimationOutSeconds = 0
            });
        }
        SyncLayersToProject(); _main.SaveCgProjects(); Raise(nameof(SelectedLayer)); RenderAll();
    }
    private void AddEllipse_Click(object sender, RoutedEventArgs e) => AddLayer(new CgLayer { Name="Ellipse", Role="Shape", Type="Shape", ShapeKind="Ellipse", X=150, Y=150, Width=420, Height=260, Background="#B58F4FD4", BorderColor="#00000000", BorderWidth=0, AnimationIn="None", AnimationOut="None", AnimationInSeconds=0, AnimationOutSeconds=0 });
    private void AddLine_Click(object sender, RoutedEventArgs e) => AddLayer(new CgLayer { Name="Line", Role="Shape", Type="Shape", ShapeKind="Line", X=180, Y=720, Width=1100, Height=14, Background="#FFFFFFFF", BorderColor="#FFFFFFFF", BorderWidth=2, AnimationIn="None", AnimationOut="None", AnimationInSeconds=0, AnimationOutSeconds=0 });
    private void AddTicker_Click(object sender, RoutedEventArgs e) => AddLayer(new CgLayer { Name="Ticker", Type="Ticker", Text="KASHTRIX NEWS • LIVE • BUSINESS • SPORTS • WEATHER", X=0, Y=990, Width=1920, Height=90, FontSize=32, Background="#E60B1118", UseBackground=false, Speed=180, AnimationIn="None", AnimationOut="None", AnimationInSeconds=0, AnimationOutSeconds=0, TickerSeparator=" • ", TickerCategoriesEnabled=true });
    private void AddRoll_Click(object sender, RoutedEventArgs e) => AddLayer(new CgLayer { Name="Roll", Type="Roll", Text="HEADLINES\nFIRST STORY\nSECOND STORY\nTHIRD STORY", X=1350, Y=250, Width=480, Height=600, FontSize=34, Background="#C80B1118", UseBackground=false, Speed=48, AnimationIn="None", AnimationOut="None", AnimationInSeconds=0, AnimationOutSeconds=0 });
    private void AddDigital_Click(object sender, RoutedEventArgs e) => AddLayer(new CgLayer { Name="Date Time", Type="DateTime", Text="dd MMM yyyy  HH:mm:ss", DataFormat="dd MMM yyyy  HH:mm:ss", CalendarSystem="AD", X=1420, Y=120, Width=420, Height=80, FontSize=36, UseBackground=false, Background="#00000000", AnimationIn="None", AnimationOut="None", AnimationInSeconds=0, AnimationOutSeconds=0 });
    private void AddAnalog_Click(object sender, RoutedEventArgs e) => AddLayer(new CgLayer { Name="Analog Clock", Type="AnalogClock", X=1680, Y=220, Width=140, Height=140, Background="#B80A1017", AnimationIn="None", AnimationOut="None", AnimationInSeconds=0, AnimationOutSeconds=0 });
    private void AddProgramPip_Click(object sender, RoutedEventArgs e) => AddLayer(new CgLayer
    {
        Name = "Program Video PIP",
        Role = "VideoPIP",
        Type = "Shape",
        ShapeKind = "Rectangle",
        X = 80,
        Y = 60,
        Width = 1480,
        Height = 832,
        Background = "#00000000",
        BorderColor = "#00000000",
        BorderWidth = 0,
        SqueezeProgram = true,
        SqueezeHorizontalMode = "Free",
        SqueezeX = 80,
        SqueezeY = 60,
        SqueezeWidth = 1480,
        SqueezeHeight = 832,
        SqueezeInSeconds = 0.65,
        SqueezeOutSeconds = 0.65
    });
    private void AddWeatherIcon_Click(object sender, RoutedEventArgs e)
    {
        var weather = DataSources.FirstOrDefault(x => x.SourceType.Equals("OpenWeather", StringComparison.OrdinalIgnoreCase));
        AddLayer(new CgLayer { Name="Animated Weather Icon", Type="WeatherIcon", X=140, Y=220, Width=220, Height=220, DataSourceId=weather?.Id ?? Guid.Empty, DataField="icon", DataItemDurationSeconds=3600 });
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        var offset = 0.0;
        foreach (var dir in files.Where(Directory.Exists))
        {
            var validExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".webp" };
            var imgFiles = Directory.EnumerateFiles(dir).Where(f => validExts.Contains(IOPath.GetExtension(f))).OrderBy(f => f).ToList();
            if (imgFiles.Count > 0)
            {
                var frameCount = imgFiles.Count;
                double fps = 25.0;
                double duration = frameCount / fps;
                var (nativeWidth, nativeHeight) = ReadNativeImageSize(imgFiles[0]);
                var folderName = IOPath.GetFileName(dir.TrimEnd(IOPath.DirectorySeparatorChar, IOPath.AltDirectorySeparatorChar));
                AddLayer(new CgLayer
                {
                    Name = string.IsNullOrWhiteSpace(folderName) ? "Image Sequence" : folderName,
                    Type = "ImageSequence",
                    Source = dir,
                    X = 0,
                    Y = 0,
                    Width = nativeWidth,
                    Height = nativeHeight,
                    StartSeconds = 0,
                    EndSeconds = Math.Max(0.1, duration),
                    SequenceFps = fps,
                    SequenceStartFrame = 0,
                    SequenceEndFrame = frameCount - 1,
                    SequenceLoop = true,
                    SequenceHoldLastFrame = true
                });
            }
        }
        foreach (var file in files.Where(File.Exists))
        {
            var ext = IOPath.GetExtension(file).ToLowerInvariant();
            if (new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" }.Contains(ext))
            {
                var (nativeWidth, nativeHeight) = ReadNativeImageSize(file);
                AddLayer(new CgLayer { Name = IOPath.GetFileNameWithoutExtension(file), Type = "Image", Source = file, X = 140 + offset, Y = 160 + offset, Width = nativeWidth, Height = nativeHeight });
                offset += 24;
            }
            else if (new[] { ".mp4", ".mov", ".mxf", ".mkv", ".avi", ".webm", ".ts", ".m2ts" }.Contains(ext))
            {
                AddLayer(new CgLayer { Name = IOPath.GetFileNameWithoutExtension(file), Type = "Video", Source = file, VideoSourceKind="File", VideoIsLiveSource=false, X = 140 + offset, Y = 160 + offset, Width = 640, Height = 360 });
                offset += 24;
            }
            else if (ext is ".aep" or ".aepx" or ".aet")
            {
                await ImportAfterEffectsProjectAsync(file);
            }
            else if (ext is ".html" or ".htm")
            {
                AddHtmlSource(new CgHtmlSource { Name = IOPath.GetFileNameWithoutExtension(file), Source = file, SourceKind = "File", CanvasWidth = SelectedProject?.Width ?? 1920, CanvasHeight = SelectedProject?.Height ?? 1080, Width = SelectedProject?.Width ?? 1920, Height = SelectedProject?.Height ?? 1080 });
            }
        }
        e.Handled = true;
    }

    private void ImportLogo_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Filter="Logo images|*.png;*.tif;*.tiff;*.bmp;*.jpg;*.jpeg|All files|*.*" };
        if (d.ShowDialog() == true)
        {
            var (nativeWidth, nativeHeight) = ReadNativeImageSize(d.FileName);
            AddLayer(new CgLayer { Name=IOPath.GetFileNameWithoutExtension(d.FileName)+" Logo", Role="Logo", Type="Image", Source=d.FileName, X=1500, Y=60, Width=nativeWidth, Height=nativeHeight });
        }
    }

    private void ImportImage_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Filter="Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files|*.*" };
        if (d.ShowDialog() == true)
        {
            var (nativeWidth, nativeHeight) = ReadNativeImageSize(d.FileName);
            AddLayer(new CgLayer { Name=IOPath.GetFileNameWithoutExtension(d.FileName), Type="Image", Source=d.FileName, X=140, Y=160, Width=nativeWidth, Height=nativeHeight });
        }
    }
    private void ImportSequence_Click(object sender, RoutedEventArgs e)
    {
        using var d = new Forms.FolderBrowserDialog { Description="Select image sequence folder" };
        if (d.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(d.SelectedPath)) return;
        var validExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".webp" };
        var files = Directory.EnumerateFiles(d.SelectedPath).Where(f => validExts.Contains(IOPath.GetExtension(f))).OrderBy(f => f).ToList();
        var frameCount = files.Count;
        if (frameCount == 0)
        {
            MessageBox.Show(this, "The selected folder does not contain a supported image sequence.", "Import Image Sequence", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var (nativeWidth, nativeHeight) = ReadNativeImageSize(files[0]);
        double fps = 25.0;
        double duration = frameCount > 0 ? (frameCount / fps) : 5.0;
        var folderName = IOPath.GetFileName(d.SelectedPath.TrimEnd(IOPath.DirectorySeparatorChar, IOPath.AltDirectorySeparatorChar));
        var layer = new CgLayer
        {
            Name = string.IsNullOrWhiteSpace(folderName) ? "Image Sequence" : folderName,
            Type = "ImageSequence",
            Source = d.SelectedPath,
            X = 0,
            Y = 0,
            Width = nativeWidth,
            Height = nativeHeight,
            StartSeconds = 0,
            EndSeconds = Math.Max(0.1, duration),
            SequenceFps = fps,
            SequenceStartFrame = 0,
            SequenceEndFrame = Math.Max(0, frameCount - 1),
            SequenceLoop = true,
            SequenceHoldLastFrame = true
        };
        if (SelectedProject is not null && SelectedProject.Layers.Count == 0)
        {
            SelectedProject.DurationSeconds = Math.Max(0.5, duration);
            Raise(nameof(ProjectDuration));
        }
        AddLayer(layer);
    }

    private static (double Width, double Height) ReadNativeImageSize(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                using var img = System.Drawing.Image.FromFile(path);
                if (img.Width > 0 && img.Height > 0)
                    return (img.Width, img.Height);
            }
        }
        catch { }

        try
        {
            if (File.Exists(path))
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.None);
                var frame = decoder.Frames.FirstOrDefault();
                if (frame is not null && frame.PixelWidth > 0 && frame.PixelHeight > 0)
                    return (frame.PixelWidth, frame.PixelHeight);
            }
        }
        catch { }
        return (640, 360);
    }
    private void ImportVideo_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Filter="Video|*.mp4;*.mov;*.mxf;*.mkv;*.avi;*.webm;*.ts|All files|*.*" };
        if (d.ShowDialog() == true) AddLayer(new CgLayer { Name=IOPath.GetFileNameWithoutExtension(d.FileName), Type="Video", Source=d.FileName, VideoSourceKind="File", VideoIsLiveSource=false, X=140, Y=160, Width=640, Height=360 });
    }

    private async void ImportAfterEffects_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog
        {
            Filter = "Adobe After Effects projects|*.aep;*.aepx;*.aet|After Effects binary project|*.aep|After Effects XML project|*.aepx|After Effects template|*.aet|All files|*.*"
        };
        if (d.ShowDialog() == true) await ImportAfterEffectsProjectAsync(d.FileName);
    }

    private async Task ImportAfterEffectsProjectAsync(string projectPath)
    {
        var composition = PromptText(
            "After Effects composition",
            "Composition name to render. This is required so Kashtrix can render one deterministic composition through Adobe aerender.",
            string.Empty);
        if (composition is null) return;
        if (string.IsNullOrWhiteSpace(composition))
        {
            MessageBox.Show(this, "Enter the After Effects composition name to import.", "After Effects Import", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var fpsText = PromptText("After Effects frame rate", "Kashtrix image-sequence playback frame rate", "25");
        if (fpsText is null) return;
        if (!double.TryParse(fpsText, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps) &&
            !double.TryParse(fpsText, NumberStyles.Float, CultureInfo.CurrentCulture, out fps)) fps = 25;
        fps = Math.Clamp(fps, 1, 120);

        var outputModule = PromptText(
            "After Effects Output Module",
            "Name of an After Effects Output Module template that renders an image sequence with alpha. The default works when that template exists in After Effects.",
            "PNG Sequence with Alpha");
        if (outputModule is null) return;

        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            var result = await AfterEffectsImportService.RenderCompositionAsync(projectPath, composition, fps, outputModule);
            if (SelectedProject is null) NewProject();
            var project = SelectedProject;
            if (project is null) return;

            var duration = result.FrameCount / Math.Max(1, result.FrameRate);
            var layer = new CgLayer
            {
                Name = string.IsNullOrWhiteSpace(result.Composition)
                    ? IOPath.GetFileNameWithoutExtension(result.ProjectPath) + " · AE"
                    : result.Composition + " · AE",
                Type = "ImageSequence",
                Source = result.SequenceFolder,
                BlendMode = "Normal",
                X = 0,
                Y = 0,
                Width = project.Width,
                Height = project.Height,
                StartSeconds = 0,
                EndSeconds = Math.Max(.05, duration),
                SequenceFps = result.FrameRate,
                SequenceStartFrame = 0,
                SequenceEndFrame = -1,
                SequenceLoop = false,
                SequenceHoldLastFrame = true,
                ExternalProjectKind = "AfterEffects",
                ExternalProjectSource = result.ProjectPath,
                ExternalComposition = result.Composition
            };
            project.DurationSeconds = Math.Max(project.DurationSeconds, Math.Max(.05, duration));
            AddLayer(layer);
            _main.SaveCgProjects();
            MessageBox.Show(
                this,
                $"After Effects animation rendered and imported as an alpha image sequence.\n\nFrames: {result.FrameCount}\nSize: {result.Width}x{result.Height}\nFPS: {result.FrameRate:0.###}\nSource: {result.ProjectPath}\nComposition: {result.Composition}\n\nThe original AEP/AEPX remains linked as source metadata; Kashtrix plays the rendered animation natively.",
                "After Effects Import",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "After Effects Import", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private void AddVideoSource_Click(object sender, RoutedEventArgs e)
    {
        var layer = new CgLayer { Name="Video Source", Type="Video", X=0, Y=0, Width=320, Height=180, Background="#D010141C", VideoIsLiveSource=true };
        if (!ConfigureVideoLayerSource(layer)) return;
        // One source workflow only: after choosing File/URL/NDI/card/screen, draw its region on the canvas.
        _pendingVideoSourceLayer = layer;
        PreviewCanvas.Cursor = Cursors.Cross;
        PreviewCanvas.Focus();
    }

    // Kept as a source-compatible alias for old XAML/project integrations; the UI exposes only VIDEO SOURCE.
    private void DrawVideoSource_Click(object sender, RoutedEventArgs e) => AddVideoSource_Click(sender, e);

    private bool ConfigureVideoLayerSource(CgLayer layer)
    {
        var dialog=new InputSourceWindow { Owner=this };
        if(dialog.ShowDialog()!=true || dialog.ResultItem is not PlaylistItem item) return false;
        layer.Type="Video";
        layer.Name=string.IsNullOrWhiteSpace(item.Title)?"Video Source":item.Title;
        layer.Source=item.FilePath ?? string.Empty;
        layer.VideoSourceKind=string.IsNullOrWhiteSpace(item.SourceKind)?"File":item.SourceKind;
        layer.VideoInputFormat=item.InputFormat ?? string.Empty;
        layer.VideoInputOptions=item.InputOptions ?? string.Empty;
        layer.VideoDevice=item.VideoDevice ?? string.Empty;
        layer.AudioDevice=item.AudioDevice ?? string.Empty;
        layer.AlternateAudioUrl=item.AlternateAudioUrl ?? string.Empty;
        layer.VideoIsLiveSource=item.IsLiveSource || layer.VideoSourceKind.Equals("NDI",StringComparison.OrdinalIgnoreCase) || layer.VideoSourceKind.Equals("DirectShow",StringComparison.OrdinalIgnoreCase) || layer.VideoSourceKind.Equals("Screen",StringComparison.OrdinalIgnoreCase) || layer.VideoSourceKind.Equals("Custom",StringComparison.OrdinalIgnoreCase);
        layer.VideoCaptureWidth=item.CaptureWidth>0?item.CaptureWidth:1920;
        layer.VideoCaptureHeight=item.CaptureHeight>0?item.CaptureHeight:1080;
        layer.VideoSourceFrameRate=item.SourceFrameRate>0?item.SourceFrameRate:25;
        return true;
    }

    private void ConfigureVideoSource_Click(object sender, RoutedEventArgs e)
    {
        if(SelectedLayer is null || !SelectedLayer.Type.Equals("Video",StringComparison.OrdinalIgnoreCase)) return;
        if(AutoKey && _autoKeyEditSnapshot is null) CaptureAutoKeyEditSnapshot();
        if(!ConfigureVideoLayerSource(SelectedLayer)) return;
        CommitInspectorAutoKey(); SyncLayersToProject(); _main.SaveCgProjects(); Raise(nameof(SelectedLayer)); RenderAll();
    }
    private void ImportHtml_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Filter="HTML|*.html;*.htm|All files|*.*" };
        if (d.ShowDialog() == true) AddHtmlSource(new CgHtmlSource
        {
            Name=IOPath.GetFileNameWithoutExtension(d.FileName), Source=d.FileName, SourceKind="File",
            CanvasWidth=SelectedProject?.Width ?? 1920, CanvasHeight=SelectedProject?.Height ?? 1080,
            Width=SelectedProject?.Width ?? 1920, Height=SelectedProject?.Height ?? 1080
        });
    }

    private void ImportHtmlFolder_Click(object sender, RoutedEventArgs e)
    {
        using var d = new Forms.FolderBrowserDialog { Description="Select HTML graphics folder (index.html)" };
        if (d.ShowDialog() == Forms.DialogResult.OK) AddHtmlSource(new CgHtmlSource
        {
            Name=IOPath.GetFileName(d.SelectedPath.TrimEnd(IOPath.DirectorySeparatorChar)), Source=d.SelectedPath, SourceKind="Folder",
            CanvasWidth=SelectedProject?.Width ?? 1920, CanvasHeight=SelectedProject?.Height ?? 1080,
            Width=SelectedProject?.Width ?? 1920, Height=SelectedProject?.Height ?? 1080
        });
    }

    private void ImportHtmlUrl_Click(object sender, RoutedEventArgs e)
    {
        var url = PromptText("HTML / Chromium URL", "Enter http:// or https:// graphics URL", "https://");
        if (string.IsNullOrWhiteSpace(url)) return;
        AddHtmlSource(new CgHtmlSource
        {
            Name="Web Graphic", Source=url.Trim(), SourceKind="URL",
            CanvasWidth=SelectedProject?.Width ?? 1920, CanvasHeight=SelectedProject?.Height ?? 1080,
            Width=SelectedProject?.Width ?? 1920, Height=SelectedProject?.Height ?? 1080
        });
    }

    private void ImportKashGfx_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Filter="Kashtrix HTML Graphics|*.kashgfx|ZIP package|*.zip|All files|*.*" };
        if (d.ShowDialog() != true) return;
        try
        {
            var entry = ChromiumCgRenderer.Shared.ImportKashGfx(d.FileName);
            AddHtmlSource(new CgHtmlSource
            {
                Name=IOPath.GetFileNameWithoutExtension(d.FileName), Source=entry, SourceKind="KashGfx",
                CanvasWidth=SelectedProject?.Width ?? 1920, CanvasHeight=SelectedProject?.Height ?? 1080,
                Width=SelectedProject?.Width ?? 1920, Height=SelectedProject?.Height ?? 1080
            });
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Kashtrix HTML Package", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void AddHtmlSource(CgHtmlSource source)
    {
        if (SelectedProject is null) NewProject();
        HtmlSources.Add(source); SelectedHtml = source; SyncLayersToProject(); RenderPreview();
        _ = ChromiumCgRenderer.Shared.GetLatest(source);
    }

    private void DeleteHtml_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedHtml is null) return;
        var old = SelectedHtml; HtmlSources.Remove(old); ChromiumCgRenderer.Shared.Remove(old.Id);
        SelectedHtml = HtmlSources.FirstOrDefault(); SyncLayersToProject(); RenderPreview();
    }

    private void ReloadHtml_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedHtml is null) return; ChromiumCgRenderer.Shared.Reload(SelectedHtml); RenderPreview();
    }

    private async void UpdateHtmlData_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedHtml is null) return;
        try { await ChromiumCgRenderer.Shared.UpdateDataAsync(SelectedHtml, SelectedHtml.DataJson); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "HTML data", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void HtmlInspector_LostFocus(object sender, RoutedEventArgs e) { SyncLayersToProject(); RenderPreview(); }

    private static string? PromptText(string title, string caption, string initial, string acceptText = "LOAD")
    {
        var box = new TextBox { Text=initial, Margin=new Thickness(12,6,12,10), MinWidth=460 };
        var ok = new Button { Content=acceptText, Width=90, IsDefault=true, Margin=new Thickness(5) };
        var cancel = new Button { Content="CANCEL", Width=90, IsCancel=true, Margin=new Thickness(5) };
        var panel = new StackPanel(); panel.Children.Add(new TextBlock { Text=caption, Margin=new Thickness(12,12,12,2), Foreground=Brushes.White }); panel.Children.Add(box);
        var buttons = new StackPanel { Orientation=Orientation.Horizontal, HorizontalAlignment=HorizontalAlignment.Right, Margin=new Thickness(7) }; buttons.Children.Add(ok); buttons.Children.Add(cancel); panel.Children.Add(buttons);
        var win = new Window { Title=title, Width=520, Height=160, WindowStartupLocation=WindowStartupLocation.CenterScreen, ResizeMode=ResizeMode.NoResize, Background=new SolidColorBrush(Color.FromRgb(9,15,20)), Content=panel };
        WindowChromeActions.ApplyCleanBorder(win);
        ok.Click += (_,_) => win.DialogResult=true;
        return win.ShowDialog()==true ? box.Text : null;
    }


    private void AddDataSource(CgDataSource source)
    {
        if (SelectedProject is null) NewProject();
        DataSources.Add(source); SelectedDataSource = source; SyncLayersToProject(); _main.SaveCgProjects();
    }

    private void AddUrlJsonData_Click(object sender, RoutedEventArgs e)
    {
        var url = PromptText("URL JSON Data", "Enter JSON endpoint URL", "https://");
        if (string.IsNullOrWhiteSpace(url)) return;
        AddDataSource(new CgDataSource { Name="URL JSON", SourceType="UrlJson", Source=url.Trim(), RefreshSeconds=30 });
    }

    private void AddJsonData_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Filter="JSON|*.json|All files|*.*" };
        if (d.ShowDialog() == true) AddDataSource(new CgDataSource { Name=IOPath.GetFileNameWithoutExtension(d.FileName), SourceType="LocalJson", Source=d.FileName, RefreshSeconds=0 });
    }

    private void AddXmlRssData_Click(object sender, RoutedEventArgs e)
    {
        var value = PromptText("XML / RSS Data", "Enter RSS URL or local XML file path", "https://");
        if (string.IsNullOrWhiteSpace(value)) return;
        var type = value.StartsWith("http://",StringComparison.OrdinalIgnoreCase) || value.StartsWith("https://",StringComparison.OrdinalIgnoreCase) ? "Rss" : "Xml";
        AddDataSource(new CgDataSource { Name=type=="Rss"?"RSS Feed":"XML Data", SourceType=type, Source=value.Trim(), RefreshSeconds=type=="Rss"?60:0 });
    }

    private void AddExcelData_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Filter="Excel/CSV|*.xlsx;*.csv|Excel workbook|*.xlsx|CSV|*.csv|All files|*.*" };
        if (d.ShowDialog() != true) return;
        var ext=IOPath.GetExtension(d.FileName);
        AddDataSource(new CgDataSource { Name=IOPath.GetFileNameWithoutExtension(d.FileName), SourceType=ext.Equals(".csv",StringComparison.OrdinalIgnoreCase)?"Csv":"Excel", Source=d.FileName, RefreshSeconds=0 });
    }


    private void AddWeatherData_Click(object sender, RoutedEventArgs e)
    {
        var city = PromptText("OpenWeather 7-Day Forecast", "City (default Kathmandu, Nepal). Use City,CC where CC is country code.", "Kathmandu,NP");
        if (string.IsNullOrWhiteSpace(city)) return;
        AddDataSource(new CgDataSource
        {
            Name = "OpenWeather · " + city.Trim(), SourceType = "OpenWeather", Source = city.Trim(), RefreshSeconds = 900,
            CredentialEnvironmentVariable = "OPENWEATHER_API_KEY", Units = "metric", Language = "en",
            Status = "Cached/demo until OPENWEATHER_API_KEY is configured"
        });
    }

    private void DynamicFields_Click(object sender, RoutedEventArgs e)
    {
        var vm = Application.Current?.MainWindow?.DataContext as MainViewModel;
        var dlg = new BroadcastDynamicFieldsDialog(vm, initialTab: 0) { Owner = this };
        dlg.ShowDialog();
    }

    private void OpenNowNextFields_Click(object sender, RoutedEventArgs e)
    {
        var vm = Application.Current?.MainWindow?.DataContext as MainViewModel;
        var dlg = new BroadcastDynamicFieldsDialog(vm, initialTab: 0) { Owner = this };
        dlg.ShowDialog();
    }

    private void OpenWeatherFields_Click(object sender, RoutedEventArgs e)
    {
        var vm = Application.Current?.MainWindow?.DataContext as MainViewModel;
        var dlg = new BroadcastDynamicFieldsDialog(vm, initialTab: 1) { Owner = this };
        dlg.ShowDialog();
    }

    private void AddTxtData_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Filter="Text|*.txt;*.log|All files|*.*" };
        if (d.ShowDialog() == true) AddDataSource(new CgDataSource { Name=IOPath.GetFileNameWithoutExtension(d.FileName), SourceType="Txt", Source=d.FileName, RefreshSeconds=0 });
    }

    private async void RefreshData_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDataSource is null) return;
        try
        {
            SelectedDataSource.Status="LOADING…";
            var rows=await CgDataRuntime.Shared.RefreshAsync(SelectedDataSource);
            SelectedDataSource.Status=$"READY · {rows.Count} item(s)";
            SyncLayersToProject(); _main.SaveCgProjects(); RenderPreview();
        }
        catch(Exception ex){SelectedDataSource.Status="ERROR · "+ex.Message;MessageBox.Show(ex.Message,"CG Data Source",MessageBoxButton.OK,MessageBoxImage.Warning);}
    }

    private void DeleteData_Click(object sender, RoutedEventArgs e)
    {
        if(SelectedDataSource is null)return; var id=SelectedDataSource.Id; DataSources.Remove(SelectedDataSource);
        foreach(var l in Layers.Where(x=>x.DataSourceId==id))l.DataSourceId=Guid.Empty;
        SelectedDataSource=DataSources.FirstOrDefault();SyncLayersToProject();_main.SaveCgProjects();RenderAll();
    }
    private void DataInspector_LostFocus(object sender,RoutedEventArgs e){SyncLayersToProject();_main.SaveCgProjects();}
    private void DataInspector_SelectionChanged(object sender,SelectionChangedEventArgs e){if(!IsLoaded)return;SyncLayersToProject();_main.SaveCgProjects();}

    private void AddGroup_Click(object sender,RoutedEventArgs e)
    {
        if(SelectedProject is null)NewProject();var g=new CgGroup{Name=$"Group {Groups.Count+1}"};Groups.Add(g);SelectedGroup=g;SyncLayersToProject();_main.SaveCgProjects();
    }
    private void DeleteGroup_Click(object sender,RoutedEventArgs e)
    {
        if(SelectedGroup is null)return;var id=SelectedGroup.Id;Groups.Remove(SelectedGroup);foreach(var l in Layers.Where(x=>x.GroupId==id))l.GroupId=Guid.Empty;SelectedGroup=Groups.FirstOrDefault();SyncLayersToProject();RenderAll();
    }
    private void AssignGroup_Click(object sender,RoutedEventArgs e){if(SelectedLayer is null||SelectedGroup is null)return;SelectedLayer.GroupId=SelectedGroup.Id;SyncLayersToProject();RenderAll();}
    private void AddGroupKeyframe_Click(object sender,RoutedEventArgs e)
    {
        if(SelectedGroup is null)return;
        var k=new CgKeyframe{TimeSeconds=Playhead,X=0,Y=0,Width=0,Height=0,ScaleX=1,ScaleY=1,Opacity=1,Rotation=0,Ease=SelectedLayer?.DefaultEase??"power2.out"};
        SelectedGroup.Keyframes.Add(k);SortGroupKeyframes(k);SyncLayersToProject();RenderAll();
    }
    private void DeleteGroupKeyframe_Click(object sender,RoutedEventArgs e)
    {
        if(SelectedGroup is null||SelectedGroupKeyframe is null)return;
        SelectedGroup.Keyframes.Remove(SelectedGroupKeyframe);SortGroupKeyframes(null);SyncLayersToProject();RenderAll();
    }
    private void ClearGroupKeyframes_Click(object sender,RoutedEventArgs e){if(SelectedGroup is null)return;SelectedGroup.Keyframes.Clear();GroupKeyframes.Clear();SelectedGroupKeyframe=null;SyncLayersToProject();RenderAll();}
    private void SortGroupKeyframes(CgKeyframe? select)
    {
        if(SelectedGroup is null)return;
        var ordered=SelectedGroup.Keyframes.OrderBy(x=>x.TimeSeconds).ToList();SelectedGroup.Keyframes=ordered;GroupKeyframes.Clear();foreach(var k in ordered)GroupKeyframes.Add(k);SelectedGroupKeyframe=select is not null&&GroupKeyframes.Contains(select)?select:GroupKeyframes.FirstOrDefault();
    }
    private void GroupKeyframeInspector_LostFocus(object sender,RoutedEventArgs e){SortGroupKeyframes(SelectedGroupKeyframe);SyncLayersToProject();RenderAll();}
    private void GroupKeyframeInspector_SelectionChanged(object sender,SelectionChangedEventArgs e){if(!IsLoaded)return;SyncLayersToProject();RenderAll();}
    private void GroupKeyframeInspector_CheckChanged(object sender,RoutedEventArgs e){if(!IsLoaded)return;SyncLayersToProject();RenderAll();}

    private void GroupInspector_LostFocus(object sender,RoutedEventArgs e){SyncLayersToProject();RenderAll();}
    private void GroupInspector_CheckChanged(object sender,RoutedEventArgs e){if(!IsLoaded)return;SyncLayersToProject();RenderAll();}
    private void GroupInspector_SelectionChanged(object sender,SelectionChangedEventArgs e){if(!IsLoaded)return;SyncLayersToProject();RenderAll();}

    private static CgKeyframe KeyframeFromMotion(double time, CgAnimationEngine.MotionState state, string ease) => new()
    {
        TimeSeconds=time, X=state.X, Y=state.Y, Z=state.Z, Width=state.Width, Height=state.Height,
        RotationX=state.RotationX, RotationY=state.RotationY, Rotation=state.Rotation, Opacity=state.Opacity,
        ScaleX=state.ScaleX, ScaleY=state.ScaleY, ScaleZ=state.ScaleZ,
        AnchorX=state.AnchorX, AnchorY=state.AnchorY, AnchorZ=state.AnchorZ,
        SkewX=state.SkewX, SkewY=state.SkewY, BlurRadius=state.BlurRadius, Ease=ease
    };

    private CgKeyframe CaptureCurrentKeyframe(double time)
    {
        var l=SelectedLayer!;
        var state=CgAnimationEngine.EvaluateLayerLocal(l,time);
        var key=KeyframeFromMotion(time,state,l.DefaultEase);
        key.StyleJson=CgAnimationEngine.CaptureStyleJson(CgAnimationEngine.CreateVisualLayer(l,time));
        return key;
    }

    private LayerEditSnapshot CaptureLayerEditSnapshot(CgLayer layer) => new(
        layer,layer.X,layer.Y,layer.Z,layer.Width,layer.Height,layer.RotationX,layer.RotationY,layer.Rotation,layer.Opacity,
        layer.ScaleX,layer.ScaleY,layer.ScaleZ,layer.AnchorX,layer.AnchorY,layer.AnchorZ,CgAnimationEngine.CaptureStyleJson(layer));

    private void CaptureAutoKeyEditSnapshot()
    {
        if(!AutoKey||SelectedLayer is null){_autoKeyEditSnapshot=null;return;}
        _autoKeyEditSnapshot=CaptureLayerEditSnapshot(SelectedLayer);
    }

    private static bool Different(double a,double b)=>Math.Abs(a-b)>.0001;

    private void CommitInspectorAutoKey()
    {
        if (SelectedLayer is null) return;
        var layer = SelectedLayer;
        var styleNow = CgAnimationEngine.CaptureStyleJson(layer);

        // Case 1: An explicit keyframe is selected in the timeline
        if (SelectedKeyframe is not null && layer.Keyframes != null && layer.Keyframes.Contains(SelectedKeyframe))
        {
            SelectedKeyframe.X = layer.X;
            SelectedKeyframe.Y = layer.Y;
            SelectedKeyframe.Z = layer.Z;
            SelectedKeyframe.Width = layer.Width;
            SelectedKeyframe.Height = layer.Height;
            SelectedKeyframe.RotationX = layer.RotationX;
            SelectedKeyframe.RotationY = layer.RotationY;
            SelectedKeyframe.Rotation = layer.Rotation;
            SelectedKeyframe.Opacity = layer.Opacity;
            SelectedKeyframe.ScaleX = layer.ScaleX;
            SelectedKeyframe.ScaleY = layer.ScaleY;
            SelectedKeyframe.ScaleZ = layer.ScaleZ;
            SelectedKeyframe.AnchorX = layer.AnchorX;
            SelectedKeyframe.AnchorY = layer.AnchorY;
            SelectedKeyframe.AnchorZ = layer.AnchorZ;
            SelectedKeyframe.SkewX = layer.SkewX;
            SelectedKeyframe.SkewY = layer.SkewY;
            SelectedKeyframe.StyleJson = styleNow;
            _autoKeyEditSnapshot = CaptureLayerEditSnapshot(layer);
            return;
        }

        // Case 2: AutoKey is enabled
        if (!AutoKey) return;

        layer.Keyframes ??= [];
        const double tolerance = .002;

        if (Playhead <= tolerance)
        {
            var zero = layer.Keyframes.FirstOrDefault(k => Math.Abs(k.TimeSeconds) <= tolerance);
            if (zero is not null)
            {
                CopyLayerTransformToKeyframe(layer, zero, styleNow);
                _autoKeyEditSnapshot = CaptureLayerEditSnapshot(layer);
                return;
            }
        }

        if (layer.Keyframes.Count == 0)
        {
            var baseState = new CgAnimationEngine.MotionState(
                layer.X, layer.Y, layer.Z, layer.Width, layer.Height,
                layer.RotationX, layer.RotationY, layer.Rotation, layer.Opacity,
                layer.ScaleX, layer.ScaleY, layer.ScaleZ,
                layer.AnchorX, layer.AnchorY, layer.AnchorZ,
                layer.SkewX, layer.SkewY, layer.BlurRadius);
            var baseKey = KeyframeFromMotion(0, baseState, layer.DefaultEase);
            baseKey.StyleJson = styleNow;
            layer.Keyframes.Add(baseKey);
        }

        var key = layer.Keyframes.OrderBy(k => Math.Abs(k.TimeSeconds - Playhead)).FirstOrDefault(k => Math.Abs(k.TimeSeconds - Playhead) <= tolerance);
        if (key is null)
        {
            var state = CgAnimationEngine.EvaluateLayerLocal(layer, Playhead);
            key = KeyframeFromMotion(Playhead, state, layer.DefaultEase);
            layer.Keyframes.Add(key);
        }

        key.X = layer.X; key.Y = layer.Y; key.Z = layer.Z;
        key.Width = layer.Width; key.Height = layer.Height;
        key.RotationX = layer.RotationX; key.RotationY = layer.RotationY; key.Rotation = layer.Rotation;
        key.Opacity = layer.Opacity;
        key.ScaleX = layer.ScaleX; key.ScaleY = layer.ScaleY; key.ScaleZ = layer.ScaleZ;
        key.AnchorX = layer.AnchorX; key.AnchorY = layer.AnchorY; key.AnchorZ = layer.AnchorZ;
        key.SkewX = layer.SkewX; key.SkewY = layer.SkewY;
        key.StyleJson = styleNow;

        Keyframes.Clear();
        foreach (var frame in layer.Keyframes.OrderBy(k => k.TimeSeconds)) Keyframes.Add(frame);
        SelectedKeyframe = key;
        _autoKeyEditSnapshot = CaptureLayerEditSnapshot(layer);
        SyncLayersToProject();
        _main.SaveCgProjects();
        RenderAll();
    }

    private static void CopyLayerTransformToKeyframe(CgLayer layer, CgKeyframe key, string styleJson)
    {
        key.X = layer.X; key.Y = layer.Y; key.Z = layer.Z; key.Width = layer.Width; key.Height = layer.Height;
        key.RotationX = layer.RotationX; key.RotationY = layer.RotationY; key.Rotation = layer.Rotation; key.Opacity = layer.Opacity;
        key.ScaleX = layer.ScaleX; key.ScaleY = layer.ScaleY; key.ScaleZ = layer.ScaleZ; key.AnchorX = layer.AnchorX; key.AnchorY = layer.AnchorY; key.AnchorZ = layer.AnchorZ;
        key.SkewX = layer.SkewX; key.SkewY = layer.SkewY;
        key.StyleJson = styleJson;
    }

    private static CgKeyframe CloneKeyframe(CgKeyframe k) => new()
    {
        Id = Guid.NewGuid(),
        TimeSeconds = k.TimeSeconds,
        X = k.X, Y = k.Y, Z = k.Z,
        Width = k.Width, Height = k.Height,
        RotationX = k.RotationX, RotationY = k.RotationY, Rotation = k.Rotation,
        Opacity = k.Opacity,
        ScaleX = k.ScaleX, ScaleY = k.ScaleY, ScaleZ = k.ScaleZ,
        AnchorX = k.AnchorX, AnchorY = k.AnchorY, AnchorZ = k.AnchorZ,
        SkewX = k.SkewX, SkewY = k.SkewY,
        BlurRadius = k.BlurRadius,
        StyleJson = k.StyleJson,
        Ease = k.Ease,
        Hold = k.Hold,
        TargetProperty = k.TargetProperty
    };

    private CgKeyframe EnsureAutoKeyframe(CgLayer layer, double time)
    {
        layer.Keyframes ??= [];
        const double tolerance = 0.002;
        var existing = layer.Keyframes.OrderBy(x => Math.Abs(x.TimeSeconds - time)).FirstOrDefault(x => Math.Abs(x.TimeSeconds - time) <= tolerance);
        if (existing is not null)
        {
            if (ReferenceEquals(layer, SelectedLayer)) SelectedKeyframe = existing;
            return existing;
        }

        // First animated edit away from zero gets a base point at t=0 automatically. This makes
        // the very common workflow "move playhead -> resize/move" animate immediately instead
        // of creating a single static point. Existing authored keyframes are never rewritten.
        if (layer.Keyframes.Count == 0 && time > tolerance)
        {
            var baseState = CgAnimationEngine.MotionState.FromLayer(layer);
            var baseKey = KeyframeFromMotion(0, baseState, layer.DefaultEase); baseKey.StyleJson = CgAnimationEngine.CaptureStyleJson(layer);
            layer.Keyframes.Add(baseKey);
        }

        var state = CgAnimationEngine.EvaluateLayerLocal(layer, time);
        var key = KeyframeFromMotion(time, state, layer.DefaultEase); key.StyleJson = CgAnimationEngine.CaptureStyleJson(CgAnimationEngine.CreateVisualLayer(layer,time));
        layer.Keyframes.Add(key);
        if (ReferenceEquals(layer, SelectedLayer))
        {
            Keyframes.Clear();
            foreach (var item in layer.Keyframes.OrderBy(x => x.TimeSeconds)) Keyframes.Add(item);
            SelectedKeyframe = key;
        }
        return key;
    }
    private void AddKeyframe_Click(object sender,RoutedEventArgs e)
    {
        if(SelectedLayer is null)return;var k=CaptureCurrentKeyframe(Playhead);SelectedLayer.Keyframes.Add(k);Keyframes.Add(k);SelectedKeyframe=k;SortSelectedKeyframes();SyncLayersToProject();RenderAll();
    }
    private void CaptureKeyframe_Click(object sender,RoutedEventArgs e)
    {
        if (SelectedLayer is null) return;
        if (SelectedKeyframe is null) { AddKeyframe_Click(sender, e); return; }
        var captured = CaptureCurrentKeyframe(Playhead);
        SelectedKeyframe.TimeSeconds = captured.TimeSeconds;
        SelectedKeyframe.X=captured.X;SelectedKeyframe.Y=captured.Y;SelectedKeyframe.Z=captured.Z;
        SelectedKeyframe.Width=captured.Width;SelectedKeyframe.Height=captured.Height;
        SelectedKeyframe.RotationX=captured.RotationX;SelectedKeyframe.RotationY=captured.RotationY;SelectedKeyframe.Rotation=captured.Rotation;SelectedKeyframe.Opacity=captured.Opacity;
        SelectedKeyframe.ScaleX=captured.ScaleX;SelectedKeyframe.ScaleY=captured.ScaleY;SelectedKeyframe.ScaleZ=captured.ScaleZ;
        SelectedKeyframe.AnchorX=captured.AnchorX;SelectedKeyframe.AnchorY=captured.AnchorY;SelectedKeyframe.AnchorZ=captured.AnchorZ;
        SelectedKeyframe.SkewX=captured.SkewX;SelectedKeyframe.SkewY=captured.SkewY;
        SelectedKeyframe.BlurRadius = captured.BlurRadius; SelectedKeyframe.StyleJson = captured.StyleJson;
        SortSelectedKeyframes(); SyncLayersToProject(); _main.SaveCgProjects(); RenderAll();
    }
    private void DeleteKeyframe_Click(object sender,RoutedEventArgs e)
    {
        if(SelectedLayer is null||SelectedKeyframe is null)return;SelectedLayer.Keyframes.Remove(SelectedKeyframe);Keyframes.Remove(SelectedKeyframe);SelectedKeyframe=Keyframes.FirstOrDefault();SyncLayersToProject();RenderAll();
    }
    private void SortSelectedKeyframes()
    {
        if(SelectedLayer is null)return;var ordered=SelectedLayer.Keyframes.OrderBy(x=>x.TimeSeconds).ToList();SelectedLayer.Keyframes=ordered;Keyframes.Clear();foreach(var k in ordered)Keyframes.Add(k);if(SelectedKeyframe is not null&&!Keyframes.Contains(SelectedKeyframe))SelectedKeyframe=Keyframes.FirstOrDefault();
    }
    private void KeyframeInspector_LostFocus(object sender,RoutedEventArgs e){SortSelectedKeyframes();SyncLayersToProject();RenderAll();}
    private void KeyframeInspector_SelectionChanged(object sender,SelectionChangedEventArgs e){if(!IsLoaded)return;SyncLayersToProject();RenderAll();}
    private void KeyframeInspector_CheckChanged(object sender,RoutedEventArgs e){if(!IsLoaded)return;SyncLayersToProject();RenderAll();}

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedLayer is null) return;
        var l = SelectedLayer;
        AddLayer(CloneLayer(l, l.Name + " Copy", 30, 30));
    }
    private static CgLayer CloneLayer(CgLayer l, string name, double dx = 0, double dy = 0) => new()
    {
        Name=name, Type=l.Type, Role=l.Role, ShapeKind=l.ShapeKind, ShapePathData=l.ShapePathData, ShapePathClosed=l.ShapePathClosed, Text=l.Text, Source=l.Source, BlendMode=l.BlendMode, MaskEnabled=l.MaskEnabled, MaskShape=l.MaskShape, MaskPathData=l.MaskPathData, MaskX=l.MaskX+dx, MaskY=l.MaskY+dy, MaskWidth=l.MaskWidth, MaskHeight=l.MaskHeight, MaskCornerRadius=l.MaskCornerRadius, MaskFeather=l.MaskFeather, MaskInvert=l.MaskInvert, ExternalProjectSource=l.ExternalProjectSource, ExternalComposition=l.ExternalComposition, ExternalProjectKind=l.ExternalProjectKind, Precomposition=ClonePrecomposition(l.Precomposition), VideoSourceKind=l.VideoSourceKind, VideoInputFormat=l.VideoInputFormat, VideoInputOptions=l.VideoInputOptions, VideoDevice=l.VideoDevice, AudioDevice=l.AudioDevice, AlternateAudioUrl=l.AlternateAudioUrl, VideoIsLiveSource=l.VideoIsLiveSource, VideoCaptureWidth=l.VideoCaptureWidth, VideoCaptureHeight=l.VideoCaptureHeight, VideoSourceFrameRate=l.VideoSourceFrameRate,
        X=l.X+dx, Y=l.Y+dy, Z=l.Z, Width=l.Width, Height=l.Height, ScaleX=l.ScaleX, ScaleY=l.ScaleY, ScaleZ=l.ScaleZ, RotationX=l.RotationX, RotationY=l.RotationY, AnchorX=l.AnchorX, AnchorY=l.AnchorY, AnchorZ=l.AnchorZ, Perspective=l.Perspective, FontSize=l.FontSize, FontFamily=l.FontFamily,
        Fill=l.Fill, Background=l.Background, StartSeconds=l.StartSeconds, EndSeconds=l.EndSeconds,
        AnimationIn=l.AnimationIn, AnimationOut=l.AnimationOut, AnimationInSeconds=l.AnimationInSeconds, AnimationOutSeconds=l.AnimationOutSeconds, Speed=l.Speed, Rotation=l.Rotation, Opacity=l.Opacity,
        CornerRadius=l.CornerRadius, CornerRadiusTopLeft=l.CornerRadiusTopLeft, CornerRadiusTopRight=l.CornerRadiusTopRight, CornerRadiusBottomRight=l.CornerRadiusBottomRight, CornerRadiusBottomLeft=l.CornerRadiusBottomLeft, CornerRadiusAsPercent=l.CornerRadiusAsPercent, CornerRadiusTopLeftPercent=l.CornerRadiusTopLeftPercent, CornerRadiusTopRightPercent=l.CornerRadiusTopRightPercent, CornerRadiusBottomRightPercent=l.CornerRadiusBottomRightPercent, CornerRadiusBottomLeftPercent=l.CornerRadiusBottomLeftPercent, BorderColor=l.BorderColor, BorderWidth=l.BorderWidth,
        HorizontalTextAlignment=l.HorizontalTextAlignment, VerticalTextAlignment=l.VerticalTextAlignment,
        Bold=l.Bold, Italic=l.Italic, Underline=l.Underline, OutlineColor=l.OutlineColor, OutlineWidth=l.OutlineWidth,
        ShadowColor=l.ShadowColor, ShadowOffsetX=l.ShadowOffsetX, ShadowOffsetY=l.ShadowOffsetY, ShadowBlur=l.ShadowBlur,
        LetterSpacing=l.LetterSpacing, LineSpacing=l.LineSpacing, UseGradient=l.UseGradient, GradientColor2=l.GradientColor2, GradientAngle=l.GradientAngle, GradientStops=new ObservableCollection<CgGradientStop>((l.GradientStops??[]).Select(x=>new CgGradientStop { Color=x.Color, Offset=x.Offset })), GlossEnabled=l.GlossEnabled, GlossOpacity=l.GlossOpacity, TextAnimationPreset=l.TextAnimationPreset, TextAnimationUnit=l.TextAnimationUnit, TextAnimationDurationSeconds=l.TextAnimationDurationSeconds, TextAnimationStaggerSeconds=l.TextAnimationStaggerSeconds, TickerMode=l.TickerMode, TickerDirection=l.TickerDirection,
        TickerGap=l.TickerGap, TickerRepeat=l.TickerRepeat, SqueezeProgram=l.SqueezeProgram, SqueezeHorizontalMode=l.SqueezeHorizontalMode, SqueezeX=l.SqueezeX,
        SqueezeY=l.SqueezeY, SqueezeWidth=l.SqueezeWidth, SqueezeHeight=l.SqueezeHeight,
        SqueezeInSeconds=l.SqueezeInSeconds, SqueezeOutSeconds=l.SqueezeOutSeconds,
        GroupId=l.GroupId, DefaultEase=l.DefaultEase, KeyframeDelaySeconds=l.KeyframeDelaySeconds, KeyframeRepeat=l.KeyframeRepeat, KeyframeYoyo=l.KeyframeYoyo, DataSourceId=l.DataSourceId, DataField=l.DataField, DataFormat=l.DataFormat, CalendarSystem=l.CalendarSystem, TimeZoneId=l.TimeZoneId,
        DataItemDurationSeconds=l.DataItemDurationSeconds, DataItemOffset=l.DataItemOffset,
        SequenceFps=l.SequenceFps, SequenceStartFrame=l.SequenceStartFrame, SequenceEndFrame=l.SequenceEndFrame,
        SequenceLoop=l.SequenceLoop, SequenceHoldLastFrame=l.SequenceHoldLastFrame, SequenceAdvanceDataItem=l.SequenceAdvanceDataItem,
        SequenceOverlayIntervalItems=l.SequenceOverlayIntervalItems,
        UseBackground=l.UseBackground, AnimationGlowColor=l.AnimationGlowColor, AnimationGlowRadius=l.AnimationGlowRadius, AnimationBlurRadius=l.AnimationBlurRadius,
        Keyframes=l.Keyframes.Select(k=>new CgKeyframe { TimeSeconds=k.TimeSeconds,X=k.X+dx,Y=k.Y+dy,Z=k.Z,Width=k.Width,Height=k.Height,RotationX=k.RotationX,RotationY=k.RotationY,Rotation=k.Rotation,Opacity=k.Opacity,ScaleX=k.ScaleX,ScaleY=k.ScaleY,ScaleZ=k.ScaleZ,AnchorX=k.AnchorX,AnchorY=k.AnchorY,AnchorZ=k.AnchorZ,SkewX=k.SkewX,SkewY=k.SkewY,BlurRadius=k.BlurRadius,StyleJson=k.StyleJson,Ease=k.Ease,Hold=k.Hold }).ToList(),
        Visible=l.Visible
    };

    private static CgProject? ClonePrecomposition(CgProject? project)
    {
        if (project is null) return null;
        try { return JsonSerializer.Deserialize<CgProject>(JsonSerializer.Serialize(project)); }
        catch { return null; }
    }

    private void DeleteLayer_Click(object sender, RoutedEventArgs e) => DeleteSelectedLayers();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProject is null) return;
        SyncLayersToProject();
        _main.SaveCgProjects();
        ProjectView.Refresh();
        Raise(nameof(TemplateCountText));
    }
    private void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProject is null) return;
        var name = PromptText("Save CG As", "New name", SelectedProject.Name + " Copy", "SAVE AS");
        if (string.IsNullOrWhiteSpace(name)) return;
        SyncLayersToProject();
        var clone = System.Text.Json.JsonSerializer.Deserialize<CgProject>(
            System.Text.Json.JsonSerializer.Serialize(SelectedProject));
        if (clone is null) return;
        clone.Id = Guid.NewGuid();
        clone.Name = name.Trim();
        Projects.Add(clone);
        SelectedProject = clone;
        _main.SaveCgProjects();
        ProjectView.Refresh();
        Raise(nameof(TemplateCountText));
    }
    private void PipAspectRatio_SelectionChanged(object sender, SelectionChangedEventArgs e) => RenderPreview();
    private void ChannelRouting_Click(object sender, RoutedEventArgs e)
    {
        var project = RootComposition;
        if (project is null)
        {
            MessageBox.Show(this, "Select or create a graphic first.", "Kashtrix CG", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        SyncLayersToProject();
        var window = new CgChannelRoutingWindow(project) { Owner = this };
        window.Show();
    }

    private void TakeOnAir_Click(object sender, RoutedEventArgs e)
    {
        SyncLayersToProject();
        var project = RootComposition;
        if (project is null) return;
        _main.ActiveCgProject = project; _main.CgEnabled = true; _main.SaveCgProjects();
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_playing)
        {
            _playbackBaseSeconds = Playhead;
            _playbackClock.Stop();
            _playing = false;
            return;
        }
        // The editor opens demos on a representative visible frame; the first PLAY must
        // nevertheless run the complete animation from frame zero. Later presses resume.
        if (!_playbackPrimed || Playhead >= ProjectDuration - .001)
        {
            Playhead = 0;
            _playbackLoopIteration = 0;
        }
        _playbackPrimed = true;
        _playbackBaseSeconds = Playhead;
        _playbackClock.Restart();
        _playing = true;
    }
    private void ColorPicker_Click(object sender, RoutedEventArgs e)
    {
        if (AutoKey && SelectedLayer is not null && _autoKeyEditSnapshot is null) CaptureAutoKeyEditSnapshot();
        if (SelectedLayer is null || sender is not FrameworkElement { Tag: string property }) return;
        var initialText = property switch
        {
            "Background" => SelectedLayer.Background,
            "GradientColor2" => SelectedLayer.GradientColor2,
            "BorderColor" => SelectedLayer.BorderColor,
            "Fill" => SelectedLayer.Fill,
            "OutlineColor" => SelectedLayer.OutlineColor,
            "ShadowColor" => SelectedLayer.ShadowColor,
            "GlowColor" => SelectedLayer.GlowColor,
            "AnimationGlowColor" => SelectedLayer.AnimationGlowColor,
            "TickerCategoryBadgeBackground" => SelectedLayer.TickerCategoryBadgeBackground,
            _ => "#FFFFFFFF"
        };

        var picker = new KashtrixColorPickerWindow(initialText) { Owner = this };
        if (picker.ShowDialog() != true) return;
        var value = picker.SelectedColorText;
        switch (property)
        {
            case "Background": SelectedLayer.Background = value; break;
            case "GradientColor2": SelectedLayer.GradientColor2 = value; break;
            case "BorderColor": SelectedLayer.BorderColor = value; break;
            case "Fill": SelectedLayer.Fill = value; break;
            case "OutlineColor": SelectedLayer.OutlineColor = value; break;
            case "ShadowColor": SelectedLayer.ShadowColor = value; break;
            case "GlowColor": SelectedLayer.GlowColor = value; break;
            case "AnimationGlowColor": SelectedLayer.AnimationGlowColor = value; break;
            case "TickerCategoryBadgeBackground": SelectedLayer.TickerCategoryBadgeBackground = value; break;
        }
        CommitInspectorAutoKey(); SyncLayersToProject(); _main.SaveCgProjects(); Raise(nameof(SelectedLayer)); RenderAll();
    }

    private void ToggleCardHeader_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement header && header.Parent is Panel parent)
        {
            int idx = parent.Children.IndexOf(header);
            if (idx >= 0 && idx + 1 < parent.Children.Count)
            {
                var content = parent.Children[idx + 1];
                bool willBeVisible = content.Visibility != Visibility.Visible;
                content.Visibility = willBeVisible ? Visibility.Visible : Visibility.Collapsed;

                if (header is Panel p)
                {
                    foreach (var child in p.Children)
                    {
                        if (child is TextBlock tb && (tb.Text == "▼" || tb.Text == "▶"))
                        {
                            tb.Text = willBeVisible ? "▼" : "▶";
                            break;
                        }
                    }
                }
            }
        }
    }

    private void BrowseTickerSeparatorLogo_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedLayer is null) return;
        var d = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Ticker Separator Logo Image",
            Filter = "Image Files|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.tga|All Files|*.*"
        };
        if (d.ShowDialog() == true)
        {
            SelectedLayer.TickerSeparatorLogo = d.FileName;
            RenderAll();
        }
    }

    private System.Windows.Controls.Primitives.Popup? QuickPalettePopupControl =>
        FindName("QuickPalettePopup") as System.Windows.Controls.Primitives.Popup;

    private void ToggleQuickPaletteModal_Click(object sender, RoutedEventArgs e)
    {
        var popup = QuickPalettePopupControl;
        if (popup != null)
        {
            popup.IsOpen = !popup.IsOpen;
        }
    }

    private void ChannelBrandStyle_Click(object sender, RoutedEventArgs e)
    {
        var popup = QuickPalettePopupControl;
        if (popup != null) popup.IsOpen = false;
        if (sender is not FrameworkElement { Tag: string style }) return;
        ApplyChannelBrandStyle(style);
    }

    public void ApplyChannelBrandStyle(string style)
    {
        var (primary, secondary, plate, border, font) = style switch
        {
            "Ap1Hd" => ("#FF800818", "#FFFFFFFF", "#FF2D124D", "#FFEF233C", "Mukta"),
            "PrimeHd" => ("#FFAF0303", "#FFE5A93C", "#E609101D", "#FFFFD166", "Segoe UI"),
            "Space4k" => ("#FF0066FF", "#FF00E5FF", "#E609101E", "#FF00E5FF", "Segoe UI"),
            "RedAlert" => ("#FFD31027", "#FFFFD200", "#F0121721", "#FFFF3B30", "Arial"),
            "CrystalGlass" => ("#FF17508A", "#FF88D6F2", "#C9111A2C", "#55FFFFFF", "Century Gothic"),
            "SportsPro" => ("#FFFF9900", "#FF10B981", "#FF121B24", "#FFFFD166", "Trebuchet MS"),
            "FinancePulse" => ("#FF1E293B", "#FF22C55E", "#FF0B111A", "#FF22C55E", "Consolas"),
            "ElectionDesk" => ("#FF1E40AF", "#FFDC2626", "#FF0E1428", "#FFF59E0B", "Georgia"),
            "Weather3d" => ("#FF0284C7", "#FFF59E0B", "#FF081A2B", "#FF38BDF8", "Segoe UI"),
            "EntertainmentNeon" => ("#FFEC4899", "#FF8B5CF6", "#FF200B29", "#FFFDA4AF", "Century Gothic"),
            _ => ("#FF334155", "#FF94A3B8", "#FF0F172A", "#FFFFFFFF", "Bahnschrift")
        };

        if (AutoKey && SelectedLayer is not null && _autoKeyEditSnapshot is null) CaptureAutoKeyEditSnapshot();

        if (SelectedLayer is not null)
        {
            if (SelectedLayer.Type.Equals("Shape", StringComparison.OrdinalIgnoreCase))
            {
                SelectedLayer.Background = plate;
                SelectedLayer.GradientColor2 = primary;
                SelectedLayer.UseGradient = true;
                SelectedLayer.BorderColor = border;
                SelectedLayer.BorderWidth = 1.5;
            }
            else if (SelectedLayer.Type.Equals("Text", StringComparison.OrdinalIgnoreCase) || SelectedLayer.Type.Equals("Ticker", StringComparison.OrdinalIgnoreCase))
            {
                SelectedLayer.Fill = secondary;
                SelectedLayer.FontFamily = font;
                SelectedLayer.OutlineColor = "#99000000";
            }
        }
        else if (SelectedProject is not null)
        {
            foreach (var layer in SelectedProject.Layers)
            {
                if (layer.Type.Equals("Shape", StringComparison.OrdinalIgnoreCase))
                {
                    if (layer.Width > 1800 && layer.Height > 1000)
                    {
                        layer.Background = plate;
                    }
                    else
                    {
                        layer.Background = plate;
                        layer.GradientColor2 = primary;
                        layer.UseGradient = true;
                        layer.BorderColor = border;
                    }
                }
                else if (layer.Type.Equals("Text", StringComparison.OrdinalIgnoreCase) || layer.Type.Equals("Ticker", StringComparison.OrdinalIgnoreCase))
                {
                    layer.FontFamily = font;
                    if (layer.FontSize > 40) layer.Fill = "#FFFFFFFF";
                    else layer.Fill = secondary;
                }
            }
        }

        CommitInspectorAutoKey(); SyncLayersToProject(); _main.SaveCgProjects(); Raise(nameof(SelectedLayer)); RenderAll();
    }

    private void ShapePreset_Click(object sender, RoutedEventArgs e)
    {
        if (AutoKey && SelectedLayer is not null && _autoKeyEditSnapshot is null) CaptureAutoKeyEditSnapshot();
        if (SelectedLayer is null || sender is not FrameworkElement { Tag: string preset }) return;
        SelectedLayer.UseGradient = true; SelectedLayer.BorderWidth = 2;
        (SelectedLayer.Background, SelectedLayer.GradientColor2, SelectedLayer.BorderColor, SelectedLayer.GradientAngle) = preset switch
        {
            "NewsRed" => ("#FFE6294B", "#FF771A42", "#FFFFB3C1", 0d),
            "SportCyan" => ("#FF087F8C", "#FF19D3C5", "#FFB8FFF7", 8d),
            "PromoGold" => ("#FFF0A62E", "#FFFFD36A", "#FFFFF1BF", 0d),
            _ => ("#FF3B176C", "#FF8F4BFF", "#FFCFAEFF", 12d)
        };
        SelectedLayer.CornerRadiusAsPercent=true;
        SelectedLayer.CornerRadiusTopLeftPercent=12;SelectedLayer.CornerRadiusTopRightPercent=12;
        SelectedLayer.CornerRadiusBottomRightPercent=12;SelectedLayer.CornerRadiusBottomLeftPercent=12;
        CommitInspectorAutoKey(); SyncLayersToProject(); _main.SaveCgProjects(); Raise(nameof(SelectedLayer)); RenderAll();
    }

    private void TextColorPreset_Click(object sender, RoutedEventArgs e)
    {
        if (AutoKey && SelectedLayer is not null && _autoKeyEditSnapshot is null) CaptureAutoKeyEditSnapshot();
        if (SelectedLayer is null || sender is not FrameworkElement { Tag: string preset }) return;
        SelectedLayer.UseGradient = true;
        (SelectedLayer.Fill, SelectedLayer.GradientColor2, SelectedLayer.GradientAngle) = preset switch
        {
            "CyanMint" => ("#FF59E8FF", "#FF7CFFD7", 0d),
            "GoldWhite" => ("#FFFFB53D", "#FFFFFFFF", 0d),
            "RedPink" => ("#FFFF4C5F", "#FFFF66C4", 0d),
            _ => ("#FFFFFFFF", "#FF9A64FF", 0d)
        };
        CommitInspectorAutoKey(); SyncLayersToProject(); _main.SaveCgProjects(); Raise(nameof(SelectedLayer)); RenderAll();
    }

    private void EnsureGradientStops(CgLayer layer)
    {
        layer.GradientStops ??= [];
        if (layer.GradientStops.Count == 0)
        {
            layer.GradientStops.Add(new CgGradientStop { Color = layer.Type.Equals("Shape", StringComparison.OrdinalIgnoreCase) ? layer.Background : layer.Fill, Offset = 0 });
            layer.GradientStops.Add(new CgGradientStop { Color = layer.GradientColor2, Offset = 1 });
        }
    }

    private void AddGradientStop_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedLayer is null) return;
        EnsureGradientStops(SelectedLayer); SelectedLayer.UseGradient = true;
        var stops=SelectedLayer.GradientStops.OrderBy(x=>x.Offset).ToArray();
        var offset = stops.Length < 2 ? .5 : Enumerable.Range(0, stops.Length - 1).Select(i => (A: stops[i], B: stops[i + 1], Gap: stops[i + 1].Offset - stops[i].Offset)).OrderByDescending(x => x.Gap).Select(x => (x.A.Offset + x.B.Offset) / 2).FirstOrDefault();
        var stop=new CgGradientStop { Color=SelectedLayer.GradientColor2, Offset=Math.Clamp(offset,0,1) };
        SelectedLayer.GradientStops.Add(stop); SelectedGradientStop=stop; CommitInspectorAutoKey(); SyncLayersToProject(); _main.SaveCgProjects(); Raise(nameof(SelectedLayer)); RenderAll();
    }
    private void RemoveGradientStop_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedLayer is null || SelectedGradientStop is null) return;
        SelectedLayer.GradientStops.Remove(SelectedGradientStop); SelectedGradientStop=SelectedLayer.GradientStops.FirstOrDefault();
        CommitInspectorAutoKey(); SyncLayersToProject(); _main.SaveCgProjects(); RenderAll();
    }
    private void PickGradientStopColor_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedLayer is null || sender is not FrameworkElement { DataContext: CgGradientStop stop }) return;
        var picker=new KashtrixColorPickerWindow(stop.Color) { Owner=this }; if(picker.ShowDialog()!=true)return;
        stop.Color=picker.SelectedColorText; CommitInspectorAutoKey(); SyncLayersToProject(); _main.SaveCgProjects(); RenderAll();
    }
    private void GradientStop_LostFocus(object sender, RoutedEventArgs e)
    {
        if (SelectedLayer is null) return;
        foreach(var stop in SelectedLayer.GradientStops) stop.Offset=FiniteUnit(stop.Offset);
        CommitInspectorAutoKey(); SyncLayersToProject(); _main.SaveCgProjects(); RenderAll();
    }

    private void NepaliFormatPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || SelectedLayer is null || sender is not ComboBox cb) return;
        var layer = SelectedLayer;
        if (layer is null) return;
        if (cb.SelectedItem is string rawFmt && !string.IsNullOrWhiteSpace(rawFmt))
        {
            if (AutoKey && _autoKeyEditSnapshot is null) CaptureAutoKeyEditSnapshot();
            // Clean format pattern without parenthetical descriptions
            var cleanFmt = System.Text.RegularExpressions.Regex.Replace(rawFmt, @"\s*\([^)]*\)", "").Trim();
            layer.DataFormat = cleanFmt;
            layer.Text = NepaliCalendarService.ResolveBroadcastFormat(rawFmt, DateTime.Now);
            CommitInspectorAutoKey();
            SyncLayersToProject();
            _main.SaveCgProjects();
            Raise(nameof(SelectedLayer));
            RenderAll();
        }
    }

    private void CalendarSystem_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || SelectedLayer is null || sender is not ComboBox { SelectedItem: string system }) return;
        SelectedLayer.CalendarSystem = system;
        if (system.Equals("BS", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(SelectedLayer.DataFormat) || SelectedLayer.DataFormat == "{0}" || SelectedLayer.DataFormat.Contains("yyyy", StringComparison.Ordinal))
                SelectedLayer.DataFormat = "dddd, DD MMMM YYYY  HH:mm:ss";
        }
        else if (SelectedLayer.DataFormat.Contains("YYYY", StringComparison.Ordinal) || SelectedLayer.DataFormat.Contains("DD", StringComparison.Ordinal))
        {
            SelectedLayer.DataFormat = "dd MMM yyyy  HH:mm:ss";
        }
        SyncLayersToProject();
        _main.SaveCgProjects();
        Raise(nameof(SelectedLayer));
        Raise(nameof(DateTimeFormatPresets));
        Raise(nameof(NepaliFormatPresets));
        RenderAll();
    }

    private void TextAnimationPresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || SelectedLayer is null || sender is not ComboBox cb || (!cb.IsDropDownOpen && !cb.IsKeyboardFocusWithin)) return;
        if (cb.SelectedItem is string preset && !string.IsNullOrWhiteSpace(preset))
        {
            ApplyTextAnimationPreset(preset);
        }
    }

    private void TextAnimationPreset_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedLayer is null || sender is not FrameworkElement { Tag: string preset }) return;
        ApplyTextAnimationPreset(preset);
    }

    private void ApplyTextAnimationPreset(string preset)
    {
        if (AutoKey && SelectedLayer is not null && _autoKeyEditSnapshot is null) CaptureAutoKeyEditSnapshot();
        if (SelectedLayer is null) return;
        SelectedLayer.TextAnimationPreset = preset;
        switch (preset)
        {
            case "Slide Left":
                SelectedLayer.TextAnimationUnit = "Word"; SelectedLayer.TextAnimationDurationSeconds = 0.65; SelectedLayer.TextAnimationStaggerSeconds = 0.06; break;
            case "Slide Right":
                SelectedLayer.TextAnimationUnit = "Word"; SelectedLayer.TextAnimationDurationSeconds = 0.65; SelectedLayer.TextAnimationStaggerSeconds = 0.06; break;
            case "Push Left":
                SelectedLayer.TextAnimationUnit = "Whole"; SelectedLayer.TextAnimationDurationSeconds = 0.55; SelectedLayer.TextAnimationStaggerSeconds = 0.05; break;
            case "Push Right":
                SelectedLayer.TextAnimationUnit = "Whole"; SelectedLayer.TextAnimationDurationSeconds = 0.55; SelectedLayer.TextAnimationStaggerSeconds = 0.05; break;
            case "Slide Up":
                SelectedLayer.TextAnimationUnit = "Word"; SelectedLayer.TextAnimationDurationSeconds = 0.60; SelectedLayer.TextAnimationStaggerSeconds = 0.05; break;
            case "Slide Down":
                SelectedLayer.TextAnimationUnit = "Word"; SelectedLayer.TextAnimationDurationSeconds = 0.60; SelectedLayer.TextAnimationStaggerSeconds = 0.05; break;
            case "Date / Time Split":
            case "Date / Time Flip":
                SelectedLayer.TextAnimationUnit = "Word"; SelectedLayer.TextAnimationDurationSeconds = 0.85; SelectedLayer.TextAnimationStaggerSeconds = 0.08; break;
            case "Type On":
                SelectedLayer.TextAnimationUnit = "Letter"; SelectedLayer.TextAnimationDurationSeconds = 1.0; SelectedLayer.TextAnimationStaggerSeconds = .035; break;
            case "Typewriter + Blur":
                SelectedLayer.TextAnimationUnit = "Letter"; SelectedLayer.TextAnimationDurationSeconds = 1.0; SelectedLayer.TextAnimationStaggerSeconds = .035; SelectedLayer.AnimationBlurRadius = 8.0; break;
            case "Blur Dissolve":
                SelectedLayer.TextAnimationUnit = "Word"; SelectedLayer.TextAnimationDurationSeconds = 0.8; SelectedLayer.TextAnimationStaggerSeconds = .06; SelectedLayer.AnimationBlurRadius = 12.0; break;
            case "Fade Cascade":
            case "Soft Reveal":
            case "Fade":
                SelectedLayer.TextAnimationUnit = "Word"; SelectedLayer.TextAnimationDurationSeconds = .7; SelectedLayer.TextAnimationStaggerSeconds = .10; break;
            case "Rise Cascade":
                SelectedLayer.TextAnimationUnit = "Word"; SelectedLayer.TextAnimationDurationSeconds = .75; SelectedLayer.TextAnimationStaggerSeconds = .09; break;
            case "Tracking Reveal":
                SelectedLayer.TextAnimationUnit = "Letter"; SelectedLayer.TextAnimationDurationSeconds = .9; SelectedLayer.TextAnimationStaggerSeconds = .025; break;
            case "Pop Cascade":
            case "Pop":
                SelectedLayer.TextAnimationUnit = "Word"; SelectedLayer.TextAnimationDurationSeconds = .65; SelectedLayer.TextAnimationStaggerSeconds = .075; break;
            case "Slide Letters":
                SelectedLayer.TextAnimationUnit = "Letter"; SelectedLayer.TextAnimationDurationSeconds = .80; SelectedLayer.TextAnimationStaggerSeconds = .028; break;
            case "Flip In":
                SelectedLayer.TextAnimationUnit = "Letter"; SelectedLayer.TextAnimationDurationSeconds = .72; SelectedLayer.TextAnimationStaggerSeconds = .032; break;
            case "Wipe Words":
                SelectedLayer.TextAnimationUnit = "Word"; SelectedLayer.TextAnimationDurationSeconds = .85; SelectedLayer.TextAnimationStaggerSeconds = .07; break;
            case "Glow Pulse":
                SelectedLayer.TextAnimationUnit = "Whole"; SelectedLayer.TextAnimationDurationSeconds = 1.2; SelectedLayer.TextAnimationStaggerSeconds = .05; SelectedLayer.AnimationGlowRadius = 14.0; break;
            case "Bounce / Wave":
                SelectedLayer.TextAnimationUnit = "Letter"; SelectedLayer.TextAnimationDurationSeconds = 1.1; SelectedLayer.TextAnimationStaggerSeconds = .04; break;
            case "Zoom":
                SelectedLayer.TextAnimationUnit = "Word"; SelectedLayer.TextAnimationDurationSeconds = 0.70; SelectedLayer.TextAnimationStaggerSeconds = 0.06; SelectedLayer.TextAnimationScaleStart = 2.0; SelectedLayer.TextAnimationScaleEnd = 1.0; break;
            case "Scale Down":
            case "Scale Down (Letter + Blur + Glow)":
                SelectedLayer.TextAnimationUnit = "Word"; SelectedLayer.TextAnimationDurationSeconds = 0.75; SelectedLayer.TextAnimationStaggerSeconds = 0.06; SelectedLayer.TextAnimationScaleStart = 2.5; SelectedLayer.TextAnimationScaleEnd = 1.0; SelectedLayer.AnimationBlurRadius = 6.0; break;
            case "Scale Up":
                SelectedLayer.TextAnimationUnit = "Word"; SelectedLayer.TextAnimationDurationSeconds = 0.75; SelectedLayer.TextAnimationStaggerSeconds = 0.06; SelectedLayer.TextAnimationScaleStart = 0.2; SelectedLayer.TextAnimationScaleEnd = 1.0; break;
            case "Wiggle":
                SelectedLayer.TextAnimationUnit = "Letter"; SelectedLayer.TextAnimationDurationSeconds = 0.80; SelectedLayer.TextAnimationStaggerSeconds = 0.04; break;
            case "Jump":
                SelectedLayer.TextAnimationUnit = "Letter"; SelectedLayer.TextAnimationDurationSeconds = 0.80; SelectedLayer.TextAnimationStaggerSeconds = 0.04; break;
            case "None":
            default:
                SelectedLayer.TextAnimationUnit = "Whole"; break;
        }
        CommitInspectorAutoKey(); SyncLayersToProject(); _main.SaveCgProjects(); Raise(nameof(SelectedLayer)); RenderAll();
    }

    private void Inspector_SliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        SyncLayersToProject();
        RequestInteractiveRender();
    }

    private void ApplyFontToSelectedLayer(string fontName)
    {
        if (SelectedLayer is null || string.IsNullOrWhiteSpace(fontName)) return;
        SelectedLayer.FontFamily = fontName;
        foreach (var k in SelectedLayer.Keyframes ?? [])
        {
            if (!string.IsNullOrWhiteSpace(k.StyleJson))
            {
                try
                {
                    var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(k.StyleJson);
                    if (dict is not null)
                    {
                        var newDict = new Dictionary<string, object?>();
                        foreach (var kv in dict)
                        {
                            if (kv.Key.Equals("FontFamily", StringComparison.OrdinalIgnoreCase))
                                newDict[kv.Key] = fontName;
                            else
                                newDict[kv.Key] = kv.Value;
                        }
                        newDict["FontFamily"] = fontName;
                        k.StyleJson = JsonSerializer.Serialize(newDict);
                    }
                }
                catch { }
            }
        }
        CommitInspectorAutoKey();
        SyncLayersToProject();
        _main.SaveCgProjects();
        Raise(nameof(SelectedLayer));
        RenderAll();
    }

    private void FontFamily_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedLayer is null || sender is not ComboBox cb) return;
        if (cb.SelectedItem is string fontName && !string.IsNullOrWhiteSpace(fontName))
        {
            ApplyFontToSelectedLayer(fontName);
        }
    }

    private void FontFamily_KeyUp(object sender, KeyEventArgs e)
    {
        if (SelectedLayer is null || sender is not ComboBox cb) return;
        if (!string.IsNullOrWhiteSpace(cb.Text))
        {
            ApplyFontToSelectedLayer(cb.Text);
        }
    }

    private void UpdateCanvasAspect()
    {
        if (PreviewCanvas is null || SelectedProject is null) return;
        var pw = Math.Max(16.0, SelectedProject.Width);
        var ph = Math.Max(16.0, SelectedProject.Height);
        var aspect = pw / ph;
        var targetW = 960.0;
        var targetH = targetW / aspect;
        if (targetH > 720.0)
        {
            targetH = 720.0;
            targetW = targetH * aspect;
        }
        PreviewCanvas.Width = targetW;
        PreviewCanvas.Height = targetH;
        if (PreviewCanvasBorder is not null)
        {
            PreviewCanvasBorder.Width = targetW;
            PreviewCanvasBorder.Height = targetH;
        }
    }

    private void Inspector_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (e.OriginalSource is not TextBox tb || tb.IsReadOnly) return;
        var binding = tb.GetBindingExpression(TextBox.TextProperty);
        if (binding is null) return;
        var propPath = binding.ParentBinding?.Path?.Path ?? "";
        if (string.IsNullOrWhiteSpace(propPath)) return;

        // When the user is typing decimals or values into a focused textbox with LostFocus or Default trigger,
        // do NOT prematurely push intermediate text (like "0." or "-") back into the model on every keystroke,
        // which causes WPF two-way binding to wipe the decimal point back to "0".
        if (tb.IsFocused && (binding.ParentBinding?.UpdateSourceTrigger == UpdateSourceTrigger.LostFocus ||
                             binding.ParentBinding?.UpdateSourceTrigger == UpdateSourceTrigger.Default))
        {
            return;
        }

        binding.UpdateSource();
        if (propPath.EndsWith("SequenceOverlayIntervalItems", StringComparison.OrdinalIgnoreCase) && SelectedLayer is not null)
        {
            var val = SelectedLayer.SequenceOverlayIntervalItems;
            foreach (var sibling in Layers)
            {
                if (sibling == SelectedLayer) continue;
                if ((SelectedLayer.GroupId != Guid.Empty && sibling.GroupId == SelectedLayer.GroupId) ||
                    (SelectedLayer.DataSourceId != Guid.Empty && sibling.DataSourceId == SelectedLayer.DataSourceId))
                {
                    sibling.SequenceOverlayIntervalItems = val;
                }
            }
            Raise(nameof(BoundDataSourceStatus));
        }
        SyncLayersToProject();
        if (propPath.StartsWith("SelectedProject", StringComparison.OrdinalIgnoreCase))
        {
            UpdateCanvasAspect();
            Raise(nameof(SelectedProject));
            Raise(nameof(ProjectDuration));
            Raise(nameof(CanvasInfoText));
            Raise(nameof(PlayheadText));
            Raise(nameof(RemainingText));
            _main.SaveCgProjects();
            RequestInteractiveRender(includeTimeline: true);
        }
        else
        {
            RequestInteractiveRender(includeTimeline: propPath.Contains("Seconds") || propPath.Contains("Time"));
        }
    }

    private void NumericInspector_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.OriginalSource is not TextBox tb || tb.IsReadOnly) return;
        // Only adjust numeric properties via mouse wheel when the user has explicitly focused the textbox.
        // Otherwise, allow the mouse wheel event to bubble naturally so the inspector scrollbar scrolls!
        if (!tb.IsFocused) return;
        var binding = tb.GetBindingExpression(TextBox.TextProperty);
        if (binding is null) return;
        var propPath = binding.ParentBinding?.Path?.Path ?? "";
        if (string.IsNullOrWhiteSpace(propPath)) return;

        if (!double.TryParse(tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var currentVal) &&
            !double.TryParse(tb.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out currentVal))
        {
            return;
        }

        var delta = e.Delta > 0 ? 1 : -1;
        var isShift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var isCtrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        double step = DetermineStepSize(propPath, isShift, isCtrl);
        var newVal = currentVal + delta * step;

        if (propPath.EndsWith("Opacity", StringComparison.OrdinalIgnoreCase) ||
            propPath.EndsWith("AnchorX", StringComparison.OrdinalIgnoreCase) ||
            propPath.EndsWith("AnchorY", StringComparison.OrdinalIgnoreCase))
        {
            newVal = Math.Clamp(newVal, 0.0, 1.0);
        }
        else if (propPath.EndsWith("FontSize", StringComparison.OrdinalIgnoreCase) ||
                 propPath.EndsWith("Width", StringComparison.OrdinalIgnoreCase) ||
                 propPath.EndsWith("Height", StringComparison.OrdinalIgnoreCase))
        {
            newVal = Math.Max(1.0, newVal);
        }
        else if (propPath.EndsWith("FrameRate", StringComparison.OrdinalIgnoreCase))
        {
            newVal = Math.Clamp(newVal, 1.0, 240.0);
        }
        else if (propPath.EndsWith("DurationSeconds", StringComparison.OrdinalIgnoreCase))
        {
            newVal = Math.Clamp(newVal, 0.1, 3600.0);
        }
        else if (propPath.EndsWith("SequenceOverlayIntervalItems", StringComparison.OrdinalIgnoreCase) ||
                 propPath.EndsWith("IntervalItems", StringComparison.OrdinalIgnoreCase))
        {
            newVal = Math.Clamp(Math.Round(newVal), 1.0, 100.0);
        }

        tb.Text = FormatNumericValue(propPath, newVal);
        binding.UpdateSource();
        e.Handled = true;

        SyncLayersToProject();
        if (propPath.StartsWith("SelectedProject", StringComparison.OrdinalIgnoreCase))
        {
            UpdateCanvasAspect();
            Raise(nameof(SelectedProject));
            Raise(nameof(ProjectDuration));
            Raise(nameof(CanvasInfoText));
            Raise(nameof(PlayheadText));
            Raise(nameof(RemainingText));
            _main.SaveCgProjects();
            RequestInteractiveRender(includeTimeline: true);
        }
        else
        {
            RequestInteractiveRender(includeTimeline: propPath.Contains("Seconds") || propPath.Contains("Time"));
        }
    }

    private void NumericInspector_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Up && e.Key != Key.Down) return;
        if (e.OriginalSource is not TextBox tb || tb.IsReadOnly || tb.AcceptsReturn) return;
        var binding = tb.GetBindingExpression(TextBox.TextProperty);
        if (binding is null) return;
        var propPath = binding.ParentBinding?.Path?.Path ?? "";
        if (string.IsNullOrWhiteSpace(propPath)) return;

        if (!double.TryParse(tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var currentVal) &&
            !double.TryParse(tb.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out currentVal))
        {
            return;
        }

        var delta = e.Key == Key.Up ? 1 : -1;
        var isShift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var isCtrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        double step = DetermineStepSize(propPath, isShift, isCtrl);
        var newVal = currentVal + delta * step;

        if (propPath.EndsWith("Opacity", StringComparison.OrdinalIgnoreCase) ||
            propPath.EndsWith("AnchorX", StringComparison.OrdinalIgnoreCase) ||
            propPath.EndsWith("AnchorY", StringComparison.OrdinalIgnoreCase))
        {
            newVal = Math.Clamp(newVal, 0.0, 1.0);
        }
        else if (propPath.EndsWith("FontSize", StringComparison.OrdinalIgnoreCase) ||
                 propPath.EndsWith("Width", StringComparison.OrdinalIgnoreCase) ||
                 propPath.EndsWith("Height", StringComparison.OrdinalIgnoreCase))
        {
            newVal = Math.Max(1.0, newVal);
        }
        else if (propPath.EndsWith("FrameRate", StringComparison.OrdinalIgnoreCase))
        {
            newVal = Math.Clamp(newVal, 1.0, 240.0);
        }
        else if (propPath.EndsWith("DurationSeconds", StringComparison.OrdinalIgnoreCase))
        {
            newVal = Math.Clamp(newVal, 0.1, 3600.0);
        }
        else if (propPath.EndsWith("SequenceOverlayIntervalItems", StringComparison.OrdinalIgnoreCase) ||
                 propPath.EndsWith("IntervalItems", StringComparison.OrdinalIgnoreCase))
        {
            newVal = Math.Clamp(Math.Round(newVal), 1.0, 100.0);
        }

        tb.Text = FormatNumericValue(propPath, newVal);
        binding.UpdateSource();
        e.Handled = true;

        SyncLayersToProject();
        if (propPath.StartsWith("SelectedProject", StringComparison.OrdinalIgnoreCase))
        {
            UpdateCanvasAspect();
            Raise(nameof(SelectedProject));
            Raise(nameof(ProjectDuration));
            Raise(nameof(CanvasInfoText));
            Raise(nameof(PlayheadText));
            Raise(nameof(RemainingText));
            _main.SaveCgProjects();
            RequestInteractiveRender(includeTimeline: true);
        }
        else
        {
            RequestInteractiveRender(includeTimeline: propPath.Contains("Seconds") || propPath.Contains("Time"));
        }
    }

    private static double DetermineStepSize(string propPath, bool isShift, bool isCtrl)
    {
        if (propPath.EndsWith("SequenceOverlayIntervalItems", StringComparison.OrdinalIgnoreCase) ||
            propPath.EndsWith("IntervalItems", StringComparison.OrdinalIgnoreCase))
        {
            return 1.0;
        }
        if (propPath.Contains("FrameRate", StringComparison.OrdinalIgnoreCase) ||
            propPath.Contains("Fps", StringComparison.OrdinalIgnoreCase))
        {
            return isCtrl ? 0.1 : (isShift ? 5.0 : 1.0);
        }
        if (propPath.Contains("Width", StringComparison.OrdinalIgnoreCase) ||
            propPath.Contains("Height", StringComparison.OrdinalIgnoreCase))
        {
            return isCtrl ? 1.0 : (isShift ? 100.0 : 10.0);
        }
        if (propPath.EndsWith("Opacity", StringComparison.OrdinalIgnoreCase) ||
            propPath.EndsWith("AnchorX", StringComparison.OrdinalIgnoreCase) ||
            propPath.EndsWith("AnchorY", StringComparison.OrdinalIgnoreCase) ||
            propPath.EndsWith("AnchorZ", StringComparison.OrdinalIgnoreCase))
        {
            return isCtrl ? 0.01 : (isShift ? 0.1 : 0.05);
        }
        if (propPath.Contains("Scale", StringComparison.OrdinalIgnoreCase))
        {
            return isCtrl ? 0.01 : (isShift ? 0.2 : 0.05);
        }
        if (propPath.Contains("Rotation", StringComparison.OrdinalIgnoreCase) ||
            propPath.Contains("Angle", StringComparison.OrdinalIgnoreCase) ||
            propPath.Contains("Skew", StringComparison.OrdinalIgnoreCase))
        {
            return isCtrl ? 0.2 : (isShift ? 15.0 : 1.0);
        }
        if (propPath.Contains("Seconds", StringComparison.OrdinalIgnoreCase) ||
            propPath.Contains("Time", StringComparison.OrdinalIgnoreCase) ||
            propPath.Contains("Delay", StringComparison.OrdinalIgnoreCase) ||
            propPath.Contains("Stagger", StringComparison.OrdinalIgnoreCase))
        {
            return isCtrl ? 0.01 : (isShift ? 1.0 : 0.1);
        }
        if (propPath.EndsWith("FontSize", StringComparison.OrdinalIgnoreCase))
        {
            return isCtrl ? 0.5 : (isShift ? 10.0 : 1.0);
        }
        if (propPath.Contains("Blur", StringComparison.OrdinalIgnoreCase) ||
            (propPath.Contains("Width", StringComparison.OrdinalIgnoreCase) && propPath.Contains("Border")) ||
            (propPath.Contains("Width", StringComparison.OrdinalIgnoreCase) && propPath.Contains("Outline")))
        {
            return isCtrl ? 0.1 : (isShift ? 2.0 : 0.5);
        }
        return isCtrl ? 0.2 : (isShift ? 10.0 : 1.0);
    }

    private static string FormatNumericValue(string propPath, double value)
    {
        if (propPath.EndsWith("Opacity", StringComparison.OrdinalIgnoreCase) ||
            propPath.Contains("Anchor", StringComparison.OrdinalIgnoreCase) ||
            propPath.Contains("Scale", StringComparison.OrdinalIgnoreCase) ||
            propPath.Contains("Seconds", StringComparison.OrdinalIgnoreCase) ||
            propPath.Contains("Stagger", StringComparison.OrdinalIgnoreCase))
        {
            return value.ToString("0.0##", CultureInfo.InvariantCulture);
        }
        return Math.Abs(value - Math.Round(value)) < 0.001 ?
            ((int)Math.Round(value)).ToString(CultureInfo.InvariantCulture) :
            value.ToString("0.#", CultureInfo.InvariantCulture);
    }

    private void DataSourceSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        Inspector_SelectionChanged(sender, e);
        Raise(nameof(BoundDataSourceUrl));
        Raise(nameof(BoundDataSourceStatus));
    }

    private void InspectorJsonUrl_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb)
        {
            BoundDataSourceUrl = tb.Text?.Trim() ?? string.Empty;
        }
    }

    private void QuickCreateJsonUrl_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProject is null || SelectedLayer is null) return;
        var url = (BoundDataSourceUrl ?? string.Empty).Trim();
        var field = string.IsNullOrWhiteSpace(SelectedLayer.DataField) ? "brk_text" : SelectedLayer.DataField.Trim();
        if (string.IsNullOrWhiteSpace(SelectedLayer.DataField))
            SelectedLayer.DataField = field;

        var ds = new CgDataSource
        {
            Id = Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(field) ? "Live JSON Feed" : $"JSON: {field}",
            SourceType = "UrlJson",
            Source = url,
            RefreshSeconds = 15
        };
        SelectedProject.DataSources.Add(ds);
        if (!DataSources.Contains(ds)) DataSources.Add(ds);
        SelectedLayer.DataSourceId = ds.Id;
        SyncLayersToProject(); _main.SaveCgProjects();
        Raise(nameof(BoundDataSourceUrl));
        Raise(nameof(BoundDataSourceStatus));
        Raise(nameof(SelectedLayer));
        RenderAll();
    }

    private async void FetchAndTestFeed_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProject is null || SelectedLayer is null) return;
        var url = (BoundDataSourceUrl ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            MessageBox.Show("Please enter a JSON Feed URL first.", "JSON Feed", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var ds = SelectedProject.DataSources.FirstOrDefault(x => x.Id == SelectedLayer.DataSourceId)
                 ?? DataSources.FirstOrDefault(x => x.Id == SelectedLayer.DataSourceId);
        if (ds is null)
        {
            var field = string.IsNullOrWhiteSpace(SelectedLayer.DataField) ? "brk_text" : SelectedLayer.DataField.Trim();
            if (string.IsNullOrWhiteSpace(SelectedLayer.DataField))
                SelectedLayer.DataField = field;
            ds = new CgDataSource
            {
                Id = Guid.NewGuid(),
                Name = $"JSON: {field}",
                SourceType = "UrlJson",
                Source = url,
                RefreshSeconds = 15
            };
            SelectedProject.DataSources.Add(ds);
            if (!DataSources.Contains(ds)) DataSources.Add(ds);
            SelectedLayer.DataSourceId = ds.Id;
        }
        else
        {
            ds.Source = url;
            ds.SourceType = "UrlJson";
        }

        try
        {
            var rows = await CgDataRuntime.Shared.RefreshAsync(ds);
            Raise(nameof(BoundDataSourceStatus));
            SyncLayersToProject(); _main.SaveCgProjects();
            RenderAll();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to fetch JSON feed:\n{ex.Message}", "JSON Feed Test", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ClearDataBinding_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedLayer is null) return;
        SelectedLayer.DataSourceId = Guid.Empty;
        Raise(nameof(BoundDataSourceUrl));
        Raise(nameof(BoundDataSourceStatus));
        Raise(nameof(SelectedLayer));
        SyncLayersToProject(); _main.SaveCgProjects();
        RenderAll();
    }

    private void ProjectDuration_LostFocus(object sender, RoutedEventArgs e)
    {
        if (SelectedProject is null) return;
        RecordUndoSnapshot();
        SelectedProject.DurationSeconds = Math.Clamp(SelectedProject.DurationSeconds, .1, 3600);
        foreach (var layer in Layers)
        {
            layer.StartSeconds = Math.Clamp(layer.StartSeconds, 0, Math.Max(0, SelectedProject.DurationSeconds - .1));
            layer.EndSeconds = Math.Clamp(layer.EndSeconds, layer.StartSeconds + .1, SelectedProject.DurationSeconds);
            foreach (var key in layer.Keyframes ?? []) key.TimeSeconds = Math.Clamp(key.TimeSeconds, 0, SelectedProject.DurationSeconds);
        }
        _playhead = Math.Clamp(_playhead, 0, SelectedProject.DurationSeconds);
        Raise(nameof(ProjectDuration)); Raise(nameof(Playhead)); Raise(nameof(PlayheadText)); Raise(nameof(RemainingText));
        SyncLayersToProject(); _main.SaveCgProjects(); FitTimelineToViewport(); RenderAll();
    }

    private void ProjectInspector_LostFocus(object sender, RoutedEventArgs e)
    {
        if (SelectedProject is null) return;
        RecordUndoSnapshot();
        SelectedProject.Width = Math.Clamp(SelectedProject.Width, 64, 7680);
        SelectedProject.Height = Math.Clamp(SelectedProject.Height, 64, 4320);
        SelectedProject.FrameRate = Math.Clamp(SelectedProject.FrameRate, 1.0, 240.0);
        SelectedProject.DurationSeconds = Math.Clamp(SelectedProject.DurationSeconds, 0.1, 3600.0);
        UpdateCanvasAspect();
        Raise(nameof(SelectedProject));
        Raise(nameof(ProjectDuration));
        Raise(nameof(CanvasInfoText));
        Raise(nameof(PlayheadText));
        Raise(nameof(RemainingText));
        SyncLayersToProject();
        _main.SaveCgProjects();
        FitTimelineToViewport();
        RenderAll();
    }

    private void SetProjectResolution_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProject is null || sender is not FrameworkElement fe || fe.Tag is not string tag) return;
        var parts = tag.Split('x');
        if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h))
        {
            SelectedProject.Width = w;
            SelectedProject.Height = h;
            ProjectInspector_LostFocus(sender, e);
        }
    }

    private void SetProjectFps_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProject is null || sender is not FrameworkElement fe || fe.Tag is not string tag) return;
        if (double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps))
        {
            SelectedProject.FrameRate = fps;
            ProjectInspector_LostFocus(sender, e);
        }
    }

    private void SetProjectDuration_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProject is null || sender is not FrameworkElement fe || fe.Tag is not string tag) return;
        if (double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var dur))
        {
            SelectedProject.DurationSeconds = dur;
            ProjectDuration_LostFocus(sender, e);
        }
    }

    private void ApplyCompositionSettings_Click(object sender, RoutedEventArgs e)
    {
        ProjectInspector_LostFocus(sender, e);
    }

    private void MaskFromElement_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedLayer is null || SelectedProject is null) return;
        if (AutoKey && _autoKeyEditSnapshot is null) CaptureAutoKeyEditSnapshot();
        var motion = CgAnimationEngine.EvaluateLayer(SelectedProject, SelectedLayer, Playhead);
        SelectedLayer.MaskEnabled = true;
        SelectedLayer.MaskShape = "Rectangle";
        SelectedLayer.MaskX = motion.X;
        SelectedLayer.MaskY = motion.Y;
        SelectedLayer.MaskWidth = Math.Max(1, motion.Width);
        SelectedLayer.MaskHeight = Math.Max(1, motion.Height);
        SelectedLayer.MaskCornerRadius = 0;
        CommitInspectorAutoKey(); SyncLayersToProject(); _main.SaveCgProjects(); Raise(nameof(SelectedLayer)); RenderAll();
    }

    private void MaskToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || SelectedLayer is null) return;
        if (MaskEnabledCheck.IsChecked == true)
        {
            SelectedLayer.MaskEnabled = true;
            if (SelectedLayer.MaskWidth <= .001 || SelectedLayer.MaskHeight <= .001)
            {
                SelectedLayer.MaskX = SelectedLayer.X; SelectedLayer.MaskY = SelectedLayer.Y;
                SelectedLayer.MaskWidth = Math.Max(4, SelectedLayer.Width); SelectedLayer.MaskHeight = Math.Max(4, SelectedLayer.Height);
            }
            _drawMaskLayer = SelectedLayer; _drawingMask = false; PreviewCanvas.Cursor = Cursors.Cross;
        }
        else
        {
            SelectedLayer.MaskEnabled = false; _drawMaskLayer = null; _drawingMask = false; PreviewCanvas.Cursor = Cursors.Arrow;
        }
        CommitInspectorAutoKey(); SyncLayersToProject(); _main.SaveCgProjects(); RenderAll();
    }

    private void DrawMask_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedLayer is null) return;
        _drawMaskLayer = SelectedLayer;
        _drawingMask = false;
        PreviewCanvas.Cursor = Cursors.Cross;
        RenderPreview();
    }

    private void ClearMask_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedLayer is null) return;
        if (AutoKey && _autoKeyEditSnapshot is null) CaptureAutoKeyEditSnapshot();
        SelectedLayer.MaskEnabled = false;
        _drawMaskLayer = null; _drawingMask = false;
        PreviewCanvas.Cursor = Cursors.Arrow;
        CommitInspectorAutoKey(); SyncLayersToProject(); _main.SaveCgProjects(); Raise(nameof(SelectedLayer)); RenderAll();
    }



    private void Inspector_LostFocus(object sender, RoutedEventArgs e)
    {
        RecordUndoSnapshot();
        CommitInspectorAutoKey();
        SyncLayersToProject();
        _main.SaveCgProjects();
        RenderAll();
    }

    private void MaskProperty_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || SelectedLayer is null) return;
        RenderPreview();
    }

    private void Inspector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        RecordUndoSnapshot();
        CommitInspectorAutoKey();
        SyncLayersToProject();
        _main.SaveCgProjects();
        RenderAll();
    }

    private void Inspector_CheckChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || SelectedLayer is null) return;
        RecordUndoSnapshot();
        CommitInspectorAutoKey();
        SyncLayersToProject();
        _main.SaveCgProjects();
        RenderAll();
    }
    private double TimelineTrackWidth()
    {
        var duration = double.IsFinite(ProjectDuration) ? Math.Max(.5, ProjectDuration) : 10.0;
        var zoom = double.IsFinite(TimelineZoom) ? Math.Clamp(TimelineZoom, .10, 12.0) : 1.0;
        return Math.Max(60.0, duration * TimelineBasePixelsPerSecond * zoom);
    }
    private double TimelineContentWidth()
    {
        return TimelineTrackWidth();
    }
    private double TimelineTimeToX(double seconds) => Math.Clamp(seconds, 0, ProjectDuration) / Math.Max(.0001, ProjectDuration) * TimelineTrackWidth();
    private double TimelineMajorTickSeconds()
    {
        var pxPerSecond = TimelineTrackWidth() / Math.Max(.5, ProjectDuration);
        foreach (var candidate in new[] { .1, .25, .5, 1.0, 2.0, 5.0, 10.0, 15.0, 30.0, 60.0 })
            if (candidate * pxPerSecond >= 72.0) return candidate;
        return 120.0;
    }
    private void TimelineZoomIn_Click(object sender, RoutedEventArgs e)
    {
        _isTimelineFit = false;
        SetTimelineZoom(TimelineZoom * 1.25);
    }
    private void TimelineZoomOut_Click(object sender, RoutedEventArgs e)
    {
        _isTimelineFit = false;
        SetTimelineZoom(TimelineZoom / 1.25);
    }
    private void TimelineZoomFit_Click(object sender, RoutedEventArgs e) => FitTimelineToViewport();
    private void TimelineViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsLoaded && (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 2 || Math.Abs(e.NewSize.Height - e.PreviousSize.Height) > 2))
        {
            if (_isTimelineFit)
            {
                FitTimelineToViewport();
            }
            else
            {
                ApplyTimelineZoom();
            }
        }
    }
    private void SetTimelineZoom(double zoom)
    {
        if (!double.IsFinite(zoom)) zoom = 1.0;
        TimelineZoom = Math.Clamp(zoom, .10, 12.0);
        ApplyTimelineZoom();
        // Timeline scale is left-origin based: zooming changes only the right edge.
        // Retaining an old horizontal offset made zoom-out look like both ends were
        // being squeezed, especially after the operator had scrolled to the right.
        TimelineLayerScroll?.ScrollToHorizontalOffset(0);
        TimelineRulerScroll?.ScrollToHorizontalOffset(0);
    }
    private void FitTimelineToViewport()
    {
        if (TimelineLayerScroll is null) return;
        var layerWidth = TimelineLayerScroll.ActualWidth;
        var rulerWidth = TimelineRulerScroll?.ActualWidth ?? layerWidth;
        var viewport = Math.Min(layerWidth, rulerWidth);
        if (viewport <= 10) viewport = TimelineLayerScroll.ViewportWidth;
        if (viewport <= 10)
        {
            Dispatcher.BeginInvoke(new Action(FitTimelineToViewport), DispatcherPriority.Render);
            return;
        }
        var duration = Math.Max(.5, ProjectDuration);
        _isTimelineFit = true;
        TimelineZoom = Math.Clamp(viewport / (duration * TimelineBasePixelsPerSecond), .10, 12.0);
        ApplyTimelineZoom();
        TimelineLayerScroll.ScrollToHorizontalOffset(0);
        TimelineRulerScroll?.ScrollToHorizontalOffset(0);
    }
    private void TimelineLayerScroll_ScrollChanged(object sender,ScrollChangedEventArgs e)
    {
        if(Math.Abs(e.HorizontalChange)>.001) TimelineRulerScroll?.ScrollToHorizontalOffset(e.HorizontalOffset);
        if(Math.Abs(e.VerticalChange)>.001) TimelineHeadersScroll?.ScrollToVerticalOffset(e.VerticalOffset);
    }
    private void TimelineHeadersScroll_ScrollChanged(object sender,ScrollChangedEventArgs e)
    {
        if(Math.Abs(e.VerticalChange)>.001) TimelineLayerScroll?.ScrollToVerticalOffset(e.VerticalOffset);
    }
    private void ApplyTimelineZoom()
    {
        if(!IsLoaded||TimelineCanvas is null||TimelineRulerCanvas is null||TimelineLayerScroll is null)return;
        var content = TimelineContentWidth();
        var height = Math.Max(146,TimelineLayerScroll.ViewportHeight>20?TimelineLayerScroll.ViewportHeight:146);
        TimelineCanvas.Height=height;
        TimelineCanvas.Width=content;
        TimelineRulerCanvas.Width=content;
        if (TimelineHeadersCanvas is not null) TimelineHeadersCanvas.Height = height;
        RenderTimeline();
    }

    private void TimelineCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RenderTimeline();

    private void SyncLayersToProject()
    {
        if (_selectedProject is null) return;
        if (Layers.Count == 0 && _selectedProject.Layers.Count > 0) return;
        _selectedProject.Layers = Layers.ToList();
        _selectedProject.HtmlSources = HtmlSources.ToList();
        _selectedProject.DataSources = DataSources.ToList();
        _selectedProject.Groups = Groups.ToList();
    }

    private void RequestInteractiveRender(bool includeTimeline = false)
    {
        _interactivePreviewRenderPending = true;
        _interactiveTimelineRenderPending |= includeTimeline;
        if (_interactiveRenderHooked) return;
        _interactiveRenderHooked = true;
        CompositionTarget.Rendering += InteractiveComposition_Rendering;
    }

    private void InteractiveComposition_Rendering(object? sender, EventArgs e)
    {
        CompositionTarget.Rendering -= InteractiveComposition_Rendering;
        _interactiveRenderHooked = false;
        var preview = _interactivePreviewRenderPending;
        var timeline = _interactiveTimelineRenderPending;
        _interactivePreviewRenderPending = false;
        _interactiveTimelineRenderPending = false;
        if (preview) RenderPreview();
        if (timeline) RenderTimeline();
    }

    private void CancelInteractiveRender()
    {
        if (_interactiveRenderHooked)
        {
            CompositionTarget.Rendering -= InteractiveComposition_Rendering;
            _interactiveRenderHooked = false;
        }
        _interactivePreviewRenderPending = false;
        _interactiveTimelineRenderPending = false;
    }

    private void RenderAll() { CancelInteractiveRender(); RenderPreview(); RenderTimeline(); Raise(nameof(ProjectDuration)); }

    private void RenderPreview()
    {
        if (!IsLoaded || PreviewCanvas is null) return;
        PreviewCanvas.Children.Clear();
        _previewLayerVisuals.Clear();
        _previewSelectionHandles.Clear();
        _previewSelectionOutline = null;
        _previewSelectionLabel = null;
        var project = SelectedProject; if (project is null) return;
        var sx = (PreviewCanvas.Width > 1 ? PreviewCanvas.Width : 960.0) / Math.Max(1.0, project.Width);
        var sy = (PreviewCanvas.Height > 1 ? PreviewCanvas.Height : 540.0) / Math.Max(1.0, project.Height);
        DrawCanvasGrid();
        var hasAnyTicker = Layers.Any(l => string.Equals(l.Type, "Ticker", StringComparison.OrdinalIgnoreCase));
        var tickerGroupId = Layers.FirstOrDefault(l => string.Equals(l.Type, "Ticker", StringComparison.OrdinalIgnoreCase))?.GroupId ?? Guid.Empty;

        bool IsTickerStripLayer(CgLayer l)
        {
            if (string.Equals(l.Type, "Ticker", StringComparison.OrdinalIgnoreCase)) return true;
            if (tickerGroupId != Guid.Empty && l.GroupId == tickerGroupId) return true;
            if (l.DataSourceId != Guid.Empty) return true;
            if (l.SequenceLoop && string.Equals(l.Type, "ImageSequence", StringComparison.OrdinalIgnoreCase)) return true;
            var n = l.Name ?? "";
            return n.Contains("Ticker", StringComparison.OrdinalIgnoreCase) ||
                   n.Contains("Badge", StringComparison.OrdinalIgnoreCase) ||
                   n.Contains("Strip", StringComparison.OrdinalIgnoreCase) ||
                   n.Contains("Plate", StringComparison.OrdinalIgnoreCase) ||
                   n.Contains("Accent", StringComparison.OrdinalIgnoreCase) ||
                   n.Contains("Divider", StringComparison.OrdinalIgnoreCase);
        }

        foreach (var layer in Layers.Where(x => x.Visible && (
            (hasAnyTicker && IsTickerStripLayer(x))
                ? (ContinuousPlaybackSeconds >= x.StartSeconds)
                : (x.SequenceAdvanceDataItem || string.Equals(x.Type, "Ticker", StringComparison.OrdinalIgnoreCase))
                    ? (ContinuousPlaybackSeconds >= x.StartSeconds)
                    : (Playhead >= x.StartSeconds && Playhead <= x.EndSeconds)
        )))
        {
            var previewMotionTime = CgDataSourceService.ResolveCategoryLayerTimelineSeconds(project, layer, ContinuousPlaybackSeconds, Playhead);
            var motion=CgAnimationEngine.EvaluateLayer(project,layer,previewMotionTime);
            if(motion.Opacity<=.0001)continue;
            var visualLayer=CgAnimationEngine.CreateVisualLayer(layer,previewMotionTime);
            var isOverlayCycle = CgDataSourceService.IsOverlayActiveForItem(project, layer, ContinuousPlaybackSeconds);
            if (isOverlayCycle && visualLayer.TextAnimationDelaySeconds <= 0.001 && visualLayer.StartSeconds <= 0.001)
            {
                visualLayer.TextAnimationDelaySeconds = CgDataSourceService.ResolveEffectiveOverlayDuration(project, layer);
            }
            var textEvalTime = visualLayer.DataSourceId != Guid.Empty ? ContinuousPlaybackSeconds : Playhead;
            var resolvedText=CgAnimationEngine.ResolveAnimatedText(visualLayer,CgDataSourceService.ResolveLayerText(project,visualLayer,ContinuousPlaybackSeconds),textEvalTime);
            var el = BuildElement(project, visualLayer, sx, sy, resolvedText); if (el is null) continue;
            // EvaluateLayer already bakes keyframed scale, Z perspective and X/Y rotation
            // foreshortening into X/Y/Width/Height. Use that geometry exactly as the software
            // compositor does so editor, controller and program remain pixel-proportional.
            var baseW = Math.Max(2, motion.Width * sx); var baseH = Math.Max(2, motion.Height * sy);
            var x = motion.X * sx; var y = motion.Y * sy; var w = baseW; var h = baseH;
            if (string.Equals(visualLayer.Type, "Ticker", StringComparison.OrdinalIgnoreCase))
            {
                w = baseW;
            }
            el.Opacity = Math.Clamp(el.Opacity * motion.Opacity, 0, 1);
            ApplyPreviewAnimation(visualLayer, ref x, ref y, ref w, ref h, el);
            var previewType = (visualLayer.Type ?? "Text").ToUpperInvariant();
            var isTickerOrBadge = previewType is "TICKER" ||
                (visualLayer.DataField != null && visualLayer.DataField.Contains("category", StringComparison.OrdinalIgnoreCase)) ||
                (visualLayer.Name != null && visualLayer.Name.Contains("Badge", StringComparison.OrdinalIgnoreCase));
            if (!isTickerOrBadge && previewType is "TEXT" or "ROLL" or "DIGITALCLOCK" or "DATETIME" or "TIMECODE" or "NEPALIDATE" or "NEPALICLOCK")
            {
                var textVisual = CgAnimationEngine.EvaluateTextVisual(visualLayer, textEvalTime);
                el.Opacity = Math.Clamp(el.Opacity * textVisual.Opacity, 0, 1);
                x += textVisual.OffsetX * sx; y += textVisual.OffsetY * sy;
                if (Math.Abs(textVisual.Scale - 1) > .0001)
                {
                    var align = (visualLayer.HorizontalTextAlignment ?? "Left").Trim().ToLowerInvariant();
                    var origin = align switch
                    {
                        "center" => new Point(0.5, 0.5),
                        "right" => new Point(1.0, 0.5),
                        _ => new Point(0.0, 0.5)
                    };
                    if (el is Border scaleBorder && scaleBorder.Child is TextBlock scaleTb)
                    {
                        scaleTb.RenderTransformOrigin = origin;
                        scaleTb.RenderTransform = new ScaleTransform(textVisual.Scale, textVisual.Scale);
                    }
                    else if (el is TextBlock directTb)
                    {
                        directTb.RenderTransformOrigin = origin;
                        directTb.RenderTransform = new ScaleTransform(textVisual.Scale, textVisual.Scale);
                    }
                }
                var effectiveBlur = Math.Max(textVisual.BlurRadius, visualLayer.BlurRadius);
                if (effectiveBlur > 0 && el is Border b && b.Child is TextBlock tb)
                {
                    tb.Effect = new System.Windows.Media.Effects.BlurEffect { Radius = effectiveBlur * sy, KernelType = System.Windows.Media.Effects.KernelType.Gaussian };
                }
            }
            var anchorPxX = w * Math.Clamp(motion.AnchorX, 0, 1);
            var anchorPxY = h * Math.Clamp(motion.AnchorY, 0, 1);
            var group = new TransformGroup();
            var skewX = Math.Clamp(motion.SkewX + Math.Tan(motion.RotationY * Math.PI / 180.0) * 35.0, -75, 75);
            var skewY = Math.Clamp(motion.SkewY - Math.Tan(motion.RotationX * Math.PI / 180.0) * 35.0, -75, 75);
            if (Math.Abs(skewX) > .001 || Math.Abs(skewY) > .001)
                group.Children.Add(new SkewTransform(skewX, skewY, anchorPxX, anchorPxY));
            if (Math.Abs(motion.Rotation) > .001)
                group.Children.Add(new RotateTransform(motion.Rotation, anchorPxX, anchorPxY));
            if (group.Children.Count > 0) el.RenderTransform = group;
            else el.RenderTransform = null;
            el.Width = w;
            el.Height = h; el.Tag = new PreviewHit { Layer = layer, Mode = "move" };
            AddPreviewLayerWithMask(el, visualLayer, x, y, sx, sy);
            _previewLayerVisuals[layer] = el;
        }
        foreach (var html in HtmlSources.Where(x => x.Visible))
        {
            var el = BuildHtmlPreview(html);
            el.Width=Math.Max(2,html.Width*sx); el.Height=Math.Max(2,html.Height*sy); el.Opacity=html.Opacity;
            Canvas.SetLeft(el,html.X*sx); Canvas.SetTop(el,html.Y*sy); PreviewCanvas.Children.Add(el);
        }
        DrawSafeAreaGuides();
        DrawPreviewSelection(project, sx, sy);
        DrawPenGuide(sx, sy);
    }

    private void DrawPenGuide(double sx, double sy)
    {
        if (!_penDrawing) return;
        if (_penPoints.Count > 0)
        {
            var line = new Polyline
            {
                Points = new PointCollection(_penPoints.Select(p => new Point(p.X * sx, p.Y * sy))),
                Stroke = _penCreatesMask ? Brushes.DeepSkyBlue : Brushes.Gold,
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 5, 3 },
                IsHitTestVisible = false
            };
            Panel.SetZIndex(line, 2000); PreviewCanvas.Children.Add(line);
        }
        foreach (var point in _penPoints)
        {
            var node = new Ellipse { Width = 9, Height = 9, Fill = Brushes.White, Stroke = _penCreatesMask ? Brushes.DeepSkyBlue : Brushes.Gold, StrokeThickness = 2, IsHitTestVisible = false };
            Canvas.SetLeft(node, point.X * sx - 4.5); Canvas.SetTop(node, point.Y * sy - 4.5);
            Panel.SetZIndex(node, 2001); PreviewCanvas.Children.Add(node);
        }
    }

    private static readonly Dictionary<string, Brush> _maskBrushCache = new();

    private void AddPreviewLayerWithMask(FrameworkElement element, CgLayer layer, double x, double y, double sx, double sy)
    {
        if (!layer.MaskEnabled)
        {
            Canvas.SetLeft(element, x); Canvas.SetTop(element, y); PreviewCanvas.Children.Add(element); return;
        }
        var canvasW = PreviewCanvas.Width > 0 ? PreviewCanvas.Width : 960.0;
        var canvasH = PreviewCanvas.Height > 0 ? PreviewCanvas.Height : 540.0;
        var host = new Canvas { Width = canvasW, Height = canvasH, ClipToBounds = true, Background = Brushes.Transparent };
        Canvas.SetLeft(host, 0);
        Canvas.SetTop(host, 0);

        if (layer.MaskFeather <= 0.001)
        {
            host.Clip = BuildPreviewMaskGeometry(layer, sx, sy, host.Width, host.Height);
            host.OpacityMask = null;
        }
        else
        {
            host.Clip = null;
            host.OpacityMask = BuildPreviewMaskOpacityMask(layer, sx, sy, (int)Math.Ceiling(canvasW), (int)Math.Ceiling(canvasH));
        }

        Canvas.SetLeft(element, x); Canvas.SetTop(element, y); host.Children.Add(element);
        PreviewCanvas.Children.Add(host);
    }

    private static Brush BuildPreviewMaskOpacityMask(CgLayer layer, double sx, double sy, int canvasWidth, int canvasHeight)
    {
        canvasWidth = Math.Clamp(canvasWidth, 10, 1920);
        canvasHeight = Math.Clamp(canvasHeight, 10, 1080);
        var mwProject = layer.MaskWidth > .0001 ? layer.MaskWidth : Math.Max(1, layer.Width);
        var mhProject = layer.MaskHeight > .0001 ? layer.MaskHeight : Math.Max(1, layer.Height);
        var mxProject = layer.MaskWidth > .0001 ? layer.MaskX : layer.X;
        var myProject = layer.MaskHeight > .0001 ? layer.MaskY : layer.Y;
        var mx = mxProject * sx;
        var my = myProject * sy;
        var mw = Math.Max(1.0, mwProject * sx);
        var mh = Math.Max(1.0, mhProject * sy);
        var feather = Math.Max(0.5, layer.MaskFeather * Math.Min(sx, sy));
        var radius = Math.Clamp(layer.MaskCornerRadius * Math.Min(sx, sy), 0.0, Math.Min(mw, mh) / 2.0);
        var shape = (layer.MaskShape ?? "Rectangle").Trim().ToUpperInvariant();
        var invert = layer.MaskInvert;
        var customMask = shape == "CUSTOM PATH"
            ? CgPathData.Parse(layer.MaskPathData)
                .Select(p => new CgPathData.Point(mx + p.X * mw, my + p.Y * mh)).ToArray()
            : [];

        var cacheKey = $"{shape}_{layer.MaskPathData}_{mx:0.#}_{my:0.#}_{mw:0.#}_{mh:0.#}_{feather:0.#}_{radius:0.#}_{invert}_{canvasWidth}_{canvasHeight}";
        if (_maskBrushCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var stride = canvasWidth * 4;
        var bytes = new byte[stride * canvasHeight];
        for (var y = 0; y < canvasHeight; y++)
        {
            var rowOffset = y * stride;
            for (var x = 0; x < canvasWidth; x++)
            {
                var px = x + 0.5;
                var py = y + 0.5;
                double coverage;
                if (customMask.Length >= 3)
                {
                    var inside = CgPathData.Contains(customMask, px, py);
                    var edge = CgPathData.DistanceToEdges(customMask, px, py);
                    var signedDistance = inside ? -edge : edge;
                    var t = Math.Clamp(0.5 - signedDistance / feather, 0.0, 1.0);
                    coverage = t * t * (3.0 - 2.0 * t);
                }
                else if (shape == "ELLIPSE")
                {
                    var rx = mw / 2.0; var ry = mh / 2.0;
                    var nx = (px - (mx + rx)) / Math.Max(.0001, rx);
                    var ny = (py - (my + ry)) / Math.Max(.0001, ry);
                    var radial = Math.Sqrt(nx * nx + ny * ny);
                    var distFromEdge = (1.0 - radial) * Math.Min(rx, ry);
                    var t = Math.Clamp(0.5 + distFromEdge / feather, 0.0, 1.0);
                    coverage = t * t * (3.0 - 2.0 * t);
                }
                else
                {
                    var cx = mx + mw / 2.0; var cy = my + mh / 2.0;
                    var halfW = mw / 2.0; var halfH = mh / 2.0;
                    var r = shape == "ROUNDED RECTANGLE" ? radius : 0.0;
                    var qx = Math.Abs(px - cx) - Math.Max(0.0, halfW - r);
                    var qy = Math.Abs(py - cy) - Math.Max(0.0, halfH - r);
                    var ox = Math.Max(qx, 0.0); var oy = Math.Max(qy, 0.0);
                    var signedDistance = Math.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(qx, qy), 0.0) - r;
                    var t = Math.Clamp(0.5 - signedDistance / feather, 0.0, 1.0);
                    coverage = t * t * (3.0 - 2.0 * t);
                }
                if (invert) coverage = 1.0 - coverage;
                var alpha = (byte)Math.Clamp((int)Math.Round(coverage * 255.0), 0, 255);
                var idx = rowOffset + x * 4;
                bytes[idx] = (byte)(alpha * alpha / 255);     // B (premultiplied)
                bytes[idx + 1] = (byte)(alpha * alpha / 255); // G (premultiplied)
                bytes[idx + 2] = (byte)(alpha * alpha / 255); // R (premultiplied)
                bytes[idx + 3] = alpha;                       // A
            }
        }

        var bmp = BitmapSource.Create(canvasWidth, canvasHeight, 96, 96, PixelFormats.Pbgra32, null, bytes, stride);
        bmp.Freeze();
        var brush = new ImageBrush(bmp)
        {
            Stretch = Stretch.None,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
            Viewbox = new Rect(0, 0, canvasWidth, canvasHeight),
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, canvasWidth, canvasHeight),
            ViewportUnits = BrushMappingMode.Absolute
        };
        brush.Freeze();

        if (_maskBrushCache.Count > 30) _maskBrushCache.Clear();
        _maskBrushCache[cacheKey] = brush;
        return brush;
    }

    private static Geometry BuildPreviewMaskGeometry(CgLayer layer, double sx, double sy, double canvasWidth, double canvasHeight)
    {
        var mwProject = layer.MaskWidth > .0001 ? layer.MaskWidth : Math.Max(1, layer.Width);
        var mhProject = layer.MaskHeight > .0001 ? layer.MaskHeight : Math.Max(1, layer.Height);
        var mxProject = layer.MaskWidth > .0001 ? layer.MaskX : layer.X;
        var myProject = layer.MaskHeight > .0001 ? layer.MaskY : layer.Y;
        var rect = new Rect(mxProject * sx, myProject * sy, Math.Max(1, mwProject * sx), Math.Max(1, mhProject * sy));
        Geometry mask = (layer.MaskShape ?? "Rectangle").Trim().ToUpperInvariant() switch
        {
            "CUSTOM PATH" => BuildNormalizedPathGeometry(layer.MaskPathData, rect, true),
            "ELLIPSE" => new EllipseGeometry(rect),
            "ROUNDED RECTANGLE" => new RectangleGeometry(rect, Math.Max(0, layer.MaskCornerRadius * Math.Min(sx, sy)), Math.Max(0, layer.MaskCornerRadius * Math.Min(sx, sy))),
            _ => new RectangleGeometry(rect)
        };
        if (!layer.MaskInvert) return mask;
        return new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, canvasWidth, canvasHeight)), mask);
    }

    private static Geometry BuildNormalizedPathGeometry(string? data, Rect bounds, bool closed)
    {
        var points = CgPathData.Parse(data)
            .Select(p => new Point(bounds.Left + p.X * bounds.Width, bounds.Top + p.Y * bounds.Height))
            .ToArray();
        if (points.Length == 0) return new RectangleGeometry(bounds);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], isFilled: closed, isClosed: closed);
            if (points.Length > 1) context.PolyLineTo(points.Skip(1).ToArray(), isStroked: true, isSmoothJoin: true);
        }
        geometry.Freeze();
        return geometry;
    }

    private void DrawCanvasGrid()
    {
        if(!ShowCanvasGrid||PreviewCanvas is null)return;
        var width=PreviewCanvas.Width>0?PreviewCanvas.Width:960.0;var height=PreviewCanvas.Height>0?PreviewCanvas.Height:540.0;
        var minor=new SolidColorBrush(Color.FromArgb(42,150,156,164));
        var major=new SolidColorBrush(Color.FromArgb(72,185,191,199));
        const double step=25.0;
        for(var x=step;x<width;x+=step)
        {
            var line=new Line{X1=x,X2=x,Y1=0,Y2=height,Stroke=((int)(x/step)%4==0)?major:minor,StrokeThickness=((int)(x/step)%4==0)?1.0:.6,IsHitTestVisible=false};
            Panel.SetZIndex(line,-10);PreviewCanvas.Children.Add(line);
        }
        for(var y=step;y<height;y+=step)
        {
            var line=new Line{X1=0,X2=width,Y1=y,Y2=y,Stroke=((int)(y/step)%4==0)?major:minor,StrokeThickness=((int)(y/step)%4==0)?1.0:.6,IsHitTestVisible=false};
            Panel.SetZIndex(line,-10);PreviewCanvas.Children.Add(line);
        }
    }

    private void DrawSafeAreaGuides()
    {
        if (PreviewCanvas is null) return;
        var width = PreviewCanvas.Width > 0 ? PreviewCanvas.Width : 960.0;
        var height = PreviewCanvas.Height > 0 ? PreviewCanvas.Height : 540.0;

        if (ShowSafeAreas)
        {
            DrawSafeRectangle(width, height, _actionSafePercent, Color.FromArgb(64, Colors.LightGray.R, Colors.LightGray.G, Colors.LightGray.B), $"ACTION SAFE {_actionSafePercent:0.#}%");
            DrawSafeRectangle(width, height, _titleSafePercent, Color.FromArgb(64, Colors.White.R, Colors.White.G, Colors.White.B), $"TITLE SAFE {_titleSafePercent:0.#}%");
        }

        if (ShowCenterGuide)
        {
            var guide = new SolidColorBrush(Color.FromArgb(150, 184, 141, 255));
            var vertical = new Line { X1 = width / 2, X2 = width / 2, Y1 = 0, Y2 = height, Stroke = guide, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 5 }, IsHitTestVisible = false };
            var horizontal = new Line { X1 = 0, X2 = width, Y1 = height / 2, Y2 = height / 2, Stroke = guide, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 5 }, IsHitTestVisible = false };
            Panel.SetZIndex(vertical, 900); Panel.SetZIndex(horizontal, 900);
            PreviewCanvas.Children.Add(vertical); PreviewCanvas.Children.Add(horizontal);
        }
    }

    private void DrawSafeRectangle(double width, double height, double percent, Color color, string labelText)
    {
        var p = Math.Clamp(percent, 1.0, 35.0) / 100.0;
        var insetX = width * p;
        var insetY = height * p;
        var stroke = new SolidColorBrush(color);
        var rectangle = new Rectangle
        {
            Width = Math.Max(2, width - insetX * 2), Height = Math.Max(2, height - insetY * 2),
            Fill = Brushes.Transparent, Stroke = stroke, StrokeThickness = 1.2, StrokeDashArray = new DoubleCollection { 7, 4 }, IsHitTestVisible = false,
            ToolTip = "Editor guide only - never rendered to Program/Preview/NDI/DeckLink output"
        };
        Canvas.SetLeft(rectangle, insetX); Canvas.SetTop(rectangle, insetY); Panel.SetZIndex(rectangle, 900); PreviewCanvas.Children.Add(rectangle);

        var label = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(190, 4, 9, 14)), BorderBrush = stroke, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2),
            Padding = new Thickness(4, 1, 4, 1), IsHitTestVisible = false, Child = new TextBlock { Text = labelText, Foreground = stroke, FontSize = 8, FontWeight = FontWeights.SemiBold }
        };
        Canvas.SetLeft(label, insetX + 4); Canvas.SetTop(label, insetY + 3); Panel.SetZIndex(label, 901); PreviewCanvas.Children.Add(label);
    }

    private void DrawPreviewSelection(CgProject project, double sx, double sy)
    {
        {
            foreach (var selected in GetSelectedLayers().Where(x => !ReferenceEquals(x, SelectedLayer) && x.Visible && Playhead >= x.StartSeconds && Playhead <= x.EndSeconds))
            {
                var selectedMotion = CgAnimationEngine.EvaluateLayer(project, selected, Playhead);
                var selectedOutline = new Border
                {
                    Width = Math.Max(8, selectedMotion.Width * sx), Height = Math.Max(8, selectedMotion.Height * sy),
                    Background = Brushes.Transparent, BorderBrush = new SolidColorBrush(Color.FromArgb(190, 145, 94, 210)),
                    BorderThickness = new Thickness(1), IsHitTestVisible = false
                };
                Canvas.SetLeft(selectedOutline, selectedMotion.X * sx); Canvas.SetTop(selectedOutline, selectedMotion.Y * sy);
                Panel.SetZIndex(selectedOutline, 998); PreviewCanvas.Children.Add(selectedOutline);
            }
        }

        var layer = SelectedLayer;
        if (layer is null || !layer.Visible || Playhead < layer.StartSeconds || Playhead > layer.EndSeconds) return;
        var motion = CgAnimationEngine.EvaluateLayer(project, layer, Playhead);
        double baseW, baseH, x, y;
        TransformGroup group;

        if (_previewLayerVisuals.TryGetValue(layer, out var visual))
        {
            baseW = Math.Max(8, visual.Width);
            baseH = Math.Max(8, visual.Height);
            x = Canvas.GetLeft(visual);
            y = Canvas.GetTop(visual);
            group = visual.RenderTransform as TransformGroup ?? new TransformGroup();
        }
        else
        {
            baseW = Math.Max(8, motion.Width * sx);
            baseH = Math.Max(8, motion.Height * sy);
            x = motion.X * sx; y = motion.Y * sy;

            var anchorPxX = baseW * Math.Clamp(motion.AnchorX, 0, 1);
            var anchorPxY = baseH * Math.Clamp(motion.AnchorY, 0, 1);

            group = new TransformGroup();
            var skewX = Math.Clamp(motion.SkewX + Math.Tan(motion.RotationY * Math.PI / 180.0) * 35.0, -75, 75);
            var skewY = Math.Clamp(motion.SkewY - Math.Tan(motion.RotationX * Math.PI / 180.0) * 35.0, -75, 75);
            if (Math.Abs(skewX) > .001 || Math.Abs(skewY) > .001)
                group.Children.Add(new SkewTransform(skewX, skewY, anchorPxX, anchorPxY));
            if (Math.Abs(motion.Rotation) > .001)
                group.Children.Add(new RotateTransform(motion.Rotation, anchorPxX, anchorPxY));
        }

        var outline = new Border
        {
            Width = baseW, Height = baseH, Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.FromRgb(255, 74, 178)), BorderThickness = new Thickness(1.5),
            RenderTransform = group.Children.Count > 0 ? group : null,
            Tag = new PreviewHit { Layer = layer, Mode = "move" }, Cursor = Cursors.SizeAll,
            ToolTip = "Drag to move · corner/edge handles resize · Arrow keys nudge · Ctrl+Arrow resize"
        };
        Canvas.SetLeft(outline, x); Canvas.SetTop(outline, y); Panel.SetZIndex(outline, 1000); PreviewCanvas.Children.Add(outline);
        _previewSelectionOutline = outline;

        if (layer.MaskEnabled)
        {
            var mwProject = layer.MaskWidth > .0001 ? layer.MaskWidth : Math.Max(1, layer.Width);
            var mhProject = layer.MaskHeight > .0001 ? layer.MaskHeight : Math.Max(1, layer.Height);
            var mxProject = layer.MaskWidth > .0001 ? layer.MaskX : layer.X;
            var myProject = layer.MaskHeight > .0001 ? layer.MaskY : layer.Y;
            var maskRect = new Rect(mxProject * sx, myProject * sy, Math.Max(1, mwProject * sx), Math.Max(1, mhProject * sy));
            Geometry maskGeometry = (layer.MaskShape ?? "Rectangle").Trim().ToUpperInvariant() switch
            {
                "CUSTOM PATH" => BuildNormalizedPathGeometry(layer.MaskPathData, maskRect, true),
                "ELLIPSE" => new EllipseGeometry(maskRect),
                "ROUNDED RECTANGLE" => new RectangleGeometry(maskRect, Math.Max(0, layer.MaskCornerRadius * Math.Min(sx, sy)), Math.Max(0, layer.MaskCornerRadius * Math.Min(sx, sy))),
                _ => new RectangleGeometry(maskRect)
            };
            var maskGuide = new System.Windows.Shapes.Path
            {
                Data = maskGeometry, Fill = new SolidColorBrush(Color.FromArgb(24, 74, 225, 255)), Stroke = new SolidColorBrush(Color.FromRgb(74, 225, 255)), StrokeThickness = 1.6,
                StrokeDashArray = new DoubleCollection { 5, 3 },
                Tag = new PreviewHit { Layer = layer, Mode = "move_mask" },
                Cursor = Cursors.SizeAll,
                ToolTip = "Drag mask to reposition · Use corner/edge handles to resize mask"
            };
            Panel.SetZIndex(maskGuide, 995); PreviewCanvas.Children.Add(maskGuide);
            var maskLabel = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(220, 2, 24, 31)), BorderBrush = new SolidColorBrush(Color.FromRgb(74,225,255)), BorderThickness = new Thickness(1), Padding = new Thickness(4,1,4,1), IsHitTestVisible = false,
                Child = new TextBlock { Text = $"MASK · {layer.MaskShape}{(layer.MaskInvert ? " · INVERT" : "")} · {layer.MaskWidth:0}×{layer.MaskHeight:0} (DRAG / RESIZE)", Foreground = new SolidColorBrush(Color.FromRgb(126,236,255)), FontSize = 8 }
            };
            Canvas.SetLeft(maskLabel, Math.Max(0, maskRect.Left)); Canvas.SetTop(maskLabel, Math.Max(0, maskRect.Top - 18)); Panel.SetZIndex(maskLabel, 996); PreviewCanvas.Children.Add(maskLabel);

            var mx = maskRect.Left; var my = maskRect.Top; var mw = maskRect.Width; var mh = maskRect.Height;
            AddPreviewHandle(layer, "mask_nw", mx - 5, my - 5, Cursors.SizeNWSE, Brushes.DeepSkyBlue, Brushes.White);
            AddPreviewHandle(layer, "mask_ne", mx + mw - 5, my - 5, Cursors.SizeNESW, Brushes.DeepSkyBlue, Brushes.White);
            AddPreviewHandle(layer, "mask_sw", mx - 5, my + mh - 5, Cursors.SizeNESW, Brushes.DeepSkyBlue, Brushes.White);
            AddPreviewHandle(layer, "mask_se", mx + mw - 5, my + mh - 5, Cursors.SizeNWSE, Brushes.DeepSkyBlue, Brushes.White);
            AddPreviewHandle(layer, "mask_n", mx + mw / 2 - 5, my - 5, Cursors.SizeNS, Brushes.DeepSkyBlue, Brushes.White);
            AddPreviewHandle(layer, "mask_s", mx + mw / 2 - 5, my + mh - 5, Cursors.SizeNS, Brushes.DeepSkyBlue, Brushes.White);
            AddPreviewHandle(layer, "mask_w", mx - 5, my + mh / 2 - 5, Cursors.SizeWE, Brushes.DeepSkyBlue, Brushes.White);
            AddPreviewHandle(layer, "mask_e", mx + mw - 5, my + mh / 2 - 5, Cursors.SizeWE, Brushes.DeepSkyBlue, Brushes.White);
        }

        if (layer.SqueezeProgram && layer.SqueezeWidth > 0 && layer.SqueezeHeight > 0)
        {
            var sqW = Math.Max(16, layer.SqueezeWidth * sx);
            var sqH = Math.Max(16, layer.SqueezeHeight * sy);
            var sqX = layer.SqueezeX * sx;
            var sqY = layer.SqueezeY * sy;
            var squeezeGuide = new Rectangle
            {
                Width = sqW, Height = sqH,
                Fill = new SolidColorBrush(Color.FromArgb(40, 0, 229, 255)),
                Stroke = new SolidColorBrush(Color.FromRgb(0, 229, 255)),
                StrokeThickness = 2.0, StrokeDashArray = new DoubleCollection { 6, 3 },
                Tag = new PreviewHit { Layer = layer, Mode = "move_pip" },
                Cursor = Cursors.SizeAll,
                ToolTip = "Drag PIP squeeze box to reposition · Use corner/edge handles to resize Program Video PIP"
            };
            Canvas.SetLeft(squeezeGuide, sqX); Canvas.SetTop(squeezeGuide, sqY);
            Panel.SetZIndex(squeezeGuide, 1005); PreviewCanvas.Children.Add(squeezeGuide);

            var squeezeLabel = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(230, 0, 24, 36)), BorderBrush = new SolidColorBrush(Color.FromRgb(0, 229, 255)),
                BorderThickness = new Thickness(1), Padding = new Thickness(5, 2, 5, 2), IsHitTestVisible = false,
                Child = new TextBlock { Text = $"PROGRAM VIDEO PIP · {layer.SqueezeWidth:0}×{layer.SqueezeHeight:0} (DRAG / RESIZE)", Foreground = new SolidColorBrush(Color.FromRgb(0, 229, 255)), FontSize = 8.5, FontWeight = FontWeights.Bold }
            };
            Canvas.SetLeft(squeezeLabel, sqX + 4); Canvas.SetTop(squeezeLabel, sqY + 4);
            Panel.SetZIndex(squeezeLabel, 1006); PreviewCanvas.Children.Add(squeezeLabel);

            AddPreviewHandle(layer, "pip_nw", sqX - 5, sqY - 5, Cursors.SizeNWSE, Brushes.Cyan, Brushes.White);
            AddPreviewHandle(layer, "pip_ne", sqX + sqW - 5, sqY - 5, Cursors.SizeNESW, Brushes.Cyan, Brushes.White);
            AddPreviewHandle(layer, "pip_sw", sqX - 5, sqY + sqH - 5, Cursors.SizeNESW, Brushes.Cyan, Brushes.White);
            AddPreviewHandle(layer, "pip_se", sqX + sqW - 5, sqY + sqH - 5, Cursors.SizeNWSE, Brushes.Cyan, Brushes.White);
            AddPreviewHandle(layer, "pip_n", sqX + sqW / 2 - 5, sqY - 5, Cursors.SizeNS, Brushes.Cyan, Brushes.White);
            AddPreviewHandle(layer, "pip_s", sqX + sqW / 2 - 5, sqY + sqH - 5, Cursors.SizeNS, Brushes.Cyan, Brushes.White);
            AddPreviewHandle(layer, "pip_w", sqX - 5, sqY + sqH / 2 - 5, Cursors.SizeWE, Brushes.Cyan, Brushes.White);
            AddPreviewHandle(layer, "pip_e", sqX + sqW - 5, sqY + sqH / 2 - 5, Cursors.SizeWE, Brushes.Cyan, Brushes.White);
        }

        var pNW = group.Transform(new Point(0, 0));
        var pNE = group.Transform(new Point(baseW, 0));
        var pSW = group.Transform(new Point(0, baseH));
        var pSE = group.Transform(new Point(baseW, baseH));
        var pN = group.Transform(new Point(baseW / 2, 0));
        var pS = group.Transform(new Point(baseW / 2, baseH));
        var pW = group.Transform(new Point(0, baseH / 2));
        var pE = group.Transform(new Point(baseW, baseH / 2));

        AddPreviewHandle(layer, "nw", x + pNW.X - 5, y + pNW.Y - 5, Cursors.SizeNWSE);
        AddPreviewHandle(layer, "ne", x + pNE.X - 5, y + pNE.Y - 5, Cursors.SizeNESW);
        AddPreviewHandle(layer, "sw", x + pSW.X - 5, y + pSW.Y - 5, Cursors.SizeNESW);
        AddPreviewHandle(layer, "se", x + pSE.X - 5, y + pSE.Y - 5, Cursors.SizeNWSE);
        AddPreviewHandle(layer, "n", x + pN.X - 5, y + pN.Y - 5, Cursors.SizeNS);
        AddPreviewHandle(layer, "s", x + pS.X - 5, y + pS.Y - 5, Cursors.SizeNS);
        AddPreviewHandle(layer, "w", x + pW.X - 5, y + pW.Y - 5, Cursors.SizeWE);
        AddPreviewHandle(layer, "e", x + pE.X - 5, y + pE.Y - 5, Cursors.SizeWE);
        var label = new Border { Background = new SolidColorBrush(Color.FromArgb(220, 31, 16, 42)), Padding = new Thickness(5,2,5,2), IsHitTestVisible = false,
            Child = new TextBlock { Text = $"{layer.Name}  X {motion.X:0}  Y {motion.Y:0}  {motion.Width:0}×{motion.Height:0}", Foreground = Brushes.White, FontSize = 8.5 } };
        Canvas.SetLeft(label, Math.Max(0, x + pNW.X)); Canvas.SetTop(label, Math.Max(0, y + pNW.Y - 22)); Panel.SetZIndex(label, 1001); PreviewCanvas.Children.Add(label);
        _previewSelectionLabel = label;
    }

    private void AddPreviewHandle(CgLayer layer, string mode, double x, double y, Cursor cursor, Brush? fill = null, Brush? stroke = null)
    {
        var handle = new Rectangle { Width = 10, Height = 10, Fill = fill ?? Brushes.White, Stroke = stroke ?? new SolidColorBrush(Color.FromRgb(139,61,255)), StrokeThickness = 1,
            Tag = new PreviewHit { Layer = layer, Mode = mode }, Cursor = cursor };
        Canvas.SetLeft(handle, x); Canvas.SetTop(handle, y); Panel.SetZIndex(handle, 1008); PreviewCanvas.Children.Add(handle);
        _previewSelectionHandles[mode] = handle;
    }

    private FrameworkElement? BuildElement(CgProject project, CgLayer layer, double sx, double sy, string? resolvedText)
    {
        var fill=BrushFrom(layer.Fill, Brushes.White); var bg=BrushFrom(layer.Background, Brushes.Transparent);
        switch ((layer.Type??"Text").ToUpperInvariant())
        {
            case "SHAPE": return ShapePreview(layer, bg, fill);
            case "LINE": return ShapePreview(layer, bg, fill);
            case "IMAGE": case "IMAGESEQUENCE":
                var path=string.Equals(layer.Type, "ImageSequence", StringComparison.OrdinalIgnoreCase) ? CgDataSourceService.ResolveSequenceFrame(project,layer,ContinuousPlaybackSeconds) : layer.Source;
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    try
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        {
                            bmp.StreamSource = fs;
                            bmp.EndInit();
                        }
                        bmp.Freeze();
                        return new Image { Source = bmp, Stretch = Stretch.Fill };
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[CG] BuildElement IMAGE FAIL: {path} → {ex.Message}");
                    }
                }
                else if (string.Equals(layer.Type, "ImageSequence", StringComparison.OrdinalIgnoreCase))
                {
                    System.Diagnostics.Debug.WriteLine($"[CG] BuildElement SEQ empty/missing: Source={layer.Source} ResolvedPath={path ?? "(null)"} Playhead={ContinuousPlaybackSeconds:F3}");
                }
                if (string.Equals(layer.Type, "ImageSequence", StringComparison.OrdinalIgnoreCase))
                {
                    return new Border { Background = Brushes.Transparent };
                }
                return MediaPlaceholder(layer,"IMAGE");
            case "VIDEO": return VideoPreviewElement(layer);
            case "PRECOMP": return PrecompositionPreviewElement(layer, Math.Max(2, (int)Math.Round(layer.Width * sx)), Math.Max(2, (int)Math.Round(layer.Height * sy)));
            case "HTML": return MediaPlaceholder(layer,"HTML");
            case "DIGITALCLOCK":
                var digiNow = CgClockTime.Now(layer);
                if (!string.IsNullOrWhiteSpace(layer.DataFormat) && (layer.DataFormat.Contains("BS") || layer.DataFormat.Contains("YYYY") || layer.DataFormat.Contains("DD") || layer.DataFormat.Contains("MMMM") || layer.DataFormat.Contains("२०") || layer.DataFormat.Contains("Nepali")))
                {
                    var digiNep = NepaliCalendarService.ResolveBroadcastFormat(layer.DataFormat, digiNow);
                    return TextElement(digiNep, layer, fill, bg, sy);
                }
                else if (!string.IsNullOrWhiteSpace(layer.Text) && (layer.Text.Contains("BS") || layer.Text.Contains("YYYY") || layer.Text.Contains("२०") || layer.Text.Contains("Nepali")))
                {
                    var digiNep = NepaliCalendarService.ResolveBroadcastFormat(layer.Text, digiNow);
                    return TextElement(digiNep, layer, fill, bg, sy);
                }
                var digiFormat = string.IsNullOrWhiteSpace(layer.DataFormat) ? "HH:mm:ss" : layer.DataFormat;
                try { return TextElement(digiNow.ToString(digiFormat), layer, fill, bg, sy); } catch { return TextElement(digiNow.ToString("HH:mm:ss"), layer, fill, bg, sy); }
            case "DATETIME":
                var dateNow = CgClockTime.Now(layer);
                if (string.Equals(layer.CalendarSystem, "BS", StringComparison.OrdinalIgnoreCase))
                {
                    var bsFormat = string.IsNullOrWhiteSpace(layer.DataFormat) || layer.DataFormat == "{0}" ? "dddd, DD MMMM YYYY  HH:mm:ss" : layer.DataFormat;
                    return TextElement(NepaliCalendarService.ResolveBroadcastFormat(bsFormat, dateNow), layer, fill, bg, sy);
                }
                var adFormat = string.IsNullOrWhiteSpace(layer.DataFormat) || layer.DataFormat == "{0}" ? "dd MMM yyyy  HH:mm:ss" : layer.DataFormat;
                try { return TextElement(dateNow.ToString(adFormat), layer, fill, bg, sy); } catch { return TextElement(dateNow.ToString("dd MMM yyyy  HH:mm:ss"), layer, fill, bg, sy); }
            case "NEPALIDATE":
            case "NEPALICLOCK":
                var nepFormat = string.IsNullOrWhiteSpace(layer.DataFormat) ? (layer.Text ?? "dddd, DD MMMM YYYY") : layer.DataFormat;
                var nepText = NepaliCalendarService.FormatNepaliDate(CgClockTime.Now(layer), nepFormat);
                return TextElement(nepText, layer, fill, bg, sy);
            case "ANALOGCLOCK": return AnalogClock(layer,fill,bg);
            case "WEATHERICON": return WeatherIconPreview(resolvedText, layer);
            case "TICKER":
                return TickerPreviewElement(layer, resolvedText, fill, bg, sx, sy);
            case "ROLL": return TextElement(resolvedText,layer,fill,bg,sy);
            default: return TextElement(resolvedText,layer,fill,bg,sy);
        }
    }

    private FrameworkElement WeatherIconPreview(string? code, CgLayer layer)
    {
        code = (code ?? layer.Text ?? "na").Trim().ToLowerInvariant();
        var prefix = code.Length >= 2 ? code[..2] : code;
        var local = Math.Max(0, ContinuousPlaybackSeconds - layer.StartSeconds);
        var bob = Math.Sin(local * 2.2) * 3.5;
        var canvas = new Canvas { Width = 180, Height = 180, ClipToBounds = false };

        static SolidColorBrush B(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        void AddEllipse(double x, double y, double w, double h, Brush brush)
            => canvas.Children.Add(new Ellipse { Width = w, Height = h, Fill = brush, RenderTransform = new TranslateTransform(x, y) });
        void AddLine(double x1, double y1, double x2, double y2, Brush brush, double thickness)
            => canvas.Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = brush, StrokeThickness = thickness, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });

        void Sun(double ox = 0, double oy = 0)
        {
            var yellow = B("#FFFFC83D");
            AddEllipse(60 + ox, 60 + oy, 60, 60, yellow);
            var rot = local * 28.0 * Math.PI / 180.0;
            for (var i = 0; i < 8; i++)
            {
                var a = rot + i * Math.PI / 4;
                AddLine(90 + ox + Math.Cos(a) * 44, 90 + oy + Math.Sin(a) * 44,
                        90 + ox + Math.Cos(a) * 62, 90 + oy + Math.Sin(a) * 62, B("#FFFFD85C"), 4);
            }
        }

        void Moon(double ox = 0, double oy = 0)
        {
            var moon = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 112,42 A 52,52 0 1 0 130,132 A 43,43 0 1 1 112,42 Z"),
                Fill = B("#FFFFE8A3"),
                RenderTransform = new TranslateTransform(ox, oy)
            };
            canvas.Children.Add(moon);
        }

        void Cloud(double ox = 0, double oy = 0)
        {
            var white = B("#FFF4F8FB");
            AddEllipse(32 + ox, 74 + oy, 76, 52, white);
            AddEllipse(70 + ox, 48 + oy, 72, 74, white);
            AddEllipse(108 + ox, 78 + oy, 70, 50, white);
            canvas.Children.Add(new Rectangle { Width = 128, Height = 30, Fill = white, RenderTransform = new TranslateTransform(38 + ox, 94 + oy) });
        }

        switch (prefix)
        {
            case "01":
                if (code.EndsWith('n')) Moon(0, bob); else Sun(0, bob);
                break;
            case "02":
                if (code.EndsWith('n')) Moon(-34, -24 + bob); else Sun(-34, -24 + bob);
                Cloud(18, 12 + bob);
                break;
            case "03":
            case "04":
                Cloud(0, bob);
                break;
            case "09":
            case "10":
                if (prefix == "10") Sun(-36, -28 + bob);
                Cloud(12, bob);
                for (var i = 0; i < 4; i++)
                {
                    var phase = (local * 44 + i * 25) % 38;
                    AddLine(42 + i * 32, 128 + phase, 35 + i * 32, 146 + phase, B("#FF5CCBFF"), 5);
                }
                break;
            case "11":
                Cloud(0, bob);
                canvas.Children.Add(new Polygon
                {
                    Points = [new Point(92,114), new Point(72,156), new Point(92,150), new Point(82,178), new Point(122,138), new Point(100,142)],
                    Fill = B(local % .8 < .22 ? "#FFFFFFFF" : "#FFFFD54A")
                });
                break;
            case "13":
                Cloud(0, bob);
                for (var i = 0; i < 6; i++)
                {
                    var phase = (local * 24 + i * 17) % 48;
                    AddEllipse(32 + i * 24 + Math.Sin(local * 1.8 + i) * 5, 124 + phase, 7, 7, B("#FFDFF7FF"));
                }
                break;
            case "50":
                for (var i = 0; i < 4; i++)
                {
                    var slide = Math.Sin(local * 1.4 + i) * 10;
                    AddLine(25 + slide, 54 + i * 25, 155 + slide, 54 + i * 25, B("#FFD7E2EA"), 5);
                }
                break;
            default:
                canvas.Children.Add(new TextBlock { Text = "?", FontSize = 64, FontWeight = FontWeights.Bold, Foreground = Brushes.White, Width = 180, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 48, 0, 0) });
                break;
        }

        return new Viewbox { Stretch = Stretch.Uniform, Child = canvas };
    }

    private FrameworkElement TickerPreviewElement(CgLayer layer, string? resolvedText, Brush fill, Brush bg, double sx, double sy)
    {
        var raw = string.IsNullOrWhiteSpace(resolvedText) ? (layer.Text ?? "") : resolvedText;
        var p = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var separatorText = string.IsNullOrEmpty(layer.TickerSeparator) ? "  ★  " : layer.TickerSeparator;
        if (!separatorText.EndsWith(' ')) separatorText += " ";
        if (!separatorText.StartsWith(' ')) separatorText = " " + separatorText;

        var badgeBg = BrushFrom(string.IsNullOrWhiteSpace(layer.TickerCategoryBadgeBackground) ? "#FFE63946" : layer.TickerCategoryBadgeBackground, Brushes.Crimson);
        var fontSize = Math.Max(8, layer.FontSize * sy);
        var badgeFontSize = Math.Max(7, (layer.FontSize * 0.82) * sy);
        var fontFamily = ResolveFontFamily(layer.FontFamily);
        var fontWeight = layer.Bold ? FontWeights.Bold : FontWeights.Normal;

        var list = new List<(string Cat, string Text, bool IsBadge)>();
        if (raw.Contains('[') && raw.Contains(']'))
        {
            var matches = System.Text.RegularExpressions.Regex.Matches(raw, @"\[(.*?)\]([^\[]*)");
            var sepChars = new List<char> { '•', '|', '\n', '★' };
            if (!string.IsNullOrWhiteSpace(layer.TickerSeparator))
            {
                foreach (var c in layer.TickerSeparator)
                    if (!char.IsWhiteSpace(c) && !sepChars.Contains(c)) sepChars.Add(c);
            }
            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                var cat = m.Groups[1].Value.Trim();
                var content = m.Groups[2].Value.Trim();
                if (!string.IsNullOrWhiteSpace(cat)) list.Add((cat, cat.ToUpperInvariant(), true));
                var subItems = content.Split(sepChars.ToArray(), StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var sub in subItems)
                {
                    var clean = sub.Trim().Trim('•', '|', '★', '-', '—', ' ').Trim();
                    if (!string.IsNullOrWhiteSpace(clean)) list.Add((cat, clean, false));
                }
            }
        }
        else if (layer.TickerCategoriesEnabled && raw.Contains("||"))
        {
            var blocks = raw.Split("||", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var block in blocks)
            {
                var colonIdx = block.IndexOf(':');
                if (colonIdx > 0)
                {
                    var cat = block[..colonIdx].Trim();
                    var rest = block[(colonIdx + 1)..].Trim();
                    list.Add((cat, cat.ToUpperInvariant(), true));
                    var subItems = rest.Split(['•', '|', '\n', '★'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    foreach (var sub in subItems)
                    {
                        var clean = sub.Trim().Trim('•', '|', '★', '-', '—', ' ').Trim();
                        if (!string.IsNullOrWhiteSpace(clean)) list.Add((cat, clean, false));
                    }
                }
                else
                {
                    list.Add(("", block, false));
                }
            }
        }
        else
        {
            var items = raw.Split(['•', '|', '\n', '★'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (items.Length == 0) items = [raw];
            foreach (var item in items)
            {
                var clean = item.Trim().Trim('•', '|', '★', '-', '—', ' ').Trim();
                if (!string.IsNullOrWhiteSpace(clean)) list.Add(("", clean, false));
            }
        }

        if (list.Count == 0) list.Add(("", raw, false));

        var layerW = (layer.Width > 0 ? layer.Width : (SelectedProject?.Width > 0 ? SelectedProject.Width : 1920.0)) * sx;
        var layerH = (layer.Height > 0 ? layer.Height : 50.0) * sy;
        var isRight = (layer.TickerDirection ?? "Left").Equals("Right", StringComparison.OrdinalIgnoreCase);

        if (string.Equals(layer.TickerMode, "Push", StringComparison.OrdinalIgnoreCase))
        {
            var speed = Math.Max(20, layer.Speed);
            var hold = layer.DataItemDurationSeconds > 0.05
                ? layer.DataItemDurationSeconds
                : Math.Max(1.5, 360.0 / speed);
            var pushElapsed = Math.Max(0, ContinuousPlaybackSeconds - layer.StartSeconds);
            // Resolve data-source items before calculating the push index. Previously this
            // happened later in the crawl-only path, so a JSON-backed Push ticker usually
            // contained one already-resolved string and appeared not to move at all.
            if (layer.DataSourceId != Guid.Empty && SelectedProject?.DataSources is not null)
            {
                var pushSource = SelectedProject.DataSources.FirstOrDefault(x => x.Id == layer.DataSourceId);
                var pushRows = pushSource is null ? null : CgDataRuntime.Shared.GetRows(pushSource);
                if (pushRows is { Count: > 0 })
                {
                    var schedule = CgDataSourceService.BuildCategoryFeedSchedule(
                        SelectedProject, layer, pushRows,
                        layer.Width > 10 ? layer.Width : SelectedProject.Width, speed);
                    if (schedule.Categories.Count > 0)
                    {
                        var (activeCategory, categoryElapsed, _) = schedule.Evaluate(Math.Max(0, ContinuousPlaybackSeconds));
                        list = activeCategory.Items
                            .Where(x => !string.IsNullOrWhiteSpace(x))
                            .Select(x => (activeCategory.Category, x, false))
                            .ToList();
                        if (list.Count == 0 && !string.IsNullOrWhiteSpace(activeCategory.CombinedItemsText))
                            list.Add((activeCategory.Category, activeCategory.CombinedItemsText, false));
                        pushElapsed = Math.Max(0, categoryElapsed - activeCategory.IntroDuration);
                    }
                }
            }
            if (list.Count == 0) list.Add(("", raw, false));
            var gapSec = layer.TickerGap switch
            {
                > 10 => layer.TickerGap / 1000.0,
                > 0 => layer.TickerGap,
                _ => 0
            };
            // Category schedules are gapless; static push tickers honor their authored gap.
            if (layer.DataSourceId != Guid.Empty) gapSec = 0;
            var itemTotalTime = Math.Max(0.1, hold + gapSec);
            var totalCycleDuration = list.Count * itemTotalTime;
            var cycleElapsed = totalCycleDuration > 0.05 ? (pushElapsed % totalCycleDuration) : pushElapsed;
            var index = (int)Math.Floor(cycleElapsed / itemTotalTime) % list.Count;
            var itemElapsed = cycleElapsed % itemTotalTime;
            var next = (index + 1) % list.Count;
            var transDur = Math.Min(0.6, hold * 0.28);
            var transStart = Math.Max(0.0, hold - transDur);
            var pVal = itemElapsed < transStart ? 0.0 : (itemElapsed - transStart) / Math.Max(.001, transDur);
            pVal = Math.Clamp(pVal, 0, 1);
            var pSmooth = pVal * pVal * (3 - 2 * pVal);

            FrameworkElement CreateSegmentVisual((string Cat, string Text, bool IsBadge) seg)
            {
                if (seg.IsBadge)
                {
                    return new Border
                    {
                        Background = badgeBg,
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(10 * sx, 2 * sy, 10 * sx, 2 * sy),
                        VerticalAlignment = VerticalAlignment.Center,
                        Child = new TextBlock
                        {
                            Text = seg.Text,
                            Foreground = Brushes.White,
                            FontFamily = fontFamily,
                            FontSize = badgeFontSize,
                            FontWeight = FontWeights.Bold,
                            VerticalAlignment = VerticalAlignment.Center
                        }
                    };
                }
                return new TextBlock
                {
                    Text = seg.Text,
                    Foreground = fill,
                    FontFamily = fontFamily,
                    FontSize = fontSize,
                    FontWeight = fontWeight,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }

            var pushCanvas = new Canvas
            {
                Width = layerW,
                Height = layerH,
                ClipToBounds = true
            };

            var curVis = CreateSegmentVisual(list[index]);
            var nextVis = CreateSegmentVisual(list[next]);

            curVis.Measure(new Size(double.PositiveInfinity, layerH));
            nextVis.Measure(new Size(double.PositiveInfinity, layerH));

            var curTop = Math.Max(0, (layerH - (curVis.DesiredSize.Height > 0 ? curVis.DesiredSize.Height : fontSize * 1.2)) / 2.0);
            var nextTop = Math.Max(0, (layerH - (nextVis.DesiredSize.Height > 0 ? nextVis.DesiredSize.Height : fontSize * 1.2)) / 2.0);
            var direction = (layer.TickerDirection ?? "Left").Trim().ToUpperInvariant();

            // Match the program compositor's first-item entrance. This was previously
            // omitted in the editor, and Up/Down were incorrectly treated as Left.
            if (pushElapsed < transDur)
            {
                var enterRaw = Math.Clamp(pushElapsed / Math.Max(.001, transDur), 0, 1);
                var enter = enterRaw * enterRaw * (3 - 2 * enterRaw);
                Canvas.SetLeft(curVis, direction == "RIGHT" ? -(1 - enter) * layerW : 10);
                Canvas.SetTop(curVis, direction switch
                {
                    "UP" => curTop + (1 - enter) * layerH,
                    "DOWN" => curTop - (1 - enter) * layerH,
                    _ => curTop
                });
                if (direction == "LEFT") Canvas.SetLeft(curVis, (1 - enter) * layerW);
                pushCanvas.Children.Add(curVis);
            }
            else
            {
                switch (direction)
                {
                    case "UP":
                        Canvas.SetLeft(curVis, 10); Canvas.SetTop(curVis, curTop - pSmooth * layerH);
                        Canvas.SetLeft(nextVis, 10); Canvas.SetTop(nextVis, nextTop + (1 - pSmooth) * layerH);
                        break;
                    case "DOWN":
                        Canvas.SetLeft(curVis, 10); Canvas.SetTop(curVis, curTop + pSmooth * layerH);
                        Canvas.SetLeft(nextVis, 10); Canvas.SetTop(nextVis, nextTop - (1 - pSmooth) * layerH);
                        break;
                    case "RIGHT":
                        Canvas.SetTop(curVis, curTop); Canvas.SetTop(nextVis, nextTop);
                        Canvas.SetLeft(curVis, pSmooth * layerW); Canvas.SetLeft(nextVis, -(1 - pSmooth) * layerW);
                        break;
                    default:
                        Canvas.SetTop(curVis, curTop); Canvas.SetTop(nextVis, nextTop);
                        Canvas.SetLeft(curVis, -pSmooth * layerW); Canvas.SetLeft(nextVis, (1 - pSmooth) * layerW);
                        break;
                }
                pushCanvas.Children.Add(curVis);
                if (pSmooth > .001) pushCanvas.Children.Add(nextVis);
            }

            return new Border
            {
                Background = layer.UseBackground ? bg : Brushes.Transparent,
                BorderBrush = BrushFrom(layer.BorderColor, Brushes.Transparent),
                BorderThickness = new Thickness(Math.Max(0, layer.BorderWidth) * .5),
                CornerRadius = LayerCornerRadius(layer, .5),
                Child = pushCanvas,
                HorizontalAlignment = HorizontalAlignment.Left
            };
        }

        for (var i = 0; i < list.Count; i++)
        {
            var seg = list[i];
            if (seg.IsBadge)
            {
                var badge = new Border
                {
                    Background = badgeBg,
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(10 * sx, 2 * sy, 10 * sx, 2 * sy),
                    Margin = new Thickness(0, 0, 14 * sx, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = seg.Text,
                        Foreground = Brushes.White,
                        FontFamily = fontFamily,
                        FontSize = badgeFontSize,
                        FontWeight = FontWeights.Bold,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };
                p.Children.Add(badge);
            }
            else
            {
                var tb = new TextBlock
                {
                    Text = seg.Text,
                    Foreground = fill,
                    FontFamily = fontFamily,
                    FontSize = fontSize,
                    FontWeight = fontWeight,
                    VerticalAlignment = VerticalAlignment.Center
                };
                p.Children.Add(tb);

                var sepTb = new TextBlock
                {
                    Text = separatorText,
                    Foreground = fill,
                    FontFamily = fontFamily,
                    FontSize = fontSize,
                    FontWeight = FontWeights.Bold,
                    VerticalAlignment = VerticalAlignment.Center
                };
                if (!string.IsNullOrWhiteSpace(layer.TickerSeparatorLogo) && System.IO.File.Exists(layer.TickerSeparatorLogo))
                {
                    try
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.UriSource = new Uri(System.IO.Path.GetFullPath(layer.TickerSeparatorLogo));
                        bmp.EndInit();
                        bmp.Freeze();
                        var img = new Image
                        {
                            Source = bmp,
                            Height = Math.Max(12, fontSize * 0.85),
                            Margin = new Thickness(8 * sx, 0, 8 * sx, 0),
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        p.Children.Add(img);
                    }
                    catch
                    {
                        p.Children.Add(sepTb);
                    }
                }
                else
                {
                    p.Children.Add(sepTb);
                }
            }
        }

        CgDataSource? ds = null;
        if (layer.DataSourceId != Guid.Empty && SelectedProject?.DataSources != null)
            ds = SelectedProject.DataSources.FirstOrDefault(x => x.Id == layer.DataSourceId);
        var rows = ds != null ? CgDataRuntime.Shared.GetRows(ds) : null;

        var speedVal = Math.Max(20.0, layer.TickerSpeed > 0 ? layer.TickerSpeed : layer.Speed);
        var speedPx = speedVal * sx;

        if (rows != null && rows.Count > 0)
        {
            var hasDedicatedCategory = SelectedProject?.Layers?.Any(x => x.Visible && x.Id != layer.Id &&
                (string.Equals(x.DataField, "category", StringComparison.OrdinalIgnoreCase) ||
                 (x.Name?.Contains("Badge", StringComparison.OrdinalIgnoreCase) ?? false))) == true;
            var showInlineBadge = layer.TickerCategoriesEnabled && !hasDedicatedCategory;

            var schedule = CgDataSourceService.BuildCategoryFeedSchedule(SelectedProject ?? new CgProject(), layer, rows, layerW, speedPx);
            if (schedule.Categories.Count > 0)
            {
                var (activeCat, categoryElapsed, _) = schedule.Evaluate(Math.Max(0, ContinuousPlaybackSeconds));
                var newsElapsed = Math.Max(0, categoryElapsed - activeCat.IntroDuration);
                var segments = activeCat.Items
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => new CgTickerSegment(activeCat.Category, x, false))
                    .ToList();
                if (segments.Count == 0 && !string.IsNullOrWhiteSpace(activeCat.CombinedItemsText))
                    segments.Add(new CgTickerSegment(activeCat.Category, activeCat.CombinedItemsText, false));

                p.Children.Clear();
                if (showInlineBadge && !string.IsNullOrWhiteSpace(activeCat.Category))
                {
                    var badge = new Border
                    {
                        Background = badgeBg,
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(10 * sx, 2 * sy, 10 * sx, 2 * sy),
                        Margin = new Thickness(0, 0, 14 * sx, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Child = new TextBlock
                        {
                            Text = activeCat.Category,
                            Foreground = Brushes.White,
                            FontFamily = fontFamily,
                            FontSize = badgeFontSize,
                            FontWeight = FontWeights.Bold,
                            VerticalAlignment = VerticalAlignment.Center
                        }
                    };
                    p.Children.Add(badge);
                }

                for (var i = 0; i < segments.Count; i++)
                {
                    var tb = new TextBlock
                    {
                        Text = segments[i].Text,
                        Foreground = fill,
                        FontFamily = fontFamily,
                        FontSize = fontSize,
                        FontWeight = fontWeight,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    p.Children.Add(tb);
                    if (i < segments.Count - 1 || layer.TickerRepeat)
                    {
                        var sepTb = new TextBlock
                        {
                            Text = separatorText,
                            Foreground = fill,
                            FontFamily = fontFamily,
                            FontSize = fontSize,
                            FontWeight = FontWeights.Bold,
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        p.Children.Add(sepTb);
                    }
                }

                p.Measure(new Size(double.PositiveInfinity, layerH));
                var catContentW = Math.Max(20.0, p.DesiredSize.Width);
                var totalTravelSpan = Math.Max(1.0, layerW + catContentW);
                var catTravelled = Math.Min(totalTravelSpan, newsElapsed * speedPx);
                var catStartX = isRight ? (-catContentW + catTravelled) : (layerW - catTravelled);

                var catCanvas = new Canvas
                {
                    Width = layerW,
                    Height = layerH,
                    ClipToBounds = true
                };
                Canvas.SetLeft(p, catStartX);
                var catTop = Math.Max(0, (layerH - (p.DesiredSize.Height > 0 ? p.DesiredSize.Height : fontSize * 1.2)) / 2.0);
                Canvas.SetTop(p, catTop);
                catCanvas.Children.Add(p);
                return catCanvas;
            }

            var allRawSegments = CgDataSourceService.BuildAllTickerSegments(SelectedProject ?? new CgProject(), layer, rows, showInlineBadge);
            if (allRawSegments.Count > 0)
            {
                list = allRawSegments.Select(s => (s.Category, s.Text, s.IsCategoryBadge)).ToList();
                p.Children.Clear();
                for (var i = 0; i < list.Count; i++)
                {
                    var seg = list[i];
                    if (seg.IsBadge)
                    {
                        var badge = new Border
                        {
                            Background = badgeBg,
                            CornerRadius = new CornerRadius(4),
                            Padding = new Thickness(10 * sx, 2 * sy, 10 * sx, 2 * sy),
                            Margin = new Thickness(0, 0, 14 * sx, 0),
                            VerticalAlignment = VerticalAlignment.Center,
                            Child = new TextBlock
                            {
                                Text = seg.Text,
                                Foreground = Brushes.White,
                                FontFamily = fontFamily,
                                FontSize = badgeFontSize,
                                FontWeight = FontWeights.Bold,
                                VerticalAlignment = VerticalAlignment.Center
                            }
                        };
                        p.Children.Add(badge);
                    }
                    else
                    {
                        var tb = new TextBlock
                        {
                            Text = seg.Text,
                            Foreground = fill,
                            FontFamily = fontFamily,
                            FontSize = fontSize,
                            FontWeight = fontWeight,
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        p.Children.Add(tb);
                        var sepTb = new TextBlock
                        {
                            Text = separatorText,
                            Foreground = fill,
                            FontFamily = fontFamily,
                            FontSize = fontSize,
                            FontWeight = FontWeights.Bold,
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        p.Children.Add(sepTb);
                    }
                }
            }
        }

        p.Measure(new Size(double.PositiveInfinity, layerH));
        var totalW = Math.Max(20.0, p.DesiredSize.Width);

        var elapsed = Math.Max(0, ContinuousPlaybackSeconds - layer.StartSeconds);
        var distance = elapsed * speedPx;
        var repeating = layer.TickerRepeat && distance >= totalW;
        var phase = repeating ? (distance - totalW) % Math.Max(1.0, totalW) : distance;
        var startX = isRight
            ? (repeating ? phase : -totalW + distance)
            : (repeating ? layerW - totalW - phase : layerW - distance);

        var crawlCanvas = new Canvas
        {
            Width = layerW,
            Height = layerH,
            ClipToBounds = true
        };
        Canvas.SetLeft(p, startX);
        var top = Math.Max(0, (layerH - (p.DesiredSize.Height > 0 ? p.DesiredSize.Height : fontSize * 1.2)) / 2.0);
        Canvas.SetTop(p, top);
        crawlCanvas.Children.Add(p);

        if (repeating)
        {
            StackPanel CreateDuplicatePanel()
            {
                var dup = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                for (var i = 0; i < list.Count; i++)
                {
                    var seg = list[i];
                    if (seg.IsBadge)
                    {
                        dup.Children.Add(new Border
                        {
                            Background = badgeBg,
                            CornerRadius = new CornerRadius(4),
                            Padding = new Thickness(10 * sx, 2 * sy, 10 * sx, 2 * sy),
                            Margin = new Thickness(0, 0, 14 * sx, 0),
                            VerticalAlignment = VerticalAlignment.Center,
                            Child = new TextBlock
                            {
                                Text = seg.Text,
                                Foreground = Brushes.White,
                                FontFamily = fontFamily,
                                FontSize = badgeFontSize,
                                FontWeight = FontWeights.Bold,
                                VerticalAlignment = VerticalAlignment.Center
                            }
                        });
                    }
                    else
                    {
                        dup.Children.Add(new TextBlock
                        {
                            Text = seg.Text,
                            Foreground = fill,
                            FontFamily = fontFamily,
                            FontSize = fontSize,
                            FontWeight = fontWeight,
                            VerticalAlignment = VerticalAlignment.Center
                        });
                        dup.Children.Add(new TextBlock
                        {
                            Text = separatorText,
                            Foreground = fill,
                            FontFamily = fontFamily,
                            FontSize = fontSize,
                            FontWeight = FontWeights.Bold,
                            VerticalAlignment = VerticalAlignment.Center
                        });
                    }
                }
                return dup;
            }

            if (isRight)
            {
                var cur = startX + totalW;
                while (cur < layerW)
                {
                    var pNext = CreateDuplicatePanel();
                    Canvas.SetLeft(pNext, cur);
                    Canvas.SetTop(pNext, top);
                    crawlCanvas.Children.Add(pNext);
                    cur += totalW;
                }
                cur = startX - totalW;
                while (cur + totalW > 0)
                {
                    var pPrev = CreateDuplicatePanel();
                    Canvas.SetLeft(pPrev, cur);
                    Canvas.SetTop(pPrev, top);
                    crawlCanvas.Children.Add(pPrev);
                    cur -= totalW;
                }
            }
            else
            {
                var cur = startX + totalW;
                while (cur < layerW)
                {
                    var pNext = CreateDuplicatePanel();
                    Canvas.SetLeft(pNext, cur);
                    Canvas.SetTop(pNext, top);
                    crawlCanvas.Children.Add(pNext);
                    cur += totalW;
                }
                cur = startX - totalW;
                while (cur + totalW > 0)
                {
                    var pPrev = CreateDuplicatePanel();
                    Canvas.SetLeft(pPrev, cur);
                    Canvas.SetTop(pPrev, top);
                    crawlCanvas.Children.Add(pPrev);
                    cur -= totalW;
                }
            }
        }

        var border = new Border
        {
            Background = layer.UseBackground ? bg : Brushes.Transparent,
            BorderBrush = BrushFrom(layer.BorderColor, Brushes.Transparent),
            BorderThickness = new Thickness(Math.Max(0, layer.BorderWidth) * .5),
            CornerRadius = LayerCornerRadius(layer, .5),
            Child = crawlCanvas,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        return border;
    }

    private FrameworkElement BuildHtmlPreview(CgHtmlSource html)
    {
        using var bitmap = ChromiumCgRenderer.Shared.GetLatest(html);
        if (bitmap is null)
            return new Border { Background=new SolidColorBrush(Color.FromArgb(150,8,13,18)), BorderBrush=Brushes.DeepSkyBlue, BorderThickness=new Thickness(1), Child=new TextBlock { Text=$"CHROMIUM HTML\n{html.Name}\n{html.Status}", Foreground=Brushes.White, HorizontalAlignment=HorizontalAlignment.Center, VerticalAlignment=VerticalAlignment.Center, TextAlignment=TextAlignment.Center } };
        try
        {
            using var ms = new MemoryStream(); bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png); ms.Position=0;
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption=BitmapCacheOption.OnLoad; image.StreamSource=ms; image.EndInit(); image.Freeze();
            return new Image { Source=image, Stretch=Stretch.Fill };
        }
        catch { return new Border { Background=Brushes.Transparent }; }
    }

    private FrameworkElement PrecompositionPreviewElement(CgLayer layer, int width, int height)
    {
        var nested = layer.Precomposition;
        if (nested is null) return MediaPlaceholder(layer, "PRECOMP");
        try
        {
            width = Math.Clamp(width, 2, 1920);
            height = Math.Clamp(height, 2, 1080);
            var frame = _editorVideoPreviewCompositor.RenderProjectSurface(nested, width, height, Math.Max(0, Playhead - layer.StartSeconds));
            var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.Bgra, frame.Stride);
            bitmap.Freeze();
            return new Image { Source = bitmap, Stretch = Stretch.Fill, SnapsToDevicePixels = false };
        }
        catch { return MediaPlaceholder(layer, "PRECOMP"); }
    }

    private FrameworkElement VideoPreviewElement(CgLayer layer)
    {
        try
        {
            var frame = _editorVideoPreviewCompositor.GetVideoLayerFrame(layer, Math.Max(0, Playhead - layer.StartSeconds));
            if (frame is null)
                return MediaPlaceholder(layer, string.IsNullOrWhiteSpace(layer.VideoSourceKind) ? "VIDEO" : $"VIDEO · {layer.VideoSourceKind.ToUpperInvariant()}");
            var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.Bgra, frame.Stride);
            bitmap.Freeze();
            return new Image { Source = bitmap, Stretch = Stretch.Fill, SnapsToDevicePixels = true };
        }
        catch
        {
            return MediaPlaceholder(layer, "VIDEO");
        }
    }

    private static Brush CreatePreviewGradientBrush(CgLayer layer, string primary, string secondary)
    {
        var rawAngle = double.IsFinite(layer.GradientAngle) ? layer.GradientAngle : 0;
        var angle = ((rawAngle % 360) + 360) % 360;
        var radians = angle * Math.PI / 180.0;
        var dx = Math.Cos(radians) * .5;
        var dy = Math.Sin(radians) * .5;
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(.5 - dx, .5 - dy),
            EndPoint = new Point(.5 + dx, .5 + dy),
            MappingMode = BrushMappingMode.RelativeToBoundingBox
        };
        var stops = (layer.GradientStops ?? []).OrderBy(x => x.Offset).ToArray();
        if (stops.Length >= 2)
        {
            foreach (var stop in stops)
                brush.GradientStops.Add(new GradientStop(ColorFrom(stop.Color, Colors.White), FiniteUnit(stop.Offset)));
        }
        else
        {
            brush.GradientStops.Add(new GradientStop(ColorFrom(primary, Colors.White), 0));
            brush.GradientStops.Add(new GradientStop(ColorFrom(secondary, Colors.White), 1));
        }
        brush.Freeze();
        return brush;
    }

    private static bool IsPreetiFont(string? font) =>
        font != null && (font.Contains("Preeti", StringComparison.OrdinalIgnoreCase) ||
                         font.Contains("Kantipur", StringComparison.OrdinalIgnoreCase) ||
                         font.Contains("Ganesh", StringComparison.OrdinalIgnoreCase) ||
                         font.Contains("Suvadin", StringComparison.OrdinalIgnoreCase) ||
                         font.Contains("Sagarmatha", StringComparison.OrdinalIgnoreCase));

    private static bool ContainsDevanagari(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var ch in text)
        {
            if (ch >= 0x0900 && ch <= 0x097F) return true;
        }
        return false;
    }

    private FrameworkElement TextElement(string? text,CgLayer layer,Brush fill,Brush bg,double sy)
    {
        var rawText = text ?? "";
        if (layer.AutoConvertToPreeti || (IsPreetiFont(layer.FontFamily) && ContainsDevanagari(rawText)))
            rawText = NepaliCalendarService.ConvertToPreeti(rawText);
        var isCenter = (layer.HorizontalTextAlignment ?? "Left").Equals("Center", StringComparison.OrdinalIgnoreCase);
        var isRight = (layer.HorizontalTextAlignment ?? "Left").Equals("Right", StringComparison.OrdinalIgnoreCase);
        var tb = new TextBlock
        {
            Text = rawText,
            Foreground = fill,
            FontFamily = ResolveFontFamily(layer.FontFamily),
            FontSize = Math.Max(8, layer.FontSize * sy),
            FontWeight = layer.Bold ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = layer.Italic ? FontStyles.Italic : FontStyles.Normal,
            TextDecorations = layer.Underline ? TextDecorations.Underline : null,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = isCenter ? HorizontalAlignment.Center : (isRight ? HorizontalAlignment.Right : HorizontalAlignment.Left),
            TextAlignment = isCenter ? TextAlignment.Center : (isRight ? TextAlignment.Right : TextAlignment.Left),
            VerticalAlignment = (layer.VerticalTextAlignment ?? "Center").Equals("Top", StringComparison.OrdinalIgnoreCase) ? VerticalAlignment.Top : ((layer.VerticalTextAlignment ?? "Center").Equals("Bottom", StringComparison.OrdinalIgnoreCase) ? VerticalAlignment.Bottom : VerticalAlignment.Center)
        };
        if (layer.BlurRadius > 0)
            tb.Effect = new System.Windows.Media.Effects.BlurEffect { Radius = Math.Max(0, layer.BlurRadius * sy), KernelType = System.Windows.Media.Effects.KernelType.Gaussian };
        else if (layer.ShadowEnabled && (layer.ShadowBlur > 0 || Math.Abs(layer.ShadowOffsetX) > .01 || Math.Abs(layer.ShadowOffsetY) > .01))
        {
            var depth = Math.Sqrt(layer.ShadowOffsetX * layer.ShadowOffsetX + layer.ShadowOffsetY * layer.ShadowOffsetY);
            var angle = Math.Atan2(layer.ShadowOffsetY, layer.ShadowOffsetX) * (180.0 / Math.PI);
            tb.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = ColorFrom(layer.ShadowColor, Colors.Black),
                BlurRadius = Math.Max(0, layer.ShadowBlur * 2),
                ShadowDepth = depth,
                Direction = angle,
                Opacity = Math.Clamp(layer.Opacity > 0 ? layer.Opacity : 0.8, 0, 1)
            };
        }
        var effectiveBg = layer.UseBackground ? bg : Brushes.Transparent;
        return new Border
        {
            Background = effectiveBg,
            BorderBrush = BrushFrom(layer.BorderColor, Brushes.Transparent),
            BorderThickness = new Thickness(Math.Max(0, layer.BorderWidth) * .5),
            CornerRadius = LayerCornerRadius(layer, .5),
            Padding = new Thickness(8, 2, 8, 2),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = tb
        };
    }

    private FrameworkElement ShapePreview(CgLayer layer, Brush bg, Brush fill)
    {
        if(layer.UseGradient) bg=CreatePreviewGradientBrush(layer,layer.Background,layer.GradientColor2);
        var kind=(layer.ShapeKind??"Rectangle").ToUpperInvariant();
        var strokeBrush = BrushFrom(layer.BorderColor, fill);
        var strokeThickness = Math.Max(0, layer.BorderWidth) * .5;

        if (kind is "CUSTOM PATH" or "PEN PATH")
        {
            return new System.Windows.Shapes.Path
            {
                Data = BuildNormalizedPathGeometry(layer.ShapePathData, new Rect(0, 0, 100, 100), layer.ShapePathClosed),
                Fill = layer.ShapePathClosed ? bg : Brushes.Transparent,
                Stroke = strokeBrush,
                StrokeThickness = Math.Max(1, strokeThickness),
                Stretch = Stretch.Fill,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
        }
        if (kind == "ELLIPSE") return new Ellipse { Fill = bg, Stroke = strokeBrush, StrokeThickness = strokeThickness };
        if (kind == "CIRCLE") return new Ellipse { Fill = bg, Stroke = strokeBrush, StrokeThickness = strokeThickness, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (kind == "LINE") return new Border { Background = fill, Height = Math.Max(2, layer.BorderWidth) };
        if (kind is "L-SHAPE" or "LSHAPE" or "U-SHAPE" or "USHAPE")
        {
            var canvas = new Canvas { Background = Brushes.Transparent };
            var arm = kind.StartsWith("L") ? 36 : 32;
            canvas.Children.Add(new Border { Background = bg, Width = arm, Height = 540 });
            var bottom = new Border { Background = bg, Width = 960, Height = arm }; Canvas.SetTop(bottom, 540 - arm); canvas.Children.Add(bottom);
            if (kind.StartsWith("U")) { var right = new Border { Background = bg, Width = arm, Height = 540 }; Canvas.SetLeft(right, 960 - arm); canvas.Children.Add(right); }
            return canvas;
        }

        string? pathData = kind switch
        {
            "TRIANGLE" => "M 50,0 L 100,100 L 0,100 Z",
            "INVERTED TRIANGLE" or "INVERTEDTRIANGLE" => "M 0,0 L 100,0 L 50,100 Z",
            "DIAMOND" => "M 50,0 L 100,50 L 50,100 L 0,50 Z",
            "STAR" => "M 50,0 L 63,35 L 100,38 L 73,63 L 81,100 L 50,81 L 19,100 L 27,63 L 0,38 L 37,35 Z",
            "PENTAGON" => "M 50,0 L 100,38 L 81,100 L 19,100 L 0,38 Z",
            "HEXAGON" => "M 25,0 L 75,0 L 100,50 L 75,100 L 25,100 L 0,50 Z",
            "OCTAGON" => "M 29,0 L 71,0 L 100,29 L 100,71 L 71,100 L 29,100 L 0,71 L 0,29 Z",
            "HEART" => "M 50,30 C 20,0 0,35 0,55 C 0,75 25,90 50,100 C 75,90 100,75 100,55 C 100,35 80,0 50,30 Z",
            "ARROW RIGHT" or "ARROWRIGHT" => "M 0,25 L 60,25 L 60,0 L 100,50 L 60,100 L 60,75 L 0,75 Z",
            "ARROW LEFT" or "ARROWLEFT" => "M 40,0 L 40,25 L 100,25 L 100,75 L 40,75 L 40,100 L 0,50 Z",
            "TRAPEZOID" => "M 20,0 L 80,0 L 100,100 L 0,100 Z",
            "CROSS / PLUS" or "CROSS" or "PLUS" => "M 35,0 L 65,0 L 65,35 L 100,35 L 100,65 L 65,65 L 65,100 L 35,100 L 35,65 L 0,65 L 0,35 L 35,35 Z",
            "SPEECH BUBBLE" or "SPEECHBUBBLE" => "M 10,0 L 90,0 C 95,0 100,5 100,10 L 100,70 C 100,75 95,80 90,80 L 35,80 L 15,100 L 20,80 L 10,80 C 5,80 0,75 0,70 L 0,10 C 0,5 5,0 10,0 Z",
            _ => null
        };

        if (pathData is not null)
        {
            return new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(pathData),
                Fill = bg,
                Stroke = strokeBrush,
                StrokeThickness = strokeThickness,
                Stretch = Stretch.Fill,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
        }

        return new Border { Background = bg, BorderBrush = BrushFrom(layer.BorderColor, Brushes.Transparent), BorderThickness = new Thickness(strokeThickness), CornerRadius = LayerCornerRadius(layer, .5) };
    }
    private static CornerRadius LayerCornerRadius(CgLayer layer, double scale)
    {
        if (layer.CornerRadiusAsPercent)
        {
            var shortest = Math.Min(Math.Max(1, layer.Width), Math.Max(1, layer.Height)) * scale;
            double P(double value) => Math.Clamp(value, 0, 50) / 100.0 * shortest;
            return new CornerRadius(P(layer.CornerRadiusTopLeftPercent), P(layer.CornerRadiusTopRightPercent), P(layer.CornerRadiusBottomRightPercent), P(layer.CornerRadiusBottomLeftPercent));
        }
        var separate = layer.CornerRadiusTopLeft > 0 || layer.CornerRadiusTopRight > 0 || layer.CornerRadiusBottomRight > 0 || layer.CornerRadiusBottomLeft > 0;
        if (!separate)
        {
            var r = Math.Max(0, layer.CornerRadius) * scale;
            return new CornerRadius(r);
        }
        return new CornerRadius(
            Math.Max(0, layer.CornerRadiusTopLeft) * scale,
            Math.Max(0, layer.CornerRadiusTopRight) * scale,
            Math.Max(0, layer.CornerRadiusBottomRight) * scale,
            Math.Max(0, layer.CornerRadiusBottomLeft) * scale);
    }

    private FrameworkElement MediaPlaceholder(CgLayer layer,string tag)
    {
        var kind=string.IsNullOrWhiteSpace(layer.VideoSourceKind)?"File":layer.VideoSourceKind;
        var source=kind.Equals("File",StringComparison.OrdinalIgnoreCase)?IOPath.GetFileName(layer.Source):layer.Source;
        return new Border { Background=BrushFrom(layer.Background,new SolidColorBrush(Color.FromArgb(220,10,16,23))), BorderBrush=Brushes.DeepSkyBlue, BorderThickness=new Thickness(1), Child=new TextBlock { Text=$"{tag}\n{source}", Foreground=Brushes.White, HorizontalAlignment=HorizontalAlignment.Center, VerticalAlignment=VerticalAlignment.Center, TextAlignment=TextAlignment.Center, TextWrapping=TextWrapping.Wrap } };
    }
    private FrameworkElement AnalogClock(CgLayer layer,Brush fill,Brush bg)
    {
        var canvas=new Canvas { Background=bg }; var size=Math.Max(20,Math.Min(layer.Width,layer.Height)*.5); var cx=size/2; var cy=size/2;
        canvas.Children.Add(new Ellipse { Width=size-4, Height=size-4, Stroke=fill, StrokeThickness=2, Margin=new Thickness(2) });
        var now=CgClockTime.Now(layer); AddHand(canvas,cx,cy,size*.25,(now.Hour%12+now.Minute/60.0)*30,fill,3); AddHand(canvas,cx,cy,size*.36,now.Minute*6,fill,2); AddHand(canvas,cx,cy,size*.4,now.Second*6,Brushes.Red,1);
        return canvas;
    }
    private static void AddHand(Canvas c,double cx,double cy,double len,double deg,Brush brush,double thickness) { var r=(deg-90)*Math.PI/180; c.Children.Add(new Line { X1=cx,Y1=cy,X2=cx+Math.Cos(r)*len,Y2=cy+Math.Sin(r)*len,Stroke=brush,StrokeThickness=thickness }); }

    private void ApplyPreviewAnimation(CgLayer layer,ref double x,ref double y,ref double w,ref double h,FrameworkElement el)
    {
        var project = SelectedProject;
        var categoryElapsed = 0.0;
        var categoryDuration = 0.0;
        var categoryIntro = 0.0;
        var categoryOutro = 0.0;
        var categoryDriven = project != null && CgDataSourceService.TryGetActiveCategoryTiming(project, layer, ContinuousPlaybackSeconds,
            out _, out categoryElapsed, out categoryDuration, out _, out categoryIntro, out categoryOutro);
        if (string.Equals(layer.Type, "Ticker", StringComparison.OrdinalIgnoreCase) && !categoryDriven)
        {
            el.Opacity = Math.Clamp(layer.Opacity, 0, 1);
            return;
        }

        var isDataItem = layer.DataSourceId != Guid.Empty && layer.DataItemDurationSeconds > 0;
        var isSequenceType = string.Equals(layer.Type, "ImageSequence", StringComparison.OrdinalIgnoreCase);
        var delay = !isSequenceType ? Math.Max(0, layer.TextAnimationDelaySeconds) : 0;
        var hasAnyTicker = project?.Layers?.Any(l => string.Equals(l.Type, "Ticker", StringComparison.OrdinalIgnoreCase)) == true;
        var isCategoryBadge = string.Equals(layer.DataField, "category", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(layer.Role, "Badge", StringComparison.OrdinalIgnoreCase) ||
                              ((layer.Name?.Contains("Badge", StringComparison.OrdinalIgnoreCase) ?? false) &&
                               (string.Equals(layer.Type, "Text", StringComparison.OrdinalIgnoreCase) || string.Equals(layer.Type, "Shape", StringComparison.OrdinalIgnoreCase))) ||
                              (string.Equals(layer.Type, "Text", StringComparison.OrdinalIgnoreCase) &&
                               (layer.Name?.Contains("Category", StringComparison.OrdinalIgnoreCase) ?? false)) ||
                              layer.TickerCategoriesEnabled;

        if (hasAnyTicker && !isCategoryBadge && layer.DataSourceId == Guid.Empty)
        {
            var layerName = layer.Name ?? "";
            var isStrip = string.Equals(layer.Type, "Shape", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(layer.Type, "ImageSequence", StringComparison.OrdinalIgnoreCase) ||
                          layerName.Contains("Bar", StringComparison.OrdinalIgnoreCase) ||
                          layerName.Contains("Plate", StringComparison.OrdinalIgnoreCase) ||
                          layerName.Contains("Accent", StringComparison.OrdinalIgnoreCase) ||
                          layerName.Contains("Divider", StringComparison.OrdinalIgnoreCase);
            if (isStrip)
            {
                var curTime = ContinuousPlaybackSeconds >= 0 ? ContinuousPlaybackSeconds : Playhead;
                if (curTime < layer.StartSeconds + delay) { el.Opacity = 0; return; }
                if (layer.AnimationInSeconds > 0.01)
                {
                    var inP = Math.Clamp((curTime - layer.StartSeconds - delay) / Math.Max(0.05, layer.AnimationInSeconds), 0, 1);
                    el.Opacity = Math.Clamp(layer.Opacity * (string.Equals(layer.AnimationIn, "Fade", StringComparison.OrdinalIgnoreCase) ? inP : 1), 0, 1);
                    switch ((layer.AnimationIn ?? "").ToUpperInvariant())
                    {
                        case "SLIDE LEFT": x += (1 - inP) * 110; break;
                        case "SLIDE RIGHT": x -= (1 - inP) * 110; break;
                        case "SLIDE UP": y += (1 - inP) * 70; break;
                        case "SLIDE DOWN": y -= (1 - inP) * 70; break;
                    }
                }
                else
                {
                    el.Opacity = Math.Clamp(layer.Opacity, 0, 1);
                }
                return;
            }
        }

        double a, b;
        var isTickerLayer = string.Equals(layer.Type, "Ticker", StringComparison.OrdinalIgnoreCase);
        if (categoryDriven)
        {
            CgDataSourceService.GetCategoryAnimationProgress(categoryElapsed, categoryDuration, categoryIntro, categoryOutro,
                out _, out _, out a, out b);
        }
        else if (isCategoryBadge && project != null)
        {
            var pausePoint = CgDataSourceService.ResolveEffectiveHoldPoint(project);
            var maxOutDuration = project.Layers.Where(x => x.Visible).Select(x => x.AnimationOutSeconds).DefaultIfEmpty(0.5).Max();
            var outPhaseStart = Math.Max(pausePoint, ProjectDuration - maxOutDuration);
            if (Playhead < pausePoint)
            {
                var introTime = Math.Max(0, Playhead - layer.StartSeconds - delay);
                if (introTime <= 0) { el.Opacity = 0; return; }
                a = layer.AnimationInSeconds > 0.01 ? Math.Clamp(introTime / Math.Max(0.05, layer.AnimationInSeconds), 0, 1) : 1.0;
                b = 1.0;
            }
            else if (Playhead >= outPhaseStart)
            {
                a = 1.0;
                b = layer.AnimationOutSeconds > 0.01 ? Math.Clamp((ProjectDuration - Playhead) / Math.Max(0.05, layer.AnimationOutSeconds), 0, 1) : 1.0;
            }
            else
            {
                // During ticker playing: NO fade out, NO fade in!
                a = 1.0;
                b = 1.0;
            }
        }
        else if (isDataItem && !isTickerLayer)
        {
            var itemDur = layer.DataItemDurationSeconds;
            var cycleTime = ContinuousPlaybackSeconds >= 0 ? Math.Max(0, ContinuousPlaybackSeconds % itemDur) : Math.Max(0, Playhead % itemDur);
            var effectiveStart = Math.Max(0, layer.StartSeconds);
            var effectiveEnd = layer.EndSeconds > effectiveStart ? Math.Min(itemDur, layer.EndSeconds) : itemDur;
            if (cycleTime < effectiveStart + delay || cycleTime > effectiveEnd) { el.Opacity = 0; return; }
            a = Math.Clamp((cycleTime - effectiveStart - delay) / Math.Max(.05, layer.AnimationInSeconds), 0, 1);
            b = Math.Clamp((effectiveEnd - cycleTime) / Math.Max(.05, layer.AnimationOutSeconds), 0, 1);
        }
        else
        {
            if (Playhead < layer.StartSeconds + delay || Playhead > layer.EndSeconds) { el.Opacity = 0; return; }
            a = Math.Clamp((Playhead - layer.StartSeconds - delay) / Math.Max(.05, layer.AnimationInSeconds), 0, 1);
            b = Math.Clamp((layer.EndSeconds - Playhead) / Math.Max(.05, layer.AnimationOutSeconds), 0, 1);
        }
        var p = Math.Min(a, b);
        var anim = a < b ? layer.AnimationIn : layer.AnimationOut;
        el.Opacity = Math.Clamp(layer.Opacity * (string.Equals(anim, "Fade", StringComparison.OrdinalIgnoreCase) ? p : 1), 0, 1);
        switch ((anim ?? "").ToUpperInvariant())
        {
            case "SLIDE LEFT": x += (1 - p) * 110; break;
            case "SLIDE RIGHT": x -= (1 - p) * 110; break;
            case "SLIDE UP": y += (1 - p) * 70; break;
            case "SLIDE DOWN": y -= (1 - p) * 70; break;
            case "PUSH LEFT": x += (1 - p) * Math.Max(110, w); break;
            case "PUSH RIGHT": x -= (1 - p) * Math.Max(110, w); break;
            case "PUSH UP": y += (1 - p) * Math.Max(70, h); break;
            case "PUSH DOWN": y -= (1 - p) * Math.Max(70, h); break;
            case "SCALE DOWN":
                var csdX = x + w / 2; var csdY = y + h / 2;
                var startScale = layer.TextAnimationScaleStart > 0.01 ? layer.TextAnimationScaleStart : 1.35;
                var endScale = layer.TextAnimationScaleEnd > 0.01 ? layer.TextAnimationScaleEnd : 1.0;
                var scDown = startScale + (endScale - startScale) * p;
                w *= scDown; h *= scDown; x = csdX - w / 2; y = csdY - h / 2;
                break;
            case "SCALE IN":
            case "SCALE UP":
                var csuX = x + w / 2; var csuY = y + h / 2;
                var suStart = layer.TextAnimationScaleStart > 0.01 ? layer.TextAnimationScaleStart : 0.65;
                var suEnd = layer.TextAnimationScaleEnd > 0.01 ? layer.TextAnimationScaleEnd : 1.0;
                var scUp = suStart + (suEnd - suStart) * p;
                w *= scUp; h *= scUp; x = csuX - w / 2; y = csuY - h / 2;
                break;
            case "ZOOM":
            case "SCALE":
            case "POP":
                var cx = x + w / 2; var cy = y + h / 2;
                var sc = (anim ?? "").Equals("Pop", StringComparison.OrdinalIgnoreCase) ? .45 + .55 * p : .72 + .28 * p;
                w *= sc; h *= sc; x = cx - w / 2; y = cy - h / 2;
                break;
            case "WIPE LEFT": w *= p; break;
            case "WIPE RIGHT": var ow = w; w *= p; x += ow - w; break;
            case "BLUR": el.Opacity *= p; break;
        }

        if (layer.AnimationBlurRadius > 0 && p < 1.0)
        {
            var dynamicBlur = layer.AnimationBlurRadius * (1.0 - p);
            if (dynamicBlur > 0.5)
            {
                el.Effect = new System.Windows.Media.Effects.BlurEffect { Radius = dynamicBlur, KernelType = System.Windows.Media.Effects.KernelType.Gaussian };
            }
        }
        if (layer.AnimationGlowRadius > 0 && !string.IsNullOrWhiteSpace(layer.AnimationGlowColor) && p < 1.0)
        {
            el.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = ColorFrom(layer.AnimationGlowColor, Colors.Cyan),
                BlurRadius = layer.AnimationGlowRadius * (1.0 - p * 0.5),
                ShadowDepth = 0,
                Opacity = Math.Clamp(p, 0, 1)
            };
        }
        if (string.Equals(layer.Type, "Ticker", StringComparison.OrdinalIgnoreCase))
        {
            var sx = (PreviewCanvas.Width > 1 ? PreviewCanvas.Width : 960.0) / Math.Max(1.0, SelectedProject?.Width > 0 ? SelectedProject.Width : 1920.0);
            var rectLeft = layer.X * sx;
            var rectWidth = (layer.Width > 0 ? layer.Width : (SelectedProject?.Width > 0 ? SelectedProject.Width : 1920.0)) * sx;
            var rectRight = rectLeft + rectWidth;

            if (string.Equals(layer.TickerMode, "Push", StringComparison.OrdinalIgnoreCase))
            {
                x = rectLeft;
                el.Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(1, rectWidth), Math.Max(1, h)));
            }
            else
            {
                var elapsed = Math.Max(0, ContinuousPlaybackSeconds - layer.StartSeconds);
                var speed = Math.Max(20, layer.Speed) * sx;
                var travelSpan = rectWidth + Math.Max(w, 400 * sx);
                var distance = (elapsed * speed) % travelSpan;
                var dir = (layer.TickerDirection ?? "Left").ToUpperInvariant();
                if (dir == "RIGHT")
                    x = rectLeft - w + distance;
                else
                    x = rectRight - distance;

                // Clip ticker preview within layer bounds so it never overlaps fixed category badges or adjacent graphics
                var clipX = Math.Max(0, rectLeft - x);
                var clipW = Math.Max(0, Math.Min(w - clipX, (rectRight - x) - clipX));
                el.Clip = new RectangleGeometry(new Rect(clipX, 0, Math.Max(0, clipW), h));
            }
        }
        if (string.Equals(layer.Type, "Roll", StringComparison.OrdinalIgnoreCase))
        {
            var cHeight = SelectedProject?.Height > 0 ? SelectedProject.Height : 1080.0;
            var travelSpan = cHeight + Math.Max(h, 600);
            var distance = ((Playhead - layer.StartSeconds) * Math.Max(10, layer.Speed)) % travelSpan;
            var dir = (layer.TickerDirection ?? "Up").ToUpperInvariant();
            if (dir == "DOWN")
                y = -h + distance;
            else
                y = cHeight - distance;
        }
    }

    private void RenderTimeline()
    {
        if (!IsLoaded || TimelineCanvas is null || TimelineRulerCanvas is null) return;
        TimelineCanvas.Children.Clear();
        TimelineRulerCanvas.Children.Clear();
        TimelineHeadersCanvas?.Children.Clear();
        var project = SelectedProject;
        if (project is null) return;

        var width = TimelineContentWidth();
        TimelineCanvas.Width = width;
        TimelineRulerCanvas.Width = width;
        const double row = 28;
        const double subRow = 20;
        var trackWidth = TimelineTrackWidth();
        var totalHeight = Math.Max(146, Math.Max(TimelineLayerScroll?.ViewportHeight ?? 0, Layers.Sum(l => row + (_expandedTimelineLayerIds.Contains(l.Id) ? 5 * subRow : 0)) + 16));
        TimelineCanvas.Height = totalHeight;
        if (TimelineHeadersCanvas is not null) TimelineHeadersCanvas.Height = totalHeight;

        // Ruler time ticks and labels
        var major = TimelineMajorTickSeconds();
        for (var sec = 0.0; sec <= ProjectDuration + .0001; sec += major)
        {
            var tx = TimelineTimeToX(sec);
            TimelineCanvas.Children.Add(new Line { X1 = tx, X2 = tx, Y1 = 0, Y2 = TimelineCanvas.Height, Stroke = new SolidColorBrush(Color.FromArgb(38, 130, 145, 156)), StrokeThickness = 1 });
            TimelineRulerCanvas.Children.Add(new Line { X1 = tx, X2 = tx, Y1 = 15, Y2 = 24, Stroke = new SolidColorBrush(Color.FromRgb(65, 77, 86)), StrokeThickness = 1 });
            var tickText = major < 1.0 ? $"{sec:0.0#}s" : TimeSpan.FromSeconds(sec).ToString(@"m\:ss");
            var label = new TextBlock { Text = tickText, Foreground = new SolidColorBrush(Color.FromRgb(121, 134, 144)), FontSize = 8, IsHitTestVisible = false };
            Canvas.SetLeft(label, tx + 3); Canvas.SetTop(label, 2); TimelineRulerCanvas.Children.Add(label);
        }
        TimelineRulerCanvas.Children.Add(new Line { X1 = 0, X2 = width - TimelineRightPad, Y1 = 23, Y2 = 23, Stroke = new SolidColorBrush(Color.FromRgb(48, 58, 66)), StrokeThickness = 1, IsHitTestVisible = false });

        // Render timeline pause/hold events
        if (project.TimelineEvents != null)
        {
            foreach (var evt in project.TimelineEvents.Where(e => e.Enabled))
            {
                var ex = TimelineTimeToX(evt.TimeSeconds);
                TimelineCanvas.Children.Add(new Line
                {
                    X1 = ex, X2 = ex, Y1 = 0, Y2 = TimelineCanvas.Height,
                    Stroke = new SolidColorBrush(Color.FromArgb(220, 245, 158, 11)),
                    StrokeThickness = 1.5,
                    StrokeDashArray = new DoubleCollection { 4, 3 },
                    IsHitTestVisible = false
                });

                var badge = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(245, 158, 11)),
                    CornerRadius = new CornerRadius(2),
                    Padding = new Thickness(3, 1, 3, 1),
                    Tag = evt,
                    Cursor = Cursors.SizeWE,
                    ToolTip = $"{evt.Type} @ {evt.TimeSeconds:0.##}s: {evt.Label} ({evt.ResumeTrigger})\nDrag to move anywhere • Right-click to edit/remove"
                };
                badge.Child = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(evt.Label) ? "⏸ PAUSE" : $"⏸ {evt.Label}",
                    FontSize = 8,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.Black,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(badge, Math.Max(0, ex - 10));
                Canvas.SetTop(badge, 2);
                TimelineRulerCanvas.Children.Add(badge);
            }
        }

        var currentY = 4.0;
        for (var visualRow = 0; visualRow < Layers.Count; visualRow++)
        {
            var i = Layers.Count - 1 - visualRow;
            var l = Layers[i];
            var y = currentY;
            var isExpanded = _expandedTimelineLayerIds.Contains(l.Id);
            var timelineSelected = IsLayerSelected(l) || ReferenceEquals(l, SelectedLayer);

            // 1. Pinned header in TimelineHeadersCanvas (fixed left column, vertically scrollable with rows)
            if (TimelineHeadersCanvas is not null)
            {
                var rowHeader = new Rectangle
                {
                    Width = 135, Height = row - 2,
                    Fill = timelineSelected ? new SolidColorBrush(Color.FromArgb(50, 139, 61, 255)) : new SolidColorBrush(Color.FromArgb(14, 255, 255, 255)),
                    Tag = new TimelineLayerHit { Layer = l, Mode = "row" }, Cursor = Cursors.Hand,
                    ToolTip = $"{l.Name} ({l.Type}) · Click to select · Ctrl/Shift for multi-select · Double-click to expand"
                };
                Canvas.SetLeft(rowHeader, 0); Canvas.SetTop(rowHeader, y - 2);
                TimelineHeadersCanvas.Children.Add(rowHeader);

                // Eye icon for layer visibility toggle
                var eyeIcon = new TextBlock
                {
                    Text = l.Visible ? "👁" : "👁‍🗨",
                    Foreground = l.Visible ? new SolidColorBrush(Color.FromRgb(130, 210, 255)) : new SolidColorBrush(Color.FromArgb(80, 130, 140, 155)),
                    FontSize = 10,
                    Cursor = Cursors.Hand,
                    Tag = new TimelineLayerHit { Layer = l, Mode = "visibility" },
                    ToolTip = l.Visible ? "Hide layer (click to toggle)" : "Show layer (click to toggle)",
                    Opacity = l.Visible ? 1.0 : 0.4
                };
                Canvas.SetLeft(eyeIcon, 120); Canvas.SetTop(eyeIcon, y + 5);
                TimelineHeadersCanvas.Children.Add(eyeIcon);

                var twirl = new TextBlock
                {
                    Text = isExpanded ? "▼" : "▶",
                    Foreground = timelineSelected ? Brushes.White : new SolidColorBrush(Color.FromRgb(155, 170, 185)),
                    FontSize = 8.5,
                    Cursor = Cursors.Hand,
                    Tag = new TimelineLayerHit { Layer = l, Mode = "twirl" },
                    ToolTip = isExpanded ? "Collapse layer properties" : "Expand layer properties (After Effects style)"
                };
                Canvas.SetLeft(twirl, 4); Canvas.SetTop(twirl, y + 5);
                TimelineHeadersCanvas.Children.Add(twirl);

                var headerText = new TextBlock
                {
                    Text = $"{i + 1}. {l.Name}",
                    Foreground = timelineSelected ? Brushes.White : new SolidColorBrush(Color.FromRgb(188, 198, 208)),
                    FontWeight = timelineSelected ? FontWeights.SemiBold : FontWeights.Normal,
                    FontSize = 9.5, Width = 98, TextTrimming = TextTrimming.CharacterEllipsis,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(headerText, 18); Canvas.SetTop(headerText, y + 5);
                TimelineHeadersCanvas.Children.Add(headerText);
            }

            // 2. Interactive track in TimelineCanvas (horizontally zooming duration canvas)
            var rowHit = new Rectangle
            {
                Width = Math.Max(1, width - TimelineRightPad), Height = row - 2,
                Fill = timelineSelected ? new SolidColorBrush(Color.FromArgb(34, 139, 61, 255)) : Brushes.Transparent,
                Tag = new TimelineLayerHit { Layer = l, Mode = "row" }, Cursor = Cursors.Arrow,
                ToolTip = $"{l.Name} · Ctrl-click toggles this layer · Shift-click selects a range"
            };
            Canvas.SetLeft(rowHit, 0); Canvas.SetTop(rowHit, y - 2); TimelineCanvas.Children.Add(rowHit);

            var x = TimelineTimeToX(Math.Max(0, l.StartSeconds));
            var w = Math.Max(8, ((Math.Max(l.StartSeconds, l.EndSeconds) - Math.Max(0, l.StartSeconds)) / ProjectDuration) * trackWidth);
            var r = new Rectangle
            {
                Width = w, Height = 18,
                Fill = timelineSelected ? new SolidColorBrush(Color.FromRgb(139,61,255)) : new SolidColorBrush(Color.FromRgb(77,36,118)),
                Stroke = timelineSelected ? new SolidColorBrush(Color.FromRgb(255,62,165)) : new SolidColorBrush(Color.FromRgb(106,67,132)),
                StrokeThickness = timelineSelected ? 1.5 : 1, RadiusX = 2, RadiusY = 2, Tag = l,
                Cursor = Cursors.SizeAll, ToolTip = $"{l.Name} · {l.StartSeconds:0.00}s → {l.EndSeconds:0.00}s · Ctrl/Shift-click to multi-select"
            };
            Canvas.SetLeft(r, x); Canvas.SetTop(r, y + 2); TimelineCanvas.Children.Add(r);

            // Inactive lead-in indicator for layers starting after 0s (e.g. text waiting for burst overlay wipe)
            if (l.StartSeconds > 0.05)
            {
                var leadX = TimelineTimeToX(0);
                var leadW = Math.Max(2, x - leadX);
                var leadBar = new Rectangle
                {
                    Width = leadW, Height = 18,
                    Fill = new SolidColorBrush(Color.FromArgb(14, 255, 255, 255)),
                    Stroke = new SolidColorBrush(Color.FromArgb(45, 180, 195, 210)),
                    StrokeThickness = 1, StrokeDashArray = [3, 2],
                    RadiusX = 2, RadiusY = 2, IsHitTestVisible = false,
                    ToolTip = $"{l.Name} · Inactive lead-in · Starts at {l.StartSeconds:0.00}s"
                };
                Canvas.SetLeft(leadBar, leadX); Canvas.SetTop(leadBar, y + 2);
                TimelineCanvas.Children.Add(leadBar);

                if (leadW >= 42)
                {
                    var leadLbl = new TextBlock
                    {
                        Text = $"STARTS {l.StartSeconds:0.00}s",
                        Foreground = new SolidColorBrush(Color.FromRgb(140, 160, 180)),
                        FontSize = 7.5, FontWeight = FontWeights.SemiBold,
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(leadLbl, leadX + 4); Canvas.SetTop(leadLbl, y + 5);
                    TimelineCanvas.Children.Add(leadLbl);
                }
            }

            // Inactive lead-out indicator for layers finishing before project duration (e.g. burst overlay wipe ending early)
            var trackEnd = TimelineTimeToX(ProjectDuration);
            var barEnd = x + w;
            if (trackEnd > barEnd + 4 && l.EndSeconds < ProjectDuration - 0.05)
            {
                var tailW = trackEnd - barEnd;
                var tailBar = new Rectangle
                {
                    Width = tailW, Height = 18,
                    Fill = new SolidColorBrush(Color.FromArgb(10, 255, 255, 255)),
                    Stroke = new SolidColorBrush(Color.FromArgb(35, 180, 195, 210)),
                    StrokeThickness = 1, StrokeDashArray = [3, 2],
                    RadiusX = 2, RadiusY = 2, IsHitTestVisible = false,
                    ToolTip = $"{l.Name} · Finished · Inactive from {l.EndSeconds:0.00}s to {ProjectDuration:0.00}s"
                };
                Canvas.SetLeft(tailBar, barEnd); Canvas.SetTop(tailBar, y + 2);
                TimelineCanvas.Children.Add(tailBar);

                if (tailW >= 42)
                {
                    var tailLbl = new TextBlock
                    {
                        Text = $"ENDS {l.EndSeconds:0.00}s",
                        Foreground = new SolidColorBrush(Color.FromRgb(140, 160, 180)),
                        FontSize = 7.5, FontWeight = FontWeights.SemiBold,
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(tailLbl, barEnd + 4); Canvas.SetTop(tailLbl, y + 5);
                    TimelineCanvas.Children.Add(tailLbl);
                }
            }

            // Visual brackets & zones: Burst Delay, IN Animation, OUT Animation
            var delayW = (l.TextAnimationDelaySeconds > 0) ? Math.Min(w, (l.TextAnimationDelaySeconds / ProjectDuration) * trackWidth) : 0;
            if (delayW > 3)
            {
                var delayBar = new Rectangle
                {
                    Width = delayW, Height = 18,
                    Fill = new SolidColorBrush(Color.FromArgb(90, 245, 158, 11)),
                    Stroke = new SolidColorBrush(Color.FromArgb(160, 245, 158, 11)),
                    StrokeThickness = 1, RadiusX = 2, RadiusY = 2,
                    Tag = new TimelineLayerHit { Layer = l, Mode = "animWait" },
                    Cursor = Cursors.SizeWE,
                    ToolTip = $"Wait / Delay: {l.TextAnimationDelaySeconds:0.##}s (drag handle to adjust)"
                };
                Canvas.SetLeft(delayBar, x); Canvas.SetTop(delayBar, y + 2);
                TimelineCanvas.Children.Add(delayBar);

                // Draggable handle at the right edge of wait delay zone
                var waitHandle = new Rectangle
                {
                    Width = 6, Height = 18,
                    Fill = new SolidColorBrush(Color.FromArgb(220, 245, 158, 11)),
                    Tag = new TimelineLayerHit { Layer = l, Mode = "animWait" },
                    Cursor = Cursors.SizeWE,
                    ToolTip = $"Drag to change WAIT delay ({l.TextAnimationDelaySeconds:0.##}s)"
                };
                Canvas.SetLeft(waitHandle, x + delayW - 3); Canvas.SetTop(waitHandle, y + 2);
                TimelineCanvas.Children.Add(waitHandle);

                if (delayW >= 42 && w >= 110)
                {
                    var delayLbl = new TextBlock
                    {
                        Text = $"WAIT {l.TextAnimationDelaySeconds:0.##}s",
                        Foreground = new SolidColorBrush(Color.FromRgb(254, 243, 199)),
                        FontSize = 7.5, FontWeight = FontWeights.Bold,
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(delayLbl, x + 3); Canvas.SetTop(delayLbl, y + 5);
                    TimelineCanvas.Children.Add(delayLbl);
                }
            }

            var inStartX = x + delayW;
            var inW = (l.AnimationInSeconds > 0) ? Math.Min(w - delayW, (l.AnimationInSeconds / ProjectDuration) * trackWidth) : 0;
            if (inW > 3)
            {
                var inBar = new Rectangle
                {
                    Width = inW, Height = 18,
                    Fill = new SolidColorBrush(Color.FromArgb(80, 0, 229, 255)),
                    Stroke = new SolidColorBrush(Color.FromArgb(190, 0, 229, 255)),
                    StrokeThickness = 1, RadiusX = 2, RadiusY = 2,
                    IsHitTestVisible = false,
                    ToolTip = $"IN Transition: {l.AnimationIn} ({l.AnimationInSeconds:0.##}s)"
                };
                Canvas.SetLeft(inBar, inStartX); Canvas.SetTop(inBar, y + 2);
                TimelineCanvas.Children.Add(inBar);

                // Draggable handle at the right edge of IN animation zone
                var inHandle = new Rectangle
                {
                    Width = 6, Height = 18,
                    Fill = new SolidColorBrush(Color.FromArgb(220, 0, 229, 255)),
                    Tag = new TimelineLayerHit { Layer = l, Mode = "animIn" },
                    Cursor = Cursors.SizeWE,
                    ToolTip = $"Drag to change IN duration ({l.AnimationInSeconds:0.##}s)"
                };
                Canvas.SetLeft(inHandle, inStartX + inW - 3); Canvas.SetTop(inHandle, y + 2);
                TimelineCanvas.Children.Add(inHandle);

                var inMarker = new Line
                {
                    X1 = inStartX + inW, X2 = inStartX + inW, Y1 = y + 2, Y2 = y + 20,
                    Stroke = new SolidColorBrush(Color.FromRgb(0, 229, 255)), StrokeThickness = 1.5,
                    IsHitTestVisible = false
                };
                TimelineCanvas.Children.Add(inMarker);

                if (inW >= 40 && w >= 110)
                {
                    var inLbl = new TextBlock
                    {
                        Text = $"IN {l.AnimationInSeconds:0.##}s",
                        Foreground = new SolidColorBrush(Color.FromRgb(207, 250, 254)),
                        FontSize = 7.5, FontWeight = FontWeights.Bold,
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(inLbl, inStartX + 3); Canvas.SetTop(inLbl, y + 5);
                    TimelineCanvas.Children.Add(inLbl);
                }
            }

            var outW = (l.AnimationOutSeconds > 0) ? Math.Min(w, (l.AnimationOutSeconds / ProjectDuration) * trackWidth) : 0;
            var outStartX = x + w - outW;
            if (outW > 3 && outStartX >= inStartX + inW)
            {
                var outBar = new Rectangle
                {
                    Width = outW, Height = 18,
                    Fill = new SolidColorBrush(Color.FromArgb(80, 244, 63, 94)),
                    Stroke = new SolidColorBrush(Color.FromArgb(190, 244, 63, 94)),
                    StrokeThickness = 1, RadiusX = 2, RadiusY = 2,
                    IsHitTestVisible = false,
                    ToolTip = $"OUT Transition: {l.AnimationOut} ({l.AnimationOutSeconds:0.##}s)"
                };
                Canvas.SetLeft(outBar, outStartX); Canvas.SetTop(outBar, y + 2);
                TimelineCanvas.Children.Add(outBar);

                // Draggable handle at the left edge of OUT animation zone
                var outHandle = new Rectangle
                {
                    Width = 6, Height = 18,
                    Fill = new SolidColorBrush(Color.FromArgb(220, 244, 63, 94)),
                    Tag = new TimelineLayerHit { Layer = l, Mode = "animOut" },
                    Cursor = Cursors.SizeWE,
                    ToolTip = $"Drag to change OUT duration ({l.AnimationOutSeconds:0.##}s)"
                };
                Canvas.SetLeft(outHandle, outStartX - 3); Canvas.SetTop(outHandle, y + 2);
                TimelineCanvas.Children.Add(outHandle);

                var outMarker = new Line
                {
                    X1 = outStartX, X2 = outStartX, Y1 = y + 2, Y2 = y + 20,
                    Stroke = new SolidColorBrush(Color.FromRgb(244, 63, 94)), StrokeThickness = 1.5,
                    IsHitTestVisible = false
                };
                TimelineCanvas.Children.Add(outMarker);

                if (outW >= 40 && w >= 110)
                {
                    var outLbl = new TextBlock
                    {
                        Text = $"OUT {l.AnimationOutSeconds:0.##}s",
                        Foreground = new SolidColorBrush(Color.FromRgb(255, 228, 230)),
                        FontSize = 7.5, FontWeight = FontWeights.Bold,
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(outLbl, outStartX + 3); Canvas.SetTop(outLbl, y + 5);
                    TimelineCanvas.Children.Add(outLbl);
                }
            }

            if (w >= 24)
            {
                var labelLeft = (inW > 0 && w >= 95) ? (inStartX + inW + 6) : (delayW > 0 && w >= 95 ? (x + delayW + 6) : (x + 8));
                var labelRight = (outW > 0 && w >= 95) ? (outStartX - 6) : (x + w - 8);
                var labelAvailW = Math.Max(16, labelRight - labelLeft);

                if (labelAvailW >= 20 || w < 95)
                {
                    var label = new TextBlock
                    {
                        Text = $"{l.Name} ({l.DurationSeconds:0.0}s)",
                        Foreground = Brushes.White,
                        FontSize = 9.0,
                        FontWeight = FontWeights.SemiBold,
                        Width = Math.Max(16, labelAvailW),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(label, labelLeft);
                    Canvas.SetTop(label, y + 4);
                    TimelineCanvas.Children.Add(label);
                }
            }

            var leftHandle = new Rectangle { Width = 6, Height = 18, Fill = Brushes.White, Opacity = .86, Tag = new TimelineLayerHit { Layer = l, Mode = "start" }, Cursor = Cursors.SizeWE, ToolTip = "Drag to trim layer start" };
            Canvas.SetLeft(leftHandle, x); Canvas.SetTop(leftHandle, y + 2); TimelineCanvas.Children.Add(leftHandle);

            var rightHandle = new Rectangle { Width = 6, Height = 18, Fill = Brushes.White, Opacity = .86, Tag = new TimelineLayerHit { Layer = l, Mode = "end" }, Cursor = Cursors.SizeWE, ToolTip = "Drag to trim layer end" };
            Canvas.SetLeft(rightHandle, x + Math.Max(0, w - 6)); Canvas.SetTop(rightHandle, y + 2); TimelineCanvas.Children.Add(rightHandle);

            foreach (var k in l.Keyframes ?? [])
            {
                var kx = TimelineTimeToX(k.TimeSeconds);
                var diamond = new Polygon { Points = new PointCollection { new Point(0,5), new Point(5,0), new Point(10,5), new Point(5,10) }, Fill = l == SelectedLayer ? Brushes.Gold : Brushes.SlateGray, Stroke = Brushes.Black, StrokeThickness = .5, ToolTip = $"{k.TimeSeconds:0.000}s · {k.Ease}", Tag = new TimelineKeyframeHit { Layer = l, Keyframe = k }, Cursor = Cursors.SizeWE };
                Canvas.SetLeft(diamond, kx - 5); Canvas.SetTop(diamond, y + 6); TimelineCanvas.Children.Add(diamond);
            }

            currentY += row;

            // Expanded AE-style property tracks
            if (isExpanded)
            {
                string[] subProps = ["Position", "Scale", "Rotation", "Opacity", "Effects"];
                string[] subLabels = ["  ↳ Position", "  ↳ Scale", "  ↳ Rotation", "  ↳ Opacity", "  ↳ Effects"];
                for (var s = 0; s < subProps.Length; s++)
                {
                    var subY = currentY;
                    var propName = subProps[s];
                    if (TimelineHeadersCanvas is not null)
                    {
                        var subHeader = new Rectangle
                        {
                            Width = 135, Height = subRow - 1,
                            Fill = new SolidColorBrush(Color.FromArgb(20, 15, 20, 28)),
                            Tag = new TimelineLayerHit { Layer = l, Mode = "property", Property = propName },
                            Cursor = Cursors.Hand
                        };
                        Canvas.SetLeft(subHeader, 0); Canvas.SetTop(subHeader, subY);
                        TimelineHeadersCanvas.Children.Add(subHeader);

                        var subText = new TextBlock
                        {
                            Text = subLabels[s],
                            Foreground = new SolidColorBrush(Color.FromRgb(150, 165, 180)),
                            FontSize = 8.5,
                            Width = 98,
                            IsHitTestVisible = false
                        };
                        Canvas.SetLeft(subText, 6); Canvas.SetTop(subText, subY + 3);
                        TimelineHeadersCanvas.Children.Add(subText);

                        var hasKeyAtPlayhead = (l.Keyframes ?? []).Any(k => Math.Abs(k.TimeSeconds - Playhead) <= 0.04 &&
                            (string.IsNullOrEmpty(k.TargetProperty) || k.TargetProperty.Equals("All", StringComparison.OrdinalIgnoreCase) || k.TargetProperty.Contains(propName, StringComparison.OrdinalIgnoreCase)));

                        var kfBtn = new TextBlock
                        {
                            Text = hasKeyAtPlayhead ? "◆" : "◇",
                            Foreground = hasKeyAtPlayhead ? Brushes.Gold : new SolidColorBrush(Color.FromRgb(130, 145, 160)),
                            FontSize = 9.5,
                            FontWeight = FontWeights.Bold,
                            Cursor = Cursors.Hand,
                            ToolTip = hasKeyAtPlayhead ? $"Remove {propName} keyframe at {Playhead:0.00}s" : $"Add {propName} keyframe at {Playhead:0.00}s",
                            Tag = new TimelineLayerHit { Layer = l, Mode = "toggle_prop_keyframe", Property = propName }
                        };
                        Canvas.SetLeft(kfBtn, 116); Canvas.SetTop(kfBtn, subY + 2);
                        TimelineHeadersCanvas.Children.Add(kfBtn);
                    }

                    var subTrack = new Rectangle
                    {
                        Width = Math.Max(1, width - TimelineRightPad), Height = subRow - 1,
                        Fill = new SolidColorBrush(Color.FromArgb(14, 18, 24, 34)),
                        Tag = new TimelineLayerHit { Layer = l, Mode = "property", Property = propName }
                    };
                    Canvas.SetLeft(subTrack, 0); Canvas.SetTop(subTrack, subY);
                    TimelineCanvas.Children.Add(subTrack);

                    TimelineCanvas.Children.Add(new Line
                    {
                        X1 = 0, X2 = width - TimelineRightPad,
                        Y1 = subY + subRow - 1, Y2 = subY + subRow - 1,
                        Stroke = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)),
                        StrokeThickness = 0.5
                    });

                    foreach (var k in l.Keyframes ?? [])
                    {
                        if (!string.IsNullOrEmpty(k.TargetProperty) && !k.TargetProperty.Equals("All", StringComparison.OrdinalIgnoreCase) && !k.TargetProperty.Contains(propName, StringComparison.OrdinalIgnoreCase))
                            continue;

                        var kx = TimelineTimeToX(k.TimeSeconds);
                        var propDiamond = new Polygon
                        {
                            Points = new PointCollection { new Point(0, 4), new Point(4, 0), new Point(8, 4), new Point(4, 8) },
                            Fill = l == SelectedLayer ? Brushes.Goldenrod : Brushes.CornflowerBlue,
                            Stroke = Brushes.Black, StrokeThickness = 0.5,
                            ToolTip = $"{propName} @ {k.TimeSeconds:0.000}s ({k.Ease})",
                            Tag = new TimelineKeyframeHit { Layer = l, Keyframe = k, Property = propName },
                            Cursor = Cursors.SizeWE
                        };
                        Canvas.SetLeft(propDiamond, kx - 4); Canvas.SetTop(propDiamond, subY + 6);
                        TimelineCanvas.Children.Add(propDiamond);
                    }

                    currentY += subRow;
                }
            }
        }

        for (var emptyY = currentY; emptyY < totalHeight; emptyY += row)
        {
            if (TimelineHeadersCanvas is not null)
            {
                var emptyHeader = new Rectangle
                {
                    Width = 135, Height = row - 2,
                    Fill = new SolidColorBrush(Color.FromArgb(8, 255, 255, 255)),
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(emptyHeader, 0); Canvas.SetTop(emptyHeader, emptyY - 2);
                TimelineHeadersCanvas.Children.Add(emptyHeader);
            }
            var emptyTrack = new Rectangle
            {
                Width = Math.Max(1, width - TimelineRightPad), Height = row - 2,
                Fill = new SolidColorBrush(Color.FromArgb(4, 255, 255, 255)),
                IsHitTestVisible = false
            };
            Canvas.SetLeft(emptyTrack, 0); Canvas.SetTop(emptyTrack, emptyY - 2);
            TimelineCanvas.Children.Add(emptyTrack);
        }

        // Timeline lifecycle events are operational markers, so render them above
        // every layer row and on the ruler instead of keeping PAUSE/HOLD invisible.
        foreach (var timelineEvent in (project.TimelineEvents ?? []).Where(x => x.Enabled))
        {
            var eventX = TimelineTimeToX(Math.Clamp(timelineEvent.TimeSeconds, 0, ProjectDuration));
            var eventBrush = new SolidColorBrush(Color.FromRgb(245, 158, 11));
            TimelineCanvas.Children.Add(new Line
            {
                X1 = eventX, X2 = eventX, Y1 = 0, Y2 = TimelineCanvas.Height,
                Stroke = eventBrush, StrokeThickness = 1.5, StrokeDashArray = [3, 2], IsHitTestVisible = false
            });
            var eventLabel = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(225, 120, 53, 15)),
                BorderBrush = eventBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2),
                Padding = new Thickness(4, 1, 4, 1),
                Child = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(timelineEvent.Label) ? timelineEvent.Type.ToUpperInvariant() : timelineEvent.Label,
                    Foreground = Brushes.White, FontSize = 7.5, FontWeight = FontWeights.Bold
                },
                ToolTip = $"{timelineEvent.Type} @ {timelineEvent.TimeSeconds:0.00}s · resumes on {timelineEvent.ResumeTrigger}"
            };
            Canvas.SetLeft(eventLabel, Math.Max(0, eventX - 4)); Canvas.SetTop(eventLabel, 1);
            TimelineRulerCanvas.Children.Add(eventLabel);
        }

        var ph = TimelineTimeToX(Playhead);
        var hit = new Rectangle { Width = 12, Height = TimelineCanvas.Height, Fill = Brushes.Transparent, Cursor = Cursors.SizeWE, Tag = "PLAYHEAD" };
        Canvas.SetLeft(hit, ph - 6); Canvas.SetTop(hit, 0); TimelineCanvas.Children.Add(hit);
        TimelineCanvas.Children.Add(new Line { X1 = ph, X2 = ph, Y1 = 0, Y2 = TimelineCanvas.Height, Stroke = Brushes.Red, StrokeThickness = 1.8, IsHitTestVisible = false });

        var rulerHit = new Rectangle { Width = 14, Height = 24, Fill = Brushes.Transparent, Cursor = Cursors.SizeWE, Tag = "PLAYHEAD" };
        Canvas.SetLeft(rulerHit, ph - 7); TimelineRulerCanvas.Children.Add(rulerHit);
        var head = new Polygon { Points = new PointCollection { new Point(0,0), new Point(12,0), new Point(6,8) }, Fill = Brushes.Red, IsHitTestVisible = false };
        Canvas.SetLeft(head, ph - 6); Canvas.SetTop(head, 0); TimelineRulerCanvas.Children.Add(head);
    }

    private static double FiniteUnit(double value, double fallback = 0) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : Math.Clamp(fallback, 0, 1);

    private static void SanitizeProjectForEditor(CgProject? project)
    {
        if (project is null) return;
        CgProjectSanitizer.Sanitize(project);
        if (!double.IsFinite(project.FrameRate) || project.FrameRate < 1 || project.FrameRate > 240) project.FrameRate = 25;
        if (!double.IsFinite(project.DurationSeconds)) project.DurationSeconds = 10;
        project.Width = Math.Clamp(project.Width <= 0 ? 1920 : project.Width, 16, 16384);
        project.Height = Math.Clamp(project.Height <= 0 ? 1080 : project.Height, 16, 16384);

        // Auto-compute a sensible DurationSeconds for ticker projects whose
        // DurationSeconds was set to an arbitrary large value (e.g. 3600).
        // The compositor lifecycle manages ticker playback via 3-phase
        // scheduling (pausePoint + tickerCycleDuration + outAnimation), so
        // the project DurationSeconds only needs to cover the non-ticker
        // layers.  A bloated value causes the editor playback to "freeze"
        // for minutes while the playhead slowly advances.
        var hasTickerWithData = (project.Layers ?? []).Any(l => l.Visible &&
            string.Equals(l.Type, "Ticker", StringComparison.OrdinalIgnoreCase));
        if (hasTickerWithData)
        {
            var tickerDuration = CgDataSourceService.ResolveEffectiveTickerCycleDuration(project);
            if (tickerDuration > 0.5)
            {
                // Compute a compact DurationSeconds that wraps in+hold+out.
                var maxOut = (project.Layers ?? []).Where(x => x.Visible)
                    .Select(x => x.AnimationOutSeconds).DefaultIfEmpty(0.5).Max();
                var holdPoint = CgDataSourceService.ResolveEffectiveHoldPoint(project);
                var compactDuration = Math.Max(2.0, holdPoint + maxOut + 0.5);
                if (project.DurationSeconds > compactDuration * 3)
                {
                    project.DurationSeconds = Math.Max(2.0, compactDuration);
                    // Re-clamp layer EndSeconds so they don't exceed the new duration
                    foreach (var l in project.Layers ?? [])
                        if (l.EndSeconds > project.DurationSeconds) l.EndSeconds = project.DurationSeconds;
                }
            }
            else if (project.DurationSeconds > 120)
            {
                // Data not loaded yet but DurationSeconds is excessively large;
                // clamp to a reasonable preview duration so editor doesn't stall.
                var lastEnd = (project.Layers ?? []).Where(l => l.Visible)
                    .Select(l => l.EndSeconds).DefaultIfEmpty(10).Max();
                var reduced = Math.Clamp(lastEnd, 5.0, 60.0);
                project.DurationSeconds = reduced;
                foreach (var l in project.Layers ?? [])
                    if (l.EndSeconds > project.DurationSeconds) l.EndSeconds = project.DurationSeconds;
            }
        }

        foreach (var layer in project.Layers ?? [])
        {
            if (!double.IsFinite(layer.X)) layer.X = 0; if (!double.IsFinite(layer.Y)) layer.Y = 0; if (!double.IsFinite(layer.Z)) layer.Z = 0;
            if (!double.IsFinite(layer.Width) || layer.Width <= 0) layer.Width = 100; if (!double.IsFinite(layer.Height) || layer.Height <= 0) layer.Height = 100;
            if (!double.IsFinite(layer.RotationX)) layer.RotationX = 0; if (!double.IsFinite(layer.RotationY)) layer.RotationY = 0; if (!double.IsFinite(layer.Rotation)) layer.Rotation = 0;
            if (!double.IsFinite(layer.ScaleX) || Math.Abs(layer.ScaleX) < .000001) layer.ScaleX = 1; if (!double.IsFinite(layer.ScaleY) || Math.Abs(layer.ScaleY) < .000001) layer.ScaleY = 1; if (!double.IsFinite(layer.ScaleZ) || Math.Abs(layer.ScaleZ) < .000001) layer.ScaleZ = 1;
            if (!double.IsFinite(layer.AnchorX)) layer.AnchorX = .5; if (!double.IsFinite(layer.AnchorY)) layer.AnchorY = .5; if (!double.IsFinite(layer.AnchorZ)) layer.AnchorZ = 0;
            if (!double.IsFinite(layer.Opacity)) layer.Opacity = 1; layer.Opacity = Math.Clamp(layer.Opacity, 0, 1);
            if (!double.IsFinite(layer.StartSeconds)) layer.StartSeconds = 0; if (!double.IsFinite(layer.EndSeconds)) layer.EndSeconds = Math.Max(.5, project.DurationSeconds);
            if (!double.IsFinite(layer.GradientAngle)) layer.GradientAngle = 0;
            if (!double.IsFinite(layer.MaskX)) layer.MaskX = layer.X; if (!double.IsFinite(layer.MaskY)) layer.MaskY = layer.Y;
            if (!double.IsFinite(layer.MaskWidth)) layer.MaskWidth = 0; if (!double.IsFinite(layer.MaskHeight)) layer.MaskHeight = 0;
            if (!double.IsFinite(layer.MaskCornerRadius)) layer.MaskCornerRadius = 0; if (!double.IsFinite(layer.MaskFeather)) layer.MaskFeather = 0;
            var stops = layer.GradientStops ?? [];
            var count = stops.Count;
            for (var i = 0; i < count; i++) stops[i].Offset = FiniteUnit(stops[i].Offset, count <= 1 ? 0 : i / (double)(count - 1));
            foreach (var key in layer.Keyframes ?? [])
            {
                if (!double.IsFinite(key.TimeSeconds)) key.TimeSeconds = 0;
                if (!double.IsFinite(key.X)) key.X = layer.X; if (!double.IsFinite(key.Y)) key.Y = layer.Y; if (!double.IsFinite(key.Z)) key.Z = layer.Z;
                if (!double.IsFinite(key.ScaleX)) key.ScaleX = 1; if (!double.IsFinite(key.ScaleY)) key.ScaleY = 1; if (!double.IsFinite(key.ScaleZ)) key.ScaleZ = 1;
                if (!double.IsFinite(key.RotationX)) key.RotationX = 0; if (!double.IsFinite(key.RotationY)) key.RotationY = 0; if (!double.IsFinite(key.Rotation)) key.Rotation = 0;
                if (!double.IsFinite(key.AnchorX)) key.AnchorX = .5; if (!double.IsFinite(key.AnchorY)) key.AnchorY = .5; if (!double.IsFinite(key.AnchorZ)) key.AnchorZ = 0;
                if (!double.IsFinite(key.Opacity)) key.Opacity = 1;
            }
        }
    }

    private void SelectTimelineLayer(CgLayer layer, ModifierKeys modifiers)
    {
        if (modifiers.HasFlag(ModifierKeys.Shift)) { SelectLayerRange(layer); return; }
        if (modifiers.HasFlag(ModifierKeys.Control)) { ToggleLayerSelection(layer); return; }
        SetLayerSelection([layer], layer, updateAnchor: true);
    }

    private static Brush BrushFrom(string? text,Brush fallback)
    {
        try { return (Brush)new BrushConverter().ConvertFromString(text??"")!; } catch { return fallback; }
    }
    private static Color ColorFrom(string? text, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(text ?? "#FF000000"); } catch { return fallback; }
    }
    private static string FirstSequenceImage(string? source)
    {
        if(string.IsNullOrWhiteSpace(source)||!Directory.Exists(source))return ""; return Directory.EnumerateFiles(source).Where(x=>new[]{".png",".jpg",".jpeg",".bmp"}.Contains(IOPath.GetExtension(x),StringComparer.OrdinalIgnoreCase)).OrderBy(x=>x).FirstOrDefault()??"";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name=null)=>PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(name));

    private void TimelineCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (TimelineCanvas.ActualWidth <= 20) return;
        var point = e.GetPosition(TimelineCanvas);
        var hit = TimelineCanvas.InputHitTest(point) as DependencyObject;
        while (hit is not null && hit is not Rectangle && hit is not Polygon) hit = VisualTreeHelper.GetParent(hit);
        var clickedLayer = hit switch
        {
            Rectangle { Tag: CgLayer directLayer } => directLayer,
            Rectangle { Tag: TimelineLayerHit rowOrHandle } => rowOrHandle.Layer,
            Polygon { Tag: TimelineKeyframeHit keyframeHit } => keyframeHit.Layer,
            _ => null
        };
        if (e.ClickCount >= 2 && clickedLayer is not null && string.Equals(clickedLayer.Type, "Precomp", StringComparison.OrdinalIgnoreCase) && clickedLayer.Precomposition is not null)
        {
            EnterPrecomposition(clickedLayer);
            e.Handled = true;
            return;
        }
        if (hit is Polygon keyDiamond && keyDiamond.Tag is TimelineKeyframeHit keyHit)
        {
            var selectionModifiers = Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift);
            SelectTimelineLayer(keyHit.Layer, selectionModifiers);
            SelectedKeyframe = keyHit.Keyframe;
            if (selectionModifiers != ModifierKeys.None) { e.Handled = true; return; }
            _dragKeyframeProperty = keyHit.Property;
            var targetLayer = keyHit.Layer;
            var targetKey = keyHit.Keyframe;

            if (!string.IsNullOrEmpty(keyHit.Property) && !keyHit.Property.Equals("All", StringComparison.OrdinalIgnoreCase))
            {
                // Property-specific sub-track dragging: isolate this property channel!
                if (string.IsNullOrEmpty(targetKey.TargetProperty) || targetKey.TargetProperty.Equals("All", StringComparison.OrdinalIgnoreCase))
                {
                    string[] allSubProps = ["Position", "Scale", "Rotation", "Opacity", "Effects"];
                    var remainingProps = string.Join(",", allSubProps.Where(p => !p.Equals(keyHit.Property, StringComparison.OrdinalIgnoreCase)));
                    var stationaryKey = CloneKeyframe(targetKey);
                    stationaryKey.TargetProperty = remainingProps;
                    targetLayer.Keyframes ??= [];
                    targetLayer.Keyframes.Add(stationaryKey);

                    targetKey.TargetProperty = keyHit.Property;
                }
                else if (targetKey.TargetProperty.Contains(keyHit.Property, StringComparison.OrdinalIgnoreCase) &&
                         !targetKey.TargetProperty.Equals(keyHit.Property, StringComparison.OrdinalIgnoreCase))
                {
                    var remaining = string.Join(",", targetKey.TargetProperty.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(p => !p.Equals(keyHit.Property, StringComparison.OrdinalIgnoreCase)));
                    targetKey.TargetProperty = remaining;

                    var isolatedKey = CloneKeyframe(targetKey);
                    isolatedKey.TargetProperty = keyHit.Property;
                    targetLayer.Keyframes ??= [];
                    targetLayer.Keyframes.Add(isolatedKey);
                    targetKey = isolatedKey;
                    SelectedKeyframe = isolatedKey;
                }
            }

            RecordUndoSnapshot();
            _dragKeyframe = targetKey; _dragKeyframeLayer = targetLayer; _dragMode = "keyframe"; TimelineCanvas.CaptureMouse(); e.Handled = true; return;
        }
        if (hit is Rectangle edgeHandle && edgeHandle.Tag is TimelineLayerHit layerHit)
        {
            var selectionModifiers = Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift);
            SelectTimelineLayer(layerHit.Layer, selectionModifiers);
            if (string.Equals(layerHit.Mode, "row", StringComparison.OrdinalIgnoreCase) || selectionModifiers != ModifierKeys.None)
            {
                e.Handled = true;
                return;
            }
            // Animation duration drag modes
            if (string.Equals(layerHit.Mode, "animWait", StringComparison.OrdinalIgnoreCase))
            {
                RecordUndoSnapshot();
                _dragAnimationLayer = layerHit.Layer; _dragStartPoint = point;
                _dragAnimationStartValue = layerHit.Layer.TextAnimationDelaySeconds; _dragMode = "animWait";
                TimelineCanvas.CaptureMouse(); e.Handled = true; return;
            }
            if (string.Equals(layerHit.Mode, "animIn", StringComparison.OrdinalIgnoreCase))
            {
                RecordUndoSnapshot();
                _dragAnimationLayer = layerHit.Layer; _dragStartPoint = point;
                _dragAnimationStartValue = layerHit.Layer.AnimationInSeconds; _dragMode = "animIn";
                TimelineCanvas.CaptureMouse(); e.Handled = true; return;
            }
            if (string.Equals(layerHit.Mode, "animOut", StringComparison.OrdinalIgnoreCase))
            {
                RecordUndoSnapshot();
                _dragAnimationLayer = layerHit.Layer; _dragStartPoint = point;
                _dragAnimationStartValue = layerHit.Layer.AnimationOutSeconds; _dragMode = "animOut";
                TimelineCanvas.CaptureMouse(); e.Handled = true; return;
            }
            RecordUndoSnapshot();
            _dragLayer = layerHit.Layer; _dragStartPoint = point;
            _dragStartSeconds = layerHit.Layer.StartSeconds; _dragEndSeconds = layerHit.Layer.EndSeconds; _dragMode = layerHit.Mode;
            TimelineCanvas.CaptureMouse(); e.Handled = true; return;
        }
        if (hit is Rectangle playheadHit && string.Equals(playheadHit.Tag as string, "PLAYHEAD", StringComparison.Ordinal))
        {
            _dragMode = "playhead";
            TimelineCanvas.CaptureMouse();
            e.Handled = true;
            return;
        }
        if (hit is Rectangle rect && rect.Tag is CgLayer layer)
        {
            var selectionModifiers = Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift);
            SelectTimelineLayer(layer, selectionModifiers);
            if (selectionModifiers != ModifierKeys.None) { e.Handled = true; return; }
            _dragLayer = layer;
            _dragStartPoint = point;
            _dragStartSeconds = layer.StartSeconds;
            _dragEndSeconds = layer.EndSeconds;
            var left = Canvas.GetLeft(rect); var local = point.X - left;
            _dragMode = local < 8 ? "start" : local > rect.Width - 8 ? "end" : "move";
            TimelineCanvas.CaptureMouse();
            e.Handled = true;
            return;
        }
        Playhead = TimelineXToTime(point.X);
        _dragMode = "playhead";
        TimelineCanvas.CaptureMouse();
    }

    private double TimelineXToTime(double x)
    {
        return Math.Clamp(x / Math.Max(1, TimelineTrackWidth()) * ProjectDuration, 0, ProjectDuration);
    }

    private void TimelineHeadersCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (TimelineHeadersCanvas is null) return;
        var point = e.GetPosition(TimelineHeadersCanvas);
        var hit = TimelineHeadersCanvas.InputHitTest(point) as DependencyObject;
        while (hit is not null && hit is not Rectangle && hit is not TextBlock) hit = VisualTreeHelper.GetParent(hit);
        if (hit is FrameworkElement { Tag: TimelineLayerHit layerHit })
        {
            // Eye icon: toggle layer visibility
            if (layerHit.Mode == "visibility")
            {
                layerHit.Layer.Visible = !layerHit.Layer.Visible;
                SyncLayersToProject(); _main.SaveCgProjects(); RenderAll();
                e.Handled = true;
                return;
            }
            // Diamond toggle: add or remove property keyframe at playhead
            if (layerHit.Mode == "toggle_prop_keyframe")
            {
                var l = layerHit.Layer;
                var prop = layerHit.Property ?? "All";
                l.Keyframes ??= [];
                var existing = l.Keyframes.FirstOrDefault(k => Math.Abs(k.TimeSeconds - Playhead) <= 0.04 &&
                    (string.IsNullOrEmpty(k.TargetProperty) || k.TargetProperty.Equals("All", StringComparison.OrdinalIgnoreCase) || k.TargetProperty.Contains(prop, StringComparison.OrdinalIgnoreCase)));
                if (existing != null)
                {
                    l.Keyframes.Remove(existing);
                    if (ReferenceEquals(SelectedKeyframe, existing)) SelectedKeyframe = null;
                }
                else
                {
                    var state = CgAnimationEngine.EvaluateLayerLocal(l, Playhead);
                    var kf = KeyframeFromMotion(Playhead, state, l.DefaultEase);
                    kf.TargetProperty = prop;
                    kf.StyleJson = CgAnimationEngine.CaptureStyleJson(l);
                    l.Keyframes.Add(kf);
                    SelectedKeyframe = kf;
                }
                Keyframes.Clear();
                foreach (var k in l.Keyframes.OrderBy(x => x.TimeSeconds)) Keyframes.Add(k);
                SyncLayersToProject(); _main.SaveCgProjects(); RenderAll();
                e.Handled = true;
                return;
            }
            if (layerHit.Mode == "twirl" || (layerHit.Mode == "row" && e.ClickCount >= 2))
            {
                if (_expandedTimelineLayerIds.Contains(layerHit.Layer.Id))
                    _expandedTimelineLayerIds.Remove(layerHit.Layer.Id);
                else
                    _expandedTimelineLayerIds.Add(layerHit.Layer.Id);
                RenderTimeline();
                e.Handled = true;
                return;
            }
            var selectionModifiers = Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift);
            SelectTimelineLayer(layerHit.Layer, selectionModifiers);
            e.Handled = true;
        }
    }

    private void TimelineHeadersCanvas_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (TimelineHeadersCanvas is null) return;
        var point = e.GetPosition(TimelineHeadersCanvas);
        var hit = TimelineHeadersCanvas.InputHitTest(point) as DependencyObject;
        while (hit is not null && hit is not Rectangle) hit = VisualTreeHelper.GetParent(hit);
        if (hit is Rectangle { Tag: TimelineLayerHit layerHit })
        {
            var layer = layerHit.Layer;
            if (!IsLayerSelected(layer)) SetLayerSelection([layer], layer, updateAnchor: true);
            else SetLayerSelection(GetSelectedLayers(), layer);
        }
    }

    private CgLayer? TimelineLayerAt(Point point)
    {
        var hit = TimelineCanvas.InputHitTest(point) as DependencyObject;
        while (hit is not null && hit is not Rectangle && hit is not Polygon) hit = VisualTreeHelper.GetParent(hit);
        return hit switch
        {
            Rectangle { Tag: CgLayer directLayer } => directLayer,
            Rectangle { Tag: TimelineLayerHit rowOrHandle } => rowOrHandle.Layer,
            Polygon { Tag: TimelineKeyframeHit keyframeHit } => keyframeHit.Layer,
            _ => null
        };
    }

    private void TimelineCanvas_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var point = e.GetPosition(TimelineCanvas);
        var hit = TimelineCanvas.InputHitTest(point) as DependencyObject;
        while (hit is not null && hit is not Rectangle && hit is not Polygon) hit = VisualTreeHelper.GetParent(hit);
        if (hit is Polygon { Tag: TimelineKeyframeHit keyHit })
        {
            SetLayerSelection([keyHit.Layer], keyHit.Layer, updateAnchor: true);
            SelectedKeyframe = keyHit.Keyframe;
            ShowKeyframeContextMenu(keyHit.Layer, keyHit.Keyframe);
            e.Handled = true;
            return;
        }
        var layer = TimelineLayerAt(point);
        if (layer is null)
        {
            var time = TimelineXToTime(point.X);
            var nearbyEvt = SelectedProject?.TimelineEvents?.FirstOrDefault(ev => Math.Abs(TimelineTimeToX(ev.TimeSeconds) - point.X) < 14);
            ShowTimelineEventContextMenu(nearbyEvt, time, TimelineCanvas);
            e.Handled = true;
            return;
        }
        if (!IsLayerSelected(layer)) SetLayerSelection([layer], layer, updateAnchor: true);
        else SetLayerSelection(GetSelectedLayers(), layer);
        ShowLayerContextMenu(layer, point);
        e.Handled = true;
    }

    private void ShowLayerContextMenu(CgLayer layer, Point point)
    {
        var cm = new ContextMenu();
        var header = new MenuItem { Header = $"LAYER: {layer.Name}", IsEnabled = false, FontWeight = FontWeights.Bold };
        cm.Items.Add(header);
        cm.Items.Add(new Separator());

        var waitMenu = new MenuItem { Header = "WAIT / DELAY" };
        var w2 = new MenuItem { Header = "Set Wait: 2.0s (Prime Burst Hold)", IsChecked = Math.Abs(layer.TextAnimationDelaySeconds - 2.0) < 0.05 };
        w2.Click += (_, _) => { RecordUndoSnapshot(); layer.TextAnimationDelaySeconds = 2.0; CommitInspectorAutoKey(); SyncLayersToProject(); _main.SaveCgProjects(); RenderAll(); };
        waitMenu.Items.Add(w2);

        var w1 = new MenuItem { Header = "Set Wait: 1.0s", IsChecked = Math.Abs(layer.TextAnimationDelaySeconds - 1.0) < 0.05 };
        w1.Click += (_, _) => { RecordUndoSnapshot(); layer.TextAnimationDelaySeconds = 1.0; CommitInspectorAutoKey(); SyncLayersToProject(); _main.SaveCgProjects(); RenderAll(); };
        waitMenu.Items.Add(w1);

        var w05 = new MenuItem { Header = "Set Wait: 0.5s", IsChecked = Math.Abs(layer.TextAnimationDelaySeconds - 0.5) < 0.05 };
        w05.Click += (_, _) => { RecordUndoSnapshot(); layer.TextAnimationDelaySeconds = 0.5; CommitInspectorAutoKey(); SyncLayersToProject(); _main.SaveCgProjects(); RenderAll(); };
        waitMenu.Items.Add(w05);

        var w0 = new MenuItem { Header = "Clear Wait: 0s", IsChecked = layer.TextAnimationDelaySeconds <= 0.01 };
        w0.Click += (_, _) => { RecordUndoSnapshot(); layer.TextAnimationDelaySeconds = 0; CommitInspectorAutoKey(); SyncLayersToProject(); _main.SaveCgProjects(); RenderAll(); };
        waitMenu.Items.Add(w0);

        cm.Items.Add(waitMenu);

        var addKeyItem = new MenuItem { Header = $"ADD KEYFRAME @ PLAYHEAD ({Playhead:0.00}s)" };
        addKeyItem.Click += (_, _) => { AddKeyframe_Click(null!, null!); };
        cm.Items.Add(addKeyItem);

        cm.Items.Add(new Separator());

        var groupItem = new MenuItem { Header = "GROUP SELECTED LAYERS" };
        groupItem.Click += GroupSelectedLayers_Click;
        cm.Items.Add(groupItem);

        var unGroupItem = new MenuItem { Header = "UNGROUP SELECTED" };
        unGroupItem.Click += UngroupSelectedLayers_Click;
        cm.Items.Add(unGroupItem);

        var delItem = new MenuItem { Header = "DELETE LAYER" };
        delItem.Click += (_, _) =>
        {
            RecordUndoSnapshot();
            Layers.Remove(layer);
            SelectedLayer = Layers.LastOrDefault();
            SyncLayersToProject();
            _main.SaveCgProjects();
            RenderAll();
        };
        cm.Items.Add(delItem);

        cm.PlacementTarget = TimelineCanvas;
        cm.IsOpen = true;
    }

    private void ShowKeyframeContextMenu(CgLayer layer, CgKeyframe keyframe)
    {
        var cm = new ContextMenu();
        var easeMenu = new MenuItem { Header = "EASING CURVE" };
        var easings = new (string Label, string EaseCode)[]
        {
            ("Linear", "linear"),
            ("Ease In-Out (Power2)", "power2.inOut"),
            ("Ease Out (Power2)", "power2.out"),
            ("Ease In (Power2)", "power2.in"),
            ("Ease In-Out (Cubic)", "cubic.inOut"),
            ("Ease Out (Cubic)", "cubic.out"),
            ("Bounce Out", "bounce.out"),
            ("Back Out (Overshoot)", "back.out(1.7)"),
            ("Elastic Out", "elastic.out(1,0.3)"),
            ("Step / Instant", "steps(1)")
        };
        foreach (var (lbl, code) in easings)
        {
            var mi = new MenuItem
            {
                Header = lbl,
                IsChecked = string.Equals(keyframe.Ease, code, StringComparison.OrdinalIgnoreCase)
            };
            mi.Click += (_, _) =>
            {
                keyframe.Ease = code;
                SyncLayersToProject(); _main.SaveCgProjects(); Raise(nameof(SelectedKeyframe)); RenderAll();
            };
            easeMenu.Items.Add(mi);
        }
        cm.Items.Add(easeMenu);

        var holdItem = new MenuItem { Header = "HOLD KEYFRAME", IsChecked = keyframe.Hold };
        holdItem.Click += (_, _) =>
        {
            keyframe.Hold = !keyframe.Hold;
            SyncLayersToProject(); _main.SaveCgProjects(); Raise(nameof(SelectedKeyframe)); RenderAll();
        };
        cm.Items.Add(holdItem);

        cm.Items.Add(new Separator());

        var colorItem = new MenuItem { Header = "KEYFRAME COLOR PICKER…" };
        colorItem.Click += (_, _) =>
        {
            var current = layer.Fill;
            if (string.Equals(layer.Type, "Shape", StringComparison.OrdinalIgnoreCase)) current = layer.Background;
            var picker = new KashtrixColorPickerWindow(current) { Owner = this };
            if (picker.ShowDialog() == true)
            {
                var hex = picker.SelectedColorText;
                var dict = new Dictionary<string, object?>();
                if (!string.IsNullOrWhiteSpace(keyframe.StyleJson))
                {
                    try { dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(keyframe.StyleJson) ?? []; } catch { }
                }
                if (string.Equals(layer.Type, "Shape", StringComparison.OrdinalIgnoreCase))
                {
                    dict["Background"] = hex;
                    layer.Background = hex;
                }
                else
                {
                    dict["Fill"] = hex;
                    layer.Fill = hex;
                }
                keyframe.StyleJson = JsonSerializer.Serialize(dict);
                SyncLayersToProject(); _main.SaveCgProjects(); Raise(nameof(SelectedKeyframe)); Raise(nameof(SelectedLayer)); RenderAll();
            }
        };
        cm.Items.Add(colorItem);

        cm.Items.Add(new Separator());

        var delItem = new MenuItem { Header = "DELETE KEYFRAME" };
        delItem.Click += (_, _) =>
        {
            layer.Keyframes.Remove(keyframe);
            SelectedKeyframe = null;
            SortSelectedKeyframes();
            SyncLayersToProject(); _main.SaveCgProjects(); RenderAll();
        };
        cm.Items.Add(delItem);

        cm.PlacementTarget = TimelineCanvas;
        cm.IsOpen = true;
    }

    private void TimelineRulerCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (TimelineRulerCanvas.ActualWidth <= 20) return;
        var pt = e.GetPosition(TimelineRulerCanvas);
        var hit = TimelineRulerCanvas.InputHitTest(pt) as DependencyObject;
        while (hit != null && hit is not Border && !ReferenceEquals(hit, TimelineRulerCanvas))
            hit = VisualTreeHelper.GetParent(hit);

        if (hit is Border { Tag: CgTimelineEvent evt })
        {
            _draggedPauseEvent = evt;
            _dragMode = "pauseEvent";
            TimelineRulerCanvas.CaptureMouse();
            e.Handled = true;
            return;
        }

        _playbackLoopIteration = 0;
        Playhead = TimelineXToTime(pt.X);
        _dragMode = "playhead";
        TimelineRulerCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void TimelineRulerCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var pt = e.GetPosition(TimelineRulerCanvas);
        if (_dragMode == "pauseEvent" && _draggedPauseEvent != null)
        {
            var newTime = TimelineXToTime(pt.X);
            _draggedPauseEvent.TimeSeconds = Math.Clamp(Math.Round(newTime, 2), 0, ProjectDuration);
            RenderTimeline();
            return;
        }
        if (_dragMode == "playhead")
        {
            Playhead = TimelineXToTime(pt.X);
        }
    }

    private void TimelineRulerCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragMode == "pauseEvent")
        {
            _dragMode = "";
            _draggedPauseEvent = null;
            TimelineRulerCanvas.ReleaseMouseCapture();
            SyncLayersToProject();
            _main.SaveCgProjects();
            RenderTimeline();
            e.Handled = true;
            return;
        }
        if (_dragMode == "playhead")
        {
            _dragMode = "";
            TimelineRulerCanvas.ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private void TimelineRulerCanvas_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var pt = e.GetPosition(TimelineRulerCanvas);
        var time = TimelineXToTime(pt.X);
        var hit = TimelineRulerCanvas.InputHitTest(pt) as DependencyObject;
        while (hit != null && hit is not Border && !ReferenceEquals(hit, TimelineRulerCanvas))
            hit = VisualTreeHelper.GetParent(hit);

        var clickedEvt = (hit as Border)?.Tag as CgTimelineEvent;
        if (clickedEvt == null && SelectedProject?.TimelineEvents != null)
        {
            clickedEvt = SelectedProject.TimelineEvents.FirstOrDefault(ev => Math.Abs(TimelineTimeToX(ev.TimeSeconds) - pt.X) < 14);
        }

        ShowTimelineEventContextMenu(clickedEvt, time, TimelineRulerCanvas);
        e.Handled = true;
    }

    private void ShowTimelineEventContextMenu(CgTimelineEvent? evt, double timeSeconds, FrameworkElement target)
    {
        var cm = new ContextMenu();
        if (evt != null)
        {
            var header = new MenuItem { Header = $"PAUSE EVENT @ {evt.TimeSeconds:0.##}s", IsEnabled = false, FontWeight = FontWeights.Bold };
            cm.Items.Add(header);
            cm.Items.Add(new Separator());

            var delItem = new MenuItem { Header = "❌ DELETE PAUSE EVENT" };
            delItem.Click += (_, _) =>
            {
                RecordUndoSnapshot();
                SelectedProject?.TimelineEvents?.Remove(evt);
                SyncLayersToProject();
                _main.SaveCgProjects();
                RenderTimeline();
            };
            cm.Items.Add(delItem);

            var triggers = new[] { "crawlComplete", "pushSequenceComplete", "tickerComplete", "manual" };
            var triggerMenu = new MenuItem { Header = "RESUME TRIGGER" };
            foreach (var trig in triggers)
            {
                var trigItem = new MenuItem { Header = trig, IsChecked = string.Equals(evt.ResumeTrigger, trig, StringComparison.OrdinalIgnoreCase) };
                trigItem.Click += (_, _) =>
                {
                    RecordUndoSnapshot();
                    evt.ResumeTrigger = trig;
                    SyncLayersToProject();
                    _main.SaveCgProjects();
                    RenderTimeline();
                };
                triggerMenu.Items.Add(trigItem);
            }
            cm.Items.Add(triggerMenu);
        }
        else
        {
            var clampedTime = Math.Clamp(Math.Round(timeSeconds, 2), 0, ProjectDuration);
            var addItem = new MenuItem { Header = $"⏸ ADD PAUSE EVENT @ {clampedTime:0.##}s" };
            addItem.Click += (_, _) =>
            {
                RecordUndoSnapshot();
                if (SelectedProject != null)
                {
                    SelectedProject.TimelineEvents ??= [];
                    SelectedProject.TimelineEvents.Add(new CgTimelineEvent
                    {
                        TimeSeconds = clampedTime,
                        Type = "Pause",
                        Label = "PAUSE",
                        ResumeTrigger = "crawlComplete"
                    });
                    SyncLayersToProject();
                    _main.SaveCgProjects();
                    RenderTimeline();
                }
            };
            cm.Items.Add(addItem);
        }

        cm.Items.Add(new Separator());
        var fitItem = new MenuItem { Header = "FIT TIMELINE TO VIEWPORT" };
        fitItem.Click += (_, _) => FitTimelineToViewport();
        cm.Items.Add(fitItem);

        cm.PlacementTarget = target;
        cm.IsOpen = true;
    }

    private void TimelineCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(TimelineCanvas);
        if (_dragMode == "playhead")
        {
            Playhead = TimelineXToTime(point.X);
            return;
        }
        if (_dragMode == "keyframe" && _dragKeyframe is not null)
        {
            _dragKeyframe.TimeSeconds = TimelineXToTime(point.X); SelectedKeyframe = _dragKeyframe; RenderTimeline(); RenderPreview(); return;
        }
        // Animation duration drag
        if ((_dragMode == "animIn" || _dragMode == "animOut" || _dragMode == "animWait") && _dragAnimationLayer is not null)
        {
            var animDelta = (point.X - _dragStartPoint.X) / Math.Max(1, TimelineTrackWidth()) * ProjectDuration;
            if (_dragMode == "animWait")
            {
                var newVal = Math.Clamp(_dragAnimationStartValue + animDelta, 0.0, Math.Max(0.1, _dragAnimationLayer.DurationSeconds * 0.9));
                _dragAnimationLayer.TextAnimationDelaySeconds = Math.Round(newVal, 2);
            }
            else if (_dragMode == "animIn")
            {
                var newVal = Math.Clamp(_dragAnimationStartValue + animDelta, 0.0, Math.Max(0.1, _dragAnimationLayer.DurationSeconds * 0.9));
                _dragAnimationLayer.AnimationInSeconds = Math.Round(newVal, 2);
            }
            else
            {
                var newVal = Math.Clamp(_dragAnimationStartValue - animDelta, 0.0, Math.Max(0.1, _dragAnimationLayer.DurationSeconds * 0.9));
                _dragAnimationLayer.AnimationOutSeconds = Math.Round(newVal, 2);
            }
            Raise(nameof(SelectedLayer)); RenderTimeline(); RenderPreview(); return;
        }
        if (_dragLayer is null) return;
        var delta = (point.X - _dragStartPoint.X) / Math.Max(1, TimelineTrackWidth()) * ProjectDuration;
        const double min = .10;
        if (_dragMode == "start") _dragLayer.StartSeconds = Math.Clamp(_dragStartSeconds + delta, 0, _dragLayer.EndSeconds - min);
        else if (_dragMode == "end") _dragLayer.EndSeconds = Math.Clamp(_dragEndSeconds + delta, _dragLayer.StartSeconds + min, ProjectDuration);
        else
        {
            var len = Math.Max(min, _dragEndSeconds - _dragStartSeconds);
            var start = Math.Clamp(_dragStartSeconds + delta, 0, Math.Max(0, ProjectDuration - len));
            _dragLayer.StartSeconds = start; _dragLayer.EndSeconds = Math.Min(ProjectDuration, start + len);
        }
        Raise(nameof(SelectedLayer)); RenderTimeline(); RenderPreview();
    }

    private void TimelineCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragMode == "") return;
        var movedLayer = _dragLayer is not null; var movedKeyframe = _dragKeyframe is not null; var movedAnimation = _dragAnimationLayer is not null;
        _dragLayer = null; _dragKeyframe = null; _dragKeyframeLayer = null; _dragKeyframeProperty = null; _dragAnimationLayer = null; _dragMode = ""; TimelineCanvas.ReleaseMouseCapture();
        if (movedKeyframe) SortSelectedKeyframes();
        if (movedLayer || movedKeyframe || movedAnimation) { SyncLayersToProject(); _main.SaveCgProjects(); }
        RenderAll();
    }
    private void LayerList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(LayerList, e.OriginalSource as DependencyObject) is not ListBoxItem item || item.DataContext is not CgLayer layer) return;
        if (e.ClickCount >= 2 && string.Equals(layer.Type, "Precomp", StringComparison.OrdinalIgnoreCase) && layer.Precomposition is not null)
        {
            e.Handled = true;
            EnterPrecomposition(layer);
            return;
        }
        var modifiers = Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift);
        e.Handled = true;
        SelectTimelineLayer(layer, modifiers);
    }

    private void LayerList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(LayerList, e.OriginalSource as DependencyObject) is not ListBoxItem item || item.DataContext is not CgLayer layer) return;
        if (!string.Equals(layer.Type, "Precomp", StringComparison.OrdinalIgnoreCase) || layer.Precomposition is null) return;
        e.Handled = true;
        EnterPrecomposition(layer);
    }

    private void LayerList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject origin) return;
        if (ItemsControl.ContainerFromElement(LayerList, origin) is not ListBoxItem item) return;
        if (item.DataContext is CgLayer layer && !IsLayerSelected(layer))
            SetLayerSelection([layer], layer, updateAnchor: true);
        else if (item.DataContext is CgLayer selectedLayer)
            SetLayerSelection(GetSelectedLayers(), selectedLayer);
    }

    private IReadOnlyList<CgLayer> GetSelectedLayers()
    {
        var selected = Layers.Where(IsLayerSelected).ToArray();
        if (selected.Length > 0) return selected;
        return SelectedLayer is not null && Layers.Contains(SelectedLayer) ? [SelectedLayer] : [];
    }

    private void OffsetLayerAtPlayhead(CgLayer layer, double dx, double dy)
    {
        if (AutoKey)
        {
            var key = EnsureAutoKeyframe(layer, Playhead);
            key.X += dx; key.Y += dy;
            if (ReferenceEquals(layer, SelectedLayer)) SelectedKeyframe = key;
            return;
        }
        layer.X += dx; layer.Y += dy;
        foreach (var key in layer.Keyframes ?? []) { key.X += dx; key.Y += dy; }
    }

    private void ResizeLayerAtPlayhead(CgLayer layer, double dw, double dh)
    {
        if (AutoKey)
        {
            var key = EnsureAutoKeyframe(layer, Playhead);
            key.Width = Math.Max(10, (key.Width > 0 ? key.Width : layer.Width) + dw);
            key.Height = Math.Max(10, (key.Height > 0 ? key.Height : layer.Height) + dh);
            if (ReferenceEquals(layer, SelectedLayer)) SelectedKeyframe = key;
            return;
        }
        layer.Width = Math.Max(10, layer.Width + dw);
        layer.Height = Math.Max(10, layer.Height + dh);
        foreach (var key in layer.Keyframes ?? [])
        {
            if (key.Width > 0) key.Width = Math.Max(10, key.Width + dw);
            if (key.Height > 0) key.Height = Math.Max(10, key.Height + dh);
        }
    }

    private void CopySelectedLayers()
    {
        var selected = GetSelectedLayers();
        if (selected.Count == 0) return;
        _layerClipboard.Clear();
        foreach (var layer in selected) _layerClipboard.Add(CloneLayer(layer, layer.Name));
        _pasteGeneration = 0;
    }

    private void PasteLayers()
    {
        if (_layerClipboard.Count == 0) return;
        RecordUndoSnapshot();
        var offset = 20.0 * (++_pasteGeneration);
        var added = new List<CgLayer>();
        foreach (var source in _layerClipboard)
        {
            var copy = CloneLayer(source, source.Name.EndsWith(" Copy", StringComparison.OrdinalIgnoreCase) ? source.Name : source.Name + " Copy", offset, offset);
            Layers.Add(copy); added.Add(copy);
        }
        if (added.Count > 0)
        {
            SetLayerSelection(added, added[^1], updateAnchor: true);
            SyncLayersToProject(); _main.SaveCgProjects(); RenderAll();
        }
    }

    private void DeleteSelectedLayers()
    {
        RecordUndoSnapshot();
        var selected = GetSelectedLayers().ToArray();
        if (selected.Length == 0) return;
        var firstIndex = selected.Select(Layers.IndexOf).Where(i => i >= 0).DefaultIfEmpty(0).Min();
        foreach (var layer in selected) Layers.Remove(layer);
        SelectedLayer = Layers.Count == 0 ? null : Layers[Math.Clamp(firstIndex, 0, Layers.Count - 1)];
        SyncLayersToProject(); _main.SaveCgProjects(); RenderAll();
    }

    private void GroupSelectedLayers()
    {
        var selected = GetSelectedLayers().ToArray();
        if (selected.Length < 2) return;
        RecordUndoSnapshot();
        var minX = selected.Min(x => x.X); var minY = selected.Min(x => x.Y);
        var maxX = selected.Max(x => x.X + x.Width); var maxY = selected.Max(x => x.Y + x.Height);
        var group = new CgGroup
        {
            Name = $"Group {Groups.Count + 1}",
            OriginX = (minX + maxX) / 2.0,
            OriginY = (minY + maxY) / 2.0
        };
        Groups.Add(group);
        foreach (var layer in selected) layer.GroupId = group.Id;
        SelectedGroup = group;
        SyncLayersToProject(); _main.SaveCgProjects(); RenderAll();
    }

    private void PrecomposeSelectedLayers()
    {
        var parent = SelectedProject;
        var selected = GetSelectedLayers().Distinct().OrderBy(Layers.IndexOf).ToArray();
        if (parent is null || selected.Length == 0) return;
        RecordUndoSnapshot();

        var selectedIds = selected.Select(x => x.Id).ToHashSet();
        var selectedIndices = selected.Select(Layers.IndexOf).Where(i => i >= 0).OrderBy(i => i).ToArray();
        if (selectedIndices.Length == 0) return;
        var minStart = Math.Max(0, selected.Min(x => x.StartSeconds));
        var maxEnd = Math.Max(minStart + .5, selected.Max(x => x.EndSeconds));
        var childDuration = Math.Max(.5, maxEnd - minStart);

        // Move fully selected groups into the child composition. A partially selected group stays
        // in the parent and its selected children become ungrouped inside the precomp, matching the
        // predictable "move all selected attributes into new composition" behavior.
        var includedGroupIds = Groups
            .Where(g =>
            {
                var members = Layers.Where(l => l.GroupId == g.Id).ToArray();
                return members.Length > 0 && members.All(l => selectedIds.Contains(l.Id));
            })
            .Select(g => g.Id).ToHashSet();
        var movedGroups = Groups.Where(g => includedGroupIds.Contains(g.Id)).ToArray();
        var originalCommonGroups = selected.Select(x => x.GroupId).Distinct().ToArray();
        var parentGroupId = originalCommonGroups.Length == 1 && originalCommonGroups[0] != Guid.Empty && !includedGroupIds.Contains(originalCommonGroups[0])
            ? originalCommonGroups[0] : Guid.Empty;

        var child = new CgProject
        {
            Name = $"Precomp {Layers.Count(x => string.Equals(x.Type, "Precomp", StringComparison.OrdinalIgnoreCase)) + 1}",
            Width = parent.Width,
            Height = parent.Height,
            FrameRate = parent.FrameRate,
            DurationSeconds = childDuration,
            OnAir = true,
            Layers = selected.ToList(),
            Groups = movedGroups.ToList(),
            HtmlSources = [],
            DataSources = JsonSerializer.Deserialize<List<CgDataSource>>(JsonSerializer.Serialize(parent.DataSources ?? [])) ?? []
        };

        foreach (var layer in child.Layers)
        {
            layer.StartSeconds = Math.Max(0, layer.StartSeconds - minStart);
            layer.EndSeconds = Math.Max(layer.StartSeconds + .001, layer.EndSeconds - minStart);
            foreach (var key in layer.Keyframes ?? []) key.TimeSeconds = Math.Max(0, key.TimeSeconds - minStart);
            if (layer.GroupId != Guid.Empty && !includedGroupIds.Contains(layer.GroupId)) layer.GroupId = Guid.Empty;
        }
        foreach (var group in child.Groups)
        {
            if (group.ParentGroupId != Guid.Empty && !includedGroupIds.Contains(group.ParentGroupId)) group.ParentGroupId = Guid.Empty;
            foreach (var key in group.Keyframes ?? []) key.TimeSeconds = Math.Max(0, key.TimeSeconds - minStart);
        }

        foreach (var layer in selected) Layers.Remove(layer);
        foreach (var group in movedGroups) Groups.Remove(group);

        var insertionIndex = Math.Clamp(selectedIndices.Min(), 0, Layers.Count);
        var precomp = new CgLayer
        {
            Name = child.Name,
            Type = "Precomp",
            Role = "Graphic",
            X = 0,
            Y = 0,
            Width = parent.Width,
            Height = parent.Height,
            StartSeconds = minStart,
            EndSeconds = maxEnd,
            AnimationIn = "None",
            AnimationOut = "None",
            Fill = "#FFFFFFFF",
            Background = "#00000000",
            GroupId = parentGroupId,
            Precomposition = child
        };
        Layers.Insert(insertionIndex, precomp);

        SetLayerSelection([precomp], precomp, updateAnchor: true);
        SelectedGroup = null;
        _timelineSelectionAnchorIndex = insertionIndex;
        SyncLayersToProject();
        _main.SaveCgProjects();
        Raise(nameof(CompositionPathText));
        RenderAll();
    }

    private void UngroupSelectedLayers()
    {
        var selected = GetSelectedLayers();
        if (selected.Count == 0) return;
        foreach (var layer in selected) layer.GroupId = Guid.Empty;
        SyncLayersToProject(); _main.SaveCgProjects(); RenderAll();
    }

    private void ReorderSelectedLayers(bool toFront, bool oneStep)
    {
        var selected = GetSelectedLayers().ToArray();
        if (selected.Length == 0) return;
        if (oneStep)
        {
            if (toFront)
            {
                foreach (var layer in selected.OrderByDescending(layer => Layers.IndexOf(layer)))
                {
                    var i = Layers.IndexOf(layer);
                    if (i >= 0 && i < Layers.Count - 1) Layers.Move(i, i + 1);
                }
            }
            else
            {
                foreach (var layer in selected.OrderBy(layer => Layers.IndexOf(layer)))
                {
                    var i = Layers.IndexOf(layer);
                    if (i > 0) Layers.Move(i, i - 1);
                }
            }
        }
        else
        {
            foreach (var layer in selected) Layers.Remove(layer);
            if (toFront)
                foreach (var layer in selected) Layers.Add(layer);
            else
                for (var i = selected.Length - 1; i >= 0; i--) Layers.Insert(0, selected[i]);
        }
        SyncLayersToProject(); _main.SaveCgProjects(); RenderAll();
    }

    private void ClearSelectedLayerAnimations()
    {
        foreach (var layer in GetSelectedLayers())
        {
            layer.AnimationIn = "None"; layer.AnimationOut = "None";
            layer.AnimationInSeconds = 0; layer.AnimationOutSeconds = 0;
        }
        SyncLayersToProject(); _main.SaveCgProjects(); Raise(nameof(SelectedLayer)); RenderAll();
    }

    private void CopyLayer_Click(object sender, RoutedEventArgs e) => CopySelectedLayers();
    private void CutLayer_Click(object sender, RoutedEventArgs e) { CopySelectedLayers(); DeleteSelectedLayers(); }
    private void PasteLayer_Click(object sender, RoutedEventArgs e) => PasteLayers();
    private void DuplicateSelectedLayers_Click(object sender, RoutedEventArgs e)
    {
        CopySelectedLayers(); PasteLayers();
    }
    private void DeleteSelectedLayers_Click(object sender, RoutedEventArgs e) => DeleteSelectedLayers();
    private void GroupSelectedLayers_Click(object sender, RoutedEventArgs e) => GroupSelectedLayers();
    private void PrecomposeSelectedLayers_Click(object sender, RoutedEventArgs e) => PrecomposeSelectedLayers();
    private void UngroupSelectedLayers_Click(object sender, RoutedEventArgs e) => UngroupSelectedLayers();
    private void BringToFront_Click(object sender, RoutedEventArgs e) => ReorderSelectedLayers(true, false);
    private void SendToBack_Click(object sender, RoutedEventArgs e) => ReorderSelectedLayers(false, false);
    private void MoveLayerUp_Click(object sender, RoutedEventArgs e) => ReorderSelectedLayers(true, true);
    private void MoveLayerDown_Click(object sender, RoutedEventArgs e) => ReorderSelectedLayers(false, true);
    private void ClearLayerAnimation_Click(object sender, RoutedEventArgs e) => ClearSelectedLayerAnimations();

    private void AlignSelectedLayers(string alignment)
    {
        var project = SelectedProject;
        if (project is null) return;
        var selected = GetSelectedLayers();
        if (selected.Count == 0) return;
        foreach (var layer in selected)
        {
            var current = AutoKey ? CgAnimationEngine.EvaluateLayerLocal(layer, Playhead) : CgAnimationEngine.MotionState.FromLayer(layer);
            var targetX = current.X;
            var targetY = current.Y;
            switch (alignment)
            {
                case "left": targetX = 0; break;
                case "hcenter": targetX = Math.Max(0, (project.Width - current.Width) / 2.0); break;
                case "right": targetX = Math.Max(0, project.Width - current.Width); break;
                case "top": targetY = 0; break;
                case "vcenter": targetY = Math.Max(0, (project.Height - current.Height) / 2.0); break;
                case "bottom": targetY = Math.Max(0, project.Height - current.Height); break;
            }
            if (AutoKey)
            {
                var key = EnsureAutoKeyframe(layer, Playhead);
                key.X = targetX; key.Y = targetY;
                if (ReferenceEquals(layer, SelectedLayer)) SelectedKeyframe = key;
            }
            else
            {
                var dx = targetX - layer.X; var dy = targetY - layer.Y;
                layer.X = targetX; layer.Y = targetY;
                foreach (var key in layer.Keyframes ?? []) { key.X += dx; key.Y += dy; }
            }
        }
        SyncLayersToProject(); _main.SaveCgProjects(); Raise(nameof(SelectedLayer)); RenderAll();
    }

    private void AlignLeft_Click(object sender, RoutedEventArgs e) => AlignSelectedLayers("left");
    private void AlignHorizontalCenter_Click(object sender, RoutedEventArgs e) => AlignSelectedLayers("hcenter");
    private void AlignRight_Click(object sender, RoutedEventArgs e) => AlignSelectedLayers("right");
    private void AlignTop_Click(object sender, RoutedEventArgs e) => AlignSelectedLayers("top");
    private void AlignVerticalCenter_Click(object sender, RoutedEventArgs e) => AlignSelectedLayers("vcenter");
    private void AlignBottom_Click(object sender, RoutedEventArgs e) => AlignSelectedLayers("bottom");

    private void ClearLayerSelection()
    {
        _previewDragLayer = null; _previewEditKeyframe = null; _previewDragMode = string.Empty;
        SetLayerSelection([], null, updateAnchor: true);
        SelectedKeyframe = null;
    }

    private void PreviewCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        PreviewCanvas.Focus();
        var point = e.GetPosition(PreviewCanvas);
        if (_penDrawing && SelectedProject is not null)
        {
            var px = SelectedProject.Width / Math.Max(1.0, PreviewCanvas.ActualWidth);
            var py = SelectedProject.Height / Math.Max(1.0, PreviewCanvas.ActualHeight);
            var projectPoint = new Point(
                Math.Clamp(point.X * px, 0, SelectedProject.Width),
                Math.Clamp(point.Y * py, 0, SelectedProject.Height));
            if (_penPoints.Count == 0 || (projectPoint - _penPoints[^1]).Length > 1)
                _penPoints.Add(projectPoint);
            if (e.ClickCount >= 2) FinishPenDrawing(); else RenderPreview();
            e.Handled = true;
            return;
        }
        if(_drawMaskLayer is not null && SelectedProject is not null)
        {
            if (AutoKey && _autoKeyEditSnapshot is null) CaptureAutoKeyEditSnapshot();
            _drawingMask=true; _drawMaskStart=point;
            var px=SelectedProject.Width/Math.Max(1.0,PreviewCanvas.ActualWidth); var py=SelectedProject.Height/Math.Max(1.0,PreviewCanvas.ActualHeight);
            _drawMaskLayer.MaskEnabled=true; _drawMaskLayer.MaskX=point.X*px; _drawMaskLayer.MaskY=point.Y*py; _drawMaskLayer.MaskWidth=2; _drawMaskLayer.MaskHeight=2;
            PreviewCanvas.CaptureMouse(); RenderPreview(); e.Handled=true; return;
        }
        if(_pendingVideoSourceLayer is not null && SelectedProject is not null)
        {
            var layer=_pendingVideoSourceLayer; _pendingVideoSourceLayer=null;
            var px=SelectedProject.Width/Math.Max(1.0,PreviewCanvas.ActualWidth); var py=SelectedProject.Height/Math.Max(1.0,PreviewCanvas.ActualHeight);
            layer.X=point.X*px; layer.Y=point.Y*py; layer.Width=4; layer.Height=4;
            Layers.Add(layer); SetLayerSelection([layer], layer, updateAnchor: true);
            _drawVideoSourceLayer=layer; _drawVideoStart=point; PreviewCanvas.CaptureMouse(); RenderPreview(); e.Handled=true; return;
        }
        var hit = PreviewCanvas.InputHitTest(point) as DependencyObject;
        while (hit is not null && hit is FrameworkElement fe && fe.Tag is null) hit = VisualTreeHelper.GetParent(hit);
        if (hit is FrameworkElement element && element.Tag is PreviewHit ph)
        {
            if (e.ClickCount >= 2 && string.Equals(ph.Layer.Type, "Precomp", StringComparison.OrdinalIgnoreCase) && ph.Layer.Precomposition is not null)
            {
                EnterPrecomposition(ph.Layer);
                e.Handled = true;
                return;
            }
            var selectionModifiers = Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift);
            if (selectionModifiers != ModifierKeys.None)
            {
                SelectTimelineLayer(ph.Layer, selectionModifiers);
                e.Handled = true;
                return;
            }
            RecordUndoSnapshot();
            SetLayerSelection([ph.Layer], ph.Layer, updateAnchor: true);
            _previewDragLayer = ph.Layer;
            _previewDragMode = ph.Mode;
            _previewDragStart = point;

            if (ph.Layer.SqueezeProgram && (ph.Layer.SqueezeWidth <= 0 || ph.Layer.SqueezeHeight <= 0))
            {
                ph.Layer.SqueezeX = ph.Layer.X;
                ph.Layer.SqueezeY = ph.Layer.Y;
                ph.Layer.SqueezeWidth = ph.Layer.Width;
                ph.Layer.SqueezeHeight = ph.Layer.Height;
            }

            if (ph.Mode == "move_pip" || ph.Mode.StartsWith("pip_"))
            {
                _previewEditKeyframe = null;
                _previewStartX = ph.Layer.SqueezeX;
                _previewStartY = ph.Layer.SqueezeY;
                _previewStartW = ph.Layer.SqueezeWidth > 0 ? ph.Layer.SqueezeWidth : ph.Layer.Width;
                _previewStartH = ph.Layer.SqueezeHeight > 0 ? ph.Layer.SqueezeHeight : ph.Layer.Height;
            }
            else if (ph.Mode == "move_mask" || ph.Mode.StartsWith("mask_"))
            {
                _previewEditKeyframe = null;
                _previewStartX = ph.Layer.MaskX;
                _previewStartY = ph.Layer.MaskY;
                _previewStartW = ph.Layer.MaskWidth > 0 ? ph.Layer.MaskWidth : ph.Layer.Width;
                _previewStartH = ph.Layer.MaskHeight > 0 ? ph.Layer.MaskHeight : ph.Layer.Height;
            }
            else if (AutoKey)
            {
                _previewEditKeyframe = EnsureAutoKeyframe(ph.Layer, Playhead);
                _previewStartX = _previewEditKeyframe.X; _previewStartY = _previewEditKeyframe.Y;
                _previewStartW = _previewEditKeyframe.Width > 0 ? _previewEditKeyframe.Width : ph.Layer.Width;
                _previewStartH = _previewEditKeyframe.Height > 0 ? _previewEditKeyframe.Height : ph.Layer.Height;
            }
            else
            {
                _previewEditKeyframe = null;
                _previewStartX = ph.Layer.X; _previewStartY = ph.Layer.Y; _previewStartW = ph.Layer.Width; _previewStartH = ph.Layer.Height;
            }

            PreviewCanvas.CaptureMouse();
            e.Handled = true;
            return;
        }
        // Empty canvas deselects the editor selection only. It never alters GroupId, data links,
        // animation bindings or layer membership, so clicking away cannot "unlink" a layer.
        ClearLayerSelection();
        e.Handled = true;
    }

    private void PreviewCanvas_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        PreviewCanvas.Focus();
        if (_penDrawing)
        {
            FinishPenDrawing();
            e.Handled = true;
            return;
        }
        var point=e.GetPosition(PreviewCanvas);
        var hit=PreviewCanvas.InputHitTest(point) as DependencyObject;
        while(hit is not null && hit is FrameworkElement fe && fe.Tag is null) hit=VisualTreeHelper.GetParent(hit);
        if(hit is FrameworkElement element && element.Tag is PreviewHit ph)
        {
            if (!IsLayerSelected(ph.Layer)) SetLayerSelection([ph.Layer], ph.Layer, updateAnchor: true);
            else SetLayerSelection(GetSelectedLayers(), ph.Layer);
        }
    }

    private void ApplyInteractivePreviewGeometry(CgProject project, CgLayer layer)
    {
        var sx = (PreviewCanvas.Width > 1 ? PreviewCanvas.Width : 960.0) / Math.Max(1.0, project.Width);
        var sy = (PreviewCanvas.Height > 1 ? PreviewCanvas.Height : 540.0) / Math.Max(1.0, project.Height);
        var previewMotionTime = CgDataSourceService.ResolveCategoryLayerTimelineSeconds(project, layer, ContinuousPlaybackSeconds, Playhead);
        var motion = CgAnimationEngine.EvaluateLayer(project, layer, previewMotionTime);
        var baseW = Math.Max(2, motion.Width * sx);
        var baseH = Math.Max(2, motion.Height * sy);
        var rawX = motion.X * sx; var rawY = motion.Y * sy;

        var anchorPxX = baseW * Math.Clamp(motion.AnchorX, 0, 1);
        var anchorPxY = baseH * Math.Clamp(motion.AnchorY, 0, 1);

        var group = new TransformGroup();
        var skewX = Math.Clamp(motion.SkewX + Math.Tan(motion.RotationY * Math.PI / 180.0) * 35.0, -75, 75);
        var skewY = Math.Clamp(motion.SkewY - Math.Tan(motion.RotationX * Math.PI / 180.0) * 35.0, -75, 75);
        if (Math.Abs(skewX) > .001 || Math.Abs(skewY) > .001)
            group.Children.Add(new SkewTransform(skewX, skewY, anchorPxX, anchorPxY));
        if (Math.Abs(motion.Rotation) > .001)
            group.Children.Add(new RotateTransform(motion.Rotation, anchorPxX, anchorPxY));

        if (_previewLayerVisuals.TryGetValue(layer, out var visual))
        {
            var x = rawX; var y = rawY; var w = baseW; var h = baseH;
            var visualLayer = CgAnimationEngine.CreateVisualLayer(layer, previewMotionTime);
            ApplyPreviewAnimation(visualLayer, ref x, ref y, ref w, ref h, visual);
            var previewType = (visualLayer.Type ?? "Text").ToUpperInvariant();
            var isTickerOrBadge = previewType is "TICKER" ||
                (visualLayer.DataField != null && visualLayer.DataField.Contains("category", StringComparison.OrdinalIgnoreCase)) ||
                (visualLayer.Name != null && visualLayer.Name.Contains("Badge", StringComparison.OrdinalIgnoreCase));
            if (!isTickerOrBadge && previewType is "TEXT" or "ROLL" or "DIGITALCLOCK" or "DATETIME" or "TIMECODE" or "NEPALIDATE" or "NEPALICLOCK")
            {
                var textVisual = CgAnimationEngine.EvaluateTextVisual(visualLayer, Playhead);
                visual.Opacity = Math.Clamp(visual.Opacity * textVisual.Opacity, 0, 1);
                x += textVisual.OffsetX * sx; y += textVisual.OffsetY * sy;
                if (Math.Abs(textVisual.Scale - 1) > .0001)
                {
                    var oldW = w;
                    var oldH = h;
                    w *= textVisual.Scale;
                    h *= textVisual.Scale;
                    var align = (visualLayer.HorizontalTextAlignment ?? "Left").Trim().ToLowerInvariant();
                    if (align == "left")
                    {
                        y += (oldH - h) / 2.0;
                    }
                    else if (align == "right")
                    {
                        x += (oldW - w);
                        y += (oldH - h) / 2.0;
                    }
                    else
                    {
                        x += (oldW - w) / 2.0;
                        y += (oldH - h) / 2.0;
                    }
                }
            }
            visual.Width = w; visual.Height = h;
            visual.RenderTransform = group.Children.Count > 0 ? group : null;
            Canvas.SetLeft(visual, x); Canvas.SetTop(visual, y);
        }

        if (_previewSelectionOutline is not null)
        {
            _previewSelectionOutline.Width = Math.Max(8, visual?.Width ?? baseW);
            _previewSelectionOutline.Height = Math.Max(8, visual?.Height ?? baseH);
            _previewSelectionOutline.RenderTransform = group.Children.Count > 0 ? group : null;
            Canvas.SetLeft(_previewSelectionOutline, visual != null ? Canvas.GetLeft(visual) : rawX);
            Canvas.SetTop(_previewSelectionOutline, visual != null ? Canvas.GetTop(visual) : rawY);
        }

        var pNW = group.Transform(new Point(0, 0));
        var pNE = group.Transform(new Point(baseW, 0));
        var pSW = group.Transform(new Point(0, baseH));
        var pSE = group.Transform(new Point(baseW, baseH));
        var pN = group.Transform(new Point(baseW / 2, 0));
        var pS = group.Transform(new Point(baseW / 2, baseH));
        var pW = group.Transform(new Point(0, baseH / 2));
        var pE = group.Transform(new Point(baseW, baseH / 2));

        if (_previewSelectionHandles.TryGetValue("nw", out var nw)) { Canvas.SetLeft(nw, rawX + pNW.X - 5); Canvas.SetTop(nw, rawY + pNW.Y - 5); }
        if (_previewSelectionHandles.TryGetValue("ne", out var ne)) { Canvas.SetLeft(ne, rawX + pNE.X - 5); Canvas.SetTop(ne, rawY + pNE.Y - 5); }
        if (_previewSelectionHandles.TryGetValue("sw", out var sw)) { Canvas.SetLeft(sw, rawX + pSW.X - 5); Canvas.SetTop(sw, rawY + pSW.Y - 5); }
        if (_previewSelectionHandles.TryGetValue("se", out var se)) { Canvas.SetLeft(se, rawX + pSE.X - 5); Canvas.SetTop(se, rawY + pSE.Y - 5); }
        if (_previewSelectionHandles.TryGetValue("n", out var nH)) { Canvas.SetLeft(nH, rawX + pN.X - 5); Canvas.SetTop(nH, rawY + pN.Y - 5); }
        if (_previewSelectionHandles.TryGetValue("s", out var sH)) { Canvas.SetLeft(sH, rawX + pS.X - 5); Canvas.SetTop(sH, rawY + pS.Y - 5); }
        if (_previewSelectionHandles.TryGetValue("w", out var wH)) { Canvas.SetLeft(wH, rawX + pW.X - 5); Canvas.SetTop(wH, rawY + pW.Y - 5); }
        if (_previewSelectionHandles.TryGetValue("e", out var eH)) { Canvas.SetLeft(eH, rawX + pE.X - 5); Canvas.SetTop(eH, rawY + pE.Y - 5); }
        if (_previewSelectionLabel is not null)
        {
            Canvas.SetLeft(_previewSelectionLabel, Math.Max(0, rawX + pNW.X));
            Canvas.SetTop(_previewSelectionLabel, Math.Max(0, rawY + pNW.Y - 22));
            if (_previewSelectionLabel.Child is TextBlock text)
                text.Text = $"{layer.Name}  X {motion.X:0}  Y {motion.Y:0}  {motion.Width:0}×{motion.Height:0}";
        }
    }

    private void PreviewCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if(_drawingMask && _drawMaskLayer is not null && e.LeftButton==MouseButtonState.Pressed && SelectedProject is not null)
        {
            var maskPoint=e.GetPosition(PreviewCanvas); var px=SelectedProject.Width/Math.Max(1.0,PreviewCanvas.ActualWidth); var py=SelectedProject.Height/Math.Max(1.0,PreviewCanvas.ActualHeight);
            var x1=Math.Min(_drawMaskStart.X,maskPoint.X)*px; var y1=Math.Min(_drawMaskStart.Y,maskPoint.Y)*py;
            var x2=Math.Max(_drawMaskStart.X,maskPoint.X)*px; var y2=Math.Max(_drawMaskStart.Y,maskPoint.Y)*py;
            _drawMaskLayer.MaskX=x1; _drawMaskLayer.MaskY=y1; _drawMaskLayer.MaskWidth=Math.Max(2,x2-x1); _drawMaskLayer.MaskHeight=Math.Max(2,y2-y1);
            RequestInteractiveRender(); return;
        }
        if(_drawVideoSourceLayer is not null && e.LeftButton==MouseButtonState.Pressed && SelectedProject is not null)
        {
            var videoPoint=e.GetPosition(PreviewCanvas); var px=SelectedProject.Width/Math.Max(1.0,PreviewCanvas.ActualWidth); var py=SelectedProject.Height/Math.Max(1.0,PreviewCanvas.ActualHeight);
            var x1=Math.Min(_drawVideoStart.X,videoPoint.X)*px; var y1=Math.Min(_drawVideoStart.Y,videoPoint.Y)*py;
            var x2=Math.Max(_drawVideoStart.X,videoPoint.X)*px; var y2=Math.Max(_drawVideoStart.Y,videoPoint.Y)*py;
            _drawVideoSourceLayer.X=x1; _drawVideoSourceLayer.Y=y1; _drawVideoSourceLayer.Width=Math.Max(4,x2-x1); _drawVideoSourceLayer.Height=Math.Max(4,y2-y1);
            RequestInteractiveRender(); return;
        }
        if (_previewDragLayer is null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(PreviewCanvas);
        var project = SelectedProject;
        if (project is null) return;
        var canvasW = PreviewCanvas.ActualWidth > 1 ? PreviewCanvas.ActualWidth : PreviewCanvas.Width;
        var canvasH = PreviewCanvas.ActualHeight > 1 ? PreviewCanvas.ActualHeight : PreviewCanvas.Height;
        var dx = (p.X - _previewDragStart.X) * project.Width / Math.Max(1.0, canvasW);
        var dy = (p.Y - _previewDragStart.Y) * project.Height / Math.Max(1.0, canvasH);
        var l = _previewDragLayer;
        var key = _previewEditKeyframe;
        const double min = 10;

        double x = _previewStartX, y = _previewStartY, w = _previewStartW, h = _previewStartH;
        if (_previewDragMode == "move_pip" || _previewDragMode.StartsWith("pip_"))
        {
            switch (_previewDragMode)
            {
                case "pip_nw": x += dx; y += dy; w = Math.Max(min, w - dx); h = Math.Max(min, h - dy); break;
                case "pip_ne": y += dy; w = Math.Max(min, w + dx); h = Math.Max(min, h - dy); break;
                case "pip_sw": x += dx; w = Math.Max(min, w - dx); h = Math.Max(min, h + dy); break;
                case "pip_se": w = Math.Max(min, w + dx); h = Math.Max(min, h + dy); break;
                case "pip_n": y += dy; h = Math.Max(min, h - dy); break;
                case "pip_s": h = Math.Max(min, h + dy); break;
                case "pip_w": x += dx; w = Math.Max(min, w - dx); break;
                case "pip_e": w = Math.Max(min, w + dx); break;
                default: x += dx; y += dy; break;
            }
            l.SqueezeX = Math.Max(0, x);
            l.SqueezeY = Math.Max(0, y);
            l.SqueezeWidth = Math.Max(20, w);
            l.SqueezeHeight = Math.Max(20, h);
            l.X = l.SqueezeX;
            l.Y = l.SqueezeY;
            l.Width = l.SqueezeWidth;
            l.Height = l.SqueezeHeight;
            ApplyInteractivePreviewGeometry(project, l);
            RequestInteractiveRender();
            return;
        }

        if (_previewDragMode == "move_mask" || _previewDragMode.StartsWith("mask_"))
        {
            switch (_previewDragMode)
            {
                case "mask_nw": x += dx; y += dy; w = Math.Max(min, w - dx); h = Math.Max(min, h - dy); break;
                case "mask_ne": y += dy; w = Math.Max(min, w + dx); h = Math.Max(min, h - dy); break;
                case "mask_sw": x += dx; w = Math.Max(min, w - dx); h = Math.Max(min, h + dy); break;
                case "mask_se": w = Math.Max(min, w + dx); h = Math.Max(min, h + dy); break;
                case "mask_n": y += dy; h = Math.Max(min, h - dy); break;
                case "mask_s": h = Math.Max(min, h + dy); break;
                case "mask_w": x += dx; w = Math.Max(min, w - dx); break;
                case "mask_e": w = Math.Max(min, w + dx); break;
                default: x += dx; y += dy; break;
            }
            l.MaskX = Math.Round(x, 1);
            l.MaskY = Math.Round(y, 1);
            l.MaskWidth = Math.Round(Math.Max(10, w), 1);
            l.MaskHeight = Math.Round(Math.Max(10, h), 1);
            RequestInteractiveRender();
            return;
        }

        switch (_previewDragMode)
        {
            case "nw": x += dx; y += dy; w = Math.Max(min, w - dx); h = Math.Max(min, h - dy); break;
            case "ne": y += dy; w = Math.Max(min, w + dx); h = Math.Max(min, h - dy); break;
            case "sw": x += dx; w = Math.Max(min, w - dx); h = Math.Max(min, h + dy); break;
            case "se": w = Math.Max(min, w + dx); h = Math.Max(min, h + dy); break;
            case "n": y += dy; h = Math.Max(min, h - dy); break;
            case "s": h = Math.Max(min, h + dy); break;
            case "w": x += dx; w = Math.Max(min, w - dx); break;
            case "e": w = Math.Max(min, w + dx); break;
            default: x += dx; y += dy; break;
        }

        if (key is not null)
        {
            key.X = x; key.Y = y; key.Width = w; key.Height = h;
            l.X = x; l.Y = y; l.Width = w; l.Height = h;
            if (l.SqueezeProgram)
            {
                l.SqueezeX = l.X; l.SqueezeY = l.Y; l.SqueezeWidth = l.Width; l.SqueezeHeight = l.Height;
            }
        }
        else
        {
            var moveDx = x - l.X; var moveDy = y - l.Y; var sizeDw = w - l.Width; var sizeDh = h - l.Height;
            l.X = x; l.Y = y; l.Width = w; l.Height = h;
            if (l.SqueezeProgram)
            {
                l.SqueezeX = l.X; l.SqueezeY = l.Y; l.SqueezeWidth = l.Width; l.SqueezeHeight = l.Height;
            }
            // AUTO KEY off means an intentional base/global edit: offset all authored points so
            // the existing animation retains its relative motion rather than collapsing.
            foreach (var frame in l.Keyframes ?? [])
            {
                frame.X += moveDx; frame.Y += moveDy;
                if (frame.Width > 0) frame.Width = Math.Max(min, frame.Width + sizeDw);
                if (frame.Height > 0) frame.Height = Math.Max(min, frame.Height + sizeDh);
            }
        }

        // Directly manipulate only the active visual/selection chrome. This avoids re-decoding
        // video, rebuilding HTML thumbnails and recreating every canvas child while the mouse moves.
        ApplyInteractivePreviewGeometry(project, l);
        if (l.SqueezeProgram) RequestInteractiveRender();
    }

    private void PreviewCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if(_drawingMask && _drawMaskLayer is not null)
        {
            var layer=_drawMaskLayer; _drawingMask=false; _drawMaskLayer=null; PreviewCanvas.Cursor=Cursors.Arrow; PreviewCanvas.ReleaseMouseCapture(); CancelInteractiveRender();
            if(layer.MaskWidth<4 || layer.MaskHeight<4) { layer.MaskX=layer.X; layer.MaskY=layer.Y; layer.MaskWidth=Math.Max(4,layer.Width); layer.MaskHeight=Math.Max(4,layer.Height); }
            CommitInspectorAutoKey(); SyncLayersToProject(); _main.SaveCgProjects(); Raise(nameof(SelectedLayer)); RenderAll(); e.Handled=true; return;
        }
        if(_drawVideoSourceLayer is not null)
        {
            var layer=_drawVideoSourceLayer; _drawVideoSourceLayer=null; PreviewCanvas.Cursor=Cursors.Arrow; PreviewCanvas.ReleaseMouseCapture(); CancelInteractiveRender();
            if(layer.Width<20 || layer.Height<20) { layer.Width=640; layer.Height=360; }
            SyncLayersToProject(); _main.SaveCgProjects(); RenderAll(); e.Handled=true; return;
        }
        if (_previewDragLayer is null) return;
        if (_previewDragMode == "move_pip" || _previewDragMode.StartsWith("pip_"))
        {
            _previewDragLayer = null; _previewDragMode = ""; _previewEditKeyframe = null;
            PreviewCanvas.ReleaseMouseCapture();
            CancelInteractiveRender();
            SyncLayersToProject(); _main.SaveCgProjects();
            Raise(nameof(SelectedLayer));
            RenderAll();
            e.Handled = true;
            return;
        }
        if (_previewDragMode == "move_mask" || _previewDragMode.StartsWith("mask_"))
        {
            _previewDragLayer = null; _previewDragMode = ""; _previewEditKeyframe = null;
            PreviewCanvas.ReleaseMouseCapture();
            CancelInteractiveRender();
            CommitInspectorAutoKey();
            SyncLayersToProject(); _main.SaveCgProjects();
            Raise(nameof(SelectedLayer));
            RenderAll();
            e.Handled = true;
            return;
        }
        var draggedLayer = _previewDragLayer;
        _previewDragLayer = null; _previewDragMode = ""; _previewEditKeyframe = null;
        PreviewCanvas.ReleaseMouseCapture();
        CancelInteractiveRender();
        SortSelectedKeyframes();
        SyncLayersToProject(); _main.SaveCgProjects();
        Raise(nameof(SelectedKeyframe));
        Raise(nameof(SelectedLayer));
        RenderAll();
    }

    private bool TryAdjustNumericTextBox(TextBox textBox, KeyEventArgs e)
    {
        var expression = textBox.GetBindingExpression(TextBox.TextProperty);
        if (expression is null) return false;
        var raw = textBox.Text?.Trim() ?? string.Empty;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) &&
            !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return false;

        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10.0
            : Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? .1
            : Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) ? .01
            : 1.0;
        if (e.Key == Key.Down) step = -step;

        var source = expression.DataItem;
        var propertyPath = expression.ParentBinding.Path?.Path ?? string.Empty;
        var pathParts = propertyPath.Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < pathParts.Length - 1 && source is not null; i++)
            source = source.GetType().GetProperty(pathParts[i])?.GetValue(source);
        var propertyName = pathParts.LastOrDefault() ?? string.Empty;
        var property = source?.GetType().GetProperty(propertyName);
        var integral = property?.PropertyType == typeof(int) || property?.PropertyType == typeof(long) ||
                       property?.PropertyType == typeof(short) || property?.PropertyType == typeof(byte);
        if (integral)
        {
            var wholeStep = Math.Sign(step) * Math.Max(1, (int)Math.Round(Math.Abs(step)));
            value = Math.Round(value) + wholeStep;
        }
        else value += step;

        if (propertyName.Equals("GradientAngle", StringComparison.OrdinalIgnoreCase)) value = (value % 360 + 360) % 360;
        else if (propertyName is "Opacity" or "GlossOpacity" or "Offset" or "AnchorX" or "AnchorY") value = Math.Clamp(value, 0, 1);
        else if (propertyName.Contains("Width", StringComparison.OrdinalIgnoreCase) || propertyName.Contains("Height", StringComparison.OrdinalIgnoreCase)) value = Math.Max(1, value);
        else if (propertyName.StartsWith("Scale", StringComparison.OrdinalIgnoreCase)) value = Math.Max(.01, value);
        else if (propertyName.Contains("Seconds", StringComparison.OrdinalIgnoreCase) || propertyName.Contains("Feather", StringComparison.OrdinalIgnoreCase) || propertyName.Contains("Radius", StringComparison.OrdinalIgnoreCase)) value = Math.Max(0, value);

        textBox.Text = integral ? value.ToString("0", CultureInfo.InvariantCulture) : value.ToString("0.###", CultureInfo.InvariantCulture);
        textBox.CaretIndex = textBox.Text.Length;
        expression.UpdateSource();
        CommitInspectorAutoKey();
        SyncLayersToProject();
        _main.SaveCgProjects();
        Raise(nameof(SelectedLayer));
        RenderAll();
        return true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        if (Keyboard.FocusedElement is TextBox textBox)
        {
            if (e.Key == Key.Enter)
            {
                var be = textBox.GetBindingExpression(TextBox.TextProperty);
                be?.UpdateSource();
                RecordUndoSnapshot();
                CommitInspectorAutoKey();
                SyncLayersToProject();
                _main.SaveCgProjects();
                RenderAll();
                Keyboard.ClearFocus();
                e.Handled = true;
                return;
            }
            if (ctrl && e.Key == Key.Z)
            {
                if (textBox.CanUndo) { textBox.Undo(); e.Handled = true; return; }
                Undo();
                e.Handled = true;
                return;
            }
            if (ctrl && e.Key == Key.Y)
            {
                if (textBox.CanRedo) { textBox.Redo(); e.Handled = true; return; }
                Redo();
                e.Handled = true;
                return;
            }
            if (e.Key is Key.Up or Key.Down && TryAdjustNumericTextBox(textBox, e)) e.Handled = true;
            return;
        }
        if (Keyboard.FocusedElement is ComboBox) return;

        if (_penDrawing && e.Key == Key.Enter) { FinishPenDrawing(); e.Handled = true; return; }
        if (_penDrawing && e.Key == Key.Escape) { FinishPenDrawing(cancel: true); e.Handled = true; return; }

        if (e.Key == Key.Space)
        {
            Play_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.Z) { Undo(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.Y) { Redo(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.A) { SetLayerSelection(Layers, Layers.LastOrDefault(), updateAnchor: true); e.Handled=true; return; }
        if (ctrl && e.Key == Key.C) { CopySelectedLayers(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.X) { CopySelectedLayers(); DeleteSelectedLayers(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.V) { PasteLayers(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.D) { DuplicateSelectedLayers_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.G) { PrecomposeSelectedLayers(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.G) { GroupSelectedLayers(); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.Delete) { ClearCanvas_Click(this,new RoutedEventArgs()); e.Handled=true; return; }
        if (e.Key == Key.Escape && (_drawMaskLayer is not null || _drawingMask)) { _drawMaskLayer=null; _drawingMask=false; PreviewCanvas.ReleaseMouseCapture(); PreviewCanvas.Cursor=Cursors.Arrow; RenderPreview(); e.Handled=true; return; }
        if (e.Key == Key.Escape && _pendingVideoSourceLayer is not null) { _pendingVideoSourceLayer=null; PreviewCanvas.Cursor=Cursors.Arrow; e.Handled=true; return; }
        if (e.Key is Key.Delete or Key.Back) { DeleteSelectedLayers(); e.Handled = true; return; }
        if (e.Key == Key.PageUp) { ReorderSelectedLayers(true, ctrl); e.Handled = true; return; }
        if (e.Key == Key.PageDown) { ReorderSelectedLayers(false, ctrl); e.Handled = true; return; }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && e.Key == Key.Up) { ReorderSelectedLayers(true, true); e.Handled = true; return; }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && e.Key == Key.Down) { ReorderSelectedLayers(false, true); e.Handled = true; return; }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && e.Key == Key.Home) { ReorderSelectedLayers(true, false); e.Handled = true; return; }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && e.Key == Key.End) { ReorderSelectedLayers(false, false); e.Handled = true; return; }

        var selected = GetSelectedLayers();
        if (selected.Count == 0) return;
        var step = shift ? 10.0 : 1.0;
        var resize = ctrl;
        var handled = true;
        foreach (var l in selected)
        {
            switch (e.Key)
            {
                case Key.Left:
                case Key.A:
                    if (resize) ResizeLayerAtPlayhead(l, -step, 0); else OffsetLayerAtPlayhead(l, -step, 0);
                    break;
                case Key.Right:
                case Key.D:
                    if (resize) ResizeLayerAtPlayhead(l, step, 0); else OffsetLayerAtPlayhead(l, step, 0);
                    break;
                case Key.Up:
                case Key.W:
                    if (resize) ResizeLayerAtPlayhead(l, 0, -step); else OffsetLayerAtPlayhead(l, 0, -step);
                    break;
                case Key.Down:
                case Key.S:
                    if (resize) ResizeLayerAtPlayhead(l, 0, step); else OffsetLayerAtPlayhead(l, 0, step);
                    break;
                default:
                    handled = false;
                    break;
            }
            if (!handled) break;
        }
        if (!handled) return;
        e.Handled = true;
        SyncLayersToProject(); _main.SaveCgProjects(); Raise(nameof(SelectedLayer)); RenderAll();
    }

    private void ClearCanvas_Click(object sender,RoutedEventArgs e)
    {
        if(SelectedProject is null)return;
        Layers.Clear();HtmlSources.Clear();Groups.Clear();Keyframes.Clear();GroupKeyframes.Clear();
        SelectedLayer=null;SelectedHtml=null;SelectedGroup=null;SelectedKeyframe=null;SelectedGroupKeyframe=null;
        SyncLayersToProject();_main.SaveCgProjects();RenderAll();PreviewCanvas.Focus();
    }

    private void Window_PreviewMouseDown(object sender,MouseButtonEventArgs e) => CaptureAutoKeyEditSnapshot();
    private void ToggleEditorPanel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Tag is not string panel) return;
        SetEditorPanelVisible(panel, item.IsChecked);
    }

    private void RestoreEditorPanels_Click(object sender, RoutedEventArgs e)
    {
        foreach (var panel in new[] { "Library", "Canvas", "Inspector", "Timeline" }) SetEditorPanelVisible(panel, true);
        LibraryPanelMenu.IsChecked = CanvasPanelMenu.IsChecked = InspectorPanelMenu.IsChecked = TimelinePanelMenu.IsChecked = true;
    }

    private void SetEditorPanelVisible(string panel, bool visible)
    {
        var show = visible ? Visibility.Visible : Visibility.Collapsed;
        switch (panel)
        {
            case "Library": LibraryPanel.Visibility = show; LibraryColumn.Width = visible ? new GridLength(286) : new GridLength(0); break;
            case "Canvas": CanvasPanel.Visibility = show; CanvasColumn.Width = visible ? new GridLength(1, GridUnitType.Star) : new GridLength(0); break;
            case "Inspector": InspectorPanel.Visibility = show; InspectorColumn.Width = visible ? new GridLength(390) : new GridLength(0); break;
            case "Timeline": TimelinePanel.Visibility = show; TimelineRow.MinHeight = visible ? 160 : 0; TimelineRow.Height = visible ? new GridLength(280) : new GridLength(0); break;
        }
        Dispatcher.BeginInvoke(new Action(() => { if (visible && panel == "Timeline") FitTimelineToViewport(); RenderAll(); }), DispatcherPriority.Background);
    }

    private void Window_PreviewGotKeyboardFocus(object sender,KeyboardFocusChangedEventArgs e)
    {
        if(e.NewFocus is TextBox or ComboBox or CheckBox)CaptureAutoKeyEditSnapshot();
    }


    private Button? TimelineDockButton => FindName("TimelineDockBtn") as Button;
    private Button? CanvasDockButton => FindName("CanvasDockBtn") as Button;
    private Button? InspectorDockButton => FindName("InspectorDockBtn") as Button;
    private Button? LibraryDockButton => FindName("LibraryDockBtn") as Button;

    private Window? _timelineDockWindow;
    private Grid? _timelineHomeGrid;

    private void TimelineDockOut_Click(object sender, RoutedEventArgs e)
    {
        if (_timelineDockWindow is { IsVisible: true }) { _timelineDockWindow.Close(); return; }
        _timelineHomeGrid ??= TimelinePanel.Parent as Grid;
        if (_timelineHomeGrid is null) return;
        _timelineHomeGrid.Children.Remove(TimelinePanel);
        var oldMargin = TimelinePanel.Margin;
        var oldTimelineHeight = TimelineRow.Height;
        var oldTimelineMinHeight = TimelineRow.MinHeight;
        TimelineRow.MinHeight = 0;
        TimelineRow.Height = new GridLength(0);
        TimelinePanel.Margin = new Thickness(0);
        if (TimelineDockButton != null) TimelineDockButton.Content = "DOCK IN";
        var dock = new Window
        {
            Title = "Kashtrix CG Timeline", Width = 1320, Height = 440, MinWidth = 900, MinHeight = 260,
            Background = new SolidColorBrush(Color.FromRgb(7, 11, 16)), Content = TimelinePanel,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, ResizeMode = ResizeMode.CanResizeWithGrip, SizeToContent = SizeToContent.Manual, ShowInTaskbar = true, Icon = Icon
        };
        _timelineDockWindow = dock;
        dock.Closed += (_, _) =>
        {
            dock.Content = null;
            TimelinePanel.Margin = oldMargin;
            Grid.SetRow(TimelinePanel, 2);
            _timelineHomeGrid.Children.Add(TimelinePanel);
            TimelineRow.MinHeight = oldTimelineMinHeight;
            TimelineRow.Height = oldTimelineHeight;
            _timelineDockWindow = null;
            if (TimelineDockButton != null) TimelineDockButton.Content = "DOCK OUT";
            Dispatcher.BeginInvoke(new Action(RenderTimeline));
        };
        dock.Show();
        Dispatcher.BeginInvoke(new Action(RenderTimeline));
    }

    private Window? _canvasDockWindow;
    private Grid? _canvasHomeGrid;

    private void CanvasDockOut_Click(object sender, RoutedEventArgs e)
    {
        if (_canvasDockWindow is { IsVisible: true }) { _canvasDockWindow.Close(); return; }
        _canvasHomeGrid ??= CanvasPanel.Parent as Grid;
        if (_canvasHomeGrid is null) return;
        _canvasHomeGrid.Children.Remove(CanvasPanel);
        var oldMargin = CanvasPanel.Margin;
        var oldWidth = CanvasColumn.Width;
        CanvasColumn.Width = new GridLength(0);
        CanvasPanel.Margin = new Thickness(0);
        if (CanvasDockButton != null) CanvasDockButton.Content = "DOCK IN";
        var dock = new Window
        {
            Title = "Kashtrix CG Canvas Preview", Width = 1000, Height = 640, MinWidth = 640, MinHeight = 400,
            Background = new SolidColorBrush(Color.FromRgb(7, 11, 16)), Content = CanvasPanel,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, ResizeMode = ResizeMode.CanResizeWithGrip, SizeToContent = SizeToContent.Manual, ShowInTaskbar = true, Icon = Icon
        };
        _canvasDockWindow = dock;
        dock.Closed += (_, _) =>
        {
            dock.Content = null;
            CanvasPanel.Margin = oldMargin;
            Grid.SetColumn(CanvasPanel, 1);
            _canvasHomeGrid.Children.Add(CanvasPanel);
            CanvasColumn.Width = oldWidth;
            _canvasDockWindow = null;
            if (CanvasDockButton != null) CanvasDockButton.Content = "DOCK OUT";
            Dispatcher.BeginInvoke(new Action(RenderPreview));
        };
        dock.Show();
        Dispatcher.BeginInvoke(new Action(RenderPreview));
    }

    private Window? _inspectorDockWindow;
    private Grid? _inspectorHomeGrid;

    private void InspectorDockOut_Click(object sender, RoutedEventArgs e)
    {
        if (_inspectorDockWindow is { IsVisible: true }) { _inspectorDockWindow.Close(); return; }
        _inspectorHomeGrid ??= InspectorPanel.Parent as Grid;
        if (_inspectorHomeGrid is null) return;
        _inspectorHomeGrid.Children.Remove(InspectorPanel);
        var oldMargin = InspectorPanel.Margin;
        var oldWidth = InspectorColumn.Width;
        InspectorColumn.Width = new GridLength(0);
        InspectorPanel.Margin = new Thickness(0);
        if (InspectorDockButton != null) InspectorDockButton.Content = "DOCK IN";
        var dock = new Window
        {
            Title = "Kashtrix CG Inspector & Properties", Width = 460, Height = 800, MinWidth = 360, MinHeight = 500,
            Background = new SolidColorBrush(Color.FromRgb(7, 11, 16)), Content = InspectorPanel,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, ResizeMode = ResizeMode.CanResizeWithGrip, SizeToContent = SizeToContent.Manual, ShowInTaskbar = true, Icon = Icon
        };
        _inspectorDockWindow = dock;
        dock.Closed += (_, _) =>
        {
            dock.Content = null;
            InspectorPanel.Margin = oldMargin;
            Grid.SetColumn(InspectorPanel, 2);
            _inspectorHomeGrid.Children.Add(InspectorPanel);
            InspectorColumn.Width = oldWidth;
            _inspectorDockWindow = null;
            if (InspectorDockButton != null) InspectorDockButton.Content = "DOCK OUT";
        };
        dock.Show();
    }

    private Window? _libraryDockWindow;
    private Grid? _libraryHomeGrid;

    private void LibraryDockOut_Click(object sender, RoutedEventArgs e)
    {
        if (_libraryDockWindow is { IsVisible: true }) { _libraryDockWindow.Close(); return; }
        _libraryHomeGrid ??= LibraryPanel.Parent as Grid;
        if (_libraryHomeGrid is null) return;
        _libraryHomeGrid.Children.Remove(LibraryPanel);
        var oldMargin = LibraryPanel.Margin;
        var oldWidth = LibraryColumn.Width;
        LibraryColumn.Width = new GridLength(0);
        LibraryPanel.Margin = new Thickness(0);
        if (LibraryDockButton != null) LibraryDockButton.Content = "DOCK IN";
        var dock = new Window
        {
            Title = "Kashtrix CG Layers & Sources", Width = 380, Height = 700, MinWidth = 280, MinHeight = 400,
            Background = new SolidColorBrush(Color.FromRgb(7, 11, 16)), Content = LibraryPanel,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, ResizeMode = ResizeMode.CanResizeWithGrip, SizeToContent = SizeToContent.Manual, ShowInTaskbar = true, Icon = Icon
        };
        _libraryDockWindow = dock;
        dock.Closed += (_, _) =>
        {
            dock.Content = null;
            LibraryPanel.Margin = oldMargin;
            Grid.SetColumn(LibraryPanel, 0);
            _libraryHomeGrid.Children.Add(LibraryPanel);
            LibraryColumn.Width = oldWidth;
            _libraryDockWindow = null;
            if (LibraryDockButton != null) LibraryDockButton.Content = "DOCK OUT";
        };
        dock.Show();
    }

    private void ExportCompositionFile_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProject is null) return;
        SyncLayersToProject();
        var sfd = new SaveFileDialog
        {
            Title = "Export Kashtrix CG Composition",
            FileName = $"{SelectedProject.Name.Replace(' ', '_')}.kcg",
            Filter = "Kashtrix CG File (*.kcg;*.kashtrixcg)|*.kcg;*.kashtrixcg|Legacy Kashtrix CG (*.cgx)|*.cgx|All Files (*.*)|*.*",
            DefaultExt = ".kcg"
        };
        if (sfd.ShowDialog() != true) return;
        try
        {
            KashtrixCgFileService.SaveComposition(sfd.FileName, SelectedProject);
            MessageBox.Show($"Composition successfully exported in proprietary format to:\n{sfd.FileName}", "Export Composition", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to export composition: {ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImportCompositionFile_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new OpenFileDialog
        {
            Title = "Import Kashtrix CG Composition",
            Filter = "Kashtrix CG Files (*.kcg;*.kashtrixcg;*.cgx;*.json)|*.kcg;*.kashtrixcg;*.cgx;*.json|All Files (*.*)|*.*"
        };
        if (ofd.ShowDialog() != true) return;
        try
        {
            var imported = KashtrixCgFileService.LoadComposition(ofd.FileName);
            if (imported is null) throw new InvalidOperationException("Invalid composition file data.");
            SanitizeProjectForEditor(imported);
            var baseName = IOPath.GetFileNameWithoutExtension(ofd.FileName);
            if (string.IsNullOrWhiteSpace(imported.Name)) imported.Name = baseName;
            imported.Id = Guid.NewGuid();
            _main.CgProjects.Add(imported);
            SelectedProject = imported;
            _main.SaveCgProjects();
            Raise(nameof(SelectedProject));
            RenderAll();
            MessageBox.Show($"Composition '{imported.Name}' successfully imported into CG project library.", "Import Composition", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to import composition: {ex.Message}", "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Rotate90Cw_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedLayer is null) return;
        if (AutoKey && _autoKeyEditSnapshot is null) CaptureAutoKeyEditSnapshot();
        if (AutoKey)
        {
            var key = EnsureAutoKeyframe(SelectedLayer, Playhead);
            key.Rotation = (key.Rotation + 90.0) % 360.0;
            SelectedKeyframe = key;
        }
        else
        {
            SelectedLayer.Rotation = (SelectedLayer.Rotation + 90.0) % 360.0;
        }
        SyncLayersToProject(); _main.SaveCgProjects(); Raise(nameof(SelectedLayer)); RenderAll();
    }

    private void Rotate90Ccw_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedLayer is null) return;
        if (AutoKey && _autoKeyEditSnapshot is null) CaptureAutoKeyEditSnapshot();
        if (AutoKey)
        {
            var key = EnsureAutoKeyframe(SelectedLayer, Playhead);
            key.Rotation = (key.Rotation - 90.0 + 360.0) % 360.0;
            SelectedKeyframe = key;
        }
        else
        {
            SelectedLayer.Rotation = (SelectedLayer.Rotation - 90.0 + 360.0) % 360.0;
        }
        SyncLayersToProject(); _main.SaveCgProjects(); Raise(nameof(SelectedLayer)); RenderAll();
    }

    private void ResetRotation_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedLayer is null) return;
        if (AutoKey && _autoKeyEditSnapshot is null) CaptureAutoKeyEditSnapshot();
        if (AutoKey)
        {
            var key = EnsureAutoKeyframe(SelectedLayer, Playhead);
            key.Rotation = 0; key.RotationX = 0; key.RotationY = 0;
            SelectedKeyframe = key;
        }
        else
        {
            SelectedLayer.Rotation = 0; SelectedLayer.RotationX = 0; SelectedLayer.RotationY = 0;
        }
        SyncLayersToProject(); _main.SaveCgProjects(); Raise(nameof(SelectedLayer)); RenderAll();
    }

    private void ApplySkew_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedLayer is null || sender is not FrameworkElement fe || fe.Tag is not string tag) return;
        if (!double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var skew)) return;
        if (AutoKey && _autoKeyEditSnapshot is null) CaptureAutoKeyEditSnapshot();
        if (AutoKey)
        {
            var key = EnsureAutoKeyframe(SelectedLayer, Playhead);
            key.SkewX = skew;
            SelectedKeyframe = key;
        }
        else
        {
            SelectedLayer.SkewX = skew;
        }
        SyncLayersToProject(); _main.SaveCgProjects(); Raise(nameof(SelectedLayer)); RenderAll();
    }

    private void Apply3dTilt_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedLayer is null || sender is not FrameworkElement fe || fe.Tag is not string tag) return;
        if (AutoKey && _autoKeyEditSnapshot is null) CaptureAutoKeyEditSnapshot();
        var key = AutoKey ? EnsureAutoKeyframe(SelectedLayer, Playhead) : null;
        switch (tag)
        {
            case "TiltX":
                if (key is not null) { key.RotationX = (key.RotationX + 20) % 360; SelectedKeyframe = key; }
                else SelectedLayer.RotationX = (SelectedLayer.RotationX + 20) % 360;
                break;
            case "TiltY":
                if (key is not null) { key.RotationY = (key.RotationY + 20) % 360; SelectedKeyframe = key; }
                else SelectedLayer.RotationY = (SelectedLayer.RotationY + 20) % 360;
                break;
            default:
                if (key is not null) { key.RotationX = 0; key.RotationY = 0; key.Z = 0; SelectedKeyframe = key; }
                else { SelectedLayer.RotationX = 0; SelectedLayer.RotationY = 0; SelectedLayer.Z = 0; }
                break;
        }
        SyncLayersToProject(); _main.SaveCgProjects(); Raise(nameof(SelectedLayer)); RenderAll();
    }

    private void ApplyWarpPreset_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedLayer is null || sender is not FrameworkElement fe || fe.Tag is not string tag) return;
        if (AutoKey && _autoKeyEditSnapshot is null) CaptureAutoKeyEditSnapshot();
        var key = AutoKey ? EnsureAutoKeyframe(SelectedLayer, Playhead) : null;
        switch (tag)
        {
            case "SlantedTicker":
                if (key is not null) { key.SkewX = -18; key.Rotation = -1.5; SelectedKeyframe = key; }
                else { SelectedLayer.SkewX = -18; SelectedLayer.Rotation = -1.5; }
                break;
            case "NewsStrip":
                if (key is not null) { key.SkewX = -12; key.RotationY = 15; key.Z = 40; SelectedKeyframe = key; }
                else { SelectedLayer.SkewX = -12; SelectedLayer.RotationY = 15; SelectedLayer.Z = 40; }
                break;
            case "Parallelogram":
                if (key is not null) { key.SkewX = 22; SelectedKeyframe = key; }
                else { SelectedLayer.SkewX = 22; }
                break;
            default:
                if (key is not null) { key.SkewX = 0; key.SkewY = 0; key.Rotation = 0; key.RotationX = 0; key.RotationY = 0; key.Z = 0; SelectedKeyframe = key; }
                else { SelectedLayer.SkewX = 0; SelectedLayer.SkewY = 0; SelectedLayer.Rotation = 0; SelectedLayer.RotationX = 0; SelectedLayer.RotationY = 0; SelectedLayer.Z = 0; }
                break;
        }
        SyncLayersToProject(); _main.SaveCgProjects(); Raise(nameof(SelectedLayer)); RenderAll();
    }

    private void SetGradientAngle_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedLayer is null || sender is not FrameworkElement fe || fe.Tag is not string tag) return;
        if (double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var angle))
        {
            SelectedLayer.GradientAngle = angle;
            SelectedLayer.UseGradient = true;
            CommitInspectorAutoKey();
            SyncLayersToProject();
            _main.SaveCgProjects();
            Raise(nameof(SelectedLayer));
            RenderAll();
        }
    }

    private void AutoDetectSqueeze_Click(object sender, RoutedEventArgs e)
    {
        var project = SelectedProject;
        if (project is null || SelectedLayer is null) return;
        var pw = project.Width > 0 ? project.Width : 1920.0;
        var ph = project.Height > 0 ? project.Height : 1080.0;

        var shapes = Layers.Where(l => l.Visible && (string.Equals(l.Type, "Shape", StringComparison.OrdinalIgnoreCase) || string.Equals(l.Type, "Line", StringComparison.OrdinalIgnoreCase))).ToArray();
        double leftMargin = 0, rightMargin = 0, bottomMargin = 0, topMargin = 0;

        foreach (var s in shapes)
        {
            var sx = s.X; var sy = s.Y; var sw = s.Width; var sh = s.Height;
            if (sx <= pw * 0.15 && sw < pw * 0.5) leftMargin = Math.Max(leftMargin, sx + sw);
            if (sx + sw >= pw * 0.85 && sw < pw * 0.5) rightMargin = Math.Max(rightMargin, pw - sx);
            if (sy + sh >= ph * 0.80 && sh < ph * 0.45) bottomMargin = Math.Max(bottomMargin, ph - sy);
            if (sy <= ph * 0.15 && sh < ph * 0.3) topMargin = Math.Max(topMargin, sy + sh);
        }

        if (leftMargin > 0) leftMargin += 8;
        if (rightMargin > 0) rightMargin += 8;
        if (bottomMargin > 0) bottomMargin += 8;
        if (topMargin > 0) topMargin += 8;

        if (leftMargin == 0 && bottomMargin == 0 && rightMargin == 0 && topMargin == 0)
        {
            leftMargin = 420;
            bottomMargin = 140;
        }

        var availX = leftMargin;
        var availY = topMargin;
        var availW = Math.Max(320, pw - leftMargin - rightMargin);
        var availH = Math.Max(180, ph - topMargin - bottomMargin);

        SelectedLayer.SqueezeProgram = true;
        SelectedLayer.SqueezeX = availX;
        SelectedLayer.SqueezeY = availY;
        SelectedLayer.SqueezeWidth = availW;
        SelectedLayer.SqueezeHeight = availH;
        SelectedLayer.SqueezeHorizontalMode = leftMargin > 0 ? "Left" : rightMargin > 0 ? "Right" : "Both";

        CommitInspectorAutoKey();
        SyncLayersToProject();
        _main.SaveCgProjects();
        Raise(nameof(SelectedLayer));
        RenderAll();
        MessageBox.Show($"Program Video Squeeze region calculated:\nX: {availX:0}, Y: {availY:0}, Width: {availW:0}, Height: {availH:0}\n(Available space outside shapes)", "Auto-Detect Program Squeeze", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => WindowChromeActions.Drag(this, e);
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowChromeActions.ToggleMaximize(this);


    private void FontSize_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || SelectedLayer is null) return;
        if (sender is TextBox tb && double.TryParse(tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var size) && size > 1)
        {
            SelectedLayer.FontSize = size;
            CommitInspectorAutoKey();
            SyncLayersToProject();
            RequestInteractiveRender();
        }
    }

    private void ReloadTemplates_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _main.RebuildCgDemoPack();
            _main.SaveCgProjects();
            _main.ReloadCgProjectsFromDatabase();
            ProjectView.Refresh();
            Raise(nameof(TemplateCountText));
            RenderAll();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Failed to reload templates: {ex.Message}", "Kashtrix CG", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ApplyEditorLayout_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not string layout) return;
        switch (layout)
        {
            case "Standard":
                SetEditorPanelVisible("Library", true);
                SetEditorPanelVisible("Canvas", true);
                SetEditorPanelVisible("Inspector", true);
                SetEditorPanelVisible("Timeline", true);
                LibraryPanelMenu.IsChecked = CanvasPanelMenu.IsChecked = InspectorPanelMenu.IsChecked = TimelinePanelMenu.IsChecked = true;
                break;
            case "CanvasFocus":
                SetEditorPanelVisible("Library", false);
                SetEditorPanelVisible("Canvas", true);
                SetEditorPanelVisible("Inspector", true);
                SetEditorPanelVisible("Timeline", false);
                LibraryPanelMenu.IsChecked = TimelinePanelMenu.IsChecked = false;
                CanvasPanelMenu.IsChecked = InspectorPanelMenu.IsChecked = true;
                break;
            case "TimelineFocus":
                SetEditorPanelVisible("Library", false);
                SetEditorPanelVisible("Canvas", true);
                SetEditorPanelVisible("Inspector", false);
                SetEditorPanelVisible("Timeline", true);
                LibraryPanelMenu.IsChecked = InspectorPanelMenu.IsChecked = false;
                CanvasPanelMenu.IsChecked = TimelinePanelMenu.IsChecked = true;
                break;
        }
    }

    private void OpenCgController_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var controller = new CgControllerWindow(_main);
            controller.Owner = this;
            controller.Show();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Could not open CG Controller: {ex.Message}", "Kashtrix CG", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void InsertVariable_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        if (SelectedLayer is null) return;
        RecordUndoSnapshot();
        var current = SelectedLayer.Text ?? string.Empty;
        SelectedLayer.Text = string.IsNullOrEmpty(current) ? tag : current + " " + tag;
        Raise(nameof(SelectedLayer));
        SyncLayersToProject();
        _main.SaveCgProjects();
        RenderAll();
    }
}
