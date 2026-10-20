using System.Diagnostics;
using FFmpeg.AutoGen.Abstractions;
using BroadcastPlayout.Models;

namespace BroadcastPlayout.Ffmpeg;

/// <summary>
/// Converts decoded frames to BGRA directly into a small ring of pinned managed buffers. That
/// removes the per-frame 8 MB Large Object Heap allocation and the extra native-to-managed copy.
/// A slot is only rewritten <see cref="RingDepth"/> frames later, and the Program cadence always
/// composites into its own buffer, so ring memory never reaches an output.
/// </summary>
public sealed unsafe class VideoFrameConverter : IDisposable
{
    public const int RingDepth = 8;

    private readonly SwsContext* _ctx;
    private readonly byte[]?[] _ring = new byte[]?[RingDepth];
    private readonly int _width;
    private readonly int _height;
    private readonly int _stride;
    private readonly int _size;
    private int _next;
    private int _newest = -1;
    private bool _disposed;

    public VideoFrameConverter(int width, int height, AVPixelFormat sourceFormat)
    {
        _width = width; _height = height;
        _ctx = ffmpeg.sws_getContext(width, height, sourceFormat, width, height, AVPixelFormat.AV_PIX_FMT_BGRA,
            (int)SwsFlags.SWS_BILINEAR, null, null, null);
        if (_ctx == null) throw new InvalidOperationException("sws_getContext failed.");
        _stride = checked(width * 4);
        _size = ffmpeg.av_image_get_buffer_size(AVPixelFormat.AV_PIX_FMT_BGRA, width, height, 1).ThrowIfError();
    }

    public VideoFrameData Convert(AVFrame source, double pts, int fpsN, int fpsD)
    {
        // Slots are rented on first use, so a decoder that only grabs a poster frame costs one buffer.
        var target = _ring[_next] ??= VideoFrameBufferPool.Rent(_size);
        _newest = _next;
        _next = (_next + 1) % RingDepth;
        fixed (byte* p = target)
        {
            var dstData = new byte_ptr4();
            var dstLinesize = new int4();
            dstData[0] = p;
            dstLinesize[0] = _stride;
            ffmpeg.sws_scale(_ctx, source.data, source.linesize, 0, source.height, dstData, dstLinesize);
        }
        return new VideoFrameData(target, _width, _height, _stride, pts, fpsN, fpsD);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ctx != null) ffmpeg.sws_freeContext(_ctx);
        // The last emitted frame may still be held after close (engine freeze frame, Program
        // cadence last source after STOP, CG video layer). The engine can decode one frame past
        // an out-point without emitting it, so the two newest slots are left to the GC and only
        // older slots go back to the pool.
        var previous = _newest < 0 ? -1 : (_newest + RingDepth - 1) % RingDepth;
        for (var i = 0; i < _ring.Length; i++)
        {
            if (_ring[i] is { } buffer)
            {
                if (i != _newest && i != previous) VideoFrameBufferPool.Return(buffer);
                else VideoFrameBufferPool.ReportAbandoned(buffer.Length);
            }
            _ring[i] = null;
        }
    }
}

/// <summary>
/// Process-wide pool of pinned BGRA decode buffers shared by all decoders (Program, Preview, CG
/// video layers). Returned buffers are quarantined briefly before reuse so a reader that took a
/// reference just before its decoder closed can finish copying.
/// </summary>
public static class VideoFrameBufferPool
{
    // Idle buffers kept per frame size, bounded by bytes so 4K does not retain 4x the memory of HD.
    private const long MaxIdleBytesPerSize = 128L * 1024 * 1024;
    private static readonly long QuarantineTicks = Stopwatch.Frequency / 2;
    private static readonly object Sync = new();
    private static readonly Dictionary<int, Queue<(byte[] Buffer, long AvailableAt)>> Free = [];
    private static long _allocated;

    public static long AllocatedBuffers => Interlocked.Read(ref _allocated);

    // Buffers left to the GC (a closed decoder's last frames, which may still be on air) are
    // pinned-heap garbage that only a gen2 collection frees. Allocation is now so low that gen2
    // can be minutes apart, so once enough accumulates without a natural gen2, ask for a
    // background (non-blocking) one. This bounds the memory sawtooth.
    private const long ReclaimThresholdBytes = 96L * 1024 * 1024;
    private static long _abandonedBytes;
    private static int _gen2AtLastReclaim;
    public static long ReclaimRequests => Interlocked.Read(ref _reclaims);
    private static long _reclaims;

    public static void ReportAbandoned(long bytes)
    {
        var gen2 = GC.CollectionCount(2);
        if (gen2 != Volatile.Read(ref _gen2AtLastReclaim))
        {
            // A natural gen2 already freed earlier garbage.
            Volatile.Write(ref _gen2AtLastReclaim, gen2);
            Interlocked.Exchange(ref _abandonedBytes, 0);
        }
        if (Interlocked.Add(ref _abandonedBytes, bytes) < ReclaimThresholdBytes) return;
        Interlocked.Exchange(ref _abandonedBytes, 0);
        Interlocked.Increment(ref _reclaims);
        GC.Collect(2, GCCollectionMode.Forced, blocking: false, compacting: false);
        Volatile.Write(ref _gen2AtLastReclaim, GC.CollectionCount(2));
    }

    public static byte[] Rent(int size)
    {
        var now = Stopwatch.GetTimestamp();
        lock (Sync)
        {
            if (Free.TryGetValue(size, out var queue) && queue.Count > 0 && queue.Peek().AvailableAt <= now)
                return queue.Dequeue().Buffer;
        }
        Interlocked.Increment(ref _allocated);
        return GC.AllocateUninitializedArray<byte>(size, pinned: true);
    }

    public static void Return(byte[] buffer)
    {
        var availableAt = Stopwatch.GetTimestamp() + QuarantineTicks;
        lock (Sync)
        {
            if (!Free.TryGetValue(buffer.Length, out var queue)) Free[buffer.Length] = queue = new Queue<(byte[], long)>();
            if (queue.Count >= Math.Max(4, MaxIdleBytesPerSize / Math.Max(1, buffer.Length))) queue.Dequeue();
            queue.Enqueue((buffer, availableAt));
        }
    }
}
