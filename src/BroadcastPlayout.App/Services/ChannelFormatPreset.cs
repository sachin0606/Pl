using System.Globalization;
using System.Text.RegularExpressions;
using BroadcastPlayout.Models;

namespace BroadcastPlayout.Services;

/// <summary>
/// Canonical channel-raster/cadence resolver. Broadcast interlaced names use field rate
/// (1080i50 = 25 complete frames / 50 fields per second); progressive names use frame rate.
/// </summary>
public sealed record ChannelFormatSpec(string Name, int Width, int Height, double FramesPerSecond, string ScanMode)
{
    public bool IsInterlaced => !ScanMode.Equals("Progressive", StringComparison.OrdinalIgnoreCase);

    public (int N, int D) RationalFrameRate
    {
        get
        {
            if (Math.Abs(FramesPerSecond - 23.976) < .002) return (24000, 1001);
            if (Math.Abs(FramesPerSecond - 29.97) < .002) return (30000, 1001);
            if (Math.Abs(FramesPerSecond - 59.94) < .002) return (60000, 1001);
            var rounded = (int)Math.Round(FramesPerSecond);
            return (Math.Max(1, rounded), 1);
        }
    }

    public string RasterText => $"{Width}x{Height}";
    public string DisplayText => IsInterlaced
        ? $"{Name} · {RasterText} · {FramesPerSecond:0.###} frames/s · {FramesPerSecond * 2:0.###} fields/s · {ScanMode}"
        : $"{Name} · {RasterText} · {FramesPerSecond:0.###}p · Progressive";
}

public static partial class ChannelFormatPreset
{
    public static IReadOnlyList<string> Supported { get; } =
    [
        "2160p50", "2160p25",
        "1080p59.94", "1080p50", "1080p30", "1080p29.97", "1080p25", "1080p24", "1080p23.976",
        "1080i59.94", "1080i50",
        "720p59.94", "720p50", "720p30", "720p25"
    ];

    public static ChannelFormatSpec Resolve(string? preset)
    {
        var value = string.IsNullOrWhiteSpace(preset) ? "1080p50" : preset.Trim();
        var match = FormatRegex().Match(value);
        if (!match.Success) return new ChannelFormatSpec("1080p50", 1920, 1080, 50, "Progressive");

        var height = int.TryParse(match.Groups["height"].Value, out var h) ? h : 1080;
        var width = height switch { 2160 => 3840, 1080 => 1920, 720 => 1280, 576 => 720, 480 => 720, _ => 1920 };
        var scan = match.Groups["scan"].Value.Equals("i", StringComparison.OrdinalIgnoreCase)
            ? "Interlaced Upper First"
            : "Progressive";
        if (!double.TryParse(match.Groups["rate"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var nominalRate))
            nominalRate = 50;
        nominalRate = Math.Clamp(nominalRate, 1, 120);
        var frameRate = scan.StartsWith("Interlaced", StringComparison.OrdinalIgnoreCase) ? nominalRate / 2.0 : nominalRate;
        return new ChannelFormatSpec(value, width, height, frameRate, scan);
    }

    public static void ApplyToProfile(OutputProfile profile, string? preset)
    {
        var spec = Resolve(preset);
        profile.Preset = spec.RasterText;
        profile.FramesPerSecond = spec.FramesPerSecond;
        profile.ScanMode = spec.ScanMode;
    }

    public static VideoFrameData Stamp(VideoFrameData frame, ChannelFormatSpec spec, double ptsSeconds)
    {
        var (n, d) = spec.RationalFrameRate;
        return new VideoFrameData(frame.Bgra, frame.Width, frame.Height, frame.Stride, ptsSeconds, n, d);
    }

    [GeneratedRegex(@"^(?<height>2160|1080|720|576|480)(?<scan>[pi])(?<rate>\d+(?:\.\d+)?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FormatRegex();
}
