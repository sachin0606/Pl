using FFmpeg.AutoGen.Abstractions;
using BroadcastPlayout.Models;

namespace BroadcastPlayout.Ffmpeg;

public sealed unsafe class AudioDecoder : IDisposable
{
    private readonly AVFormatContext* _format;
    private readonly AVCodecContext* _codec;
    private readonly AVFrame* _frame;
    private readonly AVPacket* _packet;
    private readonly SwrContext* _swr;
    private readonly int _streamIndex;
    private readonly AVRational _timeBase;
    private bool _inputEof;
    private bool _flushSent;
    private const int OutRate = 48000;
    private readonly int _outChannels;
    private readonly PlaylistItem _item;
    private readonly SharedDemuxer? _shared;
    private readonly CancellationTokenSource _readCancel = new();
    private byte[] _scratch = [];

    public AudioDecoder(string file) : this(new PlaylistItem { FilePath = file, SourceKind = "File" }) { }

    public AudioDecoder(PlaylistItem item) : this(item, null) { }

    /// <param name="shared">When set, packets come from the segment's shared input instead of a second open.</param>
    public AudioDecoder(PlaylistItem item, SharedDemuxer? shared)
    {
        FfmpegRuntime.EnsureReady();
        _item = item;
        _shared = shared;

        AVCodec* decoder = null;
        if (shared is null)
        {
            _format = FfmpegInput.Open(item, AVMediaType.AVMEDIA_TYPE_AUDIO);
            ffmpeg.avformat_find_stream_info(_format, null).ThrowIfError();
            _streamIndex = FindAudioStream(_format, item, out decoder).ThrowIfError();
        }
        else
        {
            _format = shared.Format;
            _streamIndex = shared.AudioStreamIndex;
            if (_streamIndex < 0) throw new InvalidOperationException("No audio decoder was found.");
            decoder = ffmpeg.avcodec_find_decoder(_format->streams[_streamIndex]->codecpar->codec_id);
        }

        if (decoder == null) throw new InvalidOperationException(item.AudioTrackIndex >= 0
            ? $"Audio track {item.AudioTrackIndex} does not have a supported decoder."
            : "No audio decoder was found.");

        _codec = ffmpeg.avcodec_alloc_context3(decoder);
        if (_codec == null) throw new InvalidOperationException("avcodec_alloc_context3 failed.");
        ffmpeg.avcodec_parameters_to_context(_codec, _format->streams[_streamIndex]->codecpar).ThrowIfError();
        ffmpeg.avcodec_open2(_codec, decoder, null).ThrowIfError();
        _timeBase = _format->streams[_streamIndex]->time_base;

        _packet = ffmpeg.av_packet_alloc();
        _frame = ffmpeg.av_frame_alloc();
        if (_packet == null || _frame == null) throw new OutOfMemoryException("FFmpeg frame/packet allocation failed.");

        var inputLayout = _codec->ch_layout;
        if (inputLayout.nb_channels <= 0)
            ffmpeg.av_channel_layout_default(&inputLayout, 2);
        _outChannels = Math.Clamp(inputLayout.nb_channels, 1, 8);
        AVChannelLayout outputLayout = default;
        ffmpeg.av_channel_layout_default(&outputLayout, _outChannels);

        SwrContext* swr = null;
        ffmpeg.swr_alloc_set_opts2(
            &swr,
            &outputLayout, AVSampleFormat.AV_SAMPLE_FMT_S16, OutRate,
            &inputLayout, _codec->sample_fmt, _codec->sample_rate,
            0, null).ThrowIfError();
        if (swr == null) throw new InvalidOperationException("swr_alloc_set_opts2 failed.");
        ffmpeg.swr_init(swr).ThrowIfError();
        _swr = swr;
        ffmpeg.av_channel_layout_uninit(&outputLayout);
    }

