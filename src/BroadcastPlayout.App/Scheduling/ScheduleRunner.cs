using System.Runtime.CompilerServices;
using BroadcastPlayout.Engine;
using BroadcastPlayout.Models;

namespace BroadcastPlayout.Scheduling;

/// <summary>
/// Drives one channel's Program from its schedule. It lives only in the playout process of that
/// channel and costs nothing while automation is off (no timer, no work).
///
/// Two paths, one set of rules:
///   * At a clip end the engine asks <see cref="Take"/>: resume an interrupted item if it is still
///     scheduled, else continue the current sequence, else start what is scheduled now. These joins
///     are gapless (prerolled via <see cref="Peek"/>).
///   * A 250 ms tick only cuts when content must change now: a higher-priority event starts, the
///     current window ends, or a live retry is due. What was on air is remembered with its exact
///     position and resumed there afterwards.
/// </summary>
public sealed class ScheduleRunner : IPlayoutSequenceProvider, IDisposable
{
    private const int TickMilliseconds = 250;
    private const int MaxResumeDepth = 8;
    private static readonly TimeSpan LiveRetry = TimeSpan.FromSeconds(10);
    // A live that shows no picture this long after it was taken counts as lost (dead URL, refused or
    // hanging connect), so filler covers instead of a frozen/black Program.
    private static readonly TimeSpan LiveConnectTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan SourceRefresh = TimeSpan.FromMinutes(10);

    private sealed record ResumePoint(ScheduleOccurrence Occurrence, PlaylistItem? Item, TimeSpan Position);

    private sealed class SourceCursor
    {
        public List<ScheduledSource> Sources = [];
        public int Index;
        public string OccurrenceKey = string.Empty;
        public DateTime LoadedUtc;
        public int[] Order = [];
    }

    private readonly PlayoutEngine _engine;
    private readonly Func<DateTime> _clock;
    private readonly Action<string> _log;
    private readonly string _channelId;
    private readonly bool _persist;
    private readonly object _sync = new();
    private readonly ConditionalWeakTable<PlaylistItem, ScheduleOccurrence> _owned = new();
    private readonly Dictionary<Guid, SourceCursor> _cursors = [];
    private readonly List<ResumePoint> _resume = [];
    private readonly Dictionary<string, DateTime> _finished = [];
    private readonly Dictionary<string, DateTime> _retryAfter = [];
    private readonly Dictionary<string, DateTime> _manualCut = [];
    private readonly Queue<ScheduleOccurrence> _pendingSpots = new();
    private readonly CgScheduleRunner? _cg;
    private ChannelSchedule _schedule;
    private Timer? _timer;
    private IDisposable? _watch;
    private ScheduleOccurrence? _current;
    private PlaylistItem? _onAir;
    private long _onAirPositionTicks;
    private DateTime _onAirSince;
    private int _onAirHasPicture;
    private PlaylistItem? _peeked;
    private ScheduleOccurrence? _peekedOccurrence;
    private (SourceCursor Cursor, int Index, string OccurrenceKey)? _peekRewind;
    private bool _operatorOverride;
    private bool _manualStop;
    private DateTime _overrideSince;
    private int _ticking;
    private bool _disposed;

    /// <summary>Raised (on a worker thread) when the on-air schedule state changes.</summary>
    public event Action? StateChanged;

    private ScheduleRunner(PlayoutEngine engine, ChannelSchedule schedule, Action<string> log, Func<DateTime>? clock, bool persist, CgScheduleRunner? cg)
    {
        _engine = engine;
        _cg = cg;
        _schedule = schedule;
        _channelId = schedule.ChannelId;
        _log = log;
        _clock = clock ?? (() => DateTime.Now);
        _persist = persist;
        _engine.CurrentItemChanged += OnCurrentItemChanged;
        _engine.ProgressChanged += OnProgress;
    }

    /// <summary>The runner for a playout process: loads this channel's schedule and follows its changes.</summary>
    public static ScheduleRunner ForChannel(PlayoutEngine engine, string channelId, Action<string> log, CgScheduleRunner? cg = null)
    {
        var runner = new ScheduleRunner(engine, ChannelScheduleStore.Load(channelId), log, null, persist: true, cg);
        runner._watch = ChannelScheduleStore.Watch(channelId, runner.Reload);
        runner.ApplyEnabled();
        return runner;
    }

