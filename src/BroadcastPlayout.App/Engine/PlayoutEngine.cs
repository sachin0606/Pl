using System.Diagnostics;
using BroadcastPlayout.Ffmpeg;
using BroadcastPlayout.Models;
using BroadcastPlayout.Services;

namespace BroadcastPlayout.Engine;

/// <summary>
/// Single-channel playout coordinator with composite/multipart event support.
/// A PlaylistItem may contain many trim parts that all reference the same source file.
/// The parts are decoded in order and appear to automation as one continuous event.
/// </summary>
public sealed class PlayoutEngine : IDisposable
{
    private readonly object _sync = new();
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private long _sessionId;
    private readonly ManualResetEventSlim _pauseGate = new(initialState: true);
    private bool _isPaused;
    private VideoFrameData? _lastVideoFrame;
    private bool _disposed;
    private readonly Lazy<EnterpriseMamCatalog> _mamCatalog = new(() => new EnterpriseMamCatalog());

    public event Action<VideoFrameData>? VideoFrame;
    public event Action<AudioChunk>? AudioFrame;
    // Raw programme-source audio used only for pre-fader/pre-mute metering.
    // This remains active even when an item or the master bus is muted.
    public event Action<AudioChunk>? AudioMeterFrame;
    public event Action<PlaylistItem?>? CurrentItemChanged;
    public event Action<TimeSpan>? ProgressChanged;
    public event Action<string>? StatusChanged;
    public event Action<PlaylistItem>? ControlEventTriggered;

    public bool IsPlaying { get { lock (_sync) return _runTask is { IsCompleted: false }; } }
    public bool IsPaused { get { lock (_sync) return _isPaused && _runTask is { IsCompleted: false }; } }

