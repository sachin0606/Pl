using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BroadcastPlayout.Models;

public sealed class PlaylistItem : INotifyPropertyChanged
{
    private int _sequence;
    private string _startTimeText = "--:--:--";
    private string _endTimeText = "--:--:--";
    private string _category = "Media";
    private string _notes = string.Empty;
    private string _blockName = string.Empty;
    private string _location = string.Empty;
    private string _displayDurationText = string.Empty;
    private string _rowColor = string.Empty;
    private string _rowTextColor = "#D9DEE3";
    private string _eventType = "CLIP";
    private string _title = "Untitled";
    private string _filePath = string.Empty;
    private TimeSpan _inPoint = TimeSpan.Zero;
    private TimeSpan _outPoint = TimeSpan.Zero;
    private string _codec = string.Empty;
    private string _videoFormat = string.Empty;
    private string _status = "Ready";
    private string _sourceKind = "File";
    private string _inputFormat = string.Empty;
    private string _inputOptions = string.Empty;
    private string _videoDevice = string.Empty;
    private string _audioDevice = string.Empty;
    private string _alternateAudioUrl = string.Empty;
    private int _audioGainPercent = 100;
    private bool _audioMuted;
    private string _audioChannelMode = "Auto Stereo";
    private int _audioDelayMs;
    private int _audioTrackIndex = -1;
    private string _scaleMode = "Fit";
    private string _aspectRatioMode = "Auto";
    private string _customAspectRatio = "16:9";
    private string _interlaceMode = "Auto";
    private string _decoderPreference = "Auto (CPU+GPU)";
    private string _originalTitle = string.Empty;
    private string _originalDescription = string.Empty;
    private string _episodeTitle = string.Empty;
    private string _episodeDescription = string.Empty;
    private int _episodeSeason;
    private int _episodeNumber;
    private string _genre = string.Empty;
    private int _productionYear;
    private string _country = string.Empty;
    private string _director = string.Empty;
    private string _leadActors = string.Empty;
    private string _imdbUrl = string.Empty;
    private string _imageUrl = string.Empty;
    private string _parentalAdvisory = string.Empty;
    private bool _premiere;
    private TimeSpan _sourceDuration = TimeSpan.Zero;
    private double _sourceFrameRate = 25.0;
    private bool _isLiveSource;
    private bool _isLoop;
    private bool _isBlockLoop;
    private int _loopCount;
    private int _currentLoopIteration;
    private int _captureWidth = 1920;
    private int _captureHeight = 1080;
    private string _thumbnailPath = string.Empty;
    private string _qcStatus = "Not checked";
    private string _qcSummary = string.Empty;
    private Guid _cgProjectId;
    private string _dtmfSequence = "1234#";
    private int _dtmfToneMilliseconds = 120;
    private int _dtmfGapMilliseconds = 55;
    private int _dtmfLevelPercent = 72;
    private int _dialToneMilliseconds = 2500;
    private int _dialToneLevelPercent = 55;
    private string _tcpHost = "127.0.0.1";
    private int _tcpPort = 9101;
    private string _tcpCommand = "PLAY";
    private string _mosAction = "UPDATE";
    private string _mosItemId = string.Empty;
    private string _mosTitle = string.Empty;
    private string _mosDataJson = "{}";
    private int _mosLayer = 20;
    private int _scteDurationSeconds = 30;
    private string _scteSpliceType = "Start Normal";
    private uint _scteEventId;
    private bool _scteAutoReturn = true;
    private int _scteUniqueProgramId = 1;
    private int _scteAvailNum;
    private int _scteAvailsExpected;
    private int _sctePreRollMilliseconds = 4000;
    private long _sctePts90k;
    private string _captionText = "Kashtrix caption test";
    private string _caption608Channel = "CC1";
    private string _caption608Mode = "Pop-On";
    private int _caption608Row = 15;
    private bool _caption608Underline;
    private bool _caption608Italics;
    private int _caption708ServiceNumber = 1;
    private int _caption708Window;
    private int _caption708Row = 14;
    private int _caption708Column;
    private string _caption708Justification = "Left";
    private string _dvbLanguage = "eng";
    private int _dvbSubtitlingType = 0x10;
    private int _dvbCompositionPageId = 1;
    private int _dvbAncillaryPageId = 1;
    private int _dvbTimeoutSeconds = 4;
    private int _gpoOutput;
    private int _gpoPulseMilliseconds = 250;
    private string _gpoAction = "Pulse";
    private bool _gpoActiveHigh = true;
    private string _rs422Mode = "Raw Hex";
    private string _rs422Hex = string.Empty;
    private string _rs422Ascii = string.Empty;
    private string _vdcpCommand = "PLAY";
    private int _vdcpPort;
    private string _vdcpClipId = string.Empty;
    private string _vdcpStartTimecode = "00:00:00:00";
    private string _vdcpDurationTimecode = "00:00:00:00";
    private string _vdcpHex = string.Empty;
    private string _snmpHost = "127.0.0.1";
    private int _snmpPort = 162;
    private string _snmpCommunity = "public";
    private string _snmpTrapOid = "1.3.6.1.4.1.55555.1.10.0";
    private string _snmpVarbindOid = "1.3.6.1.4.1.55555.1.11.0";
    private string _snmpValueType = "String";
    private string _snmpValue = "Kashtrix playout event";
    private ObservableCollection<MediaPart> _parts = [];

