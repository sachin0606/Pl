using System.IO;
using System.Runtime.InteropServices;
using BroadcastPlayout.Ffmpeg;
using BroadcastPlayout.Models;

namespace BroadcastPlayout.Outputs;

/// <summary>
/// Minimal NDI 6 sender using the official Processing.NDI.Lib.x64.dll ABI.
/// All native calls are serialized so a UI enable/disable cannot destroy a sender while a frame is in flight.
/// </summary>
public sealed unsafe class NdiSender : IDisposable
{
    private readonly object _nativeSync = new();
    private readonly BgraFrameScaler _scaler = new();
    private IntPtr _sender;
    private bool _runtimeAcquired;
    private bool _disposed;
    private long _videoFramesSent;
    private long _audioFramesSent;

    public bool IsOpen
    {
        get { lock (_nativeSync) return _sender != IntPtr.Zero; }
    }

    public string LastError { get; private set; } = "";
    public string RuntimeVersion { get; private set; } = "Not loaded";
    public string RuntimePath => NdiNative.RuntimePath;
    public long VideoFramesSent => Interlocked.Read(ref _videoFramesSent);
    public long AudioFramesSent => Interlocked.Read(ref _audioFramesSent);
    public int ConnectionCount
    {
        get
        {
            lock (_nativeSync)
            {
                if (_sender == IntPtr.Zero || _disposed) return 0;
                try { return NdiNative.NDIlib_send_get_no_connections(_sender, 0); } catch { return -1; }
            }
        }
    }
    public string DebugSummary => !IsOpen
        ? $"OFF · Runtime: {RuntimeVersion} · {RuntimePath} · {LastError}".Trim()
        : $"SENDER OPEN · receivers {ConnectionCount} · video {VideoFramesSent} · audio {AudioFramesSent} · {RuntimeVersion} · {RuntimePath}";

    public bool Open(string sourceName)
    {
        lock (_nativeSync)
        {
            ThrowIfDisposed();
            CloseCore();

            try
            {
                NdiNative.ConfigureResolver();
                NdiNative.ValidateAbi();

                if (!NdiNative.NDIlib_is_supported_CPU())
                    throw new PlatformNotSupportedException("This CPU is not supported by the installed NDI runtime.");

                NdiNative.AcquireRuntime();
                _runtimeAcquired = true;

                var versionPtr = NdiNative.NDIlib_version();
                RuntimeVersion = versionPtr == IntPtr.Zero
                    ? "NDI runtime"
                    : Marshal.PtrToStringUTF8(versionPtr) ?? "NDI runtime";

                sourceName = string.IsNullOrWhiteSpace(sourceName) ? "KASHTRIX-PLAYOUT-01" : sourceName.Trim();
                if (sourceName.Length > 253) sourceName = sourceName[..253];

                var name = Marshal.StringToCoTaskMemUTF8(sourceName);
                try
                {
                    var create = new NdiNative.SendCreate
                    {
                        p_ndi_name = name,
                        p_groups = IntPtr.Zero,
                        clock_video = 0,
                        clock_audio = 0
                    };
                    _sender = NdiNative.NDIlib_send_create(ref create);
                    if (_sender == IntPtr.Zero)
                    {
                        // Some NDI runtime/network combinations reject an unclocked custom sender.
                        // Retry with the SDK reference clocking mode before falling back to the
                        // documented default-create path.
                        create.clock_video = 1;
                        create.clock_audio = 1;
                        _sender = NdiNative.NDIlib_send_create(ref create);
                    }
                    if (_sender == IntPtr.Zero)
                        _sender = NdiNative.NDIlib_send_create_default(IntPtr.Zero);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(name);
                }

                if (_sender == IntPtr.Zero)
                    throw new InvalidOperationException("NDI sender creation returned NULL.");

                Interlocked.Exchange(ref _videoFramesSent, 0);
                Interlocked.Exchange(ref _audioFramesSent, 0);
                LastError = "";
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                CloseCore();
                return false;
            }
        }
    }

