using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Text.Json;
using BroadcastPlayout.Models;
using CefSharp;
using CefSharp.OffScreen;

namespace BroadcastPlayout.Services;

/// <summary>
/// Process-wide Chromium/CEF lifecycle for HTML broadcast graphics.
/// HTML graphics are rendered off-screen and therefore don't need to be timeline-authored.
/// Their own CSS/JavaScript animations continue to run inside Chromium.
/// </summary>
public static class ChromiumCgRuntime
{
    private static readonly object Sync = new();
    private static bool _initialized;
    private static bool _failed;
    private static string? _error;

    public static bool IsReady => _initialized && !_failed;
    public static string Status => IsReady ? "Chromium ready" : (_error ?? "Chromium not initialized");

    public static void Initialize()
    {
        lock (Sync)
        {
            if (_initialized || _failed) return;
            try
            {
                CefSharpSettings.SubprocessExitIfParentProcessClosed = true;
                Cef.EnableWaitForBrowsersToClose();
                var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KashtrixPlayout", "chromium-cache");
                Directory.CreateDirectory(cache);
                var settings = new CefSettings
                {
                    CachePath = cache,
                    BackgroundColor = Cef.ColorSetARGB(0, 0, 0, 0),
                    LogSeverity = LogSeverity.Warning
                };
                settings.CefCommandLineArgs["autoplay-policy"] = "no-user-gesture-required";
                settings.CefCommandLineArgs["disable-background-timer-throttling"] = "1";
                settings.CefCommandLineArgs["disable-renderer-backgrounding"] = "1";
                settings.CefCommandLineArgs["disable-backgrounding-occluded-windows"] = "1";
                _initialized = Cef.InitializeAsync(settings).GetAwaiter().GetResult();
                if (!_initialized)
                {
                    _failed = true;
                    _error = "CEF initialization returned false.";
                }
            }
            catch (Exception ex)
            {
                _failed = true;
                _error = ex.GetBaseException().Message;
            }
        }
    }

    public static void Shutdown()
    {
        lock (Sync)
        {
            if (!_initialized) return;
            try
            {
                ChromiumCgRenderer.Shared.Dispose();
                Cef.WaitForBrowsersToClose();
                Cef.Shutdown();
            }
            catch { }
            finally { _initialized = false; }
        }
    }
}

/// <summary>
/// Keeps Chromium browsers alive per HTML source and exposes the most recent transparent bitmap
/// to the synchronous Program compositor. Browser capture happens on background tasks and never
/// blocks the playout thread waiting for web content.
/// </summary>
public sealed class ChromiumCgRenderer : IDisposable
{
    public static ChromiumCgRenderer Shared { get; } = new();
    private readonly ConcurrentDictionary<Guid, BrowserState> _states = new();
    private readonly string _packageRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KashtrixPlayout", "html-packages");
    private bool _disposed;

    private ChromiumCgRenderer() => Directory.CreateDirectory(_packageRoot);

    public Bitmap? GetLatest(CgHtmlSource source)
    {
        if (_disposed || !source.Visible || !ChromiumCgRuntime.IsReady) return null;
        try
        {
            var state = _states.GetOrAdd(source.Id, _ => new BrowserState(source, ResolveAddress(source)));
            state.EnsureConfiguration(source, ResolveAddress(source));
            source.Status = state.Status;
            return state.CloneLatest();
        }
        catch (Exception ex)
        {
            source.Status = "HTML error: " + ex.GetBaseException().Message;
            return null;
        }
    }

    public void Reload(CgHtmlSource source)
    {
        if (_states.TryGetValue(source.Id, out var state)) state.Reload();
    }

    public async Task UpdateDataAsync(CgHtmlSource source, string json)
    {
        source.DataJson = string.IsNullOrWhiteSpace(json) ? "{}" : json;
        if (!_states.TryGetValue(source.Id, out var state))
        {
            _ = GetLatest(source);
            _states.TryGetValue(source.Id, out state);
        }
        if (state is not null) await state.UpdateDataAsync(source.DataJson).ConfigureAwait(false);
    }

    public void Remove(Guid id)
    {
        if (_states.TryRemove(id, out var state)) state.Dispose();
    }

    public string ImportKashGfx(string packagePath)
    {
        if (!File.Exists(packagePath)) throw new FileNotFoundException("Graphics package not found.", packagePath);
        var key = Path.GetFileNameWithoutExtension(packagePath) + "-" + Math.Abs(packagePath.GetHashCode()).ToString("X8");
        var dest = Path.Combine(_packageRoot, key);
        if (Directory.Exists(dest)) Directory.Delete(dest, true);
        Directory.CreateDirectory(dest);
        ZipFile.ExtractToDirectory(packagePath, dest, overwriteFiles: true);

        var manifestPath = Path.Combine(dest, "manifest.json");
        if (File.Exists(manifestPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
                if (doc.RootElement.TryGetProperty("entry", out var entry))
                {
                    var candidate = Path.GetFullPath(Path.Combine(dest, entry.GetString() ?? "index.html"));
                    if (candidate.StartsWith(Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase) && File.Exists(candidate)) return candidate;
                }
            }
            catch { }
        }

        var index = Directory.EnumerateFiles(dest, "index.html", SearchOption.AllDirectories).FirstOrDefault()
                    ?? Directory.EnumerateFiles(dest, "*.htm", SearchOption.AllDirectories).FirstOrDefault();
        if (index is null) throw new InvalidDataException(".kashgfx package does not contain index.html (or manifest entry).");
        return index;
    }

