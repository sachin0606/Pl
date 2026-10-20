using FFmpeg.AutoGen.Abstractions;
using BroadcastPlayout.Models;

namespace BroadcastPlayout.Ffmpeg;

/// <summary>Creates FFmpeg inputs for files, network URLs and Windows live capture sources.</summary>
public static unsafe class FfmpegInput
{
    public static AVFormatContext* Open(PlaylistItem item, AVMediaType? preferredMediaType = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        FfmpegRuntime.EnsureReady();

        var source = BuildSource(item, preferredMediaType);
        var formatName = ResolveFormat(item);
        AVInputFormat* inputFormat = null;
        if (!string.IsNullOrWhiteSpace(formatName))
        {
            inputFormat = ffmpeg.av_find_input_format(formatName);
            if (inputFormat == null)
                throw new InvalidOperationException($"Input module '{formatName}' is not available in the installed media runtime.");
        }

        AVDictionary* options = null;
        try
        {
            AddDefaultOptions(item, preferredMediaType, &options);
            AddCustomOptions(item.InputOptions, &options);

            AVFormatContext* context = null;
            var result = ffmpeg.avformat_open_input(&context, source, inputFormat, &options);
            if (result < 0)
                throw new InvalidOperationException($"Cannot open {item.SourceKind} input '{source}': {result.ToFfmpegError()}");
            if (context == null) throw new InvalidOperationException("The media engine could not initialize this input source.");
            return context;
        }
        finally
        {
            ffmpeg.av_dict_free(&options);
        }
    }

    public static string BuildSource(PlaylistItem item, AVMediaType? preferredMediaType = null)
    {
        if (item.SourceKind.Equals("DirectShow", StringComparison.OrdinalIgnoreCase))
        {
            if (preferredMediaType == AVMediaType.AVMEDIA_TYPE_VIDEO)
            {
                if (string.IsNullOrWhiteSpace(item.VideoDevice)) throw new InvalidOperationException("No DirectShow video device is selected.");
                return $"video={item.VideoDevice}";
            }
            if (preferredMediaType == AVMediaType.AVMEDIA_TYPE_AUDIO)
            {
                if (string.IsNullOrWhiteSpace(item.AudioDevice)) throw new InvalidOperationException("No DirectShow audio device is selected.");
                return $"audio={item.AudioDevice}";
            }

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(item.VideoDevice)) parts.Add($"video={item.VideoDevice}");
            if (!string.IsNullOrWhiteSpace(item.AudioDevice)) parts.Add($"audio={item.AudioDevice}");
            if (parts.Count == 0) throw new InvalidOperationException("No DirectShow video/audio device is selected.");
            return string.Join(":", parts);
        }

        if (item.SourceKind.Equals("Screen", StringComparison.OrdinalIgnoreCase)) return "desktop";
        if (item.SourceKind.Equals("URL", StringComparison.OrdinalIgnoreCase) &&
            preferredMediaType == AVMediaType.AVMEDIA_TYPE_AUDIO &&
            !string.IsNullOrWhiteSpace(item.AlternateAudioUrl))
            return item.AlternateAudioUrl.Trim();
        if (string.IsNullOrWhiteSpace(item.FilePath)) throw new InvalidOperationException("Input source is empty.");
        return item.FilePath.Trim();
    }

    public static string? ResolveFormat(PlaylistItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.InputFormat)) return item.InputFormat.Trim();
        if (item.SourceKind.Equals("DirectShow", StringComparison.OrdinalIgnoreCase)) return "dshow";
        if (item.SourceKind.Equals("Screen", StringComparison.OrdinalIgnoreCase)) return "gdigrab";
        return null;
    }

    private static void AddDefaultOptions(PlaylistItem item, AVMediaType? preferredMediaType, AVDictionary** options)
    {
        void Set(string key, string value) => ffmpeg.av_dict_set(options, key, value, 0).ThrowIfError();

        if (item.SourceKind.Equals("DirectShow", StringComparison.OrdinalIgnoreCase))
        {
            Set("rtbufsize", "512M");
            if (preferredMediaType != AVMediaType.AVMEDIA_TYPE_AUDIO)
            {
                if (item.CaptureWidth > 0 && item.CaptureHeight > 0)
                    Set("video_size", $"{item.CaptureWidth}x{item.CaptureHeight}");
                if (item.SourceFrameRate > 0)
                    Set("framerate", item.SourceFrameRate.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            }
            return;
        }

        if (item.SourceKind.Equals("Screen", StringComparison.OrdinalIgnoreCase))
        {
            if (item.SourceFrameRate > 0) Set("framerate", item.SourceFrameRate.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            Set("draw_mouse", "1");
            return;
        }

        if (item.SourceKind.Equals("URL", StringComparison.OrdinalIgnoreCase))
        {
            var source = item.FilePath.Trim();
            if (source.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
            {
                Set("rtsp_transport", "tcp");
                Set("rw_timeout", "5000000");
            }
            else if (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                Set("reconnect", "1");
                Set("reconnect_streamed", "1");
                Set("reconnect_delay_max", "5");
            }
        }
    }

    private static void AddCustomOptions(string? text, AVDictionary** options)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var split = part.IndexOf('=');
            if (split <= 0 || split >= part.Length - 1) continue;
            var key = part[..split].Trim();
            var value = part[(split + 1)..].Trim();
            if (key.Length == 0 || value.Length == 0) continue;
            ffmpeg.av_dict_set(options, key, value, 0).ThrowIfError();
        }
    }
}
