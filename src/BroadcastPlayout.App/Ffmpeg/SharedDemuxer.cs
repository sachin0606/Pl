using FFmpeg.AutoGen.Abstractions;
using BroadcastPlayout.Models;

namespace BroadcastPlayout.Ffmpeg;

/// <summary>
/// One input (file or network URL) feeding both decoders of a playout segment. Opening the source
/// once halves file I/O and, for live URLs, avoids two unrelated network sessions whose audio and
/// video drift apart (or which a server refuses). A reader thread routes packets into bounded
/// per-stream queues; the reader thread owns and finally closes the format context.
/// </summary>
public sealed unsafe class SharedDemuxer : IDisposable
{
    private readonly AVFormatContext* _format;
    private readonly PacketQueue _video;
    private readonly PacketQueue? _audio;
    private readonly object _lifecycle = new();
    private Thread? _reader;
    private volatile bool _stop;
    private bool _closed;

    private static long _opened;

    /// <summary>Diagnostics: shared inputs opened in this process.</summary>
    public static long OpenedCount => Interlocked.Read(ref _opened);

    public int VideoStreamIndex { get; }
    public int AudioStreamIndex { get; }
    public bool HasAudio => _audio is not null;
    internal AVFormatContext* Format => _format;

    private SharedDemuxer(AVFormatContext* format, int videoStream, int audioStream)
    {
        _format = format;
        VideoStreamIndex = videoStream;
        AudioStreamIndex = audioStream;
        // Bounds cover a few seconds of interleave so one slow consumer never starves the other.
        _video = new PacketQueue(240);
        _audio = audioStream >= 0 ? new PacketQueue(480) : null;
    }

    /// <summary>Shared input is used for files and plain network URLs. Capture devices and NDI keep their own paths.</summary>
    public static bool Supports(PlaylistItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.InputFormat)) return false;
        if (item.SourceKind.Equals("File", StringComparison.OrdinalIgnoreCase)) return true;
        return item.SourceKind.Equals("URL", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(item.AlternateAudioUrl);
    }

    /// <summary>Opens the input and selects the streams, or returns null so the caller falls back to separate inputs.</summary>
    public static SharedDemuxer? TryOpen(PlaylistItem item, bool wantAudio)
    {
        if (!Supports(item)) return null;
        FfmpegRuntime.EnsureReady();
        AVFormatContext* format = null;
        try
        {
            format = FfmpegInput.Open(item);
            if (ffmpeg.avformat_find_stream_info(format, null) < 0) { Close(format); return null; }
            AVCodec* decoder = null;
            var video = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &decoder, 0);
            if (video < 0) { Close(format); return null; }
            var audio = wantAudio ? AudioDecoder.FindAudioStream(format, item, out _) : -1;
            Interlocked.Increment(ref _opened);
            return new SharedDemuxer(format, video, audio);
        }
        catch
        {
            if (format != null) Close(format);
            return null;
        }
    }

    /// <summary>Seeks every stream to the keyframe at or before <paramref name="time"/>. Call before <see cref="Start"/>.</summary>
    public void Seek(TimeSpan time)
    {
        var ts = (long)(Math.Max(0, time.TotalSeconds) * ffmpeg.AV_TIME_BASE);
        ffmpeg.av_seek_frame(_format, -1, ts, ffmpeg.AVSEEK_FLAG_BACKWARD).ThrowIfError();
    }

    public void Start()
    {
        lock (_lifecycle)
        {
            if (_reader is not null || _closed) return;
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "Kashtrix Program Demux" };
            _reader.Start();
        }
    }

    /// <summary>Moves the next video packet into <paramref name="destination"/>. Returns false at end of stream or after close.</summary>
    internal bool ReadVideo(AVPacket* destination, CancellationToken ct) => _video.Dequeue(destination, ct);

    internal bool ReadAudio(AVPacket* destination, CancellationToken ct) => _audio is not null && _audio.Dequeue(destination, ct);

    /// <summary>A consumer that stops early must say so, or the reader could block on its full queue.</summary>
    internal void ReleaseVideo() => _video.Abandon();
    internal void ReleaseAudio() => _audio?.Abandon();

    private void ReadLoop()
    {
        var packet = ffmpeg.av_packet_alloc();
        try
        {
            while (!_stop)
            {
                var read = ffmpeg.av_read_frame(_format, packet);
                if (read < 0) break; // EOF or input error: both decoders drain and finish.
                var queue = packet->stream_index == VideoStreamIndex ? _video
                    : packet->stream_index == AudioStreamIndex ? _audio
                    : null;
                if (queue is null) { ffmpeg.av_packet_unref(packet); continue; }
                queue.Enqueue(packet, () => _stop);
                ffmpeg.av_packet_unref(packet);
            }
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
            _video.Complete();
            _audio?.Complete();
            lock (_lifecycle)
            {
                if (!_closed) { _closed = true; Close(_format); }
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        _video.Abandon();
        _audio?.Abandon();
        Thread? reader;
        lock (_lifecycle)
        {
            reader = _reader;
            if (reader is null && !_closed) { _closed = true; Close(_format); }
        }
        // A network read can stay blocked in FFmpeg; the reader closes the context itself when it
        // returns, so a bounded join never risks freeing the context underneath it.
        reader?.Join(750);
        _video.FreeAll();
        _audio?.FreeAll();
    }

    private static void Close(AVFormatContext* format)
    {
        var f = format;
        ffmpeg.avformat_close_input(&f);
    }

    /// <summary>Bounded single-producer/single-consumer queue of owned AVPackets.</summary>
    private sealed class PacketQueue(int capacity)
    {
        private readonly object _sync = new();
        private readonly Queue<IntPtr> _packets = new();
        private bool _completed;
        private bool _abandoned;

        public void Enqueue(AVPacket* source, Func<bool> stopRequested)
        {
            lock (_sync)
            {
                while (_packets.Count >= capacity && !_abandoned && !stopRequested())
                    Monitor.Wait(_sync, 50);
                if (_abandoned || stopRequested()) return;
                var owned = ffmpeg.av_packet_alloc();
                ffmpeg.av_packet_move_ref(owned, source);
                _packets.Enqueue((IntPtr)owned);
                Monitor.PulseAll(_sync);
            }
        }

        public bool Dequeue(AVPacket* destination, CancellationToken ct)
        {
            lock (_sync)
            {
                while (_packets.Count == 0)
                {
                    if (_completed || _abandoned || ct.IsCancellationRequested) return false;
                    Monitor.Wait(_sync, 50);
                }
                var owned = (AVPacket*)_packets.Dequeue();
                ffmpeg.av_packet_move_ref(destination, owned);
                ffmpeg.av_packet_free(&owned);
                Monitor.PulseAll(_sync);
                return true;
            }
        }

        public void Complete() { lock (_sync) { _completed = true; Monitor.PulseAll(_sync); } }

        public void Abandon()
        {
            lock (_sync)
            {
                _abandoned = true;
                FreeQueued();
                Monitor.PulseAll(_sync);
            }
        }

        public void FreeAll() { lock (_sync) FreeQueued(); }

        private void FreeQueued()
        {
            while (_packets.Count > 0)
            {
                var owned = (AVPacket*)_packets.Dequeue();
                ffmpeg.av_packet_free(&owned);
            }
        }
    }
}
