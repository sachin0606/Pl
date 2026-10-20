using System.Diagnostics;
using BroadcastPlayout.Models;

namespace BroadcastPlayout.Services;

/// <summary>Creates a real H.264/AAC editing/review proxy and returns the generated file path.</summary>
public static class ProxyGenerationService
{
    public static async Task<string> GenerateAsync(string masterPath, string? proxyRoot = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(masterPath) || !File.Exists(masterPath))
            throw new FileNotFoundException("Master media file was not found.", masterPath);

        var ffmpeg = Path.Combine(AppContext.BaseDirectory, "native", "ffmpeg", "ffmpeg.exe");
        if (!File.Exists(ffmpeg)) throw new FileNotFoundException("Kashtrix FFmpeg runtime is missing.", ffmpeg);

        if (string.IsNullOrWhiteSpace(proxyRoot))
        {
            try { proxyRoot = SettingsStore.Load().MamProxyRoot; } catch { }
        }
        if (string.IsNullOrWhiteSpace(proxyRoot))
            proxyRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Kashtrix Proxy");

        var fullRoot = Path.GetFullPath(proxyRoot);
        Directory.CreateDirectory(fullRoot);
        var safeBase = MakeSafe(Path.GetFileNameWithoutExtension(masterPath));
        var hashSuffix = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(masterPath)))).ToLowerInvariant()[..10];
        var output = Path.Combine(fullRoot, $"{safeBase}.{hashSuffix}.proxy.mp4");
        var encoder = ResolveH264Encoder(ffmpeg);

        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(masterPath);
        psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("0:v:0?");
        psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("0:a:0?");
        psi.ArgumentList.Add("-vf"); psi.ArgumentList.Add("scale='min(1280,iw)':-2:flags=lanczos");
        psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add(encoder);
        if (encoder.Equals("libx264", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("-preset"); psi.ArgumentList.Add("veryfast");
            psi.ArgumentList.Add("-crf"); psi.ArgumentList.Add("23");
        }
        else
        {
            psi.ArgumentList.Add("-b:v"); psi.ArgumentList.Add("3M");
            psi.ArgumentList.Add("-maxrate"); psi.ArgumentList.Add("4M");
            psi.ArgumentList.Add("-bufsize"); psi.ArgumentList.Add("8M");
        }
        psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add(encoder.EndsWith("_mf", StringComparison.OrdinalIgnoreCase) ? "nv12" : "yuv420p");
        psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add("aac");
        psi.ArgumentList.Add("-b:a"); psi.ArgumentList.Add("128k");
        psi.ArgumentList.Add("-ac"); psi.ArgumentList.Add("2");
        psi.ArgumentList.Add("-movflags"); psi.ArgumentList.Add("+faststart");
        psi.ArgumentList.Add("-map_metadata"); psi.ArgumentList.Add("0");
        psi.ArgumentList.Add("-y"); psi.ArgumentList.Add(output);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Unable to start proxy encoder.");
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            throw;
        }
        var stderr = await stderrTask.ConfigureAwait(false);
        _ = await stdoutTask.ConfigureAwait(false);
        if (process.ExitCode != 0 || !File.Exists(output) || new FileInfo(output).Length == 0)
            throw new InvalidOperationException("Proxy generation failed. " + LastLine(stderr));
        return output;
    }

    private static string ResolveH264Encoder(string ffmpeg)
    {
        try
        {
            var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("-hide_banner"); psi.ArgumentList.Add("-encoders");
            using var p = Process.Start(psi);
            if (p is null) return "libx264";
            var text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            if (!p.WaitForExit(5000)) { try { p.Kill(true); } catch { } }
            foreach (var candidate in new[] { "h264_mf", "h264_qsv", "h264_nvenc", "h264_amf", "libx264", "libopenh264" })
                if (text.Contains(" " + candidate + " ", StringComparison.OrdinalIgnoreCase)) return candidate;
        }
        catch { }
        return "libx264";
    }

    private static string MakeSafe(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = string.Concat((string.IsNullOrWhiteSpace(value) ? "asset" : value).Select(c => invalid.Contains(c) ? '_' : c));
        return cleaned.Length <= 120 ? cleaned : cleaned[..120];
    }

    private static string LastLine(string text)
    {
        var lines = (text ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        return lines.Length == 0 ? "No encoder diagnostics were returned." : lines[^1];
    }
}
