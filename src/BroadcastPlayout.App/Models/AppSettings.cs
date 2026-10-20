namespace BroadcastPlayout.Models;

public sealed class AppSettings
{
    public string ChannelName { get; set; } = "KASHTRIX NEWS HD";
    public string ChannelId { get; set; } = "";
    public bool FirstRunCompleted { get; set; }
    public string PlayoutPreset { get; set; } = "1080p50";
    public bool EnableFiller { get; set; } = true;
    public string FillerFolder { get; set; } = "";
    public string ControllerHost { get; set; } = "127.0.0.1";
    public int ControllerPort { get; set; } = 9120;
    public bool ConnectToController { get; set; } = true;
    public string NdiSourceName { get; set; } = "KASHTRIX-PLAYOUT-01";
    public bool EnableNdiOutput { get; set; }
    public bool EnableDeckLinkOutput { get; set; }
    public bool EnableLocalAudio { get; set; } = true;
    public int ProgramAudioDeviceNumber { get; set; } = -1;
    public int PreviewAudioDeviceNumber { get; set; } = -1;
    public int FullscreenMonitor { get; set; } = 1;
    public bool KeepEngineRunningInTray { get; set; } = true;
    public bool RestoreLastPlaylist { get; set; } = true;
    public OutputProfile NdiProfile { get; set; } = new() { Preset = "1920x1080" };
    public int DeckLinkDeviceIndex { get; set; } = 0;
    public string DeckLinkDeviceName { get; set; } = "";
    public OutputProfile DeckLinkProfile { get; set; } = new() { Preset = "1920x1080" };
    public OutputProfile DisplayProfile { get; set; } = new() { Preset = "Source", Scaling = "Fit" };

    // FIX46: dedicated transparent CG output. This is intentionally separate from the playout
    // NDI/DeckLink outputs so graphics can feed an external switcher as NDI alpha or SDI fill/key.
    public bool EnableCgNdiAlphaOutput { get; set; }
    public string CgNdiAlphaSourceName { get; set; } = "KASHTRIX-CG-ALPHA";
    public bool EnableCgDeckLinkKeyFill { get; set; }
    public int CgDeckLinkDeviceIndex { get; set; }
    public string CgDeckLinkDeviceName { get; set; } = "";
    public string CgDeckLinkKeyerMode { get; set; } = "External";
    public int CgDeckLinkKeyerLevel { get; set; } = 255;
    public OutputProfile CgOutputProfile { get; set; } = new() { Preset = "1920x1080", FramesPerSecond = 50 };

    // CG Editor design guides. These values affect only the editor overlay; they are never
    // composited into Preview/Program/NDI/DeckLink output.
    public bool CgEditorShowSafeAreas { get; set; } = true;
    public bool CgEditorShowCenterGuide { get; set; }
    public double CgEditorActionSafePercent { get; set; } = 5.0;
    public double CgEditorTitleSafePercent { get; set; } = 10.0;

    // Master audio affects local, SDI, NDI, multiview and virtual output together.
    public bool EnableMasterOutputVolume { get; set; } = true;
    public int MasterOutputVolumePercent { get; set; } = 100;
    public bool MasterOutputMuted { get; set; }
    public string MasterMuteShortcut { get; set; } = "Ctrl+M";
    public string MasterVolumeUpShortcut { get; set; } = "Ctrl+Up";
    public string MasterVolumeDownShortcut { get; set; } = "Ctrl+Down";

    // Final-program video processor. These adjustments are applied after CG/logo compositing and
    // before every program output (local monitor, SDI, NDI, multiview and virtual output).
    public bool EnableVideoProcessing { get; set; }
    public int VideoBrightness { get; set; } = 0;          // -100..100
    public int VideoContrast { get; set; } = 100;          // 0..200
    public int VideoSaturation { get; set; } = 100;        // 0..200
    public double VideoGamma { get; set; } = 1.0;          // 0.10..4.00
    public int VideoHueDegrees { get; set; } = 0;          // -180..180
    public double VideoExposureStops { get; set; } = 0.0;  // -4..4
    public int VideoTemperature { get; set; } = 0;         // -100..100
    public int VideoTint { get; set; } = 0;                // -100..100
    public int VideoSharpness { get; set; } = 0;           // 0..100
    public int VideoBlackLevel { get; set; } = 0;          // -100..100
    public int VideoWhiteLevel { get; set; } = 0;          // -100..100
    public int VideoRedGainPercent { get; set; } = 100;    // 0..200
    public int VideoGreenGainPercent { get; set; } = 100;  // 0..200
    public int VideoBlueGainPercent { get; set; } = 100;   // 0..200

    // QC / import preset. File Manager and Playlist Editor use the same policy.
    public bool EnableQcOnImport { get; set; } = true;
    public MediaQcPreset QcPreset { get; set; } = new();
    public List<string> MediaFolders { get; set; } = [];

    // Enterprise MAM / ingest workflow storage. Keep the SQLite catalog on the MAM/API host;
    // remote workstations should use the REST API rather than sharing the database file over SMB.
    public string MamDatabasePath { get; set; } = "";
    public string MamArchiveRoot { get; set; } = "";
    public string MamProxyRoot { get; set; } = "";
    public bool MamAutoVerifyIngest { get; set; } = true;
    public bool MamAutoQcIngest { get; set; } = true;
    public string IngestReplicationFolder { get; set; } = "";
    public double IngestMinimumFreeSpaceGb { get; set; } = 10;

