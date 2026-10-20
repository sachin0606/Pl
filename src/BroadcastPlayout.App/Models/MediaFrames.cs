namespace BroadcastPlayout.Models;

public sealed record VideoFrameData(byte[] Bgra, int Width, int Height, int Stride, double PtsSeconds, int FrameRateNumerator, int FrameRateDenominator)
{
    public double FramesPerSecond => FrameRateDenominator == 0 ? 0d : (double)FrameRateNumerator / FrameRateDenominator;

    /// <summary>Stopwatch timestamp at which the playout engine scheduled this frame to be shown; 0 when unscheduled.</summary>
    public long DueTimestamp { get; init; }
}

/// <param name="Pcm16Stereo48k">Interleaved PCM16 samples.</param>
public sealed record AudioChunk(byte[] Pcm16Stereo48k, double PtsSeconds, int SampleRate = 48000, int Channels = 2, int BitsPerSample = 16)
{
    /// <summary>Stopwatch timestamp at which the first sample is due on the Program timeline; 0 when unscheduled.</summary>
    public long DueTimestamp { get; init; }
}

public sealed record MediaInfo(TimeSpan Duration, int Width, int Height, double FramesPerSecond, string VideoCodec, bool HasAudio);