    public void SendVideo(VideoFrameData frame, OutputProfile profile)
    {
        if (frame is null || frame.Bgra.Length == 0) return;

        lock (_nativeSync)
        {
            if (_sender == IntPtr.Zero || _disposed) return;

            try
            {
                var (outW, outH) = profile.Resolve(frame.Width, frame.Height);
                var outgoing = _scaler.ScaleReusable(frame, outW, outH);

                fixed (byte* p = outgoing.Bgra)
                {
                    var fps = NdiNative.ResolveFrameRate(profile.FramesPerSecond, outgoing.FrameRateNumerator, outgoing.FrameRateDenominator);
                    var vf = new NdiNative.VideoFrameV2
                    {
                        xres = outgoing.Width,
                        yres = outgoing.Height,
                        FourCC = NdiNative.FourCC_BGRA,
                        frame_rate_N = fps.N,
                        frame_rate_D = fps.D,
                        picture_aspect_ratio = outgoing.Width / (float)Math.Max(1, outgoing.Height),
                        frame_format_type = profile.IsInterlaced ? NdiNative.FrameFormatInterleaved : NdiNative.FrameFormatProgressive,
                        timecode = NdiNative.SendTimecodeSynthesize,
                        p_data = (IntPtr)p,
                        line_stride_in_bytes = outgoing.Stride,
                        p_metadata = IntPtr.Zero,
                        timestamp = 0
                    };
                    NdiNative.NDIlib_send_send_video_v2(_sender, ref vf);
                }
                Interlocked.Increment(ref _videoFramesSent);
                LastError = "";
            }
            catch (Exception ex)
            {
                LastError = "NDI video: " + ex.Message;
            }
        }
    }

    public void SendAudio(AudioChunk chunk)
    {
        if (chunk is null || chunk.Pcm16Stereo48k.Length == 0) return;

        lock (_nativeSync)
        {
            if (_sender == IntPtr.Zero || _disposed) return;

            try
            {
                if (chunk.Channels <= 0 || chunk.BitsPerSample != 16) return;
                var bytesPerSampleFrame = chunk.Channels * sizeof(short);
                if (chunk.Pcm16Stereo48k.Length < bytesPerSampleFrame) return;

                fixed (byte* p = chunk.Pcm16Stereo48k)
                {
                    // IMPORTANT: native field order is timecode, reference_level, p_data.
                    // Putting p_data before reference_level corrupts the pointer on x64.
                    var af = new NdiNative.AudioInterleaved16s
                    {
                        sample_rate = chunk.SampleRate,
                        no_channels = chunk.Channels,
                        no_samples = chunk.Pcm16Stereo48k.Length / bytesPerSampleFrame,
                        timecode = NdiNative.SendTimecodeSynthesize,
                        reference_level = 0,
                        p_data = (IntPtr)p
                    };
                    NdiNative.NDIlib_util_send_send_audio_interleaved_16s(_sender, ref af);
                }
                Interlocked.Increment(ref _audioFramesSent);
                LastError = "";
            }
            catch (Exception ex)
            {
                LastError = "NDI audio: " + ex.Message;
            }
        }
    }

    public void Close()
    {
        lock (_nativeSync) CloseCore();
    }

    private void CloseCore()
    {
        if (_sender != IntPtr.Zero)
        {
            try { NdiNative.NDIlib_send_destroy(_sender); }
            catch { }
            _sender = IntPtr.Zero;
        }

        if (_runtimeAcquired)
        {
            NdiNative.ReleaseRuntime();
            _runtimeAcquired = false;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(NdiSender));
    }

    public void Dispose()
    {
        lock (_nativeSync)
        {
            if (_disposed) return;
            CloseCore();
            _scaler.Dispose();
            _disposed = true;
        }
    }

    private static class NdiNative
    {
        private const string Lib = "Processing.NDI.Lib.x64.dll";
        private static int _resolverConfigured;
        private static IntPtr _loadedRuntime;
        private static string _loadedRuntimePath = "";
        private static readonly object RuntimeSync = new();
        private static int _runtimeUsers;

        internal const long SendTimecodeSynthesize = long.MaxValue;
        internal const int FrameFormatInterleaved = 0;
        internal const int FrameFormatProgressive = 1;
        internal static readonly int FourCC_BGRA = 'B' | ('G' << 8) | ('R' << 16) | ('A' << 24);
        internal static string RuntimePath => _loadedRuntimePath;

        internal static void AcquireRuntime()
        {
            lock (RuntimeSync)
            {
                if (_runtimeUsers == 0 && !NDIlib_initialize())
                    throw new InvalidOperationException("NDI runtime initialization failed.");
                _runtimeUsers++;
            }
        }

        internal static void ReleaseRuntime()
        {
            lock (RuntimeSync)
            {
                if (_runtimeUsers <= 0) return;
                _runtimeUsers--;
                if (_runtimeUsers == 0)
                {
                    try { NDIlib_destroy(); } catch { }
                }
            }
        }

        internal static (int N, int D) ResolveFrameRate(double requested, int sourceN, int sourceD)
        {
            if (requested <= 0)
                return (Math.Max(1, sourceN), Math.Max(1, sourceD));
            if (Math.Abs(requested - 23.976) < .02) return (24000, 1001);
            if (Math.Abs(requested - 29.97) < .02) return (30000, 1001);
            if (Math.Abs(requested - 59.94) < .02) return (60000, 1001);
            return ((int)Math.Round(requested * 1000.0), 1000);
        }

