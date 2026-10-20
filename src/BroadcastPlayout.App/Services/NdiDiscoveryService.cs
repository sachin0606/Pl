using System.Runtime.InteropServices;

namespace BroadcastPlayout.Services;

/// <summary>
/// Lightweight NDI discovery that talks directly to the bundled NDI runtime.
/// It does not depend on FFmpeg NDI modules. A scan waits for source changes for the
/// requested window, then returns the current source table exposed by the NDI finder.
/// </summary>
public static class NdiDiscoveryService
{
    public sealed record NdiSourceInfo(string Name, string Address)
    {
        public override string ToString() => string.IsNullOrWhiteSpace(Address) ? Name : $"{Name}  ·  {Address}";
    }

    private static readonly object Sync = new();
    private static IntPtr _library;
    private static Initialize? _initialize;
    private static bool _initialized;
    private static FindCreateV2? _findCreate;
    private static FindDestroy? _findDestroy;
    private static FindWaitForSources? _findWait;
    private static FindGetCurrentSources? _findGet;

    public static async Task<IReadOnlyList<NdiSourceInfo>> ScanAsync(TimeSpan? duration = null, CancellationToken cancellationToken = default)
    {
        var scanDuration = duration ?? TimeSpan.FromSeconds(10);
        if (scanDuration < TimeSpan.FromMilliseconds(250)) scanDuration = TimeSpan.FromMilliseconds(250);
        return await Task.Run(() => Scan(scanDuration, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public static IReadOnlyList<NdiSourceInfo> Scan(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        EnsureLoaded();

        // NDI Finder can behave differently when the sender and receiver live in different
        // processes on the same workstation. Explicitly seed loopback + local IPv4 addresses
        // and accumulate snapshots during the scan window so a short-lived discovery update is
        // not lost before the final NDIlib_find_get_current_sources call.
        var extraIpText = BuildExtraIpList();
        var extraIpPointer = string.IsNullOrWhiteSpace(extraIpText) ? IntPtr.Zero : Marshal.StringToCoTaskMemUTF8(extraIpText);
        IntPtr finder = IntPtr.Zero;
        try
        {
            var settings = new FinderCreate
            {
                show_local_sources = 1,
                p_groups = IntPtr.Zero,
                p_extra_ips = extraIpPointer
            };
            finder = _findCreate!(ref settings);
            if (finder == IntPtr.Zero) throw new InvalidOperationException("NDI finder creation failed.");

            var sources = new Dictionary<string,NdiSourceInfo>(StringComparer.OrdinalIgnoreCase);
            void CaptureSnapshot()
            {
                uint count = 0;
                var pointer = _findGet!(finder, ref count);
                if (pointer == IntPtr.Zero || count == 0) return;
                var size = Marshal.SizeOf<NativeSource>();
                for (var i = 0; i < count; i++)
                {
                    var source = Marshal.PtrToStructure<NativeSource>(pointer + i * size);
                    var name = PtrUtf8(source.p_ndi_name).Trim();
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    sources[name] = new NdiSourceInfo(name, PtrUtf8(source.p_url_address).Trim());
                }
            }

            var deadline = DateTime.UtcNow + duration;
            CaptureSnapshot();
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) break;
                _findWait!(finder, (uint)Math.Clamp((int)left.TotalMilliseconds, 50, 500));
                CaptureSnapshot();
            } while (DateTime.UtcNow < deadline);

            return sources.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        finally
        {
            if (finder != IntPtr.Zero) _findDestroy!(finder);
            if (extraIpPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(extraIpPointer);
        }
    }

    private static string BuildExtraIpList()
    {
        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "127.0.0.1" };
        try
        {
            foreach (var address in System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName()))
                if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(address))
                    addresses.Add(address.ToString());
        }
        catch { }
        return string.Join(",", addresses);
    }

    public static string RuntimePath
    {
        get { EnsureLoaded(); return ResolveRuntimePath(); }
    }

    private static void EnsureLoaded()
    {
        lock (Sync)
        {
            if (_library != IntPtr.Zero) return;
            var path = ResolveRuntimePath();
            if (!File.Exists(path)) throw new FileNotFoundException("Processing.NDI.Lib.x64.dll was not found.", path);
            _library = NativeLibrary.Load(path);
            _initialize = GetDelegate<Initialize>("NDIlib_initialize");
            if (!_initialized)
            {
                if (!_initialize()) throw new InvalidOperationException("NDI runtime initialization failed.");
                _initialized = true;
            }
            _findCreate = GetDelegate<FindCreateV2>("NDIlib_find_create_v2");
            _findDestroy = GetDelegate<FindDestroy>("NDIlib_find_destroy");
            _findWait = GetDelegate<FindWaitForSources>("NDIlib_find_wait_for_sources");
            _findGet = GetDelegate<FindGetCurrentSources>("NDIlib_find_get_current_sources");
        }
    }

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

    private static T GetDelegate<T>(string export) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, export));

    private static string PtrUtf8(IntPtr value) => value == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(value) ?? string.Empty;

    [StructLayout(LayoutKind.Sequential)]
    private struct FinderCreate
    {
        public byte show_local_sources;
        public IntPtr p_groups;
        public IntPtr p_extra_ips;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSource
    {
        public IntPtr p_ndi_name;
        public IntPtr p_url_address;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool Initialize();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr FindCreateV2(ref FinderCreate settings);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FindDestroy(IntPtr finder);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool FindWaitForSources(IntPtr finder, uint timeoutMs);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr FindGetCurrentSources(IntPtr finder, ref uint count);
}
