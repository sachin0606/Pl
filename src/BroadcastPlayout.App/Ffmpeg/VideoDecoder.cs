using FFmpeg.AutoGen.Abstractions;
using BroadcastPlayout.Models;
using BroadcastPlayout.Outputs;
using BroadcastPlayout.Services;

namespace BroadcastPlayout.Ffmpeg;

/// <summary>
/// FFmpeg video decoder with a broadcast-safe hardware acceleration policy.
/// CPU mode always uses the software decoder. Auto/GPU/Hybrid first ask FFmpeg for a
/// Windows hardware device (D3D11VA -> DXVA2 -> QSV -> CUDA) and transparently fall back
/// to the normal software decoder when a device or codec does not support the request.
/// Hardware surfaces are transferred to system memory only once, immediately before the
/// existing BGRA compositor/output pipeline, so the rest of Kashtrix remains deterministic.
/// </summary>
public sealed unsafe class VideoDecoder : IDisposable
{
    private readonly AVFormatContext* _format;
    private readonly AVCodecContext* _codec;
    private readonly AVFrame* _frame;
    private readonly AVFrame* _softwareFrame;
    private readonly AVPacket* _packet;
    private readonly int _streamIndex;
    private readonly AVRational _timeBase;
    private readonly AVRational _frameRate;
    private VideoFrameConverter? _converter;
    private int _converterWidth;
    private int _converterHeight;
    private AVPixelFormat _converterFormat = AVPixelFormat.AV_PIX_FMT_NONE;
    private readonly NdiVideoReceiver? _ndi;
    private readonly PlaylistItem _item;
    private readonly BgraFrameScaler _itemScaler = new();
    private readonly SharedDemuxer? _shared;
    private readonly CancellationTokenSource _readCancel = new();
    private bool _inputEof;
    private bool _flushSent;
    private bool _hardwareFrameSeen;
    private readonly int _maxOutputWidth;

    public string DecoderBackend { get; private set; } = "CPU";
    public bool HardwareAccelerationActive => _hardwareFrameSeen;

    public VideoDecoder(string file) : this(new PlaylistItem { FilePath = file, SourceKind = "File" }) { }

    public VideoDecoder(PlaylistItem item) : this(item, null) { }

    /// <param name="shared">When set, packets come from the segment's shared input instead of a second open.</param>
    public VideoDecoder(PlaylistItem item, SharedDemuxer? shared) : this(item, shared, 0) { }

    /// <param name="maxOutputWidth">
    /// Above 0 the decoder works as a light preview: frames are scaled down to at most this width
    /// during colour conversion, and the codec uses two threads without the loop filter.
    /// </param>
    public VideoDecoder(PlaylistItem item, SharedDemuxer? shared, int maxOutputWidth)
    {
        _item = item ?? throw new ArgumentNullException(nameof(item));
        _maxOutputWidth = Math.Max(0, maxOutputWidth);
        if (item.SourceKind.Equals("NDI", StringComparison.OrdinalIgnoreCase))
        {
            _ndi = new NdiVideoReceiver(item.FilePath);
            DecoderBackend = "NDI native";
            return;
        }

        FfmpegRuntime.EnsureReady();
        _shared = shared;

        AVCodec* decoder = null;
        if (shared is null)
        {
            _format = FfmpegInput.Open(item, AVMediaType.AVMEDIA_TYPE_VIDEO);
            ffmpeg.avformat_find_stream_info(_format, null).ThrowIfError();
            _streamIndex = ffmpeg.av_find_best_stream(
                _format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &decoder, 0).ThrowIfError();
        }
        else
        {
            _format = shared.Format;
            _streamIndex = shared.VideoStreamIndex;
            decoder = ffmpeg.avcodec_find_decoder(_format->streams[_streamIndex]->codecpar->codec_id);
        }
        if (decoder == null) throw new InvalidOperationException("No video decoder was found.");

        var codec = CreateCodecContext(decoder, item, allowHardware: WantsHardware(item.DecoderPreference), out var requestedBackend);
        var open = ffmpeg.avcodec_open2(codec, decoder, null);
        if (open < 0 && codec->hw_device_ctx != null)
        {
            // A GPU can exist while the selected codec/profile is unsupported by that device.
            // Rebuild a clean software context rather than failing the on-air item.
            var failed = codec;
            ffmpeg.avcodec_free_context(&failed);
            codec = CreateCodecContext(decoder, item, allowHardware: false, out _);
            ffmpeg.avcodec_open2(codec, decoder, null).ThrowIfError();
            requestedBackend = "CPU fallback";
        }
        else
        {
            open.ThrowIfError();
        }
        _codec = codec;
        DecoderBackend = requestedBackend;

        _timeBase = _format->streams[_streamIndex]->time_base;
        _frameRate = ffmpeg.av_guess_frame_rate(_format, _format->streams[_streamIndex], null);
        _packet = ffmpeg.av_packet_alloc();
        _frame = ffmpeg.av_frame_alloc();
        _softwareFrame = ffmpeg.av_frame_alloc();
        if (_packet == null || _frame == null || _softwareFrame == null)
            throw new OutOfMemoryException("FFmpeg frame/packet allocation failed.");
    }