    public PlaylistItem() => AttachParts(_parts);

    public Guid Id { get; set; } = Guid.NewGuid();
    // Operator-facing rundown metadata. These fields are optional for imported media,
    // but are populated by demo/scheduler workflows so the playout grid matches MCR layouts.
    public string StartTimeText { get => _startTimeText; set => Set(ref _startTimeText, string.IsNullOrWhiteSpace(value) ? "--:--:--" : value); }
    public string EndTimeText { get => _endTimeText; set => Set(ref _endTimeText, string.IsNullOrWhiteSpace(value) ? "--:--:--" : value); }
    public string Category { get => _category; set { if (Set(ref _category, string.IsNullOrWhiteSpace(value) ? "Media" : value.Trim())) { Raise(nameof(CategoryColor)); Raise(nameof(IsMusic)); Raise(nameof(IsNews)); } } }
    public string Notes { get => _notes; set => Set(ref _notes, value ?? string.Empty); }
    /// <summary>Optional block marker. A non-empty value starts a new operator rundown block at this item.</summary>
    public string BlockName { get => _blockName; set => Set(ref _blockName, value?.Trim() ?? string.Empty); }
    public string Location { get => _location; set => Set(ref _location, value ?? string.Empty); }
    public bool IsLoop { get => _isLoop; set => Set(ref _isLoop, value); }
    public bool IsBlockLoop { get => _isBlockLoop; set => Set(ref _isBlockLoop, value); }
    public int LoopCount { get => _loopCount; set => Set(ref _loopCount, Math.Max(0, value)); }
    public int CurrentLoopIteration { get => _currentLoopIteration; set => Set(ref _currentLoopIteration, Math.Max(0, value)); }
    public string DisplayDurationText { get => _displayDurationText; set { if (Set(ref _displayDurationText, value ?? string.Empty)) Raise(nameof(RundownDurationText)); } }
    public string RowColor { get => _rowColor; set { if (Set(ref _rowColor, value ?? string.Empty)) Raise(nameof(CategoryColor)); } }
    public string RowTextColor { get => _rowTextColor; set => Set(ref _rowTextColor, string.IsNullOrWhiteSpace(value) ? "#D9DEE3" : value.Trim()); }
    public string RundownDurationText => DurationText;
    public bool IsMusic => Category.Contains("Music", StringComparison.OrdinalIgnoreCase);
    public bool IsNews => Category.Contains("News", StringComparison.OrdinalIgnoreCase);
    public bool IsDtmfEvent => EventType.Equals("DTMF", StringComparison.OrdinalIgnoreCase);
    public bool IsDialToneEvent => EventType.Equals("DIALTONE", StringComparison.OrdinalIgnoreCase);
    public bool IsTcpEvent => EventType.Equals("TCP", StringComparison.OrdinalIgnoreCase);
    public bool IsMosEvent => EventType.Equals("MOS", StringComparison.OrdinalIgnoreCase);
    public bool IsProfessionalControlEvent => EventType is "SCTE104" or "CAPTION608" or "CAPTION708" or "DVBSUB" or "GPO" or "RS422" or "VDCP" or "SNMP";
    public bool IsEditorialItem => !IsControlEvent && !IsCgEvent;
    public bool IsControlEvent => EventType is "STOP" or "PLAY" or "PAUSE" or "RESUME" or "NEXT" or "NOTE"
        or "LOOP" or "SCTE35" or "CUETONE" or "DIALTONE" or "DTMF" or "CGSHOW" or "CGHIDE"
        or "LOGOON" or "LOGOOFF" or "TCP" or "MOS" or "SCTE104" or "CAPTION608" or "CAPTION708" or "DVBSUB"
        or "GPO" or "RS422" or "VDCP" or "SNMP";
    private static readonly Dictionary<string, string> _categoryColorCache = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _categoryColorLoadedUtc = DateTime.MinValue;

    public static string ResolveCategoryColor(string category)
    {
        if (string.IsNullOrWhiteSpace(category)) return "#0F1418";
        if ((DateTime.UtcNow - _categoryColorLoadedUtc).TotalSeconds > 2)
        {
            try
            {
                var settings = BroadcastPlayout.Services.SettingsStore.Load();
                _categoryColorCache.Clear();
                foreach (var cat in settings.Categories)
                {
                    if (!string.IsNullOrWhiteSpace(cat.Name) && !string.IsNullOrWhiteSpace(cat.Color))
                        _categoryColorCache[cat.Name.Trim()] = cat.Color.Trim();
                }
                _categoryColorLoadedUtc = DateTime.UtcNow;
            }
            catch { }
        }
        if (_categoryColorCache.TryGetValue(category.Trim(), out var color)) return color;
        return category.Contains("Commercial", StringComparison.OrdinalIgnoreCase) ? "#3A3019" :
               category.Contains("Music", StringComparison.OrdinalIgnoreCase) ? "#182F46" :
               category.Contains("News", StringComparison.OrdinalIgnoreCase) ? "#251A38" :
               category.Contains("Live", StringComparison.OrdinalIgnoreCase) ? "#3B1B20" :
               category.Contains("Graphics", StringComparison.OrdinalIgnoreCase) ? "#2D1836" :
               category.Contains("Promo", StringComparison.OrdinalIgnoreCase) ? "#2A2D18" :
               category.Contains("Filler", StringComparison.OrdinalIgnoreCase) ? "#141A1E" : "#0F1418";
    }

