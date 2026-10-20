using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Kashtrix.OutputEngine.Models;

public enum BroadcastOutputProtocol
{
    DeckLink,
    NDI,
    Matrox,
    AJA,
    DvbUdp,
    SRT,
    RTMP,
    HLS,
    MPD,
    RTSP,
    CgOutput,
    VirtualOutput
}

public enum OutputStatus
{
    Standby,
    Starting,
    Online,
    Warning,
    Error
}

public enum OutputInputSource
{
    PlayoutProgram,
    CgProgram,
    Manual
}

public class OutputChannel : INotifyPropertyChanged
{
    public Guid Id { get; set; } = Guid.NewGuid();

    private OutputInputSource _inputSource = OutputInputSource.PlayoutProgram;
    public OutputInputSource InputSource
    {
        get => _inputSource;
        set
        {
            if (SetField(ref _inputSource, value))
                OnPropertyChanged(nameof(InputSourceDisplayName));
        }
    }

    private string _manualInputSource = string.Empty;
    public string ManualInputSource
    {
        get => _manualInputSource;
        set
        {
            if (SetField(ref _manualInputSource, value ?? string.Empty))
                OnPropertyChanged(nameof(InputSourceDisplayName));
        }
    }

    public string InputSourceDisplayName => InputSource switch
    {
        OutputInputSource.CgProgram => "CG PROGRAM BUS",
        OutputInputSource.Manual => string.IsNullOrWhiteSpace(ManualInputSource) ? "MANUAL INPUT" : $"MANUAL · {ManualInputSource}",
        _ => "PLAYOUT PROGRAM BUS"
    };

    private string _name = "Output Channel";
    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    private BroadcastOutputProtocol _protocol = BroadcastOutputProtocol.DeckLink;
    public BroadcastOutputProtocol Protocol
    {
        get => _protocol;
        set
        {
            if (SetField(ref _protocol, value))
            {
                OnPropertyChanged(nameof(ProtocolDisplayName));
                OnPropertyChanged(nameof(IsHardwareDevice));
                OnPropertyChanged(nameof(IsCardHardware));
                OnPropertyChanged(nameof(RequiresGpuEncoder));
                OnPropertyChanged(nameof(IsNetworkStream));
                OnPropertyChanged(nameof(FormattedBitrate));
                OnPropertyChanged(nameof(FormattedFps));
                OnPropertyChanged(nameof(FormattedFrames));
            }
        }
    }

    public string ProtocolDisplayName => Protocol switch
    {
        BroadcastOutputProtocol.DeckLink => "DECKLINK SDI / HDMI",
        BroadcastOutputProtocol.NDI => "NDI 6 / HX BROADCAST",
        BroadcastOutputProtocol.Matrox => "MATROX DSX / X.MIO",
        BroadcastOutputProtocol.AJA => "AJA KONA / CORVID",
        BroadcastOutputProtocol.CgOutput => "CG OUTPUT (FILL+KEY / NDI)",
        BroadcastOutputProtocol.VirtualOutput => "VIRTUAL DIRECTSHOW OUTPUT",
        BroadcastOutputProtocol.DvbUdp => "DVB-TS UDP / RTP (CBR)",
        BroadcastOutputProtocol.SRT => "SRT (CALLER / LISTENER)",
        BroadcastOutputProtocol.RTMP => "RTMP / RTMPS LIVE",
        BroadcastOutputProtocol.HLS => "DVB-COMPLIANT HLS",
        BroadcastOutputProtocol.MPD => "DASH (MPD) STREAM",
        BroadcastOutputProtocol.RTSP => "RTSP STREAM",
        _ => Protocol.ToString()
    };

    public bool IsHardwareDevice => Protocol is BroadcastOutputProtocol.DeckLink or BroadcastOutputProtocol.Matrox or BroadcastOutputProtocol.AJA or BroadcastOutputProtocol.NDI or BroadcastOutputProtocol.CgOutput or BroadcastOutputProtocol.VirtualOutput;
    public bool IsCardHardware => Protocol is BroadcastOutputProtocol.DeckLink or BroadcastOutputProtocol.Matrox or BroadcastOutputProtocol.AJA;
    public bool RequiresGpuEncoder => !IsCardHardware && Protocol is not BroadcastOutputProtocol.VirtualOutput;
    public bool IsNetworkStream => !IsHardwareDevice;

    private string _destinationUri = "DeckLink 8K Pro (Device 1 - SDI 1)";
    public string DestinationUri
    {
        get => _destinationUri;
        set => SetField(ref _destinationUri, value);
    }

    private string _rasterFormat = "1080p50";
    public string RasterFormat
    {
        get => _rasterFormat;
        set => SetField(ref _rasterFormat, value);
    }

    private int _customWidth = 1920;
    public int CustomWidth
    {
        get => _customWidth;
        set => SetField(ref _customWidth, value);
    }

    private int _customHeight = 1080;
    public int CustomHeight
    {
        get => _customHeight;
        set => SetField(ref _customHeight, value);
    }

    private bool _isCustomResolution;
    public bool IsCustomResolution
    {
        get => _isCustomResolution;
        set => SetField(ref _isCustomResolution, value);
    }

    private double _targetFps = 50.0;
    public double TargetFps
    {
        get => _targetFps;
        set
        {
            if (SetField(ref _targetFps, value))
            {
                OnPropertyChanged(nameof(FormattedFps));
            }
        }
    }