    /// <summary>The operator-selected audio track (ordinal among audio streams) or the best audio stream; negative when none.</summary>
    internal static int FindAudioStream(AVFormatContext* format, PlaylistItem item, out AVCodec* decoder)
    {
        decoder = null;
        if (item.AudioTrackIndex >= 0)
        {
            var ordinal = 0;
            for (var i = 0; i < (int)format->nb_streams; i++)
            {
                var parameters = format->streams[i]->codecpar;
                if (parameters == null || parameters->codec_type != AVMediaType.AVMEDIA_TYPE_AUDIO) continue;
                if (ordinal++ != item.AudioTrackIndex) continue;
                decoder = ffmpeg.avcodec_find_decoder(parameters->codec_id);
                return i;
            }
        }
        AVCodec* best = null;
        var stream = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, &best, 0);
        decoder = best;
        return stream;
    }

    public void Seek(TimeSpan time)
    {
        // With a shared input the demuxer seeks once for both streams; only the codec is reset here.
        if (_shared is null)
        {
            var tb = ffmpeg.av_q2d(_timeBase);
            var ts = tb > 0 ? (long)(Math.Max(0, time.TotalSeconds) / tb) : 0;
            ffmpeg.av_seek_frame(_format, _streamIndex, ts, ffmpeg.AVSEEK_FLAG_BACKWARD).ThrowIfError();
        }
        ffmpeg.avcodec_flush_buffers(_codec);
        ffmpeg.swr_close(_swr);
        ffmpeg.swr_init(_swr).ThrowIfError();
        _inputEof = false;
        _flushSent = false;
        ffmpeg.av_packet_unref(_packet);
        ffmpeg.av_frame_unref(_frame);
    }

    public bool TryRead(out AudioChunk chunk)
    {
        while (true)
        {
            // Drain every buffered decoded audio frame before feeding another packet.
            ffmpeg.av_frame_unref(_frame);
            var receive = ffmpeg.avcodec_receive_frame(_codec, _frame);
            if (receive >= 0)
            {
                var expected = ffmpeg.swr_get_out_samples(_swr, _frame->nb_samples);
                var maxSamples = Math.Max(_frame->nb_samples * 4, Math.Max(expected, 1));
                var scratchLength = maxSamples * _outChannels * sizeof(short);
                if (_scratch.Length < scratchLength) _scratch = new byte[scratchLength];
                int converted;
                fixed (byte* p = _scratch)
                {
                    byte** output = stackalloc byte*[1];
                    output[0] = p;
                    converted = ffmpeg.swr_convert(
                        _swr, output, maxSamples,
                        _frame->extended_data, _frame->nb_samples).ThrowIfError();
                }

                // The router writes the exact-size stereo chunk; the resampler scratch is reused.
                var bytes = AudioChannelRouter.ToStereo(_scratch, converted * _outChannels * sizeof(short), _outChannels, string.IsNullOrWhiteSpace(_item.AudioChannelMode) ? "Auto Stereo" : _item.AudioChannelMode);
                var ts = _frame->best_effort_timestamp;
                var pts = ts == ffmpeg.AV_NOPTS_VALUE ? 0.0 : ts * ffmpeg.av_q2d(_timeBase);
                chunk = new AudioChunk(bytes, pts);
                return true;
            }

            if (receive == ffmpeg.AVERROR_EOF)
            {
                chunk = null!;
                return false;
            }

            if (receive != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                receive.ThrowIfError();

            if (_inputEof)
            {
                if (!_flushSent)
                {
                    var flush = ffmpeg.avcodec_send_packet(_codec, null);
                    if (flush != ffmpeg.AVERROR_EOF)
                        flush.ThrowIfError();
                    _flushSent = true;
                    continue;
                }

                chunk = null!;
                return false;
            }

            while (true)
            {
                ffmpeg.av_packet_unref(_packet);
                var read = _shared is null
                    ? ffmpeg.av_read_frame(_format, _packet)
                    : _shared.ReadAudio(_packet, _readCancel.Token) ? 0 : ffmpeg.AVERROR_EOF;
                if (read == ffmpeg.AVERROR_EOF)
                {
                    _inputEof = true;
                    break;
                }
                read.ThrowIfError();

                if (_packet->stream_index != _streamIndex)
                    continue;

                var send = ffmpeg.avcodec_send_packet(_codec, _packet);
                ffmpeg.av_packet_unref(_packet);
                if (send == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    break;
                send.ThrowIfError();
                break;
            }
        }
    }

    public void Dispose()
    {
        _readCancel.Cancel();
        _shared?.ReleaseAudio();
        var swr = _swr;
        ffmpeg.swr_free(&swr);
        var frame = _frame;
        ffmpeg.av_frame_free(&frame);
        var packet = _packet;
        ffmpeg.av_packet_free(&packet);
        var codec = _codec;
        ffmpeg.avcodec_free_context(&codec);
        if (_shared is null)
        {
            var format = _format;
            ffmpeg.avformat_close_input(&format);
        }
    }
}