    public string CategoryColor => !string.IsNullOrWhiteSpace(RowColor) ? RowColor :
        IsCgEvent ? "#382052" : IsLiveSource ? "#3B1B20" :
        EventType.Equals("STOP", StringComparison.OrdinalIgnoreCase) ? "#4A1717" :
        EventType.Equals("PLAY", StringComparison.OrdinalIgnoreCase) || EventType.Equals("RESUME", StringComparison.OrdinalIgnoreCase) ? "#173823" :
        EventType.Equals("PAUSE", StringComparison.OrdinalIgnoreCase) ? "#403117" :
        EventType is "SCTE35" or "SCTE104" or "CAPTION608" or "CAPTION708" or "DVBSUB" or "CUETONE" or "DIALTONE" or "DTMF" ? "#3D2A0B" :
        EventType is "CGSHOW" or "CGHIDE" or "LOGOON" or "LOGOOFF" ? "#382052" :
        EventType is "TCP" or "MOS" or "GPO" or "RS422" or "VDCP" or "SNMP" ? "#123040" :
        ResolveCategoryColor(Category);
    public string ParsedTitle => BroadcastPlayout.Services.MediaNameParser.Parse(FilePath, Title).Title;
    public string ParsedArtist => BroadcastPlayout.Services.MediaNameParser.Parse(FilePath, Title).Artist;
    public string ParsedExtra => BroadcastPlayout.Services.MediaNameParser.Parse(FilePath, Title).Extra;
    public int Sequence { get => _sequence; set => Set(ref _sequence, value); }
    public string EventType { get => _eventType; set { if (Set(ref _eventType, string.IsNullOrWhiteSpace(value) ? "CLIP" : value.Trim().ToUpperInvariant())) { Raise(nameof(IsCgEvent)); Raise(nameof(IsControlEvent)); Raise(nameof(IsEditorialItem)); Raise(nameof(IsDtmfEvent)); Raise(nameof(IsDialToneEvent)); Raise(nameof(IsTcpEvent)); Raise(nameof(IsMosEvent)); Raise(nameof(IsProfessionalControlEvent)); Raise(nameof(CategoryColor)); } } }
    public string Title { get => _title; set => Set(ref _title, string.IsNullOrWhiteSpace(value) ? "Untitled" : value.Trim()); }
    public string FilePath { get => _filePath; set { if (Set(ref _filePath, value ?? string.Empty)) { Raise(nameof(SourceDescription)); Raise(nameof(MediaDetails)); Raise(nameof(ParsedTitle)); Raise(nameof(ParsedArtist)); Raise(nameof(ParsedExtra)); } } }
    public string SourceKind { get => _sourceKind; set { if (Set(ref _sourceKind, string.IsNullOrWhiteSpace(value) ? "File" : value.Trim())) { Raise(nameof(SourceDescription)); Raise(nameof(MediaDetails)); Raise(nameof(IsCgEvent)); } } }
    public string InputFormat { get => _inputFormat; set => Set(ref _inputFormat, value ?? string.Empty); }
    public string InputOptions { get => _inputOptions; set => Set(ref _inputOptions, value ?? string.Empty); }
    public string VideoDevice { get => _videoDevice; set { if (Set(ref _videoDevice, value ?? string.Empty)) Raise(nameof(SourceDescription)); } }
    public string AudioDevice { get => _audioDevice; set => Set(ref _audioDevice, value ?? string.Empty); }
    /// <summary>Optional separate audio stream URL returned by web resolvers such as yt-dlp.</summary>
    public string AlternateAudioUrl { get => _alternateAudioUrl; set => Set(ref _alternateAudioUrl, value ?? string.Empty); }
    public int AudioGainPercent { get => _audioGainPercent; set { if (Set(ref _audioGainPercent, Math.Clamp(value, 0, 200))) Raise(nameof(MediaPropertiesSummary)); } }
    public bool AudioMuted { get => _audioMuted; set { if (Set(ref _audioMuted, value)) Raise(nameof(MediaPropertiesSummary)); } }
    /// <summary>Per-item channel routing. Auto Stereo preserves stereo and folds multichannel safely; mono sources are duplicated L/R.</summary>
    public string AudioChannelMode { get => _audioChannelMode; set { if (Set(ref _audioChannelMode, string.IsNullOrWhiteSpace(value) ? "Auto Stereo" : value.Trim())) Raise(nameof(MediaPropertiesSummary)); } }
    /// <summary>Positive values delay audio; negative values advance it. Range is deliberately bounded for operator safety.</summary>
    public int AudioDelayMs { get => _audioDelayMs; set { if (Set(ref _audioDelayMs, Math.Clamp(value, -5000, 5000))) Raise(nameof(MediaPropertiesSummary)); } }
    /// <summary>Zero-based audio stream ordinal. -1 asks the demuxer for its best/default stream.</summary>
    public int AudioTrackIndex { get => _audioTrackIndex; set { if (Set(ref _audioTrackIndex, Math.Clamp(value, -1, 31))) Raise(nameof(MediaPropertiesSummary)); } }
    public string ScaleMode { get => _scaleMode; set { if (Set(ref _scaleMode, string.IsNullOrWhiteSpace(value) ? "Fit" : value.Trim())) Raise(nameof(MediaPropertiesSummary)); } }
    public string AspectRatioMode { get => _aspectRatioMode; set { if (Set(ref _aspectRatioMode, string.IsNullOrWhiteSpace(value) ? "Auto" : value.Trim())) Raise(nameof(MediaPropertiesSummary)); } }
    public string CustomAspectRatio { get => _customAspectRatio; set { if (Set(ref _customAspectRatio, string.IsNullOrWhiteSpace(value) ? "16:9" : value.Trim())) Raise(nameof(MediaPropertiesSummary)); } }
    public string InterlaceMode { get => _interlaceMode; set { if (Set(ref _interlaceMode, string.IsNullOrWhiteSpace(value) ? "Auto" : value.Trim())) Raise(nameof(MediaPropertiesSummary)); } }
    /// <summary>Decoder policy. GPU/Hybrid are preferences; unsupported hardware paths fall back to the stable software decoder.</summary>
    public string DecoderPreference { get => _decoderPreference; set { if (Set(ref _decoderPreference, string.IsNullOrWhiteSpace(value) ? "Auto (CPU+GPU)" : value.Trim())) Raise(nameof(MediaPropertiesSummary)); } }