    private string ResolveAddress(CgHtmlSource source)
    {
        var value = source.Source?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return "about:blank";
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme is "http" or "https" or "file")) return uri.AbsoluteUri;
        if (Directory.Exists(value))
        {
            var index = Path.Combine(value, "index.html");
            if (!File.Exists(index)) index = Directory.EnumerateFiles(value, "*.htm*", SearchOption.TopDirectoryOnly).FirstOrDefault() ?? index;
            value = index;
        }
        return File.Exists(value) ? new Uri(Path.GetFullPath(value)).AbsoluteUri : value;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var state in _states.Values) state.Dispose();
        _states.Clear();
    }

    private sealed class BrowserState : IDisposable
    {
        private readonly object _sync = new();
        private readonly object _frameSync = new();
        private ChromiumWebBrowser? _browser;
        private Bitmap? _latest;
        private CancellationTokenSource? _cts;
        private Task? _pump;
        private string _address;
        private int _width;
        private int _height;
        private int _fps;
        private bool _transparent;
        private bool _disposed;
        private volatile string _status = "Loading";
        public string Status => _status;

        public BrowserState(CgHtmlSource source, string address)
        {
            _address = address;
            _width = Math.Clamp(source.CanvasWidth, 64, 4096);
            _height = Math.Clamp(source.CanvasHeight, 64, 2160);
            _fps = Math.Clamp(source.BrowserFps, 1, 60);
            _transparent = source.Transparent;
            Start(source);
        }

        public void EnsureConfiguration(CgHtmlSource source, string address)
        {
            var w = Math.Clamp(source.CanvasWidth, 64, 4096);
            var h = Math.Clamp(source.CanvasHeight, 64, 2160);
            var fps = Math.Clamp(source.BrowserFps, 1, 60);
            if (string.Equals(address, _address, StringComparison.OrdinalIgnoreCase) && w == _width && h == _height && fps == _fps && source.Transparent == _transparent) return;
            lock (_sync)
            {
                if (_disposed) return;
                StopBrowser();
                _address = address; _width = w; _height = h; _fps = fps; _transparent = source.Transparent;
                Start(source);
            }
        }

        private void Start(CgHtmlSource source)
        {
            if (!ChromiumCgRuntime.IsReady) { _status = ChromiumCgRuntime.Status; return; }
            try
            {
                var browserSettings = new BrowserSettings
                {
                    WindowlessFrameRate = _fps,
                    BackgroundColor = source.Transparent ? Cef.ColorSetARGB(0, 0, 0, 0) : Cef.ColorSetARGB(255, 0, 0, 0),
                    Javascript = source.JavaScriptEnabled ? CefState.Enabled : CefState.Disabled
                };
                _browser = new ChromiumWebBrowser(_address, browserSettings) { Size = new Size(_width, _height) };
                _cts = new CancellationTokenSource();
                _pump = Task.Run(() => PumpAsync(source, _cts.Token));
                _status = "Loading";
            }
            catch (Exception ex) { _status = "Browser error: " + ex.GetBaseException().Message; }
        }

        private async Task PumpAsync(CgHtmlSource source, CancellationToken ct)
        {
            try
            {
                if (_browser is null) return;
                var load = await _browser.WaitForInitialLoadAsync().ConfigureAwait(false);
                _status = load.Success ? "READY" : $"Load error: {load.ErrorCode}";
                if (!string.IsNullOrWhiteSpace(source.DataJson) && source.DataJson.Trim() != "{}") await UpdateDataAsync(source.DataJson).ConfigureAwait(false);

                while (!ct.IsCancellationRequested && !_disposed)
                {
                    try
                    {
                        using var shot = _browser.ScreenshotOrNull();
                        if (shot is not null)
                        {
                            lock (_frameSync)
                            {
                                _latest?.Dispose();
                                _latest = shot.Clone(new Rectangle(0, 0, shot.Width, shot.Height), PixelFormat.Format32bppArgb);
                            }
                            _status = "READY";
                        }
                    }
                    catch (Exception ex) { _status = "Capture: " + ex.GetBaseException().Message; }
                    await Task.Delay(Math.Max(15, 1000 / Math.Max(1, _fps)), ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _status = "HTML error: " + ex.GetBaseException().Message; }
        }

        public Bitmap? CloneLatest()
        {
            lock (_frameSync) return _latest is null ? null : (Bitmap)_latest.Clone();
        }

        public void Reload()
        {
            try { if (_browser is { IsBrowserInitialized: true }) _browser.GetBrowser().Reload(true); _status = "Reloading"; } catch { }
        }

        public async Task UpdateDataAsync(string json)
        {
            if (_browser is null) return;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                var payload = JsonSerializer.Serialize(doc.RootElement);
                var script = $"(function(){{var d={payload};if(window.Kashtrix&&typeof window.Kashtrix.update==='function'){{window.Kashtrix.update(d);}}window.dispatchEvent(new CustomEvent('kashtrix-data',{{detail:d}}));return true;}})();";
                await _browser.EvaluateScriptAsync(script).ConfigureAwait(false);
            }
            catch (Exception ex) { _status = "Data error: " + ex.GetBaseException().Message; }
        }

        private void StopBrowser()
        {
            try { _cts?.Cancel(); } catch { }
            try { _pump?.Wait(300); } catch { }
            try { _browser?.Dispose(); } catch { }
            _browser = null;
            _cts?.Dispose(); _cts = null; _pump = null;
            lock (_frameSync) { _latest?.Dispose(); _latest = null; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopBrowser();
        }
    }
}