    /// <param name="firstItemOffset">Offset inside the logical event, not an absolute source-media timestamp.</param>
    /// <param name="continuation">Optional source of further items once <paramref name="items"/> run out
    /// (schedule automation). Its items join gaplessly and are prerolled like playlist items.</param>
    public Task PlayAsync(IReadOnlyList<PlaylistItem> items, int startIndex, TimeSpan? firstItemOffset = null, IPlayoutSequenceProvider? continuation = null)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PlayoutEngine));
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0) return Task.CompletedTask;
        // The decode/emit pumps sleep in 1-6 ms steps; the 15.6 ms default would make them oversleep.
        RealtimeScheduling.EnsureTimerResolution();

        CancellationTokenSource source;
        Task task;
        long session;
        lock (_sync)
        {
            CancelCurrentSource_NoThrow();
            EndPauseAccounting();
            _isPaused = false;
            _pauseGate.Set();
            source = new CancellationTokenSource();
            _cts = source;
            session = ++_sessionId;
            var snapshot = items.ToArray();
            var safeIndex = Math.Clamp(startIndex, 0, items.Count - 1);
            task = Task.Run(() => RunPlaylistAsync(snapshot, safeIndex, firstItemOffset, session, continuation, source.Token));
            _runTask = task;
        }
        _ = CleanupSessionAsync(task, source, session);
        return task;
    }

    private async Task CleanupSessionAsync(Task task, CancellationTokenSource source, long session)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception ex) { SetStatus(session, "ERROR: " + ex.Message); }
        finally
        {
            lock (_sync)
            {
                // Always clear the CTS reference that belongs to this source before disposing it.
                // Stop() advances _sessionId; the old FIX39 condition also required the old session
                // id to match, which could leave _cts pointing at an already-disposed source.
                if (ReferenceEquals(_cts, source)) _cts = null;
                if (_sessionId == session && ReferenceEquals(_runTask, task)) _runTask = null;
            }
            source.Dispose();
        }
    }

    private bool IsCurrentSession(long session) { lock (_sync) return _sessionId == session; }

    // Output/UI subscribers must never be able to terminate the decoder loop. A graphics,
    // NDI, monitor or UI presentation exception is isolated to that subscriber/frame.
    private static void InvokeSafely<T>(Action<T>? handlers, T value)
    {
        if (handlers is null) return;
        foreach (Action<T> handler in handlers.GetInvocationList())
        {
            try { handler(value); } catch { }
        }
    }

    private void SetStatus(long session, string status) { if (IsCurrentSession(session)) InvokeSafely(StatusChanged, status); }
    private void SetCurrentItem(long session, PlaylistItem? item) { if (IsCurrentSession(session)) InvokeSafely(CurrentItemChanged, item); }
    private void SetProgress(long session, TimeSpan progress) { if (IsCurrentSession(session)) InvokeSafely(ProgressChanged, progress < TimeSpan.Zero ? TimeSpan.Zero : progress); }
    private void EmitVideo(VideoFrameData frame) => InvokeSafely(VideoFrame, frame);
    private void EmitAudio(AudioChunk chunk) => InvokeSafely(AudioFrame, chunk);
    private void EmitAudioMeter(AudioChunk chunk) => InvokeSafely(AudioMeterFrame, chunk);
    private void EmitControl(PlaylistItem item) => InvokeSafely(ControlEventTriggered, item);

    private async Task RunPlaylistAsync(PlaylistItem[] items, int index, TimeSpan? firstItemOffset, long session, IPlayoutSequenceProvider? continuation, CancellationToken ct)
    {
        try
        {
            SetStatus(session, "PLAYING");
            // Stopwatch time at which the previous media segment ended; the next segment starts its
            // clock exactly there so consecutive items join frame-accurately (0 = start now).
            long continueAt = 0;
            var firstPlay = true;
            for (var i = index; i < items.Length; i++)
            {
                if (ct.IsCancellationRequested || !IsCurrentSession(session)) return;
                var item = items[i];
                SetCurrentItem(session, item);
                // The TAKE offset applies to the first play only; a loop or block loop that returns
                // to this item replays it from the start.
                var offset = firstPlay && firstItemOffset.HasValue ? ClampOffset(item, firstItemOffset.Value) : TimeSpan.Zero;
                firstPlay = false;
                SetProgress(session, offset);
                var itemIndex = i;
                var result = await PlayItemAsync(item, offset, session, continueAt, () => PredictNextSegment(items, itemIndex, continuation), ct).ConfigureAwait(false);
                if (!result.Completed || ct.IsCancellationRequested || !IsCurrentSession(session)) return;
                continueAt = result.EndDue;

                if (item.IsLoop)
                {
                    if (item.LoopCount <= 0 || item.CurrentLoopIteration < item.LoopCount - 1)
                    {
                        item.CurrentLoopIteration++;
                        i--;
                        continue;
                    }
                    item.CurrentLoopIteration = 0;
                }
                else if (item.IsBlockLoop)
                {
                    var nextIdx = i + 1;
                    // BlockName is a marker on the first row. Following rows inherit it,
                    // so the next marker (or rundown end) is the real end of the block.
                    var isEndOfBlock = nextIdx >= items.Length || !string.IsNullOrWhiteSpace(items[nextIdx].BlockName);
                    if (isEndOfBlock)
                    {
                        var blockStart = i;
                        while (blockStart > 0 && string.IsNullOrWhiteSpace(items[blockStart].BlockName)) blockStart--;
                        i = blockStart - 1;
                        continue;
                    }
                }
            }

            // Schedule automation: keep asking for the next item. Nothing is accumulated here, so a
            // channel can run for months on one session without growing memory.
            while (continuation is not null && !ct.IsCancellationRequested && IsCurrentSession(session))
            {
                var item = continuation.Take();
                if (item is null) break;
                SetCurrentItem(session, item);
                var offset = firstPlay && firstItemOffset.HasValue ? ClampOffset(item, firstItemOffset.Value) : TimeSpan.Zero;
                firstPlay = false;
                SetProgress(session, offset);
                var result = await PlayItemAsync(item, offset, session, continueAt, () => FirstSegmentOf(continuation.Peek()), ct).ConfigureAwait(false);
                if (!result.Completed || ct.IsCancellationRequested || !IsCurrentSession(session)) return;
                continueAt = result.EndDue;
            }
            SetStatus(session, "READY");
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested && IsCurrentSession(session)) SetStatus(session, "ERROR: " + ex.Message);
        }
        finally
        {
            DiscardPreroll();
            SetCurrentItem(session, null);
        }
    }

    private static TimeSpan ClampOffset(PlaylistItem item, TimeSpan requested)
    {
        if (requested < TimeSpan.Zero) return TimeSpan.Zero;
        if (item.IsLiveSource) return TimeSpan.Zero;
        if (item.Duration <= TimeSpan.Zero) return TimeSpan.Zero;
        if (requested >= item.Duration)
        {
            var safe = item.Duration - TimeSpan.FromMilliseconds(40);
            return safe < TimeSpan.Zero ? TimeSpan.Zero : safe;
        }
        return requested;
    }

    /// <summary>Outcome of a played item or segment. <c>EndDue</c> is where the next segment may start (0 = unknown).</summary>
    private readonly record struct PlayResult(bool Completed, long EndDue);

    private async Task<PlayResult> PlayItemAsync(PlaylistItem item, TimeSpan eventOffset, long session, long continueAt, Func<SegmentKey?> nextItemSegment, CancellationToken ct)
    {
        // Enforce MAM policy at the actual playout boundary, not only in UI. This also protects
        // auto-follow events and remote API/automation TAKE commands. If an online master was
        // archived/offlined but an archive copy is available, restore it automatically before air.
        if (!item.IsLiveSource && !item.IsControlEvent && !item.IsCgEvent && !string.IsNullOrWhiteSpace(item.FilePath))
        {
            var mam = _mamCatalog.Value;
            var asset = mam.GetByPath(item.FilePath);
            if (asset is not null)
            {
                mam.AssertMayUse(item.FilePath, DateTime.UtcNow);
                if (!File.Exists(item.FilePath) && !string.IsNullOrWhiteSpace(asset.ArchivePath) && File.Exists(asset.ArchivePath))
                    await mam.RestoreAsync(asset.AssetId, item.FilePath, ct).ConfigureAwait(false);
            }
        }

        if (item.IsControlEvent)
            return new PlayResult(await PlayControlEventAsync(item, session, ct).ConfigureAwait(false), 0);

        if (item.IsCgEvent)
            return new PlayResult(await PlayCgEventAsync(item, eventOffset, session, ct).ConfigureAwait(false), 0);

        var parts = item.GetPlaybackParts();
        if (parts.Count == 0) return new PlayResult(true, continueAt);

        var startPartIndex = 0;
        var sourceStart = parts[0].InPoint;
        if (!item.IsLiveSource)
        {
            var mapped = item.MapEventOffset(eventOffset);
            startPartIndex = Math.Max(0, parts.ToList().FindIndex(x => x.Index == mapped.Part.Index));
            sourceStart = mapped.SourcePosition;
        }

        var endDue = continueAt;
        for (var i = startPartIndex; i < parts.Count; i++)
        {
            if (ct.IsCancellationRequested || !IsCurrentSession(session)) return new PlayResult(false, 0);
            var part = parts[i];
            var start = i == startPartIndex ? sourceStart : part.InPoint;
            var nextIndex = i + 1;
            Func<SegmentKey?> next = nextIndex < parts.Count
                ? () => item.IsLiveSource ? null : new SegmentKey(item, nextIndex, parts[nextIndex].InPoint)
                : nextItemSegment;
            var result = await PlaySegmentAsync(item, part, i, start, session, endDue, next, ct).ConfigureAwait(false);
            if (!result.Completed) return new PlayResult(false, 0);
            endDue = result.EndDue;
        }
        return new PlayResult(!ct.IsCancellationRequested && IsCurrentSession(session), endDue);
    }

    // ---- Gapless preroll -------------------------------------------------------------------
    // While a segment plays, the next one is opened, seeked and decoded up to its first picture in
    // the background. At the boundary its clock starts where the previous segment ended, so the
    // first prerolled frame lands in the very next frame slot instead of after a ~200 ms open.

    /// <summary>Identifies one playable segment; a preroll is used only for an exact match.</summary>
    private sealed record SegmentKey(PlaylistItem Item, int PartIndex, TimeSpan StartAt)
    {
        public bool Matches(PlaylistItem item, int partIndex, TimeSpan startAt)
            => ReferenceEquals(Item, item) && PartIndex == partIndex && StartAt == startAt;
    }

    /// <summary>Opened, seeked decoders for one segment, optionally primed with its first picture.</summary>
    private sealed class SegmentInputs(SharedDemuxer? demux, VideoDecoder video, AudioDecoder? audio, string? audioError) : IDisposable
    {
        public SharedDemuxer? Demux { get; } = demux;
        public VideoDecoder Video { get; } = video;
        public AudioDecoder? Audio { get; private set; } = audio;
        public string? AudioError { get; } = audioError;
        public bool Primed { get; private set; }
        public VideoFrameData? FirstFrame { get; private set; }

        /// <summary>Decodes up to the first picture that would be shown. A null frame after priming means the segment is empty.</summary>
        public void Prime(PlaylistItem item, TimeSpan startAt)
        {
            while (Video.TryRead(out var frame))
            {
                if (!item.IsLiveSource && frame.PtsSeconds < startAt.TotalSeconds - 0.050) continue;
                FirstFrame = frame;
                break;
            }
            Primed = true;
        }

        /// <summary>Hands the audio decoder to the caller, which then owns its disposal.</summary>
        public AudioDecoder? DetachAudio() { var a = Audio; Audio = null; return a; }

        public void Dispose()
        {
            try { Audio?.Dispose(); } catch { }
            try { Video.Dispose(); } catch { }
            try { Demux?.Dispose(); } catch { }
        }
    }

    private static bool HasConfiguredAudio(PlaylistItem item)
        => !item.SourceKind.Equals("Screen", StringComparison.OrdinalIgnoreCase)
           && (!item.SourceKind.Equals("DirectShow", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(item.AudioDevice));

    private static SegmentInputs OpenSegmentInputs(PlaylistItem item, TimeSpan startAt)
    {
        var wantAudio = HasConfiguredAudio(item);
        // Files and network URLs are opened once and demuxed for both decoders; anything else, or a
        // shared open that fails, falls back to separate inputs.
        var demux = SharedDemuxer.TryOpen(item, wantAudio);
        VideoDecoder? video = null;
        // A freshly opened input already sits at its start. Seeking there anyway resets the
        // container's codec-priming handling (MP4/AAC edit lists), which made the decoder drop the
        // first real audio frame and left a 21 ms hole at every join; it also costs open time.
        var seek = !item.IsLiveSource && startAt > TimeSpan.Zero;
        try
        {
            video = new VideoDecoder(item, demux);
            if (seek)
            {
                demux?.Seek(startAt);
                video.Seek(startAt);
            }

            AudioDecoder? audio = null;
            string? audioError = null;
            if (wantAudio && (demux is null || demux.HasAudio))
            {
                try { audio = new AudioDecoder(item, demux); if (seek) audio.Seek(startAt); }
                catch (Exception ex)
                {
                    audio?.Dispose(); audio = null;
                    audioError = ex.Message;
                }
            }
            demux?.Start();
            return new SegmentInputs(demux, video, audio, audioError);
        }
        catch
        {
            video?.Dispose();
            demux?.Dispose();
            throw;
        }
    }

    private static SegmentKey? PredictNextSegment(PlaylistItem[] items, int i, IPlayoutSequenceProvider? continuation)
    {
        // Mirrors RunPlaylistAsync's sequencing before it mutates loop counters.
        var item = items[i];
        int next;
        if (item.IsLoop)
            next = item.LoopCount <= 0 || item.CurrentLoopIteration < item.LoopCount - 1 ? i : i + 1;
        else if (item.IsBlockLoop && (i + 1 >= items.Length || !string.IsNullOrWhiteSpace(items[i + 1].BlockName)))
        {
            next = i;
            while (next > 0 && string.IsNullOrWhiteSpace(items[next].BlockName)) next--;
        }
        else
            next = i + 1;
        return next < items.Length ? FirstSegmentOf(items[next]) : FirstSegmentOf(continuation?.Peek());
    }

    private static SegmentKey? FirstSegmentOf(PlaylistItem? candidate)
    {
        if (candidate is null) return null;
        // Control/CG events need no decoder; live sources would buffer stale data while waiting.
        if (candidate.IsControlEvent || candidate.IsCgEvent || candidate.IsLiveSource || string.IsNullOrWhiteSpace(candidate.FilePath))
            return null;
        if (candidate.SourceKind.Equals("NDI", StringComparison.OrdinalIgnoreCase) ||
            candidate.SourceKind.Equals("DirectShow", StringComparison.OrdinalIgnoreCase) ||
            candidate.SourceKind.Equals("Screen", StringComparison.OrdinalIgnoreCase))
            return null;
        var parts = candidate.GetPlaybackParts();
        return parts.Count == 0 ? null : new SegmentKey(candidate, 0, parts[0].InPoint);
    }

    private readonly object _prerollSync = new();
    private (SegmentKey Key, long Session, Task<SegmentInputs?> Task)? _preroll;

    private void BeginPreroll(SegmentKey? key, long session, CancellationToken ct)
    {
        if (key is null) return;
        lock (_prerollSync)
        {
            if (_preroll is { } existing && existing.Session == session && existing.Key.Matches(key.Item, key.PartIndex, key.StartAt)) return;
            DisposeWhenDone(_preroll?.Task);
            _preroll = (key, session, Task.Run(() => Prepare(key, ct)));
        }
    }

    private static SegmentInputs? Prepare(SegmentKey key, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return null;
        SegmentInputs? inputs = null;
        try
        {
            inputs = OpenSegmentInputs(key.Item, key.StartAt);
            inputs.Prime(key.Item, key.StartAt);
            return inputs;
        }
        catch (Exception ex)
        {
            // The boundary falls back to a normal open, which reports the real error on air.
            inputs?.Dispose();
            AuditLogService.Write("PREROLL_FAILED", key.Item.Title, ex.Message);
            return null;
        }
    }

    /// <summary>Returns the prerolled inputs for exactly this segment, or null (any other preroll is discarded).</summary>
    private async Task<SegmentInputs?> TakePrerollAsync(PlaylistItem item, int partIndex, TimeSpan startAt, long session)
    {
        Task<SegmentInputs?>? task = null;
        lock (_prerollSync)
        {
            if (_preroll is { } p)
            {
                if (p.Session == session && p.Key.Matches(item, partIndex, startAt)) task = p.Task;
                else DisposeWhenDone(p.Task);
                _preroll = null;
            }
        }
        if (task is null) return null;
        // A preroll still opening is never slower than opening from scratch.
        try { return await task.ConfigureAwait(false); }
        catch { return null; }
    }

    private void DiscardPreroll()
    {
        lock (_prerollSync)
        {
            DisposeWhenDone(_preroll?.Task);
            _preroll = null;
        }
    }

    private static void DisposeWhenDone(Task<SegmentInputs?>? task)
    {
        task?.ContinueWith(t => { if (t.Status == TaskStatus.RanToCompletion) t.Result?.Dispose(); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task<PlayResult> PlaySegmentAsync(PlaylistItem item, MediaPlaybackPart part, int partIndex, TimeSpan startAt, long session, long continueAt, Func<SegmentKey?> nextSegment, CancellationToken ct)
    {
        var inputs = await TakePrerollAsync(item, partIndex, startAt, session).ConfigureAwait(false);
        var prerolled = inputs is not null;
        inputs ??= OpenSegmentInputs(item, startAt);
        var video = inputs.Video;
        var demux = inputs.Demux;
        AuditLogService.Write("DECODER_OPEN", item.Title, $"{item.DecoderPreference} -> {video.DecoderBackend}{(demux is null ? "" : " · shared input")}{(prerolled ? " · prerolled" : "")}");
        if (inputs.AudioError is not null)
        {
            AuditLogService.Write("AUDIO_DECODER_ERROR", item.Title, inputs.AudioError);
            SetStatus(session, "AUDIO DECODER: " + inputs.AudioError);
        }
        // The audio pump owns the audio decoder from here (it may outlive this method by the bounded tail wait).
        var audio = inputs.DetachAudio();

        // One clock for both streams: audio and video samples are due relative to the same anchor,
        // so a difference between their first timestamps can no longer become a lip-sync offset.
        // Separately opened live inputs (DirectShow/screen devices) have unrelated timestamp bases, so
        // their audio keeps its own anchor; a shared live input has one base and shares the clock.
        var clock = new SegmentClock(this);
        var audioClock = item.IsLiveSource && demux is null ? new SegmentClock(this) : clock;
        var audioStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var audioTask = audio is null ? Task.CompletedTask : Task.Run(() => PumpAudio(audio, item, part, startAt, session, audioClock, audioStop.Token));

        // Join the previous segment exactly when it ends. Starting a little late is harmless: frames
        // keep their exact due times and the Program cadence renders one frame + 30 ms behind, so a
        // frame up to ~33 ms late still reaches its own slot even at 60p. Later joins start now.
        var anchorAt = continueAt != 0 && continueAt >= Stopwatch.GetTimestamp() - Stopwatch.Frequency * 30 / 1000 ? continueAt : 0;
        long endDue = 0;
        var primed = inputs.Primed ? inputs.FirstFrame : null;
        var primedEmpty = inputs.Primed && inputs.FirstFrame is null;
        var prerollRequested = false;

        try
        {
            var firstPts = double.NaN;
            while (!ct.IsCancellationRequested && IsCurrentSession(session))
            {
                WaitWhilePaused(session, ct);
                if (ct.IsCancellationRequested || !IsCurrentSession(session)) return new PlayResult(false, 0);
                VideoFrameData frame;
                if (primed is not null) { frame = primed; primed = null; }
                else if (primedEmpty || !video.TryRead(out frame)) break;
                if (!item.IsLiveSource && frame.PtsSeconds < startAt.TotalSeconds - 0.050) continue;
                if (double.IsNaN(firstPts)) firstPts = frame.PtsSeconds;

                double segmentElapsed;
                if (item.IsLiveSource)
                {
                    segmentElapsed = Math.Max(0, frame.PtsSeconds - firstPts);
                    if (item.Duration > TimeSpan.Zero && segmentElapsed >= item.Duration.TotalSeconds) break;
                }
                else
                {
                    if (frame.PtsSeconds >= part.OutPoint.TotalSeconds) break;
                    segmentElapsed = Math.Max(0, frame.PtsSeconds - part.InPoint.TotalSeconds);
                }

                // Anchor on the first frame that is actually shown (after open/seek/GOP decode), at the
                // previous segment's end when joining gaplessly, so the segment never starts with a
                // burst of "late" frames.
                clock.Anchor(frame.PtsSeconds, anchorAt);
                while (!ct.IsCancellationRequested && IsCurrentSession(session))
                {
                    WaitWhilePaused(session, ct);
                    var delay = clock.SecondsUntil(frame.PtsSeconds);
                    // Program used to sleep for the whole remaining frame interval. At 50/60 fps
                    // a normal Windows scheduler overshoot was enough to create visible cadence
                    // jitter. Sleep coarsely first, then finish against the Stopwatch deadline.
                    if (delay <= 0.0015) break;
                    if (delay > 0.006)
                    {
                        var coarseMs = Math.Max(1.0, Math.Min(5.0, (delay - 0.003) * 1000.0));
                        await Task.Delay(TimeSpan.FromMilliseconds(coarseMs)).ConfigureAwait(false);
                    }
                    else
                    {
                        Thread.Sleep(1);
                    }
                }
                if (ct.IsCancellationRequested || !IsCurrentSession(session)) return new PlayResult(false, 0);

                SetProgress(session, item.IsLiveSource ? TimeSpan.FromSeconds(segmentElapsed) : part.EventOffset + TimeSpan.FromSeconds(segmentElapsed));
                var due = clock.DueTimestamp(frame.PtsSeconds);
                frame = frame with { DueTimestamp = due };
                _lastVideoFrame = frame;
                EmitVideo(frame);
                var fps = frame.FramesPerSecond > 0 ? Math.Clamp(frame.FramesPerSecond, 1, 120) : 25.0;
                endDue = due + (long)(Stopwatch.Frequency / fps);

                // Start preparing the next segment once this one is safely on screen.
                if (!prerollRequested)
                {
                    prerollRequested = true;
                    BeginPreroll(nextSegment(), session, ct);
                }

                // The last picture of a file segment is on air: finish now instead of decoding one
                // more frame just to find it past the out-point (with frame-threaded decoders that
                // cost ~45 ms and made the join late).
                if (!item.IsLiveSource && part.OutPoint.TotalSeconds - frame.PtsSeconds <= 0.75 / fps) break;
            }
            return new PlayResult(!ct.IsCancellationRequested && IsCurrentSession(session), endDue);
        }
        finally
        {
            audioStop.Cancel();
            if (audio is null)
            {
                audioStop.Dispose();
            }
            else
            {
                // Some capture/network demuxers can remain inside av_read_frame after video
                // has already completed. Do not hold the whole Program playlist forever
                // waiting for an audio tail that ignored cancellation.
                var finished = await Task.WhenAny(audioTask, Task.Delay(750)).ConfigureAwait(false);
                if (ReferenceEquals(finished, audioTask))
                {
                    try { await audioTask.ConfigureAwait(false); }
                    catch (Exception ex) { if (!ct.IsCancellationRequested && IsCurrentSession(session)) SetStatus(session, "AUDIO ERROR: " + ex.Message); }
                    audio.Dispose();
                    audioStop.Dispose();
                }
                else
                {
                    try { audio.Dispose(); } catch { }
                    _ = audioTask.ContinueWith(t =>
                    {
                        try { _ = t.Exception; } catch { }
                        try { audioStop.Dispose(); } catch { }
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }
            // Closing decoders (threads, GPU device) and the demuxer takes tens of ms and nothing uses
            // them after the last picture, so it happens off the boundary between segments.
            var closedInputs = inputs;
            _ = Task.Run(closedInputs.Dispose);
        }
    }

    // Audio is handed to the Program audio clock this far ahead of its due time. The cadence reads
    // it by due time, so early delivery costs no sync and absorbs decode/scheduling jitter.
    private const double AudioLeadSeconds = 0.060;

    private void PumpAudio(AudioDecoder audio, PlaylistItem item, MediaPlaybackPart part, TimeSpan startAt, long session, SegmentClock clock, CancellationToken ct)
    {
        try
        {
            var firstPts = double.NaN;
            while (!ct.IsCancellationRequested && IsCurrentSession(session))
            {
                WaitWhilePaused(session, ct);
                if (ct.IsCancellationRequested || !IsCurrentSession(session)) return;
                if (!audio.TryRead(out var chunk)) return;
                if (!item.IsLiveSource && chunk.PtsSeconds < startAt.TotalSeconds - 0.100) continue;
                if (double.IsNaN(firstPts)) firstPts = chunk.PtsSeconds;
                if (item.IsLiveSource)
                {
                    if (item.Duration > TimeSpan.Zero && chunk.PtsSeconds - firstPts >= item.Duration.TotalSeconds) return;
                }
                else if (chunk.PtsSeconds >= part.OutPoint.TotalSeconds) return;

                // A shared clock is anchored by the first shown video frame. Wait for it (bounded,
                // so an item whose video never starts still plays its audio).
                if (!clock.IsAnchored)
                {
                    clock.WaitAnchored(item.IsLiveSource ? 0 : 2000, ct);
                    clock.Anchor(chunk.PtsSeconds);
                }
                // A file segment's audio is exactly [first shown picture, out-point). Codec priming
                // or tails outside that window would overlap the neighbouring segment at a join.
                if (!item.IsLiveSource)
                {
                    var trimmed = TrimToWindow(chunk, clock.AnchorPts, part.OutPoint.TotalSeconds);
                    if (trimmed is null) continue;
                    chunk = trimmed;
                }
                var mediaPts = chunk.PtsSeconds + item.AudioDelayMs / 1000.0;
                while (!ct.IsCancellationRequested && IsCurrentSession(session))
                {
                    WaitWhilePaused(session, ct);
                    var wait = clock.SecondsUntil(mediaPts) - AudioLeadSeconds;
                    if (wait <= 0.001) break;
                    if (ct.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(Math.Min(20.0, wait * 1000.0)))) return;
                }
                if (ct.IsCancellationRequested || !IsCurrentSession(session)) return;
                chunk = chunk with { DueTimestamp = clock.DueTimestamp(mediaPts) };
                EmitAudioMeter(chunk);
                EmitAudio(AudioMasterProcessor.Apply(chunk, item.AudioGainPercent, item.AudioMuted));
            }
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested && IsCurrentSession(session)) SetStatus(session, "AUDIO ERROR: " + ex.Message);
        }
    }

    /// <summary>Sample-accurately keeps only the part of the chunk inside [from, to); null when nothing remains.</summary>
    private static AudioChunk? TrimToWindow(AudioChunk chunk, double fromPts, double toPts)
    {
        var bytesPerFrame = Math.Max(1, chunk.Channels) * sizeof(short);
        var frames = chunk.Pcm16Stereo48k.Length / bytesPerFrame;
        var rate = Math.Max(1, chunk.SampleRate);
        var drop = (int)Math.Clamp(Math.Round((fromPts - chunk.PtsSeconds) * rate), 0, frames);
        var keepEnd = (int)Math.Clamp(Math.Round((toPts - chunk.PtsSeconds) * rate), 0, frames);
        if (drop == 0 && keepEnd == frames) return chunk;
        if (keepEnd <= drop) return null;
        var pcm = new byte[(keepEnd - drop) * bytesPerFrame];
        Buffer.BlockCopy(chunk.Pcm16Stereo48k, drop * bytesPerFrame, pcm, 0, pcm.Length);
        return chunk with { Pcm16Stereo48k = pcm, PtsSeconds = chunk.PtsSeconds + drop / (double)rate };
    }

    private async Task<bool> PlayControlEventAsync(PlaylistItem item, long session, CancellationToken ct)
    {
        var action = (item.EventType ?? "NOTE").Trim().ToUpperInvariant();
        SetProgress(session, TimeSpan.Zero);
        EmitControl(item);
        switch (action)
        {
            case "STOP":
                SetStatus(session, "STOPPED · CONTROL EVENT");
                await Task.Delay(40).ConfigureAwait(false);
                return false;
            case "PAUSE":
                Pause();
                WaitWhilePaused(session, ct);
                return !ct.IsCancellationRequested && IsCurrentSession(session);
            case "RESUME":
                Resume();
                return true;
            case "CUETONE":
                await EmitToneAsync(1000, 0, 850, session, ct, .34).ConfigureAwait(false);
                return !ct.IsCancellationRequested && IsCurrentSession(session);
            case "DIALTONE":
                await EmitToneAsync(350, 440, item.DialToneMilliseconds, session, ct, item.DialToneLevelPercent / 100.0).ConfigureAwait(false);
                return !ct.IsCancellationRequested && IsCurrentSession(session);
            case "DTMF":
                await EmitDtmfAsync(item.DtmfSequence, item.DtmfToneMilliseconds, item.DtmfGapMilliseconds, item.DtmfLevelPercent, session, ct).ConfigureAwait(false);
                return !ct.IsCancellationRequested && IsCurrentSession(session);
            case "NEXT":
            case "PLAY":
            case "NOTE":
            default:
                await Task.Delay(40).ConfigureAwait(false);
                return !ct.IsCancellationRequested && IsCurrentSession(session);
        }
    }

    private async Task EmitToneAsync(double f1, double f2, int milliseconds, long session, CancellationToken ct, double level = .275)
    {
        await EmitToneCoreAsync(f1, f2, milliseconds, Math.Clamp(level, .01, 1.0), ct,
            () => IsCurrentSession(session)).ConfigureAwait(false);
    }

    private async Task EmitToneCoreAsync(double f1, double f2, int milliseconds, double level, CancellationToken ct, Func<bool>? keepRunning = null)
    {
        const int sampleRate = 48000;
        const int chunkMs = 20;
        var total = Math.Max(1, (int)Math.Ceiling(Math.Max(20, milliseconds) / (double)chunkMs));
        var amplitude = Math.Clamp(level, .01, 1.0) * short.MaxValue * .92;
        // Chunks get exact contiguous due times so the Program audio clock plays them gap-free.
        var toneStart = Stopwatch.GetTimestamp() + (long)(AudioLeadSeconds * Stopwatch.Frequency);
        var chunkTicks = Stopwatch.Frequency * chunkMs / 1000;
        for (var c = 0; c < total && !ct.IsCancellationRequested && (keepRunning?.Invoke() ?? true); c++)
        {
            var due = toneStart + c * chunkTicks;
            var wait = (due - (long)(AudioLeadSeconds * Stopwatch.Frequency) - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency;
            if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(wait), ct).ConfigureAwait(false);
            var samples = sampleRate * chunkMs / 1000;
            var pcm = new byte[samples * 4];
            for (var i = 0; i < samples; i++)
            {
                var t = (c * samples + i) / (double)sampleRate;
                var wave = Math.Sin(2 * Math.PI * f1 * t);
                if (f2 > 0) wave = (wave + Math.Sin(2 * Math.PI * f2 * t)) * .5;
                // A short edge ramp avoids clicks when operators test DTMF/dial tone on air.
                var absoluteSample = c * samples + i;
                var totalSamples = total * samples;
                var ramp = Math.Min(1.0, Math.Min(absoluteSample / (sampleRate * .006), (totalSamples - absoluteSample) / (sampleRate * .006)));
                var sample = (short)Math.Clamp((int)(wave * amplitude * Math.Max(0, ramp)), short.MinValue, short.MaxValue);
                pcm[i * 4] = (byte)(sample & 0xff); pcm[i * 4 + 1] = (byte)((sample >> 8) & 0xff);
                pcm[i * 4 + 2] = pcm[i * 4]; pcm[i * 4 + 3] = pcm[i * 4 + 1];
            }
            var toneChunk = new AudioChunk(pcm, c * chunkMs / 1000.0) { DueTimestamp = due };
            EmitAudioMeter(toneChunk);
            EmitAudio(toneChunk);
        }
        // Return once the last chunk has actually played so callers' gaps/sequencing stay audible.
        var endWait = (toneStart + total * chunkTicks - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency;
        if (endWait > 0 && !ct.IsCancellationRequested) await Task.Delay(TimeSpan.FromMilliseconds(endWait), ct).ConfigureAwait(false);
    }

    private async Task EmitDialToneAsync(string? notes, long session, CancellationToken ct)
    {
        // North-American/commonly-recognised dial tone: 350 Hz + 440 Hz. Notes may override
        // duration and level: "DIALTONE: duration=3000;level=55".
        var duration = ParseOptionInt(notes, "duration", 2500, 100, 30000);
        var levelPercent = ParseOptionInt(notes, "level", 55, 1, 100);
        await EmitToneAsync(350, 440, duration, session, ct, levelPercent / 100.0).ConfigureAwait(false);
    }

    private async Task EmitDtmfAsync(string? sequence, int toneMs, int gapMs, int levelPercent, long session, CancellationToken ct)
    {
        var digits = new string((sequence ?? "1234#").Where(ch => "0123456789*#ABCDabcd".Contains(ch)).Select(char.ToUpperInvariant).ToArray());
        if (digits.Length == 0) digits = "1234#";
        toneMs = Math.Clamp(toneMs, 45, 2000);
        gapMs = Math.Clamp(gapMs, 20, 2000);
        levelPercent = Math.Clamp(levelPercent, 1, 100);
        foreach (var d in digits)
        {
            var (low, high) = DtmfFrequencies(d);
            await EmitToneAsync(low, high, toneMs, session, ct, levelPercent / 100.0).ConfigureAwait(false);
            if (gapMs > 0) await Task.Delay(gapMs, ct).ConfigureAwait(false);
        }
    }

    public async Task PlayDiagnosticToneAsync(double f1, double f2, int milliseconds, double level = .5, CancellationToken ct = default)
    {
        await EmitToneCoreAsync(f1, f2, Math.Clamp(milliseconds, 50, 30000), Math.Clamp(level, .01, 1.0), ct).ConfigureAwait(false);
    }

    public async Task PlayDiagnosticDtmfAsync(string? sequence, int toneMilliseconds = 120, int gapMilliseconds = 55, double level = .72, CancellationToken ct = default)
    {
        var digits = new string((sequence ?? "1234#").Where(ch => "0123456789*#ABCDabcd".Contains(ch)).Select(char.ToUpperInvariant).ToArray());
        if (digits.Length == 0) digits = "1234#";
        toneMilliseconds = Math.Clamp(toneMilliseconds, 45, 2000);
        gapMilliseconds = Math.Clamp(gapMilliseconds, 20, 2000);
        foreach (var d in digits)
        {
            var (low, high) = DtmfFrequencies(d);
            await EmitToneCoreAsync(low, high, toneMilliseconds, Math.Clamp(level, .01, 1.0), ct).ConfigureAwait(false);
            if (gapMilliseconds > 0) await Task.Delay(gapMilliseconds, ct).ConfigureAwait(false);
        }
    }

    private static int ParseOptionInt(string? text, string key, int fallback, int min, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            if (!part[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            if (int.TryParse(part[(eq + 1)..].Trim(), out var value)) return Math.Clamp(value, min, max);
        }
        return fallback;
    }

    private static (double Low, double High) DtmfFrequencies(char c) => c switch
    {
        '1' => (697,1209), '2' => (697,1336), '3' => (697,1477), 'A' => (697,1633),
        '4' => (770,1209), '5' => (770,1336), '6' => (770,1477), 'B' => (770,1633),
        '7' => (852,1209), '8' => (852,1336), '9' => (852,1477), 'C' => (852,1633),
        '*' => (941,1209), '0' => (941,1336), '#' => (941,1477), 'D' => (941,1633),
        _ => (697,1209)
    };

    private async Task<bool> PlayCgEventAsync(PlaylistItem item, TimeSpan eventOffset, long session, CancellationToken ct)
    {
        var duration = item.Duration > TimeSpan.Zero ? item.Duration : TimeSpan.FromSeconds(10);
        var start = eventOffset < TimeSpan.Zero ? TimeSpan.Zero : eventOffset > duration ? duration : eventOffset;
        var fps = item.SourceFrameRate > 0 ? Math.Clamp(item.SourceFrameRate, 1, 60) : 25.0;
        var frameDelay = TimeSpan.FromSeconds(1.0 / fps);
        var baseFrame = _lastVideoFrame ?? CreateBlackFrame(1280, 720, fps);
        var clock = Stopwatch.StartNew();
        var pausedSeconds = 0.0;

        while (!ct.IsCancellationRequested && IsCurrentSession(session))
        {
            pausedSeconds += WaitWhilePaused(session, ct);
            if (ct.IsCancellationRequested || !IsCurrentSession(session)) return false;

            var elapsed = start.TotalSeconds + Math.Max(0, clock.Elapsed.TotalSeconds - pausedSeconds);
            if (elapsed >= duration.TotalSeconds) break;
            SetProgress(session, TimeSpan.FromSeconds(elapsed));
            var frame = new VideoFrameData(baseFrame.Bgra, baseFrame.Width, baseFrame.Height, baseFrame.Stride, elapsed, baseFrame.FrameRateNumerator, baseFrame.FrameRateDenominator);
            EmitVideo(frame);
            await Task.Delay(frameDelay).ConfigureAwait(false);
        }
        return !ct.IsCancellationRequested && IsCurrentSession(session);
    }

    private static VideoFrameData CreateBlackFrame(int width, int height, double fps)
    {
        var stride = width * 4;
        var numerator = Math.Max(1, (int)Math.Round(fps * 1000));
        return new VideoFrameData(new byte[stride * height], width, height, stride, 0, numerator, 1000);
    }

    // Total time spent paused, in Stopwatch ticks. Segment clocks shift every due time by the pause
    // accumulated since they were anchored, so audio and video resume together.
    private long _pausedTotalTicks;
    private long _pauseStartedTicks;

    // Caller holds _sync.
    private void EndPauseAccounting()
    {
        if (_isPaused && _pauseStartedTicks != 0) _pausedTotalTicks += Stopwatch.GetTimestamp() - _pauseStartedTicks;
        _pauseStartedTicks = 0;
    }

    private long PausedTotalTicks
    {
        get
        {
            lock (_sync)
            {
                var total = _pausedTotalTicks;
                if (_isPaused && _pauseStartedTicks != 0) total += Stopwatch.GetTimestamp() - _pauseStartedTicks;
                return total;
            }
        }
    }

    /// <summary>Maps media timestamps of one playback segment onto the Stopwatch timeline.</summary>
    private sealed class SegmentClock(PlayoutEngine engine)
    {
        private readonly object _gate = new();
        private readonly ManualResetEventSlim _anchored = new(false);
        private long _anchorTicks;
        private double _anchorPts;
        private long _pausedAtAnchor;

        public bool IsAnchored => _anchored.IsSet;

        /// <summary>Media timestamp of the first shown picture (valid once anchored).</summary>
        public double AnchorPts { get { lock (_gate) return _anchorPts; } }

        /// <summary>
        /// Starts the clock: <paramref name="pts"/> is due at <paramref name="atTicks"/> (0 = now).
        /// Later calls are ignored.
        /// </summary>
        public void Anchor(double pts, long atTicks = 0)
        {
            lock (_gate)
            {
                if (_anchored.IsSet) return;
                _anchorPts = pts;
                _pausedAtAnchor = engine.PausedTotalTicks;
                _anchorTicks = atTicks != 0 ? atTicks : Stopwatch.GetTimestamp();
                _anchored.Set();
            }
        }

        public void WaitAnchored(int timeoutMs, CancellationToken ct)
        {
            if (timeoutMs <= 0) return;
            try { _anchored.Wait(timeoutMs, ct); } catch (OperationCanceledException) { }
        }

        public long DueTimestamp(double pts)
            => _anchorTicks + (long)((pts - _anchorPts) * Stopwatch.Frequency) + (engine.PausedTotalTicks - _pausedAtAnchor);

        public double SecondsUntil(double pts)
            => (DueTimestamp(pts) - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency;
    }

    private double WaitWhilePaused(long session, CancellationToken ct)
    {
        if (_pauseGate.IsSet) return 0;
        var wait = Stopwatch.StartNew();
        try { _pauseGate.Wait(ct); }
        catch (OperationCanceledException) { return 0; }
        return IsCurrentSession(session) ? wait.Elapsed.TotalSeconds : 0;
    }

    public void Pause()
    {
        long session;
        lock (_sync)
        {
            if (_runTask is null || _runTask.IsCompleted || _isPaused) return;
            _isPaused = true;
            _pauseStartedTicks = Stopwatch.GetTimestamp();
            _pauseGate.Reset();
            session = _sessionId;
        }
        SetStatus(session, "PAUSED");
    }

    public void Resume()
    {
        long session;
        lock (_sync)
        {
            if (_runTask is null || _runTask.IsCompleted || !_isPaused) return;
            EndPauseAccounting();
            _isPaused = false;
            _pauseGate.Set();
            session = _sessionId;
        }
        SetStatus(session, "PLAYING");
    }

    public void TogglePause()
    {
        if (IsPaused) Resume(); else Pause();
    }

    private void CancelCurrentSource_NoThrow()
    {
        var source = _cts;
        if (source is null) return;
        try { source.Cancel(); }
        catch (ObjectDisposedException)
        {
            // A completed session may race its asynchronous cleanup. Never let an operator
            // TAKE/PLAY key turn that harmless race into an unhandled dispatcher exception.
            if (ReferenceEquals(_cts, source)) _cts = null;
        }
    }

    public void Stop()
    {
        long session;
        lock (_sync)
        {
            CancelCurrentSource_NoThrow();
            EndPauseAccounting();
            _isPaused = false;
            _pauseGate.Set();
            session = ++_sessionId;
        }
        SetStatus(session, "STOPPED"); SetCurrentItem(session, null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