    // Typed signaling / automation payloads. Notes remain serialized for backward compatibility,
    // but live execution and the properties dialog use these strongly-typed fields.
    public string DtmfSequence { get => _dtmfSequence; set => Set(ref _dtmfSequence, SanitizeDtmf(value)); }
    public int DtmfToneMilliseconds { get => _dtmfToneMilliseconds; set => Set(ref _dtmfToneMilliseconds, Math.Clamp(value, 45, 2000)); }
    public int DtmfGapMilliseconds { get => _dtmfGapMilliseconds; set => Set(ref _dtmfGapMilliseconds, Math.Clamp(value, 20, 2000)); }
    public int DtmfLevelPercent { get => _dtmfLevelPercent; set => Set(ref _dtmfLevelPercent, Math.Clamp(value, 1, 100)); }
    public int DialToneMilliseconds { get => _dialToneMilliseconds; set => Set(ref _dialToneMilliseconds, Math.Clamp(value, 100, 30000)); }
    public int DialToneLevelPercent { get => _dialToneLevelPercent; set => Set(ref _dialToneLevelPercent, Math.Clamp(value, 1, 100)); }
    public string TcpHost { get => _tcpHost; set => Set(ref _tcpHost, string.IsNullOrWhiteSpace(value) ? "127.0.0.1" : value.Trim()); }
    public int TcpPort { get => _tcpPort; set => Set(ref _tcpPort, Math.Clamp(value, 1, 65535)); }
    public string TcpCommand { get => _tcpCommand; set => Set(ref _tcpCommand, string.IsNullOrWhiteSpace(value) ? "PLAY" : value.Trim()); }
    public string MosAction { get => _mosAction; set => Set(ref _mosAction, string.IsNullOrWhiteSpace(value) ? "UPDATE" : value.Trim().ToUpperInvariant()); }
    public string MosItemId { get => _mosItemId; set => Set(ref _mosItemId, value?.Trim() ?? string.Empty); }
    public string MosTitle { get => _mosTitle; set => Set(ref _mosTitle, value?.Trim() ?? string.Empty); }
    public string MosDataJson { get => _mosDataJson; set => Set(ref _mosDataJson, string.IsNullOrWhiteSpace(value) ? "{}" : value.Trim()); }
    public int MosLayer { get => _mosLayer; set => Set(ref _mosLayer, Math.Clamp(value, 0, 999)); }
    public int ScteDurationSeconds { get => _scteDurationSeconds; set => Set(ref _scteDurationSeconds, Math.Clamp(value, 0, 86400)); }
    public string ScteSpliceType { get => _scteSpliceType; set => Set(ref _scteSpliceType, NormalizeChoice(value, "Start Normal")); }
    /// <summary>Zero means allocate the next channel-local SCTE event id at execution time.</summary>
    public uint ScteEventId { get => _scteEventId; set => Set(ref _scteEventId, value); }
    public bool ScteAutoReturn { get => _scteAutoReturn; set => Set(ref _scteAutoReturn, value); }
    public int ScteUniqueProgramId { get => _scteUniqueProgramId; set => Set(ref _scteUniqueProgramId, Math.Clamp(value, 0, 65535)); }
    public int ScteAvailNum { get => _scteAvailNum; set => Set(ref _scteAvailNum, Math.Clamp(value, 0, 255)); }
    public int ScteAvailsExpected { get => _scteAvailsExpected; set => Set(ref _scteAvailsExpected, Math.Clamp(value, 0, 255)); }
    public int SctePreRollMilliseconds { get => _sctePreRollMilliseconds; set => Set(ref _sctePreRollMilliseconds, Math.Clamp(value, 0, 65535)); }
    /// <summary>Optional absolute 90 kHz splice PTS. Zero asks the runtime to derive timing from current program PTS + pre-roll.</summary>
    public long SctePts90k { get => _sctePts90k; set => Set(ref _sctePts90k, Math.Max(0, value)); }
    public string CaptionText { get => _captionText; set => Set(ref _captionText, value ?? string.Empty); }
    public string Caption608Channel { get => _caption608Channel; set => Set(ref _caption608Channel, NormalizeChoice(value, "CC1")); }
    public string Caption608Mode { get => _caption608Mode; set => Set(ref _caption608Mode, NormalizeChoice(value, "Pop-On")); }
    public int Caption608Row { get => _caption608Row; set => Set(ref _caption608Row, Math.Clamp(value, 1, 15)); }
    public bool Caption608Underline { get => _caption608Underline; set => Set(ref _caption608Underline, value); }
    public bool Caption608Italics { get => _caption608Italics; set => Set(ref _caption608Italics, value); }
    public int Caption708ServiceNumber { get => _caption708ServiceNumber; set => Set(ref _caption708ServiceNumber, Math.Clamp(value, 1, 63)); }
    public int Caption708Window { get => _caption708Window; set => Set(ref _caption708Window, Math.Clamp(value, 0, 7)); }
    public int Caption708Row { get => _caption708Row; set => Set(ref _caption708Row, Math.Clamp(value, 0, 14)); }
    public int Caption708Column { get => _caption708Column; set => Set(ref _caption708Column, Math.Clamp(value, 0, 31)); }
    public string Caption708Justification { get => _caption708Justification; set => Set(ref _caption708Justification, NormalizeChoice(value, "Left")); }
    public string DvbLanguage { get => _dvbLanguage; set => Set(ref _dvbLanguage, NormalizeDvbLanguage(value)); }
    public int DvbSubtitlingType { get => _dvbSubtitlingType; set => Set(ref _dvbSubtitlingType, Math.Clamp(value, 0, 255)); }
    public int DvbCompositionPageId { get => _dvbCompositionPageId; set => Set(ref _dvbCompositionPageId, Math.Clamp(value, 0, 65535)); }
    public int DvbAncillaryPageId { get => _dvbAncillaryPageId; set => Set(ref _dvbAncillaryPageId, Math.Clamp(value, 0, 65535)); }
    public int DvbTimeoutSeconds { get => _dvbTimeoutSeconds; set => Set(ref _dvbTimeoutSeconds, Math.Clamp(value, 1, 255)); }
    public int GpoOutput { get => _gpoOutput; set => Set(ref _gpoOutput, Math.Clamp(value, 0, 31)); }
    public int GpoPulseMilliseconds { get => _gpoPulseMilliseconds; set => Set(ref _gpoPulseMilliseconds, Math.Clamp(value, 10, 60000)); }
    public string GpoAction { get => _gpoAction; set => Set(ref _gpoAction, NormalizeChoice(value, "Pulse")); }
    public bool GpoActiveHigh { get => _gpoActiveHigh; set => Set(ref _gpoActiveHigh, value); }
    public string Rs422Mode { get => _rs422Mode; set => Set(ref _rs422Mode, NormalizeChoice(value, "Raw Hex")); }
    public string Rs422Hex { get => _rs422Hex; set => Set(ref _rs422Hex, value?.Trim() ?? string.Empty); }
    public string Rs422Ascii { get => _rs422Ascii; set => Set(ref _rs422Ascii, value ?? string.Empty); }
    public string VdcpCommand { get => _vdcpCommand; set => Set(ref _vdcpCommand, NormalizeChoice(value, "PLAY").ToUpperInvariant()); }
    public int VdcpPort { get => _vdcpPort; set => Set(ref _vdcpPort, Math.Clamp(value, 0, 15)); }
    public string VdcpClipId { get => _vdcpClipId; set => Set(ref _vdcpClipId, (value ?? string.Empty).Trim().PadRight(8).Substring(0, 8).TrimEnd()); }
    public string VdcpStartTimecode { get => _vdcpStartTimecode; set => Set(ref _vdcpStartTimecode, NormalizeTimecode(value)); }
    public string VdcpDurationTimecode { get => _vdcpDurationTimecode; set => Set(ref _vdcpDurationTimecode, NormalizeTimecode(value)); }
    public string VdcpHex { get => _vdcpHex; set => Set(ref _vdcpHex, value?.Trim() ?? string.Empty); }
    public string SnmpHost { get => _snmpHost; set => Set(ref _snmpHost, string.IsNullOrWhiteSpace(value) ? "127.0.0.1" : value.Trim()); }
    public int SnmpPort { get => _snmpPort; set => Set(ref _snmpPort, Math.Clamp(value, 1, 65535)); }
    public string SnmpCommunity { get => _snmpCommunity; set => Set(ref _snmpCommunity, string.IsNullOrWhiteSpace(value) ? "public" : value.Trim()); }
    public string SnmpTrapOid { get => _snmpTrapOid; set => Set(ref _snmpTrapOid, NormalizeOid(value, "1.3.6.1.4.1.55555.1.10.0")); }
    public string SnmpVarbindOid { get => _snmpVarbindOid; set => Set(ref _snmpVarbindOid, NormalizeOid(value, "1.3.6.1.4.1.55555.1.11.0")); }
    public string SnmpValueType { get => _snmpValueType; set => Set(ref _snmpValueType, NormalizeChoice(value, "String")); }
    public string SnmpValue { get => _snmpValue; set => Set(ref _snmpValue, value ?? string.Empty); }

