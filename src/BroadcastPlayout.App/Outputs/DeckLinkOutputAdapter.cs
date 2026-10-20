using System.Runtime.InteropServices;
using BroadcastPlayout.Ffmpeg;
using BroadcastPlayout.Models;
#if DECKLINK_SDK
using DeckLinkAPI;
#endif

namespace BroadcastPlayout.Outputs;

public enum DeckLinkKeyerMode
{
    Off,
    Internal,
    External
}

public sealed record DeckLinkDeviceOption(int Index, string ModelName, string DisplayName)
{
    public string PersistentName => string.IsNullOrWhiteSpace(DisplayName) ? ModelName : DisplayName;
    public string Label => string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Equals(ModelName, StringComparison.OrdinalIgnoreCase)
        ? ModelName
        : $"{ModelName} · {DisplayName}";
}

public sealed record DeckLinkDisplayModeOption(string ModeName, int Width, int Height, double FrameRate, string ScanMode)
{
    public string DisplayLabel => $"{ModeName} ({Width}x{Height} @ {FrameRate:0.##} fps {ScanMode})";
    public override string ToString() => DisplayLabel;
}

/// <summary>
/// DeckLink SDI/HDMI output using the SDK's scheduled playback: frames and audio are queued a few
/// frames ahead on the card's own timeline and the card plays them out on its (genlockable) clock,
/// so Windows scheduling jitter never reaches the wire. Video and audio are placed by the Program
/// cadence timestamp; when the card's clock and the PC clock drift apart the adapter drops or holds
/// one video frame together with its audio block (frame-synchroniser behaviour), so A/V stays locked.
/// If a card refuses scheduled playback the adapter falls back to immediate (sync) output.
/// </summary>
public sealed class DeckLinkOutputAdapter : IDisposable
{
    private readonly object _sync = new();
    private readonly BgraFrameScaler _scaler = new();
#if DECKLINK_SDK
    private const int FramePoolSize = 8;
    private const int TargetBufferedFrames = 4;
    // Start only once the target buffer is queued, so the card never begins below its safety margin.
    private const int PrerollFrames = TargetBufferedFrames;
    private const int HighWaterFrames = 7;
    private const int LowWaterFrames = 1;
    private const int AudioSampleRate = 48000;

    private IDeckLink? _device;
    private IDeckLinkOutput? _output;
    private IDeckLinkMutableVideoFrame? _videoFrame;
    private IDeckLinkKeyer? _keyer;
    private int _width, _height;
    private string _openProfileKey = string.Empty;

    // Scheduled playback state (guarded by _sync; the free-frame queue by _poolSync).
    private readonly object _poolSync = new();
    private readonly Queue<IDeckLinkMutableVideoFrame> _freeFrames = new();
    private readonly List<IDeckLinkMutableVideoFrame> _frames = [];
    private CompletionCallback? _callback;
    private bool _scheduled;
    private bool _started;
    private long _frameDuration = 1;
    private long _timeScale = 50;
    private double _basePts = double.NaN;
    private long _baseSlot;
    private long _lastVideoSlot = -1;
    private long _sequentialVideoSlot;
    private long _sequentialAudioSlot;
    private double _tickPts = double.NaN;
    private long? _tickSlot;
    private int _scheduledSinceOpen;
    private byte[]? _lastAudioBlock;
    private long _framesLate, _framesDropped, _syncDrops, _syncHolds, _poolStarved;

    public int DeviceIndex { get; set; }
    public DeckLinkKeyerMode KeyerMode { get; set; } = DeckLinkKeyerMode.Off;
    public byte KeyerLevel { get; set; } = 255;
    public bool EnableAudio { get; set; } = true;
    public bool IsSupportedBuild => true;
    public bool IsOpen { get { lock (_sync) return _output is not null; } }
    public string LastError { get; private set; } = "";

