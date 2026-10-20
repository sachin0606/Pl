using FFmpeg.AutoGen.Abstractions;
using BroadcastPlayout.Models;

namespace BroadcastPlayout.Ffmpeg;

public static unsafe class MediaProbe
{
    public static MediaInfo Read(string file) => Read(new PlaylistItem { FilePath = file, SourceKind = "File" });

    public static MediaInfo Read(PlaylistItem item)
    {
        FfmpegRuntime.EnsureReady();
        AVFormatContext* format = null;
        try
        {
            format = FfmpegInput.Open(item);
            ffmpeg.avformat_find_stream_info(format, null).ThrowIfError();
            var duration = format->duration > 0 ? TimeSpan.FromSeconds(format->duration / (double)ffmpeg.AV_TIME_BASE) : TimeSpan.Zero;
            int width = 0, height = 0; double fps = 0; string codec = "unknown"; bool audio = false;
            for (var i = 0; i < format->nb_streams; i++)
            {
                var s = format->streams[i];
                if (s->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO && width == 0)
                {
                    width = s->codecpar->width; height = s->codecpar->height;
                    var r = ffmpeg.av_guess_frame_rate(format, s, null); if (r.den != 0) fps = r.num / (double)r.den;
                    codec = ffmpeg.avcodec_get_name(s->codecpar->codec_id) ?? "unknown";
                }
                if (s->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO) audio = true;
            }
            return new MediaInfo(duration, width, height, fps, codec, audio);
        }
        finally
        {
            if (format != null) ffmpeg.avformat_close_input(&format);
        }
    }
}