    public void RefreshLegacyControlNotes()
    {
        Notes = EventType switch
        {
            "DTMF" => $"DTMF: {DtmfSequence};tone={DtmfToneMilliseconds};gap={DtmfGapMilliseconds};level={DtmfLevelPercent}",
            "DIALTONE" => $"DIALTONE: duration={DialToneMilliseconds};level={DialToneLevelPercent}",
            "TCP" => $"{TcpHost}:{TcpPort} {TcpCommand}",
            "MOS" => $"MOS: action={MosAction};id={MosItemId};title={MosTitle};layer={MosLayer}",
            "SCTE35" => $"SCTE-35: {ScteSpliceType};event={(ScteEventId == 0 ? "auto" : ScteEventId)};preRoll={SctePreRollMilliseconds}ms;duration={ScteDurationSeconds}s;program={ScteUniqueProgramId};avail={ScteAvailNum}/{ScteAvailsExpected};autoReturn={ScteAutoReturn}",
            "SCTE104" => $"SCTE-104: {ScteSpliceType};event={(ScteEventId == 0 ? "auto" : ScteEventId)};preRoll={SctePreRollMilliseconds}ms;duration={ScteDurationSeconds}s;program={ScteUniqueProgramId};avail={ScteAvailNum}/{ScteAvailsExpected};autoReturn={ScteAutoReturn}",
            "CAPTION608" => $"CEA-608 {Caption608Channel} {Caption608Mode} row={Caption608Row}: {CaptionText}",
            "CAPTION708" => $"CEA-708 service={Caption708ServiceNumber} window={Caption708Window} row={Caption708Row} col={Caption708Column}: {CaptionText}",
            "DVBSUB" => $"DVB SUB {DvbLanguage} type=0x{DvbSubtitlingType:X2} page={DvbCompositionPageId}/{DvbAncillaryPageId}: {CaptionText}",
            "GPO" => $"GPO: output={GpoOutput};action={GpoAction};pulse={GpoPulseMilliseconds}ms;activeHigh={GpoActiveHigh}",
            "RS422" => $"RS422 {Rs422Mode}: {(Rs422Mode.Equals("ASCII", StringComparison.OrdinalIgnoreCase) ? Rs422Ascii : Rs422Hex)}",
            "VDCP" => $"VDCP: port={VdcpPort};command={VdcpCommand};clip={VdcpClipId};raw={VdcpHex}",
            "SNMP" => $"SNMPv2c: {SnmpHost}:{SnmpPort};community={SnmpCommunity};trap={SnmpTrapOid};var={SnmpVarbindOid};type={SnmpValueType};value={SnmpValue}",
            _ => Notes
        };
    }

