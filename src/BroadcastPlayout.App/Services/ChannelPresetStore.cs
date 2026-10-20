using System.Text.Json;
using BroadcastPlayout.Models;

namespace BroadcastPlayout.Services;

public static class ChannelPresetStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static void Export(string path, AppSettings settings)
    {
        var preset = new ChannelPreset
        {
            ChannelName = settings.ChannelName, ChannelId = settings.ChannelId, PlayoutPreset = settings.PlayoutPreset,
            NdiSourceName = settings.NdiSourceName, NdiProfile = settings.NdiProfile.Clone(), DeckLinkProfile = settings.DeckLinkProfile.Clone(),
            DisplayProfile = settings.DisplayProfile.Clone(), QcPreset = settings.QcPreset?.Clone() ?? new MediaQcPreset(),
            EnableQcOnImport = settings.EnableQcOnImport, EnableVirtualOutput = settings.EnableVirtualOutput,
            VirtualOutputName = settings.VirtualOutputName, VirtualOutputWidth = settings.VirtualOutputWidth, VirtualOutputHeight = settings.VirtualOutputHeight,
            VirtualOutputFrameRate = settings.VirtualOutputFrameRate, VirtualOutputPixelFormat = settings.VirtualOutputPixelFormat,
            EnableMasterOutputVolume = settings.EnableMasterOutputVolume, MasterOutputVolumePercent = settings.MasterOutputVolumePercent, MasterOutputMuted = settings.MasterOutputMuted,
            MasterMuteShortcut = settings.MasterMuteShortcut, MasterVolumeUpShortcut = settings.MasterVolumeUpShortcut, MasterVolumeDownShortcut = settings.MasterVolumeDownShortcut,
            EnableVideoProcessing = settings.EnableVideoProcessing, VideoBrightness = settings.VideoBrightness, VideoContrast = settings.VideoContrast, VideoSaturation = settings.VideoSaturation,
            VideoGamma = settings.VideoGamma, VideoHueDegrees = settings.VideoHueDegrees, VideoExposureStops = settings.VideoExposureStops, VideoTemperature = settings.VideoTemperature, VideoTint = settings.VideoTint,
            VideoSharpness = settings.VideoSharpness, VideoBlackLevel = settings.VideoBlackLevel, VideoWhiteLevel = settings.VideoWhiteLevel,
            VideoRedGainPercent = settings.VideoRedGainPercent, VideoGreenGainPercent = settings.VideoGreenGainPercent, VideoBlueGainPercent = settings.VideoBlueGainPercent,
            EnableFiller = settings.EnableFiller, FillerFolder = settings.FillerFolder
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(preset, Json));
    }

    public static ChannelPreset Import(string path)
    {
        var preset = JsonSerializer.Deserialize<ChannelPreset>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("Invalid channel preset.");
        if (!string.Equals(preset.Format, "KASHTRIX-CHANNEL-PRESET", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The selected JSON file is not a Kashtrix channel preset.");
        return preset;
    }

    public static void Apply(ChannelPreset preset, AppSettings settings)
    {
        settings.ChannelName = string.IsNullOrWhiteSpace(preset.ChannelName) ? settings.ChannelName : preset.ChannelName.Trim();
        settings.ChannelId = string.IsNullOrWhiteSpace(preset.ChannelId) ? settings.ChannelId : preset.ChannelId.Trim();
        settings.PlayoutPreset = preset.PlayoutPreset;
        settings.NdiSourceName = preset.NdiSourceName;
        settings.NdiProfile = preset.NdiProfile?.Clone() ?? settings.NdiProfile;
        settings.DeckLinkProfile = preset.DeckLinkProfile?.Clone() ?? settings.DeckLinkProfile;
        settings.DisplayProfile = preset.DisplayProfile?.Clone() ?? settings.DisplayProfile;
        settings.QcPreset = preset.QcPreset?.Clone() ?? new MediaQcPreset();
        settings.EnableQcOnImport = preset.EnableQcOnImport;
        settings.EnableVirtualOutput = preset.EnableVirtualOutput;
        settings.VirtualOutputName = string.IsNullOrWhiteSpace(preset.VirtualOutputName) ? settings.VirtualOutputName : preset.VirtualOutputName;
        settings.VirtualOutputWidth = Math.Clamp(preset.VirtualOutputWidth, 320, 7680);
        settings.VirtualOutputHeight = Math.Clamp(preset.VirtualOutputHeight, 240, 4320);
        settings.VirtualOutputFrameRate = Math.Clamp(preset.VirtualOutputFrameRate, 1, 120);
        settings.VirtualOutputPixelFormat = string.IsNullOrWhiteSpace(preset.VirtualOutputPixelFormat) ? "BGRA32" : preset.VirtualOutputPixelFormat;
        settings.EnableMasterOutputVolume = preset.EnableMasterOutputVolume;
        settings.MasterOutputVolumePercent = Math.Clamp(preset.MasterOutputVolumePercent, 0, 200);
        settings.MasterOutputMuted = preset.MasterOutputMuted;
        settings.MasterMuteShortcut = preset.MasterMuteShortcut;
        settings.MasterVolumeUpShortcut = preset.MasterVolumeUpShortcut;
        settings.MasterVolumeDownShortcut = preset.MasterVolumeDownShortcut;
        settings.EnableVideoProcessing = preset.EnableVideoProcessing;
        settings.VideoBrightness = Math.Clamp(preset.VideoBrightness, -100, 100);
        settings.VideoContrast = Math.Clamp(preset.VideoContrast, 0, 200);
        settings.VideoSaturation = Math.Clamp(preset.VideoSaturation, 0, 200);
        settings.VideoGamma = Math.Clamp(preset.VideoGamma, 0.10, 4.0);
        settings.VideoHueDegrees = Math.Clamp(preset.VideoHueDegrees, -180, 180);
        settings.VideoExposureStops = Math.Clamp(preset.VideoExposureStops, -4.0, 4.0);
        settings.VideoTemperature = Math.Clamp(preset.VideoTemperature, -100, 100);
        settings.VideoTint = Math.Clamp(preset.VideoTint, -100, 100);
        settings.VideoSharpness = Math.Clamp(preset.VideoSharpness, 0, 100);
        settings.VideoBlackLevel = Math.Clamp(preset.VideoBlackLevel, -100, 100);
        settings.VideoWhiteLevel = Math.Clamp(preset.VideoWhiteLevel, -100, 100);
        settings.VideoRedGainPercent = Math.Clamp(preset.VideoRedGainPercent, 0, 200);
        settings.VideoGreenGainPercent = Math.Clamp(preset.VideoGreenGainPercent, 0, 200);
        settings.VideoBlueGainPercent = Math.Clamp(preset.VideoBlueGainPercent, 0, 200);
        settings.EnableFiller = preset.EnableFiller;
        settings.FillerFolder = preset.FillerFolder ?? string.Empty;
    }
}
