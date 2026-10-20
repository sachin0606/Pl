using BroadcastPlayout.Ffmpeg;
using BroadcastPlayout.Models;

namespace BroadcastPlayout.Services;

public static class QualityControlService
{
    public static async Task<MediaQcResult> CheckAsync(string file, MediaQcPreset preset, bool deepDecode = false, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var result = new MediaQcResult { FilePath = file };
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(file)) throw new FileNotFoundException("Media file was not found.", file);
            var info = await Task.Run(() => MediaProbe.Read(file), ct).ConfigureAwait(false);
            result.MediaInfo = info;

            var issues = new List<string>();
            var log = new List<string>
            {
                $"[Header] File: {Path.GetFileName(file)}",
                $"[Header] Duration: {Format(info.Duration)}",
                $"[Header] Video: {info.Width}x{info.Height} @ {info.FramesPerSecond:0.###} fps · {info.VideoCodec}",
                $"[Header] Audio: {(info.HasAudio ? "present" : "not detected")}",
                $"[Header] Size: {new FileInfo(file).Length / (1024d * 1024d):0.00} MiB"
            };

            if (info.Duration <= TimeSpan.Zero) issues.Add("Duration is zero or unavailable");
            if (info.Width <= 0 || info.Height <= 0) issues.Add("No decodable video stream");
            if (preset.RequireExactRaster && (info.Width != preset.Width || info.Height != preset.Height))
                issues.Add($"Raster {info.Width}x{info.Height} does not match required {preset.Width}x{preset.Height}");
            else if (!preset.RequireExactRaster && (info.Width > preset.Width || info.Height > preset.Height))
                issues.Add($"Raster {info.Width}x{info.Height} exceeds preset {preset.Width}x{preset.Height}");
            if (info.FramesPerSecond > 0 && (info.FramesPerSecond + .001 < preset.MinimumFrameRate || info.FramesPerSecond - .001 > preset.MaximumFrameRate))
                issues.Add($"Frame rate {info.FramesPerSecond:0.###} fps is outside {preset.MinimumFrameRate:0.###}-{preset.MaximumFrameRate:0.###}");
            if (preset.RequireAudio && !info.HasAudio) issues.Add("Audio stream is required");
            if (preset.CodecSet.Count > 0 && !preset.CodecSet.Contains(info.VideoCodec)) issues.Add($"Codec {info.VideoCodec} is not allowed");
            var fi = new FileInfo(file);
            if (preset.MaximumFileBytes > 0 && fi.Length > preset.MaximumFileBytes) issues.Add("File exceeds preset maximum size");

            if (deepDecode && issues.All(x => !x.Contains("No decodable", StringComparison.OrdinalIgnoreCase)))
            {
                var decode = await Task.Run(() => DecodeTiming(file, info, progress, ct), ct).ConfigureAwait(false);
                log.Add($"[Decode] Video frames decoded: {decode.Frames:n0}");
                log.Add($"[Decode] First video position: {decode.FirstPts:0.000} sec");
                log.Add($"[Decode] Last video position: {decode.LastPts:0.000} sec");
                log.Add($"[Decode] Decoded duration estimate: {decode.DecodedDuration:0.000} sec");

                var headerSeconds = info.Duration.TotalSeconds;
                var difference = decode.DecodedDuration - headerSeconds;
                if (Math.Abs(difference) >= .04)
                {
                    var direction = difference > 0 ? "longer" : "shorter";
                    log.Add($"[Warning] Stream 0 (Video) Header: stream is {direction} than clip duration by {Math.Abs(difference):0.00} sec");
                }
                if (decode.LastPts + Math.Max(.04, 1.5 / Math.Max(1, info.FramesPerSecond)) < headerSeconds)
                {
                    var gap = headerSeconds - decode.LastPts;
                    log.Add($"[Low] Stream 0 (Video) Gap at end: last decoded is {gap:0.00} sec before clip end. Last position {Format(TimeSpan.FromSeconds(decode.LastPts))}");
                }
                if (Math.Abs(difference) > 1.0)
                    issues.Add($"Decoded video duration differs from header by {Math.Abs(difference):0.00} sec");
            }
            else if (!deepDecode)
            {
                log.Add("[Decode] Fast QC selected · full frame decode was not requested");
            }

            foreach (var issue in issues) log.Add("[Issue] " + issue);
            if (issues.Count == 0) log.Add("[OK] Media conforms to the selected Kashtrix QC preset.");

            result.Passed = issues.Count == 0;
            result.Summary = result.Passed
                ? $"PASS · {info.Width}x{info.Height} · {info.FramesPerSecond:0.###} fps · {info.VideoCodec}"
                : $"FAIL · {issues.Count} issue(s)";
            result.Detail = string.Join(Environment.NewLine, log);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            result.Passed = false;
            result.Summary = "FAIL · unable to validate";
            result.Detail = "[Error] " + ex.Message;
        }
        return result;
    }

    private static DecodeSummary DecodeTiming(string file, MediaInfo info, IProgress<double>? progress, CancellationToken ct)
    {
        var item = new PlaylistItem { FilePath = file, SourceKind = "File", SourceDuration = info.Duration, OutPoint = info.Duration };
        using var decoder = new VideoDecoder(item);
        long frames = 0;
        var first = double.NaN;
        var last = 0.0;
        var expected = Math.Max(1.0, info.Duration.TotalSeconds * Math.Max(1, info.FramesPerSecond));
        while (decoder.TryRead(out var frame))
        {
            ct.ThrowIfCancellationRequested();
            if (double.IsNaN(first)) first = frame.PtsSeconds;
            last = frame.PtsSeconds;
            frames++;
            if ((frames & 127) == 0) progress?.Report(Math.Clamp(frames / expected, 0, 1));
        }
        if (frames == 0 || double.IsNaN(first)) throw new InvalidDataException("Full decode produced no video frames.");
        var frameDuration = info.FramesPerSecond > 0 ? 1.0 / info.FramesPerSecond : 0.04;
        var decodedDuration = Math.Max(0, last - first) + frameDuration;
        progress?.Report(1);
        return new DecodeSummary(frames, first, last, decodedDuration);
    }

    private static string Format(TimeSpan value) => $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}.{value.Milliseconds:000}";
    private sealed record DecodeSummary(long Frames, double FirstPts, double LastPts, double DecodedDuration);
}