    private static string SanitizeDtmf(string? value)
    {
        var chars = (value ?? string.Empty).ToUpperInvariant().Where(ch => "0123456789*#ABCD".Contains(ch)).ToArray();
        return new string(chars);
    }

    private static string NormalizeChoice(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    private static string NormalizeDvbLanguage(string? value)
    {
        var letters = new string((value ?? string.Empty).Where(char.IsLetter).Take(3).Select(char.ToLowerInvariant).ToArray());
        return letters.Length == 3 ? letters : "eng";
    }
    private static string NormalizeTimecode(string? value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "00:00:00:00" : value.Trim();
        var p = text.Split(':');
        if (p.Length != 4 || p.Any(x => !int.TryParse(x, out _))) return "00:00:00:00";
        return string.Join(":", p.Select(x => Math.Clamp(int.Parse(x), 0, 99).ToString("00")));
    }
    private static string NormalizeOid(string? value, string fallback)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length > 2 && text.Split('.').All(x => int.TryParse(x, out var n) && n >= 0) ? text : fallback;
    }

    // Optional programme metadata used by custom EPG/XMLTV exports.
    public string OriginalTitle { get => _originalTitle; set => Set(ref _originalTitle, value ?? string.Empty); }
    public string OriginalDescription { get => _originalDescription; set => Set(ref _originalDescription, value ?? string.Empty); }
    public string EpisodeTitle { get => _episodeTitle; set => Set(ref _episodeTitle, value ?? string.Empty); }
    public string EpisodeDescription { get => _episodeDescription; set => Set(ref _episodeDescription, value ?? string.Empty); }
    public int EpisodeSeason { get => _episodeSeason; set => Set(ref _episodeSeason, Math.Max(0, value)); }
    public int EpisodeNumber { get => _episodeNumber; set => Set(ref _episodeNumber, Math.Max(0, value)); }
    public string Genre { get => _genre; set => Set(ref _genre, value ?? string.Empty); }
    public int ProductionYear { get => _productionYear; set => Set(ref _productionYear, Math.Clamp(value, 0, 9999)); }
    public string Country { get => _country; set => Set(ref _country, value ?? string.Empty); }
    public string Director { get => _director; set => Set(ref _director, value ?? string.Empty); }
    public string LeadActors { get => _leadActors; set => Set(ref _leadActors, value ?? string.Empty); }
    public string ImdbUrl { get => _imdbUrl; set => Set(ref _imdbUrl, value ?? string.Empty); }
    public string ImageUrl { get => _imageUrl; set => Set(ref _imageUrl, value ?? string.Empty); }
    public string ParentalAdvisory { get => _parentalAdvisory; set => Set(ref _parentalAdvisory, value ?? string.Empty); }
    public bool Premiere { get => _premiere; set => Set(ref _premiere, value); }

