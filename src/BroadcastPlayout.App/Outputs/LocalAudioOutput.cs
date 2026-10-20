using NAudio.Wave;
using BroadcastPlayout.Models;

namespace BroadcastPlayout.Outputs;

public sealed record AudioDeviceOption(int DeviceNumber, string Name)
{
    public string Label => DeviceNumber < 0 ? "Default Windows Audio" : $"{DeviceNumber}: {Name}";
    public override string ToString() => Label;
}

public sealed class LocalAudioOutput : IDisposable
{
    private readonly BufferedWaveProvider _buffer = new(new WaveFormat(48000, 16, 2), TimeSpan.FromSeconds(2)) { DiscardOnBufferOverflow = true };
    private readonly WaveOut? _device;
    public bool IsAvailable => _device is not null;
    public string LastError { get; } = "";
    public int DeviceNumber { get; }

    public LocalAudioOutput(int deviceNumber = -1)
    {
        DeviceNumber = deviceNumber;
        try
        {
            _device = new WaveOut { DeviceNumber = deviceNumber, BufferMilliseconds = 50 };
            _device.Init(_buffer);
            _device.Play();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            try { _device?.Dispose(); } catch { }
            _device = null;
        }
    }

    public static IReadOnlyList<AudioDeviceOption> GetDevices()
    {
        var items = new List<AudioDeviceOption> { new(-1, "Default Windows Audio") };
        try
        {
            for (var i = 0; i < WaveOut.DeviceCount; i++)
                items.Add(new AudioDeviceOption(i, WaveOut.GetCapabilities(i).ProductName));
        }
        catch { }
        return items;
    }

    public void Reset() => _buffer.ClearBuffer();
    public void Send(AudioChunk chunk)
    {
        if (_device is not null && _buffer.BufferedDuration < TimeSpan.FromSeconds(1.5))
            _buffer.AddSamples(chunk.Pcm16Stereo48k);
    }
    public void Dispose() { if (_device is null) return; try { _device.Stop(); } catch { } _device.Dispose(); }
}
