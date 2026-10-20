using System.Diagnostics;
using BroadcastPlayout.Models;

namespace BroadcastPlayout.Engine;

/// <summary>
/// Program render-time model. The cadence renders reference time R = tick deadline minus a fixed
/// pipeline latency; video and audio are both selected by R, so picture and sound leave the
/// cadence together regardless of how each decoder was scheduled.
/// </summary>
public static class ProgramAvTiming
{
    public const int SampleRate = 48000;

    /// <summary>Render latency: one channel frame plus a fixed margin for decode/emit jitter.</summary>
    public static long LatencyTicks(double channelFps)
        => (long)((1.0 / Math.Clamp(channelFps, 1, 120) + 0.030) * Stopwatch.Frequency);

    /// <summary>
    /// Video is chosen as the frame nearest the middle of the audio block that starts at the
    /// reference time (floor selection would show pictures half a frame older than their sound).
    /// </summary>
    public static long VideoSelectionTicks(long referenceTicks, double channelFps)
        => referenceTicks + (long)(0.5 / Math.Clamp(channelFps, 1, 120) * Stopwatch.Frequency);

    /// <summary>Samples in channel frame <paramref name="frameIndex"/> for an exact rational rate (59.94p alternates 801/800).</summary>
    public static int SamplesForFrame(long frameIndex, int rateNumerator, int rateDenominator)
    {
        var n = Math.Max(1, rateNumerator);
        var d = Math.Max(1, rateDenominator);
        return (int)((frameIndex + 1) * SampleRate * d / n - frameIndex * SampleRate * d / n);
    }

    /// <summary>Exact Stopwatch-tick to 48 kHz sample conversion without double precision loss.</summary>
    public static long TicksToSample(long ticks)
    {
        var frequency = Stopwatch.Frequency;
        return ticks / frequency * SampleRate + ticks % frequency * SampleRate / frequency;
    }
}

/// <summary>
/// Recently emitted Program source frames. The cadence shows the newest frame that is due by its
/// reference time and keeps showing it (freeze) until a newer one becomes due.
/// </summary>
public sealed class ProgramVideoTimeline
{
    private readonly object _sync = new();
    private readonly VideoFrameData?[] _frames = new VideoFrameData?[6];
    private int _next;
    private VideoFrameData? _shown;

    public void Push(VideoFrameData frame)
    {
        // Frames without a schedule (CG-event holds, external sources) are due when they arrive.
        if (frame.DueTimestamp == 0) frame = frame with { DueTimestamp = Stopwatch.GetTimestamp() };
        lock (_sync)
        {
            _frames[_next] = frame;
            _next = (_next + 1) % _frames.Length;
        }
    }

    public VideoFrameData? Select(long referenceTicks)
    {
        lock (_sync)
        {
            VideoFrameData? best = null;
            foreach (var frame in _frames)
            {
                if (frame is null || frame.DueTimestamp > referenceTicks) continue;
                if (best is null || frame.DueTimestamp >= best.DueTimestamp) best = frame;
            }
            // Never step backwards: an older frame can only win if the shown one left the window.
            if (best is not null && (_shown is null || best.DueTimestamp >= _shown.DueTimestamp || !Contains(_shown)))
                _shown = best;
            return _shown;
        }
    }

    private bool Contains(VideoFrameData frame)
    {
        foreach (var f in _frames) if (ReferenceEquals(f, frame)) return true;
        return false;
    }

    public void Clear()
    {
        lock (_sync)
        {
            Array.Clear(_frames);
            _next = 0;
            _shown = null;
        }
    }
}

/// <summary>
/// Timestamp-indexed Program audio FIFO (48 kHz stereo PCM16). Chunks are placed by their due time;
/// the cadence reads exactly one channel frame of samples per tick from a continuous cursor that is
/// re-aligned to the reference time only when it drifts, so normal playback is sample-continuous.
/// </summary>
public sealed class ProgramAudioClock
{
    private const int BytesPerFrame = 4;
    private const long MaxQueuedSamples = ProgramAvTiming.SampleRate * 4;
    private const long ResyncThresholdSamples = ProgramAvTiming.SampleRate * 40 / 1000;

    private readonly object _sync = new();
    private readonly LinkedList<(long Start, byte[] Pcm, int Frames)> _chunks = new();
    private long _queuedSamples;
    private long _lastEnd = long.MinValue;
    private long _cursor = long.MinValue;
    private long _underrunSamples;
    private long _resyncs;

    public long UnderrunSamples => Interlocked.Read(ref _underrunSamples);
    public long Resyncs => Interlocked.Read(ref _resyncs);

    public void Add(AudioChunk chunk)
    {
        if (chunk.Channels != 2 || chunk.BitsPerSample != 16 || chunk.SampleRate != ProgramAvTiming.SampleRate) return;
        var frames = chunk.Pcm16Stereo48k.Length / BytesPerFrame;
        if (frames <= 0) return;
        var due = chunk.DueTimestamp != 0 ? chunk.DueTimestamp : Stopwatch.GetTimestamp();
        var start = ProgramAvTiming.TicksToSample(due);
        lock (_sync)
        {
            // Timestamps rounded to samples can differ by one; snap so continuous audio stays gap-free.
            if (_lastEnd != long.MinValue && Math.Abs(start - _lastEnd) <= 2) start = _lastEnd;
            _chunks.AddLast((start, chunk.Pcm16Stereo48k, frames));
            _queuedSamples += frames;
            _lastEnd = start + frames;
            while (_queuedSamples > MaxQueuedSamples && _chunks.First is { } oldest)
            {
                _queuedSamples -= oldest.Value.Frames;
                _chunks.RemoveFirst();
            }
        }
    }

    /// <summary>Returns exactly <paramref name="frames"/> stereo samples for the block that starts at the reference time.</summary>
    public byte[] Read(long referenceTicks, int frames)
    {
        var output = new byte[Math.Max(0, frames) * BytesPerFrame];
        if (frames <= 0) return output;
        var target = ProgramAvTiming.TicksToSample(referenceTicks);
        lock (_sync)
        {
            if (_cursor == long.MinValue || Math.Abs(_cursor - target) > ResyncThresholdSamples)
            {
                if (_cursor != long.MinValue) Interlocked.Increment(ref _resyncs);
                _cursor = target;
            }
            var blockStart = _cursor;
            var blockEnd = blockStart + frames;
            long covered = 0;
            var node = _chunks.First;
            while (node is not null)
            {
                var next = node.Next;
                var (start, pcm, count) = node.Value;
                var end = start + count;
                if (end <= blockStart)
                {
                    _queuedSamples -= count;
                    _chunks.Remove(node);
                }
                else if (start >= blockEnd)
                {
                    break;
                }
                else
                {
                    var from = Math.Max(start, blockStart);
                    var to = Math.Min(end, blockEnd);
                    Buffer.BlockCopy(pcm, (int)(from - start) * BytesPerFrame, output, (int)(from - blockStart) * BytesPerFrame, (int)(to - from) * BytesPerFrame);
                    covered += to - from;
                }
                node = next;
            }
            _cursor = blockEnd;
            if (covered < frames && _chunks.Count > 0) Interlocked.Add(ref _underrunSamples, frames - covered);
        }
        return output;
    }

    public void Clear()
    {
        lock (_sync)
        {
            _chunks.Clear();
            _queuedSamples = 0;
            _lastEnd = long.MinValue;
            _cursor = long.MinValue;
        }
    }
}