    public bool IsLiveSource { get => _isLiveSource; set { if (Set(ref _isLiveSource, value)) { Raise(nameof(CanTrim)); Raise(nameof(MediaDetails)); Raise(nameof(CategoryColor)); } } }
    public int CaptureWidth { get => _captureWidth; set => Set(ref _captureWidth, Math.Max(0, value)); }
    public int CaptureHeight { get => _captureHeight; set => Set(ref _captureHeight, Math.Max(0, value)); }
    public double SourceFrameRate { get => _sourceFrameRate; set { if (Set(ref _sourceFrameRate, value < 0 ? 0 : value)) Raise(nameof(MediaDetails)); } }
    public string ThumbnailPath { get => _thumbnailPath; set => Set(ref _thumbnailPath, value ?? string.Empty); }
    public string QcStatus { get => _qcStatus; set => Set(ref _qcStatus, value ?? "Not checked"); }
    public string QcSummary { get => _qcSummary; set => Set(ref _qcSummary, value ?? string.Empty); }
    public Guid CgProjectId { get => _cgProjectId; set { if (Set(ref _cgProjectId, value)) Raise(nameof(IsCgEvent)); } }
    public bool IsCgEvent => SourceKind.Equals("CG", StringComparison.OrdinalIgnoreCase) || EventType.Equals("CG", StringComparison.OrdinalIgnoreCase);

    public ObservableCollection<MediaPart> Parts
    {
        get => _parts;
        set
        {
            var next = value ?? [];
            if (ReferenceEquals(_parts, next)) return;
            DetachParts(_parts); _parts = next; AttachParts(_parts);
            Raise(nameof(Parts)); RaisePartsDerived();
        }
    }

    public bool HasParts => Parts.Count > 0;
    public int EnabledPartCount => Parts.Count(x => x.Enabled && x.OutPoint > x.InPoint);
    public string PartsSummary => HasParts ? $"{EnabledPartCount} PART{(EnabledPartCount == 1 ? "" : "S")}" : "SINGLE";

    public TimeSpan SourceDuration
    {
        get => _sourceDuration;
        set
        {
            if (!Set(ref _sourceDuration, value < TimeSpan.Zero ? TimeSpan.Zero : value)) return;
            Raise(nameof(SourceDurationText)); Raise(nameof(CanTrim)); Raise(nameof(MediaDetails));
        }
    }

    public string SourceDurationText => Timecode.Format(SourceDuration);
    public bool CanTrim => !IsLiveSource && !IsCgEvent && SourceDuration > TimeSpan.Zero;
    public string SourceDescription => SourceKind.Equals("DirectShow", StringComparison.OrdinalIgnoreCase)
        ? (string.IsNullOrWhiteSpace(VideoDevice) ? "Capture device" : VideoDevice)
        : SourceKind.Equals("Screen", StringComparison.OrdinalIgnoreCase) ? "Desktop Capture"
        : SourceKind.Equals("CG", StringComparison.OrdinalIgnoreCase) ? "CG Overlay Event" : FilePath;

    public TimeSpan InPoint
    {
        get => _inPoint;
        set
        {
            if (!Set(ref _inPoint, value < TimeSpan.Zero ? TimeSpan.Zero : value)) return;
            Raise(nameof(InPointText)); RaiseDurationDerived();
        }
    }

    public TimeSpan OutPoint
    {
        get => _outPoint;
        set
        {
            if (!Set(ref _outPoint, value < TimeSpan.Zero ? TimeSpan.Zero : value)) return;
            Raise(nameof(OutPointText)); RaiseDurationDerived();
        }
    }

    /// <summary>Total event duration. With multipart trimming this is the sum of all enabled parts.</summary>
    public TimeSpan Duration => HasParts
        ? TimeSpan.FromTicks(Parts.Where(x => x.Enabled && x.OutPoint > x.InPoint).Sum(x => x.Duration.Ticks))
        : OutPoint > InPoint ? OutPoint - InPoint : TimeSpan.Zero;
    public string DurationText => Timecode.Format(Duration);

