using BroadcastPlayout.Models;

namespace BroadcastPlayout.Services;

public static class AudioMasterProcessor
{
    public static AudioChunk Apply(AudioChunk input, int volumePercent, bool muted, bool enabled = true)
    {
        // Unity gain is a pass-through. Faders go to 200%, so gains above 100% must be applied too.
        if (!enabled || (!muted && volumePercent == 100)) return input;
        var gain = muted ? 0.0 : Math.Clamp(volumePercent, 0, 200) / 100.0;
        var source = input.Pcm16Stereo48k;
        var output = new byte[source.Length];
        for (var i = 0; i + 1 < source.Length; i += 2)
        {
            var sample = BitConverter.ToInt16(source, i);
            var scaled = (int)Math.Round(sample * gain);
            var clipped = (short)Math.Clamp(scaled, short.MinValue, short.MaxValue);
            output[i] = (byte)(clipped & 0xFF);
            output[i + 1] = (byte)((clipped >> 8) & 0xFF);
        }
        return input with { Pcm16Stereo48k = output };
    }
}
