using System.IO.MemoryMappedFiles;
using System.Text;
using BroadcastPlayout.Models;

namespace BroadcastPlayout.Outputs;

/// <summary>
/// Publishes formatted program video/audio to named shared memory. The optional native
/// KashtrixVirtualOutput.ax DirectShow source filter reads these maps and exposes them to
/// applications such as vMix/OBS/Easy Media Suite as a virtual capture device.
/// </summary>
public sealed class VirtualOutputBridge : IDisposable
{
    public const string VideoMapName = "KashtrixPlayout.VirtualOutput.Video.v1";
    public const string AudioMapName = "KashtrixPlayout.VirtualOutput.Audio.v1";
    public const string ProgramConfidenceVideoMapName = "KashtrixPlayout.ProgramConfidence.Video.v1";
    public const string ProgramConfidenceAudioMapName = "KashtrixPlayout.ProgramConfidence.Audio.v1";
    public const string CgProgramVideoMapName = "KashtrixPlayout.CgProgram.Video.v1";
    public const string CgProgramAudioMapName = "KashtrixPlayout.CgProgram.Audio.v1";
    private readonly object _sync = new();
    private readonly string _videoMapName;
    private readonly string _audioMapName;
    private MemoryMappedFile? _videoMap;
    private MemoryMappedViewAccessor? _videoView;
    private MemoryMappedFile? _audioMap;
    private MemoryMappedViewAccessor? _audioView;
    private int _width = 1920, _height = 1080;
    private double _fps = 50;
    private long _videoSequence, _audioSequence;
    public bool IsOpen => _videoView is not null;
    public string Status { get; private set; } = "OFF";

    public VirtualOutputBridge(string videoMapName = VideoMapName, string audioMapName = AudioMapName)
    {
        _videoMapName = string.IsNullOrWhiteSpace(videoMapName) ? VideoMapName : videoMapName;
        _audioMapName = string.IsNullOrWhiteSpace(audioMapName) ? AudioMapName : audioMapName;
    }

    public void Open(int width, int height, double fps)
    {
        lock (_sync)
        {
            CloseCore();
            _width = Math.Clamp(width, 320, 7680); _height = Math.Clamp(height, 240, 4320); _fps = Math.Clamp(fps, 1, 120);
            var frameCapacity = (long)_width * _height * 4;
            if (string.Equals(_videoMapName, ProgramConfidenceVideoMapName, StringComparison.Ordinal) ||
                string.Equals(_videoMapName, CgProgramVideoMapName, StringComparison.Ordinal))
                frameCapacity = Math.Max(frameCapacity, 3840L * 2160 * 4);
            var videoCapacity = 256L + frameCapacity;
            _videoMap = MemoryMappedFile.CreateOrOpen(_videoMapName, videoCapacity, MemoryMappedFileAccess.ReadWrite);
            _videoView = _videoMap.CreateViewAccessor(0, videoCapacity, MemoryMappedFileAccess.ReadWrite);
            var audioCapacity = 256L + 48000 * 2 * 2 * 2; // about two seconds stereo PCM16
            _audioMap = MemoryMappedFile.CreateOrOpen(_audioMapName, audioCapacity, MemoryMappedFileAccess.ReadWrite);
            _audioView = _audioMap.CreateViewAccessor(0, audioCapacity, MemoryMappedFileAccess.ReadWrite);
            Status = $"ON · {_width}x{_height} · {_fps:0.###} fps";
        }
    }

    public void SubmitVideo(VideoFrameData frame, bool onAir = false)
    {
        lock (_sync)
        {
            if (_videoView is null) return;
            var bytes = frame.Width == _width && frame.Height == _height && frame.Stride == _width * 4
                ? frame.Bgra : ResizeBgra(frame.Bgra, frame.Width, frame.Height, frame.Stride, _width, _height);
            // Publish the sequence last. Readers use it as a commit marker and verify it
            // after copying pixels, preventing half-written/torn confidence frames.
            var nextSequence = _videoSequence + 1;
            _videoView.Write(0, 0x4B545856); // KTXV
            _videoView.Write(4, 1);
            _videoView.Write(8, _width); _videoView.Write(12, _height); _videoView.Write(16, _width * 4);
            _videoView.Write(20, _fps); _videoView.Write(36, DateTime.UtcNow.Ticks);
            var length = (int)Math.Min(bytes.Length, _videoView.Capacity - 256);
            _videoView.Write(44, length);
            _videoView.Write(48, onAir ? 1 : 0);
            CopyToView(_videoView, 256, bytes, length);
            _videoView.Write(28, nextSequence);
            _videoSequence = nextSequence;
        }
    }

    public void SubmitAudio(AudioChunk chunk)
    {
        lock (_sync)
        {
            if (_audioView is null) return;
            var max = (int)Math.Min(chunk.Pcm16Stereo48k.Length, _audioView.Capacity - 256);
            _audioSequence++;
            _audioView.Write(0, 0x4B545841); // KTXA
            _audioView.Write(4, 1); _audioView.Write(8, chunk.SampleRate); _audioView.Write(12, chunk.Channels); _audioView.Write(16, chunk.BitsPerSample);
            _audioView.Write(20, _audioSequence); _audioView.Write(28, DateTime.UtcNow.Ticks); _audioView.Write(36, max);
            _audioView.WriteArray(256, chunk.Pcm16Stereo48k, 0, max);
        }
    }

    // One bulk copy into the mapped view; WriteArray's per-call validation made 8 MB frames slow.
    private static unsafe void CopyToView(MemoryMappedViewAccessor view, long offset, byte[] source, int length)
    {
        if (length <= 0) return;
        byte* basePtr = null;
        var handle = view.SafeMemoryMappedViewHandle;
        handle.AcquirePointer(ref basePtr);
        try
        {
            fixed (byte* src = source)
                Buffer.MemoryCopy(src, basePtr + view.PointerOffset + offset, view.Capacity - offset, length);
        }
        finally
        {
            handle.ReleasePointer();
        }
    }

    public void Close() { lock (_sync) CloseCore(); }
    private void CloseCore(){_videoView?.Dispose();_videoMap?.Dispose();_audioView?.Dispose();_audioMap?.Dispose();_videoView=null;_videoMap=null;_audioView=null;_audioMap=null;Status="OFF";}
    public void Dispose() => Close();

    private static byte[] ResizeBgra(byte[] source, int sw, int sh, int sourceStride, int dw, int dh)
    {
        var output = new byte[dw * dh * 4];
        for (var y = 0; y < dh; y++)
        {
            var sy = Math.Min(sh - 1, y * sh / dh);
            for (var x = 0; x < dw; x++)
            {
                var sx = Math.Min(sw - 1, x * sw / dw);
                var s = sy * sourceStride + sx * 4; var d = (y * dw + x) * 4;
                if (s + 3 >= source.Length) continue;
                output[d]=source[s]; output[d+1]=source[s+1]; output[d+2]=source[s+2]; output[d+3]=source[s+3];
            }
        }
        return output;
    }
}