    /// <summary>Scheduled-playback health for diagnostics.</summary>
    public string OutputStats
    {
        get
        {
            lock (_sync)
            {
                if (_output is null) return "DeckLink closed";
                if (!_scheduled) return "DeckLink immediate (sync) output";
                uint buffered = 0;
                try { _output.GetBufferedVideoFrameCount(out buffered); } catch { }
                return $"DeckLink scheduled · buffered {buffered} · late {Interlocked.Read(ref _framesLate)} · dropped {Interlocked.Read(ref _framesDropped)} · sync drop/hold {_syncDrops}/{_syncHolds} · pool starved {_poolStarved}";
            }
        }
    }

    /// <summary>Returns played-out card frames to the pool and counts late/dropped results. Runs on a DeckLink thread.</summary>
    private sealed class CompletionCallback(DeckLinkOutputAdapter owner) : IDeckLinkVideoOutputCallback
    {
        public void ScheduledFrameCompleted(IDeckLinkVideoFrame completedFrame, _BMDOutputFrameCompletionResult result)
        {
            if (result == _BMDOutputFrameCompletionResult.bmdOutputFrameDisplayedLate) Interlocked.Increment(ref owner._framesLate);
            else if (result == _BMDOutputFrameCompletionResult.bmdOutputFrameDropped) Interlocked.Increment(ref owner._framesDropped);
            if (completedFrame is IDeckLinkMutableVideoFrame frame)
                lock (owner._poolSync) owner._freeFrames.Enqueue(frame);
        }

        public void ScheduledPlaybackHasStopped() { }
    }

    private bool OpenFor(VideoFrameData frame, OutputProfile profile)
    {
        try
        {
            CloseDeckLinkCore();
            var iterator = (IDeckLinkIterator)new CDeckLinkIterator();
            try
            {
                _device = null;
                for (var index = 0; index <= Math.Max(0, DeviceIndex); index++)
                {
                    iterator.Next(out var candidate);
                    if (candidate is null) break;
                    if (_device is not null) ReleaseCom(_device);
                    _device = candidate;
                }
            }
            finally
            {
                ReleaseCom(iterator);
            }
            if (_device is null)
                throw new InvalidOperationException("No DeckLink device found. Install Desktop Video and verify the card in Desktop Video Setup.");

            _output = (IDeckLinkOutput)_device;
            _output.GetDisplayModeIterator(out var modes);
            IDeckLinkDisplayMode? chosen = null;
            var compatible = new List<string>();
            try
            {
                while (true)
                {
                    modes.Next(out var mode);
                    if (mode is null) break;
                    if (mode.GetWidth() != frame.Width || mode.GetHeight() != frame.Height) { ReleaseCom(mode); continue; }

                    var modeFps = GetModeFrameRate(mode);
                    var modeScan = GetModeScanMode(mode);
                    compatible.Add($"{modeFps:0.###}fps {modeScan}");
                    var fpsMatch = modeFps <= 0 || Math.Abs(modeFps - profile.FramesPerSecond) < .08;
                    var scanMatch = ScanMatches(profile.ScanMode, modeScan);
                    if (fpsMatch && scanMatch) { chosen = mode; break; }
                    ReleaseCom(mode);
                }
            }
            finally
            {
                ReleaseCom(modes);
            }

            if (chosen is null)
            {
                var available = compatible.Count == 0 ? "no modes at this raster" : string.Join(", ", compatible.Distinct());
                throw new InvalidOperationException($"DeckLink does not expose {frame.Width}x{frame.Height} @ {profile.FramesPerSecond:0.###} fps · {profile.ScanMode}. Available: {available}.");
            }

            _width = chosen.GetWidth();
            _height = chosen.GetHeight();
            var displayMode = chosen.GetDisplayMode();
            var (duration, scale) = GetModeFrameDuration(chosen, profile.FramesPerSecond);
            ReleaseCom(chosen);
            _output.EnableVideoOutput(displayMode, _BMDVideoOutputFlags.bmdVideoOutputFlagDefault);
            if (EnableAudio)
                _output.EnableAudioOutput(_BMDAudioSampleRate.bmdAudioSampleRate48kHz,
                    _BMDAudioSampleType.bmdAudioSampleType16bitInteger, 2,
                    _BMDAudioOutputStreamType.bmdAudioOutputStreamContinuous);
            ConfigureKeyerCore();
            _scheduled = TryStartScheduledMode(duration, scale);
            if (!_scheduled)
            {
                _output.CreateVideoFrame(_width, _height, _width * 4, _BMDPixelFormat.bmdFormat8BitBGRA,
                    _BMDFrameFlags.bmdFrameFlagDefault, out _videoFrame);
            }
            _openProfileKey = ProfileKey(profile);
            LastError = "";
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            CloseDeckLinkCore();
            return false;
        }
    }

