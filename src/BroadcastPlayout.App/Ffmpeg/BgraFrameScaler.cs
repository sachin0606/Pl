using BroadcastPlayout.Models;
using FFmpeg.AutoGen.Abstractions;

namespace BroadcastPlayout.Ffmpeg;

/// <summary>
/// Reusable BGRA-to-BGRA scaler for output adapters. One instance must be used from one thread at a time.
/// </summary>
public sealed unsafe class BgraFrameScaler : IDisposable
{
    private SwsContext* _ctx;
    private int _srcW, _srcH, _dstW, _dstH;
    private byte[]? _dst;
    private int _dstStride;

    /// <summary>Scales into a new buffer the caller owns. Returns the source unchanged when no scaling is needed.</summary>
    public VideoFrameData Scale(VideoFrameData source, int targetWidth, int targetHeight)
    {
        var scaled = ScaleReusable(source, targetWidth, targetHeight);
        if (ReferenceEquals(scaled, source)) return source;
        return scaled with { Bgra = (byte[])scaled.Bgra.Clone() };
    }

    /// <summary>
    /// Scales into this scaler's own buffer, which stays valid only until the next call on this
    /// instance. Use it when the result is consumed immediately (sent, copied or drawn) so output
    /// adapters do not allocate a full frame per call.
    /// </summary>
    public VideoFrameData ScaleReusable(VideoFrameData source, int targetWidth, int targetHeight)
    {
        ArgumentNullException.ThrowIfNull(source);
        targetWidth = Math.Max(16, targetWidth);
        targetHeight = Math.Max(16, targetHeight);

        if (source.Width == targetWidth && source.Height == targetHeight)
            return source;

        EnsureContext(source.Width, source.Height, targetWidth, targetHeight);

        fixed (byte* sourcePtr = source.Bgra)
        fixed (byte* destinationPtr = _dst)
        {
            var srcData = new byte_ptr8();
            var srcStride = new int8();
            srcData[0] = sourcePtr;
            srcStride[0] = source.Stride;
            var dstData = new byte_ptr4();
            var dstStride = new int4();
            dstData[0] = destinationPtr;
            dstStride[0] = _dstStride;

            var scaled = ffmpeg.sws_scale(
                _ctx,
                srcData,
                srcStride,
                0,
                source.Height,
                dstData,
                dstStride);

            if (scaled <= 0)
                throw new InvalidOperationException("FFmpeg sws_scale failed while resizing the output frame.");
        }

        return new VideoFrameData(
            _dst!,
            targetWidth,
            targetHeight,
            _dstStride,
            source.PtsSeconds,
            source.FrameRateNumerator,
            source.FrameRateDenominator);
    }

    private void EnsureContext(int srcW, int srcH, int dstW, int dstH)
    {
        FfmpegRuntime.EnsureReady();
        if (_ctx != null && _srcW == srcW && _srcH == srcH && _dstW == dstW && _dstH == dstH)
            return;

        ReleaseContext();

        _ctx = ffmpeg.sws_getContext(
            srcW, srcH, AVPixelFormat.AV_PIX_FMT_BGRA,
            dstW, dstH, AVPixelFormat.AV_PIX_FMT_BGRA,
            (int)SwsFlags.SWS_BILINEAR,
            null, null, null);

        if (_ctx == null)
            throw new InvalidOperationException("FFmpeg sws_getContext failed for output scaling.");

        _dstStride = checked(dstW * 4);
        var size = ffmpeg.av_image_get_buffer_size(AVPixelFormat.AV_PIX_FMT_BGRA, dstW, dstH, 1).ThrowIfError();
        _dst = GC.AllocateUninitializedArray<byte>(size, pinned: true);

        _srcW = srcW;
        _srcH = srcH;
        _dstW = dstW;
        _dstH = dstH;
    }

    private void ReleaseContext()
    {
        if (_ctx != null)
        {
            ffmpeg.sws_freeContext(_ctx);
            _ctx = null;
        }

        _dst = null;
        _dstStride = 0;
        _srcW = _srcH = _dstW = _dstH = 0;
    }

    public void Dispose() => ReleaseContext();
}
