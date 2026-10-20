using System.Diagnostics;
using System.Runtime.InteropServices;
using BroadcastPlayout.Ffmpeg;
using BroadcastPlayout.Models;

namespace BroadcastPlayout.Outputs;

/// <summary>
/// Native NDI video receiver backed directly by Processing.NDI.Lib.x64.dll.
/// This deliberately bypasses FFmpeg so generic FFmpeg builds without libndi_newtek can still
/// preview and play discovered NDI video sources.
/// </summary>
public sealed class NdiVideoReceiver : IDisposable
{
    private readonly IntPtr _library;
    private readonly IntPtr _receiver;
    private readonly CaptureV3 _capture;
    private readonly FreeVideoV2 _freeVideo;
    private readonly RecvDestroy _destroy;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private bool _disposed;

    public NdiVideoReceiver(string sourceName)
    {
        if (string.IsNullOrWhiteSpace(sourceName)) throw new InvalidOperationException("NDI source name is empty.");
        var path = ResolveRuntimePath();
        if (!File.Exists(path)) throw new FileNotFoundException("NDI runtime was not found.", path);
        _library = NativeLibrary.Load(path);
        var initialize = GetDelegate<Initialize>("NDIlib_initialize");
        if (!initialize()) throw new InvalidOperationException("NDI runtime initialization failed.");
        var create = GetDelegate<RecvCreateV3>("NDIlib_recv_create_v3");
        _capture = GetDelegate<CaptureV3>("NDIlib_recv_capture_v3");
        _freeVideo = GetDelegate<FreeVideoV2>("NDIlib_recv_free_video_v2");
        _destroy = GetDelegate<RecvDestroy>("NDIlib_recv_destroy");

        var effectiveName = NormalizeNdiSourceName(sourceName);
        var name = Marshal.StringToCoTaskMemUTF8(effectiveName);
        var recvName = Marshal.StringToCoTaskMemUTF8("Kashtrix Playout NDI Input");
        try
        {
            var settings = new RecvCreate
            {
                source_to_connect_to = new NdiSource { p_ndi_name = name, p_url_address = IntPtr.Zero },
                color_format = 0, // NDIlib_recv_color_format_BGRX_BGRA
                bandwidth = 100, // highest
                allow_video_fields = 0,
                p_ndi_recv_name = recvName
            };
            _receiver = create(ref settings);
        }
        finally
        {
            Marshal.FreeCoTaskMem(name);
            Marshal.FreeCoTaskMem(recvName);
        }
        if (_receiver == IntPtr.Zero) throw new InvalidOperationException($"Unable to create NDI receiver for '{sourceName}'.");
    }

    public bool TryRead(out VideoFrameData frame, int timeoutMs = 2000)
    {
        ThrowIfDisposed();
        var native = default(VideoFrameV2);
        while (true)
        {
            var type = _capture(_receiver, ref native, IntPtr.Zero, IntPtr.Zero, (uint)Math.Clamp(timeoutMs, 1, 5000));
            if (type == 1) // video
            {
                try
                {
                    if (native.xres <= 0 || native.yres <= 0 || native.p_data == IntPtr.Zero || native.line_stride_in_bytes == 0)
                    {
                        frame = null!;
                        return false;
                    }
                    var stride = Math.Abs(native.line_stride_in_bytes);
                    var bytes = new byte[stride * native.yres];
                    if (native.line_stride_in_bytes > 0)
                    {
                        Marshal.Copy(native.p_data, bytes, 0, bytes.Length);
                    }
                    else
                    {
                        for (var y = 0; y < native.yres; y++)
                        {
                            var src = native.p_data + (native.yres - 1 - y) * stride;
                            Marshal.Copy(src, bytes, y * stride, stride);
                        }
                    }
                    var n = native.frame_rate_N > 0 ? native.frame_rate_N : 25;
                    var d = native.frame_rate_D > 0 ? native.frame_rate_D : 1;
                    frame = new VideoFrameData(bytes, native.xres, native.yres, stride, _clock.Elapsed.TotalSeconds, n, d);
                    return true;
                }
                finally { _freeVideo(_receiver, ref native); }
            }
            if (type == 4) throw new InvalidOperationException("NDI receiver reported an error frame.");
            if (type == 0 || type == 100) { frame = null!; return false; }
            // audio/metadata are ignored here; capture again for video.
        }
    }

    private T GetDelegate<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));
    private static string ResolveRuntimePath()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "native", "ndi", "Processing.NDI.Lib.x64.dll"),
            Path.Combine(AppContext.BaseDirectory, "Processing.NDI.Lib.x64.dll")
        };
        foreach (var env in new[] { "NDI_RUNTIME_DIR_V6", "NDI_RUNTIME_DIR_V5" })
        {
            var dir = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrWhiteSpace(dir)) candidates.Add(Path.Combine(dir, "Processing.NDI.Lib.x64.dll"));
        }
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }
    private static string NormalizeNdiSourceName(string sourceName)
    {
        var trimmed = sourceName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed)) return trimmed;
        if (trimmed.Contains('(') && trimmed.Contains(')')) return trimmed;
        if (trimmed.Contains('.') || trimmed.Contains(':') || trimmed.Contains('/')) return trimmed;
        try
        {
            var machine = Environment.MachineName;
            return $"{machine} ({trimmed})";
        }
        catch { return trimmed; }
    }

    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(NdiVideoReceiver)); }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_receiver != IntPtr.Zero) try { _destroy(_receiver); } catch { }
        try { NativeLibrary.Free(_library); } catch { }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NdiSource { public IntPtr p_ndi_name; public IntPtr p_url_address; }
    [StructLayout(LayoutKind.Sequential)] private struct RecvCreate { public NdiSource source_to_connect_to; public int color_format; public int bandwidth; public byte allow_video_fields; public IntPtr p_ndi_recv_name; }
    [StructLayout(LayoutKind.Sequential, Pack = 8)] private struct VideoFrameV2
    {
        public int xres, yres, FourCC, frame_rate_N, frame_rate_D;
        public float picture_aspect_ratio;
        public int frame_format_type;
        public long timecode;
        public IntPtr p_data;
        public int line_stride_in_bytes;
        public IntPtr p_metadata;
        public long timestamp;
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool Initialize();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr RecvCreateV3(ref RecvCreate settings);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CaptureV3(IntPtr recv, ref VideoFrameV2 video, IntPtr audio, IntPtr metadata, uint timeoutMs);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FreeVideoV2(IntPtr recv, ref VideoFrameV2 video);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RecvDestroy(IntPtr recv);
}
