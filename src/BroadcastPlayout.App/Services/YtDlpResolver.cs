using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace BroadcastPlayout.Services;

/// <summary>
/// Resolves supported web video pages (notably YouTube) to directly playable media URLs.
/// Modern YouTube commonly exposes separate video/audio streams, so the resolver preserves
/// both URLs and lets the playout engine open video and audio independently through FFmpeg.
/// </summary>
public static class YtDlpResolver
{
    public static bool LooksLikeWebVideoPage(string? value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)) return false;
        var host = uri.Host.ToLowerInvariant();
        return host.Contains("youtube.com") || host.Contains("youtu.be") || host.Contains("vimeo.com") || host.Contains("dailymotion.com");
    }

    public static string FindExecutable()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "yt-dlp.exe");
        if (File.Exists(local)) return local;

        var sourceTool = Path.Combine(AppContext.BaseDirectory, "tools", "yt-dlp.exe");
        if (File.Exists(sourceTool)) return sourceTool;

        return "yt-dlp.exe";
    }

    public static async Task<YtDlpResult> ResolveAsync(string pageUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pageUrl)) throw new InvalidOperationException("Web video URL is empty.");
        var url = pageUrl.Trim();

        var selectors = new[]
        {
            "bestvideo*+bestaudio/best",
            "bv*+ba/b",
            "best[acodec!=none][vcodec!=none]/best"
        };

        string lastError = string.Empty;
        foreach (var selector in selectors)
        {
            var result = await RunAsync(url, selector, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                lastError = result.Error;
                continue;
            }

            try
            {
                var parsed = ParseResolvedResult(url, result.Output);
                if (parsed is not null) return parsed;
                lastError = "yt-dlp completed but did not return a playable media URL.";
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }
        }

        var friendly = string.IsNullOrWhiteSpace(lastError) ? "yt-dlp could not resolve this web video." : lastError.Trim();
        throw new InvalidOperationException(friendly + Environment.NewLine + "Try updating the bundled yt-dlp if the site recently changed its player API.");
    }

    private static YtDlpResult? ParseResolvedResult(string pageUrl, string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var title = TryReadString(root, "title") ?? "Web Video";

        string? videoUrl = null;
        string? audioUrl = null;

        if (root.TryGetProperty("requested_formats", out var requestedFormats) && requestedFormats.ValueKind == JsonValueKind.Array)
        {
            foreach (var format in requestedFormats.EnumerateArray())
            {
                var url = ReadPlayableUrl(format);
                if (string.IsNullOrWhiteSpace(url)) continue;
                var hasVideo = HasCodec(format, "vcodec");
                var hasAudio = HasCodec(format, "acodec");
                if (videoUrl is null && hasVideo) videoUrl = url;
                if (audioUrl is null && hasAudio && (!hasVideo || !string.Equals(videoUrl, url, StringComparison.Ordinal))) audioUrl = url;
            }
        }

        if (videoUrl is null && root.TryGetProperty("requested_downloads", out var requestedDownloads) && requestedDownloads.ValueKind == JsonValueKind.Array)
        {
            foreach (var format in requestedDownloads.EnumerateArray())
            {
                var url = ReadPlayableUrl(format);
                if (string.IsNullOrWhiteSpace(url)) continue;
                if (videoUrl is null) videoUrl = url;
                if (audioUrl is null && HasCodec(format, "acodec") && !HasCodec(format, "vcodec")) audioUrl = url;
            }
        }

        if (videoUrl is null)
            videoUrl = TryReadString(root, "url") ?? TryReadString(root, "manifest_url");

        if (videoUrl is null && root.TryGetProperty("formats", out var formats) && formats.ValueKind == JsonValueKind.Array)
        {
            foreach (var format in formats.EnumerateArray().Reverse())
            {
                var url = ReadPlayableUrl(format);
                if (string.IsNullOrWhiteSpace(url)) continue;
                var hasVideo = HasCodec(format, "vcodec");
                var hasAudio = HasCodec(format, "acodec");
                if (videoUrl is null && hasVideo) videoUrl = url;
                if (audioUrl is null && hasAudio && !hasVideo) audioUrl = url;
                if (videoUrl is not null && (audioUrl is not null || hasAudio)) break;
            }
        }

        if (string.IsNullOrWhiteSpace(videoUrl)) return null;
        if (string.Equals(videoUrl, audioUrl, StringComparison.Ordinal)) audioUrl = null;
        return new YtDlpResult(pageUrl, videoUrl, audioUrl, title);
    }

    private static string? ReadPlayableUrl(JsonElement element)
    {
        var url = TryReadString(element, "url");
        if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
            (parsed.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) || parsed.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)))
            return url;

        var manifest = TryReadString(element, "manifest_url");
        if (!string.IsNullOrWhiteSpace(manifest) && Uri.TryCreate(manifest, UriKind.Absolute, out parsed) &&
            (parsed.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) || parsed.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)))
            return manifest;

        return null;
    }

    private static bool HasCodec(JsonElement element, string propertyName)
    {
        var value = TryReadString(element, propertyName);
        return !string.IsNullOrWhiteSpace(value) && !value.Equals("none", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)) return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString();
    }

    private static async Task<ResolverProcessResult> RunAsync(string pageUrl, string selector, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = FindExecutable(),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add("--no-playlist");
        psi.ArgumentList.Add("--no-warnings");
        psi.ArgumentList.Add("--no-progress");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(selector);
        psi.ArgumentList.Add("-J");
        psi.ArgumentList.Add(pageUrl);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Unable to start yt-dlp.exe.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new ResolverProcessResult(process.ExitCode, stdout, string.IsNullOrWhiteSpace(stderr) ? $"yt-dlp exited with code {process.ExitCode}." : stderr.Trim());
    }

    private sealed record ResolverProcessResult(int ExitCode, string Output, string Error);
}

public sealed record YtDlpResult(string PageUrl, string MediaUrl, string? AudioUrl, string Title);