    // Shared-memory output consumed by the optional DirectShow .ax filter.
    public bool EnableVirtualOutput { get; set; } = true;
    public string VirtualOutputName { get; set; } = "Kashtrix Playout Virtual Output";
    public int VirtualOutputWidth { get; set; } = 1920;
    public int VirtualOutputHeight { get; set; } = 1080;
    public double VirtualOutputFrameRate { get; set; } = 50;
    public string VirtualOutputPixelFormat { get; set; } = "BGRA32";

    public bool MarkPlayedItemsCompleted { get; set; } = true;
    public bool AutoRemovePlayedItems { get; set; }
    public bool HidePlayedItems { get; set; }
    public bool DimCompletedItems { get; set; } = true;
    public int PlaylistRowHeight { get; set; } = 30;
    public double PlaylistFontSize { get; set; } = 10.5;
    public string PlaylistDefaultRowColor { get; set; } = string.Empty;
    public string PlaylistDefaultTextColor { get; set; } = "#D9DEE3";
    public string AudioDownmixPreset { get; set; } = "Auto Stereo";
    public bool ShowPlaylistThumbnails { get; set; } = true;
    public int PlaylistThumbnailWidth { get; set; } = 42;
    public int PlaylistThumbnailHeight { get; set; } = 24;
    public string DefaultDecoderPreference { get; set; } = "Auto (CPU+GPU)";

    // XMLTV/EPG and automatic title/template policy shared by Playout/Scheduler/CG Controller.
    public EpgOutputSettings Epg { get; set; } = new();

    public string DefaultLogoTemplate { get; set; } = "Logo · Station Ident";
    public string DefaultGraphicsTemplate { get; set; } = "Prime HD · Breaking News Live";
    public string DefaultBackInTemplate { get; set; } = "Commercial · Back In Countdown Lower Third";
    public bool EnableAutoBackIn { get; set; } = true;
    public double BackInTriggerLeadSeconds { get; set; } = 90.0;

    public bool EnableAutoCg { get; set; } = true;
    public double ProgramNowPlayingDelaySeconds { get; set; } = 3;
    public double ProgramComingUpBeforeEndSeconds { get; set; } = 12;
    public double MusicNowPlayingDelaySeconds { get; set; } = 3;
    public double MusicComingUpBeforeEndSeconds { get; set; } = 12;
    public double NewsNowPlayingDelaySeconds { get; set; } = 2;
    public double NewsComingUpBeforeEndSeconds { get; set; } = 8;
    public double AutoCgHoldSeconds { get; set; } = 10;

    public List<CategorySetting> Categories { get; set; } = GetDefaultCategories();

    public static List<CategorySetting> GetDefaultCategories() =>
    [
        new() { Name = "Program", Color = "#1E2638", TextColor = "#E6ECF5", EnableAutoCg = true, DefaultTemplate = "Program · Now Playing Lower Third", ComingUpTemplate = "Program · Coming Up Lower Third", NowPlayingDelaySeconds = 3, ComingUpBeforeEndSeconds = 12, AllowGraphics = true },
        new() { Name = "Music", Color = "#182F46", TextColor = "#D5E8FF", EnableAutoCg = true, DefaultTemplate = "Music · Now Playing Lower Third", ComingUpTemplate = "Music · Coming Up Lower Third", NowPlayingDelaySeconds = 3, ComingUpBeforeEndSeconds = 12, AllowGraphics = false, AutoNowNextOnly = true },
        new() { Name = "News", Color = "#251A38", TextColor = "#EBD9FF", EnableAutoCg = true, DefaultTemplate = "News · Now Playing Lower Third", ComingUpTemplate = "News · Coming Up Lower Third", NowPlayingDelaySeconds = 2, ComingUpBeforeEndSeconds = 8, AllowGraphics = true },
        new() { Name = "Commercial", Color = "#3A3019", TextColor = "#FFF0C2", EnableAutoCg = true, DefaultTemplate = "Commercial · Back In Countdown Lower Third", ShowInEpg = false, AllowGraphics = false, AllowLogoOnly = true },
        new() { Name = "Live", Color = "#3B1B20", TextColor = "#FFD5D5", EnableAutoCg = false, AllowGraphics = true },
        new() { Name = "Graphics", Color = "#2D1836", TextColor = "#F5D6FF", EnableAutoCg = false, AllowGraphics = true },
        new() { Name = "Promo", Color = "#2A2D18", TextColor = "#F5FAD0", EnableAutoCg = false, AllowGraphics = true },
        new() { Name = "Filler", Color = "#141A1E", TextColor = "#A0B0BA", EnableAutoCg = false, ShowInEpg = false, AllowGraphics = true }
    ];

    // FIX42 professional broadcast I/O, standards and high-availability configuration.
    public ProfessionalBroadcastSettings ProfessionalBroadcast { get; set; } = new();
}

public sealed class CategorySetting
{
    public string Name { get; set; } = "Program";
    public string Color { get; set; } = "#1E2638";
    public string TextColor { get; set; } = "#E6ECF5";
    public bool EnableAutoCg { get; set; } = true;
    public string DefaultTemplate { get; set; } = "";
    public string ComingUpTemplate { get; set; } = "";
    public double NowPlayingDelaySeconds { get; set; } = 3.0;
    public double ComingUpBeforeEndSeconds { get; set; } = 12.0;
    public bool ShowInEpg { get; set; } = true;
    public bool AllowGraphics { get; set; } = true;
    public bool AllowLogoOnly { get; set; } = false;
    public bool AutoNowNextOnly { get; set; } = false;
}
