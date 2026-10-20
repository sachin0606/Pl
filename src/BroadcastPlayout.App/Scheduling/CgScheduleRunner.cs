using BroadcastPlayout.Models;
using BroadcastPlayout.Services;

namespace BroadcastPlayout.Scheduling;

/// <summary>A scheduled overlay action: put a CG template on a Program layer, or clear the layer.</summary>
public sealed record CgScheduleCommand(bool Play, int Layer, string ProjectName, string Reason);

/// <summary>
/// Scheduled overlays (bottom-ad loops, CG spot ads, L-shapes). Ticked by <see cref="ScheduleRunner"/>,
/// so it adds no timer of its own. It only decides; the host applies commands through the same
/// Program CG layer path the CG Controller uses.
/// </summary>
public sealed class CgScheduleRunner
{
    private sealed class LayerState
    {
        public string Project = string.Empty;
        public int Layer;
        public DateTime StopAt;
        public int Index = -1;
        public string Occurrence = string.Empty;
    }

    private readonly Func<string, CgProject?> _findProject;
    private readonly Action<CgScheduleCommand> _apply;
    private readonly Dictionary<Guid, LayerState> _active = [];
    private readonly Dictionary<string, DateTime> _fired = [];

    /// <param name="findProject">Read-only lookup of a CG template by name (thread-safe snapshot).</param>
    public CgScheduleRunner(Func<string, CgProject?> findProject, Action<CgScheduleCommand> apply)
    {
        _findProject = findProject;
        _apply = apply;
    }

    public void Tick(ChannelSchedule schedule, DateTime now, PlaylistItem? onAir)
    {
        foreach (var key in _fired.Where(x => x.Value < now.AddDays(-1)).Select(x => x.Key).ToList()) _fired.Remove(key);
        var seen = new HashSet<Guid>();
        foreach (var item in schedule.Items)
        {
            if (!ScheduleRules.IsOverlay(item) || !item.Enabled || item.CgProjects.Count == 0) continue;
            seen.Add(item.Id);
            switch (item.Kind)
            {
                case ScheduleKind.CgBottomAd: TickBottomAd(item, now); break;
                case ScheduleKind.SpotAd: TickInstants(item, now, SpotInstants(item, now)); break;
                case ScheduleKind.CgLShape: TickLShape(item, now, onAir); break;
            }
        }
        // Items deleted or disabled while on air are cleared.
        foreach (var gone in _active.Keys.Where(id => !seen.Contains(id)).ToList()) Clear(gone, "removed from schedule");
    }

    /// <summary>Clears every layer this runner put on air (automation switched off).</summary>
    public void StopAll()
    {
        foreach (var id in _active.Keys.ToList()) Clear(id, "automation off");
        _fired.Clear();
    }

    // ---- Bottom ads: loop all ads through the window, each for its CG duration ----------------

    private void TickBottomAd(ChannelScheduleItem item, DateTime now)
    {
        var window = ActiveWindow(item, now);
        if (window is null)
        {
            if (_active.ContainsKey(item.Id)) Clear(item.Id, "window ended");
            return;
        }
        if (_active.TryGetValue(item.Id, out var state) && now < state.StopAt) return;
        state ??= _active[item.Id] = new LayerState();
        // Advance to the next ad that exists; a missing template is skipped.
        for (var tries = 0; tries < item.CgProjects.Count; tries++)
        {
            state.Index = (state.Index + 1) % item.CgProjects.Count;
            var name = item.CgProjects[state.Index];
            if (_findProject(name) is not { } project) continue;
            state.Project = name;
            state.Layer = item.CgLayer;
            state.StopAt = now + TimeSpan.FromSeconds(DurationOf(project, item.CgDurationOverrideSeconds));
            _apply(new CgScheduleCommand(true, item.CgLayer, name, $"BOTTOM AD · {item.Name}"));
            return;
        }
        Clear(item.Id, "no CG template found");
    }

    // ---- L-shape: interval, fixed times, or during a programme ---------------------------------

    private void TickLShape(ChannelScheduleItem item, DateTime now, PlaylistItem? onAir)
    {
        switch (item.LTrigger)
        {
            case LShapeTrigger.AtTimes:
                TickInstants(item, now, TimesToday(item, now));
                break;
            case LShapeTrigger.Interval:
                TickInstants(item, now, IntervalInstants(item, now));
                break;
            case LShapeTrigger.DuringProgramme:
                var matches = onAir is not null && item.OccursOn(now.Date) && MatchesProgramme(item.ProgrammeMatch, onAir);
                if (!matches)
                {
                    if (_active.ContainsKey(item.Id) && now >= _active[item.Id].StopAt) Clear(item.Id, "programme ended");
                    return;
                }
                // Once when the programme starts, then every N minutes while it stays on air.
                var key = $"{item.Id:N}@{onAir!.Id:N}";
                var started = _fired.TryGetValue(key, out var first) ? first : now;
                var repeat = item.RepeatMinutes > 0 ? TimeSpan.FromMinutes(item.RepeatMinutes) : TimeSpan.MaxValue;
                var instant = repeat == TimeSpan.MaxValue ? started : started + TimeSpan.FromTicks((now - started).Ticks / repeat.Ticks * repeat.Ticks);
                if (!_fired.ContainsKey(key)) _fired[key] = now;
                TickInstants(item, now, [instant]);
                break;
        }
    }