    /// <summary>A runner over an in-memory schedule (tests/tools); nothing is read from or written to the database.</summary>
    public static ScheduleRunner ForSchedule(PlayoutEngine engine, ChannelSchedule schedule, Action<string> log, Func<DateTime>? clock = null, CgScheduleRunner? cg = null)
    {
        schedule.EnsureDefaultFiller();
        var runner = new ScheduleRunner(engine, schedule, log, clock, persist: false, cg);
        runner.ApplyEnabled();
        return runner;
    }

    public string ChannelId => _channelId;
    public bool IsActive { get { lock (_sync) return _schedule.AutomationEnabled && !_disposed; } }
    public bool OperatorOverride { get { lock (_sync) return _operatorOverride; } }

    /// <summary>Short status for the operator UI.</summary>
    public string StatusText
    {
        get
        {
            lock (_sync)
            {
                if (!_schedule.AutomationEnabled) return "SCHEDULE OFF";
                if (_operatorOverride) return _manualStop ? "SCHEDULE · STOPPED BY OPERATOR" : "SCHEDULE · OPERATOR OVERRIDE";
                return _current is null ? "SCHEDULE ON" : $"SCHEDULE · {ScheduleRules.KindLabel(_current.Item.Kind)} · {_current.Item.Name}";
            }
        }
    }

    public ScheduleOccurrence? NextEvent { get { lock (_sync) return ScheduleResolver.NextEvent(_schedule, _clock()); } }

    // ---- Operator actions --------------------------------------------------------------------

    public void SetAutomationEnabled(bool enabled)
    {
        lock (_sync)
        {
            if (_schedule.AutomationEnabled == enabled) return;
            _schedule.AutomationEnabled = enabled;
            if (_persist) ChannelScheduleStore.Save(_schedule);
        }
        ApplyEnabled();
    }

    /// <summary>Operator pressed STOP: automation waits for the next scheduled event or Resume.</summary>
    public void NotifyManualStop()
    {
        lock (_sync)
        {
            if (!_schedule.AutomationEnabled) return;
            _operatorOverride = true;
            _manualStop = true;
            _overrideSince = _clock();
        }
        Notify("SCHEDULE · STOPPED BY OPERATOR (resumes at next scheduled event)");
    }

    /// <summary>Hand the channel back to the schedule immediately.</summary>
    public void ResumeAutomation()
    {
        lock (_sync)
        {
            if (!_schedule.AutomationEnabled) return;
            _operatorOverride = false;
            _manualStop = false;
            CutTo(_clock(), pushCurrent: false);
        }
        Notify("SCHEDULE · AUTOMATION RESUMED");
    }

    /// <summary>Ends a live that runs "until manual cut" (or any live early).</summary>
    public bool CutLive()
    {
        lock (_sync)
        {
            if (_current is null || _current.Item.Kind != ScheduleKind.Live) return false;
            var now = _clock();
            _manualCut[_current.Key] = now;
            Notify($"SCHEDULE · LIVE CUT · {_current.Item.Name}");
            CutTo(now, pushCurrent: false);
            return true;
        }
    }

    // ---- Lifecycle -----------------------------------------------------------------------------

    private void ApplyEnabled()
    {
        bool enabled;
        lock (_sync)
        {
            enabled = _schedule.AutomationEnabled && !_disposed;
            if (enabled && _timer is null)
                _timer = new Timer(_ => Tick(), null, 0, TickMilliseconds);
            else if (!enabled && _timer is not null)
            {
                _timer.Dispose();
                _timer = null;
                _resume.Clear();
                _pendingSpots.Clear();
                ClearPeek();
                _current = null;
                _operatorOverride = false;
                _cg?.StopAll();
            }
            else return;
        }
        Notify(enabled ? "SCHEDULE AUTOMATION ON" : "SCHEDULE AUTOMATION OFF");
    }