    /// <summary>Creates the card frame pool and arms scheduled playback. Returns false (sync fallback) if the card refuses.</summary>
    private bool TryStartScheduledMode(long frameDuration, long timeScale)
    {
        try
        {
            for (var i = 0; i < FramePoolSize; i++)
            {
                _output!.CreateVideoFrame(_width, _height, _width * 4, _BMDPixelFormat.bmdFormat8BitBGRA,
                    _BMDFrameFlags.bmdFrameFlagDefault, out var poolFrame);
                _frames.Add(poolFrame);
                lock (_poolSync) _freeFrames.Enqueue(poolFrame);
            }
            _callback = new CompletionCallback(this);
            _output!.SetScheduledFrameCompletionCallback(_callback);
            if (EnableAudio) _output.BeginAudioPreroll();
            _frameDuration = Math.Max(1, frameDuration);
            _timeScale = Math.Max(1, timeScale);
            ResetSchedule();
            return true;
        }
        catch (Exception ex)
        {
            LastError = "DeckLink scheduled playback unavailable, using immediate output: " + ex.Message;
            ReleaseFramePool();
            return false;
        }
    }

    private void ResetSchedule()
    {
        _started = false;
        _basePts = double.NaN;
        _baseSlot = 0;
        _lastVideoSlot = -1;
        _sequentialVideoSlot = 0;
        _sequentialAudioSlot = 0;
        _tickPts = double.NaN;
        _tickSlot = null;
        _scheduledSinceOpen = 0;
        _lastAudioBlock = null;
    }

    private static (long Duration, long Scale) GetModeFrameDuration(IDeckLinkDisplayMode mode, double fallbackFps)
    {
        mode.GetFrameRate(out var duration, out var scale);
        if (duration > 0 && scale > 0) return (duration, scale);
        // Fallback mirrors ChannelFormatSpec rational rates.
        if (Math.Abs(fallbackFps - 29.97) < .02) return (1001, 30000);
        if (Math.Abs(fallbackFps - 59.94) < .02) return (1001, 60000);
        if (Math.Abs(fallbackFps - 23.976) < .02) return (1001, 24000);
        return (1000, (long)Math.Round(Math.Max(1, fallbackFps) * 1000));
    }

    private static double GetModeFrameRate(IDeckLinkDisplayMode mode)
    {
        try
        {
            var method = typeof(IDeckLinkDisplayMode).GetMethod("GetFrameRate");
            if (method is null) return 0;
            object?[] args = [0L, 0L];
            method.Invoke(mode, args);
            var duration = Convert.ToInt64(args[0]);
            var scale = Convert.ToInt64(args[1]);
            return duration > 0 && scale > 0 ? scale / (double)duration : 0;
        }
        catch { return 0; }
    }

    private static string GetModeScanMode(IDeckLinkDisplayMode mode)
    {
        try
        {
            var method = typeof(IDeckLinkDisplayMode).GetMethod("GetFieldDominance");
            var raw = method?.Invoke(mode, null)?.ToString() ?? string.Empty;
            if (raw.Contains("Lower", StringComparison.OrdinalIgnoreCase)) return "Interlaced Lower First";
            if (raw.Contains("Upper", StringComparison.OrdinalIgnoreCase)) return "Interlaced Upper First";
            if (raw.Contains("Progressive", StringComparison.OrdinalIgnoreCase)) return "Progressive";
        }
        catch { }
        return "Unknown";
    }

