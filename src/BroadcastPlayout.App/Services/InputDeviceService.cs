using System.Diagnostics;
using System.Text.RegularExpressions;

namespace BroadcastPlayout.Services;

public sealed record CaptureDeviceLists(IReadOnlyList<string> VideoDevices, IReadOnlyList<string> AudioDevices);

public static partial class InputDeviceService
{
    public static async Task<CaptureDeviceLists> EnumerateDirectShowAsync()
    {
        var ffmpegPath = Path.Combine(AppContext.BaseDirectory, "native", "ffmpeg", "ffmpeg.exe");
        if (!File.Exists(ffmpegPath))
            return new CaptureDeviceLists([], []);

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = "-hide_banner -list_devices true -f dshow -i dummy",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process is null) return new CaptureDeviceLists([], []);
        var stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);

        var video = new List<string>();
        var audio = new List<string>();
        foreach (var line in stderr.Split('\n'))
        {
            if (line.Contains("Alternative name", StringComparison.OrdinalIgnoreCase)) continue;
            var match = DeviceRegex().Match(line);
            if (!match.Success) continue;
            var name = match.Groups["name"].Value.Trim();
            var kind = match.Groups["kind"].Value;
            if (kind.Equals("video", StringComparison.OrdinalIgnoreCase) && !video.Contains(name, StringComparer.OrdinalIgnoreCase)) video.Add(name);
            if (kind.Equals("audio", StringComparison.OrdinalIgnoreCase) && !audio.Contains(name, StringComparer.OrdinalIgnoreCase)) audio.Add(name);
        }

        return new CaptureDeviceLists(video, audio);
    }

    [GeneratedRegex("\\\"(?<name>[^\\\"]+)\\\"\\s+\\((?<kind>video|audio)\\)", RegexOptions.IgnoreCase)]
    private static partial Regex DeviceRegex();
}