    private void Reload()
    {
        lock (_sync)
        {
            if (_disposed) return;
            var enabledBefore = _schedule.AutomationEnabled;
            _schedule = ChannelScheduleStore.Load(_channelId);
            ClearPeek();
            var ids = _schedule.Items.Select(x => x.Id).ToHashSet();
            foreach (var removed in _cursors.Keys.Where(id => !ids.Contains(id)).ToList()) _cursors.Remove(removed);
            foreach (var cursor in _cursors.Values) cursor.LoadedUtc = DateTime.MinValue; // re-read sources on next use
            if (enabledBefore == _schedule.AutomationEnabled) { Notify("SCHEDULE · RELOADED"); return; }
        }
        ApplyEnabled();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
        _watch?.Dispose();
        _engine.CurrentItemChanged -= OnCurrentItemChanged;
        _engine.ProgressChanged -= OnProgress;
    }

    // ---- Engine events ----------------------------------------------------------------------------

    private void OnCurrentItemChanged(PlaylistItem? item)
    {
        var overrideStarted = false;
        lock (_sync)
        {
            _onAir = item;
            Interlocked.Exchange(ref _onAirPositionTicks, 0);
            Interlocked.Exchange(ref _onAirHasPicture, 0);
            _onAirSince = _clock();
            if (item is null || !_schedule.AutomationEnabled) return;
            if (_owned.TryGetValue(item, out var occurrence))
            {
                _current = occurrence;
            }
            else if (!_operatorOverride || _manualStop)
            {
                // Something the operator (or the legacy scheduler) started: hands off until the next event.
                _operatorOverride = true;
                _manualStop = false;
                _overrideSince = _clock();
                overrideStarted = true;
            }
        }
        if (overrideStarted) Notify("SCHEDULE · OPERATOR OVERRIDE (resumes at next scheduled event)");
        else StateChanged?.Invoke();
    }

    private void OnProgress(TimeSpan position)
    {
        Interlocked.Exchange(ref _onAirPositionTicks, position.Ticks);
        // The engine reports the start offset before any picture; only advancing time means frames.
        if (position > TimeSpan.Zero) Interlocked.Exchange(ref _onAirHasPicture, 1);
    }

    // ---- Tick: cuts that must happen now -------------------------------------------------------