    private static bool ScanMatches(string requested, string actual)
    {
        if (actual.Equals("Unknown", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.IsNullOrWhiteSpace(requested)) requested = "Progressive";
        return requested.Equals(actual, StringComparison.OrdinalIgnoreCase);
    }

    private static string ProfileKey(OutputProfile profile) => $"{profile.Preset}|{profile.Width}x{profile.Height}|{profile.FramesPerSecond:0.###}|{profile.ScanMode}|{profile.AlphaMode}";


    private void ConfigureKeyerCore()
    {
        _keyer = _device as IDeckLinkKeyer;
        if (KeyerMode == DeckLinkKeyerMode.Off)
        {
            try { _keyer?.Disable(); } catch { }
            return;
        }
        if (_keyer is null)
            throw new NotSupportedException($"DeckLink device {DeviceIndex + 1} does not expose IDeckLinkKeyer. Select a DeckLink model/port that supports {KeyerMode.ToString().ToLowerInvariant()} keying.");

        // tlbimp has emitted both BOOL-as-bool and BOOL-as-int signatures across SDK/toolchain
        // generations. Reflect the generated interop signature so FIX46 remains source-compatible.
        var enable = typeof(IDeckLinkKeyer).GetMethod("Enable") ?? throw new MissingMethodException("IDeckLinkKeyer.Enable");
        var parameters = enable.GetParameters();
        if (parameters.Length != 1)
            throw new MissingMethodException("IDeckLinkKeyer.Enable(bool/int isExternal) signature was not found in the generated DeckLink interop.");

        // Blackmagic keyer API uses one isExternal flag: false = internal key, true = external key.
        // Keep both modes explicit so configuration, diagnostics and build verification cannot
        // accidentally collapse Internal into a generic fallback branch.
        var externalKey = KeyerMode switch
        {
            DeckLinkKeyerMode.External => true,
            DeckLinkKeyerMode.Internal => false,
            _ => throw new InvalidOperationException("DeckLink keyer Enable called while keyer mode is Off.")
        };
        var parameterType = parameters[0].ParameterType;
        object external = parameterType == typeof(bool) ? externalKey : Convert.ChangeType(externalKey ? 1 : 0, parameterType);
        enable.Invoke(_keyer, [external]);
        var setLevel = typeof(IDeckLinkKeyer).GetMethod("SetLevel") ?? throw new MissingMethodException("IDeckLinkKeyer.SetLevel");
        var levelType = setLevel.GetParameters()[0].ParameterType;
        object level = levelType == typeof(byte) ? KeyerLevel : Convert.ChangeType(KeyerLevel, levelType);
        setLevel.Invoke(_keyer, [level]);
    }

    /// <summary>
    /// Card slot for one Program tick. Program frames and their audio share a cadence timestamp, so
    /// the first call for a new timestamp makes the buffer-level decision (drop/hold) and the second
    /// (same timestamp) reuses it. Sources without advancing timestamps (CG key engine, extra routes)
    /// are placed sequentially. Returns null when this tick is dropped to absorb clock drift.
    /// </summary>
    private long? ResolveSlot(double pts, bool audio)
    {
        var mapped = !double.IsNaN(_basePts);
        if (pts <= 0 && !mapped)
        {
            // No usable timestamps (CG key engine sends pts 0): place frames sequentially.
            var sequential = audio ? _sequentialAudioSlot : Math.Max(_lastVideoSlot + 1, _sequentialVideoSlot);
            var adjust = DriftAdjustment(sequential);
            if (adjust is null) return null;
            var placed = sequential + adjust.Value;
            if (audio) _sequentialAudioSlot = placed + 1; else _sequentialVideoSlot = placed + 1;
            return placed;
        }

        // Program ticks: audio and video of one tick share a timestamp and therefore one decision.
        if (mapped && !double.IsNaN(_tickPts) && Math.Abs(pts - _tickPts) < 1e-6) return _tickSlot;
        if (!mapped || pts < _tickPts)
        {
            // First timestamped tick, or the timestamps restarted: map from the next free slot.
            _basePts = pts;
            _baseSlot = Math.Max(_lastVideoSlot + 1, Math.Max(_sequentialVideoSlot, _sequentialAudioSlot));
        }
        _tickPts = pts;
        var slot = _baseSlot + (long)Math.Round((pts - _basePts) * (_timeScale / (double)_frameDuration));
        var shift = DriftAdjustment(slot);
        if (shift is null)
        {
            _baseSlot -= 1;
            _tickSlot = null;
            return null;
        }
        _baseSlot += shift.Value;
        _tickSlot = slot + shift.Value;
        return _tickSlot;
    }

    /// <summary>
    /// Frame-synchroniser decision for a candidate slot once playback runs: null = drop this tick
    /// (PC clock faster than the card); otherwise the number of slots to move it later (card faster
    /// than the PC, or the Program side stalled behind the card).
    /// </summary>
    private long? DriftAdjustment(long slot)
    {
        if (!_started) return 0;
        uint buffered = 0;
        try { _output!.GetBufferedVideoFrameCount(out buffered); } catch { }
        _output!.GetScheduledStreamTime(_timeScale, out var streamTime, out _);
        var playingSlot = streamTime / _frameDuration;
        if (slot <= playingSlot)
            return playingSlot + TargetBufferedFrames - slot; // stalled: land just ahead of the card again
        if (buffered >= HighWaterFrames)
        {
            _syncDrops++;
            return null;
        }
        if (buffered <= LowWaterFrames)
        {
            // The card holds the previous picture for one slot and the previous audio block is
            // repeated there, so neither picture nor sound has a hole.
            RepeatLastAudioBlock(slot);
            _syncHolds++;
            return 1;
        }
        return 0;
    }

    private long AudioStreamTimeForSlot(long slot) => slot * AudioSampleRate * _frameDuration / _timeScale;

    private unsafe void RepeatLastAudioBlock(long slot)
    {
        if (!EnableAudio || _lastAudioBlock is null) return;
        fixed (byte* p = _lastAudioBlock)
            _output!.ScheduleAudioSamples((IntPtr)p, (uint)(_lastAudioBlock.Length / 4), AudioStreamTimeForSlot(slot), AudioSampleRate, out _);
    }

    public void SendVideo(VideoFrameData frame, OutputProfile profile)
    {
        lock (_sync)
        {
            try
            {
                var (targetW, targetH) = profile.Resolve(frame.Width, frame.Height);
                var outgoing = _scaler.ScaleReusable(frame, targetW, targetH);
                if (_output is null || (_videoFrame is null && !_scheduled) || outgoing.Width != _width || outgoing.Height != _height || !string.Equals(_openProfileKey, ProfileKey(profile), StringComparison.Ordinal))
                {
                    if (!OpenFor(outgoing, profile)) return;
                }

                var rowBytes = checked(_width * 4);
                if (outgoing.Stride < rowBytes || outgoing.Bgra.Length < outgoing.Stride * _height)
                    throw new InvalidOperationException("DeckLink source BGRA buffer/stride is invalid.");

                if (!_scheduled)
                {
                    _videoFrame!.GetBytes(out var syncPtr);
                    CopyRows(outgoing, syncPtr, rowBytes);
                    _output!.DisplayVideoFrameSync(_videoFrame);
                    LastError = "";
                    return;
                }

                var slot = ResolveSlot(frame.PtsSeconds, audio: false);
                if (slot is null || slot.Value <= _lastVideoSlot) return;
                IDeckLinkMutableVideoFrame? cardFrame;
                lock (_poolSync) _freeFrames.TryDequeue(out cardFrame);
                if (cardFrame is null) { _poolStarved++; return; }
                cardFrame.GetBytes(out var ptr);
                CopyRows(outgoing, ptr, rowBytes);
                _output!.ScheduleVideoFrame(cardFrame, slot.Value * _frameDuration, _frameDuration, _timeScale);
                _lastVideoSlot = slot.Value;
                _sequentialVideoSlot = Math.Max(_sequentialVideoSlot, slot.Value + 1);

                if (!_started && ++_scheduledSinceOpen >= PrerollFrames)
                {
                    if (EnableAudio) _output.EndAudioPreroll();
                    // Start at the first queued frame so the preroll plays out in order.
                    _output.StartScheduledPlayback((_lastVideoSlot - PrerollFrames + 1) * _frameDuration, _timeScale, 1.0);
                    _started = true;
                }
                LastError = "";
            }
            catch (Exception ex)
            {
                LastError = "DeckLink video: " + ex.Message;
                CloseDeckLinkCore();
            }
        }
    }

    private static void CopyRows(VideoFrameData source, IntPtr destination, int rowBytes)
    {
        for (var y = 0; y < source.Height; y++)
            Marshal.Copy(source.Bgra, y * source.Stride, IntPtr.Add(destination, y * rowBytes), rowBytes);
    }

    public unsafe void SendAudio(AudioChunk chunk)
    {
        lock (_sync)
        {
            if (_output is null || chunk.Pcm16Stereo48k.Length == 0 || chunk.Channels <= 0) return;
            try
            {
                var sampleFrames = (uint)(chunk.Pcm16Stereo48k.Length / (chunk.Channels * 2));
                if (!_scheduled)
                {
                    fixed (byte* p = chunk.Pcm16Stereo48k)
                        _output.WriteAudioSamplesSync((IntPtr)p, sampleFrames, out _);
                    return;
                }

                var slot = ResolveSlot(chunk.PtsSeconds, audio: true);
                if (slot is null) return;
                fixed (byte* p = chunk.Pcm16Stereo48k)
                    _output.ScheduleAudioSamples((IntPtr)p, sampleFrames, AudioStreamTimeForSlot(slot.Value), AudioSampleRate, out _);
                _lastAudioBlock = chunk.Pcm16Stereo48k;
            }
            catch (Exception ex)
            {
                LastError = "DeckLink audio: " + ex.Message;
                CloseDeckLinkCore();
            }
        }
    }

    private void ReleaseFramePool()
    {
        lock (_poolSync) _freeFrames.Clear();
        foreach (var frame in _frames) ReleaseCom(frame);
        _frames.Clear();
    }

    private void CloseDeckLinkCore()
    {
        if (_output is not null && _scheduled)
        {
            try { if (_started) _output.StopScheduledPlayback(0, out _, _timeScale); } catch { }
            try { _output.SetScheduledFrameCompletionCallback(null!); } catch { }
            try { if (EnableAudio) _output.FlushBufferedAudioSamples(); } catch { }
        }
        try { _keyer?.Disable(); } catch { }
        _keyer = null;
        try { _output?.DisableAudioOutput(); } catch { }
        try { _output?.DisableVideoOutput(); } catch { }
        ReleaseFramePool();
        if (_videoFrame is not null) ReleaseCom(_videoFrame);
        _videoFrame = null;
        _callback = null;
        _scheduled = false;
        ResetSchedule();
        // _output and _keyer are the same COM object as _device; one final release frees it.
        if (_device is not null) ReleaseCom(_device, final: true);
        _output = null;
        _device = null;
        _width = _height = 0;
        _openProfileKey = string.Empty;
    }

    private static void ReleaseCom(object? comObject, bool final = false)
    {
        if (comObject is null || !Marshal.IsComObject(comObject)) return;
        try
        {
            if (final) Marshal.FinalReleaseComObject(comObject);
            else Marshal.ReleaseComObject(comObject);
        }
        catch { }
    }
#else
    public int DeviceIndex { get; set; }
    public DeckLinkKeyerMode KeyerMode { get; set; } = DeckLinkKeyerMode.Off;
    public byte KeyerLevel { get; set; } = 255;
    public bool EnableAudio { get; set; } = true;
    public bool IsSupportedBuild => false;
    public bool IsOpen => false;
    public string LastError { get; private set; } = "DeckLink SDK interop is not generated. Run tools\\Build-DeckLinkInterop.ps1 with Blackmagic Desktop Video SDK 16.0.";
    public string OutputStats => "DeckLink SDK interop not built";
    public void SendVideo(VideoFrameData frame, OutputProfile profile) { }
    public void SendAudio(AudioChunk chunk) { }
#endif

    public static IReadOnlyList<DeckLinkDeviceOption> EnumerateDevices()
    {
#if DECKLINK_SDK
        var devices = new List<DeckLinkDeviceOption>();
        IDeckLinkIterator? iterator = null;
        try
        {
            iterator = (IDeckLinkIterator)new CDeckLinkIterator();
            var index = 0;
            while (true)
            {
                iterator.Next(out var device);
                if (device is null) break;
                string model;
                string display;
                try { device.GetModelName(out model); } catch { model = $"DeckLink Device {index + 1}"; }
                try { device.GetDisplayName(out display); } catch { display = model; }
                devices.Add(new DeckLinkDeviceOption(index++, model, display));
                ReleaseCom(device);
            }
        }
        catch { }
        finally { ReleaseCom(iterator); }
        if (devices.Count > 0) return devices;
#endif
        return [new DeckLinkDeviceOption(0, "DeckLink hardware", "Desktop Video SDK scan unavailable")];
    }

    public static IReadOnlyList<DeckLinkDisplayModeOption> EnumerateDisplayModes(int deviceIndex = 0)
    {
#if DECKLINK_SDK
        var modes = new List<DeckLinkDisplayModeOption>();
        IDeckLinkIterator? iterator = null;
        IDeckLink? targetDevice = null;
        try
        {
            iterator = (IDeckLinkIterator)new CDeckLinkIterator();
            for (var index = 0; index <= Math.Max(0, deviceIndex); index++)
            {
                iterator.Next(out var candidate);
                if (candidate is null) break;
                if (targetDevice is not null) ReleaseCom(targetDevice);
                targetDevice = candidate;
            }
            if (targetDevice is IDeckLinkOutput output)
            {
                output.GetDisplayModeIterator(out var modeIterator);
                try
                {
                    while (true)
                    {
                        modeIterator.Next(out var mode);
                        if (mode is null) break;
                        mode.GetName(out var modeName);
                        var width = mode.GetWidth();
                        var height = mode.GetHeight();
                        var fps = GetModeFrameRate(mode);
                        var scan = GetModeScanMode(mode);
                        modes.Add(new DeckLinkDisplayModeOption(modeName, width, height, fps, scan));
                        ReleaseCom(mode);
                    }
                }
                finally { ReleaseCom(modeIterator); }
            }
        }
        catch { }
        finally
        {
            ReleaseCom(targetDevice);
            ReleaseCom(iterator);
        }
        if (modes.Count > 0) return modes;
#endif
        return
        [
            new DeckLinkDisplayModeOption("1080p50", 1920, 1080, 50.0, "Progressive"),
            new DeckLinkDisplayModeOption("1080i50", 1920, 1080, 25.0, "Interlaced Upper First"),
            new DeckLinkDisplayModeOption("1080p59.94", 1920, 1080, 59.94, "Progressive"),
            new DeckLinkDisplayModeOption("1080i59.94", 1920, 1080, 29.97, "Interlaced Upper First"),
            new DeckLinkDisplayModeOption("1080p60", 1920, 1080, 60.0, "Progressive"),
            new DeckLinkDisplayModeOption("1080p25", 1920, 1080, 25.0, "Progressive"),
            new DeckLinkDisplayModeOption("1080p29.97", 1920, 1080, 29.97, "Progressive"),
            new DeckLinkDisplayModeOption("1080p24", 1920, 1080, 24.0, "Progressive"),
            new DeckLinkDisplayModeOption("1080p23.98", 1920, 1080, 23.976, "Progressive"),
            new DeckLinkDisplayModeOption("720p50", 1280, 720, 50.0, "Progressive"),
            new DeckLinkDisplayModeOption("720p59.94", 1280, 720, 59.94, "Progressive"),
            new DeckLinkDisplayModeOption("720p60", 1280, 720, 60.0, "Progressive"),
            new DeckLinkDisplayModeOption("2160p50", 3840, 2160, 50.0, "Progressive"),
            new DeckLinkDisplayModeOption("2160p59.94", 3840, 2160, 59.94, "Progressive"),
            new DeckLinkDisplayModeOption("2160p60", 3840, 2160, 60.0, "Progressive"),
            new DeckLinkDisplayModeOption("2160p25", 3840, 2160, 25.0, "Progressive"),
            new DeckLinkDisplayModeOption("2160p29.97", 3840, 2160, 29.97, "Progressive")
        ];
    }

    public void Close()
    {
#if DECKLINK_SDK
        lock (_sync) CloseDeckLinkCore();
#endif
    }

    public void Dispose()
    {
        Close();
        _scaler.Dispose();
    }
}