    private double _runningFps = 50.0;
    public double RunningFps
    {
        get => _runningFps;
        set
        {
            if (SetField(ref _runningFps, value))
            {
                OnPropertyChanged(nameof(FormattedFps));
            }
        }
    }

    private long _framesTransmitted;
    public long FramesTransmitted
    {
        get => _framesTransmitted;
        set
        {
            if (SetField(ref _framesTransmitted, value))
            {
                OnPropertyChanged(nameof(FormattedFrames));
            }
        }
    }

    private long _droppedFrames;
    public long DroppedFrames
    {
        get => _droppedFrames;
        set => SetField(ref _droppedFrames, value);
    }

    private double _bitrateMbps = 15.0;
    public double BitrateMbps
    {
        get => _bitrateMbps;
        set
        {
            if (SetField(ref _bitrateMbps, value))
            {
                OnPropertyChanged(nameof(FormattedBitrate));
            }
        }
    }

    public string FormattedBitrate => IsCardHardware ? "Direct Baseband (Raw)" : $"{BitrateMbps:F2} Mbps";
    public string FormattedFps => IsCardHardware ? $"{TargetFps:0.##} fps (HW Locked)" : $"{RunningFps:F2} fps";
    public string FormattedFrames => IsCardHardware ? (Status == OutputStatus.Online ? $"{FramesTransmitted:N0} (HW)" : "—") : $"{FramesTransmitted:N0}";

    private double _pcrJitterNs = 12.4;
    public double PcrJitterNs
    {
        get => _pcrJitterNs;
        set => SetField(ref _pcrJitterNs, value);
    }

    private double _latencyMs = 3.2;
    public double LatencyMs
    {
        get => _latencyMs;
        set => SetField(ref _latencyMs, value);
    }

    private int _bufferPercent = 95;
    public int BufferPercent
    {
        get => _bufferPercent;
        set => SetField(ref _bufferPercent, value);
    }

    private bool _isEnabled = true;
    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetField(ref _isEnabled, value);
    }

    private OutputStatus _status = OutputStatus.Online;
    public OutputStatus Status
    {
        get => _status;
        set
        {
            if (SetField(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusColorBrush));
                OnPropertyChanged(nameof(HasAlert));
                OnPropertyChanged(nameof(StatusBadgeText));
                OnPropertyChanged(nameof(FormattedFrames));
            }
        }
    }

    public string StatusBadgeText => Status switch
    {
        OutputStatus.Online => "ONLINE",
        OutputStatus.Starting => "STARTING",
        OutputStatus.Warning => "ALERT (!)",
        OutputStatus.Error => "FAULT (!)",
        _ => "STANDBY"
    };

    public string StatusColorBrush => Status switch
    {
        OutputStatus.Online => "#FF10B981", // Green
        OutputStatus.Starting => "#FF06B6D4", // Cyan
        OutputStatus.Warning => "#FFF59E0B", // Amber / Alert
        OutputStatus.Error => "#FFEF4444", // Red Fault
        _ => "#FF64748B" // Slate / Standby
    };

    private string _alertMessage = string.Empty;
    public string AlertMessage
    {
        get => _alertMessage;
        set
        {
            if (SetField(ref _alertMessage, value))
            {
                OnPropertyChanged(nameof(HasAlert));
            }
        }
    }

    public bool HasAlert => !string.IsNullOrWhiteSpace(AlertMessage) || Status is OutputStatus.Warning or OutputStatus.Error;

    // Broadcast DVB & Signaling properties
    public bool DvbStandardEnabled { get; set; } = true;
    public bool Scte35Enabled { get; set; } = true;
    public bool MosEnabled { get; set; } = true;
    public int ServiceId { get; set; } = 101;
    public int PmtPid { get; set; } = 256;
    public int VideoPid { get; set; } = 257;
    public int AudioPid { get; set; } = 258;
    public int PcrPid { get; set; } = 257;
    public int Scte35Pid { get; set; } = 500;
    public string ProviderName { get; set; } = "Kashtrix Broadcast";
    public string ServiceName { get; set; } = "Prime HD Playout";

    // GPU-accelerated encoding selection
    private string _gpuEncoder = "NVENC H.264 (CUDA)";
    public string GpuEncoder
    {
        get => _gpuEncoder;
        set => SetField(ref _gpuEncoder, value);
    }

    // Custom streaming URL presets (for RTMP/social/CDN outputs)
    private string _streamPreset = string.Empty;
    public string StreamPreset
    {
        get => _streamPreset;
        set => SetField(ref _streamPreset, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}

public class BroadcastEventLog
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string Category { get; set; } = "GENERAL"; // SCTE-35, MOS, DVB-TS, HARDWARE, ALERT
    public string ChannelName { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Level { get; set; } = "INFO"; // INFO, WARN, ERROR, SUCCESS

    public string FormattedTime => Timestamp.ToString("HH:mm:ss.fff");

    public string LevelColorBrush => Level switch
    {
        "WARN" => "#FFF59E0B",
        "ERROR" => "#FFEF4444",
        "SUCCESS" => "#FF10B981",
        _ => "#FF94A3B8"
    };
}

public class Scte35SpliceEvent
{
    public uint EventId { get; set; }
    public string CommandType { get; set; } = "splice_insert"; // splice_insert, time_signal
    public double DurationSeconds { get; set; } = 30.0;
    public string PtsTimestampHex { get; set; } = "0x00000000";
    public string State { get; set; } = "Active"; // Scheduled, Active, Complete
    public DateTime Timestamp { get; set; } = DateTime.Now;
}
