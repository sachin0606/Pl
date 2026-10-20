using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using BroadcastPlayout.Engine;
using BroadcastPlayout.Ffmpeg;
using BroadcastPlayout.Models;
using BroadcastPlayout.Outputs;
using BroadcastPlayout.Services;
using BroadcastPlayout.Professional;
using BroadcastPlayout.Scheduling;
using Forms = System.Windows.Forms;
using WpfApplication = System.Windows.Application;
using WpfMessageBox = BroadcastPlayout.Views.MessageBox;
using WpfOpenFileDialog = Microsoft.Win32.OpenFileDialog;
using WpfSaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace BroadcastPlayout.ViewModels;

public sealed record DisplayOption(int Index, string Name, string Resolution)
{
    public string Label => $"DISPLAY {Index + 1} · {Name} · {Resolution}";
}

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly PlayoutEngine _engine = new();
    private readonly NdiSender _ndi = new();
    private readonly DeckLinkOutputAdapter _deckLink = new();
    private LocalAudioOutput _localAudio = null!;
    private LocalAudioOutput _previewAudio = null!;
    private readonly MultiNdiOutputManager _multiNdi = new();
    private readonly VirtualOutputBridge _virtualOutput = new();
    // Internal confidence transport is independent from the optional DirectShow bridge.
    // Only the real Playout VM owns this map; standalone controllers must never publish
    // idle/black frames over the active channel's Program confidence feed.
    private readonly VirtualOutputBridge _programConfidenceOutput = new(
        VirtualOutputBridge.ProgramConfidenceVideoMapName,
        VirtualOutputBridge.ProgramConfidenceAudioMapName);
    private readonly AsRunLogService _asRun = new();
    private readonly ProfessionalBroadcastRuntime _professional = new();
    private readonly DvbSubtitleTsSender _dvbSubtitles = new();
    private readonly CgCompositor _cgCompositor = new();
    private readonly CgCompositor _previewCgCompositor = new();
    private readonly CgCompositor _logoCgCompositor = new();
    private readonly SchedulerService _scheduler = new();
    private readonly BroadcastDatabase? _database = null;
    private AppSettings _settings;

    private PlaylistItem? _selectedItem;
    private PlaylistItem? _currentItem;
    private string _engineStatus = "READY";
    private bool _ndiEnabled;
    private bool _deckLinkEnabled;
    private int _deckLinkDeviceIndex;
    private bool _localAudioEnabled = true;
    private bool _fullscreenEnabled;
    private string _channelName = "KASHTRIX NEWS HD";
    private string _ndiName = "KASHTRIX-PLAYOUT-01";
    private string _ndiStatus = "OFF";
    private double _currentElapsedSeconds;
    private double _programSeekSeconds;
    private double _previewSeekSeconds;
    private bool _programSeekDragging;
    private double _audioLeftLevel;
    private double _audioRightLevel;
    private string _audioLeftDb = "-∞ dB";
    private string _audioRightDb = "-∞ dB";
    private CancellationTokenSource? _previewCts;
    private CancellationTokenSource? _previewAudioCts;
    private CancellationTokenSource? _previewPlaybackCts;
    private bool _isPreviewPlaying;
    private bool _isPreviewPaused;
    private readonly ManualResetEventSlim _previewPauseGate = new(initialState: true);
    private bool _manualStopRequested;
    private bool _markPlayedItemsCompleted = true;
    private bool _autoRemovePlayedItems;
    private bool _hidePlayedItems;
    private bool _dimCompletedItems = true;
    private int _playlistRowHeight = 30;
    private double _playlistFontSize = 10.5;
    private string _playlistDefaultRowColor = string.Empty;
    private string _playlistDefaultTextColor = "#D9DEE3";
    private bool _showPlaylistThumbnails = true;
    private int _playlistThumbnailWidth = 42;
    private int _playlistThumbnailHeight = 24;
    private string _defaultDecoderPreference = "Auto (CPU+GPU)";
    private string _audioDownmixPreset = "Auto Stereo";
    private DateTime _lastProgressUi = DateTime.MinValue;
    private DateTime _lastMeterUi = DateTime.MinValue;
    private DateTime _lastRawMeterUtc = DateTime.MinValue;
    private int _fullscreenMonitorIndex;
    private CgProject? _activeCgProject;
    private CgProject? _previewCgProject;
    private CgProject? _logoOverlayProject;
    private DateTime _activeCgStartedUtc = DateTime.UtcNow;
    private DateTime _previewCgStartedUtc = DateTime.UtcNow;
    private DateTime _logoCgStartedUtc = DateTime.UtcNow;
    private VideoFrameData? _lastPreviewBaseFrame;
    private CancellationTokenSource? _cgPreviewLoopCts;
    private Task? _cgPreviewLoopTask;
    private bool _keepEngineRunningInTray = true;
    private bool _restoreLastPlaylist = true;
    private string _databaseStatus = "LOCAL DB READY";
    private VideoFrameData? _lastProgramSourceFrame;
    // The PROGRAM bus is format-locked to the selected channel preset rather than inheriting
    // arbitrary source raster/cadence. The cadence pump samples the newest decoded frame at
    // the channel rate, renders CG at that fixed raster, and stamps authoritative frame timing.
    private readonly BgraFrameScaler _programChannelScaler = new();
    // Reusable Program output buffers: one rented per cadence frame, held by the output worker
    // until its synchronous sends finish. UI and Multi-NDI subscribers copy on receipt.
    private readonly ProgramFrameRing _programFrameRing = new(4);
    // Render-time A/V model: the cadence selects the picture and reads exactly one frame of audio
    // for the same reference time, so outputs receive both together.
    private readonly ProgramVideoTimeline _programVideoTimeline = new();
    private readonly ProgramAudioClock _programAudioClock = new();
    private VideoFrameData? _latestProgramCadenceSourceFrame;
    private CancellationTokenSource? _programCadenceCts;
    private Task? _programCadenceTask;
    private long _lastProgramSourceStamp;
    private double _lastProgramSourceElapsedSeconds;
    private double _programCadencePtsSeconds;
    // Hardware/network outputs are paced on a latest-frame worker so a synchronous DeckLink/NDI
    // call can never stall Program decoding. Capacity is intentionally one: under overload we
    // drop stale frames rather than accumulating latency and visible judder.
    private VideoFrameData? _pendingProgramOutputFrame;
    private int _programOutputWorkerActive;
    private bool _fillerEnabled;
    private int _fillerIndex;
    private string _fillerFolder = string.Empty;
    private string _defaultLogoTemplate = "Logo · Station Ident";
    private string _defaultGraphicsTemplate = "Prime HD · Breaking News Live";
    private double _programNowPlayingDelaySeconds = 3;
    private double _programComingUpBeforeEndSeconds = 12;
    private Guid _commercialBlockId = Guid.Empty;
    private bool _commercialBackInTriggered;
    private FileSystemWatcher? _settingsSignalWatcher;
    private FileSystemWatcher? _settingsSignalWatcherRoaming;
    private VideoFrameData? _cachedBlackBaseFrame;
    private string _channelId = string.Empty;
    private string _playoutPreset = "1080p50";
    private string _controllerHost = "127.0.0.1";
    private int _controllerPort = 9120;
    private bool _connectToController;
    private FileSystemWatcher? _scheduleSignalWatcher;
    private FileSystemWatcher? _playlistSignalWatcher;
    private FileSystemWatcher? _localCgWatcher;
    private FileSystemWatcher? _platformControlWatcher;
    private readonly object _cgCommandGate = new();
    private readonly Queue<string> _recentCgCommandOrder = new();
    private readonly HashSet<string> _recentCgCommandIds = new(StringComparer.OrdinalIgnoreCase);
    private bool _playlistReloadPending;
    private DateTime _timelineWindowStart = DateTime.Now;
    private DateTime _currentItemTimelineStart = DateTime.Now;
    private DateTime? _timelinePauseStartedUtc;
    private int _timelineStartPlaylistIndex;
    private double _timelineCanvasWidth = 1120.0;
    private ChannelClientService? _channelClient;
    private string _controllerStatus = "OFF";
    private string _controllerProgramPreview = "";
    private string _controllerCuePreview = "";
    private DateTime _lastControllerProgramPreviewUtc = DateTime.MinValue;
    private readonly bool _allowControllerClient;
    private bool _enableMasterOutputVolume = true;
    private int _masterOutputVolumePercent = 100;
    private bool _masterOutputMuted;
    private bool _enableVideoProcessing;
    private int _videoBrightness;
    private int _videoContrast = 100;
    private int _videoSaturation = 100;
    private double _videoGamma = 1.0;
    private int _videoHueDegrees;
    private double _videoExposureStops;
    private int _videoTemperature;
    private int _videoTint;
    private int _videoSharpness;
    private int _videoBlackLevel;
    private int _videoWhiteLevel;
    private int _videoRedGainPercent = 100;
    private int _videoGreenGainPercent = 100;
    private int _videoBlueGainPercent = 100;
    private bool _enableQcOnImport = true;
    private bool _virtualOutputEnabled;
    private string _virtualOutputName = "Kashtrix Playout Virtual Output";
    private int _virtualOutputWidth = 1920;
    private int _virtualOutputHeight = 1080;
    private double _virtualOutputFrameRate = 50;
    private int _programAudioDeviceNumber = -1;
    private int _previewAudioDeviceNumber = -1;
    private bool _enableAutoCg = true;
    private double _musicNowPlayingDelaySeconds = 3;
    private double _musicComingUpBeforeEndSeconds = 12;
    private double _newsNowPlayingDelaySeconds = 2;
    private double _newsComingUpBeforeEndSeconds = 8;
    private bool _autoNowPlayingShown;
    private bool _autoComingUpShown;
    private bool _autoCgProjectActive;
    private double _autoCgOnAirUntilSeconds = -1;
    private double _autoCgHoldSeconds = 10;
    private readonly object _controllerCgStackGate = new();
    private readonly object _programCgTimelineGate = new();
    private readonly Dictionary<CgProject, (DateTime StartedUtc, double PtsAnchor)> _programCgTimelineAnchors =
        new(ReferenceEqualityComparer.Instance);
    private readonly List<CgProject> _controllerProgramStack = [];
    private ControllerCgTransitionState? _controllerCgTransition;

    private sealed class ControllerCgTransitionState
    {
        public CgProject? Outgoing { get; init; }
        public required CgProject Incoming { get; init; }
        public required string Mode { get; init; }
        public double DurationSeconds { get; init; }
        public DateTime StartedUtc { get; init; }
    }

    private System.Threading.Timer? _runtimeDiagnosticsTimer;
    private int _disposed;

    public MainViewModel(bool enableAutomation = true)
    {
        _allowControllerClient = enableAutomation;
        _settings = SettingsStore.Load();
        _settings.ProfessionalBroadcast ??= new ProfessionalBroadcastSettings();
        _channelName = string.IsNullOrWhiteSpace(_settings.ChannelName) ? "KASHTRIX NEWS HD" : _settings.ChannelName;
        // Keep the default channel identity stable across all standalone Kashtrix processes.
        // A random per-process id made local CG routing impossible when Settings.ChannelId
        // had not yet been saved by Playout.
        _channelId = string.IsNullOrWhiteSpace(_settings.ChannelId) ? "KTX-PLAYOUT-01" : _settings.ChannelId.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(_settings.ChannelId))
        {
            _settings.ChannelId = _channelId;
            try { SettingsStore.Save(_settings); } catch { }
        }
        _playoutPreset = ChannelFormatPreset.Resolve(_settings.PlayoutPreset).Name;
        _fillerEnabled = _settings.EnableFiller;
        _fillerFolder = _settings.FillerFolder ?? string.Empty;
        _controllerHost = string.IsNullOrWhiteSpace(_settings.ControllerHost) ? "127.0.0.1" : _settings.ControllerHost;
        _controllerPort = Math.Clamp(_settings.ControllerPort, 1024, 65535);
        _connectToController = _settings.ConnectToController || _controllerHost.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) || _controllerHost.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        _settings.ConnectToController = _connectToController;
        _ndiName = string.IsNullOrWhiteSpace(_settings.NdiSourceName) ? "KASHTRIX-PLAYOUT-01" : _settings.NdiSourceName;
        _localAudioEnabled = _settings.EnableLocalAudio;
        _programAudioDeviceNumber = _settings.ProgramAudioDeviceNumber;
        _previewAudioDeviceNumber = _settings.PreviewAudioDeviceNumber;
        _localAudio = new LocalAudioOutput(_programAudioDeviceNumber);
        _previewAudio = new LocalAudioOutput(_previewAudioDeviceNumber);
        _enableMasterOutputVolume = _settings.EnableMasterOutputVolume;
        _masterOutputVolumePercent = Math.Clamp(_settings.MasterOutputVolumePercent, 0, 200);
        _masterOutputMuted = _settings.MasterOutputMuted;
        _enableVideoProcessing = _settings.EnableVideoProcessing;
        _videoBrightness = Math.Clamp(_settings.VideoBrightness, -100, 100);
        _videoContrast = Math.Clamp(_settings.VideoContrast, 0, 200);
        _videoSaturation = Math.Clamp(_settings.VideoSaturation, 0, 200);
        _videoGamma = Math.Clamp(_settings.VideoGamma, 0.10, 4.0);
        _videoHueDegrees = Math.Clamp(_settings.VideoHueDegrees, -180, 180);
        _videoExposureStops = Math.Clamp(_settings.VideoExposureStops, -4.0, 4.0);
        _videoTemperature = Math.Clamp(_settings.VideoTemperature, -100, 100);
        _videoTint = Math.Clamp(_settings.VideoTint, -100, 100);
        _videoSharpness = Math.Clamp(_settings.VideoSharpness, 0, 100);
        _videoBlackLevel = Math.Clamp(_settings.VideoBlackLevel, -100, 100);
        _videoWhiteLevel = Math.Clamp(_settings.VideoWhiteLevel, -100, 100);
        _videoRedGainPercent = Math.Clamp(_settings.VideoRedGainPercent, 0, 200);
        _videoGreenGainPercent = Math.Clamp(_settings.VideoGreenGainPercent, 0, 200);
        _videoBlueGainPercent = Math.Clamp(_settings.VideoBlueGainPercent, 0, 200);
        _enableQcOnImport = _settings.EnableQcOnImport;
        _markPlayedItemsCompleted = _settings.MarkPlayedItemsCompleted;
        _autoRemovePlayedItems = _settings.AutoRemovePlayedItems;
        _hidePlayedItems = _settings.HidePlayedItems;
        _dimCompletedItems = _settings.DimCompletedItems;
        _playlistRowHeight = Math.Clamp(_settings.PlaylistRowHeight, 22, 96);
        _playlistFontSize = Math.Clamp(_settings.PlaylistFontSize, 8.0, 28.0);
        _playlistDefaultRowColor = _settings.PlaylistDefaultRowColor ?? string.Empty;
        _playlistDefaultTextColor = string.IsNullOrWhiteSpace(_settings.PlaylistDefaultTextColor) ? "#D9DEE3" : _settings.PlaylistDefaultTextColor;
        _showPlaylistThumbnails = _settings.ShowPlaylistThumbnails;
        _playlistThumbnailWidth = Math.Clamp(_settings.PlaylistThumbnailWidth, 24, 120);
        _playlistThumbnailHeight = Math.Clamp(_settings.PlaylistThumbnailHeight, 16, 90);
        _defaultDecoderPreference = string.IsNullOrWhiteSpace(_settings.DefaultDecoderPreference) ? "Auto (CPU+GPU)" : _settings.DefaultDecoderPreference;
        _settings.Epg ??= new EpgOutputSettings();
        _settings.Epg.Normalize();
        _audioDownmixPreset = string.IsNullOrWhiteSpace(_settings.AudioDownmixPreset) ? "Auto Stereo" : _settings.AudioDownmixPreset;
        AudioChannelRouter.CurrentPreset = _audioDownmixPreset;
        _virtualOutputEnabled = _settings.EnableVirtualOutput;
        _virtualOutputName = string.IsNullOrWhiteSpace(_settings.VirtualOutputName) ? "Kashtrix Playout Virtual Output" : _settings.VirtualOutputName;
        _virtualOutputWidth = Math.Clamp(_settings.VirtualOutputWidth, 320, 7680);
        _virtualOutputHeight = Math.Clamp(_settings.VirtualOutputHeight, 240, 4320);
        _virtualOutputFrameRate = Math.Clamp(_settings.VirtualOutputFrameRate, 1, 120);
        _enableAutoCg = _settings.EnableAutoCg;
        _musicNowPlayingDelaySeconds = Math.Clamp(_settings.MusicNowPlayingDelaySeconds, 0, 600);
        _musicComingUpBeforeEndSeconds = Math.Clamp(_settings.MusicComingUpBeforeEndSeconds, 0, 600);
        _newsNowPlayingDelaySeconds = Math.Clamp(_settings.NewsNowPlayingDelaySeconds, 0, 600);
        _newsComingUpBeforeEndSeconds = Math.Clamp(_settings.NewsComingUpBeforeEndSeconds, 0, 600);
        _autoCgHoldSeconds = Math.Clamp(_settings.AutoCgHoldSeconds, 1, 120);
        _programNowPlayingDelaySeconds = Math.Clamp(_settings.ProgramNowPlayingDelaySeconds, 0, 600);
        _programComingUpBeforeEndSeconds = Math.Clamp(_settings.ProgramComingUpBeforeEndSeconds, 0, 600);
        _defaultLogoTemplate = _settings.DefaultLogoTemplate ?? "Logo · Station Ident";
        _defaultGraphicsTemplate = _settings.DefaultGraphicsTemplate ?? "Prime HD · Breaking News Live";
        _fullscreenMonitorIndex = Math.Clamp(_settings.FullscreenMonitor, 0, Math.Max(0, Displays.Count - 1));

        Categories.Clear();
        foreach (var cat in _settings.Categories ?? AppSettings.GetDefaultCategories())
            Categories.Add(cat);

        var playlistView = System.Windows.Data.CollectionViewSource.GetDefaultView(Playlist);
        if (playlistView is not null)
        {
            playlistView.Filter = obj =>
            {
                if (!_hidePlayedItems) return true;
                if (obj is PlaylistItem item && item.Status == "Completed") return false;
                return true;
            };
        }

        NdiProfile.LoadFrom(_settings.NdiProfile);
        var savedDeckLink = DeckLinkDevices.FirstOrDefault(x => !string.IsNullOrWhiteSpace(_settings.DeckLinkDeviceName) &&
            x.PersistentName.Equals(_settings.DeckLinkDeviceName, StringComparison.OrdinalIgnoreCase));
        _deckLinkDeviceIndex = savedDeckLink?.Index ?? Math.Clamp(_settings.DeckLinkDeviceIndex, 0, Math.Max(0, DeckLinkDevices.Count - 1));
        _deckLink.DeviceIndex = _deckLinkDeviceIndex;
        DeckLinkProfile.LoadFrom(_settings.DeckLinkProfile);
        DisplayProfile.LoadFrom(_settings.DisplayProfile);
        SynchronizePrimaryOutputProfilesToChannel();

        NdiProfile.PropertyChanged += (_, _) => RaiseOutputProfileProperties();
        DeckLinkProfile.PropertyChanged += (_, _) => RaiseOutputProfileProperties();
        DisplayProfile.PropertyChanged += (_, _) => { RaiseOutputProfileProperties(); PersistState(); };
        NdiProfile.PropertyChanged += (_, _) => PersistState();
        DeckLinkProfile.PropertyChanged += (_, _) => PersistState();

        _keepEngineRunningInTray = _settings.KeepEngineRunningInTray;
        _restoreLastPlaylist = _settings.RestoreLastPlaylist;

        CgPlaybackRuntime.GetRemainingCommercialBreakSeconds = () => GetRemainingCommercialBreakDuration();
        CgPlaybackRuntime.GetCurrentTitle = () => CurrentItem?.Title ?? string.Empty;
        CgPlaybackRuntime.GetCurrentArtist = () =>
        {
            if (CurrentItem is null) return string.Empty;
            var parsed = MediaNameParser.Parse(CurrentItem.FilePath, CurrentItem.Title);
            return !string.IsNullOrWhiteSpace(parsed.Artist) ? parsed.Artist : CurrentItem.Category;
        };
        CgPlaybackRuntime.GetCurrentProgramName = () => CurrentItem?.Title ?? string.Empty;
        CgPlaybackRuntime.GetCurrentCategory = () => CurrentItem?.Category ?? string.Empty;
        CgPlaybackRuntime.GetNextTitle = () => NextItem?.Title ?? string.Empty;
        CgPlaybackRuntime.GetNextArtist = () =>
        {
            if (NextItem is null) return string.Empty;
            var parsed = MediaNameParser.Parse(NextItem.FilePath, NextItem.Title);
            return !string.IsNullOrWhiteSpace(parsed.Artist) ? parsed.Artist : NextItem.Category;
        };
        CgPlaybackRuntime.GetNextProgramName = () =>
        {
            var nextProg = Playlist.SkipWhile(x => x != CurrentItem).Skip(1).FirstOrDefault(x => !IsCommercialCategory(x.Category));
            return nextProg?.Title ?? NextItem?.Title ?? string.Empty;
        };
        MusicPackageRuntime.IsLive = true;
        MusicPackageRuntime.GetProgress = () => CurrentProgressPercent / 100.0;
        _musicPackage = new MusicPackageRunner(
            () => _settings.MusicPackage,
            () => _settings.ProfessionalBroadcast.NextItemVideo,
            command => Ui(() => ApplyMusicPackageCommand(command)));

        try
        {
            _database = new BroadcastDatabase();
            if (_restoreLastPlaylist)
            {
                foreach (var item in _database.LoadPlaylist())
                    AttachPlaylistItem(item);
            }

            foreach (var route in _database.LoadState("output-routes", CreateDefaultOutputRoutes()))
                AttachOutputRoute(route);

            foreach (var schedule in _database.LoadState("schedules", new List<ScheduleEntry>()))
                Schedules.Add(schedule);

            var projects = _database.LoadState("cg-projects", new List<CgProject>());
            foreach (var project in projects) { CgProjectSanitizer.Sanitize(project); CgProjects.Add(project); }
            MergeMissingCgDemos();
            var activeCgId = _database.LoadState("active-cg-id", "");
            ActiveCgProject = Guid.TryParse(activeCgId, out var activeId)
                ? CgProjects.FirstOrDefault(x => x.Id == activeId) ?? CgProjects.FirstOrDefault()
                : CgProjects.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _databaseStatus = "DB ERROR · " + ex.Message;
        }

        _multiNdi.StatusChanged += (id, status) => Ui(() =>
        {
            var route = AdditionalOutputs.FirstOrDefault(x => x.Id == id);
            if (route is not null) route.Status = status;
        });
        _multiNdi.Synchronize(AdditionalOutputs);

        if (enableAutomation)
        {
            _scheduler.Due += OnScheduleDue;
            _scheduler.SetEntries(Schedules);
            StartScheduleSignalWatcher();
            StartPlaylistSignalWatcher();
            StartLocalCgCommandWatcher();
            StartSettingsSignalWatcher();
        }

        _engine.VideoFrame += OnVideo;
        _engine.AudioFrame += OnAudio;
        _engine.AudioMeterFrame += OnAudioMeter;
        _engine.ProgressChanged += OnProgress;
        _engine.StatusChanged += s => Ui(() =>
        {
            EngineStatus = s;
            if (CurrentItem is not null)
            {
                if (s.Equals("PAUSED", StringComparison.OrdinalIgnoreCase))
                {
                    CurrentItem.Status = "Paused";
                    _timelinePauseStartedUtc ??= DateTime.UtcNow;
                }
                else if (s.Equals("PLAYING", StringComparison.OrdinalIgnoreCase))
                {
                    CurrentItem.Status = "On Air";
                    if (_timelinePauseStartedUtc is DateTime pausedAt)
                    {
                        _currentItemTimelineStart = _currentItemTimelineStart.Add(DateTime.UtcNow - pausedAt);
                        _timelinePauseStartedUtc = null;
                        RefreshTimelineLayout();
                    }
                }
            }
            Raise(nameof(IsPaused));
            Raise(nameof(PlayPauseStateText));
            Raise(nameof(ProgramPlayPauseGlyph));
            if (!string.IsNullOrWhiteSpace(s)) AddRecentEvent($"ENGINE · {s}");
        });
        _engine.CurrentItemChanged += x => Ui(() => CurrentItem = x);
        _engine.ControlEventTriggered += item => Ui(() => HandleControlEvent(item));

        AddCommand = new RelayCommand(() => _ = AddFilesAsync());
        RemoveCommand = new RelayCommand(Remove, () => SelectedItem is not null);
        PlayCommand = new RelayCommand(PlayOrResume, () => Playlist.Count > 0 || _engine.IsPlaying);
        TakeSelectedCommand = new RelayCommand(TakeSelected, () => Playlist.Count > 0 && SelectedItem is not null);
        PauseCommand = new RelayCommand(ToggleCurrentPause);
        NextCommand = new RelayCommand(Next, () => Playlist.Count > 1);
        StopCommand = new RelayCommand(Stop);
        FullscreenCommand = new RelayCommand(() => FullscreenEnabled = !FullscreenEnabled);

        // A fresh workstation opens with a complete safe demo rundown/schedule instead of
        // an empty operator surface. Existing persisted playlists are never overwritten.
        if (Playlist.Count == 0)
            LoadDemoWorkspace();
        else
            SelectedItem = Playlist[0];
        RaisePlaylistSummary();
        if (_allowControllerClient)
        {
            try { OpenProgramConfidenceOutput(); }
            catch (Exception ex) { RuntimeDiagnosticsService.WriteException("PROGRAM_CONFIDENCE_OPEN_ERROR", ex); }
        }
        if (_allowControllerClient && _virtualOutputEnabled)
        {
            try { _virtualOutput.Open(_virtualOutputWidth, _virtualOutputHeight, _virtualOutputFrameRate); }
            catch { _virtualOutputEnabled = false; }
        }
        // Hardware/network outputs are operator-selectable and disabled by default on a fresh install.
        // Persisted choices are restored only after the model is fully initialized.
        if (_settings.EnableNdiOutput) NdiEnabled = true;
        if (_settings.EnableDeckLinkOutput) DeckLinkEnabled = true;
        if (_allowControllerClient && _connectToController) StartControllerClient();
        StartPlatformControlWatcher();

        // FIX42 professional I/O and HA. HA controls only transmission paths; local confidence
        // monitoring remains available on a standby node.
        _professional.ArmStateChanged += (armed, status) => Ui(() =>
        {
            Raise(nameof(ProfessionalOutputArmed));
            Raise(nameof(ProfessionalBroadcastStatus));
            AddRecentEvent($"HA / OUTPUT {(armed ? "ARMED" : "INHIBITED")} · {status}");
        });
        _professional.StandbySyncReceived += (itemId, positionSeconds) => Ui(() => SynchronizeWarmStandby(itemId, positionSeconds));
        _professional.Start(_settings.ProfessionalBroadcast, ChannelId, ChannelName, () => (CurrentItem?.Id.ToString(), _currentElapsedSeconds));

        ScteReceiverService.Instance.SpliceReceived += OnScteSpliceReceived;
        FrameComparisonService.Instance.CueFrameDetected += OnCueFrameDetected;
        FrameComparisonService.Instance.EmergencyReturnTriggered += OnEmergencyReturnTriggered;

        // FIX32 real live-debug telemetry. This is deliberately low-frequency and append-only so
        // an operator can reproduce a fault while LIVE-DEBUG.cmd captures the exact playout state.
        // Channel Schedule automation runs only in the playout process of this channel; module
        // windows (Scheduler, MAM, ...) edit the schedule but never run it.
        if (_allowControllerClient) StartScheduleRunner();
        RuntimeDiagnosticsService.Write("PLAYOUT_READY", ChannelName, new { ChannelId, PlayoutPreset, PlaylistCount = Playlist.Count });
        _runtimeDiagnosticsTimer = new System.Threading.Timer(_ => WriteRuntimeDiagnosticsSnapshot(), null, 1000, 2000);
        StartProgramCadencePump();
    }

    public ObservableCollection<PlaylistItem> Playlist { get; } = [];
    public ObservableCollection<PlaylistItem> InputSources { get; } = [];
    public ObservableCollection<OutputRoute> AdditionalOutputs { get; } = [];
    public ObservableCollection<ScheduleEntry> Schedules { get; } = [];
    public ObservableCollection<RundownTimelineItem> TimelineBlocks { get; } = [];
    public ObservableCollection<RundownBlockNode> RundownBlocks { get; } = [];
    public ObservableCollection<CgProject> CgProjects { get; } = [];
    public event EventHandler? CgProjectsChanged;
    public ObservableCollection<RecentEventEntry> RecentEvents { get; } = [];
    public IReadOnlyList<string> OutputPresets => OutputProfile.Presets;
    public IReadOnlyList<string> ScalingModes => OutputProfile.ScalingModes;
    public IReadOnlyList<double> FrameRates => OutputProfile.FrameRates;
    public IReadOnlyList<string> ScanModes => OutputProfile.ScanModes;
    public IReadOnlyList<string> AlphaModes => OutputProfile.AlphaModes;
    public IReadOnlyList<string> OutputKinds { get; } = ["NDI", "DECKLINK", "DISPLAY"];
    public IReadOnlyList<string> ScheduleRepeats { get; } = ["One Time", "Daily", "Weekdays", "Weekly", "Custom Days"];
    public IReadOnlyList<AudioDeviceOption> AudioDevices => LocalAudioOutput.GetDevices();
    public IReadOnlyList<DisplayOption> Displays { get; } = Forms.Screen.AllScreens
        .Select((screen, index) => new DisplayOption(index, screen.DeviceName, $"{screen.Bounds.Width}x{screen.Bounds.Height}"))
        .ToArray();

    public OutputProfile NdiProfile { get; } = new();
    public OutputProfile DeckLinkProfile { get; } = new();
    public OutputProfile DisplayProfile { get; } = new() { Preset = "Source", Scaling = "Fit" };

    public event Action<VideoFrameData>? VideoFrameReady;
    public event Action<VideoFrameData>? PreviewFrameReady;
    // Uncomposited confidence frames let the CG Controller render its latched snapshot
    // exactly once. Reusing the final bus frame there caused duplicate overlays and made
    // preview/program layer geometry appear different.
    public event Action<VideoFrameData>? ProgramBaseFrameReady;
    public event Action<VideoFrameData>? PreviewBaseFrameReady;
    public event Action? PreviewCleared;
    public event Action<bool>? FullscreenStateRequested;
    public event Action<string, PlaylistItem?>? AutomationSignalRaised;

    public bool ProfessionalOutputArmed
    {
        get => _professional.OutputArmed;
        set { _professional.SetManualArm(value); _settings.ProfessionalBroadcast.OutputArmed = value; Raise(); Raise(nameof(ProfessionalBroadcastStatus)); PersistState(); }
    }
    public string ProfessionalBroadcastStatus => _professional.CapabilityStatus();

    public PlaylistItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            var previous = _selectedItem;
            if (!Set(ref _selectedItem, value)) return;
            StopPreviewPlayback();

            if (previous is not null && !ReferenceEquals(previous, CurrentItem) && previous.Status == "Cued")
                previous.Status = "Ready";
            if (value is not null && !ReferenceEquals(value, CurrentItem))
                value.Status = "Cued";

            _previewSeekSeconds = 0;
            Raise(nameof(PreviewSeekSeconds));
            Raise(nameof(PreviewSeekMaximum));
            Raise(nameof(PreviewSeekText));
            Raise(nameof(PreviewDurationText));
            Raise(nameof(CanPreviewSeek));
            Raise(nameof(SelectedRemainingTimeText));
            RemoveCommand.RaiseCanExecuteChanged();
            TakeSelectedCommand.RaiseCanExecuteChanged();
            PlayCommand.RaiseCanExecuteChanged();
            Raise(nameof(StatusDetail));
            RaiseNextItems();
            RefreshTimelineLayout();

            // Avoid opening a second decoder against the same currently-on-air live source.
            // That duplicate open can stall device-backed/live inputs when the operator clicks
            // the running item. Reuse the current program frame for Preview in that case.
            if (value is not null && ReferenceEquals(value, CurrentItem) && value.IsLiveSource)
            {
                if (_lastProgramSourceFrame is not null)
                {
                    PreviewBaseFrameReady?.Invoke(_lastProgramSourceFrame);
                    var previewFrame = PreviewCgProject is null
                        ? _lastProgramSourceFrame
                        : _previewCgCompositor.Composite(_lastProgramSourceFrame, PreviewCgProject, GetCgBusElapsed(preview: true));
                    PreviewFrameReady?.Invoke(previewFrame);
                }
                return;
            }

            _ = LoadPreviewAsync(value, 0, debounce: false);
        }
    }

    public PlaylistItem? CurrentItem
    {
        get => _currentItem;
        private set
        {
            var previousCurrent = _currentItem;
            var previousSelected = SelectedItem;
            if (!Set(ref _currentItem, value)) return;

            if (previousCurrent is not null && (previousCurrent.Status == "On Air" || previousCurrent.Status == "Paused"))
            {
                if (_markPlayedItemsCompleted && !_manualStopRequested)
                    previousCurrent.Status = "Completed";
                else
                    previousCurrent.Status = "Ready";
                _asRun.Complete(previousCurrent, _manualStopRequested ? "STOPPED" : "COMPLETED");
                if (_autoRemovePlayedItems && !_manualStopRequested && previousCurrent.Status == "Completed")
                {
                    var isLooped = previousCurrent.IsLoop || previousCurrent.IsBlockLoop || IsItemInLoopedBlock(previousCurrent);
                    if (!isLooped)
                    {
                        var toRemove = previousCurrent;
                        Ui(() => Playlist.Remove(toRemove));
                    }
                }
            }
            if (value is not null)
            {
                _manualStopRequested = false;
                value.Status = "On Air";
                _asRun.Start(ChannelId, value);
                AddRecentEvent($"TAKE · {value.Title}");
            }
            else if (previousCurrent is not null)
            {
                AddRecentEvent($"ENDED · {previousCurrent.Title}");
                _manualStopRequested = false;
            }

            // Clear any automatic Now/Next overlay before loading a dedicated rundown CG event.
            // Doing this after ApplyRundownCgTransition used to immediately turn the newly selected
            // CG demo off when an Auto-CG was active on the preceding media item.
            if (_autoCgProjectActive && ActiveCgProject is not null)
            {
                ActiveCgProject.OnAir = false;
                ActiveCgProject = null;
                _autoCgProjectActive = false;
            }
            ApplyRundownCgTransition(previousCurrent, value);
            _autoNowPlayingShown = false;
            _autoComingUpShown = false;
            _autoCgOnAirUntilSeconds = -1;

            _currentElapsedSeconds = 0;
            _programSeekSeconds = 0;
            _timelinePauseStartedUtc = null;
            if (value is not null) _currentItemTimelineStart = DateTime.Now;
            RaisePlaybackCounters();
            Raise(nameof(ProgramSeekSeconds));
            Raise(nameof(ProgramSeekMaximum));
            Raise(nameof(ProgramSeekText));
            Raise(nameof(ProgramDurationText));
            Raise(nameof(CanProgramSeek));
            Raise(nameof(StatusDetail));
            RaiseNextItems();
            RefreshTimelineLayout();

            // Keep preview on the next event after a successful TAKE/automation advance.
            if (value is not null && (previousSelected is null || ReferenceEquals(previousSelected, value)))
            {
                var index = Playlist.IndexOf(value);
                if (index >= 0 && index + 1 < Playlist.Count)
                    SelectedItem = Playlist[index + 1];
                else
                    SelectedItem = null;
            }

            if (value is null)
            {
                if (_playlistReloadPending) ReloadPlaylistFromDatabase();
                if (FillerEnabled) ScheduleFillerRetry();
            }
        }
    }

    private bool IsItemInLoopedBlock(PlaylistItem item)
    {
        var block = RundownBlocks.FirstOrDefault(b => b.Items.Contains(item));
        return block?.IsLoop ?? false;
    }

    private static bool IsEditorialForNowNext(PlaylistItem item) => item.IsEditorialItem && item.EventType is not "NOTE";

    public PlaylistItem? NextItem
    {
        get
        {
            var currentIndex = CurrentItem is null ? -1 : Playlist.IndexOf(CurrentItem);
            if (currentIndex >= 0)
                return Playlist.Skip(currentIndex + 1).FirstOrDefault(IsEditorialForNowNext);
            if (SelectedItem is not null && !ReferenceEquals(SelectedItem, CurrentItem) && IsEditorialForNowNext(SelectedItem)) return SelectedItem;
            return Playlist.FirstOrDefault(x => !ReferenceEquals(x, CurrentItem) && IsEditorialForNowNext(x));
        }
    }
    public PlaylistItem? UpNextItem
    {
        get
        {
            var next = NextItem;
            if (next is null) return null;
            var index = Playlist.IndexOf(next);
            return index < 0 ? null : Playlist.Skip(index + 1).FirstOrDefault(IsEditorialForNowNext);
        }
    }
    public string NowPlayingAutoText => CurrentItem is null ? "OFF AIR" : MediaNameParser.Parse(CurrentItem.FilePath, CurrentItem.Title).Display;
    public string NextAutoText => NextItem is null ? "--" : MediaNameParser.Parse(NextItem.FilePath, NextItem.Title).Display;
    public string UpNextAutoText => UpNextItem is null ? "--" : MediaNameParser.Parse(UpNextItem.FilePath, UpNextItem.Title).Display;

    public string ChannelName
    {
        get => _channelName;
        set => Set(ref _channelName, string.IsNullOrWhiteSpace(value) ? "KASHTRIX NEWS HD" : value.Trim());
    }

    public string ChannelId
    {
        get => _channelId;
        set
        {
            if (!Set(ref _channelId, string.IsNullOrWhiteSpace(value) ? _channelId : value.Trim().ToUpperInvariant())) return;
            SaveSettings();
            if (_allowControllerClient) StartLocalCgCommandWatcher();
            if (_scheduleRunner is not null) StartScheduleRunner(); // follow the new channel's schedule
        }
    }

    // ---- Channel Schedule automation (playout process only) ----------------------------------

    private ScheduleRunner? _scheduleRunner;
    // Immutable copy of the CG library for the schedule's worker-thread lookups (no collection locking).
    private CgProject[] _cgProjectSnapshot = [];
    private bool _cgSnapshotHooked;

    private void StartScheduleRunner()
    {
        try
        {
            if (!_cgSnapshotHooked)
            {
                _cgSnapshotHooked = true;
                Volatile.Write(ref _cgProjectSnapshot, CgProjects.ToArray());
                CgProjects.CollectionChanged += (_, _) => Volatile.Write(ref _cgProjectSnapshot, CgProjects.ToArray());
            }
            var overlays = new CgScheduleRunner(
                name => Array.Find(Volatile.Read(ref _cgProjectSnapshot), p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)),
                command => Ui(() => ApplyScheduledCg(command)));
            _scheduleRunner?.Dispose();
            _scheduleRunner = ScheduleRunner.ForChannel(_engine, ChannelId, message => Ui(() => AddRecentEvent(message)), overlays);
            _scheduleRunner.StateChanged += () => Ui(RaiseScheduleState);
            RaiseScheduleState();
        }
        catch (Exception ex)
        {
            _scheduleRunner = null;
            RuntimeDiagnosticsService.WriteException("SCHEDULE_RUNNER_START_ERROR", ex);
        }
    }

    // ---- Music Now/Next package (song info + progress, NEXT / LATER video box) ---------------

    // Created in the constructor; null only while earlier constructor code runs.
    private readonly MusicPackageRunner? _musicPackage;

    /// <summary>Runs on the UI thread so the playlist is read safely; the runner only reacts to real changes.</summary>
    private void RefreshMusicPackage()
    {
        if (_musicPackage is null) return;
        var current = CurrentItem;
        _musicPackage.Prepare(current, current is null ? null : NextItem, current is null ? null : UpNextItem);
    }

    /// <summary>Called by the settings window after a save.</summary>
    public void ApplyMusicPackageSettings()
    {
        PersistSettings();
        _musicPackage?.Stop();
        RefreshMusicPackage();
        AddRecentEvent(_settings.MusicPackage.Enabled ? "MUSIC PACKAGE · ON" : "MUSIC PACKAGE · OFF");
    }

    /// <summary>Package graphics use the same Program CG layer stack as the CG Controller and the schedule.</summary>
    private void ApplyMusicPackageCommand(MusicPackageCommand command)
    {
        CgProject? project = null;
        if (command.Play)
        {
            var template = CgProjects.FirstOrDefault(x => string.Equals(x.Name, command.TemplateName, StringComparison.OrdinalIgnoreCase))
                ?? MusicPackageTemplates.Create().FirstOrDefault(x => string.Equals(x.Name, command.TemplateName, StringComparison.OrdinalIgnoreCase));
            if (template is null)
            {
                AddRecentEvent("MUSIC PACKAGE · TEMPLATE NOT FOUND · " + command.TemplateName);
                return;
            }
            project = JsonSerializer.Deserialize<CgProject>(JsonSerializer.Serialize(template));
            if (project is null) return;
        }
        ApplyRemoteCg(new CgRemoteCommand
        {
            Action = command.Play ? "PLAY" : "STOP",
            Bus = "PROGRAM",
            Layer = command.Layer,
            Project = project
        });
    }

    /// <summary>Scheduled overlays use the same Program CG layer path as the CG Controller.</summary>
    private void ApplyScheduledCg(CgScheduleCommand command)
    {
        CgProject? project = null;
        if (command.Play)
        {
            var template = CgProjects.FirstOrDefault(x => string.Equals(x.Name, command.ProjectName, StringComparison.OrdinalIgnoreCase));
            if (template is null) return;
            project = JsonSerializer.Deserialize<CgProject>(JsonSerializer.Serialize(template));
            if (project is null) return;
        }
        ApplyRemoteCg(new CgRemoteCommand
        {
            Action = command.Play ? "PLAY" : "STOP",
            Bus = "PROGRAM",
            Layer = command.Layer,
            Project = project
        });
    }

    private void RaiseScheduleState()
    {
        Raise(nameof(ScheduleAutomationEnabled));
        Raise(nameof(ScheduleStatusText));
        Raise(nameof(ScheduleNextEventText));
    }

    /// <summary>True only in the playout process that runs this channel's schedule.</summary>
    public bool CanRunSchedule => _scheduleRunner is not null;

    public bool ScheduleAutomationEnabled
    {
        get => _scheduleRunner?.IsActive == true;
        set { _scheduleRunner?.SetAutomationEnabled(value); RaiseScheduleState(); }
    }

    public string ScheduleStatusText => _scheduleRunner?.StatusText ?? "SCHEDULE · runs in the Playout app";

    public string ScheduleNextEventText => _scheduleRunner?.IsActive == true && _scheduleRunner.NextEvent is { } next
        ? $"NEXT {next.Start:HH:mm:ss} · {ScheduleRules.KindLabel(next.Item.Kind)} · {next.Item.Name}"
        : string.Empty;

    public void ResumeSchedule() => _scheduleRunner?.ResumeAutomation();

    public bool CutScheduledLive() => _scheduleRunner?.CutLive() == true;

    public string PlayoutPreset
    {
        get => _playoutPreset;
        set
        {
            var normalized = ChannelFormatPreset.Resolve(value).Name;
            if (!Set(ref _playoutPreset, normalized)) return;
            SynchronizePrimaryOutputProfilesToChannel();
            if (_allowControllerClient)
            {
                try { OpenProgramConfidenceOutput(); }
                catch (Exception ex) { RuntimeDiagnosticsService.WriteException("PROGRAM_CONFIDENCE_REOPEN_ERROR", ex); }
            }
            Raise(nameof(ProgramFormatText));
            SaveSettings();
        }
    }
    public string ProgramFormatText => ChannelFormatPreset.Resolve(PlayoutPreset).DisplayText;

    private void SynchronizePrimaryOutputProfilesToChannel()
    {
        foreach (var profile in new[] { NdiProfile, DeckLinkProfile, DisplayProfile })
            ChannelFormatPreset.ApplyToProfile(profile, _playoutPreset);
    }

    private void OpenProgramConfidenceOutput()
    {
        var spec = ChannelFormatPreset.Resolve(_playoutPreset);
        _programConfidenceOutput.Open(spec.Width, spec.Height, spec.FramesPerSecond);
    }

    public bool FillerEnabled
    {
        get => _fillerEnabled;
        set { if (Set(ref _fillerEnabled, value)) { SaveSettings(); if (value && CurrentItem is null) BeginFillerIfIdle(); } }
    }

    public string FillerFolder
    {
        get => _fillerFolder;
        set { if (Set(ref _fillerFolder, value ?? string.Empty)) SaveSettings(); }
    }

    public string ControllerHost { get => _controllerHost; set { if (Set(ref _controllerHost, string.IsNullOrWhiteSpace(value) ? "127.0.0.1" : value.Trim())) SaveSettings(); } }
    public int ControllerPort { get => _controllerPort; set { if (Set(ref _controllerPort, Math.Clamp(value, 1024, 65535))) SaveSettings(); } }
    public bool ConnectToController { get => _connectToController; set { if (Set(ref _connectToController, value)) { SaveSettings(); if (_allowControllerClient) RestartControllerClient(); } } }
    public string ControllerStatus { get => _controllerStatus; private set => Set(ref _controllerStatus, value); }

    public string EngineStatus
    {
        get => _engineStatus;
        private set
        {
            if (!Set(ref _engineStatus, value)) return;
            Raise(nameof(StatusDetail));
        }
    }

    public string FfmpegVersion => FfmpegRuntime.IsInitialized ? FfmpegRuntime.Version : "Not loaded";
    public ObservableCollection<DeckLinkDeviceOption> DeckLinkDevices { get; } = new(DeckLinkOutputAdapter.EnumerateDevices());
    public int DeckLinkDeviceIndex
    {
        get => _deckLinkDeviceIndex;
        set
        {
            value = Math.Clamp(value, 0, Math.Max(0, DeckLinkDevices.Count - 1));
            if (!Set(ref _deckLinkDeviceIndex, value)) return;
            _deckLink.DeviceIndex = value;
            _deckLink.Close();
            _settings.DeckLinkDeviceName = DeckLinkDevices.FirstOrDefault(x => x.Index == value)?.PersistentName ?? string.Empty;
            SaveSettings();
            Raise(nameof(DeckLinkDeviceText));
        }
    }
    public string DeckLinkDeviceText => DeckLinkDevices.FirstOrDefault(x => x.Index == DeckLinkDeviceIndex)?.Label ?? "DeckLink hardware scan unavailable";
    public void RefreshDeckLinkDevices()
    {
        var currentName = _settings.DeckLinkDeviceName;
        var devices = DeckLinkOutputAdapter.EnumerateDevices();
        DeckLinkDevices.Clear();
        foreach (var device in devices) DeckLinkDevices.Add(device);
        var selected = DeckLinkDevices.FirstOrDefault(x => !string.IsNullOrWhiteSpace(currentName) && x.PersistentName.Equals(currentName, StringComparison.OrdinalIgnoreCase))
                       ?? DeckLinkDevices.FirstOrDefault();
        DeckLinkDeviceIndex = selected?.Index ?? 0;
        Raise(nameof(DeckLinkDevices));
        Raise(nameof(DeckLinkDeviceText));
    }
    public string DeckLinkBuildStatus => _deckLink.IsSupportedBuild
        ? (_deckLink.IsOpen ? "SDK READY · " + _deckLink.OutputStats : "SDK READY")
        : "SDK INTEROP REQUIRED";
    public string NdiRuntimeVersion => _ndi.RuntimeVersion;
    public string NdiRuntimePath => string.IsNullOrWhiteSpace(_ndi.RuntimePath) ? "App-local / system runtime" : _ndi.RuntimePath;

    public string NdiName
    {
        get => _ndiName;
        set => Set(ref _ndiName, value);
    }

    public string NdiStatus
    {
        get => _ndiStatus;
        private set => Set(ref _ndiStatus, value);
    }

    public bool LocalAudioEnabled
    {
        get => _localAudioEnabled;
        set { if (Set(ref _localAudioEnabled, value)) SaveSettings(); }
    }

    public int ProgramAudioDeviceNumber
    {
        get => _programAudioDeviceNumber;
        set
        {
            if (!Set(ref _programAudioDeviceNumber, value)) return;
            _settings.ProgramAudioDeviceNumber = value;
            try { _localAudio.Dispose(); } catch { }
            _localAudio = new LocalAudioOutput(value);
            SaveSettings();
        }
    }
    public int PreviewAudioDeviceNumber
    {
        get => _previewAudioDeviceNumber;
        set
        {
            if (!Set(ref _previewAudioDeviceNumber, value)) return;
            _settings.PreviewAudioDeviceNumber = value;
            try { _previewAudio.Dispose(); } catch { }
            _previewAudio = new LocalAudioOutput(value);
            SaveSettings();
        }
    }
    public bool EnableAutoCg { get => _enableAutoCg; set { if (Set(ref _enableAutoCg, value)) { _settings.EnableAutoCg = value; Raise(nameof(AutoCgStatusText)); SaveSettings(); } } }
    public double MusicNowPlayingDelaySeconds { get => _musicNowPlayingDelaySeconds; set { if (Set(ref _musicNowPlayingDelaySeconds, Math.Clamp(value,0,600))) { _settings.MusicNowPlayingDelaySeconds = _musicNowPlayingDelaySeconds; SaveSettings(); } } }
    public double MusicComingUpBeforeEndSeconds { get => _musicComingUpBeforeEndSeconds; set { if (Set(ref _musicComingUpBeforeEndSeconds, Math.Clamp(value,0,600))) { _settings.MusicComingUpBeforeEndSeconds = _musicComingUpBeforeEndSeconds; SaveSettings(); } } }
    public double NewsNowPlayingDelaySeconds { get => _newsNowPlayingDelaySeconds; set { if (Set(ref _newsNowPlayingDelaySeconds, Math.Clamp(value,0,600))) { _settings.NewsNowPlayingDelaySeconds = _newsNowPlayingDelaySeconds; SaveSettings(); } } }
    public double NewsComingUpBeforeEndSeconds { get => _newsComingUpBeforeEndSeconds; set { if (Set(ref _newsComingUpBeforeEndSeconds, Math.Clamp(value,0,600))) { _settings.NewsComingUpBeforeEndSeconds = _newsComingUpBeforeEndSeconds; SaveSettings(); } } }
    public double AutoCgHoldSeconds { get => _autoCgHoldSeconds; set { if (Set(ref _autoCgHoldSeconds, Math.Clamp(value,1,120))) { _settings.AutoCgHoldSeconds = _autoCgHoldSeconds; SaveSettings(); } } }
    public string AutoCgStatusText => EnableAutoCg ? "AUTO CG ARMED · MUSIC + NEWS + ADS" : "AUTO CG OFF";

    public bool EnableAutoBackIn
    {
        get => _settings.EnableAutoBackIn;
        set { if (_settings.EnableAutoBackIn != value) { _settings.EnableAutoBackIn = value; Raise(nameof(EnableAutoBackIn)); SaveSettings(); } }
    }
    public double BackInTriggerLeadSeconds
    {
        get => _settings.BackInTriggerLeadSeconds;
        set { if (Math.Abs(_settings.BackInTriggerLeadSeconds - value) > 0.001) { _settings.BackInTriggerLeadSeconds = value; Raise(nameof(BackInTriggerLeadSeconds)); SaveSettings(); } }
    }
    public string DefaultBackInTemplate
    {
        get => _settings.DefaultBackInTemplate ?? "Commercial · Back In Countdown Lower Third";
        set { if (_settings.DefaultBackInTemplate != value) { _settings.DefaultBackInTemplate = value; Raise(nameof(DefaultBackInTemplate)); SaveSettings(); } }
    }

    public bool EnableMasterOutputVolume
    {
        get => _enableMasterOutputVolume;
        set { if (Set(ref _enableMasterOutputVolume, value)) SaveSettings(); }
    }

    public int MasterOutputVolumePercent
    {
        get => _masterOutputVolumePercent;
        set
        {
            if (!Set(ref _masterOutputVolumePercent, Math.Clamp(value, 0, 200))) return;
            Raise(nameof(MasterOutputVolumeText)); SaveSettings();
        }
    }
    public string MasterOutputVolumeText => MasterOutputMuted ? "MUTED" : $"{MasterOutputVolumePercent}%";
    public bool MasterOutputMuted
    {
        get => _masterOutputMuted;
        set { if (!Set(ref _masterOutputMuted, value)) return; Raise(nameof(MasterOutputVolumeText)); AddRecentEvent(value ? "MASTER AUDIO · MUTED" : "MASTER AUDIO · UNMUTED"); SaveSettings(); }
    }
    public string MasterMuteShortcut { get => _settings.MasterMuteShortcut; set { _settings.MasterMuteShortcut = string.IsNullOrWhiteSpace(value) ? "Ctrl+M" : value.Trim(); Raise(); SaveSettings(); } }
    public string MasterVolumeUpShortcut { get => _settings.MasterVolumeUpShortcut; set { _settings.MasterVolumeUpShortcut = string.IsNullOrWhiteSpace(value) ? "Ctrl+Up" : value.Trim(); Raise(); SaveSettings(); } }
    public string MasterVolumeDownShortcut { get => _settings.MasterVolumeDownShortcut; set { _settings.MasterVolumeDownShortcut = string.IsNullOrWhiteSpace(value) ? "Ctrl+Down" : value.Trim(); Raise(); SaveSettings(); } }
    public void ToggleMasterMute() => MasterOutputMuted = !MasterOutputMuted;
    public void ChangeMasterVolume(int delta) => MasterOutputVolumePercent = Math.Clamp(MasterOutputVolumePercent + delta, 0, 200);

    public bool EnableVideoProcessing { get => _enableVideoProcessing; set { if (Set(ref _enableVideoProcessing, value)) { Raise(nameof(VideoProcessingStatus)); SaveSettings(); } } }
    public int VideoBrightness { get => _videoBrightness; set { if (Set(ref _videoBrightness, Math.Clamp(value,-100,100))) VideoFxChanged(); } }
    public int VideoContrast { get => _videoContrast; set { if (Set(ref _videoContrast, Math.Clamp(value,0,200))) VideoFxChanged(); } }
    public int VideoSaturation { get => _videoSaturation; set { if (Set(ref _videoSaturation, Math.Clamp(value,0,200))) VideoFxChanged(); } }
    public double VideoGamma { get => _videoGamma; set { if (Set(ref _videoGamma, Math.Clamp(value,0.10,4.0))) VideoFxChanged(); } }
    public int VideoHueDegrees { get => _videoHueDegrees; set { if (Set(ref _videoHueDegrees, Math.Clamp(value,-180,180))) VideoFxChanged(); } }
    public double VideoExposureStops { get => _videoExposureStops; set { if (Set(ref _videoExposureStops, Math.Clamp(value,-4.0,4.0))) VideoFxChanged(); } }
    public int VideoTemperature { get => _videoTemperature; set { if (Set(ref _videoTemperature, Math.Clamp(value,-100,100))) VideoFxChanged(); } }
    public int VideoTint { get => _videoTint; set { if (Set(ref _videoTint, Math.Clamp(value,-100,100))) VideoFxChanged(); } }
    public int VideoSharpness { get => _videoSharpness; set { if (Set(ref _videoSharpness, Math.Clamp(value,0,100))) VideoFxChanged(); } }
    public int VideoBlackLevel { get => _videoBlackLevel; set { if (Set(ref _videoBlackLevel, Math.Clamp(value,-100,100))) VideoFxChanged(); } }
    public int VideoWhiteLevel { get => _videoWhiteLevel; set { if (Set(ref _videoWhiteLevel, Math.Clamp(value,-100,100))) VideoFxChanged(); } }
    public int VideoRedGainPercent { get => _videoRedGainPercent; set { if (Set(ref _videoRedGainPercent, Math.Clamp(value,0,200))) VideoFxChanged(); } }
    public int VideoGreenGainPercent { get => _videoGreenGainPercent; set { if (Set(ref _videoGreenGainPercent, Math.Clamp(value,0,200))) VideoFxChanged(); } }
    public int VideoBlueGainPercent { get => _videoBlueGainPercent; set { if (Set(ref _videoBlueGainPercent, Math.Clamp(value,0,200))) VideoFxChanged(); } }
    public string VideoProcessingStatus => EnableVideoProcessing ? $"ON · B {VideoBrightness:+#;-#;0} · C {VideoContrast}% · S {VideoSaturation}% · G {VideoGamma:0.00}" : "BYPASSED";
    public void ResetVideoProcessing()
    {
        _videoBrightness=0; _videoContrast=100; _videoSaturation=100; _videoGamma=1.0; _videoHueDegrees=0; _videoExposureStops=0;
        _videoTemperature=0; _videoTint=0; _videoSharpness=0; _videoBlackLevel=0; _videoWhiteLevel=0;
        _videoRedGainPercent=100; _videoGreenGainPercent=100; _videoBlueGainPercent=100;
        Raise(nameof(VideoBrightness)); Raise(nameof(VideoContrast)); Raise(nameof(VideoSaturation)); Raise(nameof(VideoGamma)); Raise(nameof(VideoHueDegrees));
        Raise(nameof(VideoExposureStops)); Raise(nameof(VideoTemperature)); Raise(nameof(VideoTint)); Raise(nameof(VideoSharpness)); Raise(nameof(VideoBlackLevel)); Raise(nameof(VideoWhiteLevel));
        Raise(nameof(VideoRedGainPercent)); Raise(nameof(VideoGreenGainPercent)); Raise(nameof(VideoBlueGainPercent)); Raise(nameof(VideoProcessingStatus)); SaveSettings();
    }
    private void VideoFxChanged()
    {
        // An operator moving a grading control expects Program to change immediately.
        // Auto-enable processing on the first adjustment; BYPASS remains available via
        // the explicit Enable Processing toggle.
        if (!_enableVideoProcessing)
        {
            _enableVideoProcessing = true;
            Raise(nameof(EnableVideoProcessing));
        }
        Raise(nameof(VideoProcessingStatus));
        SaveSettings();
    }
    private VideoOutputAdjustments CurrentVideoOutputAdjustments => new(EnableVideoProcessing, VideoBrightness, VideoContrast, VideoSaturation, VideoGamma, VideoHueDegrees, VideoExposureStops, VideoTemperature, VideoTint, VideoSharpness, VideoBlackLevel, VideoWhiteLevel, VideoRedGainPercent, VideoGreenGainPercent, VideoBlueGainPercent);

    public bool EnableQcOnImport { get => _enableQcOnImport; set { if (Set(ref _enableQcOnImport, value)) SaveSettings(); } }
    public MediaQcPreset QcPreset => _settings.QcPreset ??= new MediaQcPreset();

    public bool VirtualOutputEnabled
    {
        get => _virtualOutputEnabled;
        set
        {
            if (!Set(ref _virtualOutputEnabled, value)) return;
            if (value)
            {
                try { _virtualOutput.Open(_virtualOutputWidth, _virtualOutputHeight, _virtualOutputFrameRate); }
                catch (Exception ex) { _virtualOutputEnabled = false; Raise(nameof(VirtualOutputEnabled)); WpfMessageBox.Show(ex.Message, "Virtual Output"); }
            }
            else _virtualOutput.Close();
            Raise(nameof(VirtualOutputStatus)); SaveSettings();
        }
    }
    public string VirtualOutputStatus => _virtualOutputEnabled ? _virtualOutput.Status : "OFF";
    public string VirtualOutputName { get => _virtualOutputName; set { if (Set(ref _virtualOutputName, string.IsNullOrWhiteSpace(value) ? "Kashtrix Playout Virtual Output" : value.Trim())) SaveSettings(); } }
    public int VirtualOutputWidth { get => _virtualOutputWidth; set { if (Set(ref _virtualOutputWidth, Math.Clamp(value,320,7680))) RestartVirtualOutput(); } }
    public int VirtualOutputHeight { get => _virtualOutputHeight; set { if (Set(ref _virtualOutputHeight, Math.Clamp(value,240,4320))) RestartVirtualOutput(); } }
    public double VirtualOutputFrameRate { get => _virtualOutputFrameRate; set { if (Set(ref _virtualOutputFrameRate, Math.Clamp(value,1,120))) RestartVirtualOutput(); } }

    private void RestartVirtualOutput()
    {
        if (_virtualOutputEnabled) { try { _virtualOutput.Open(_virtualOutputWidth,_virtualOutputHeight,_virtualOutputFrameRate); } catch { } }
        Raise(nameof(VirtualOutputStatus)); SaveSettings();
    }

    public int FullscreenMonitorIndex
    {
        get => _fullscreenMonitorIndex;
        set => Set(ref _fullscreenMonitorIndex, Math.Clamp(value, 0, Math.Max(0, Displays.Count - 1)));
    }

    public bool FullscreenEnabled
    {
        get => _fullscreenEnabled;
        set
        {
            if (!Set(ref _fullscreenEnabled, value)) return;
            FullscreenStateRequested?.Invoke(value);
        }
    }

    public bool KeepEngineRunningInTray
    {
        get => _keepEngineRunningInTray;
        set { if (Set(ref _keepEngineRunningInTray, value)) PersistState(); }
    }

    public bool RestoreLastPlaylist
    {
        get => _restoreLastPlaylist;
        set { if (Set(ref _restoreLastPlaylist, value)) PersistState(); }
    }

    public string DatabaseStatus => _databaseStatus;
    public string DatabasePathText => BroadcastDatabase.DatabasePath;

    public CgProject? PreviewCgProject
    {
        get => _previewCgProject;
        private set
        {
            if (!Set(ref _previewCgProject, value)) return;
            if (value is not null)
            {
                _previewCgStartedUtc = DateTime.UtcNow;
                StartCgPreviewLoop();
            }
            else
            {
                StopCgPreviewLoop();
                if (_lastPreviewBaseFrame is not null)
                {
                    PreviewFrameReady?.Invoke(_lastPreviewBaseFrame);
                    if (_allowControllerClient && _connectToController)
                        _controllerCuePreview = CreateControllerThumbnail(_lastPreviewBaseFrame);
                }
                else
                {
                    _controllerCuePreview = "";
                    PreviewCleared?.Invoke();
                    if (SelectedItem is not null)
                    {
                        _ = LoadPreviewAsync(SelectedItem, PreviewSeekSeconds, debounce: false);
                    }
                }
            }
            Raise(nameof(PreviewCgStatusText));
        }
    }

    private void StartCgPreviewLoop()
    {
        StopCgPreviewLoop();
        var cts = new CancellationTokenSource();
        _cgPreviewLoopCts = cts;
        _cgPreviewLoopTask = Task.Run(() => RunCgPreviewLoop(cts.Token));
    }

    private void StopCgPreviewLoop()
    {
        var old = Interlocked.Exchange(ref _cgPreviewLoopCts, null);
        if (old != null)
        {
            try { old.Cancel(); old.Dispose(); } catch { }
        }
    }

    private async Task RunCgPreviewLoop(CancellationToken ct)
    {
        const int targetFps = 50;
        var intervalMs = 1000 / targetFps;
        var lastThumbUtc = DateTime.MinValue;

        try
        {
            while (!ct.IsCancellationRequested && Volatile.Read(ref _disposed) == 0)
            {
                if (_isPreviewPlaying && !_isPreviewPaused)
                {
                    await Task.Delay(intervalMs, ct).ConfigureAwait(false);
                    continue;
                }

                var project = PreviewCgProject;
                if (project is null) break;

                var baseFrame = _lastPreviewBaseFrame ?? _lastProgramSourceFrame;
                if (baseFrame is null)
                {
                    if (_cachedBlackBaseFrame is null)
                    {
                        var stride = 1920 * 4;
                        var bytes = new byte[stride * 1080];
                        for (var i = 3; i < bytes.Length; i += 4) bytes[i] = 255;
                        _cachedBlackBaseFrame = new VideoFrameData(bytes, 1920, 1080, stride, 0, 50, 1);
                    }
                    baseFrame = _cachedBlackBaseFrame;
                }

                var elapsed = GetCgBusElapsed(preview: true);
                PreviewBaseFrameReady?.Invoke(baseFrame);
                var previewFrame = _previewCgCompositor.Composite(baseFrame, project, elapsed);
                PreviewFrameReady?.Invoke(previewFrame);

                if (_allowControllerClient && _connectToController && (DateTime.UtcNow - lastThumbUtc).TotalMilliseconds >= 250)
                {
                    lastThumbUtc = DateTime.UtcNow;
                    _controllerCuePreview = CreateControllerThumbnail(previewFrame);
                }

                await Task.Delay(intervalMs, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            RuntimeDiagnosticsService.WriteException("CG_PREVIEW_LOOP_ERROR", ex);
        }
    }
    public string PreviewCgStatusText => PreviewCgProject is null ? "PREVIEW CG OFF" : "PREVIEW CG · " + PreviewCgProject.Name;

    public CgProject? ActiveCgProject
    {
        get => _activeCgProject;
        set
        {
            if (!Set(ref _activeCgProject, value)) return;
            if (value is not null) _activeCgStartedUtc = DateTime.UtcNow;
            Raise(nameof(CgStatusText));
            PersistState();
        }
    }

    public string CgStatusText
    {
        get
        {
            lock (_controllerCgStackGate) if (_controllerProgramStack.Count > 0) return $"CG STACK ON AIR · {_controllerProgramStack.Count}";
            return ActiveCgProject is null ? "CG OFF" : (ActiveCgProject.OnAir ? "CG ON AIR · " : "CG READY · ") + ActiveCgProject.Name;
        }
    }
    public bool CgEnabled
    {
        get { lock (_controllerCgStackGate) if (_controllerProgramStack.Any(x => x.OnAir)) return true; return ActiveCgProject?.OnAir ?? false; }
        set
        {
            if (ActiveCgProject is null) return;
            ActiveCgProject.OnAir = value;
            if (value) _activeCgStartedUtc = DateTime.UtcNow;
            Raise(nameof(CgEnabled));
            Raise(nameof(CgStatusText));
            PersistState();
        }
    }

    public bool IsPaused => _engine.IsPaused;
    public string PlayPauseStateText => _engine.IsPaused ? "RESUME" : "PAUSE";
    public string ProgramPlayPauseGlyph => _engine.IsPaused ? "▶" : "Ⅱ";
    public bool IsPreviewPlaying => _isPreviewPlaying;
    public string PreviewPlayPauseGlyph => _isPreviewPlaying && !_isPreviewPaused ? "Ⅱ" : "▶";
    public bool MarkPlayedItemsCompleted { get => _markPlayedItemsCompleted; set { if (Set(ref _markPlayedItemsCompleted, value)) SaveSettings(); } }
    public bool AutoRemovePlayedItems { get => _autoRemovePlayedItems; set { if (Set(ref _autoRemovePlayedItems, value)) SaveSettings(); } }
    public bool HidePlayedItems { get => _hidePlayedItems; set { if (Set(ref _hidePlayedItems, value)) { SaveSettings(); System.Windows.Data.CollectionViewSource.GetDefaultView(Playlist)?.Refresh(); } } }
    public bool DimCompletedItems { get => _dimCompletedItems; set { if (Set(ref _dimCompletedItems, value)) SaveSettings(); } }
    public int PlaylistRowHeight { get => _playlistRowHeight; set { if (Set(ref _playlistRowHeight, Math.Clamp(value, 22, 96))) SaveSettings(); } }
    public double PlaylistFontSize { get => _playlistFontSize; set { if (Set(ref _playlistFontSize, Math.Clamp(value, 8.0, 28.0))) SaveSettings(); } }
    public string PlaylistDefaultRowColor { get => _playlistDefaultRowColor; set { if (Set(ref _playlistDefaultRowColor, value?.Trim() ?? string.Empty)) { ApplyGlobalPlaylistAppearance(); SaveSettings(); } } }
    public string PlaylistDefaultTextColor { get => _playlistDefaultTextColor; set { if (Set(ref _playlistDefaultTextColor, string.IsNullOrWhiteSpace(value) ? "#D9DEE3" : value.Trim())) { ApplyGlobalPlaylistAppearance(); SaveSettings(); } } }
    public IReadOnlyList<string> PlaylistRowColorOptions { get; } = ["", "#0F1418", "#131A22", "#182F46", "#251A38", "#3A3019", "#173823", "#4A1717"];
    public IReadOnlyList<string> PlaylistTextColorOptions { get; } = ["#D9DEE3", "#FFFFFFFF", "#D5E8FF", "#EBD9FF", "#FFF0C2", "#CFF5D6"];
    public bool ShowPlaylistThumbnails { get => _showPlaylistThumbnails; set { if (Set(ref _showPlaylistThumbnails, value)) SaveSettings(); } }
    public int PlaylistThumbnailWidth { get => _playlistThumbnailWidth; set { if (Set(ref _playlistThumbnailWidth, Math.Clamp(value, 24, 120))) SaveSettings(); } }
    public int PlaylistThumbnailHeight { get => _playlistThumbnailHeight; set { if (Set(ref _playlistThumbnailHeight, Math.Clamp(value, 16, 90))) SaveSettings(); } }
    public string DefaultDecoderPreference { get => _defaultDecoderPreference; set { var v=string.IsNullOrWhiteSpace(value)?"Auto (CPU+GPU)":value.Trim(); if(Set(ref _defaultDecoderPreference,v)) SaveSettings(); } }
    public IReadOnlyList<string> DecoderPreferences { get; } = ["Auto (CPU+GPU)", "CPU", "GPU Preferred", "CPU+GPU Hybrid"];
    public AppSettings Settings => _settings;
    public string DefaultLogoTemplate
    {
        get => _defaultLogoTemplate;
        set { if (Set(ref _defaultLogoTemplate, value ?? string.Empty)) SaveSettings(); }
    }
    public string DefaultGraphicsTemplate
    {
        get => _defaultGraphicsTemplate;
        set
        {
            if (Set(ref _defaultGraphicsTemplate, value ?? string.Empty))
            {
                var target = CgProjects.FirstOrDefault(x => string.Equals(x.Name, value, StringComparison.OrdinalIgnoreCase));
                if (target is not null) ActiveCgProject = target;
                SaveSettings();
            }
        }
    }
    public double ProgramNowPlayingDelaySeconds
    {
        get => _programNowPlayingDelaySeconds;
        set { if (Set(ref _programNowPlayingDelaySeconds, Math.Clamp(value, 0, 600))) SaveSettings(); }
    }
    public double ProgramComingUpBeforeEndSeconds
    {
        get => _programComingUpBeforeEndSeconds;
        set { if (Set(ref _programComingUpBeforeEndSeconds, Math.Clamp(value, 0, 600))) SaveSettings(); }
    }
    public ObservableCollection<CategorySetting> Categories { get; } = [];
    public void AddCategory()
    {
        Categories.Add(new CategorySetting
        {
            Name = $"Category {Categories.Count + 1}",
            Color = "#1E2638",
            TextColor = "#E6ECF5",
            EnableAutoCg = false,
            ShowInEpg = true
        });
        SaveSettings();
    }
    public void ResetCategoriesToDefault()
    {
        Categories.Clear();
        foreach (var c in AppSettings.GetDefaultCategories())
            Categories.Add(c);
        SaveSettings();
    }
    public string AudioDownmixPreset
    {
        get => _audioDownmixPreset;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "Auto Stereo" : value.Trim();
            if (!Set(ref _audioDownmixPreset, normalized)) return;
            AudioChannelRouter.CurrentPreset = normalized;
            SaveSettings();
        }
    }
    public IReadOnlyList<string> AudioDownmixPresets { get; } = ["Auto Stereo", "7.1 → Stereo", "5.1 → Stereo", "Front L/R Only", "Ch 1/2", "Ch 3/4", "Ch 5/6", "Ch 7/8", "Mono Sum"];

    public string CurrentElapsedText => FormatClock(TimeSpan.FromSeconds(Math.Max(0, _currentElapsedSeconds)));

    public string CurrentRemainingText
    {
        get
        {
            if (CurrentItem is null || CurrentItem.Duration <= TimeSpan.Zero) return "--:--:--";
            var remaining = Math.Max(0, CurrentItem.Duration.TotalSeconds - _currentElapsedSeconds);
            return FormatClock(TimeSpan.FromSeconds(remaining));
        }
    }

    public string CurrentBlockRemainingText
    {
        get
        {
            if (Playlist.Count == 0) return "--:--:--";
            var currentIndex = CurrentItem is null ? -1 : Playlist.IndexOf(CurrentItem);
            if (currentIndex < 0) return FormatClock(TimeSpan.FromTicks(Playlist.Sum(x => x.Duration.Ticks)));
            var ticks = TimeSpan.FromSeconds(Math.Max(0, CurrentItem!.Duration.TotalSeconds - _currentElapsedSeconds)).Ticks;
            for (var i = currentIndex + 1; i < Playlist.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(Playlist[i].BlockName)) break;
                if (!Playlist[i].IsControlEvent) ticks += Playlist[i].Duration.Ticks;
            }
            return FormatClock(TimeSpan.FromTicks(Math.Max(0, ticks)));
        }
    }

    public string CurrentBlockNameText
    {
        get
        {
            var index = CurrentItem is null ? -1 : Playlist.IndexOf(CurrentItem);
            if (index < 0) return "RUNDOWN";
            for (var i = index; i >= 0; i--)
                if (!string.IsNullOrWhiteSpace(Playlist[i].BlockName)) return Playlist[i].BlockName;
            return "RUNDOWN";
        }
    }

    public string NextDurationText => NextItem is null ? "--:--:--" : FormatClock(NextItem.Duration);

    public double CurrentProgressPercent
    {
        get
        {
            if (CurrentItem is null || CurrentItem.Duration.TotalSeconds <= 0) return 0;
            return Math.Clamp((_currentElapsedSeconds / CurrentItem.Duration.TotalSeconds) * 100.0, 0, 100);
        }
    }

    // Cue seeker: offset from selected item's IN point.
    public double PreviewSeekSeconds
    {
        get => _previewSeekSeconds;
        set
        {
            var clamped = Math.Clamp(value, 0, PreviewSeekMaximum);
            if (!Set(ref _previewSeekSeconds, clamped)) return;
            Raise(nameof(PreviewSeekText));
            Raise(nameof(SelectedRemainingTimeText));
            _ = LoadPreviewAsync(SelectedItem, clamped, debounce: true);
        }
    }

    public double PreviewSeekMaximum => Math.Max(0.001, SelectedItem?.Duration.TotalSeconds ?? 0.001);
    public string PreviewSeekText => FormatClock(TimeSpan.FromSeconds(Math.Max(0, PreviewSeekSeconds)));
    public string PreviewDurationText => SelectedItem is null ? "--:--:--" : FormatClock(SelectedItem.Duration);
    public bool CanPreviewSeek => SelectedItem is { IsLiveSource: false };

    // Program seeker: offset from current item's IN point. Seeking occurs when the UI commits the slider.
    public double ProgramSeekSeconds
    {
        get => _programSeekSeconds;
        set
        {
            var clamped = Math.Clamp(value, 0, ProgramSeekMaximum);
            if (!Set(ref _programSeekSeconds, clamped)) return;
            Raise(nameof(ProgramSeekText));
        }
    }

    public double ProgramSeekMaximum => Math.Max(0.001, CurrentItem?.Duration.TotalSeconds ?? 0.001);
    public string ProgramSeekText => FormatClock(TimeSpan.FromSeconds(Math.Max(0, ProgramSeekSeconds)));
    public string ProgramDurationText => CurrentItem is null ? "--:--:--" : FormatClock(CurrentItem.Duration);
    public bool CanProgramSeek => CurrentItem is { IsLiveSource: false };

    public double AudioLeftLevel { get => _audioLeftLevel; private set => Set(ref _audioLeftLevel, value); }
    public double AudioRightLevel { get => _audioRightLevel; private set => Set(ref _audioRightLevel, value); }
    public double AudioMasterLevel => Math.Max(AudioLeftLevel, AudioRightLevel);
    public string AudioLeftDb { get => _audioLeftDb; private set => Set(ref _audioLeftDb, value); }
    public string AudioRightDb { get => _audioRightDb; private set => Set(ref _audioRightDb, value); }
    public string AudioMasterDb => AudioLeftLevel >= AudioRightLevel ? AudioLeftDb : AudioRightDb;

    public string PlaylistCountText => $"{Playlist.Count} ITEMS";
    public string PlaylistDurationText => $"TOTAL {FormatClock(TimeSpan.FromTicks(Playlist.Sum(x => x.Duration.Ticks)))}";
    private double PlaylistPlayedSeconds
    {
        get
        {
            if (Playlist.Count == 0) return 0;
            var currentIndex = CurrentItem is null ? -1 : Playlist.IndexOf(CurrentItem);
            double seconds = 0;
            if (currentIndex < 0)
            {
                foreach (var item in Playlist.Where(x => x.Status == "Completed")) seconds += Math.Max(0, item.Duration.TotalSeconds);
            }
            else
            {
                for (var i = 0; i < currentIndex; i++) seconds += Math.Max(0, Playlist[i].Duration.TotalSeconds);
                seconds += Math.Max(0, _currentElapsedSeconds);
            }
            return seconds;
        }
    }
    public string PlaylistPlayedTimeText => FormatClock(TimeSpan.FromSeconds(PlaylistPlayedSeconds));
    public string PlaylistRemainingTimeText
    {
        get
        {
            var total = Playlist.Sum(x => Math.Max(0, x.Duration.TotalSeconds));
            return FormatClock(TimeSpan.FromSeconds(Math.Max(0, total - PlaylistPlayedSeconds)));
        }
    }
    public string SelectedRemainingTimeText
    {
        get
        {
            var selected = SelectedItem;
            if (selected is null || selected.Duration <= TimeSpan.Zero) return "00:00:00";
            var elapsed = ReferenceEquals(selected, CurrentItem) ? _currentElapsedSeconds : PreviewSeekSeconds;
            return FormatClock(TimeSpan.FromSeconds(Math.Max(0, selected.Duration.TotalSeconds - elapsed)));
        }
    }
    public double TimelineCanvasWidth
    {
        get => _timelineCanvasWidth;
        set
        {
            var width = Math.Clamp(value, 320.0, 5000.0);
            if (Math.Abs(_timelineCanvasWidth - width) < 1.0) return;
            _timelineCanvasWidth = width;
            Raise();
            RefreshTimelineLayout();
        }
    }
    public string TimelineDateText => DateTime.Now.ToString("ddd dd MMM yyyy").ToUpperInvariant();
    public string TimelineStartText => _timelineWindowStart.ToString("HH:mm:ss");
    public string TimelineMidText => _timelineWindowStart.AddSeconds(Math.Max(0, VisibleTimelineDurationSeconds / 2.0)).ToString("HH:mm:ss");
    public string TimelineEndText => _timelineWindowStart.AddSeconds(Math.Max(0, VisibleTimelineDurationSeconds)).ToString("HH:mm:ss");
    public string TimelinePlayheadText => CurrentItem is null ? "CUED" : _currentItemTimelineStart.AddSeconds(Math.Max(0, _currentElapsedSeconds)).ToString("HH:mm:ss");
    private double VisibleTimelineDurationSeconds => TimelineBlocks.Sum(x => Math.Max(0.001, Playlist.ElementAtOrDefault(x.PlaylistIndex)?.Duration.TotalSeconds ?? 0.001));
    public double TimelinePlayheadX
    {
        get
        {
            var current = CurrentItem;
            if (current is not null)
            {
                var index = Playlist.IndexOf(current);
                var block = TimelineBlocks.FirstOrDefault(x => x.PlaylistIndex == index);
                if (block is not null)
                {
                    var fraction = current.Duration.TotalSeconds <= 0 ? 0 : Math.Clamp(_currentElapsedSeconds / current.Duration.TotalSeconds, 0, 1);
                    return Math.Clamp(block.Left + block.Width * fraction, 0, TimelineCanvasWidth - 2);
                }
            }

            var selected = SelectedItem;
            if (selected is not null)
            {
                var index = Playlist.IndexOf(selected);
                var block = TimelineBlocks.FirstOrDefault(x => x.PlaylistIndex == index);
                if (block is not null) return Math.Clamp(block.Left, 0, TimelineCanvasWidth - 2);
            }
            return 0;
        }
    }
    public string NdiResolutionText => NdiProfile.Summary;
    public string DeckLinkResolutionText => DeckLinkProfile.Summary;
    public string DisplayResolutionText => DisplayProfile.Summary;

    public string StatusDetail
    {
        get
        {
            if (CurrentItem is not null) return $"ON AIR · {CurrentItem.Title} · {CurrentItem.VideoFormat} · {EngineStatus}";
            if (SelectedItem is not null) return $"CUED · {SelectedItem.Title} · {SelectedItem.FilePath}";
            return $"{ChannelName} · READY";
        }
    }

    public bool NdiEnabled
    {
        get => _ndiEnabled;
        set
        {
            if (value == _ndiEnabled) return;

            if (value)
            {
                NdiStatus = "STARTING";
                if (!_ndi.Open(NdiName))
                {
                    _ndiEnabled = false;
                    Raise(nameof(NdiEnabled));
                    NdiStatus = "ERROR";
                    WpfMessageBox.Show(_ndi.LastError, "NDI Output", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    return;
                }

                _ndiEnabled = true;
                Raise(nameof(NdiEnabled));
                NdiStatus = "CONNECTED";
                Raise(nameof(NdiRuntimeVersion));
                Raise(nameof(NdiRuntimePath));
                Raise(nameof(NdiDiagnosticsText));
                SaveSettings();
            }
            else
            {
                _ndiEnabled = false;
                Raise(nameof(NdiEnabled));
                _ndi.Close();
                NdiStatus = "OFF";
                Raise(nameof(NdiDiagnosticsText));
                SaveSettings();
            }
        }
    }

    public string NdiDiagnosticsText => _ndi.DebugSummary;
    public void RefreshOutputDiagnostics()
    {
        Raise(nameof(NdiDiagnosticsText));
        Raise(nameof(NdiStatus));
        Raise(nameof(VirtualOutputStatus));
        Raise(nameof(DeckLinkBuildStatus));
    }

    public bool DeckLinkEnabled
    {
        get => _deckLinkEnabled;
        set
        {
            if (value && !_deckLink.IsSupportedBuild)
            {
                WpfMessageBox.Show(_deckLink.LastError, "DeckLink Output");
                value = false;
            }
            if (!value) _deckLink.Close();
            if (Set(ref _deckLinkEnabled, value)) SaveSettings();
        }
    }

    public RelayCommand AddCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand PlayCommand { get; }
    public RelayCommand TakeSelectedCommand { get; }
    public RelayCommand PauseCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand FullscreenCommand { get; }

    public void BeginProgramSeek() => _programSeekDragging = true;

    public void CommitProgramSeek() => CommitProgramSeek(pauseAfterSeek: false);

    private void CommitProgramSeek(bool pauseAfterSeek)
    {
        _programSeekDragging = false;
        var current = CurrentItem;
        if (current is null) return;
        var currentIndex = Playlist.IndexOf(current);
        if (currentIndex < 0) return;

        var offset = TimeSpan.FromSeconds(Math.Clamp(ProgramSeekSeconds, 0, current.Duration.TotalSeconds));
        var preservePause = _engine.IsPaused;
        _localAudio.Reset();
        _ = _engine.PlayAsync(Playlist.ToArray(), currentIndex, offset);
        if (pauseAfterSeek || preservePause) _engine.Pause();
        Raise(nameof(ProgramPlayPauseGlyph));
    }

    public void GoToProgramIn()
    {
        if (CurrentItem is null) return;
        ProgramSeekSeconds = 0;
        CommitProgramSeek(pauseAfterSeek: true);
    }

    public void GoToProgramOut()
    {
        if (CurrentItem is null) return;
        ProgramSeekSeconds = Math.Max(0, ProgramSeekMaximum - 0.25);
        CommitProgramSeek(pauseAfterSeek: true);
    }

    public void ExportEpgXmlTv()
    {
        var dialog = new WpfSaveFileDialog { Filter = "XMLTV EPG|*.xml", FileName = $"{ChannelId}-epg.xml" };
        if (dialog.ShowDialog() != true) return;
        EpgGenerator.SaveXmlTv(dialog.FileName, Playlist, ChannelId, ChannelName);
        AddRecentEvent("EPG EXPORTED · XMLTV");
    }

    public void ExportEpgJson()
    {
        var dialog = new WpfSaveFileDialog { Filter = "JSON EPG|*.json", FileName = $"{ChannelId}-epg.json" };
        if (dialog.ShowDialog() != true) return;
        EpgGenerator.SaveJson(dialog.FileName, Playlist, ChannelId, ChannelName);
        AddRecentEvent("EPG EXPORTED · JSON");
    }

    public void SaveSettings()
    {
        _settings.ChannelName = ChannelName;
        _settings.ChannelId = ChannelId;
        _settings.PlayoutPreset = PlayoutPreset;
        _settings.EnableFiller = FillerEnabled;
        _settings.FillerFolder = FillerFolder;
        _settings.ControllerHost = ControllerHost;
        _settings.ControllerPort = ControllerPort;
        _settings.ConnectToController = ConnectToController;
        _settings.NdiSourceName = NdiName;
        _settings.EnableNdiOutput = NdiEnabled;
        _settings.EnableDeckLinkOutput = DeckLinkEnabled;
        _settings.EnableLocalAudio = LocalAudioEnabled;
        _settings.ProgramAudioDeviceNumber = ProgramAudioDeviceNumber;
        _settings.PreviewAudioDeviceNumber = PreviewAudioDeviceNumber;
        _settings.EnableMasterOutputVolume = EnableMasterOutputVolume;
        _settings.MasterOutputVolumePercent = MasterOutputVolumePercent;
        _settings.MasterOutputMuted = MasterOutputMuted;
        _settings.EnableVideoProcessing = EnableVideoProcessing;
        _settings.VideoBrightness = VideoBrightness; _settings.VideoContrast = VideoContrast; _settings.VideoSaturation = VideoSaturation; _settings.VideoGamma = VideoGamma;
        _settings.VideoHueDegrees = VideoHueDegrees; _settings.VideoExposureStops = VideoExposureStops; _settings.VideoTemperature = VideoTemperature; _settings.VideoTint = VideoTint;
        _settings.VideoSharpness = VideoSharpness; _settings.VideoBlackLevel = VideoBlackLevel; _settings.VideoWhiteLevel = VideoWhiteLevel;
        _settings.VideoRedGainPercent = VideoRedGainPercent; _settings.VideoGreenGainPercent = VideoGreenGainPercent; _settings.VideoBlueGainPercent = VideoBlueGainPercent;
        _settings.EnableQcOnImport = EnableQcOnImport;
        _settings.MarkPlayedItemsCompleted = MarkPlayedItemsCompleted;
        _settings.AutoRemovePlayedItems = AutoRemovePlayedItems;
        _settings.HidePlayedItems = HidePlayedItems;
        _settings.DimCompletedItems = DimCompletedItems;
        _settings.PlaylistRowHeight = PlaylistRowHeight;
        _settings.PlaylistFontSize = PlaylistFontSize;
        _settings.PlaylistDefaultRowColor = PlaylistDefaultRowColor;
        _settings.PlaylistDefaultTextColor = PlaylistDefaultTextColor;
        _settings.ShowPlaylistThumbnails = ShowPlaylistThumbnails;
        _settings.PlaylistThumbnailWidth = PlaylistThumbnailWidth;
        _settings.PlaylistThumbnailHeight = PlaylistThumbnailHeight;
        _settings.DefaultDecoderPreference = DefaultDecoderPreference;
        _settings.Epg ??= new EpgOutputSettings();
        _settings.Epg.Normalize();
        _settings.AudioDownmixPreset = AudioDownmixPreset;
        _settings.EnableVirtualOutput = VirtualOutputEnabled;
        _settings.VirtualOutputName = VirtualOutputName;
        _settings.VirtualOutputWidth = VirtualOutputWidth;
        _settings.VirtualOutputHeight = VirtualOutputHeight;
        _settings.VirtualOutputFrameRate = VirtualOutputFrameRate;
        _settings.EnableAutoCg = EnableAutoCg;
        _settings.EnableAutoBackIn = EnableAutoBackIn;
        _settings.BackInTriggerLeadSeconds = BackInTriggerLeadSeconds;
        _settings.DefaultBackInTemplate = DefaultBackInTemplate;
        _settings.DefaultLogoTemplate = DefaultLogoTemplate;
        _settings.DefaultGraphicsTemplate = DefaultGraphicsTemplate;
        _settings.ProgramNowPlayingDelaySeconds = ProgramNowPlayingDelaySeconds;
        _settings.ProgramComingUpBeforeEndSeconds = ProgramComingUpBeforeEndSeconds;
        _settings.Categories = Categories.ToList();
        _settings.MusicNowPlayingDelaySeconds = MusicNowPlayingDelaySeconds;
        _settings.MusicComingUpBeforeEndSeconds = MusicComingUpBeforeEndSeconds;
        _settings.NewsNowPlayingDelaySeconds = NewsNowPlayingDelaySeconds;
        _settings.NewsComingUpBeforeEndSeconds = NewsComingUpBeforeEndSeconds;
        _settings.AutoCgHoldSeconds = AutoCgHoldSeconds;
        _settings.FullscreenMonitor = FullscreenMonitorIndex;
        _settings.KeepEngineRunningInTray = KeepEngineRunningInTray;
        _settings.RestoreLastPlaylist = RestoreLastPlaylist;
        _settings.NdiProfile = NdiProfile.Clone();
        _settings.DeckLinkDeviceIndex = DeckLinkDeviceIndex;
        _settings.DeckLinkDeviceName = DeckLinkDevices.FirstOrDefault(x => x.Index == DeckLinkDeviceIndex)?.PersistentName ?? string.Empty;
        _settings.DeckLinkProfile = DeckLinkProfile.Clone();
        _settings.DisplayProfile = DisplayProfile.Clone();
        SettingsStore.Save(_settings);
        _professional.Start(_settings.ProfessionalBroadcast, ChannelId, ChannelName, () => (CurrentItem?.Id.ToString(), _currentElapsedSeconds));
        Raise(nameof(ProfessionalOutputArmed));
        Raise(nameof(ProfessionalBroadcastStatus));
        PersistState();
    }

    private async Task AddFilesAsync()
    {
        var dlg = new WpfOpenFileDialog
        {
            Multiselect = true,
            Filter = "Media files|*.mp4;*.mov;*.mxf;*.mkv;*.avi;*.ts;*.m2ts;*.mpg;*.mpeg;*.webm;*.wmv|All files|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        foreach (var file in dlg.FileNames)
        {
            try { await ImportMediaFileAsync(file); }
            catch (Exception ex) { WpfMessageBox.Show($"Cannot add {file}\n\n{ex.Message}", "Media Import"); }
        }
        if (SelectedItem is null && Playlist.Count > 0) SelectedItem = Playlist[0];
        RaisePlaylistSummary(); PersistPlaylist();
    }

    public async Task<PlaylistItem?> InsertMediaAfterSelectedAsync()
    {
        var dlg = new WpfOpenFileDialog
        {
            Multiselect = true,
            Filter = "Media files|*.mp4;*.mov;*.mxf;*.mkv;*.avi;*.ts;*.m2ts;*.mpg;*.mpeg;*.webm;*.wmv|All files|*.*"
        };
        if (dlg.ShowDialog() != true) return null;

        var insertIndex = SelectedItem is null ? Playlist.Count : Playlist.IndexOf(SelectedItem) + 1;
        PlaylistItem? lastInserted = null;
        foreach (var file in dlg.FileNames)
        {
            try
            {
                var targetIndex = Math.Clamp(insertIndex, 0, Playlist.Count);
                var imported = await ImportMediaFileAsync(file, targetIndex);
                insertIndex = Playlist.IndexOf(imported) + 1;
                lastInserted = imported;
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show($"Cannot insert {file}\n\n{ex.Message}", "Media Insert");
            }
        }

        RenumberPlaylist();
        if (lastInserted is not null) SelectedItem = lastInserted;
        RaisePlaylistSummary();
        PersistPlaylist();
        return lastInserted;
    }

    public async Task<PlaylistItem> ImportMediaAtAsync(string file, bool insertAfterSelected)
    {
        var anchor = SelectedItem;
        var anchorIndex = anchor is null ? Playlist.Count - 1 : Playlist.IndexOf(anchor);
        var insertionIndex = insertAfterSelected ? Math.Clamp(anchorIndex + 1, 0, Playlist.Count) : (int?)null;
        var imported = await ImportMediaFileAsync(file, insertionIndex);
        RenumberPlaylist();
        SelectedItem = imported;
        RaisePlaylistSummary();
        PersistPlaylist();
        return imported;
    }

    public async Task<PlaylistItem> ImportMediaFileAsync(string file, int? insertionIndex = null)
    {
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) throw new FileNotFoundException("Media file was not found.", file);
        var mam = new EnterpriseMamCatalog();
        var catalogAsset = mam.RegisterFile(file, MediaAssetMetadataStore.Get(file), "Playout import");
        mam.AssertMayUse(file, DateTime.UtcNow);
        var info = await Task.Run(() => MediaProbe.Read(file));
        MediaQcResult? qc = null;
        if (EnableQcOnImport)
        {
            qc = await QualityControlService.CheckAsync(file, QcPreset, deepDecode: false);
            mam.SetQc(catalogAsset.AssetId, qc.Status, qc.Summary, "Playout import QC");
        }
        var item = new PlaylistItem
        {
            Sequence = Playlist.Count + 1, EventType = "CLIP", Title = Path.GetFileNameWithoutExtension(file), FilePath = file, Category = MediaCategoryStore.Get(file),
            SourceKind = "File", IsLiveSource = false, SourceDuration = info.Duration, SourceFrameRate = info.FramesPerSecond > 0 ? info.FramesPerSecond : 25.0,
            OutPoint = info.Duration, Codec = info.VideoCodec, VideoFormat = $"{info.Width}x{info.Height} {info.FramesPerSecond:0.###}p",
            DecoderPreference = DefaultDecoderPreference,
            QcStatus = qc is null ? "Not checked" : qc.Status, QcSummary = qc?.Summary ?? string.Empty
        };
        if (insertionIndex.HasValue)
        {
            ApplyGlobalPlaylistAppearance(item);
            InsertPlaylistItemAt(item, Math.Clamp(insertionIndex.Value, 0, Playlist.Count));
        }
        else
        {
            AttachPlaylistItem(item);
        }
        try { item.ThumbnailPath = await MediaThumbnailService.EnsureThumbnailAsync(item); } catch { }
        if (qc is { Passed: false }) AddRecentEvent($"QC FAIL · {item.Title} · {qc.Summary}");
        SelectedItem ??= item; RaisePlaylistSummary(); PersistPlaylist();
        return item;
    }

    public void AddInputSource(PlaylistItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        NormalizeInputEvent(item);
        RegisterInputSource(item);
        AttachPlaylistItem(item);
        SelectedItem = item;
        RaisePlaylistSummary();
        PersistPlaylist();
    }

    public void RegisterInputSource(PlaylistItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        NormalizeInputEvent(item);
        if (!InputSources.Any(x => SameInputSource(x, item))) InputSources.Add(item);
    }

    private static bool SameInputSource(PlaylistItem a, PlaylistItem b) =>
        string.Equals(a.SourceKind, b.SourceKind, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.FilePath, b.FilePath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.InputOptions, b.InputOptions, StringComparison.OrdinalIgnoreCase);

    public void InsertInputSourceAfterSelected(PlaylistItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        NormalizeInputEvent(item);
        RegisterInputSource(item);
        var anchorIndex = SelectedItem is null ? Playlist.Count - 1 : Playlist.IndexOf(SelectedItem);
        InsertPlaylistItemAt(item, Math.Clamp(anchorIndex + 1, 0, Playlist.Count));
        SelectedItem = item;
        RaisePlaylistSummary();
        PersistPlaylist();
    }

    public PlaylistItem AddCgEvent(CgProject project, bool insertAfterSelected)
    {
        ArgumentNullException.ThrowIfNull(project);
        var duration = TimeSpan.FromSeconds(Math.Max(0.5, project.DurationSeconds));
        var item = new PlaylistItem
        {
            EventType = "CG", Category = "Graphics", Title = "CG · " + project.Name, SourceKind = "CG", CgProjectId = project.Id,
            SourceDuration = duration, InPoint = TimeSpan.Zero, OutPoint = duration, SourceFrameRate = 25,
            Codec = "Kashtrix CG", VideoFormat = "Program Overlay", Status = "Ready", Notes = "CG overlay event", Location = "CG / " + project.Name
        };

        if (insertAfterSelected)
        {
            var anchorIndex = SelectedItem is null ? Playlist.Count - 1 : Playlist.IndexOf(SelectedItem);
            InsertPlaylistItemAt(item, Math.Clamp(anchorIndex + 1, 0, Playlist.Count));
        }
        else
        {
            AttachPlaylistItem(item);
            RenumberPlaylist();
        }

        SelectedItem = item;
        RaisePlaylistSummary();
        PersistPlaylist();
        AddRecentEvent($"ADD CG · {project.Name}");
        return item;
    }

    private static void NormalizeInputEvent(PlaylistItem item)
    {
        if (item.OutPoint <= item.InPoint)
        {
            var duration = item.SourceDuration > TimeSpan.Zero ? item.SourceDuration : TimeSpan.FromHours(1);
            item.InPoint = TimeSpan.Zero;
            item.OutPoint = duration;
        }
    }

    private void InsertPlaylistItemAt(PlaylistItem item, int index)
    {
        item.PropertyChanged -= OnPlaylistItemPropertyChanged;
        item.PropertyChanged += OnPlaylistItemPropertyChanged;
        Playlist.Insert(Math.Clamp(index, 0, Playlist.Count), item);
        RenumberPlaylist();
    }

    public void InsertPlaylistItem(PlaylistItem item)
    {
        var targetIndex = SelectedItem is not null ? Math.Max(0, Playlist.IndexOf(SelectedItem) + 1) : Playlist.Count;
        InsertPlaylistItemAt(item, targetIndex);
        SelectedItem = item;
        RaisePlaylistSummary();
        PersistPlaylist();
        AddRecentEvent($"INSERT ITEM · {item.EventType} · {item.Title}");
    }

    public void ExecuteManualScteCue(ScteSpliceCue cue, string onBreakMode = "Current File in Playlist")
    {
        _professional.SendManualScte(cue);
        AddRecentEvent($"SCTE TRANSMIT · {cue.SpliceType} · Event={cue.EventId} · Dur={cue.DurationSeconds:0}s · {cue.Source}");

        if (cue.IsCueIn || cue.OutOfNetworkIndicator)
        {
            HandleScteCueIn(cue, onBreakMode);
        }
        else if (cue.IsCueOut || !cue.OutOfNetworkIndicator)
        {
            HandleScteCueOut(cue);
        }
    }

    public void HandleScteCueIn(ScteSpliceCue cue, string onBreakMode)
    {
        AddRecentEvent($"AD BREAK START · SCTE Event={cue.EventId} · Mode={onBreakMode} · Duration={cue.DurationSeconds:0}s");

        switch (onBreakMode)
        {
            case "Current File in Playlist":
                if (Playlist.Count > 0 && !_engine.IsPlaying)
                {
                    PlayOrResume();
                }
                break;
            case "Black Image":
                _engine.Pause();
                break;
            case "Custom Video":
            case "Custom Image":
                if (!string.IsNullOrWhiteSpace(_settings.ProfessionalBroadcast.ScteOnBreakMediaFile) &&
                    File.Exists(_settings.ProfessionalBroadcast.ScteOnBreakMediaFile))
                {
                    var dur = cue.DurationSeconds > 0 ? TimeSpan.FromSeconds(cue.DurationSeconds) : TimeSpan.FromSeconds(30);
                    var breakItem = new PlaylistItem
                    {
                        Title = $"AD BREAK [{cue.EventId}]",
                        FilePath = _settings.ProfessionalBroadcast.ScteOnBreakMediaFile,
                        InPoint = TimeSpan.Zero,
                        OutPoint = dur,
                        SourceDuration = dur,
                        Category = "Commercial",
                        BlockName = "COMMERCIAL BREAK"
                    };
                    InsertPlaylistItem(breakItem);
                    SelectedItem = breakItem;
                    TakeSelected();
                }
                break;
        }

        if (cue.AutoReturn && cue.DurationSeconds > 0)
        {
            var returnTimer = new System.Threading.Timer(_ =>
            {
                Ui(() =>
                {
                    var returnCue = new ScteSpliceCue
                    {
                        EventId = cue.EventId,
                        SpliceType = "End Immediate",
                        IsCueOut = true,
                        OutOfNetworkIndicator = false,
                        Source = "Auto Return Timer"
                    };
                    HandleScteCueOut(returnCue);
                });
            }, null, (int)(cue.DurationSeconds * 1000), System.Threading.Timeout.Infinite);
        }
    }

    public void HandleScteCueOut(ScteSpliceCue cue)
    {
        AddRecentEvent($"AD BREAK END · RETURN TO PROGRAM · SCTE Event={cue.EventId}");
        if (_engine.IsPaused)
        {
            _engine.Resume();
        }
    }

    private void OnScteSpliceReceived(object? sender, ScteSpliceCue cue)
    {
        Ui(() =>
        {
            AddRecentEvent($"SCTE RECEIVER · {cue.SpliceType} · Event={cue.EventId} · Dur={cue.DurationSeconds:0}s · {cue.Source}");
            if (_settings.ProfessionalBroadcast.ScteAutoAdBreak)
            {
                if (cue.IsCueIn || cue.OutOfNetworkIndicator)
                {
                    HandleScteCueIn(cue, _settings.ProfessionalBroadcast.ScteOnBreakPlayMode);
                }
                else if (cue.IsCueOut || !cue.OutOfNetworkIndicator)
                {
                    HandleScteCueOut(cue);
                }
            }
        });
    }

    private void OnCueFrameDetected(string matchType, double score)
    {
        Ui(() =>
        {
            AddRecentEvent($"OPTICAL CUE DETECTED · {matchType} · Score: {score:0.0}%");
            if (matchType.Equals("OUT", StringComparison.OrdinalIgnoreCase))
            {
                var cue = new ScteSpliceCue
                {
                    EventId = (uint)new Random().Next(10000, 99999),
                    SpliceType = "Start Immediate",
                    IsCueIn = true,
                    OutOfNetworkIndicator = true,
                    DurationSeconds = _settings.ProfessionalBroadcast.ScteDefaultBreakDuration,
                    AutoReturn = true,
                    Source = "Optical Cue Tone Frame Detection"
                };
                HandleScteCueIn(cue, _settings.ProfessionalBroadcast.ScteOnBreakPlayMode);
            }
            else if (matchType.Equals("IN", StringComparison.OrdinalIgnoreCase))
            {
                var cue = new ScteSpliceCue
                {
                    EventId = 0,
                    SpliceType = "End Immediate",
                    IsCueOut = true,
                    OutOfNetworkIndicator = false,
                    Source = "Optical Cue Tone (IN Frame)"
                };
                HandleScteCueOut(cue);
            }
        });
    }

    private void OnEmergencyReturnTriggered(string reason)
    {
        Ui(() =>
        {
            AddRecentEvent($"EMERGENCY RETURN · {reason}");
            var cue = new ScteSpliceCue
            {
                EventId = 0,
                SpliceType = "End Immediate",
                IsCueOut = true,
                OutOfNetworkIndicator = false,
                Source = "Emergency Return"
            };
            HandleScteCueOut(cue);
        });
    }

    public void NotifySelectedItemEdited()
    {
        Raise(nameof(PreviewSeekMaximum));
        Raise(nameof(PreviewDurationText));
        Raise(nameof(ProgramSeekMaximum));
        Raise(nameof(ProgramDurationText));
        Raise(nameof(CanPreviewSeek));
        Raise(nameof(CanProgramSeek));
        RaisePlaylistSummary();
        PersistPlaylist();
        if (SelectedItem is not null) _ = LoadPreviewAsync(SelectedItem, 0, debounce: false);
    }

    /// <summary>Rebuild the selected event preview without changing the rundown.</summary>
    public void CueSelected()
    {
        if (SelectedItem is null) return;
        _ = LoadPreviewAsync(SelectedItem, Math.Clamp(PreviewSeekSeconds, 0, PreviewSeekMaximum), debounce: false);
        AddRecentEvent($"CUE · {SelectedItem.Title}");
    }

    /// <summary>Duplicate the selected rundown event immediately after its source row.</summary>
    public void DuplicateSelected()
    {
        var source = SelectedItem;
        if (source is null) return;

        // Serialize/deserialize so duplicated rundown items retain every operator property,
        // including newer EPG, appearance, audio, video and decoder fields.
        var clone = JsonSerializer.Deserialize<PlaylistItem>(JsonSerializer.Serialize(source)) ?? new PlaylistItem();
        clone.Id = Guid.NewGuid();
        clone.Title = source.Title + " Copy";
        clone.Status = "Ready";

        var sourceIndex = Playlist.IndexOf(source);
        clone.PropertyChanged += OnPlaylistItemPropertyChanged;
        Playlist.Insert(Math.Clamp(sourceIndex + 1, 0, Playlist.Count), clone);
        RenumberPlaylist();
        SelectedItem = clone;
        RaisePlaylistSummary();
        PersistPlaylist();
        AddRecentEvent($"DUPLICATE · {source.Title}");
    }

    /// <summary>Move the selected event by one or more rows while preserving event identity.</summary>
    public bool MoveSelected(int delta)
    {
        var item = SelectedItem;
        if (item is null || delta == 0) return false;
        var oldIndex = Playlist.IndexOf(item);
        if (oldIndex < 0) return false;
        var newIndex = Math.Clamp(oldIndex + delta, 0, Playlist.Count - 1);
        if (newIndex == oldIndex) return false;
        Playlist.Move(oldIndex, newIndex);
        RenumberPlaylist();
        SelectedItem = item;
        RaisePlaylistSummary();
        PersistPlaylist();
        AddRecentEvent($"MOVE · {item.Title} · {newIndex + 1}");
        return true;
    }

    public void RemoveSelected() => Remove();

    public bool SetSelectedInAtPreview()
    {
        var item = SelectedItem;
        if (item is null || item.IsLiveSource || item.IsCgEvent || item.HasParts) return false;
        var sourcePosition = item.MapEventOffset(TimeSpan.FromSeconds(Math.Clamp(PreviewSeekSeconds, 0, item.Duration.TotalSeconds))).SourcePosition;
        if (item.OutPoint > TimeSpan.Zero && sourcePosition >= item.OutPoint) return false;
        item.InPoint = sourcePosition;
        PreviewSeekSeconds = 0;
        NotifySelectedItemEdited();
        AddRecentEvent($"MARK IN · {item.Title} · {item.InPointText}");
        return true;
    }

    public bool SetSelectedOutAtPreview()
    {
        var item = SelectedItem;
        if (item is null || item.IsLiveSource || item.IsCgEvent || item.HasParts) return false;
        var sourcePosition = item.MapEventOffset(TimeSpan.FromSeconds(Math.Clamp(PreviewSeekSeconds, 0, item.Duration.TotalSeconds))).SourcePosition;
        if (sourcePosition <= item.InPoint) return false;
        item.OutPoint = item.SourceDuration > TimeSpan.Zero && sourcePosition > item.SourceDuration ? item.SourceDuration : sourcePosition;
        NotifySelectedItemEdited();
        AddRecentEvent($"MARK OUT · {item.Title} · {item.OutPointText}");
        return true;
    }

    public bool ResetSelectedTrim()
    {
        var item = SelectedItem;
        if (item is null || item.IsLiveSource || item.IsCgEvent) return false;
        item.Parts.Clear();
        item.InPoint = TimeSpan.Zero;
        item.OutPoint = item.SourceDuration > TimeSpan.Zero ? item.SourceDuration : item.OutPoint;
        PreviewSeekSeconds = 0;
        NotifySelectedItemEdited();
        AddRecentEvent($"RESET TRIM · {item.Title}");
        return true;
    }

    public async Task<MediaQcResult?> RunSelectedQcAsync()
    {
        var item = SelectedItem;
        if (item is null || item.IsLiveSource || item.IsCgEvent || string.IsNullOrWhiteSpace(item.FilePath) || !File.Exists(item.FilePath)) return null;
        var result = await QualityControlService.CheckAsync(item.FilePath, QcPreset, deepDecode: false);
        item.QcStatus = result.Status;
        item.QcSummary = result.Summary;
        PersistPlaylist();
        AddRecentEvent($"QC {result.Status.ToUpperInvariant()} · {item.Title}");
        return result;
    }

    private void Remove()
    {
        if (SelectedItem is null) return;
        var index = Playlist.IndexOf(SelectedItem);
        SelectedItem.PropertyChanged -= OnPlaylistItemPropertyChanged;
        Playlist.Remove(SelectedItem);
        RenumberPlaylist();
        SelectedItem = Playlist.Count == 0 ? null : Playlist[Math.Clamp(index, 0, Playlist.Count - 1)];
        RaisePlaylistSummary();
        PersistPlaylist();
    }


    private void RenumberPlaylist()
    {
        for (var i = 0; i < Playlist.Count; i++)
            Playlist[i].Sequence = i + 1;
    }

    private void PlayOrResume()
    {
        if (_engine.IsPlaying)
        {
            if (_engine.IsPaused) _engine.Resume();
            Raise(nameof(IsPaused));
            Raise(nameof(PlayPauseStateText));
            Raise(nameof(ProgramPlayPauseGlyph));
            return;
        }
        if (SelectedItem is not null) TakeSelected();
    }

    private void TakeSelected()
    {
        if (Playlist.Count == 0) { BeginFillerIfIdle(); return; }
        var idx = SelectedItem is null ? 0 : Math.Max(0, Playlist.IndexOf(SelectedItem));
        var item = Playlist[idx];
        var eventOffset = ReferenceEquals(item, SelectedItem)
            ? TimeSpan.FromSeconds(Math.Clamp(PreviewSeekSeconds, 0, item.Duration.TotalSeconds))
            : TimeSpan.Zero;
        _localAudio.Reset();
        _ = _engine.PlayAsync(Playlist.ToArray(), idx, eventOffset);
    }

    public void TogglePreviewPlayback()
    {
        if (_isPreviewPlaying)
        {
            _isPreviewPaused = !_isPreviewPaused;
            if (_isPreviewPaused) _previewPauseGate.Reset(); else _previewPauseGate.Set();
            Raise(nameof(PreviewPlayPauseGlyph));
            return;
        }

        var item = SelectedItem;
        if (item is null || item.IsControlEvent || item.IsCgEvent || !FfmpegRuntime.IsInitialized) return;

        StopPreviewPlayback();
        var cts = new CancellationTokenSource();
        _previewPlaybackCts = cts;
        _isPreviewPlaying = true;
        _isPreviewPaused = false;
        _previewPauseGate.Set();
        Raise(nameof(IsPreviewPlaying));
        Raise(nameof(PreviewPlayPauseGlyph));
        var startOffset = Math.Clamp(PreviewSeekSeconds, 0, Math.Max(0, item.Duration.TotalSeconds));
        _ = Task.Run(() => RunPreviewPlayback(item, startOffset, cts.Token));
    }

    public void StopPreviewPlayback()
    {
        var cts = Interlocked.Exchange(ref _previewPlaybackCts, null);
        try { cts?.Cancel(); } catch { }
        try { cts?.Dispose(); } catch { }
        _previewPauseGate.Set();
        _isPreviewPaused = false;
        if (!_isPreviewPlaying) return;
        _isPreviewPlaying = false;
        Raise(nameof(IsPreviewPlaying));
        Raise(nameof(PreviewPlayPauseGlyph));
    }

    private void RunPreviewPlayback(PlaylistItem item, double startOffsetSeconds, CancellationToken ct)
    {
        try
        {
            if (item.IsLiveSource)
            {
                using var liveDecoder = new VideoDecoder(item);
                var firstPts = double.NaN;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var pausedSeconds = 0.0;
                while (!ct.IsCancellationRequested)
                {
                    pausedSeconds += WaitPreviewWhilePaused(ct);
                    if (ct.IsCancellationRequested || !liveDecoder.TryRead(out var frame)) break;
                    if (double.IsNaN(firstPts)) firstPts = frame.PtsSeconds;
                    var due = Math.Max(0, frame.PtsSeconds - firstPts);
                    while (!ct.IsCancellationRequested && clock.Elapsed.TotalSeconds - pausedSeconds + .004 < due)
                    {
                        pausedSeconds += WaitPreviewWhilePaused(ct);
                        Thread.Sleep(3);
                    }
                    _lastPreviewBaseFrame = frame;
                    PreviewBaseFrameReady?.Invoke(frame);
                    var outFrame = PreviewCgProject is null
                        ? frame
                        : _previewCgCompositor.Composite(frame, PreviewCgProject, GetCgBusElapsed(preview: true));
                    PreviewFrameReady?.Invoke(outFrame);
                }
                return;
            }

            var parts = item.GetPlaybackParts();
            if (parts.Count == 0) return;
            var mapped = item.MapEventOffset(TimeSpan.FromSeconds(startOffsetSeconds));
            var partIndex = Math.Max(0, parts.ToList().FindIndex(x => x.Index == mapped.Part.Index));
            for (var i = partIndex; i < parts.Count && !ct.IsCancellationRequested; i++)
            {
                var part = parts[i];
                var sourceStart = i == partIndex ? mapped.SourcePosition : part.InPoint;
                using var decoder = new VideoDecoder(item);
                decoder.Seek(sourceStart);
                var firstPts = double.NaN;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var pausedSeconds = 0.0;
                var logicalBase = part.EventOffset.TotalSeconds + Math.Max(0, sourceStart.TotalSeconds - part.InPoint.TotalSeconds);
                while (!ct.IsCancellationRequested)
                {
                    pausedSeconds += WaitPreviewWhilePaused(ct);
                    if (ct.IsCancellationRequested || !decoder.TryRead(out var frame)) break;
                    if (frame.PtsSeconds + .010 < sourceStart.TotalSeconds) continue;
                    if (frame.PtsSeconds >= part.OutPoint.TotalSeconds - .005) break;
                    if (double.IsNaN(firstPts)) firstPts = frame.PtsSeconds;
                    var due = Math.Max(0, frame.PtsSeconds - firstPts);
                    while (!ct.IsCancellationRequested && clock.Elapsed.TotalSeconds - pausedSeconds + .004 < due)
                    {
                        pausedSeconds += WaitPreviewWhilePaused(ct);
                        Thread.Sleep(3);
                    }
                    if (ct.IsCancellationRequested) break;
                    _lastPreviewBaseFrame = frame;
                    PreviewBaseFrameReady?.Invoke(frame);
                    var outFrame = PreviewCgProject is null
                        ? frame
                        : _previewCgCompositor.Composite(frame, PreviewCgProject, GetCgBusElapsed(preview: true));
                    PreviewFrameReady?.Invoke(outFrame);
                    var eventSeconds = Math.Clamp(logicalBase + due, 0, item.Duration.TotalSeconds);
                    Ui(() =>
                    {
                        if (!ReferenceEquals(SelectedItem, item) || ct.IsCancellationRequested) return;
                        _previewSeekSeconds = eventSeconds;
                        Raise(nameof(PreviewSeekSeconds));
                        Raise(nameof(PreviewSeekText));
                    });
                }
            }
        }
        catch { }
        finally
        {
            Ui(() =>
            {
                var active = _previewPlaybackCts;
                if (active is null || !active.Token.Equals(ct)) return;
                if (!ReferenceEquals(Interlocked.CompareExchange(ref _previewPlaybackCts, null, active), active)) return;
                try { active.Dispose(); } catch { }
                _isPreviewPlaying = false;
                _isPreviewPaused = false;
                _previewPauseGate.Set();
                Raise(nameof(IsPreviewPlaying));
                Raise(nameof(PreviewPlayPauseGlyph));
            });
        }
    }

    private double WaitPreviewWhilePaused(CancellationToken ct)
    {
        if (_previewPauseGate.IsSet) return 0;
        var wait = System.Diagnostics.Stopwatch.StartNew();
        try { _previewPauseGate.Wait(ct); }
        catch (OperationCanceledException) { return 0; }
        return wait.Elapsed.TotalSeconds;
    }

    public void ToggleSelectedPlayPause()
    {
        // Operator-safe Ctrl+Space behavior:
        // - while Program is active, only pause/resume the current on-air event;
        //   never TAKE a different selected/preview row by accident.
        // - while stopped, start the currently selected rundown item.
        if (_engine.IsPlaying)
        {
            ToggleCurrentPause();
            return;
        }

        if (SelectedItem is not null) TakeSelected();
    }

    private void ToggleCurrentPause()
    {
        if (!_engine.IsPlaying) return;

        if (_engine.IsPaused)
        {
            _engine.Resume();
            if (CurrentItem is not null) CurrentItem.Status = "On Air";
            AddRecentEvent("RESUME · " + (CurrentItem?.Title ?? "PROGRAM"));
        }
        else
        {
            _engine.Pause();
            if (CurrentItem is not null) CurrentItem.Status = "Paused";
            AddRecentEvent("PAUSE · " + (CurrentItem?.Title ?? "PROGRAM"));
        }
        Raise(nameof(IsPaused));
        Raise(nameof(PlayPauseStateText));
        Raise(nameof(ProgramPlayPauseGlyph));
    }

    private void Next()
    {
        if (Playlist.Count < 2) return;

        var currentIndex = CurrentItem is null ? -1 : Playlist.IndexOf(CurrentItem);
        var selectedIndex = SelectedItem is null ? -1 : Playlist.IndexOf(SelectedItem);
        var nextIndex = currentIndex >= 0
            ? Math.Min(currentIndex + 1, Playlist.Count - 1)
            : Math.Max(0, selectedIndex);

        if (nextIndex < 0 || nextIndex >= Playlist.Count) return;
        SelectedItem = Playlist[nextIndex];
        _localAudio.Reset();
        _ = _engine.PlayAsync(Playlist.ToArray(), nextIndex, TimeSpan.Zero);
    }

    private void Stop()
    {
        _manualStopRequested = true;
        _scheduleRunner?.NotifyManualStop();
        StopPreviewPlayback();
        _engine.Stop();
        _localAudio.Reset();
        _programSeekSeconds = 0;
        Raise(nameof(ProgramSeekSeconds));
        Raise(nameof(ProgramSeekText));
        if (FillerEnabled) ScheduleFillerRetry(300);
    }

    private void ScheduleFillerRetry(int delayMs = 180)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(delayMs).ConfigureAwait(false);
            Ui(() =>
            {
                if (!FillerEnabled || CurrentItem is not null) return;
                if (_engine.IsPlaying) { ScheduleFillerRetry(220); return; }
                BeginFillerIfIdle();
            });
        });
    }

    private void BeginFillerIfIdle()
    {
        // With Channel Schedule automation on, filler comes from the schedule's filler slots.
        if (_scheduleRunner?.IsActive == true) return;
        if (!FillerEnabled || CurrentItem is not null || _engine.IsPlaying) return;
        if (string.IsNullOrWhiteSpace(FillerFolder) || !Directory.Exists(FillerFolder)) return;
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".mxf", ".ts", ".m2ts", ".mpeg", ".mpg", ".avi", ".mkv", ".webm" };
        var files = Directory.EnumerateFiles(FillerFolder).Where(x => extensions.Contains(Path.GetExtension(x))).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0) return;
        if (_fillerIndex >= files.Length) _fillerIndex = 0;
        var path = files[_fillerIndex++];
        try
        {
            var info = MediaProbe.Read(path);
            var item = new PlaylistItem
            {
                Title = "FILLER · " + Path.GetFileNameWithoutExtension(path), EventType = "FILL", SourceKind = "File", FilePath = path,
                SourceDuration = info.Duration, InPoint = TimeSpan.Zero, OutPoint = info.Duration, SourceFrameRate = info.FramesPerSecond,
                Codec = info.VideoCodec, VideoFormat = $"{info.Width}x{info.Height} {info.FramesPerSecond:0.###}p", Status = "Ready"
            };
            _localAudio.Reset();
            _ = _engine.PlayAsync(new[] { item }, 0, TimeSpan.Zero);
        }
        catch { /* A bad filler never stops channel automation. */ }
    }

    private async Task LoadPreviewAsync(PlaylistItem? item, double offsetSeconds, bool debounce)
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        var source = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _previewCts, source);
        CancelOnly(previous);

        if (item?.IsCgEvent == true)
        {
            try
            {
                var project = CgProjects.FirstOrDefault(x => x.Id == item.CgProjectId);
                var baseFrame = _lastProgramSourceFrame ?? new VideoFrameData(new byte[1280 * 720 * 4], 1280, 720, 1280 * 4, 0, 25, 1);
                PreviewBaseFrameReady?.Invoke(baseFrame);
                if (project is not null)
                {
                    var wasOnAir = project.OnAir;
                    try
                    {
                        project.OnAir = true;
                        var previewFrame = _previewCgCompositor.Composite(baseFrame, project, Math.Max(0, offsetSeconds));
                        PreviewFrameReady?.Invoke(previewFrame);
                    }
                    finally { project.OnAir = wasOnAir; }
                }
                else PreviewCleared?.Invoke();
            }
            finally
            {
                if (ReferenceEquals(Interlocked.CompareExchange(ref _previewCts, null, source), source)) source.Dispose();
                else source.Dispose();
            }
            return;
        }

        if (item?.IsControlEvent == true)
        {
            PreviewCleared?.Invoke();
            if (ReferenceEquals(Interlocked.CompareExchange(ref _previewCts, null, source), source)) source.Dispose();
            else source.Dispose();
            return;
        }

        if (item is null || !FfmpegRuntime.IsInitialized)
        {
            if (PreviewCgProject is not null)
            {
                var baseFrame = _lastProgramSourceFrame ?? new VideoFrameData(new byte[1280 * 720 * 4], 1280, 720, 1280 * 4, 0, 25, 1);
                PreviewBaseFrameReady?.Invoke(baseFrame);
                var previewFrame = _previewCgCompositor.Composite(baseFrame, PreviewCgProject, GetCgBusElapsed(preview: true));
                PreviewFrameReady?.Invoke(previewFrame);
            }
            else
            {
                if (item is null) PreviewCleared?.Invoke();
            }
            if (ReferenceEquals(Interlocked.CompareExchange(ref _previewCts, null, source), source))
                source.Dispose();
            else
                source.Dispose();
            return;
        }

        try
        {
            // Rapid cue scrubbing is normal operator input. Debounce without passing the
            // token into Task.Delay so cancellation never becomes a first-chance exception.
            if (debounce)
            {
                await Task.Delay(110).ConfigureAwait(false);
                if (source.IsCancellationRequested || Volatile.Read(ref _disposed) != 0) return;
            }

            var offset = Math.Clamp(offsetSeconds, 0, item.Duration.TotalSeconds);
            var target = item.IsLiveSource ? TimeSpan.Zero : item.MapEventOffset(TimeSpan.FromSeconds(offset)).SourcePosition;

            var frame = await Task.Run(() =>
            {
                if (source.IsCancellationRequested || Volatile.Read(ref _disposed) != 0) return (VideoFrameData?)null;

                using var decoder = new VideoDecoder(item);
                if (!item.IsLiveSource) decoder.Seek(target);

                while (!source.IsCancellationRequested && Volatile.Read(ref _disposed) == 0 && decoder.TryRead(out var candidate))
                {
                    if (item.IsLiveSource || candidate.PtsSeconds + 0.050 >= target.TotalSeconds) return candidate;
                }

                return null;
            }).ConfigureAwait(false);

            if (frame is not null && !source.IsCancellationRequested && Volatile.Read(ref _disposed) == 0)
            {
                _lastPreviewBaseFrame = frame;
                PreviewBaseFrameReady?.Invoke(frame);
                // PREVIEW CG is an independent bus: operators can prepare/verify a layer
                // without altering the PROGRAM compositor.
                var previewFrame = PreviewCgProject is null
                    ? frame
                    : _previewCgCompositor.Composite(frame, PreviewCgProject, GetCgBusElapsed(preview: true));
                if (_allowControllerClient && _connectToController)
                    _controllerCuePreview = CreateControllerThumbnail(previewFrame);
                PreviewFrameReady?.Invoke(previewFrame);
            }
        }
        catch (ObjectDisposedException)
        {
            // Shutdown can race a stale cue worker; disposal is expected here.
        }
        catch
        {
            // Preview failures are isolated from the on-air engine.
        }
        finally
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref _previewCts, null, source), source))
            {
                try { source.Dispose(); } catch { }
            }
            else
            {
                try { source.Dispose(); } catch { }
            }
        }
    }

    private double GetCgBusElapsed(bool preview)
    {
        var started = preview ? _previewCgStartedUtc : _activeCgStartedUtc;
        return Math.Max(0, (DateTime.UtcNow - started).TotalSeconds);
    }

    private void ApplyRundownCgTransition(PlaylistItem? previous, PlaylistItem? current)
    {
        if (previous?.IsCgEvent == true && previous.CgProjectId != Guid.Empty && ActiveCgProject?.Id == previous.CgProjectId)
        {
            ActiveCgProject.OnAir = false;
            ActiveCgProject = null;
            Raise(nameof(CgEnabled));
            Raise(nameof(CgStatusText));
        }

        if (current?.IsCgEvent != true || current.CgProjectId == Guid.Empty) return;
        var project = CgProjects.FirstOrDefault(x => x.Id == current.CgProjectId);
        if (project is null) return;
        ActiveCgProject = project;
        _activeCgStartedUtc = DateTime.UtcNow;
        ActiveCgProject.OnAir = true;
        Raise(nameof(CgEnabled));
        Raise(nameof(CgStatusText));
    }

    private void OnVideo(VideoFrameData frame)
    {
        _lastProgramSourceFrame = frame;
        var elapsed = Math.Max(0, _currentElapsedSeconds);
        EvaluateAutoCg(elapsed);
        Volatile.Write(ref _lastProgramSourceElapsedSeconds, elapsed);
        Volatile.Write(ref _lastProgramSourceStamp, System.Diagnostics.Stopwatch.GetTimestamp());
        Interlocked.Exchange(ref _latestProgramCadenceSourceFrame, frame);
        _programVideoTimeline.Push(frame);

        if (_settings.ProfessionalBroadcast.EnableFrameComparison && frame.Width > 0 && frame.Height > 0)
        {
            FrameComparisonService.Instance.ProcessFrame(_settings.ProfessionalBroadcast, frame);
        }

        if ((DateTime.UtcNow - _lastProgressUi).TotalMilliseconds >= 70)
        {
            _lastProgressUi = DateTime.UtcNow;
            var current = CurrentItem;
            if (current is not null)
            {
                Ui(() =>
                {
                    _currentElapsedSeconds = elapsed;
                    if (!_programSeekDragging)
                    {
                        _programSeekSeconds = Math.Clamp(elapsed, 0, current.Duration.TotalSeconds);
                        Raise(nameof(ProgramSeekSeconds));
                        Raise(nameof(ProgramSeekText));
                    }
                    RaisePlaybackCounters();
                });
            }
        }
    }

    private void StartProgramCadencePump()
    {
        var source = new CancellationTokenSource();
        _programCadenceCts = source;
        // A dedicated time-critical thread: the cadence must never queue behind thread-pool work.
        _programCadenceTask = Task.Factory.StartNew(() => ProgramCadenceLoop(source.Token), source.Token,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private void ProgramCadenceLoop(CancellationToken ct)
    {
        try { Thread.CurrentThread.Name = "Kashtrix Program Cadence"; } catch { }
        using var timeCritical = RealtimeScheduling.EnterTimeCriticalThread();
        using var frameClock = new PrecisionFrameClock();
        var frequency = (double)System.Diagnostics.Stopwatch.Frequency;
        var clockEpoch = System.Diagnostics.Stopwatch.GetTimestamp();
        double Elapsed() => (System.Diagnostics.Stopwatch.GetTimestamp() - clockEpoch) / frequency;
        var activePreset = string.Empty;
        var nextDeadline = 0.0;
        long tickIndex = 0;
        try
        {
            while (!ct.IsCancellationRequested && Volatile.Read(ref _disposed) == 0)
            {
                var spec = ChannelFormatPreset.Resolve(PlayoutPreset);
                if (!string.Equals(activePreset, spec.Name, StringComparison.OrdinalIgnoreCase))
                {
                    activePreset = spec.Name;
                    clockEpoch = System.Diagnostics.Stopwatch.GetTimestamp();
                    nextDeadline = 0;
                    tickIndex = 0;
                    _programCadencePtsSeconds = 0;
                }

                var interval = 1.0 / Math.Clamp(spec.FramesPerSecond, 1.0, 120.0);
                nextDeadline += interval;
                frameClock.WaitUntil(clockEpoch + (long)(nextDeadline * frequency), ct);
                if (ct.IsCancellationRequested) break;

                // Resynchronize after long stalls/suspend instead of trying to replay a backlog.
                if (Elapsed() - nextDeadline > interval * 3)
                    nextDeadline = Elapsed();

                // A module window that neither plays out nor shows Program (standalone Scheduler, MAM,
                // ...) keeps its clock but skips building 1080p frames it would throw away.
                if (!_allowControllerClient && VideoFrameReady is null && ProgramBaseFrameReady is null && !_engine.IsPlaying)
                {
                    tickIndex++;
                    _programCadencePtsSeconds += interval;
                    continue;
                }

                // Render the scheduled deadline (not the jittery wake-up time) minus a fixed latency.
                var referenceTicks = clockEpoch + (long)(nextDeadline * System.Diagnostics.Stopwatch.Frequency)
                    - ProgramAvTiming.LatencyTicks(spec.FramesPerSecond);
                var sourceFrame = _programVideoTimeline.Select(ProgramAvTiming.VideoSelectionTicks(referenceTicks, spec.FramesPerSecond))
                    ?? Volatile.Read(ref _latestProgramCadenceSourceFrame);
                if (sourceFrame is null)
                {
                    if (_cachedBlackBaseFrame is null || _cachedBlackBaseFrame.Width != spec.Width || _cachedBlackBaseFrame.Height != spec.Height)
                    {
                        var stride = spec.Width * 4;
                        var bytes = new byte[stride * spec.Height];
                        for (var i = 3; i < bytes.Length; i += 4) bytes[i] = 255;
                        var (n, d) = spec.RationalFrameRate;
                        _cachedBlackBaseFrame = new VideoFrameData(bytes, spec.Width, spec.Height, stride, 0, n, d);
                    }
                    sourceFrame = _cachedBlackBaseFrame;
                }

                byte[]? rented = null;
                try
                {
                    var fixedRaster = _programChannelScaler.Scale(sourceFrame, spec.Width, spec.Height);
                    var stampedBase = ChannelFormatPreset.Stamp(fixedRaster, spec, _programCadencePtsSeconds);
                    ProgramBaseFrameReady?.Invoke(stampedBase);
                    var elapsed = EstimateProgramElapsedForCadence();
                    rented = _programFrameRing.Rent(checked(stampedBase.Stride * stampedBase.Height));
                    var programFrame = BuildProgramFrame(stampedBase, elapsed, rented);
                    programFrame = ChannelFormatPreset.Stamp(programFrame, spec, _programCadencePtsSeconds);
                    var (rateN, rateD) = spec.RationalFrameRate;
                    var audioBlock = _programAudioClock.Read(referenceTicks, ProgramAvTiming.SamplesForFrame(tickIndex++, rateN, rateD));
                    SendProgramAudio(new AudioChunk(audioBlock, _programCadencePtsSeconds) { DueTimestamp = referenceTicks });
                    _programCadencePtsSeconds += interval;

                    VideoFrameReady?.Invoke(programFrame);
                    // The local virtual-output map is also the Output Engine confidence
                    // source, so keep publishing it while hardware/network transmission is
                    // disarmed.  Professional outputs remain protected inside SendProgramOutput.
                    if (_allowControllerClient) QueueProgramOutput(programFrame);
                }
                catch (Exception ex)
                {
                    RuntimeDiagnosticsService.WriteException("PROGRAM_CADENCE_FRAME_ERROR", ex);
                }
                finally
                {
                    if (rented is not null) _programFrameRing.Release(rented);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested) RuntimeDiagnosticsService.WriteException("PROGRAM_CADENCE_LOOP_ERROR", ex);
        }
    }

    private double EstimateProgramElapsedForCadence()
    {
        var elapsed = Math.Max(0, Volatile.Read(ref _lastProgramSourceElapsedSeconds));
        var stamp = Volatile.Read(ref _lastProgramSourceStamp);
        if (stamp > 0 && _engine.IsPlaying && !_engine.IsPaused)
            elapsed += Math.Max(0, (System.Diagnostics.Stopwatch.GetTimestamp() - stamp) / (double)System.Diagnostics.Stopwatch.Frequency);
        var duration = CurrentItem?.Duration.TotalSeconds ?? 0;
        return duration > 0 ? Math.Clamp(elapsed, 0, duration) : elapsed;
    }

    /// <param name="target">Program buffer rented by the cadence. The base frame is copied into it once
    /// and every graphics/colour stage then draws in place, so a Program frame costs no allocation
    /// and decoder ring buffers never reach an output.</param>
    private VideoFrameData BuildProgramFrame(VideoFrameData frame, double elapsed, byte[] target)
    {
        Buffer.BlockCopy(frame.Bgra, 0, target, 0, Math.Min(target.Length, frame.Bgra.Length));
        var owned = frame with { Bgra = target };
        var current = CurrentItem;
        var cgElapsed = current?.IsCgEvent == true ? elapsed
            : ActiveCgProject is not null
                ? ResolveCadencedCgElapsed(ActiveCgProject, _activeCgStartedUtc, frame.PtsSeconds)
                : GetCgBusElapsed(preview: false);
        var programFrame = _cgCompositor.CompositeInPlace(owned, ActiveCgProject, ActiveCgProject is { OnAir: true } ? cgElapsed : elapsed);
        CgProject[] controllerStack;
        ControllerCgTransitionState? controllerTransition;
        lock (_controllerCgStackGate)
        {
            controllerStack = _controllerProgramStack.Where(x => x.OnAir).OrderBy(x => x.ExternalLayer).ToArray();
            controllerTransition = _controllerCgTransition;
        }

        // Enforce Category Graphics Policy on Program Output
        var activeCategory = CurrentItem?.Category;
        var categorySetting = Settings.Categories.FirstOrDefault(c => string.Equals(c.Name, activeCategory, StringComparison.OrdinalIgnoreCase));
        var allowLogoOnly = categorySetting is not null && categorySetting.AllowLogoOnly;
        var autoNowNextOnly = categorySetting is not null && categorySetting.AutoNowNextOnly;

        if (allowLogoOnly)
        {
            // Category suppresses all external graphics overlays; only station logo bug is permitted
            controllerStack = [];
            controllerTransition = null;
        }
        else if (autoNowNextOnly)
        {
            // Category allows only automated Now/Next templates, suppresses news tickers and external overlays
            controllerStack = controllerStack.Where(x => (x.Name ?? "").Contains("Now Playing", StringComparison.OrdinalIgnoreCase) || (x.Name ?? "").Contains("Coming Up", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (controllerTransition is not null && !(controllerTransition.Incoming.Name ?? "").Contains("Now Playing", StringComparison.OrdinalIgnoreCase) && !(controllerTransition.Incoming.Name ?? "").Contains("Coming Up", StringComparison.OrdinalIgnoreCase))
            {
                controllerTransition = null;
            }
        }

        if (controllerTransition is null)
        {
            foreach (var overlay in controllerStack)
                programFrame = _cgCompositor.CompositeInPlace(programFrame, overlay,
                    ResolveCadencedCgElapsed(overlay, overlay.StartedUtc == default ? _activeCgStartedUtc : overlay.StartedUtc, frame.PtsSeconds));
        }
        else
        {
            foreach (var overlay in controllerStack.Where(x =>
                         !ReferenceEquals(x, controllerTransition.Incoming) &&
                         !ReferenceEquals(x, controllerTransition.Outgoing)))
                programFrame = _cgCompositor.CompositeInPlace(programFrame, overlay,
                    ResolveCadencedCgElapsed(overlay, overlay.StartedUtc == default ? _activeCgStartedUtc : overlay.StartedUtc, frame.PtsSeconds));

            var transitionElapsed = QuantizeToFrame(
                Math.Max(0, (DateTime.UtcNow - controllerTransition.StartedUtc).TotalSeconds), frame);
            var transitionProgress = Math.Clamp(transitionElapsed / Math.Max(.05, controllerTransition.DurationSeconds), 0, 1);
            if (transitionProgress < .9999)
            {
                programFrame = _cgCompositor.CompositeTransition(
                    programFrame,
                    controllerTransition.Outgoing,
                    controllerTransition.Incoming,
                    transitionElapsed,
                    transitionElapsed,
                    transitionProgress,
                    controllerTransition.Mode);
            }
            else
            {
                programFrame = _cgCompositor.CompositeInPlace(programFrame, controllerTransition.Incoming, transitionElapsed);
                lock (_controllerCgStackGate)
                {
                    if (ReferenceEquals(_controllerCgTransition, controllerTransition))
                    {
                        if (controllerTransition.Outgoing is not null)
                        {
                            controllerTransition.Outgoing.OnAir = false;
                            _controllerProgramStack.Remove(controllerTransition.Outgoing);
                        }
                        _controllerCgTransition = null;
                    }
                }
            }
        }
        if (_logoOverlayProject is { OnAir: true })
            programFrame = _logoCgCompositor.CompositeInPlace(programFrame, _logoOverlayProject,
                ResolveCadencedCgElapsed(_logoOverlayProject, _logoCgStartedUtc, frame.PtsSeconds));

        programFrame = VideoOutputProcessor.ApplyInPlace(programFrame, CurrentVideoOutputAdjustments);
        if (_allowControllerClient && _connectToController && (DateTime.UtcNow - _lastControllerProgramPreviewUtc).TotalMilliseconds >= 90)
        {
            _lastControllerProgramPreviewUtc = DateTime.UtcNow;
            _controllerProgramPreview = CreateControllerThumbnail(programFrame);
        }
        return programFrame;
    }

    private double ResolveCadencedCgElapsed(CgProject project, DateTime startedUtc, double outputPtsSeconds)
    {
        lock (_programCgTimelineGate)
        {
            if (!_programCgTimelineAnchors.TryGetValue(project, out var anchor) || anchor.StartedUtc != startedUtc)
            {
                var elapsedAtAnchor = Math.Max(0, (DateTime.UtcNow - startedUtc).TotalSeconds);
                anchor = (startedUtc, outputPtsSeconds - elapsedAtAnchor);
                _programCgTimelineAnchors[project] = anchor;
                if (_programCgTimelineAnchors.Count > 128)
                {
                    // Do not enumerate the controller stack here: the cadence thread must
                    // never race UI/controller mutations. OnAir is enough to identify old
                    // timeline entries, while the two direct buses are retained explicitly.
                    foreach (var stale in _programCgTimelineAnchors.Keys.Where(x =>
                                 !x.OnAir && !ReferenceEquals(x, ActiveCgProject) && !ReferenceEquals(x, _logoOverlayProject)).ToArray())
                        _programCgTimelineAnchors.Remove(stale);
                }
            }
            return Math.Max(0, outputPtsSeconds - anchor.PtsAnchor);
        }
    }

    private static double QuantizeToFrame(double seconds, VideoFrameData frame)
    {
        var fps = frame.FrameRateDenominator > 0
            ? frame.FrameRateNumerator / (double)frame.FrameRateDenominator
            : 50.0;
        fps = Math.Clamp(fps, 1, 120);
        return Math.Max(0, Math.Floor(seconds * fps + .0001) / fps);
    }

    private void QueueProgramOutput(VideoFrameData frame)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        // The worker holds its own reference so the cadence can release the buffer at tick end.
        _programFrameRing.AddReference(frame.Bgra);
        var superseded = Interlocked.Exchange(ref _pendingProgramOutputFrame, frame);
        if (superseded is not null) _programFrameRing.Release(superseded.Bgra);
        if (Interlocked.CompareExchange(ref _programOutputWorkerActive, 1, 0) == 0)
            _ = Task.Run(DrainProgramOutput);
    }

    private void DrainProgramOutput()
    {
        try
        {
            while (Volatile.Read(ref _disposed) == 0)
            {
                var frame = Interlocked.Exchange(ref _pendingProgramOutputFrame, null);
                if (frame is null) break;
                try { SendProgramOutput(frame); }
                catch (Exception ex) { RuntimeDiagnosticsService.WriteException("PROGRAM_OUTPUT_DISPATCH_ERROR", ex); }
                finally { _programFrameRing.Release(frame.Bgra); }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _programOutputWorkerActive, 0);
            if (Volatile.Read(ref _disposed) == 0 &&
                Interlocked.CompareExchange(ref _pendingProgramOutputFrame, null, null) is not null &&
                Interlocked.CompareExchange(ref _programOutputWorkerActive, 1, 0) == 0)
                _ = Task.Run(DrainProgramOutput);
        }
    }

    private void SendProgramOutput(VideoFrameData programFrame)
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        // Confidence is always available from the real Playout process, including standby.
        // Its header separately reports whether transmission is armed, so consumers never
        // mistake a standby confidence picture for a live Program output.
        _programConfidenceOutput.SubmitVideo(programFrame, _professional.OutputArmed);
        // The optional virtual device remains governed by its operator setting.
        if (VirtualOutputEnabled) _virtualOutput.SubmitVideo(programFrame, _professional.OutputArmed);
        if (!_professional.OutputArmed) return;
        if (NdiEnabled)
        {
            _ndi.SendVideo(programFrame, NdiProfile);
            RefreshNdiStatusFromSender();
        }
        if (DeckLinkEnabled) _deckLink.SendVideo(programFrame, DeckLinkProfile);
        _multiNdi.SubmitVideo(programFrame);
        _professional.SubmitVideo(programFrame);
    }

    private void RefreshNdiStatusFromSender()
    {
        var desired = string.IsNullOrWhiteSpace(_ndi.LastError) ? "CONNECTED" : "ERROR";
        if (string.Equals(_ndiStatus, desired, StringComparison.Ordinal)) return;
        Ui(() =>
        {
            if (!string.Equals(_ndiStatus, desired, StringComparison.Ordinal))
                NdiStatus = desired;
        });
    }

    private void OnProgress(TimeSpan progress)
    {
        var elapsed = Math.Max(0, progress.TotalSeconds);
        _currentElapsedSeconds = elapsed;
        EvaluateAutoCg(elapsed);
        if ((DateTime.UtcNow - _lastProgressUi).TotalMilliseconds < 55) return;
        _lastProgressUi = DateTime.UtcNow;
        Ui(() =>
        {
            if (!_programSeekDragging)
            {
                _programSeekSeconds = Math.Clamp(elapsed, 0, CurrentItem?.Duration.TotalSeconds ?? 0);
                Raise(nameof(ProgramSeekSeconds)); Raise(nameof(ProgramSeekText));
            }
            RaisePlaybackCounters();
        });
    }

    private void OnAudioMeter(AudioChunk chunk)
    {
        // True PFL-style meter: raw decoded programme source before per-item gain/mute AND
        // before the master fader/mute. Operators can therefore see incoming audio activity
        // while the item or final output is muted, matching broadcast-console behaviour.
        _lastRawMeterUtc = DateTime.UtcNow;
        UpdateAudioMeters(chunk);
    }

    private void UpdateAudioMeters(AudioChunk chunk)
    {
        if ((DateTime.UtcNow - _lastMeterUi).TotalMilliseconds < 45) return;
        _lastMeterUi = DateTime.UtcNow;
        var (left, right, leftDb, rightDb) = MeasureStereoPcm16(chunk.Pcm16Stereo48k);
        Ui(() =>
        {
            // Short peak hold/decay keeps speech and music visible on narrow broadcast meters
            // without making the visual meter sluggish.
            AudioLeftLevel = Math.Max(left, AudioLeftLevel * 0.78);
            AudioRightLevel = Math.Max(right, AudioRightLevel * 0.78);
            AudioLeftDb = left > 0.0001 ? leftDb : LevelToDbText(AudioLeftLevel);
            AudioRightDb = right > 0.0001 ? rightDb : LevelToDbText(AudioRightLevel);
            Raise(nameof(AudioMasterLevel));
            Raise(nameof(AudioMasterDb));
        });
    }

    private void OnAudio(AudioChunk chunk)
    {
        // Safety path: if a third-party/live input bypasses the dedicated raw-meter event,
        // keep the Program meters alive from the decoded pre-master chunk.
        if ((DateTime.UtcNow - _lastRawMeterUtc).TotalMilliseconds > 250) UpdateAudioMeters(chunk);
        // Decoded audio arrives slightly ahead of its due time and is played by the Program
        // cadence (SendProgramAudio) together with the matching picture.
        _programAudioClock.Add(chunk);
    }

    // Called once per cadence frame with exactly one frame of samples (silence when nothing plays,
    // as SDI/NDI receivers expect continuous audio). An output failure must not cost the video frame.
    private void SendProgramAudio(AudioChunk chunk)
    {
        try
        {
            var master = AudioMasterProcessor.Apply(chunk, MasterOutputVolumePercent, MasterOutputMuted, EnableMasterOutputVolume);
            if (LocalAudioEnabled) _localAudio.Send(master);
            var transmissionArmed = _professional.OutputArmed;
            if (transmissionArmed && NdiEnabled)
            {
                _ndi.SendAudio(master);
                RefreshNdiStatusFromSender();
            }
            if (transmissionArmed && DeckLinkEnabled) _deckLink.SendAudio(master);
            if (transmissionArmed) _multiNdi.SubmitAudio(master);
            if (transmissionArmed && VirtualOutputEnabled) _virtualOutput.SubmitAudio(master);
            _professional.SubmitAudio(master);
        }
        catch (Exception ex)
        {
            RuntimeDiagnosticsService.WriteException("PROGRAM_AUDIO_DISPATCH_ERROR", ex);
        }
    }

    private void OnPlaylistItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlaylistItem.InPoint) or nameof(PlaylistItem.OutPoint) or nameof(PlaylistItem.Duration) or nameof(PlaylistItem.SourceDuration) or nameof(PlaylistItem.Parts))
        {
            Raise(nameof(PlaylistDurationText));
            RefreshTimelineLayout();
            Raise(nameof(PreviewSeekMaximum));
            Raise(nameof(PreviewDurationText));
            Raise(nameof(ProgramSeekMaximum));
            Raise(nameof(ProgramDurationText));
            if (ReferenceEquals(sender, CurrentItem)) RaisePlaybackCounters();
        }

        if (e.PropertyName is nameof(PlaylistItem.Title) or nameof(PlaylistItem.EventType) or nameof(PlaylistItem.Category) or nameof(PlaylistItem.BlockName))
        {
            RefreshRundownBlocks();
            RefreshTimelineLayout();
        }

        if (e.PropertyName == nameof(PlaylistItem.Status) && _hidePlayedItems)
            System.Windows.Data.CollectionViewSource.GetDefaultView(Playlist)?.Refresh();

        if (ReferenceEquals(sender, SelectedItem) || ReferenceEquals(sender, CurrentItem))
            Raise(nameof(StatusDetail));

        if (e.PropertyName is not nameof(PlaylistItem.Status) and not nameof(PlaylistItem.StartTimeText))
            PersistPlaylist();
    }

    private void RaiseNextItems()
    {
        Raise(nameof(NextItem)); Raise(nameof(UpNextItem));
        Raise(nameof(NowPlayingAutoText)); Raise(nameof(NextAutoText)); Raise(nameof(UpNextAutoText));
        Raise(nameof(NextDurationText));
        Raise(nameof(CurrentBlockRemainingText));
        Ui(RefreshMusicPackage);
    }

    private void EvaluateAutoCg(double elapsedSeconds)
    {
        // The music package runs on its own switch; while it owns an item the classic
        // Now Playing / Coming Up lower thirds stay off so the two never overlap.
        if (_musicPackage is { } musicPackage)
        {
            musicPackage.Tick(CurrentItem, elapsedSeconds);
            if (musicPackage.Handles(CurrentItem)) return;
        }
        if (!EnableAutoCg || CurrentItem is null || CurrentItem.IsCgEvent || CurrentItem.IsControlEvent) return;
        var current = CurrentItem;

        var isComm = IsCommercialCategory(current.Category);
        if (isComm)
        {
            if (_commercialBlockId == Guid.Empty) _commercialBlockId = Guid.NewGuid();
            var breakRemaining = GetRemainingCommercialBreakDuration();
            if (EnableAutoBackIn && !_commercialBackInTriggered && breakRemaining <= BackInTriggerLeadSeconds && breakRemaining > 1)
            {
                _commercialBackInTriggered = true;
                _autoCgOnAirUntilSeconds = elapsedSeconds + Math.Max(breakRemaining, 10);
                var nextProg = Playlist.SkipWhile(x => x != current).Skip(1).FirstOrDefault(x => !IsCommercialCategory(x.Category));
                var backInProject = AutoCgFactory.CreateBackIn(breakRemaining, nextProg, DefaultBackInTemplate);
                Ui(() =>
                {
                    if (!ReferenceEquals(CurrentItem, current)) return;
                    ActiveCgProject = backInProject;
                    ActiveCgProject.OnAir = true;
                    _autoCgProjectActive = true;
                    AddRecentEvent($"AUTO CG · BACK IN · {CgPlaybackRuntime.FormatTimer(breakRemaining)}");
                    Raise(nameof(CgEnabled)); Raise(nameof(CgStatusText));
                });
            }
            return;
        }
        else
        {
            _commercialBlockId = Guid.Empty;
            _commercialBackInTriggered = false;
        }

        var catSetting = _settings.Categories.FirstOrDefault(c => c.Name.Equals(current.Category, StringComparison.OrdinalIgnoreCase))
            ?? _settings.Categories.FirstOrDefault(c => current.Category.Contains(c.Name, StringComparison.OrdinalIgnoreCase));

        var isMusic = current.Category.Contains("Music", StringComparison.OrdinalIgnoreCase);
        var isNews = current.Category.Contains("News", StringComparison.OrdinalIgnoreCase);
        var isProgram = current.Category.Contains("Program", StringComparison.OrdinalIgnoreCase) || (!isMusic && !isNews && (catSetting?.EnableAutoCg ?? false));

        var autoEnabled = catSetting?.EnableAutoCg ?? (isMusic || isNews || isProgram);
        if (!autoEnabled) return;

        if (_autoCgProjectActive && _autoCgOnAirUntilSeconds >= 0 && elapsedSeconds >= _autoCgOnAirUntilSeconds)
        {
            _autoCgOnAirUntilSeconds = -1;
            Ui(() =>
            {
                if (!ReferenceEquals(CurrentItem, current) || !_autoCgProjectActive) return;
                if (ActiveCgProject is not null) ActiveCgProject.OnAir = false;
                ActiveCgProject = null;
                _autoCgProjectActive = false;
                AddRecentEvent("AUTO CG · OUT");
                Raise(nameof(CgEnabled)); Raise(nameof(CgStatusText));
            });
        }

        var nowDelay = catSetting != null && catSetting.NowPlayingDelaySeconds > 0
            ? catSetting.NowPlayingDelaySeconds
            : (isMusic ? MusicNowPlayingDelaySeconds : isNews ? NewsNowPlayingDelaySeconds : ProgramNowPlayingDelaySeconds);
        var beforeEnd = catSetting != null && catSetting.ComingUpBeforeEndSeconds > 0
            ? catSetting.ComingUpBeforeEndSeconds
            : (isMusic ? MusicComingUpBeforeEndSeconds : isNews ? NewsComingUpBeforeEndSeconds : ProgramComingUpBeforeEndSeconds);
        var remaining = Math.Max(0, current.Duration.TotalSeconds - elapsedSeconds);

        if (!_autoNowPlayingShown && elapsedSeconds >= nowDelay)
        {
            _autoNowPlayingShown = true;
            _autoCgOnAirUntilSeconds = elapsedSeconds + AutoCgHoldSeconds;

            var tplName = !string.IsNullOrWhiteSpace(catSetting?.DefaultTemplate) ? catSetting.DefaultTemplate
                : isMusic ? (!string.IsNullOrWhiteSpace(_settings.Epg.DefaultMusicTemplate) ? _settings.Epg.DefaultMusicTemplate : "Music · Now Playing Lower Third")
                : isNews ? "News · Now Playing Lower Third"
                : (!string.IsNullOrWhiteSpace(_settings.Epg.DefaultProgramTemplate) ? _settings.Epg.DefaultProgramTemplate : "Program · Now Playing Lower Third");

            var project = AutoCgFactory.CreateNowPlaying(current, ResolveAutoCgTemplate(tplName));
            var label = MediaNameParser.Parse(current.FilePath, current.Title).Display;
            Ui(() =>
            {
                if (!ReferenceEquals(CurrentItem, current)) return;
                ActiveCgProject = project; ActiveCgProject.OnAir = true; _autoCgProjectActive = true;
                AddRecentEvent("AUTO CG · NOW PLAYING · " + label);
                Raise(nameof(CgEnabled)); Raise(nameof(CgStatusText));
            });
        }
        if (!_autoComingUpShown && beforeEnd > 0 && remaining <= beforeEnd && NextItem is PlaylistItem next)
        {
            _autoComingUpShown = true;
            _autoCgOnAirUntilSeconds = Math.Min(Math.Max(elapsedSeconds + 1, current.Duration.TotalSeconds - .05), elapsedSeconds + AutoCgHoldSeconds);

            var nextIsMusic = next.Category.Contains("Music", StringComparison.OrdinalIgnoreCase);
            var nextIsNews = next.Category.Contains("News", StringComparison.OrdinalIgnoreCase);

            var tplName = !string.IsNullOrWhiteSpace(catSetting?.ComingUpTemplate) ? catSetting.ComingUpTemplate
                : !string.IsNullOrWhiteSpace(_settings.Epg.NowNextTemplate) ? _settings.Epg.NowNextTemplate
                : nextIsMusic ? "Music · Coming Up Lower Third"
                : nextIsNews ? "News · Coming Up Lower Third"
                : "Program · Coming Up Lower Third";

            var project = AutoCgFactory.CreateComingUp(next, ResolveAutoCgTemplate(tplName), current);
            var label = MediaNameParser.Parse(next.FilePath, next.Title).Display;
            Ui(() =>
            {
                if (!ReferenceEquals(CurrentItem, current)) return;
                ActiveCgProject = project; ActiveCgProject.OnAir = true; _autoCgProjectActive = true;
                AddRecentEvent("AUTO CG · COMING UP · " + label);
                Raise(nameof(CgEnabled)); Raise(nameof(CgStatusText));
            });
        }
    }

    private CgProject? ResolveAutoCgTemplate(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return CgProjects.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public void StartBlockAtSelected()
    {
        if (Playlist.Count == 0) return;
        var target = SelectedItem ?? Playlist[0];

        // Repeated NEW BLOCK on an existing boundary must create another boundary, not
        // silently rename the same one. Pick the next unmarked item when possible.
        if (!string.IsNullOrWhiteSpace(target.BlockName))
        {
            var start = Math.Max(0, Playlist.IndexOf(target) + 1);
            target = Playlist.Skip(start).FirstOrDefault(item => string.IsNullOrWhiteSpace(item.BlockName));
            if (target is null)
            {
                AddRecentEvent("BLOCK · NO FOLLOWING ITEM AVAILABLE");
                return;
            }
        }

        var usedNames = Playlist
            .Select(item => item.BlockName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var number = 1;
        while (usedNames.Contains($"BLOCK {number}")) number++;

        target.BlockName = $"BLOCK {number}";
        SelectedItem = target;
        AddRecentEvent($"BLOCK CREATED · {target.BlockName} · {target.Title}");
        PersistPlaylist();
        Raise(nameof(CurrentBlockNameText));
        Raise(nameof(CurrentBlockRemainingText));
        RaisePlaylistSummary();
    }

    public void ClearBlockMarkerAtSelected()
    {
        if (SelectedItem is null) return;
        SelectedItem.BlockName = string.Empty;
        PersistPlaylist();
        Raise(nameof(CurrentBlockNameText));
        Raise(nameof(CurrentBlockRemainingText));
        RaisePlaylistSummary();
    }

    public void AddControlEvent(string eventType, bool insertAfterSelected = true, string? notes = null)
    {
        eventType = (eventType ?? "NOTE").Trim().ToUpperInvariant();
        var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "STOP","PLAY","PAUSE","RESUME","NEXT","NOTE","LOOP","SCTE35","SCTE104","CAPTION608","CAPTION708","DVBSUB","CUETONE","DIALTONE","DTMF","CGSHOW","CGHIDE","LOGOON","LOGOOFF","TCP","MOS","GPO","RS422","VDCP","SNMP"
        };
        if (!supported.Contains(eventType)) eventType = "NOTE";
        var category = eventType switch
        {
            "SCTE35" or "SCTE104" or "CUETONE" or "DIALTONE" or "DTMF" => "Signaling",
            "CAPTION608" or "CAPTION708" or "DVBSUB" => "Captions/Subtitles",
            "CGSHOW" or "CGHIDE" or "LOGOON" or "LOGOOFF" => "Graphics",
            "TCP" or "MOS" or "GPO" or "RS422" or "VDCP" or "SNMP" => "Automation",
            _ => "Control"
        };
        var controlDuration = eventType switch
        {
            "CUETONE" => TimeSpan.FromMilliseconds(850),
            "DIALTONE" => TimeSpan.FromMilliseconds(2500),
            "DTMF" => TimeSpan.FromMilliseconds(900),
            _ => TimeSpan.FromMilliseconds(80)
        };
        var item = new PlaylistItem
        {
            EventType = eventType, Category = category, SourceKind = "Control",
            Title = eventType == "NOTE" ? "Operator Note" : eventType + " EVENT",
            Notes = !string.IsNullOrWhiteSpace(notes) ? notes.Trim() : eventType == "NOTE" ? "Double-click to edit note" : "Automation control event",
            SourceDuration = controlDuration, InPoint = TimeSpan.Zero, OutPoint = controlDuration, Status = "Ready"
        };
        if (eventType == "DTMF") { item.DtmfSequence = "1234#"; item.DtmfToneMilliseconds = 120; item.DtmfGapMilliseconds = 55; item.DtmfLevelPercent = 72; }
        else if (eventType == "DIALTONE") { item.DialToneMilliseconds = 2500; item.DialToneLevelPercent = 55; }
        else if (eventType == "TCP") { item.TcpHost = "127.0.0.1"; item.TcpPort = 9101; item.TcpCommand = "PLAY"; }
        else if (eventType == "MOS") { item.MosAction = "UPDATE"; item.MosTitle = "MOS automation marker"; }
        else if (eventType == "SCTE35")
        {
            item.ScteSpliceType = "Start Normal"; item.SctePreRollMilliseconds = 4000; item.ScteDurationSeconds = 30;
            item.ScteUniqueProgramId = 1; item.ScteAutoReturn = true;
        }
        else if (eventType == "SCTE104")
        {
            item.ScteSpliceType = "Start Normal"; item.SctePreRollMilliseconds = 4000; item.ScteDurationSeconds = 30;
            item.ScteUniqueProgramId = 1; item.ScteAutoReturn = true;
        }
        else if (eventType == "CAPTION608")
        {
            item.CaptionText = "Kashtrix caption test"; item.Caption608Channel = "CC1"; item.Caption608Mode = "Pop-On"; item.Caption608Row = 15;
        }
        else if (eventType == "CAPTION708")
        {
            item.CaptionText = "Kashtrix caption test"; item.Caption708ServiceNumber = 1; item.Caption708Window = 0; item.Caption708Row = 14; item.Caption708Column = 0;
        }
        else if (eventType == "DVBSUB")
        {
            item.CaptionText = "Kashtrix subtitle test"; item.DvbLanguage = "eng"; item.DvbSubtitlingType = 0x10; item.DvbCompositionPageId = 1; item.DvbAncillaryPageId = 1; item.DvbTimeoutSeconds = 4;
        }
        else if (eventType == "GPO") { item.GpoOutput = 0; item.GpoAction = "Pulse"; item.GpoPulseMilliseconds = 250; item.GpoActiveHigh = true; }
        else if (eventType == "RS422") { item.Rs422Mode = "Raw Hex"; }
        else if (eventType == "VDCP") { item.VdcpCommand = "PLAY"; item.VdcpPort = 0; }
        else if (eventType == "SNMP")
        {
            item.SnmpHost = _settings.ProfessionalBroadcast.SnmpTrapHost; item.SnmpPort = _settings.ProfessionalBroadcast.SnmpTrapPort;
            item.SnmpCommunity = _settings.ProfessionalBroadcast.SnmpTrapCommunity; item.SnmpValue = "Kashtrix playout event";
        }
        if (eventType is "DTMF" or "DIALTONE" or "TCP" or "MOS" or "SCTE35" or "SCTE104" or "CAPTION608" or "CAPTION708" or "DVBSUB" or "GPO" or "RS422" or "VDCP" or "SNMP") item.RefreshLegacyControlNotes();
        var index = insertAfterSelected && SelectedItem is not null ? Playlist.IndexOf(SelectedItem) + 1 : Playlist.Count;
        InsertPlaylistItemAt(item, Math.Clamp(index, 0, Playlist.Count)); SelectedItem = item; RaisePlaylistSummary(); PersistPlaylist();
    }

    public bool MoveItemTo(PlaylistItem item, int targetIndex)
    {
        var oldIndex = Playlist.IndexOf(item); if (oldIndex < 0 || Playlist.Count == 0) return false;
        targetIndex = Math.Clamp(targetIndex, 0, Playlist.Count - 1); if (oldIndex == targetIndex) return false;
        Playlist.Move(oldIndex, targetIndex); RenumberPlaylist(); SelectedItem = item; RaisePlaylistSummary(); PersistPlaylist(); return true;
    }

    public IReadOnlyList<PlaylistItem> ReplaceWithPartEvents(PlaylistItem source)
    {
        var index = Playlist.IndexOf(source); if (index < 0) return [];
        var enabled = source.Parts.Where(x => x.Enabled && x.OutPoint > x.InPoint).OrderBy(x => x.Order).ToArray(); if (enabled.Length == 0) return [];
        source.PropertyChanged -= OnPlaylistItemPropertyChanged; Playlist.RemoveAt(index); var created = new List<PlaylistItem>();
        foreach (var part in enabled)
        {
            var item = JsonSerializer.Deserialize<PlaylistItem>(JsonSerializer.Serialize(source)) ?? new PlaylistItem();
            item.Id = Guid.NewGuid();
            item.Title = $"{source.Title} · {part.Name}";
            item.InPoint = part.InPoint; item.OutPoint = part.OutPoint; item.Parts = [];
            item.Notes = string.IsNullOrWhiteSpace(part.Note) ? source.Notes : part.Note;
            item.Status = "Ready";
            InsertPlaylistItemAt(item, index + created.Count); created.Add(item);
        }
        RenumberPlaylist(); SelectedItem = created.FirstOrDefault(); RaisePlaylistSummary(); PersistPlaylist(); return created;
    }

    public void AuditionPreviewAudio()
    {
        var item = SelectedItem; if (item is null || item.IsLiveSource || item.IsCgEvent || item.IsControlEvent || !FfmpegRuntime.IsInitialized) return;
        var source = new CancellationTokenSource(); CancelOnly(Interlocked.Exchange(ref _previewAudioCts, source));
        try { _previewAudio.Reset(); } catch { }
        var offset = Math.Clamp(PreviewSeekSeconds, 0, Math.Max(0, item.Duration.TotalSeconds));
        var target = item.MapEventOffset(TimeSpan.FromSeconds(offset)).SourcePosition;
        _ = Task.Run(() =>
        {
            try
            {
                using var decoder = new AudioDecoder(item); decoder.Seek(target); var first = double.NaN; var clock = System.Diagnostics.Stopwatch.StartNew();
                while (!source.IsCancellationRequested && decoder.TryRead(out var chunk))
                {
                    if (chunk.PtsSeconds < target.TotalSeconds - .1) continue; if (double.IsNaN(first)) first = chunk.PtsSeconds;
                    var local = chunk.PtsSeconds - first; if (local > 8.0) break;
                    while (!source.IsCancellationRequested && clock.Elapsed.TotalSeconds + .03 < local) Thread.Sleep(5);
                    if (!source.IsCancellationRequested) _previewAudio.Send(chunk);
                }
            }
            catch { }
            finally { if (ReferenceEquals(Interlocked.CompareExchange(ref _previewAudioCts, null, source), source)) source.Dispose(); }
        });
    }

    private void RaiseOutputProfileProperties()
    {
        Raise(nameof(NdiResolutionText));
        Raise(nameof(DeckLinkResolutionText));
        Raise(nameof(DisplayResolutionText));
    }

    private void RaisePlaybackCounters()
    {
        Raise(nameof(CurrentElapsedText));
        Raise(nameof(CurrentRemainingText));
        Raise(nameof(CurrentProgressPercent));
        Raise(nameof(CurrentBlockRemainingText));
        Raise(nameof(PlaylistPlayedTimeText));
        Raise(nameof(PlaylistRemainingTimeText));
        Raise(nameof(SelectedRemainingTimeText));
        Raise(nameof(CurrentBlockNameText));
        Raise(nameof(NextDurationText));
        Raise(nameof(TimelinePlayheadX));
        Raise(nameof(TimelinePlayheadText));
    }

    private void RaisePlaylistSummary()
    {
        Raise(nameof(PlaylistCountText));
        Raise(nameof(PlaylistDurationText));
        Raise(nameof(StatusDetail));
        RefreshRundownBlocks();
        RefreshTimelineLayout();
        PlayCommand.RaiseCanExecuteChanged();
        TakeSelectedCommand.RaiseCanExecuteChanged();
        NextCommand.RaiseCanExecuteChanged();
        RemoveCommand.RaiseCanExecuteChanged();
    }


    public void RefreshRundownBlocks()
    {
        RundownBlocks.Clear();
        if (Playlist.Count == 0) return;

        RundownBlockNode? current = null;
        for (var i = 0; i < Playlist.Count; i++)
        {
            var item = Playlist[i];
            if (current is null || !string.IsNullOrWhiteSpace(item.BlockName))
            {
                current = new RundownBlockNode
                {
                    Name = string.IsNullOrWhiteSpace(item.BlockName) ? (i == 0 ? "RUNDOWN" : $"BLOCK {RundownBlocks.Count + 1}") : item.BlockName,
                    Category = string.IsNullOrWhiteSpace(item.Category) ? "Mixed" : item.Category,
                    FirstIndex = i
                };
                RundownBlocks.Add(current);
            }
            current.Items.Add(item);
        }
    }

    public void SelectRundownBlock(RundownBlockNode? block)
    {
        if (block?.Items.FirstOrDefault() is PlaylistItem item) SelectedItem = item;
    }

    public bool PlayCgTemplateFromAutomation(string templateName, string? dataJson, int layer)
    {
        if (string.IsNullOrWhiteSpace(templateName)) return false;
        var source = CgProjects.FirstOrDefault(x => x.Name.Equals(templateName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (source is null) return false;

        // Clone the authored template so newsroom/MOS data never mutates the master project.
        var project = JsonSerializer.Deserialize<CgProject>(JsonSerializer.Serialize(source));
        if (project is null) return false;
        project.Id = Guid.NewGuid();
        project.OnAir = true;

        if (!string.IsNullOrWhiteSpace(dataJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(dataJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var cgLayer in project.Layers)
                    {
                        if (!cgLayer.Type.Equals("Text", StringComparison.OrdinalIgnoreCase) &&
                            !cgLayer.Type.Equals("Ticker", StringComparison.OrdinalIgnoreCase) &&
                            !cgLayer.Type.Equals("Roll", StringComparison.OrdinalIgnoreCase)) continue;

                        var key = !string.IsNullOrWhiteSpace(cgLayer.DataField) ? cgLayer.DataField : cgLayer.Name;
                        if (TryResolveTemplateJson(doc.RootElement, key, out var value)) cgLayer.Text = value;
                    }
                }
            }
            catch (JsonException ex)
            {
                AddRecentEvent("CG MOS DATA INVALID · " + ex.Message);
            }
        }

        // Layer is an external routing identity. The native compositor stacks complete projects;
        // encode the requested layer into the runtime identity while retaining authored sublayers.
        project.ExternalLayer = Math.Clamp(layer, 0, 999);
        project.Name = $"{source.Name} · MOS L{project.ExternalLayer}";
        lock (_controllerCgStackGate)
        {
            foreach (var existing in _controllerProgramStack.Where(x => x.ExternalLayer == project.ExternalLayer).ToArray())
            {
                existing.OnAir = false;
                _controllerProgramStack.Remove(existing);
            }
            _controllerProgramStack.Add(project);
        }
        _activeCgStartedUtc = DateTime.UtcNow;
        Raise(nameof(CgEnabled));
        Raise(nameof(CgStatusText));
        AddRecentEvent($"CG MOS PLAY · {source.Name} · layer {Math.Clamp(layer, 0, 999)}");
        return true;
    }

    private static bool TryResolveTemplateJson(JsonElement root, string? path, out string value)
    {
        value = string.Empty;
        if (root.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(path)) return false;
        var current = root;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (current.ValueKind != JsonValueKind.Object) return false;
            JsonElement next = default;
            var found = false;
            foreach (var property in current.EnumerateObject())
            {
                if (!property.Name.Equals(segment, StringComparison.OrdinalIgnoreCase)) continue;
                next = property.Value; found = true; break;
            }
            if (!found) return false;
            current = next;
        }
        value = current.ValueKind switch
        {
            JsonValueKind.String => current.GetString() ?? string.Empty,
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => current.ToString(),
            _ => current.GetRawText()
        };
        return true;
    }

    public void ShowActiveCg()
    {
        if (ActiveCgProject is null || !ActiveCgProject.OnAir)
        {
            var defGfx = _settings.DefaultGraphicsTemplate;
            if (!string.IsNullOrWhiteSpace(defGfx))
            {
                var target = CgProjects.FirstOrDefault(x => string.Equals(x.Name, defGfx, StringComparison.OrdinalIgnoreCase))
                    ?? CgProjects.FirstOrDefault(x => x.Name.Contains(defGfx, StringComparison.OrdinalIgnoreCase));
                if (target is not null) ActiveCgProject = target;
            }
        }
        if (ActiveCgProject is null) ActiveCgProject = CgProjects.FirstOrDefault();
        if (ActiveCgProject is null) return;
        CgEnabled = true;
        AddRecentEvent("CG SHOW · " + ActiveCgProject.Name);
    }

    public void ShowBackInGraphics()
    {
        var breakRemaining = GetRemainingCommercialBreakDuration();
        if (breakRemaining <= 0) breakRemaining = BackInTriggerLeadSeconds > 0 ? BackInTriggerLeadSeconds : 90;
        var nextProg = Playlist.SkipWhile(x => x != CurrentItem).Skip(1).FirstOrDefault(x => !IsCommercialCategory(x.Category));
        var backInProject = AutoCgFactory.CreateBackIn(breakRemaining, nextProg, DefaultBackInTemplate);
        ActiveCgProject = backInProject;
        ActiveCgProject.OnAir = true;
        _autoCgProjectActive = true;
        CgEnabled = true;
        AddRecentEvent($"CG SHOW · BACK IN · {CgPlaybackRuntime.FormatTimer(breakRemaining)}");
        Raise(nameof(CgEnabled)); Raise(nameof(CgStatusText));
    }

    public static bool IsCommercialCategory(string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return false;
        var cat = category.Trim();
        return cat.Equals("Commercial", StringComparison.OrdinalIgnoreCase) ||
               cat.Equals("Ad", StringComparison.OrdinalIgnoreCase) ||
               cat.Equals("Advert", StringComparison.OrdinalIgnoreCase) ||
               cat.Equals("Advertisement", StringComparison.OrdinalIgnoreCase) ||
               cat.Equals("Promo", StringComparison.OrdinalIgnoreCase) ||
               cat.Contains("Commercial", StringComparison.OrdinalIgnoreCase) ||
               cat.Contains("Advert", StringComparison.OrdinalIgnoreCase);
    }

    public double GetRemainingCommercialBreakDuration()
    {
        var current = CurrentItem;
        if (current is null) return 0;

        var pos = Math.Max(0, _currentElapsedSeconds);
        var remaining = Math.Max(0, current.Duration.TotalSeconds - pos);

        // If current item is a commercial, accumulate subsequent commercial items in the break
        if (IsCommercialCategory(current.Category))
        {
            var items = Playlist.ToList();
            var currentIndex = items.IndexOf(current);
            if (currentIndex < 0) currentIndex = items.FindIndex(x => x.Id == current.Id);

            if (currentIndex >= 0)
            {
                for (var i = currentIndex + 1; i < items.Count; i++)
                {
                    var item = items[i];
                    if (IsCommercialCategory(item.Category))
                    {
                        remaining += item.Duration.TotalSeconds;
                    }
                    else
                    {
                        // Hit non-commercial item (e.g. Program / News / Music), stop!
                        break;
                    }
                }
            }
        }
        return remaining;
    }

    public void HideActiveCg()
    {
        if (ActiveCgProject is null) return;
        CgEnabled = false;
        AddRecentEvent("CG HIDE · " + ActiveCgProject.Name);
    }

    public void SetLogoVisible(bool visible)
    {
        if (!visible)
        {
            _logoOverlayProject = null;
            AddRecentEvent("LOGO OFF");
            return;
        }

        // Logo is an independent PROGRAM overlay bus. Prefer configured default logo template,
        // then active project logo, then first saved project with logo/bug layer.
        var defaultLogoName = _settings.DefaultLogoTemplate;
        var source = !string.IsNullOrWhiteSpace(defaultLogoName)
            ? CgProjects.FirstOrDefault(x => string.Equals(x.Name, defaultLogoName, StringComparison.OrdinalIgnoreCase))
                ?? CgProjects.FirstOrDefault(x => x.Name.Contains(defaultLogoName, StringComparison.OrdinalIgnoreCase))
            : null;

        source ??= ActiveCgProject is not null && ActiveCgProject.Layers.Any(IsLogoLayer)
            ? ActiveCgProject
            : CgProjects.FirstOrDefault(x => x.Layers.Any(IsLogoLayer));
        if (source is null) { AddRecentEvent("LOGO ON · NO LOGO LAYER"); return; }

        var logoLayers = source.Layers.Where(IsLogoLayer).Select(CloneLogoLayer).ToList();
        if (logoLayers.Count == 0) return;
        var duration = Math.Max(3600, logoLayers.Max(x => Math.Max(1, x.EndSeconds - x.StartSeconds)));
        foreach (var layer in logoLayers)
        {
            layer.StartSeconds = 0;
            layer.EndSeconds = duration;
            layer.Visible = true;
        }
        _logoOverlayProject = new CgProject { Name = source.Name + " · LOGO BUS", Width = source.Width, Height = source.Height, FrameRate = source.FrameRate, DurationSeconds = duration, OnAir = true, Layers = logoLayers };
        _logoCgStartedUtc = DateTime.UtcNow;
        AddRecentEvent("LOGO ON · " + source.Name);
    }

    private static bool IsLogoLayer(CgLayer layer) =>
        layer.Role.Equals("Logo", StringComparison.OrdinalIgnoreCase) ||
        layer.Role.Equals("Bug", StringComparison.OrdinalIgnoreCase) ||
        layer.Name.Contains("Logo", StringComparison.OrdinalIgnoreCase) ||
        layer.Name.Contains("Bug", StringComparison.OrdinalIgnoreCase);

    private static CgLayer CloneLogoLayer(CgLayer x) => new()
    {
        Id=x.Id, Name=x.Name, Type=x.Type, Role=x.Role, ShapeKind=x.ShapeKind, Text=x.Text, Source=x.Source,
        X=x.X, Y=x.Y, Z=x.Z, Width=x.Width, Height=x.Height, ScaleX=x.ScaleX, ScaleY=x.ScaleY, ScaleZ=x.ScaleZ, RotationX=x.RotationX, RotationY=x.RotationY, AnchorX=x.AnchorX, AnchorY=x.AnchorY, AnchorZ=x.AnchorZ, Perspective=x.Perspective, FontSize=x.FontSize, FontFamily=x.FontFamily, Fill=x.Fill, Background=x.Background,
        StartSeconds=x.StartSeconds, EndSeconds=x.EndSeconds, AnimationIn=x.AnimationIn, AnimationOut=x.AnimationOut,
        AnimationInSeconds=x.AnimationInSeconds, AnimationOutSeconds=x.AnimationOutSeconds, Visible=x.Visible, Speed=x.Speed, Rotation=x.Rotation, Opacity=x.Opacity,
        CornerRadius=x.CornerRadius, CornerRadiusTopLeft=x.CornerRadiusTopLeft, CornerRadiusTopRight=x.CornerRadiusTopRight, CornerRadiusBottomRight=x.CornerRadiusBottomRight, CornerRadiusBottomLeft=x.CornerRadiusBottomLeft, CornerRadiusAsPercent=x.CornerRadiusAsPercent, CornerRadiusTopLeftPercent=x.CornerRadiusTopLeftPercent, CornerRadiusTopRightPercent=x.CornerRadiusTopRightPercent, CornerRadiusBottomRightPercent=x.CornerRadiusBottomRightPercent, CornerRadiusBottomLeftPercent=x.CornerRadiusBottomLeftPercent, BorderColor=x.BorderColor, BorderWidth=x.BorderWidth, HorizontalTextAlignment=x.HorizontalTextAlignment,
        VerticalTextAlignment=x.VerticalTextAlignment, Bold=x.Bold, Italic=x.Italic, Underline=x.Underline, OutlineColor=x.OutlineColor,
        OutlineWidth=x.OutlineWidth, ShadowColor=x.ShadowColor, ShadowOffsetX=x.ShadowOffsetX, ShadowOffsetY=x.ShadowOffsetY,
        ShadowBlur=x.ShadowBlur, LetterSpacing=x.LetterSpacing, LineSpacing=x.LineSpacing, TickerMode=x.TickerMode,
        TickerDirection=x.TickerDirection, TickerGap=x.TickerGap, TickerRepeat=x.TickerRepeat, SqueezeProgram=false
    };

    private void HandleControlEvent(PlaylistItem item)
    {
        var action = (item.EventType ?? "").Trim().ToUpperInvariant();
        switch (action)
        {
            case "CGSHOW": ShowActiveCg(); break;
            case "CGHIDE": HideActiveCg(); break;
            case "LOGOON": SetLogoVisible(true); break;
            case "LOGOOFF": SetLogoVisible(false); break;
            case "SCTE35":
                {
                    var cueId = _professional.SendScte35(item, (long)Math.Round(_currentElapsedSeconds * 90000));
                    AddRecentEvent($"SCTE-35 · {item.ScteSpliceType} · event={cueId} · pre-roll={item.SctePreRollMilliseconds}ms · duration={item.ScteDurationSeconds}s");
                    AutomationSignalRaised?.Invoke(action, item);
                    break;
                }
            case "SCTE104":
                {
                    var cueId = _professional.SendScte104(item);
                    AddRecentEvent($"SCTE-104 · {item.ScteSpliceType} · event={cueId} · pre-roll={item.SctePreRollMilliseconds}ms · duration={item.ScteDurationSeconds}s");
                    AutomationSignalRaised?.Invoke(action, item);
                    break;
                }
            case "CAPTION608":
                _professional.SendCaption608(item);
                AddRecentEvent($"CEA-608 · {item.Caption608Channel} · {item.Caption608Mode} · row {item.Caption608Row} · {item.CaptionText}");
                break;
            case "CAPTION708":
                _professional.SendCaption708(item, CurrentItem?.SourceFrameRate ?? 25);
                AddRecentEvent($"CEA-708 · service {item.Caption708ServiceNumber} · window {item.Caption708Window} · {item.CaptionText}");
                break;
            case "DVBSUB":
                if (_professional.OutputArmed)
                    _dvbSubtitles.SendText(_settings.ProfessionalBroadcast, item, (long)Math.Round(_currentElapsedSeconds * 90000));
                AddRecentEvent($"DVB SUBTITLE · {(_professional.OutputArmed ? "SENT" : "INHIBITED")} · {item.DvbLanguage} · page {item.DvbCompositionPageId}/{item.DvbAncillaryPageId} · {item.CaptionText}");
                break;
            case "GPO":
                _ = _professional.ExecuteGpoAsync(item);
                AddRecentEvent($"GPO {item.GpoOutput} · {item.GpoAction} · {(item.GpoAction.Equals("Pulse", StringComparison.OrdinalIgnoreCase) ? item.GpoPulseMilliseconds + " ms" : (item.GpoActiveHigh ? "active-high" : "active-low"))}");
                break;
            case "RS422":
                AddRecentEvent($"RS-422 {item.Rs422Mode} · {(_professional.SendRs422(item) ? "SENT" : "FAILED")}");
                break;
            case "VDCP":
                AddRecentEvent($"VDCP {item.VdcpCommand} · port {item.VdcpPort} · clip {item.VdcpClipId} · {(_professional.SendVdcp(item) ? "SENT" : "FAILED")}");
                break;
            case "SNMP":
                _ = _professional.SendSnmpTrapAsync(item);
                AddRecentEvent($"SNMPv2c TRAP · {item.SnmpHost}:{item.SnmpPort} · {item.SnmpTrapOid} · {item.SnmpValue}");
                break;
            case "CUETONE": case "DIALTONE": case "DTMF": case "TCP": case "MOS":
                AddRecentEvent($"{action} · {item.Notes}");
                AutomationSignalRaised?.Invoke(action, item);
                break;
        }
    }

    public void ExecuteExternalCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return;
        var raw = command.Trim();
        var parts = raw.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return;
        switch (parts[0].ToUpperInvariant())
        {
            case "PLAY": PlayOrResume(); break;
            case "PAUSE": ToggleCurrentPause(); break;
            case "STOP": Stop(); break;
            case "NEXT": Next(); break;
            case "TAKE":
                if (parts.Length > 1 && int.TryParse(parts[1], out var oneBased) && oneBased >= 1 && oneBased <= Playlist.Count) SelectedItem = Playlist[oneBased - 1];
                TakeSelected();
                break;
            case "CG":
                if (parts.Length > 1 && parts[1].Equals("SHOW", StringComparison.OrdinalIgnoreCase)) ShowActiveCg();
                else if (parts.Length > 1 && parts[1].Equals("HIDE", StringComparison.OrdinalIgnoreCase)) HideActiveCg();
                break;
            case "LOGO":
                SetLogoVisible(parts.Length < 2 || !parts[1].Equals("OFF", StringComparison.OrdinalIgnoreCase));
                break;
            case "CUE": CueSelected(); break;
            case "DTMF":
                _ = TestDtmfAsync(parts.Length > 1 ? parts[1] : "1234#");
                AddRecentEvent("TCP DTMF · " + (parts.Length > 1 ? parts[1] : "1234#"));
                break;
            case "DIALTONE":
                _ = TestDialToneAsync();
                AddRecentEvent("TCP DIAL TONE");
                break;
            case "TONE":
                _ = TestLineupToneAsync();
                AddRecentEvent("TCP 1 KHZ LINE-UP TONE");
                break;
            default: AddRecentEvent("TCP CMD · " + raw); break;
        }
    }

    public Task TestDtmfAsync(string sequence, int toneMilliseconds = 120, int gapMilliseconds = 55, double levelPercent = 72)
    {
        AuditLogService.Write("DTMF_TEST", ChannelName, $"sequence={sequence};tone={toneMilliseconds};gap={gapMilliseconds};level={levelPercent}%");
        return _engine.PlayDiagnosticDtmfAsync(sequence, toneMilliseconds, gapMilliseconds, Math.Clamp(levelPercent / 100.0, 0.02, 1.0));
    }

    public Task TestDialToneAsync(int durationMilliseconds = 2500, double levelPercent = 55)
    {
        AuditLogService.Write("DIAL_TONE_TEST", ChannelName, $"350+440Hz;duration={durationMilliseconds};level={levelPercent}%");
        return _engine.PlayDiagnosticToneAsync(350, 440, durationMilliseconds, Math.Clamp(levelPercent / 100.0, 0.02, 1.0));
    }

    public Task TestLineupToneAsync(int durationMilliseconds = 1200, double levelPercent = 45)
    {
        AuditLogService.Write("LINE_TONE_TEST", ChannelName, $"1000Hz;duration={durationMilliseconds};level={levelPercent}%");
        return _engine.PlayDiagnosticToneAsync(1000, 0, durationMilliseconds, Math.Clamp(levelPercent / 100.0, 0.02, 1.0));
    }

    public void ApplyMosItem(string action, string itemId, string title, TimeSpan? duration = null)
    {
        action = (action ?? "").ToUpperInvariant();
        var marker = "MOS-ID=" + itemId;
        var existing = Playlist.FirstOrDefault(x => x.Notes.Contains(marker, StringComparison.OrdinalIgnoreCase));
        if (action.Contains("DELETE"))
        {
            if (existing is not null) { existing.PropertyChanged -= OnPlaylistItemPropertyChanged; Playlist.Remove(existing); }
        }
        else if (action.Contains("CUE"))
        {
            if (existing is not null)
            {
                SelectedItem = existing;
                CueSelected();
            }
        }
        else if (action.Contains("PLAY") || action.Contains("TAKE"))
        {
            if (existing is not null)
            {
                SelectedItem = existing;
                TakeSelected();
            }
        }
        else if (existing is null)
        {
            var d = duration.GetValueOrDefault(TimeSpan.FromSeconds(10));
            var item = new PlaylistItem { EventType="NOTE", Category="MOS", SourceKind="Control", Title=string.IsNullOrWhiteSpace(title)?"MOS ITEM":title, Notes=marker, SourceDuration=d, InPoint=TimeSpan.Zero, OutPoint=d, Status="Ready" };
            InsertPlaylistItemAt(item, Playlist.Count);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(title)) existing.Title = title;
            if (duration is TimeSpan d && d > TimeSpan.Zero) { existing.SourceDuration = d; existing.OutPoint = d; }
        }
        RenumberPlaylist(); RaisePlaylistSummary(); PersistPlaylist();
    }

    private void RefreshTimelineLayout()
    {
        TimelineBlocks.Clear();
        if (Playlist.Count == 0)
        {
            _timelineStartPlaylistIndex = 0;
            _timelineWindowStart = DateTime.Now;
            Raise(nameof(TimelineDateText));
            Raise(nameof(TimelineStartText));
            Raise(nameof(TimelineMidText));
            Raise(nameof(TimelineEndText));
            Raise(nameof(TimelinePlayheadX));
            Raise(nameof(TimelinePlayheadText));
            return;
        }

        var currentIndex = CurrentItem is null ? -1 : Playlist.IndexOf(CurrentItem);
        var anchorIndex = currentIndex >= 0 ? currentIndex : SelectedItem is not null ? Playlist.IndexOf(SelectedItem) : 0;
        if (anchorIndex < 0) anchorIndex = 0;

        var starts = new DateTime[Playlist.Count];
        var anchorClock = currentIndex >= 0 ? _currentItemTimelineStart : DateTime.Now;
        starts[anchorIndex] = anchorClock;
        for (var i = anchorIndex - 1; i >= 0; i--)
            starts[i] = starts[i + 1] - Playlist[i].Duration;
        for (var i = anchorIndex + 1; i < Playlist.Count; i++)
            starts[i] = starts[i - 1] + Playlist[i - 1].Duration;

        for (var i = 0; i < Playlist.Count; i++)
        {
            Playlist[i].StartTimeText = FormatRundownStartTime(starts[i], Playlist[i].SourceFrameRate);
            Playlist[i].EndTimeText = FormatRundownStartTime(starts[i] + Playlist[i].Duration, Playlist[i].SourceFrameRate);
        }

        _timelineStartPlaylistIndex = Math.Max(0, anchorIndex - 2);
        var visible = Playlist.Skip(_timelineStartPlaylistIndex).Take(8).ToArray();
        var weights = visible.Select(x => Math.Max(0.25, x.Duration.TotalSeconds)).ToArray();
        var weightTotal = Math.Max(0.25, weights.Sum());
        _timelineWindowStart = starts[_timelineStartPlaylistIndex];

        var left = 0.0;
        for (var i = 0; i < visible.Length; i++)
        {
            var item = visible[i];
            var playlistIndex = _timelineStartPlaylistIndex + i;
            var width = i == visible.Length - 1 ? TimelineCanvasWidth - left : TimelineCanvasWidth * weights[i] / weightTotal;
            if (left + width > TimelineCanvasWidth) width = Math.Max(1, TimelineCanvasWidth - left);
            var isCurrent = ReferenceEquals(item, CurrentItem);
            var palette = TimelinePalette(item, isCurrent);
            TimelineBlocks.Add(new RundownTimelineItem
            {
                PlaylistIndex = playlistIndex, Sequence = item.Sequence, Title = item.Title,
                DurationText = FormatClock(item.Duration), StartText = starts[playlistIndex].ToString("HH:mm:ss"),
                Background = palette.Background, Border = palette.Border, Left = left, Width = Math.Max(1, width - 3), IsCurrent = isCurrent
            });
            left += width;
            if (left >= TimelineCanvasWidth - 1) break;
        }

        Raise(nameof(TimelineDateText));
        Raise(nameof(TimelineStartText));
        Raise(nameof(TimelineMidText));
        Raise(nameof(TimelineEndText));
        Raise(nameof(TimelinePlayheadX));
        Raise(nameof(TimelinePlayheadText));
    }

    private static string FormatRundownStartTime(DateTime time, double frameRate)
    {
        var fps = Math.Clamp((int)Math.Round(frameRate <= 0 ? 25 : frameRate), 1, 120);
        var frame = Math.Clamp((int)Math.Floor(time.Millisecond * fps / 1000.0), 0, fps - 1);
        return $"{time:HH:mm:ss}:{frame:00}";
    }

    private static (string Background, string Border) TimelinePalette(PlaylistItem item, bool current)
    {
        if (current) return ("#4C286C", "#E1BA35");
        if (item.IsCgEvent) return ("#4B276E", "#9B63CB");
        if (item.IsLiveSource) return ("#5D2025", "#B64D57");
        if (item.EventType.Equals("AD", StringComparison.OrdinalIgnoreCase) || item.Category.Contains("Commercial", StringComparison.OrdinalIgnoreCase)) return ("#705419", "#A17B31");
        if (item.Category.Contains("News", StringComparison.OrdinalIgnoreCase)) return ("#412263", "#70439A");
        return ("#213E70", "#3E6198");
    }


    private void ApplyGlobalPlaylistAppearance()
    {
        foreach (var item in Playlist) ApplyGlobalPlaylistAppearance(item);
        Raise(nameof(PlaylistDefaultRowColor));
        Raise(nameof(PlaylistDefaultTextColor));
        PersistPlaylist();
    }

    private void ApplyGlobalPlaylistAppearance(PlaylistItem item)
    {
        item.RowColor = string.IsNullOrWhiteSpace(PlaylistDefaultRowColor) ? string.Empty : PlaylistDefaultRowColor;
        item.RowTextColor = PlaylistDefaultTextColor;
    }

    private void AttachPlaylistItem(PlaylistItem item, bool addToPlaylist = true)
    {
        item.PropertyChanged -= OnPlaylistItemPropertyChanged;
        item.PropertyChanged += OnPlaylistItemPropertyChanged;
        ApplyGlobalPlaylistAppearance(item);
        if (addToPlaylist) Playlist.Add(item);

        // Restore the source-monitor wall after restart/import as well as when an input
        // is created interactively. File clips stay in the rundown only; live/network
        // sources are registered once and can be previewed/mixed independently.
        if (!item.IsControlEvent && !item.IsCgEvent &&
            (item.IsLiveSource || !item.SourceKind.Equals("File", StringComparison.OrdinalIgnoreCase)))
            RegisterInputSource(item);
    }

    private void AttachOutputRoute(OutputRoute route)
    {
        route.PropertyChanged += OnOutputRouteChanged;
        route.Profile.PropertyChanged += (_, _) => OnOutputRouteChanged(route, new System.ComponentModel.PropertyChangedEventArgs(nameof(OutputRoute.Profile)));
        AdditionalOutputs.Add(route);
    }

    private void OnOutputRouteChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OutputRoute.Status)) return;
        PersistState();
        _multiNdi.Synchronize(AdditionalOutputs);
    }

    private static List<OutputRoute> CreateDefaultOutputRoutes() =>
    [
        new OutputRoute { Name = "NDI CLEAN FEED", Kind = "NDI", SourceName = "KASHTRIX-CLEAN-01", Enabled = false, FramesPerSecond = 25, Profile = new OutputProfile { Preset = "1280x720", FramesPerSecond = 25 } },
        new OutputRoute { Name = "NDI SOCIAL / 50P", Kind = "NDI", SourceName = "KASHTRIX-AUX-50P", Enabled = false, FramesPerSecond = 50, Profile = new OutputProfile { Preset = "1920x1080", FramesPerSecond = 50 } }
    ];

    public void AddOutputRoute()
    {
        var index = AdditionalOutputs.Count + 2;
        var route = new OutputRoute
        {
            Name = $"NDI OUTPUT {index}",
            Kind = "NDI",
            SourceName = $"KASHTRIX-OUTPUT-{index:00}",
            Enabled = false,
            FramesPerSecond = 25,
            Profile = new OutputProfile { Preset = "1920x1080", FramesPerSecond = 25 }
        };
        AttachOutputRoute(route);
        PersistState();
    }

    public void RemoveOutputRoute(OutputRoute? route)
    {
        if (route is null) return;
        route.PropertyChanged -= OnOutputRouteChanged;
        AdditionalOutputs.Remove(route);
        _multiNdi.Synchronize(AdditionalOutputs);
        PersistState();
    }

    public void AddSchedule()
    {
        Schedules.Add(new ScheduleEntry { Name = $"Schedule {Schedules.Count + 1}", StartAt = DateTime.Now.AddMinutes(5), Repeat = "One Time", Enabled = true });
        SaveSchedules();
    }

    public void RemoveSchedule(ScheduleEntry? entry)
    {
        if (entry is null) return;
        Schedules.Remove(entry);
        SaveSchedules();
    }

    public void SaveSchedules()
    {
        _scheduler.SetEntries(Schedules);
        PersistState();
    }

    private void OnScheduleDue(ScheduleEntry entry)
    {
        Ui(() =>
        {
            try
            {
                if (entry.EventType.Equals("STOP", StringComparison.OrdinalIgnoreCase)) { Stop(); return; }
                if (entry.EventType.Equals("NEXT", StringComparison.OrdinalIgnoreCase)) { Next(); return; }
                if (entry.EventType.Equals("PAUSE", StringComparison.OrdinalIgnoreCase)) { if (!_engine.IsPaused) _engine.Pause(); return; }
                if (entry.EventType.Equals("RESUME", StringComparison.OrdinalIgnoreCase) || entry.EventType.Equals("PLAY", StringComparison.OrdinalIgnoreCase)) { if (_engine.IsPaused) { _engine.Resume(); return; } }
                if (!string.IsNullOrWhiteSpace(entry.PlaylistPath) && File.Exists(entry.PlaylistPath))
                {
                    if (entry.PlaylistPath.EndsWith(".kashtrix", StringComparison.OrdinalIgnoreCase) ||
                        entry.PlaylistPath.EndsWith(".kashtrixplaylist", StringComparison.OrdinalIgnoreCase) ||
                        entry.PlaylistPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        ReplacePlaylist(PlaylistDocumentService.Load(entry.PlaylistPath));
                    }
                    else
                    {
                        var info = MediaProbe.Read(entry.PlaylistPath);
                        var item = new PlaylistItem
                        {
                            Title = Path.GetFileNameWithoutExtension(entry.PlaylistPath),
                            FilePath = entry.PlaylistPath,
                            SourceKind = "File",
                            EventType = "VIDEO",
                            InPoint = TimeSpan.Zero,
                            OutPoint = info.Duration,
                            SourceDuration = info.Duration,
                            SourceFrameRate = info.FramesPerSecond,
                            Codec = info.VideoCodec,
                            VideoFormat = $"{info.Width}x{info.Height} {info.FramesPerSecond:0.###}p"
                        };
                        ReplacePlaylist([item]);
                    }
                }
                if (Playlist.Count == 0) return;
                SelectedItem = Playlist[0]; _localAudio.Reset();
                // Hard Start deliberately interrupts current Program so the first scheduled event starts at the exact configured clock.
                if (entry.IsHardStart) _engine.Stop();
                _ = _engine.PlayAsync(Playlist.ToArray(), 0, Playlist[0].InPoint);
            }
            catch (Exception ex)
            {
                EngineStatus = "SCHEDULER ERROR: " + ex.Message;
            }
        });
    }

    public void SavePlaylistDocument()
    {
        var dialog = new WpfSaveFileDialog { Filter = "Kashtrix Playlist (*.kashtrixplaylist;*.kashtrix)|*.kashtrixplaylist;*.kashtrix|All files (*.*)|*.*", DefaultExt = ".kashtrixplaylist", FileName = "channel-playlist.kashtrixplaylist" };
        if (dialog.ShowDialog() != true) return;
        PlaylistDocumentService.Save(dialog.FileName, Playlist);
    }

    public void OpenPlaylistDocument() => ImportPlaylistClean();

    public void ImportPlaylistClean(string? filePath = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            var dialog = new WpfOpenFileDialog { Filter = "Kashtrix Playlist (*.kashtrixplaylist;*.kashtrix)|*.kashtrixplaylist;*.kashtrix|All files (*.*)|*.*" };
            if (dialog.ShowDialog() != true) return;
            filePath = dialog.FileName;
        }
        try
        {
            ReplacePlaylist(PlaylistDocumentService.Load(filePath));
            AddRecentEvent($"IMPORT PLAYLIST · {System.IO.Path.GetFileName(filePath)}");
        }
        catch (Exception ex) { WpfMessageBox.Show(ex.Message, "Import Kashtrix Playlist"); }
    }

    public void AppendPlaylistBottom(string? filePath = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            var dialog = new WpfOpenFileDialog { Filter = "Kashtrix Playlist (*.kashtrixplaylist;*.kashtrix)|*.kashtrixplaylist;*.kashtrix|All files (*.*)|*.*" };
            if (dialog.ShowDialog() != true) return;
            filePath = dialog.FileName;
        }
        try
        {
            var loaded = PlaylistDocumentService.Load(filePath).ToList();
            if (loaded.Count == 0) return;
            foreach (var item in loaded)
            {
                item.Status = "Ready";
                AttachPlaylistItem(item);
            }
            RenumberPlaylist();
            RaisePlaylistSummary();
            PersistPlaylist();
            AddRecentEvent($"APPEND BOTTOM · {loaded.Count} items from {System.IO.Path.GetFileName(filePath)}");
        }
        catch (Exception ex) { WpfMessageBox.Show(ex.Message, "Append Kashtrix Playlist"); }
    }

    public void AppendPlaylistHere(string? filePath = null, int? targetIndex = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            var dialog = new WpfOpenFileDialog { Filter = "Kashtrix Playlist (*.kashtrixplaylist;*.kashtrix)|*.kashtrixplaylist;*.kashtrix|All files (*.*)|*.*" };
            if (dialog.ShowDialog() != true) return;
            filePath = dialog.FileName;
        }
        try
        {
            var loaded = PlaylistDocumentService.Load(filePath).ToList();
            if (loaded.Count == 0) return;
            var insertPos = targetIndex ?? (SelectedItem is not null ? Playlist.IndexOf(SelectedItem) + 1 : Playlist.Count);
            insertPos = Math.Clamp(insertPos, 0, Playlist.Count);
            for (var i = 0; i < loaded.Count; i++)
            {
                var item = loaded[i];
                item.Status = "Ready";
                AttachPlaylistItem(item, addToPlaylist: false);
                Playlist.Insert(insertPos + i, item);
            }
            RenumberPlaylist();
            RaisePlaylistSummary();
            PersistPlaylist();
            AddRecentEvent($"APPEND HERE · {loaded.Count} items at row {insertPos + 1}");
        }
        catch (Exception ex) { WpfMessageBox.Show(ex.Message, "Append Kashtrix Playlist"); }
    }

    public void NewPlaylist()
    {
        Stop();
        foreach (var item in Playlist) item.PropertyChanged -= OnPlaylistItemPropertyChanged;
        Playlist.Clear();
        SelectedItem = null;
        RaisePlaylistSummary();
        PersistPlaylist();
    }

    private void ReplacePlaylist(IEnumerable<PlaylistItem> items)
    {
        Stop();
        foreach (var old in Playlist) old.PropertyChanged -= OnPlaylistItemPropertyChanged;
        Playlist.Clear();
        foreach (var item in items)
        {
            item.Status = "Ready";
            AttachPlaylistItem(item);
        }
        RenumberPlaylist();
        SelectedItem = Playlist.FirstOrDefault();
        RaisePlaylistSummary();
        PersistPlaylist();
    }

    private void RestartControllerClient()
    {
        _channelClient?.Dispose(); _channelClient = null; ControllerStatus = "OFF";
        if (_allowControllerClient && ConnectToController) StartControllerClient();
    }

    private void StartControllerClient()
    {
        try
        {
            _channelClient = new ChannelClientService();
            _channelClient.StatusChanged += status => Ui(() => ControllerStatus = status);
            _channelClient.CgCommandReceived += command => Ui(() => ApplyRemoteCg(command));
            _channelClient.Start(ControllerHost, ControllerPort, ChannelId, ChannelName, BuildChannelStatus);
        }
        catch { ControllerStatus = "ERROR"; }
    }

    private ChannelStatusSnapshot BuildChannelStatus()
    {
        var current = CurrentItem; var next = SelectedItem;
        return new ChannelStatusSnapshot
        {
            ChannelId = ChannelId, ChannelName = ChannelName, State = current is null ? EngineStatus : "ON AIR",
            ProgramTitle = current?.Title ?? "", NextTitle = next?.Title ?? "", Format = current?.VideoFormat ?? PlayoutPreset,
            ElapsedSeconds = _currentElapsedSeconds, AudioLeft = AudioLeftLevel, AudioRight = AudioRightLevel,
            ProgramPreviewJpegBase64 = _controllerProgramPreview, PreviewJpegBase64 = _controllerCuePreview, LastSeenUtc = DateTime.UtcNow
        };
    }

    private void ApplyRemoteCg(CgRemoteCommand command)
    {
        if (!AcceptRemoteCgCommand(command)) return;
        var bus = string.Equals(command.Bus, "PREVIEW", StringComparison.OrdinalIgnoreCase) ? "PREVIEW" : "PROGRAM";
        var stop = command.Action.Equals("STOP", StringComparison.OrdinalIgnoreCase) ||
                   command.Action.Equals("CLEAR", StringComparison.OrdinalIgnoreCase);

        if (bus == "PREVIEW")
        {
            if (stop)
            {
                var isInstantClear = command.Action.Equals("CLEAR", StringComparison.OrdinalIgnoreCase);
                if (isInstantClear || PreviewCgProject is null)
                {
                    PreviewCgProject = null;
                    AddRecentEvent("CG PREVIEW · CLEAR");
                }
                else
                {
                    var currentProj = PreviewCgProject;
                    currentProj.IsStopping = true;
                    currentProj.StopRequestedTimelineSeconds = Math.Max(0, (DateTime.UtcNow - _previewCgStartedUtc).TotalSeconds);
                    var maxOut = currentProj.Layers.Where(l => l.Visible).Select(l => l.AnimationOutSeconds).DefaultIfEmpty(0.0).Max();
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(TimeSpan.FromSeconds(maxOut + 0.15)).ConfigureAwait(false);
                        Ui(() =>
                        {
                            if (ReferenceEquals(PreviewCgProject, currentProj))
                            {
                                PreviewCgProject = null;
                                AddRecentEvent("CG PREVIEW · STOPPED");
                            }
                        });
                    });
                }
                return;
            }
            if (command.Project is null) return;
            // OnAir here means "render enabled" inside CgCompositor; bus separation is
            // provided by PreviewCgProject vs ActiveCgProject, so PREVIEW must also be enabled.
            command.Project.OnAir = true;
            if (command.Layer >= 0) command.Project.ExternalLayer = command.Layer;
            _previewCgStartedUtc = DateTime.UtcNow;
            command.Project.StartedUtc = DateTime.UtcNow;
            PreviewCgProject = command.Project;
            AddRecentEvent($"CG PREVIEW · {command.Action} · {command.Project.Name}");
            return;
        }

        if (stop)
        {
            var isInstantClear = command.Action.Equals("CLEAR", StringComparison.OrdinalIgnoreCase);
            lock (_controllerCgStackGate)
            {
                CgProject[] targets;
                if (command.Project is not null)
                {
                    // Prioritize exact project match by Id or Name so stopping Live never stops Station Logo Bug or other layers
                    targets = _controllerProgramStack.Where(x =>
                        x.Id == command.Project.Id ||
                        string.Equals(x.Name, command.Project.Name, StringComparison.OrdinalIgnoreCase)).ToArray();

                    if (command.Layer >= 0 && targets.Length > 1)
                    {
                        var onLayer = targets.Where(x => x.ExternalLayer == command.Layer).ToArray();
                        if (onLayer.Length > 0) targets = onLayer;
                    }

                    if (targets.Length == 0 && command.Layer >= 0)
                    {
                        targets = _controllerProgramStack.Where(x => x.ExternalLayer == command.Layer).ToArray();
                    }
                }
                else if (command.Layer >= 0)
                {
                    targets = _controllerProgramStack.Where(x => x.ExternalLayer == command.Layer).ToArray();
                }
                else
                {
                    targets = _controllerProgramStack.ToArray();
                }

                foreach (var project in targets)
                {
                    if (isInstantClear)
                    {
                        project.OnAir = false;
                        _controllerProgramStack.Remove(project);
                    }
                    else
                    {
                        // Stop with out-animation!
                        project.IsStopping = true;
                        project.StopRequestedTimelineSeconds = Math.Max(0, (DateTime.UtcNow - (project.StartedUtc == default ? _activeCgStartedUtc : project.StartedUtc)).TotalSeconds);
                        var maxOut = project.Layers.Where(l => l.Visible).Select(l => l.AnimationOutSeconds).DefaultIfEmpty(0.5).Max();
                        var pRef = project;
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(TimeSpan.FromSeconds(maxOut + 0.15)).ConfigureAwait(false);
                            Ui(() =>
                            {
                                lock (_controllerCgStackGate)
                                {
                                    pRef.OnAir = false;
                                    _controllerProgramStack.Remove(pRef);
                                }
                                Raise(nameof(CgEnabled)); Raise(nameof(CgStatusText));
                            });
                        });
                    }
                }

                if (command.Layer < 0 && command.Project is null && isInstantClear)
                {
                    _controllerProgramStack.Clear();
                }

                if (_controllerCgTransition is not null)
                {
                    var transition = _controllerCgTransition;
                    var touchesStoppedLayer = (command.Layer < 0 && command.Project is null) ||
                        (transition.Outgoing != null && targets.Contains(transition.Outgoing)) ||
                        targets.Contains(transition.Incoming);
                    if (touchesStoppedLayer) _controllerCgTransition = null;
                }
            }
            Raise(nameof(CgEnabled)); Raise(nameof(CgStatusText));
            var targetDesc = command.Project?.Name ?? (command.Layer >= 0 ? $"LAYER {command.Layer}" : "ALL");
            AddRecentEvent($"CG PROGRAM · {(isInstantClear ? "CLEAR" : "STOP (OUT)")} · {targetDesc}");
            return;
        }
        CgProject? incoming = command.Project;
        if (command.Action.Equals("TAKE", StringComparison.OrdinalIgnoreCase) && incoming is null && PreviewCgProject is not null)
        {
            incoming = JsonSerializer.Deserialize<CgProject>(JsonSerializer.Serialize(PreviewCgProject));
            PreviewCgProject = null;
        }
        if (incoming is null) return;
        incoming.OnAir = true;
        incoming.IsStopping = false;
        incoming.StopRequestedTimelineSeconds = -1;
        incoming.StartedUtc = DateTime.UtcNow;
        if (command.Layer >= 0) incoming.ExternalLayer = command.Layer;
        lock (_controllerCgStackGate)
        {
            // PLAY is additive when Transition=None; TAKE with None follows the same current behavior.
            // A transition is an explicit operator choice: it replaces only the current top
            // controller composition after the transition while leaving lower stacked graphics on-air.
            if (command.Action.Equals("UPDATE", StringComparison.OrdinalIgnoreCase))
            {
                var same = command.Layer >= 0
                    ? _controllerProgramStack.LastOrDefault(x => x.ExternalLayer == command.Layer)
                    : _controllerProgramStack.LastOrDefault(x => x.Id == incoming.Id);
                if (same is not null) { same.OnAir = false; _controllerProgramStack.Remove(same); }
            }

            // Remove existing instances of the same project, layer or name so playing multiple times never multiplies PIP squeezes!
            var duplicates = _controllerProgramStack
                .Where(x => (incoming.ExternalLayer >= 0 && x.ExternalLayer == incoming.ExternalLayer) ||
                            x.Id == incoming.Id ||
                            string.Equals(x.Name, incoming.Name, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            foreach (var dup in duplicates)
            {
                dup.OnAir = false;
                _controllerProgramStack.Remove(dup);
            }

            var transitionMode = string.IsNullOrWhiteSpace(command.Transition) ? "None" : command.Transition.Trim();
            var useTransition = command.Action.Equals("TAKE", StringComparison.OrdinalIgnoreCase) &&
                                !transitionMode.Equals("None", StringComparison.OrdinalIgnoreCase);
            if (useTransition)
            {
                var outgoing = _controllerProgramStack.LastOrDefault(x => x.OnAir);
                _controllerProgramStack.Add(incoming);
                _controllerCgTransition = new ControllerCgTransitionState
                {
                    Outgoing = outgoing,
                    Incoming = incoming,
                    Mode = transitionMode,
                    DurationSeconds = Math.Clamp(command.TransitionSeconds, .05, 5.0),
                    StartedUtc = DateTime.UtcNow
                };
            }
            else
            {
                _controllerProgramStack.Add(incoming);
                if (command.Action.Equals("TAKE", StringComparison.OrdinalIgnoreCase)) _controllerCgTransition = null;
            }
        }
        if (_controllerProgramStack.Count <= 1 || _activeCgStartedUtc == default) _activeCgStartedUtc = DateTime.UtcNow;
        Raise(nameof(CgEnabled)); Raise(nameof(CgStatusText));
        _ = LoadPreviewAsync(SelectedItem, PreviewSeekSeconds, debounce: false);
        var transitionEvent = !string.IsNullOrWhiteSpace(command.Transition) && !command.Transition.Equals("None", StringComparison.OrdinalIgnoreCase)
            ? $" · {command.Transition} {Math.Clamp(command.TransitionSeconds,.05,5.0):0.00}s"
            : string.Empty;
        AddRecentEvent($"CG PROGRAM · {command.Action} · {incoming.Name} · {(incoming.ExternalLayer >= 0 ? $"L{incoming.ExternalLayer}" : "STACK")}{transitionEvent}");
    }

    private bool AcceptRemoteCgCommand(CgRemoteCommand command)
    {
        // Older external controllers may omit CommandId; keep those compatible.
        if (string.IsNullOrWhiteSpace(command.CommandId)) return true;
        lock (_cgCommandGate)
        {
            if (!_recentCgCommandIds.Add(command.CommandId)) return false;
            _recentCgCommandOrder.Enqueue(command.CommandId);
            while (_recentCgCommandOrder.Count > 512)
            {
                var expired = _recentCgCommandOrder.Dequeue();
                _recentCgCommandIds.Remove(expired);
            }
        }
        return true;
    }

    private void StartPlatformControlWatcher()
    {
        try
        {
            _platformControlWatcher?.Dispose();
            var folder = PlatformControlBus.Inbox(ChannelId);
            Directory.CreateDirectory(folder);
            _platformControlWatcher = new FileSystemWatcher(folder, "*.json")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };
            _platformControlWatcher.Created += (_, e) => QueuePlatformControlCommand(e.FullPath);
            _platformControlWatcher.Renamed += (_, e) => QueuePlatformControlCommand(e.FullPath);
            foreach (var pending in Directory.EnumerateFiles(folder, "*.json")) QueuePlatformControlCommand(pending);
        }
        catch (Exception ex) { RuntimeDiagnosticsService.WriteException("PLATFORM_CONTROL_WATCHER", ex); }
    }

    private void QueuePlatformControlCommand(string path)
    {
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                if (!File.Exists(path)) return;
                if (PlatformControlBus.TryConsume(path, out var command) && command is not null)
                {
                    Ui(() => ExecutePlatformControlCommand(command));
                    return;
                }
                await Task.Delay(35).ConfigureAwait(false);
            }
        });
    }

    private void ExecutePlatformControlCommand(PlatformControlCommand command)
    {
        try
        {
            var action = (command.Action ?? string.Empty).Trim().ToLowerInvariant();
            switch (action)
            {
                case "reload_playlist":
                    ReloadPlaylistFromDatabase();
                    PlatformControlBus.Complete(command, true, "OK", "Playlist reloaded from platform database.");
                    break;
                case "take":
                case "play":
                    SelectPlatformItem(command);
                    if (!_engine.IsPlaying || action == "take") TakeSelected(); else if (_engine.IsPaused) _engine.Resume();
                    PlatformControlBus.Complete(command, true, "OK", "Program command accepted.", StatusData());
                    break;
                case "play_story":
                    var storyId = command.Parameters.TryGetValue("storyId", out var sid) ? sid : string.Empty;
                    var slug = command.Parameters.TryGetValue("slug", out var slugValue) ? slugValue : string.Empty;
                    var storyItem = Playlist.FirstOrDefault(x => !string.IsNullOrWhiteSpace(storyId) && !x.EventType.Equals("MOS", StringComparison.OrdinalIgnoreCase) && x.Notes.Contains("NRCS " + storyId, StringComparison.OrdinalIgnoreCase))
                        ?? Playlist.FirstOrDefault(x => !string.IsNullOrWhiteSpace(slug) && !x.EventType.Equals("MOS", StringComparison.OrdinalIgnoreCase) && x.Title.Contains(slug, StringComparison.OrdinalIgnoreCase))
                        ?? Playlist.FirstOrDefault(x => !string.IsNullOrWhiteSpace(storyId) && string.Equals(x.MosItemId, storyId, StringComparison.OrdinalIgnoreCase));
                    if (storyItem is null) throw new InvalidOperationException($"Published NRCS story is not present in the active playout rundown: {slug}");
                    SelectedItem = storyItem;
                    TakeSelected();
                    PlatformControlBus.Complete(command, true, "OK", "NRCS story selected and taken to Program.", StatusData());
                    break;
                case "next":
                    Next();
                    PlatformControlBus.Complete(command, true, "OK", "NEXT accepted.", StatusData());
                    break;
                case "pause":
                case "hold":
                    if (_engine.IsPlaying && !_engine.IsPaused) ToggleCurrentPause();
                    PlatformControlBus.Complete(command, true, "OK", action.ToUpperInvariant() + " accepted.", StatusData());
                    break;
                case "resume":
                    if (_engine.IsPlaying && _engine.IsPaused) ToggleCurrentPause();
                    PlatformControlBus.Complete(command, true, "OK", "RESUME accepted.", StatusData());
                    break;
                case "stop":
                    Stop();
                    PlatformControlBus.Complete(command, true, "OK", "STOP accepted.", StatusData());
                    break;
                case "jip":
                    SelectPlatformItem(command);
                    var offset = command.Parameters.TryGetValue("offsetSeconds", out var o) && double.TryParse(o, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? Math.Max(0, d) : 0;
                    if (SelectedItem is null) throw new InvalidOperationException("No selected playlist item for JIP.");
                    var idx = Playlist.IndexOf(SelectedItem);
                    _localAudio.Reset();
                    _ = _engine.PlayAsync(Playlist.ToArray(), idx, TimeSpan.FromSeconds(Math.Min(offset, SelectedItem.Duration.TotalSeconds)));
                    PlatformControlBus.Complete(command, true, "OK", "JIP accepted.", StatusData());
                    break;
                case "output_arm":
                    var armed = command.Parameters.TryGetValue("armed", out var a) && bool.TryParse(a, out var parsed) && parsed;
                    ProfessionalOutputArmed = armed;
                    PlatformControlBus.Complete(command, true, "OK", armed ? "Output manually armed." : "Output manually inhibited.", StatusData());
                    break;
                default:
                    PlatformControlBus.Complete(command, false, "UNSUPPORTED_ACTION", $"Unsupported playout action: {command.Action}");
                    break;
            }
        }
        catch (Exception ex)
        {
            PlatformControlBus.Complete(command, false, "COMMAND_FAILED", ex.GetBaseException().Message);
            RuntimeDiagnosticsService.WriteException("PLATFORM_COMMAND_FAILED", ex);
        }
    }

    private void SelectPlatformItem(PlatformControlCommand command)
    {
        if (!command.Parameters.TryGetValue("itemId", out var text) || !Guid.TryParse(text, out var id)) return;
        var item = Playlist.FirstOrDefault(x => x.Id == id);
        if (item is not null) SelectedItem = item;
    }

    private Dictionary<string, string> StatusData() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["channelId"] = ChannelId,
        ["engine"] = EngineStatus,
        ["currentItemId"] = CurrentItem?.Id.ToString("D") ?? string.Empty,
        ["currentTitle"] = CurrentItem?.Title ?? string.Empty,
        ["outputArmed"] = ProfessionalOutputArmed.ToString(),
        ["ha"] = _professional.HaStatus
    };

    private void SynchronizeWarmStandby(string? itemId, double positionSeconds)
    {
        if (ProfessionalOutputArmed || string.IsNullOrWhiteSpace(itemId) || !Guid.TryParse(itemId, out var id)) return;
        var item = Playlist.FirstOrDefault(x => x.Id == id);
        if (item is null) return;
        var index = Playlist.IndexOf(item);
        if (index < 0) return;
        var drift = CurrentItem?.Id == id ? Math.Abs(_currentElapsedSeconds - positionSeconds) : double.MaxValue;
        if (drift < 2.0) return;
        SelectedItem = item;
        _localAudio.Reset();
        _ = _engine.PlayAsync(Playlist.ToArray(), index, TimeSpan.FromSeconds(Math.Clamp(positionSeconds, 0, item.Duration.TotalSeconds)));
        AddRecentEvent($"HA WARM SYNC · {item.Title} · {positionSeconds:0.0}s");
    }

    private void StartLocalCgCommandWatcher()
    {
        try
        {
            _localCgWatcher?.Dispose();
            _localCgWatcher = null;
            var folder = LocalCgCommandBus.GetInbox(ChannelId);
            Directory.CreateDirectory(folder);
            _localCgWatcher = new FileSystemWatcher(folder, "*.json")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };
            _localCgWatcher.Created += (_, e) => QueueLocalCgCommand(e.FullPath);
            _localCgWatcher.Renamed += (_, e) => QueueLocalCgCommand(e.FullPath);
            foreach (var pending in Directory.EnumerateFiles(folder, "*.json")) QueueLocalCgCommand(pending);
        }
        catch { }
    }

    private void QueueLocalCgCommand(string path)
    {
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                await Task.Delay(20 + attempt * 25).ConfigureAwait(false);
                if (!LocalCgCommandBus.TryConsume(path, out var command) || command is null) continue;
                Ui(() => ApplyRemoteCg(command));
                return;
            }
        });
    }


    private void StartSettingsSignalWatcher()
    {
        try
        {
            var localFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KashtrixPlayout");
            Directory.CreateDirectory(localFolder);
            _settingsSignalWatcher = new FileSystemWatcher(localFolder, "settings.reload") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName, EnableRaisingEvents = true };
            _settingsSignalWatcher.Changed += (_, _) => Ui(ReloadSettingsFromDisk);
            _settingsSignalWatcher.Created += (_, _) => Ui(ReloadSettingsFromDisk);

            var roamingFolder = Path.GetDirectoryName(SettingsStore.SettingsPath)!;
            Directory.CreateDirectory(roamingFolder);
            _settingsSignalWatcherRoaming = new FileSystemWatcher(roamingFolder, "settings.reload") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName, EnableRaisingEvents = true };
            _settingsSignalWatcherRoaming.Changed += (_, _) => Ui(ReloadSettingsFromDisk);
            _settingsSignalWatcherRoaming.Created += (_, _) => Ui(ReloadSettingsFromDisk);
        }
        catch { }
    }

    public void ReloadSettingsFromDisk()
    {
        try
        {
            _settings = SettingsStore.Load();
            _playlistRowHeight = Math.Clamp(_settings.PlaylistRowHeight, 22, 96);
            _playlistFontSize = Math.Clamp(_settings.PlaylistFontSize, 8.0, 28.0);
            _playlistDefaultRowColor = _settings.PlaylistDefaultRowColor ?? string.Empty;
            _playlistDefaultTextColor = string.IsNullOrWhiteSpace(_settings.PlaylistDefaultTextColor) ? "#D9DEE3" : _settings.PlaylistDefaultTextColor;
            _dimCompletedItems = _settings.DimCompletedItems;
            _markPlayedItemsCompleted = _settings.MarkPlayedItemsCompleted;
            _autoRemovePlayedItems = _settings.AutoRemovePlayedItems;
            _hidePlayedItems = _settings.HidePlayedItems;
            _showPlaylistThumbnails = _settings.ShowPlaylistThumbnails;
            _defaultLogoTemplate = _settings.DefaultLogoTemplate ?? "Logo · Station Ident";
            _defaultGraphicsTemplate = _settings.DefaultGraphicsTemplate ?? "Prime HD · Breaking News Live";
            _programNowPlayingDelaySeconds = Math.Clamp(_settings.ProgramNowPlayingDelaySeconds, 0, 600);
            _programComingUpBeforeEndSeconds = Math.Clamp(_settings.ProgramComingUpBeforeEndSeconds, 0, 600);

            Categories.Clear();
            foreach (var cat in _settings.Categories ?? AppSettings.GetDefaultCategories())
                Categories.Add(cat);

            Raise(nameof(PlaylistRowHeight));
            Raise(nameof(PlaylistFontSize));
            Raise(nameof(PlaylistDefaultRowColor));
            Raise(nameof(PlaylistDefaultTextColor));
            Raise(nameof(DimCompletedItems));
            Raise(nameof(MarkPlayedItemsCompleted));
            Raise(nameof(ShowPlaylistThumbnails));
            Raise(nameof(DefaultLogoTemplate));
            Raise(nameof(DefaultGraphicsTemplate));
            Raise(nameof(DefaultBackInTemplate));
            Raise(nameof(EnableAutoBackIn));
            Raise(nameof(BackInTriggerLeadSeconds));
            Raise(nameof(ProgramNowPlayingDelaySeconds));
            Raise(nameof(ProgramComingUpBeforeEndSeconds));
            Raise(nameof(Categories));
            Raise(nameof(Settings));

            ApplyGlobalPlaylistAppearance();
            AddRecentEvent("SETTINGS · SYNCHRONIZED");
        }
        catch { }
    }

    private void StartScheduleSignalWatcher()
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KashtrixPlayout");
            Directory.CreateDirectory(folder);
            _scheduleSignalWatcher = new FileSystemWatcher(folder, "scheduler.reload") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName, EnableRaisingEvents = true };
            _scheduleSignalWatcher.Changed += (_, _) => Ui(ReloadSchedulesFromDatabase);
            _scheduleSignalWatcher.Created += (_, _) => Ui(ReloadSchedulesFromDatabase);
        }
        catch { }
    }


    private void StartPlaylistSignalWatcher()
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KashtrixPlayout");
            Directory.CreateDirectory(folder);
            _playlistSignalWatcher = new FileSystemWatcher(folder, "playlist.reload") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName, EnableRaisingEvents = true };
            _playlistSignalWatcher.Changed += (_, _) => Ui(RequestPlaylistReload);
            _playlistSignalWatcher.Created += (_, _) => Ui(RequestPlaylistReload);
        }
        catch { }
    }

    private void RequestPlaylistReload()
    {
        if (_engine.IsPlaying || CurrentItem is not null) { _playlistReloadPending = true; return; }
        ReloadPlaylistFromDatabase();
    }

    public void ReloadPlaylistFromDatabase()
    {
        try
        {
            if (_engine.IsPlaying || CurrentItem is not null) { _playlistReloadPending = true; return; }
            var items = _database?.LoadPlaylist() ?? [];
            foreach (var old in Playlist) old.PropertyChanged -= OnPlaylistItemPropertyChanged;
            Playlist.Clear();
            foreach (var item in items) { item.Status = "Ready"; AttachPlaylistItem(item); }
            RenumberPlaylist();
            SelectedItem = Playlist.FirstOrDefault();
            RaisePlaylistSummary();
            _playlistReloadPending = false;
        }
        catch { }
    }

    public void ReloadSchedulesFromDatabase()
    {
        try
        {
            var items = _database?.LoadState("schedules", new List<ScheduleEntry>()) ?? [];
            Schedules.Clear();
            foreach (var item in items) Schedules.Add(item);
            _scheduler.SetEntries(Schedules);
        }
        catch { }
    }

    public void ReloadCgProjectsFromDatabase()
    {
        try
        {
            var activeId = ActiveCgProject?.Id ?? Guid.Empty;
            var items = _database?.LoadState("cg-projects", new List<CgProject>()) ?? [];
            if (items.Count == 0) return;
            CgProjects.Clear();
            foreach (var project in items) { CgProjectSanitizer.Sanitize(project); CgProjects.Add(project); }
            // Controllers can reload an older persisted demo bank after startup. Re-merge the
            // canonical 200 demos here as well so every TAKE uses the repaired full-duration
            // layers/keyframes rather than stale templates from a previous Kashtrix version.
            MergeMissingCgDemos();
            ActiveCgProject = CgProjects.FirstOrDefault(x => x.Id == activeId) ?? CgProjects.FirstOrDefault();
            Raise(nameof(CgStatusText));
            Raise(nameof(CgEnabled));
        }
        catch { }
    }

    public void SaveCgProjects()
    {
        PersistState();
        Raise(nameof(CgStatusText));
        Raise(nameof(CgEnabled));
        CgProjectsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void EnsureCgDemos()
    {
        var before = CgProjects.Count;
        var activeId = ActiveCgProject?.Id ?? Guid.Empty;
        MergeMissingCgDemos();
        ActiveCgProject = activeId != Guid.Empty ? CgProjects.FirstOrDefault(x => x.Id == activeId) ?? CgProjects.FirstOrDefault() : CgProjects.FirstOrDefault();
        if (CgProjects.Count != before || activeId != Guid.Empty) PersistState();
    }

    public void RebuildCgDemoPack()
    {
        var defaults = CgDemoFactory.CreateDefaults(forceRefresh: true);
        var canonicalNames = defaults.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var activeName = ActiveCgProject?.Name ?? string.Empty;
        var activeWasGenerated = canonicalNames.Contains(activeName) || activeName.StartsWith("Demo ", StringComparison.OrdinalIgnoreCase);

        // Remove both the canonical catalog and legacy demos,
        // while leaving operator-imported and custom projects untouched.
        var remove = CgProjects.Where(x => x.Name.StartsWith("Demo ", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var project in remove) CgProjects.Remove(project);
        foreach (var project in defaults)
        {
            if (!CgProjects.Any(x => string.Equals(x.Name, project.Name, StringComparison.OrdinalIgnoreCase)))
                CgProjects.Add(project);
        }

        ActiveCgProject = activeWasGenerated
            ? CgProjects.FirstOrDefault(x => string.Equals(x.Name, activeName, StringComparison.OrdinalIgnoreCase)) ?? CgProjects.FirstOrDefault()
            : ActiveCgProject ?? CgProjects.FirstOrDefault();
        PersistState();
        Raise(nameof(CgStatusText));
        Raise(nameof(CgEnabled));
        AuditLogService.Write("CG_DEMO_REBUILD", ChannelName, $"Rebuilt {defaults.Count} canonical broadcast CG compositions.");
    }

    private void MergeMissingCgDemos()
    {
        var demos = CgDemoFactory.CreateDefaults(forceRefresh: false);
        var demoNames = demos.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var legacy = CgProjects.Where(x => x.Name.StartsWith("Demo ", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var old in legacy) CgProjects.Remove(old);

        for (var i = 0; i < demos.Count; i++)
        {
            var demo = demos[i];
            var existing = CgProjects.FirstOrDefault(x => string.Equals(x.Name, demo.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                var index = CgProjects.IndexOf(existing);
                demo.Id = existing.Id;
                demo.OnAir = existing.OnAir;
                // Do not overwrite user customized demo with factory default
                continue;
            }
            CgProjects.Add(demo);
        }
    }

    public void LoadDemoWorkspace()
    {
        Stop();

        foreach (var old in Playlist)
            old.PropertyChanged -= OnPlaylistItemPropertyChanged;
        Playlist.Clear();
        foreach (var item in DemoDataFactory.CreatePlaylist())
            AttachPlaylistItem(item);
        RenumberPlaylist();
        SelectedItem = Playlist.FirstOrDefault();
        RaisePlaylistSummary();

        var demoPackageFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KashtrixPlayout", "Demo");
        Directory.CreateDirectory(demoPackageFolder);
        var demoPackage = Path.Combine(demoPackageFolder, "kashtrix-demo-rundown.kashtrix");
        PlaylistDocumentService.Save(demoPackage, Playlist);

        Schedules.Clear();
        foreach (var schedule in DemoDataFactory.CreateSchedules())
        {
            if (!schedule.EventType.Equals("Filler", StringComparison.OrdinalIgnoreCase))
                schedule.PlaylistPath = demoPackage;
            Schedules.Add(schedule);
        }
        _scheduler.SetEntries(Schedules);

        CgProjects.Clear();
        foreach (var project in CgDemoFactory.CreateDefaults(forceRefresh: true))
        {
            project.OnAir = false;
            CgProjects.Add(project);
        }
        ActiveCgProject = CgProjects.FirstOrDefault(x => x.Name.Contains(_settings.DefaultGraphicsTemplate, StringComparison.OrdinalIgnoreCase))
            ?? CgProjects.FirstOrDefault(x => x.Name.Contains("Prime HD", StringComparison.OrdinalIgnoreCase) && x.Name.Contains("Breaking", StringComparison.OrdinalIgnoreCase))
            ?? CgProjects.FirstOrDefault();
        if (ActiveCgProject is not null) ActiveCgProject.OnAir = false;

        PersistPlaylist();
        PersistState();
        Raise(nameof(CgStatusText));
        Raise(nameof(CgEnabled));
    }

    private void PersistPlaylist()
    {
        try { _database?.SavePlaylist(Playlist); }
        catch (Exception ex) { _databaseStatus = "DB ERROR · " + ex.Message; Raise(nameof(DatabaseStatus)); }
    }

    public void PersistSettings() => PersistState();

    public void PersistState()
    {
        try
        {
            _database?.SaveState("output-routes", AdditionalOutputs.ToList());
            _database?.SaveState("schedules", Schedules.ToList());
            _database?.SaveState("cg-projects", CgProjects.ToList());
            _database?.SaveState("active-cg-id", ActiveCgProject?.Id.ToString() ?? "");
        }
        catch (Exception ex)
        {
            _databaseStatus = "DB ERROR · " + ex.Message;
            Raise(nameof(DatabaseStatus));
        }
    }

    private static string LevelToDbText(double normalized)
    {
        if (normalized <= 0.00001) return "-∞ dB";
        var db = normalized * 60.0 - 60.0;
        return $"{db:0.0} dB";
    }

    private static (double LeftLevel, double RightLevel, string LeftDb, string RightDb) MeasureStereoPcm16(byte[] pcm)
    {
        if (pcm.Length < 4) return (0, 0, "-∞ dB", "-∞ dB");

        var leftPeak = 0;
        var rightPeak = 0;
        for (var i = 0; i + 3 < pcm.Length; i += 4)
        {
            var l = Math.Abs((int)BitConverter.ToInt16(pcm, i));
            var r = Math.Abs((int)BitConverter.ToInt16(pcm, i + 2));
            if (l > leftPeak) leftPeak = l;
            if (r > rightPeak) rightPeak = r;
        }

        return Meter(leftPeak / 32768.0, rightPeak / 32768.0);
    }

    private static (double LeftLevel, double RightLevel, string LeftDb, string RightDb) Meter(double leftLinear, double rightLinear)
    {
        static (double Level, string Text) One(double linear)
        {
            if (linear <= 0.00001) return (0, "-∞ dB");
            var db = Math.Clamp(20.0 * Math.Log10(linear), -60.0, 0.0);
            var normalized = (db + 60.0) / 60.0;
            return (normalized, $"{db:0.0} dB");
        }

        var l = One(leftLinear);
        var r = One(rightLinear);
        return (l.Level, r.Level, l.Text, r.Text);
    }

    private static string CreateControllerThumbnail(VideoFrameData frame)
    {
        try
        {
            var source = System.Windows.Media.Imaging.BitmapSource.Create(frame.Width, frame.Height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, frame.Bgra, frame.Stride);
            source.Freeze();
            var scale = Math.Min(320.0 / Math.Max(1, frame.Width), 180.0 / Math.Max(1, frame.Height));
            System.Windows.Media.Imaging.BitmapSource output = source;
            if (scale < .999)
            {
                var transformed = new System.Windows.Media.Imaging.TransformedBitmap(source, new System.Windows.Media.ScaleTransform(scale, scale));
                transformed.Freeze();
                output = transformed;
            }
            var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 55 };
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(output));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return Convert.ToBase64String(ms.ToArray());
        }
        catch { return string.Empty; }
    }

    private static string FormatClock(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        var totalHours = (int)value.TotalHours;
        return $"{totalHours:00}:{value.Minutes:00}:{value.Seconds:00}";
    }

    private const int RecentEventCapacity = 200;

    public void AddRecentEvent(string message)
    {
        if (Volatile.Read(ref _disposed) != 0 || string.IsNullOrWhiteSpace(message)) return;
        RecentEvents.Insert(0, new RecentEventEntry(DateTime.Now, message));
        AuditLogService.Write("OPERATOR_EVENT", CurrentItem?.Title ?? SelectedItem?.Title ?? "", message);
        while (RecentEvents.Count > RecentEventCapacity) RecentEvents.RemoveAt(RecentEvents.Count - 1);
    }

    private void Ui(Action action)
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        var app = WpfApplication.Current;
        if (app is null) return;
        var dispatcher = app.Dispatcher;
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;

        try
        {
            if (dispatcher.CheckAccess())
            {
                if (Volatile.Read(ref _disposed) == 0) action();
            }
            else
            {
                dispatcher.BeginInvoke(new Action(() =>
                {
                    if (Volatile.Read(ref _disposed) == 0 &&
                        !dispatcher.HasShutdownStarted &&
                        !dispatcher.HasShutdownFinished)
                        action();
                }));
            }
        }
        catch (InvalidOperationException)
        {
            // Dispatcher shutdown race; no UI work is required after shutdown starts.
        }
        catch (TaskCanceledException)
        {
            // Dispatcher queue was torn down during application exit.
        }
    }

    public AppSettings GetSettingsSnapshot() => SettingsStore.Load();

    public void ApplyChannelPreset(ChannelPreset preset)
    {
        ChannelName = preset.ChannelName;
        ChannelId = string.IsNullOrWhiteSpace(preset.ChannelId) ? ChannelId : preset.ChannelId;
        PlayoutPreset = preset.PlayoutPreset;
        NdiName = preset.NdiSourceName;
        NdiProfile.LoadFrom(preset.NdiProfile); DeckLinkProfile.LoadFrom(preset.DeckLinkProfile); DisplayProfile.LoadFrom(preset.DisplayProfile);
        // Primary Program routes always inherit the selected channel raster/cadence/scan mode.
        // Route-specific scaling/alpha/preroll remain intact; additional outputs stay independent.
        SynchronizePrimaryOutputProfilesToChannel();
        _settings.QcPreset = preset.QcPreset?.Clone() ?? new MediaQcPreset();
        EnableQcOnImport = preset.EnableQcOnImport;
        VirtualOutputName = preset.VirtualOutputName;
        VirtualOutputWidth = preset.VirtualOutputWidth;
        VirtualOutputHeight = preset.VirtualOutputHeight;
        VirtualOutputFrameRate = preset.VirtualOutputFrameRate;
        EnableMasterOutputVolume = preset.EnableMasterOutputVolume;
        MasterOutputVolumePercent = preset.MasterOutputVolumePercent;
        MasterOutputMuted = preset.MasterOutputMuted;
        MasterMuteShortcut = preset.MasterMuteShortcut;
        MasterVolumeUpShortcut = preset.MasterVolumeUpShortcut;
        MasterVolumeDownShortcut = preset.MasterVolumeDownShortcut;
        EnableVideoProcessing = preset.EnableVideoProcessing;
        VideoBrightness = preset.VideoBrightness; VideoContrast = preset.VideoContrast; VideoSaturation = preset.VideoSaturation; VideoGamma = preset.VideoGamma;
        VideoHueDegrees = preset.VideoHueDegrees; VideoExposureStops = preset.VideoExposureStops; VideoTemperature = preset.VideoTemperature; VideoTint = preset.VideoTint;
        VideoSharpness = preset.VideoSharpness; VideoBlackLevel = preset.VideoBlackLevel; VideoWhiteLevel = preset.VideoWhiteLevel;
        VideoRedGainPercent = preset.VideoRedGainPercent; VideoGreenGainPercent = preset.VideoGreenGainPercent; VideoBlueGainPercent = preset.VideoBlueGainPercent;
        FillerEnabled = preset.EnableFiller;
        FillerFolder = preset.FillerFolder;
        VirtualOutputEnabled = preset.EnableVirtualOutput;
        Raise(nameof(QcPreset)); SaveSettings();
    }

    private void WriteRuntimeDiagnosticsSnapshot()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            var current = CurrentItem;
            var selected = SelectedItem;
            RuntimeDiagnosticsService.Write("PLAYOUT_STATE", EngineStatus, new
            {
                channel = ChannelName,
                channelId = ChannelId,
                preset = PlayoutPreset,
                engine = EngineStatus,
                current = current?.Title ?? "",
                currentPath = current?.FilePath ?? "",
                elapsedSeconds = Math.Round(_currentElapsedSeconds, 3),
                durationSeconds = Math.Round(current?.Duration.TotalSeconds ?? 0, 3),
                selected = selected?.Title ?? "",
                decoderPreference = current?.DecoderPreference ?? DefaultDecoderPreference,
                mediaProperties = current?.MediaPropertiesSummary ?? "",
                audio = new
                {
                    leftDb = AudioLeftDb,
                    rightDb = AudioRightDb,
                    leftLevel = Math.Round(AudioLeftLevel, 4),
                    rightLevel = Math.Round(AudioRightLevel, 4),
                    masterPercent = MasterOutputVolumePercent,
                    masterMuted = MasterOutputMuted,
                    localEnabled = LocalAudioEnabled,
                    programDevice = _programAudioDeviceNumber,
                    previewDevice = _previewAudioDeviceNumber
                },
                videoProcessing = new
                {
                    enabled = EnableVideoProcessing,
                    brightness = VideoBrightness, contrast = VideoContrast, saturation = VideoSaturation, gamma = VideoGamma,
                    hue = VideoHueDegrees, exposure = VideoExposureStops, temperature = VideoTemperature, tint = VideoTint,
                    sharpness = VideoSharpness, blackLevel = VideoBlackLevel, whiteLevel = VideoWhiteLevel,
                    redGain = VideoRedGainPercent, greenGain = VideoGreenGainPercent, blueGain = VideoBlueGainPercent
                },
                outputs = new
                {
                    ndi = NdiEnabled, ndiStatus = NdiStatus,
                    deckLink = DeckLinkEnabled,
                    virtualOutput = VirtualOutputEnabled, virtualName = _virtualOutputName,
                    controller = ControllerStatus
                },
                cg = new
                {
                    program = _activeCgProject?.Name ?? "",
                    preview = _previewCgProject?.Name ?? "",
                    logo = _logoOverlayProject?.Name ?? ""
                },
                fullscreen = FullscreenEnabled,
                playlistCount = Playlist.Count
            });
            PlatformControlBus.PublishStatus(new PlatformChannelStatus
            {
                ChannelId = ChannelId,
                ChannelName = ChannelName,
                Engine = EngineStatus,
                CurrentItemId = current?.Id.ToString("D") ?? string.Empty,
                CurrentTitle = current?.Title ?? string.Empty,
                SelectedItemId = selected?.Id.ToString("D") ?? string.Empty,
                SelectedTitle = selected?.Title ?? string.Empty,
                ElapsedSeconds = Math.Round(_currentElapsedSeconds, 3),
                DurationSeconds = Math.Round(current?.Duration.TotalSeconds ?? 0, 3),
                OutputArmed = ProfessionalOutputArmed,
                HaStatus = _professional.HaStatus,
                CapabilityStatus = ProfessionalBroadcastStatus
            });
            RuntimeDiagnosticsService.WriteProcessHeartbeat(EngineStatus);
        }
        catch (Exception ex)
        {
            RuntimeDiagnosticsService.WriteException("PLAYOUT_TELEMETRY_ERROR", ex);
        }
    }

    private static void CancelOnly(CancellationTokenSource? source)
    {
        if (source is null) return;
        try { source.Cancel(); } catch (ObjectDisposedException) { }
        // The owning preview task disposes its source in finally. Disposing here can race
        // stale workers that are still checking IsCancellationRequested.
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try { Interlocked.Exchange(ref _runtimeDiagnosticsTimer, null)?.Dispose(); } catch { }
        RuntimeDiagnosticsService.Write("PROCESS_EXIT", "MainViewModel disposed.");
        CancelOnly(Interlocked.Exchange(ref _previewCts, null));
        CancelOnly(Interlocked.Exchange(ref _previewAudioCts, null));
        CancelOnly(Interlocked.Exchange(ref _previewPlaybackCts, null));
        StopCgPreviewLoop();
        try { _previewPauseGate.Set(); } catch { }
        foreach (var item in Playlist) item.PropertyChanged -= OnPlaylistItemPropertyChanged;
        try { SaveSettings(); } catch { }
        try { PersistPlaylist(); PersistState(); } catch { }
        try { _channelClient?.Dispose(); } catch { }
        try { _scheduleSignalWatcher?.Dispose(); } catch { }
        try { _playlistSignalWatcher?.Dispose(); } catch { }
        try { _settingsSignalWatcher?.Dispose(); } catch { }
        try { _settingsSignalWatcherRoaming?.Dispose(); } catch { }
        try { _localCgWatcher?.Dispose(); } catch { }
        try { _platformControlWatcher?.Dispose(); } catch { }
        try { _scheduler.Dispose(); } catch { }
        try { _scheduleRunner?.Dispose(); } catch { }
        MusicPackageRuntime.GetProgress = null;
        _engine.VideoFrame -= OnVideo;
        _engine.AudioFrame -= OnAudio;
        _engine.AudioMeterFrame -= OnAudioMeter;
        _engine.ProgressChanged -= OnProgress;
        try { _engine.Dispose(); } catch { }
        var cadenceCts = Interlocked.Exchange(ref _programCadenceCts, null);
        try { cadenceCts?.Cancel(); } catch { }
        try { _programCadenceTask?.Wait(900); } catch { }
        try { cadenceCts?.Dispose(); } catch { }
        _programCadenceTask = null;
        Interlocked.Exchange(ref _latestProgramCadenceSourceFrame, null);
        Interlocked.Exchange(ref _pendingProgramOutputFrame, null);
        // The latest-frame output worker can still be returning from a native NDI/DeckLink send.
        // Give that in-flight call a short bounded drain before disposing native output objects.
        if (Volatile.Read(ref _programOutputWorkerActive) != 0)
            SpinWait.SpinUntil(() => Volatile.Read(ref _programOutputWorkerActive) == 0, 750);
        try { _multiNdi.Dispose(); } catch { }
        try { _programConfidenceOutput.Dispose(); } catch { }
        try { _virtualOutput.Dispose(); } catch { }
        try { _ndi.Dispose(); } catch { }
        try { _deckLink.Dispose(); } catch { }
        try { _professional.Dispose(); } catch { }
        try { _dvbSubtitles.Dispose(); } catch { }
        try { _localAudio.Dispose(); } catch { }
        try { _previewAudio.Dispose(); } catch { }
        try { _previewCgCompositor.Dispose(); } catch { }
        try { _logoCgCompositor.Dispose(); } catch { }
        try { _cgCompositor.Dispose(); } catch { }
        try { _programChannelScaler.Dispose(); } catch { }
        try { _previewPauseGate.Dispose(); } catch { }
        try
        {
            ScteReceiverService.Instance.SpliceReceived -= OnScteSpliceReceived;
            FrameComparisonService.Instance.CueFrameDetected -= OnCueFrameDetected;
            FrameComparisonService.Instance.EmergencyReturnTriggered -= OnEmergencyReturnTriggered;
        }
        catch { }
    }
}