    public string InPointText { get => Timecode.Format(InPoint); set { if (Timecode.TryParse(value, out var parsed)) InPoint = parsed; else Raise(); } }
    public string OutPointText { get => Timecode.Format(OutPoint); set { if (Timecode.TryParse(value, out var parsed)) OutPoint = parsed; else Raise(); } }

    public string Codec { get => _codec; set { if (Set(ref _codec, value ?? string.Empty)) Raise(nameof(MediaDetails)); } }
    public string VideoFormat { get => _videoFormat; set { if (Set(ref _videoFormat, value ?? string.Empty)) Raise(nameof(MediaDetails)); } }
    public string Status { get => _status; set { if (Set(ref _status, value ?? "Ready")) Raise(nameof(IsCompleted)); } }
    public bool IsCompleted => Status.Equals("Completed", StringComparison.OrdinalIgnoreCase);
    public string MediaDetails => $"{VideoFormat} · {Codec} · {SourceFrameRate:0.###} fps · {PartsSummary}";
    public string MediaPropertiesSummary => $"A:{AudioChannelMode} {AudioGainPercent}% {AudioDelayMs:+#;-#;0}ms T{(AudioTrackIndex < 0 ? "Auto" : AudioTrackIndex.ToString())} · V:{ScaleMode}/{AspectRatioMode}/{InterlaceMode} · {DecoderPreference}";

    /// <summary>Returns normalized source ranges in playout order.</summary>
    public IReadOnlyList<MediaPlaybackPart> GetPlaybackParts()
    {
        if (IsLiveSource)
            return [new MediaPlaybackPart(0, TimeSpan.Zero, Duration > TimeSpan.Zero ? Duration : TimeSpan.FromHours(24), TimeSpan.Zero)];

        if (HasParts)
        {
            var result = new List<MediaPlaybackPart>();
            var offset = TimeSpan.Zero;
            var index = 0;
            foreach (var part in Parts.Where(x => x.Enabled && x.OutPoint > x.InPoint).OrderBy(x => x.Order))
            {
                var input = part.InPoint < TimeSpan.Zero ? TimeSpan.Zero : part.InPoint;
                var output = SourceDuration > TimeSpan.Zero && part.OutPoint > SourceDuration ? SourceDuration : part.OutPoint;
                if (output <= input) continue;
                result.Add(new MediaPlaybackPart(index++, input, output, offset));
                offset += output - input;
            }
            if (result.Count > 0) return result;
        }

        var outPoint = OutPoint > InPoint ? OutPoint : SourceDuration;
        if (outPoint <= InPoint) outPoint = InPoint + TimeSpan.FromMilliseconds(40);
        return [new MediaPlaybackPart(0, InPoint, outPoint, TimeSpan.Zero)];
    }

    public (MediaPlaybackPart Part, TimeSpan SourcePosition) MapEventOffset(TimeSpan eventOffset)
    {
        var parts = GetPlaybackParts();
        if (parts.Count == 0) throw new InvalidOperationException("This event has no playable media parts.");
        var safe = eventOffset < TimeSpan.Zero ? TimeSpan.Zero : eventOffset;
        foreach (var part in parts)
        {
            if (safe < part.EventOffset + part.Duration || part.Index == parts.Count - 1)
            {
                var within = safe - part.EventOffset;
                if (within < TimeSpan.Zero) within = TimeSpan.Zero;
                if (within >= part.Duration) within = part.Duration - TimeSpan.FromMilliseconds(40);
                if (within < TimeSpan.Zero) within = TimeSpan.Zero;
                return (part, part.InPoint + within);
            }
        }
        return (parts[^1], parts[^1].InPoint);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void AttachParts(ObservableCollection<MediaPart> parts)
    {
        parts.CollectionChanged += Parts_CollectionChanged;
        foreach (var part in parts) part.PropertyChanged += Part_PropertyChanged;
    }
    private void DetachParts(ObservableCollection<MediaPart> parts)
    {
        parts.CollectionChanged -= Parts_CollectionChanged;
        foreach (var part in parts) part.PropertyChanged -= Part_PropertyChanged;
    }
    private void Parts_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null) foreach (MediaPart p in e.OldItems) p.PropertyChanged -= Part_PropertyChanged;
        if (e.NewItems is not null) foreach (MediaPart p in e.NewItems) p.PropertyChanged += Part_PropertyChanged;
        RaisePartsDerived();
    }
    private void Part_PropertyChanged(object? sender, PropertyChangedEventArgs e) => RaisePartsDerived();
    private void RaisePartsDerived()
    {
        Raise(nameof(HasParts)); Raise(nameof(EnabledPartCount)); Raise(nameof(PartsSummary)); RaiseDurationDerived(); Raise(nameof(MediaDetails));
    }
    private void RaiseDurationDerived() { Raise(nameof(Duration)); Raise(nameof(DurationText)); Raise(nameof(RundownDurationText)); }
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Raise(propertyName); return true;
    }
    private void Raise([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    public override string ToString() => Title;
}

public sealed record MediaPlaybackPart(int Index, TimeSpan InPoint, TimeSpan OutPoint, TimeSpan EventOffset)
{
    public TimeSpan Duration => OutPoint > InPoint ? OutPoint - InPoint : TimeSpan.Zero;
}
