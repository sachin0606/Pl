using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BroadcastPlayout.Models;

public sealed class MediaQcPreset : INotifyPropertyChanged
{
    private string _name = "Broadcast HD 1080";
    private int _width = 1920;
    private int _height = 1080;
    private double _minimumFrameRate = 24;
    private double _maximumFrameRate = 60;
    private bool _requireAudio = true;
    private bool _requireExactRaster;
    private long _maximumFileBytes;
    private string _allowedVideoCodecs = "h264,hevc,mpeg2video,dnxhd,prores";

    public string Name { get => _name; set => Set(ref _name, string.IsNullOrWhiteSpace(value) ? "QC Preset" : value.Trim()); }
    public int Width { get => _width; set => Set(ref _width, Math.Max(0, value)); }
    public int Height { get => _height; set => Set(ref _height, Math.Max(0, value)); }
    public double MinimumFrameRate { get => _minimumFrameRate; set => Set(ref _minimumFrameRate, Math.Max(0, value)); }
    public double MaximumFrameRate { get => _maximumFrameRate; set => Set(ref _maximumFrameRate, Math.Max(MinimumFrameRate, value)); }
    public bool RequireAudio { get => _requireAudio; set => Set(ref _requireAudio, value); }
    public bool RequireExactRaster { get => _requireExactRaster; set => Set(ref _requireExactRaster, value); }
    public long MaximumFileBytes { get => _maximumFileBytes; set => Set(ref _maximumFileBytes, Math.Max(0, value)); }
    public string AllowedVideoCodecs { get => _allowedVideoCodecs; set => Set(ref _allowedVideoCodecs, value ?? string.Empty); }
    public string Summary => $"{Name} · {(RequireExactRaster ? "=" : "≤")} {Width}x{Height} · {MinimumFrameRate:0.###}-{MaximumFrameRate:0.###} fps";
    public IReadOnlySet<string> CodecSet => AllowedVideoCodecs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
    public MediaQcPreset Clone() => new() { Name=Name, Width=Width, Height=Height, MinimumFrameRate=MinimumFrameRate, MaximumFrameRate=MaximumFrameRate, RequireAudio=RequireAudio, RequireExactRaster=RequireExactRaster, MaximumFileBytes=MaximumFileBytes, AllowedVideoCodecs=AllowedVideoCodecs };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field,T value,[CallerMemberName]string? name=null){if(EqualityComparer<T>.Default.Equals(field,value))return;field=value;PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(name));PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(Summary)));}
}

public sealed class MediaQcResult
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName => Path.GetFileName(FilePath);
    public bool Passed { get; set; }
    public string Status => Passed ? "PASS" : "FAIL";
    public string Summary { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public MediaInfo? MediaInfo { get; set; }
    public DateTime CheckedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class ChannelPreset
{
    public string Format { get; set; } = "KASHTRIX-CHANNEL-PRESET";
    public int Version { get; set; } = 2;
    public string ChannelName { get; set; } = "KASHTRIX NEWS HD";
    public string ChannelId { get; set; } = string.Empty;
    public string PlayoutPreset { get; set; } = "1080p50";
    public string NdiSourceName { get; set; } = "KASHTRIX-PLAYOUT-01";
    public OutputProfile NdiProfile { get; set; } = new() { Preset = "1920x1080" };
    public OutputProfile DeckLinkProfile { get; set; } = new() { Preset = "1920x1080" };
    public OutputProfile DisplayProfile { get; set; } = new() { Preset = "Source", Scaling = "Fit" };
    public MediaQcPreset QcPreset { get; set; } = new();
    public bool EnableQcOnImport { get; set; } = true;
    public bool EnableVirtualOutput { get; set; }
    public string VirtualOutputName { get; set; } = "Kashtrix Playout Virtual Output";
    public int VirtualOutputWidth { get; set; } = 1920;
    public int VirtualOutputHeight { get; set; } = 1080;
    public double VirtualOutputFrameRate { get; set; } = 50;
    public string VirtualOutputPixelFormat { get; set; } = "BGRA32";
    public bool EnableMasterOutputVolume { get; set; } = true;
    public int MasterOutputVolumePercent { get; set; } = 100;
    public bool MasterOutputMuted { get; set; }
    public string MasterMuteShortcut { get; set; } = "Ctrl+M";
    public string MasterVolumeUpShortcut { get; set; } = "Ctrl+Up";
    public string MasterVolumeDownShortcut { get; set; } = "Ctrl+Down";
    public bool EnableVideoProcessing { get; set; }
    public int VideoBrightness { get; set; }
    public int VideoContrast { get; set; } = 100;
    public int VideoSaturation { get; set; } = 100;
    public double VideoGamma { get; set; } = 1.0;
    public int VideoHueDegrees { get; set; }
    public double VideoExposureStops { get; set; }
    public int VideoTemperature { get; set; }
    public int VideoTint { get; set; }
    public int VideoSharpness { get; set; }
    public int VideoBlackLevel { get; set; }
    public int VideoWhiteLevel { get; set; }
    public int VideoRedGainPercent { get; set; } = 100;
    public int VideoGreenGainPercent { get; set; } = 100;
    public int VideoBlueGainPercent { get; set; } = 100;
    public bool EnableFiller { get; set; } = true;
    public string FillerFolder { get; set; } = string.Empty;
}
