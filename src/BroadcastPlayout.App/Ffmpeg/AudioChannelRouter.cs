namespace BroadcastPlayout.Ffmpeg;

/// <summary>
/// Managed PCM16 channel router used after FFmpeg has normalized decoded audio to 48 kHz
/// interleaved samples. This keeps user-selectable channel remapping independent of the
/// source codec/channel layout and always returns the stereo bus expected by playout outputs.
/// </summary>
public static class AudioChannelRouter
{
    private static string _preset = "Auto Stereo";
    public static string CurrentPreset
    {
        get => Volatile.Read(ref _preset);
        set => Volatile.Write(ref _preset, string.IsNullOrWhiteSpace(value) ? "Auto Stereo" : value.Trim());
    }

    public static byte[] ToStereo(byte[] interleavedPcm16, int channels, string? preset = null)
        => ToStereo(interleavedPcm16, interleavedPcm16.Length, channels, preset);

    /// <summary>Routes only the first <paramref name="length"/> bytes, so callers can pass a pooled scratch buffer.</summary>
    public static byte[] ToStereo(byte[] interleavedPcm16, int length, int channels, string? preset = null)
    {
        length = Math.Clamp(length, 0, interleavedPcm16.Length);
        if (length == 0) return [];
        channels = Math.Max(1, channels);
        preset = string.IsNullOrWhiteSpace(preset) ? CurrentPreset : preset.Trim();
        var frames = length / (channels * sizeof(short));
        if (frames <= 0) return [];
        var output = new byte[frames * 2 * sizeof(short)];

        static short Read(byte[] data, int sampleIndex)
        {
            var offset = sampleIndex * 2;
            if (offset < 0 || offset + 1 >= data.Length) return 0;
            return (short)(data[offset] | (data[offset + 1] << 8));
        }
        static short Clamp(double x) => (short)Math.Clamp((int)Math.Round(x), short.MinValue, short.MaxValue);
        static void Write(byte[] data, int frame, short l, short r)
        {
            var o = frame * 4;
            data[o] = (byte)(l & 0xff); data[o + 1] = (byte)((l >> 8) & 0xff);
            data[o + 2] = (byte)(r & 0xff); data[o + 3] = (byte)((r >> 8) & 0xff);
        }
        int PairStart()
        {
            if (preset.Equals("Ch 3/4", StringComparison.OrdinalIgnoreCase)) return 2;
            if (preset.Equals("Ch 5/6", StringComparison.OrdinalIgnoreCase)) return 4;
            if (preset.Equals("Ch 7/8", StringComparison.OrdinalIgnoreCase)) return 6;
            return 0;
        }

        for (var f = 0; f < frames; f++)
        {
            var baseIndex = f * channels;

            // Per-item broadcast routing presets.  These are applied after FFmpeg has
            // normalized sample rate/format, so they work the same for files, URLs and
            // capture devices.
            if (preset.Equals("Mono to Stereo", StringComparison.OrdinalIgnoreCase) || preset.Equals("Left to Stereo", StringComparison.OrdinalIgnoreCase))
            {
                var mono = Read(interleavedPcm16, baseIndex);
                Write(output, f, mono, mono);
                continue;
            }
            if (preset.Equals("Right to Stereo", StringComparison.OrdinalIgnoreCase))
            {
                var mono = Read(interleavedPcm16, baseIndex + Math.Min(1, channels - 1));
                Write(output, f, mono, mono);
                continue;
            }
            if (preset.Equals("Swap L/R", StringComparison.OrdinalIgnoreCase))
            {
                var l = Read(interleavedPcm16, baseIndex);
                var r = Read(interleavedPcm16, baseIndex + Math.Min(1, channels - 1));
                Write(output, f, r, l);
                continue;
            }
            if (preset.Equals("Stereo", StringComparison.OrdinalIgnoreCase))
            {
                var l = Read(interleavedPcm16, baseIndex);
                var r = Read(interleavedPcm16, baseIndex + Math.Min(1, channels - 1));
                Write(output, f, l, r);
                continue;
            }
            if (preset.Equals("Mono Sum", StringComparison.OrdinalIgnoreCase))
            {
                double sum = 0;
                for (var c = 0; c < channels; c++) sum += Read(interleavedPcm16, baseIndex + c);
                var mono = Clamp(sum / channels);
                Write(output, f, mono, mono);
                continue;
            }

            if (preset.StartsWith("Ch ", StringComparison.OrdinalIgnoreCase) || preset.Equals("Front L/R Only", StringComparison.OrdinalIgnoreCase))
            {
                var start = preset.Equals("Front L/R Only", StringComparison.OrdinalIgnoreCase) ? 0 : PairStart();
                var l = Read(interleavedPcm16, baseIndex + Math.Min(start, channels - 1));
                var r = Read(interleavedPcm16, baseIndex + Math.Min(start + 1, channels - 1));
                Write(output, f, l, r);
                continue;
            }

            if (channels == 1)
            {
                var mono = Read(interleavedPcm16, baseIndex);
                Write(output, f, mono, mono);
                continue;
            }
            if (channels == 2)
            {
                Write(output, f, Read(interleavedPcm16, baseIndex), Read(interleavedPcm16, baseIndex + 1));
                continue;
            }

            // FFmpeg default channel order for common layouts:
            // 5.1: FL FR FC LFE BL BR, 7.1: FL FR FC LFE BL BR SL SR.
            // Center/surround are mixed at -3 dB; LFE at -6 dB.
            var fl = Read(interleavedPcm16, baseIndex);
            var fr = Read(interleavedPcm16, baseIndex + Math.Min(1, channels - 1));
            var fc = channels > 2 ? Read(interleavedPcm16, baseIndex + 2) : 0;
            var lfe = channels > 3 ? Read(interleavedPcm16, baseIndex + 3) : 0;
            var bl = channels > 4 ? Read(interleavedPcm16, baseIndex + 4) : 0;
            var br = channels > 5 ? Read(interleavedPcm16, baseIndex + 5) : 0;
            var sl = channels > 6 ? Read(interleavedPcm16, baseIndex + 6) : 0;
            var sr = channels > 7 ? Read(interleavedPcm16, baseIndex + 7) : 0;
            var left = fl + fc * .70710678 + lfe * .5 + bl * .70710678 + sl * .70710678;
            var right = fr + fc * .70710678 + lfe * .5 + br * .70710678 + sr * .70710678;
            // Conservative normalization prevents clipping on dense 5.1/7.1 mixes.
            var norm = channels >= 6 ? .55 : .70;
            Write(output, f, Clamp(left * norm), Clamp(right * norm));
        }
        return output;
    }
}
