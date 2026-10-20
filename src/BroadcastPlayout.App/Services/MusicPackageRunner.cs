using BroadcastPlayout.Models;

namespace BroadcastPlayout.Services;

/// <summary>Play or stop one package graphic on a Program CG layer.</summary>
public sealed record MusicPackageCommand(bool Play, int Layer, string TemplateName);

/// <summary>
/// Times the music Now/Next package for the item on air:
/// the info graphic (title, credits, progress) runs for the whole song, and the video box shows
/// NEXT, then LATER, early in the song and NEXT again near the end.
///
/// <see cref="Prepare"/> runs on the UI thread when the item or the playlist order changes and does the
/// work (credits lookup, timing plan). <see cref="Tick"/> runs on the engine thread every frame and only
/// compares the position with the plan; it sends a command when a window starts or ends.
/// </summary>
public sealed class MusicPackageRunner
{
    private sealed record Window(double Start, double End, string Tag, SongInfo Song);
    private sealed record Plan(PlaylistItem Item, SongInfo Now, PlaylistItem? Next, PlaylistItem? Later, Window[] Windows, MusicPackageSettings Settings);

    private readonly object _gate = new();
    private readonly Func<MusicPackageSettings> _settings;
    private readonly Func<bool> _videoBoxEnabled;
    private readonly Action<MusicPackageCommand> _send;
    private Plan? _plan;
    private bool _infoOnAir;
    private bool _boxOnAir;
    private Window? _shown;

    /// <param name="send">Called from either thread; the host marshals it to the CG layer stack.</param>
    public MusicPackageRunner(Func<MusicPackageSettings> settings, Func<bool> videoBoxEnabled, Action<MusicPackageCommand> send)
    {
        _settings = settings;
        _videoBoxEnabled = videoBoxEnabled;
        _send = send;
    }

    /// <summary>True when the package owns the graphics for this item (the classic auto lower thirds stay off).</summary>
    public bool Handles(PlaylistItem? item)
    {
        var plan = Volatile.Read(ref _plan);
        return plan is not null && item is not null && ReferenceEquals(plan.Item, item);
    }

    /// <summary>
    /// Call on the UI thread when the on-air item, the next items or the settings change. Re-preparing the
    /// same song only refreshes NEXT / LATER, so the info graphic is not restarted.
    /// </summary>
    public void Prepare(PlaylistItem? current, PlaylistItem? next, PlaylistItem? later)
    {
        var settings = _settings();
        var applies = settings.Enabled && current is not null && !current.IsCgEvent && !current.IsControlEvent && settings.AppliesTo(current.Category);
        lock (_gate)
        {
            var previous = _plan;
            if (!applies)
            {
                Volatile.Write(ref _plan, null);
                StopAll();
                return;
            }

            var sameItem = previous is not null && ReferenceEquals(previous.Item, current);
            if (sameItem && ReferenceEquals(previous!.Next, next) && ReferenceEquals(previous.Later, later) && ReferenceEquals(previous.Settings, settings))
                return;

            var now = sameItem ? previous!.Now : SongCredits.Resolve(current!);
            var plan = new Plan(current!, now, next, later, BuildWindows(settings, now, next, later), settings);
            Volatile.Write(ref _plan, plan);
            MusicPackageRuntime.Now = now;

            if (!sameItem || !_infoOnAir)
            {
                // A new song: the box from the previous song must not hang over, and the info graphic
                // animates in again so the credits roll restarts with the new title.
                if (_boxOnAir) _send(new MusicPackageCommand(false, settings.BoxLayer, settings.BoxTemplate));
                _boxOnAir = false;
                _shown = null;
                _send(new MusicPackageCommand(true, settings.InfoLayer, settings.InfoTemplate));
                _infoOnAir = true;
            }
        }
    }