        internal static void ConfigureResolver()
        {
            if (Interlocked.Exchange(ref _resolverConfigured, 1) != 0) return;

            NativeLibrary.SetDllImportResolver(typeof(NdiNative).Assembly, (name, _, _) =>
            {
                if (!name.Equals(Lib, StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
                if (_loadedRuntime != IntPtr.Zero) return _loadedRuntime;

                var candidates = new List<string>
                {
                    Path.Combine(AppContext.BaseDirectory, "native", "ndi", Lib),
                    Path.Combine(AppContext.BaseDirectory, Lib)
                };

                foreach (var envName in new[] { "NDI_RUNTIME_DIR_V6", "NDI_RUNTIME_DIR_V5" })
                {
                    var dir = Environment.GetEnvironmentVariable(envName);
                    if (!string.IsNullOrWhiteSpace(dir)) candidates.Add(Path.Combine(dir, Lib));
                }

                foreach (var full in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!File.Exists(full)) continue;
                    _loadedRuntime = NativeLibrary.Load(full);
                    _loadedRuntimePath = full;
                    return _loadedRuntime;
                }

                return IntPtr.Zero;
            });
        }

        internal static void ValidateAbi()
        {
            if (IntPtr.Size != 8)
                throw new PlatformNotSupportedException("Kashtrix NDI output requires an x64 process.");

            ValidateSize<SendCreate>(24);
            ValidateOffset<SendCreate>(nameof(SendCreate.clock_video), 16);
            ValidateOffset<SendCreate>(nameof(SendCreate.clock_audio), 17);

            ValidateSize<VideoFrameV2>(72);
            ValidateOffset<VideoFrameV2>(nameof(VideoFrameV2.timecode), 32);
            ValidateOffset<VideoFrameV2>(nameof(VideoFrameV2.p_data), 40);
            ValidateOffset<VideoFrameV2>(nameof(VideoFrameV2.line_stride_in_bytes), 48);
            ValidateOffset<VideoFrameV2>(nameof(VideoFrameV2.p_metadata), 56);
            ValidateOffset<VideoFrameV2>(nameof(VideoFrameV2.timestamp), 64);

            ValidateSize<AudioInterleaved16s>(40);
            ValidateOffset<AudioInterleaved16s>(nameof(AudioInterleaved16s.timecode), 16);
            ValidateOffset<AudioInterleaved16s>(nameof(AudioInterleaved16s.reference_level), 24);
            ValidateOffset<AudioInterleaved16s>(nameof(AudioInterleaved16s.p_data), 32);
        }

        private static void ValidateSize<T>(int expected) where T : struct
        {
            var actual = Marshal.SizeOf<T>();
            if (actual != expected)
                throw new InvalidOperationException($"NDI ABI mismatch: {typeof(T).Name} size {actual}, expected {expected}.");
        }

        private static void ValidateOffset<T>(string field, int expected) where T : struct
        {
            var actual = Marshal.OffsetOf<T>(field).ToInt32();
            if (actual != expected)
                throw new InvalidOperationException($"NDI ABI mismatch: {typeof(T).Name}.{field} offset {actual}, expected {expected}.");
        }

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        internal struct SendCreate
        {
            public IntPtr p_ndi_name;
            public IntPtr p_groups;
            public byte clock_video;
            public byte clock_audio;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        internal struct VideoFrameV2
        {
            public int xres;
            public int yres;
            public int FourCC;
            public int frame_rate_N;
            public int frame_rate_D;
            public float picture_aspect_ratio;
            public int frame_format_type;
            public long timecode;
            public IntPtr p_data;
            public int line_stride_in_bytes;
            public IntPtr p_metadata;
            public long timestamp;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        internal struct AudioInterleaved16s
        {
            public int sample_rate;
            public int no_channels;
            public int no_samples;
            public long timecode;
            public int reference_level;
            public IntPtr p_data;
        }

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool NDIlib_initialize();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void NDIlib_destroy();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr NDIlib_version();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool NDIlib_is_supported_CPU();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr NDIlib_send_create(ref SendCreate p_create_settings);

        [DllImport(Lib, EntryPoint = "NDIlib_send_create", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr NDIlib_send_create_default(IntPtr p_create_settings);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void NDIlib_send_destroy(IntPtr p_instance);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void NDIlib_send_send_video_v2(IntPtr p_instance, ref VideoFrameV2 p_video_data);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int NDIlib_send_get_no_connections(IntPtr p_instance, uint timeout_in_ms);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void NDIlib_util_send_send_audio_interleaved_16s(IntPtr p_instance, ref AudioInterleaved16s p_audio_data);
    }
}
