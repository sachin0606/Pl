using BroadcastPlayout.Models;

namespace BroadcastPlayout.Services;

/// <summary>Play or stop one Now/Next graphic on a Program CG layer.</summary>
public sealed record NowNextCommand(bool Play, int Layer, string TemplateName);

/// <summary>
/// Times the Now/Next graphics for whatever is on air, using the profile for its category:
/// the info graphic (title, credits, progress, request) for the whole item, and the NEXT / LATER box
/// at the start and end, continuously, or every N minutes. NEXT and LATER are the next two airings
/// from the schedule (or the playlist when the schedule is off).
///
/// <see cref="Prepare"/> runs on the UI thread whenever the item, the upcoming list or the settings
/// change; it does the lookups and builds a timing plan. If the upcoming list changes while the box is
/// on screen, the box content updates in place. <see cref="Tick"/> runs on the engine thread every frame
/// and only compares the position with the plan.
/// </summary>
public sealed class NowNextRunner
{
    private const string Next = "NEXT", Later = "LATER", Cycle = "CYCLE";

    private sealed record Window(double Start, double End, string Tag);
    private sealed record Plan(PlaylistItem Item, AiringInfo Now, IReadOnlyList<AiringInfo> Upcoming, Window[] Windows, NowNextProfile Profile, NowNextSettings Settings);

    private readonly object _gate = new();
    private readonly Func<NowNextSettings> _settings;
    private readonly Func<bool> _videoPreview;
    private readonly Action<NowNextCommand> _send;
    private Plan? _plan;
    private bool _infoOnAir;
    private bool _boxOnAir;
    private Window? _shown;
    private string _shownTag = string.Empty;

    /// <param name="videoPreview">Video inside the box (the "Next Item Video" option); off shows a title card.</param>
    /// <param name="send">Called from either thread; the host marshals it to the CG layer stack.</param>
    public NowNextRunner(Func<NowNextSettings> settings, Func<bool> videoPreview, Action<NowNextCommand> send)
    {
        _settings = settings;
        _videoPreview = videoPreview;
        _send = send;
    }

    /// <summary>True when Now/Next owns the graphics for this item (the classic auto lower thirds stay off).</summary>
    public bool Handles(PlaylistItem? item)
    {
        var plan = Volatile.Read(ref _plan);
        return plan is not null && item is not null && ReferenceEquals(plan.Item, item);
    }

    /// <summary>UI thread. Cheap when nothing changed.</summary>
    public void Prepare(PlaylistItem? current, IReadOnlyList<AiringInfo> upcoming)
    {
        var settings = _settings();
        var profile = settings.Enabled && current is not null && !current.IsCgEvent && !current.IsControlEvent
            ? settings.ProfileFor(current.Category) : null;
        lock (_gate)
        {
            var previous = _plan;
            if (profile is null || (!profile.ShowInfo && profile.BoxMode == NowNextBoxMode.Off))
            {
                Volatile.Write(ref _plan, null);
                StopAll(settings);
                return;
            }

            var sameItem = previous is not null && ReferenceEquals(previous.Item, current);
            if (sameItem && ReferenceEquals(previous!.Settings, settings) && ReferenceEquals(previous.Profile, profile) && previous.Upcoming.SequenceEqual(upcoming))
                return;

            var now = sameItem ? previous!.Now : SongCredits.Resolve(current!);
            var plan = new Plan(current!, now, upcoming, BuildWindows(profile, now.DurationSeconds, upcoming.Count), profile, settings);
            Volatile.Write(ref _plan, plan);
            NowNextRuntime.Now = now;

            if (!sameItem)
            {
                // A new item: the old box must not hang over, and the info animates in again so the
                // credits roll restarts with the new title.
                if (_boxOnAir) _send(new NowNextCommand(false, settings.BoxLayer, settings.BoxTemplate));
                _boxOnAir = false;
                _shown = null;
                _shownTag = string.Empty;
                if (profile.ShowInfo) _send(new NowNextCommand(true, settings.InfoLayer, settings.InfoTemplate));
                else if (_infoOnAir) _send(new NowNextCommand(false, settings.InfoLayer, settings.InfoTemplate));
                _infoOnAir = profile.ShowInfo;
                return;
            }

            if (profile.ShowInfo != _infoOnAir)
            {
                _send(new NowNextCommand(profile.ShowInfo, settings.InfoLayer, settings.InfoTemplate));
                _infoOnAir = profile.ShowInfo;
            }
            // Real-time update: the schedule or playlist changed while the box is on screen.
            if (_boxOnAir && _shownTag.Length > 0)
            {
                if (EntryFor(plan, _shownTag) is { } entry) NowNextRuntime.Box = BoxState(plan, _shownTag, entry);
                else
                {
                    _send(new NowNextCommand(false, settings.BoxLayer, settings.BoxTemplate));
                    _boxOnAir = false;
                    _shownTag = string.Empty;
                }
            }
        }
    }