    private AVCodecContext* CreateCodecContext(AVCodec* decoder, PlaylistItem item, bool allowHardware, out string backend)
    {
        var codec = ffmpeg.avcodec_alloc_context3(decoder);
        if (codec == null) throw new InvalidOperationException("avcodec_alloc_context3 failed.");
        try
        {
            ffmpeg.avcodec_parameters_to_context(codec, _format->streams[_streamIndex]->codecpar).ThrowIfError();

            // FFmpeg's frame threading is considerably cheaper than opening multiple software
            // decoder instances. GPU modes keep software threads conservative so compositing,
            // audio and output workers retain CPU headroom.
            var pref = (item.DecoderPreference ?? "Auto (CPU+GPU)").Trim();
            codec->thread_count = pref.Equals("CPU", StringComparison.OrdinalIgnoreCase)
                ? 0
                : Math.Clamp(Environment.ProcessorCount / 4, 1, 4);
            if (_maxOutputWidth > 0)
            {
                // A thumbnail-sized preview must stay cheap next to the Program decoder.
                codec->thread_count = 2;
                codec->skip_loop_filter = AVDiscard.AVDISCARD_ALL;
            }

            if (allowHardware && TryAttachHardwareDevice(codec, decoder, pref, out backend))
                return codec;

            backend = pref.Equals("CPU", StringComparison.OrdinalIgnoreCase) ? "CPU" : "CPU fallback";
            return codec;
        }
        catch
        {
            ffmpeg.avcodec_free_context(&codec);
            throw;
        }
    }

    private static bool WantsHardware(string? preference)
        => !string.Equals(preference?.Trim(), "CPU", StringComparison.OrdinalIgnoreCase);

    private static bool TryAttachHardwareDevice(AVCodecContext* codec, AVCodec* decoder, string preference, out string backend)
    {
        backend = "CPU fallback";
        if (!OperatingSystem.IsWindows()) return false;

        // D3D11VA is the preferred zero-copy Windows decode backend. DXVA2 covers older GPUs;
        // QSV/CUDA give vendor-specific fallback choices when present in the FFmpeg build.
        var devices = new[]
        {
            (AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, "GPU D3D11VA"),
            (AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2, "GPU DXVA2"),
            (AVHWDeviceType.AV_HWDEVICE_TYPE_QSV, "GPU Intel QSV"),
            (AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA, "GPU NVIDIA CUDA")
        };

        foreach (var (type, name) in devices)
        {
            if (!CodecSupportsDevice(decoder, type)) continue;
            codec->hw_device_ctx = null;
            var result = ffmpeg.av_hwdevice_ctx_create(&codec->hw_device_ctx, type, null, null, 0);
            if (result >= 0 && codec->hw_device_ctx != null)
            {
                backend = preference.Equals("CPU+GPU Hybrid", StringComparison.OrdinalIgnoreCase)
                    ? $"CPU+GPU Hybrid · {name}"
                    : name;
                return true;
            }
            if (codec->hw_device_ctx != null)
            {
                var failedDevice = codec->hw_device_ctx;
                ffmpeg.av_buffer_unref(&failedDevice);
                codec->hw_device_ctx = null;
            }
        }
        return false;
    }

    private static bool CodecSupportsDevice(AVCodec* decoder, AVHWDeviceType type)
    {
        for (var index = 0; index < 32; index++)
        {
            var config = ffmpeg.avcodec_get_hw_config(decoder, index);
            if (config == null) return false;
            if (config->device_type == type) return true;
        }
        return false;
    }