    /// <summary>Engine thread, every frame. Cheap: no allocation unless a window starts or ends.</summary>
    public void Tick(PlaylistItem? current, double elapsedSeconds)
    {
        var plan = Volatile.Read(ref _plan);
        if (plan is null || !ReferenceEquals(plan.Item, current)) return;

        Window? due = null;
        foreach (var window in plan.Windows)
            if (elapsedSeconds >= window.Start && elapsedSeconds < window.End) { due = window; break; }
        if (ReferenceEquals(due, Volatile.Read(ref _shown))) return;

        lock (_gate)
        {
            if (!ReferenceEquals(_plan, plan) || ReferenceEquals(due, _shown)) return;
            _shown = due;
            if (due is null)
            {
                if (_boxOnAir) _send(new MusicPackageCommand(false, plan.Settings.BoxLayer, plan.Settings.BoxTemplate));
                _boxOnAir = false;
                return;
            }
            // Box content is read by the compositor every frame; switching it and re-playing the box
            // gives NEXT -> LATER its own in-animation.
            MusicPackageRuntime.Box = new UpNextBoxState(due.Tag, due.Song,
                due.Song.InPointSeconds + due.Song.DurationSeconds * Math.Clamp(plan.Settings.PreviewStartPercent, 0, 90) / 100.0);
            _send(new MusicPackageCommand(true, plan.Settings.BoxLayer, plan.Settings.BoxTemplate));
            _boxOnAir = true;
        }
    }

    /// <summary>Takes both graphics off air (package switched off, playout stopped).</summary>
    public void Stop()
    {
        lock (_gate)
        {
            Volatile.Write(ref _plan, null);
            StopAll();
        }
    }

    private void StopAll()
    {
        var settings = _settings();
        if (_boxOnAir) _send(new MusicPackageCommand(false, settings.BoxLayer, settings.BoxTemplate));
        if (_infoOnAir) _send(new MusicPackageCommand(false, settings.InfoLayer, settings.InfoTemplate));
        _boxOnAir = _infoOnAir = false;
        _shown = null;
    }

    private Window[] BuildWindows(MusicPackageSettings s, SongInfo now, PlaylistItem? next, PlaylistItem? later)
    {
        if (!_videoBoxEnabled() || next is null || now.DurationSeconds <= 0) return [];
        return BuildWindows(s, now.DurationSeconds, SongCredits.Resolve(next), later is null ? null : SongCredits.Resolve(later));
    }

    /// <summary>
    /// NEXT at FirstNextAt, then LATER, then NEXT again near the end. Windows that would not finish
    /// a second before the song ends, or would overlap, are dropped. Public for tests.
    /// </summary>
    public static (double Start, double End, string Tag)[] PlanWindows(MusicPackageSettings s, double duration, bool hasLater)
        => BuildWindows(s, duration, SongInfo.Empty, hasLater ? SongInfo.Empty : null).Select(w => (w.Start, w.End, w.Tag)).ToArray();

    private static Window[] BuildWindows(MusicPackageSettings s, double duration, SongInfo next, SongInfo? later)
    {
        var windows = new List<Window>(3);
        var limit = duration - 1;
        var nextHold = Math.Max(2, s.NextHoldSeconds);
        var start = Math.Max(0, s.FirstNextAtSeconds);
        if (start + nextHold <= limit)
        {
            windows.Add(new Window(start, start + nextHold, "NEXT", next));
            if (later is not null && s.LaterHoldSeconds > 0 && start + nextHold + s.LaterHoldSeconds <= limit)
                windows.Add(new Window(start + nextHold, start + nextHold + s.LaterHoldSeconds, "LATER", later));
        }
        if (s.EndNextBeforeSeconds > 0)
        {
            var endStart = duration - s.EndNextBeforeSeconds;
            var lastEnd = windows.Count > 0 ? windows[^1].End : 0;
            if (endStart >= lastEnd + 5 && endStart + nextHold <= limit)
                windows.Add(new Window(endStart, endStart + nextHold, "NEXT", next));
        }
        return [.. windows];
    }
}