    private static bool MatchesProgramme(string match, PlaylistItem onAir)
    {
        if (string.IsNullOrWhiteSpace(match)) return false;
        var text = match.Trim();
        return (onAir.Title ?? string.Empty).Contains(text, StringComparison.OrdinalIgnoreCase)
            || string.Equals(onAir.Category ?? string.Empty, text, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Instants: play at a moment for the CG duration, once per instant -----------------------

    private void TickInstants(ChannelScheduleItem item, DateTime now, IEnumerable<DateTime> instants)
    {
        if (_active.TryGetValue(item.Id, out var state))
        {
            if (now < state.StopAt) return;
            Clear(item.Id, "duration complete");
        }
        foreach (var instant in instants)
        {
            if (now < instant || now > instant.AddMinutes(1)) continue; // only start on time (1 min grace)
            var occurrence = $"{item.Id:N}@{instant:yyyyMMddHHmmss}";
            if (_fired.ContainsKey(occurrence)) continue;
            var name = item.CgProjects[0];
            if (_findProject(name) is not { } project) { _fired[occurrence] = instant; continue; }
            _fired[occurrence] = instant;
            var duration = DurationOf(project, item.CgDurationOverrideSeconds);
            if (now >= instant.AddSeconds(duration)) continue;
            _active[item.Id] = new LayerState { Project = name, Layer = item.CgLayer, StopAt = instant.AddSeconds(duration), Occurrence = occurrence };
            _apply(new CgScheduleCommand(true, item.CgLayer, name, $"{ScheduleRules.KindLabel(item.Kind)} · {item.Name}"));
            return;
        }
    }

    private static IEnumerable<DateTime> SpotInstants(ChannelScheduleItem item, DateTime now)
        => TimesToday(item, now);

    private static IEnumerable<DateTime> TimesToday(ChannelScheduleItem item, DateTime now)
    {
        if (!item.OccursOn(now.Date)) yield break;
        foreach (var time in item.Times.Count > 0 ? item.Times : new List<TimeSpan> { item.StartTime })
            yield return now.Date + time;
    }

    private static IEnumerable<DateTime> IntervalInstants(ChannelScheduleItem item, DateTime now)
    {
        if (ActiveWindow(item, now) is not { } window) yield break;
        var step = TimeSpan.FromMinutes(Math.Max(1, item.RepeatMinutes));
        var elapsed = now - window.Start;
        yield return window.Start + TimeSpan.FromTicks(elapsed.Ticks / step.Ticks * step.Ticks);
    }

    private static (DateTime Start, DateTime End)? ActiveWindow(ChannelScheduleItem item, DateTime now)
    {
        foreach (var date in new[] { now.Date, now.Date.AddDays(-1) })
            if (ScheduleResolver.DailyWindow(item, date) is { } window && now >= window.Start && now < window.End)
                return window;
        return null;
    }

    private void Clear(Guid itemId, string reason)
    {
        if (!_active.Remove(itemId, out var state)) return;
        _apply(new CgScheduleCommand(false, state.Layer, state.Project, reason));
    }

    /// <summary>
    /// How long a template stays on air: the operator override, else a dynamic ticker's in + one full
    /// scroll + out, else the template's own duration.
    /// </summary>
    public static double DurationOf(CgProject project, double overrideSeconds)
    {
        if (overrideSeconds > 0) return Math.Clamp(overrideSeconds, 1, 7200);
        var maxOut = project.Layers.Where(x => x.Visible).Select(x => x.AnimationOutSeconds).DefaultIfEmpty(0.5).Max();
        var cycle = CgDataSourceService.ResolveEffectiveTickerCycleDuration(project);
        if (cycle > 0.5)
        {
            // Same lifecycle the compositor uses: in-animation to the pause point, the ticker holds
            // there for one complete scroll, then the out-animation.
            var holdPoint = Math.Max(0.1, project.DurationSeconds - maxOut - 0.05);
            var pausePoint = Math.Min(holdPoint, CgDataSourceService.ResolveEffectiveHoldPoint(project));
            var intro = Math.Min(CgDataSourceService.ResolveEffectiveCategoryIntroDuration(project), pausePoint);
            return Math.Clamp(pausePoint + Math.Max(0.05, cycle - intro) + maxOut, 1, 7200);
        }
        return Math.Clamp(project.DurationSeconds, 1, 7200);
    }
}