    private void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        try
        {
            lock (_sync)
            {
                if (_disposed || !_schedule.AutomationEnabled) return;
                var now = _clock();
                TickCore(now);
                // Overlays run with the schedule even while an operator has taken the video.
                _cg?.Tick(_schedule, now, _onAir);
            }
        }
        catch (Exception ex) { _log("SCHEDULE ERROR · " + ex.Message); }
        finally { Interlocked.Exchange(ref _ticking, 0); }
    }

    /// <summary>Runs one scheduling decision at <paramref name="now"/> (exposed for deterministic tests).</summary>
    public void TickNow()
    {
        lock (_sync)
        {
            if (_disposed || !_schedule.AutomationEnabled) return;
            var now = _clock();
            TickCore(now);
            _cg?.Tick(_schedule, now, _onAir);
        }
    }

    private void TickCore(DateTime now)
    {
        Prune(now);
        var desired = SelectTarget(now);
        if (desired is null) return;

        if (_operatorOverride)
        {
            var eventStarted = desired.Item.Kind != ScheduleKind.Filler && desired.Start > _overrideSince;
            var operatorContentEnded = !_manualStop && !_engine.IsPlaying;
            if (!eventStarted && !operatorContentEnded) return;
            _operatorOverride = false;
            _manualStop = false;
            _log("SCHEDULE · AUTOMATION RESUMED");
            CutTo(now, pushCurrent: false);
            return;
        }

        if (!_engine.IsPlaying || _current is null || _onAir is null)
        {
            CutTo(now, pushCurrent: false);
            return;
        }

        if (_current.Item.Kind == ScheduleKind.Live && _onAir.IsLiveSource &&
            Volatile.Read(ref _onAirHasPicture) == 0 && now - _onAirSince > LiveConnectTimeout)
        {
            _retryAfter[_current.Key] = now + LiveRetry;
            _log($"SCHEDULE · LIVE FEED LOST · {_current.Item.Name} · no picture in {LiveConnectTimeout.TotalSeconds:0} s · retry in {LiveRetry.TotalSeconds:0} s");
            CutTo(now, pushCurrent: false);
            return;
        }

        if (desired.Key == _current.Key) return;

        // A filler slot change waits for the end of the current song (the engine asks Take()).
        if (desired.Item.Kind == ScheduleKind.Filler && _current.Item.Kind == ScheduleKind.Filler) return;

        if (desired.Priority > _current.Priority || (desired.Priority == _current.Priority && desired.Item.Kind != ScheduleKind.Filler))
        {
            if (desired.Item.Kind == ScheduleKind.SpotAd && desired.Item.SpotMode == SpotCutMode.AfterCurrentClip)
            {
                if (!_pendingSpots.Any(x => x.Key == desired.Key) && !_finished.ContainsKey(desired.Key))
                {
                    _pendingSpots.Enqueue(desired);
                    _finished[desired.Key] = desired.End ?? now.AddDays(1); // the queue owns it now
                    ClearPeek();
                    _log($"SCHEDULE · SPOT QUEUED AFTER CURRENT CLIP · {desired.Item.Name}");
                }
                return;
            }
            _log($"SCHEDULE · {ScheduleRules.KindLabel(desired.Item.Kind)} · {desired.Item.Name}");
            CutTo(now, pushCurrent: true);
            return;
        }

        // Lower priority wins only because the current window is over (news end, live end time ...).
        // Programme/Movie content is owed in full and ends by itself.
        if (!IsOwedContent(_current.Item.Kind) && _current.End is { } end && now >= end)
        {
            MarkFinished(_current, now);
            CutTo(now, pushCurrent: false);
        }
    }

    private void CutTo(DateTime now, bool pushCurrent)
    {
        if (pushCurrent && _current is not null && _onAir is not null && !_onAir.IsLiveSource)
            PushResume(new ResumePoint(_current, _onAir, TimeSpan.FromTicks(Interlocked.Read(ref _onAirPositionTicks))));
        ClearPeek();
        var item = NextItem(now);
        if (item is null) return;
        _ = _engine.PlayAsync([item], 0, null, this);
        StateChanged?.Invoke();
    }

    // ---- Provider: what follows a finished clip ------------------------------------------------

    /// <summary>
    /// Prepares the next clip ahead of time so the engine can preroll it. Only the plain cases are
    /// prerolled (continue or start a sequence); a resume or a queued spot is decided at the boundary.
    /// The only state a peek touches is one cursor step, which is rewound if the peek is discarded.
    /// </summary>
    public PlaylistItem? Peek()
    {
        lock (_sync)
        {
            if (_disposed || !_schedule.AutomationEnabled || _operatorOverride) return null;
            if (_peeked is not null) return _peeked;
            if (_pendingSpots.Count > 0) return null;
            // Decide for the moment the current clip ends, so a slot change is seen in advance.
            var now = _clock();
            var remaining = _onAir is null ? TimeSpan.Zero : _onAir.Duration - TimeSpan.FromTicks(Interlocked.Read(ref _onAirPositionTicks));
            var at = remaining > TimeSpan.Zero ? now + remaining : now;
            var desired = SelectTarget(at);
            if (desired is null || _resume.Any(r => r.Occurrence.Key == desired.Key)) return null;
            if (desired.Item.Kind == ScheduleKind.Live) return null; // a live is connected at its start, never early

            var cursor = CursorFor(desired.Item, at);
            _peekRewind = (cursor, cursor.Index, cursor.OccurrenceKey);
            var item = _current?.Key == desired.Key ? ContinueOccurrence(desired, at) : StartOccurrence(desired, at);
            if (item is null) { ClearPeek(); return null; }
            _peeked = Own(item, desired);
            _peekedOccurrence = desired;
            return _peeked;
        }
    }

    public PlaylistItem? Take()
    {
        lock (_sync)
        {
            if (_disposed || !_schedule.AutomationEnabled) return null;
            var now = _clock();
            if (_peeked is not null && _peekedOccurrence is not null && _pendingSpots.Count == 0 &&
                SelectTarget(now)?.Key == _peekedOccurrence.Key && !_resume.Any(r => r.Occurrence.Key == _peekedOccurrence.Key))
            {
                var item = _peeked;
                _current = _peekedOccurrence;
                _peeked = null;
                _peekedOccurrence = null;
                _peekRewind = null;
                return item;
            }
            ClearPeek();
            return NextItem(now);
        }
    }

    private PlaylistItem? NextItem(DateTime now)
    {
        // Spots waiting for a clip boundary go first; what was playing continues after them.
        while (_pendingSpots.TryDequeue(out var queued))
        {
            if (_current is not null) PushResume(new ResumePoint(_current, null, TimeSpan.Zero));
            // A spot that waited for the clip boundary still airs in full: its window moves to now.
            var spot = queued with { Start = now, End = now + ((queued.End ?? queued.Start) - queued.Start) };
            _current = spot;
            if (StartOccurrence(spot, now) is { } spotItem) return Own(spotItem, spot);
        }

        for (var attempt = 0; attempt < 12; attempt++)
        {
            var desired = SelectTarget(now);
            if (desired is null) return null;

            var resumeIndex = _resume.FindLastIndex(r => r.Occurrence.Key == desired.Key);
            if (resumeIndex >= 0)
            {
                var point = _resume[resumeIndex];
                _resume.RemoveAt(resumeIndex);
                _current = desired;
                var resumed = point.Item is null ? null : ScheduleSources.ResumeAt(point.Item, point.Position);
                if (resumed is not null)
                {
                    _log($"SCHEDULE · RESUME · {desired.Item.Name} at {point.Position:hh\\:mm\\:ss}");
                    return Own(resumed, desired);
                }
                if (ContinueOccurrence(desired, now) is { } next) return Own(next, desired);
                MarkFinished(desired, now);
                continue;
            }

            PlaylistItem? item;
            if (_current?.Key == desired.Key)
            {
                if (desired.Item.Kind == ScheduleKind.Live)
                {
                    // The feed ended inside its window: retry shortly, filler covers meanwhile.
                    _retryAfter[desired.Key] = now + LiveRetry;
                    _log($"SCHEDULE · LIVE FEED LOST · {desired.Item.Name} · retry in {LiveRetry.TotalSeconds:0} s");
                    continue;
                }
                item = ContinueOccurrence(desired, now);
            }
            else
            {
                _current = desired;
                item = StartOccurrence(desired, now);
            }
            if (item is not null) return Own(item, desired);
            MarkFinished(desired, now);
        }
        return null;
    }

    private PlaylistItem Own(PlaylistItem item, ScheduleOccurrence occurrence)
    {
        _owned.AddOrUpdate(item, occurrence);
        return item;
    }

    private PlaylistItem? StartOccurrence(ScheduleOccurrence occurrence, DateTime now)
    {
        var item = occurrence.Item;
        if (item.Kind == ScheduleKind.Live)
        {
            var length = occurrence.End is { } end ? end - now : TimeSpan.FromHours(24);
            return length > TimeSpan.FromSeconds(1) ? ScheduleSources.BuildLive(item, length) : null;
        }
        var cursor = CursorFor(item, now);
        // Fillers keep cycling their songs across slots and days; every other kind starts at its first source.
        if (item.Kind != ScheduleKind.Filler && cursor.OccurrenceKey != occurrence.Key)
        {
            cursor.Index = 0;
            cursor.OccurrenceKey = occurrence.Key;
        }
        return ContinueOccurrence(occurrence, now);
    }

    private PlaylistItem? ContinueOccurrence(ScheduleOccurrence occurrence, DateTime now)
    {
        var item = occurrence.Item;
        if (item.Kind == ScheduleKind.Live) return null;
        var cursor = CursorFor(item, now);
        if (cursor.Sources.Count == 0) return null;
        for (var tries = 0; tries < cursor.Sources.Count; tries++)
        {
            if (cursor.Index >= cursor.Sources.Count)
            {
                if (item.Kind != ScheduleKind.Filler) return null; // sequence finished
                cursor.Index = 0;
                if (item.Shuffle) cursor.Order = Shuffled(cursor.Sources.Count);
            }
            var position = cursor.Order.Length == cursor.Sources.Count ? cursor.Order[cursor.Index] : cursor.Index;
            cursor.Index++;
            if (ScheduleSources.Build(cursor.Sources[position], item) is { } built) return built;
        }
        return null;
    }

    private SourceCursor CursorFor(ChannelScheduleItem item, DateTime now)
    {
        if (!_cursors.TryGetValue(item.Id, out var cursor)) _cursors[item.Id] = cursor = new SourceCursor();
        if (DateTime.UtcNow - cursor.LoadedUtc > SourceRefresh)
        {
            cursor.Sources = ScheduleSources.Expand(item.Sources);
            cursor.LoadedUtc = DateTime.UtcNow;
            cursor.Order = item.Shuffle ? Shuffled(cursor.Sources.Count) : [];
            if (cursor.Index > cursor.Sources.Count) cursor.Index = 0;
        }
        return cursor;
    }

    private static int[] Shuffled(int count)
    {
        var order = Enumerable.Range(0, count).ToArray();
        Random.Shared.Shuffle(order);
        return order;
    }

    // ---- State helpers ----------------------------------------------------------------------------

    private ScheduleOccurrence? Resolve(DateTime now)
        => ScheduleResolver.ResolveVideo(_schedule, now, o =>
            _finished.ContainsKey(o.Key) ||
            _manualCut.ContainsKey(o.Key) ||
            (_retryAfter.TryGetValue(o.Key, out var retry) && retry > now));

    private static bool IsOwedContent(ScheduleKind kind) => kind is ScheduleKind.Programme or ScheduleKind.Movie;

    /// <summary>
    /// What should be on air: the schedule's answer, except that Programme/Movie content that already
    /// started is owed in full and continues (or resumes) unless something more important is
    /// scheduled. Its window only decides when it starts. Used by the tick, Peek, Take and NextItem.
    /// </summary>
    private ScheduleOccurrence? SelectTarget(DateTime now)
    {
        CleanResume(now);
        var desired = Resolve(now);
        var owedPoint = _resume.LastOrDefault(r => IsOwedContent(r.Occurrence.Item.Kind))?.Occurrence;
        var owedCurrent = _current is not null && IsOwedContent(_current.Item.Kind) && !_finished.ContainsKey(_current.Key) ? _current : null;
        if (owedPoint is not null && (desired is null || owedPoint.Priority > desired.Priority)) desired = owedPoint;
        if (owedCurrent is not null && (desired is null || owedCurrent.Priority > desired.Priority)) desired = owedCurrent;
        return desired;
    }

    /// <summary>
    /// Drops resume points that no longer apply: finished occurrences, time-bound ones (filler slot,
    /// news, live, spot) whose window has ended, and anything older than 12 hours.
    /// </summary>
    private void CleanResume(DateTime now)
        => _resume.RemoveAll(r =>
            _finished.ContainsKey(r.Occurrence.Key) ||
            r.Occurrence.Start < now.AddHours(-12) ||
            (!IsOwedContent(r.Occurrence.Item.Kind) && r.Occurrence.End is { } end && end <= now));

    private void PushResume(ResumePoint point)
    {
        _resume.RemoveAll(r => r.Occurrence.Key == point.Occurrence.Key);
        _resume.Add(point);
        if (_resume.Count > MaxResumeDepth) _resume.RemoveAt(0);
    }

    private void MarkFinished(ScheduleOccurrence occurrence, DateTime now) => _finished[occurrence.Key] = occurrence.End ?? now.AddDays(1);

    private void ClearPeek()
    {
        // A discarded peek must not skip a clip: put its cursor back where it was.
        if (_peekRewind is { } rewind)
        {
            rewind.Cursor.Index = rewind.Index;
            rewind.Cursor.OccurrenceKey = rewind.OccurrenceKey;
        }
        _peekRewind = null;
        _peeked = null;
        _peekedOccurrence = null;
    }

    /// <summary>Bounded bookkeeping: entries for occurrences long over are dropped.</summary>
    private void Prune(DateTime now)
    {
        foreach (var key in _finished.Where(x => x.Value < now.AddHours(-1)).Select(x => x.Key).ToList()) _finished.Remove(key);
        foreach (var key in _retryAfter.Where(x => x.Value < now.AddMinutes(-5)).Select(x => x.Key).ToList()) _retryAfter.Remove(key);
        foreach (var key in _manualCut.Where(x => x.Value < now.AddDays(-2)).Select(x => x.Key).ToList()) _manualCut.Remove(key);
    }

    private void Notify(string message)
    {
        try { _log(message); } catch { }
        try { StateChanged?.Invoke(); } catch { }
    }
}