    /// <summary>Engine thread, every frame. No allocation unless the box starts, changes or ends.</summary>
    public void Tick(PlaylistItem? current, double elapsedSeconds)
    {
        var plan = Volatile.Read(ref _plan);
        if (plan is null || !ReferenceEquals(plan.Item, current)) return;

        Window? due = null;
        foreach (var window in plan.Windows)
            if (elapsedSeconds >= window.Start && elapsedSeconds < window.End) { due = window; break; }
        var tag = due is null ? string.Empty : due.Tag == Cycle ? CycleTag(plan.Profile, plan.Upcoming.Count, elapsedSeconds - due.Start) : due.Tag;
        if (Equals(due, _shown) && tag == _shownTag) return;

        lock (_gate)
        {
            if (!ReferenceEquals(_plan, plan) || (Equals(due, _shown) && tag == _shownTag)) return;
            var sameWindow = due is not null && Equals(due, _shown) && _boxOnAir;
            _shown = due;
            var entry = due is null ? null : EntryFor(plan, tag);
            if (entry is null)
            {
                if (_boxOnAir) _send(new NowNextCommand(false, plan.Settings.BoxLayer, plan.Settings.BoxTemplate));
                _boxOnAir = false;
                _shownTag = string.Empty;
                return;
            }
            NowNextRuntime.Box = BoxState(plan, tag, entry);
            _shownTag = tag;
            // Continuous rotation swaps the content in place; a new window (or NEXT -> LATER) animates in.
            if (!sameWindow || due!.Tag != Cycle)
                _send(new NowNextCommand(true, plan.Settings.BoxLayer, plan.Settings.BoxTemplate));
            _boxOnAir = true;
        }
    }

    /// <summary>Takes both graphics off air (Now/Next switched off, playout stopped).</summary>
    public void Stop()
    {
        lock (_gate)
        {
            Volatile.Write(ref _plan, null);
            StopAll(_settings());
        }
    }

    private void StopAll(NowNextSettings settings)
    {
        if (_boxOnAir) _send(new NowNextCommand(false, settings.BoxLayer, settings.BoxTemplate));
        if (_infoOnAir) _send(new NowNextCommand(false, settings.InfoLayer, settings.InfoTemplate));
        _boxOnAir = _infoOnAir = false;
        _shown = null;
        _shownTag = string.Empty;
    }

    private static AiringInfo? EntryFor(Plan plan, string tag)
        => tag == Later ? plan.Upcoming.ElementAtOrDefault(1) : plan.Upcoming.ElementAtOrDefault(0);

    private UpNextBoxState BoxState(Plan plan, string tag, AiringInfo entry)
        => new(tag, entry,
            entry.InPointSeconds + entry.DurationSeconds * Math.Clamp(plan.Settings.PreviewStartPercent, 0, 90) / 100.0,
            plan.Profile.TimeDisplay, plan.Profile.CountdownMinutes)
        { ShowVideo = _videoPreview() };

    private static string CycleTag(NowNextProfile p, int upcomingCount, double sinceStart)
    {
        var hold = Math.Max(2, p.HoldSeconds);
        if (!p.ShowLater || upcomingCount < 2 || p.LaterHoldSeconds <= 0) return Next;
        return sinceStart % (hold + p.LaterHoldSeconds) < hold ? Next : Later;
    }

    /// <summary>The box windows for one item, in seconds from its start. Public for tests.</summary>
    public static (double Start, double End, string Tag)[] PlanWindows(NowNextProfile profile, double duration, int upcomingCount)
        => BuildWindows(profile, duration, upcomingCount).Select(w => (w.Start, w.End, w.Tag)).ToArray();

    private static Window[] BuildWindows(NowNextProfile p, double duration, int upcomingCount)
    {
        if (p.BoxMode == NowNextBoxMode.Off || upcomingCount == 0 || duration <= 0) return [];
        var windows = new List<Window>();
        var limit = duration - 1;
        var hold = Math.Max(2, p.HoldSeconds);
        var laterHold = p.ShowLater && upcomingCount > 1 ? Math.Max(0, p.LaterHoldSeconds) : 0;
        var start = Math.Max(0, p.StartAtSeconds);

        bool Add(double at, bool withLater)
        {
            if (at + hold > limit) return false;
            windows.Add(new Window(at, at + hold, Next));
            if (withLater && laterHold > 0 && at + hold + laterHold <= limit)
                windows.Add(new Window(at + hold, at + hold + laterHold, Later));
            return true;
        }

        switch (p.BoxMode)
        {
            case NowNextBoxMode.StartAndEnd:
                if (p.StartAtSeconds > 0) Add(start, withLater: true);
                if (p.EndLeadSeconds > 0)
                {
                    var endStart = duration - p.EndLeadSeconds;
                    var lastEnd = windows.Count > 0 ? windows[^1].End : 0;
                    if (endStart >= lastEnd + 5) Add(endStart, withLater: false);
                }
                break;
            case NowNextBoxMode.Continuous:
                if (start < limit) windows.Add(new Window(start, limit, Cycle));
                break;
            case NowNextBoxMode.Repeat:
                var every = Math.Max(1, p.RepeatEveryMinutes) * 60;
                var times = p.RepeatTimes > 0 ? p.RepeatTimes : int.MaxValue;
                for (var k = 0; k < times && Add(start + k * every, withLater: true); k++) { }
                break;
        }
        return [.. windows];
    }
}