    public void Seek(TimeSpan time)
    {
        if (_ndi is not null) return;
        // With a shared input the demuxer seeks once for both streams; only the codec is reset here.
        if (_shared is null)
        {
            var seconds = Math.Max(0, time.TotalSeconds);
            var tb = ffmpeg.av_q2d(_timeBase);
            var ts = tb > 0 ? (long)(seconds / tb) : 0;
            ffmpeg.av_seek_frame(_format, _streamIndex, ts, ffmpeg.AVSEEK_FLAG_BACKWARD).ThrowIfError();
        }
        ffmpeg.avcodec_flush_buffers(_codec);
        _inputEof = false;
        _flushSent = false;
        ffmpeg.av_packet_unref(_packet);
        ffmpeg.av_frame_unref(_frame);
        ffmpeg.av_frame_unref(_softwareFrame);
    }

    public bool TryRead(out VideoFrameData frame)
    {
        if (_ndi is not null)
        {
            if (!_ndi.TryRead(out var ndiFrame)) { frame = null!; return false; }
            frame = ItemVideoProcessor.Apply(ndiFrame, _item, _itemScaler);
            return true;
        }
        while (true)
        {
            ffmpeg.av_frame_unref(_frame);
            var receive = ffmpeg.avcodec_receive_frame(_codec, _frame);
            if (receive >= 0)
            {
                var decodedFrame = _frame;
                if (_frame->hw_frames_ctx != null)
                {
                    ffmpeg.av_frame_unref(_softwareFrame);
                    var transfer = ffmpeg.av_hwframe_transfer_data(_softwareFrame, _frame, 0);
                    if (transfer >= 0)
                    {
                        decodedFrame = _softwareFrame;
                        _hardwareFrameSeen = true;
                    }
                    else
                    {
                        // Do not crash air because a driver rejected one transfer. The next item
                        // will reopen through the normal CPU fallback policy if necessary.
                        transfer.ThrowIfError();
                    }
                }

                var ts = _frame->best_effort_timestamp;
                var pts = ts == ffmpeg.AV_NOPTS_VALUE ? 0.0 : ts * ffmpeg.av_q2d(_timeBase);
                var n = _frameRate.num > 0 ? _frameRate.num : 25;
                var d = _frameRate.den > 0 ? _frameRate.den : 1;
                var sourceFormat = (AVPixelFormat)decodedFrame->format;
                EnsureConverter(decodedFrame->width, decodedFrame->height, sourceFormat);
                var decoded = _converter!.Convert(*decodedFrame, pts, n, d);
                frame = ItemVideoProcessor.Apply(decoded, _item, _itemScaler);
                return true;
            }

            if (receive == ffmpeg.AVERROR_EOF)
            {
                frame = null!;
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

                frame = null!;
                return false;
            }

            while (true)
            {
                ffmpeg.av_packet_unref(_packet);
                var read = _shared is null
                    ? ffmpeg.av_read_frame(_format, _packet)
                    : _shared.ReadVideo(_packet, _readCancel.Token) ? 0 : ffmpeg.AVERROR_EOF;
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

    private void EnsureConverter(int width, int height, AVPixelFormat sourceFormat)
    {
        if (_converter is not null && _converterWidth == width && _converterHeight == height && _converterFormat == sourceFormat)
            return;
        _converter?.Dispose();
        if (_maxOutputWidth > 0 && width > _maxOutputWidth)
        {
            // Keep the aspect ratio; even sizes keep chroma-subsampled sources clean.
            var outWidth = _maxOutputWidth & ~1;
            var outHeight = Math.Max(2, (int)Math.Round(height * (outWidth / (double)width)) & ~1);
            _converter = new VideoFrameConverter(width, height, sourceFormat, outWidth, outHeight);
        }
        else
        {
            _converter = new VideoFrameConverter(width, height, sourceFormat);
        }
        _converterWidth = width;
        _converterHeight = height;
        _converterFormat = sourceFormat;
    }

    public void Dispose()
    {
        if (_ndi is not null) { _ndi.Dispose(); _itemScaler.Dispose(); return; }
        _readCancel.Cancel();
        _shared?.ReleaseVideo();
        _converter?.Dispose();
        _itemScaler.Dispose();
        var swFrame = _softwareFrame;
        ffmpeg.av_frame_free(&swFrame);
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
