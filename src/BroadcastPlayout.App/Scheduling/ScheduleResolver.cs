namespace BroadcastPlayout.Scheduling;

/// <summary>One concrete airing of a schedule item. End null = open-ended (live until cut).</summary>
public sealed record ScheduleOccurrence(ChannelScheduleItem Item, DateTime Start, DateTime? End)
{
    public string Key => $"{Item.Id:N}@{Start:yyyyMMddHHmmss}";
    public int Priority => ScheduleRules.Priority(Item.Kind);
    public bool Contains(DateTime time) => time >= Start && (End is null || time < End);
}

/// <summary>
/// Pure schedule rules: given a schedule and a wall-clock time, what should be on air. No state and
/// no playback, so it is cheap to call on every runner tick and easy to test.
/// </summary>
public static class ScheduleResolver
{
    /// <summary>
    /// The highest-priority video occurrence active at <paramref name="now"/>. Ties at the same
    /// priority go to the day-specific item, then to the later start.
    /// </summary>
    public static ScheduleOccurrence? ResolveVideo(ChannelSchedule schedule, DateTime now, Func<ScheduleOccurrence, bool>? isExcluded = null)
    {
        ScheduleOccurrence? best = null;
        foreach (var occurrence in ActiveVideo(schedule, now))
        {
            if (isExcluded?.Invoke(occurrence) == true) continue;
            if (best is null || Wins(occurrence, best)) best = occurrence;
        }
        return best;
    }

    private static bool Wins(ScheduleOccurrence a, ScheduleOccurrence b)
    {
        if (a.Priority != b.Priority) return a.Priority > b.Priority;
        var aSpecific = a.Item.Days != ScheduleDays.All;
        var bSpecific = b.Item.Days != ScheduleDays.All;
        if (aSpecific != bSpecific) return aSpecific;
        return a.Start > b.Start;
    }

    /// <summary>Every video occurrence active at <paramref name="now"/> (all priorities).</summary>
    public static IEnumerable<ScheduleOccurrence> ActiveVideo(ChannelSchedule schedule, DateTime now)
    {
        foreach (var item in schedule.Items)
        {
            if (item.Kind == ScheduleKind.Filler || ScheduleRules.IsOverlay(item)) continue;
            // Yesterday too: a window that started before midnight can still be running.
            for (var date = now.Date.AddDays(-1); date <= now.Date; date = date.AddDays(1))
            {
                if (!item.OccursOn(date)) continue;
                foreach (var occurrence in Occurrences(item, date))
                    if (occurrence.Contains(now)) yield return occurrence;
            }
        }
        if (ResolveFiller(schedule, now) is { } filler) yield return filler;
    }

    /// <summary>The airings of a non-filler video item on one date.</summary>
    public static IEnumerable<ScheduleOccurrence> Occurrences(ChannelScheduleItem item, DateTime date)
    {
        switch (item.Kind)
        {
            case ScheduleKind.SpotAd:
                var length = TimeSpan.FromSeconds(Math.Max(1, item.PlannedDurationSeconds));
                foreach (var time in item.Times.Count > 0 ? item.Times : new List<TimeSpan> { item.StartTime })
                    yield return new ScheduleOccurrence(item, date + time, date + time + length);
                break;
            case ScheduleKind.News:
                // No news file added: the slot is skipped and lower priorities (filler) play.
                if (item.EndTime is { } newsEnd && HasPlayableSource(item))
                    yield return Window(item, date, item.StartTime, newsEnd);
                break;
            case ScheduleKind.Live:
                if (item.Sources.Count == 0) break;
                if (item.LiveEnd == LiveEndMode.UntilManualCut)
                    yield return new ScheduleOccurrence(item, date + item.StartTime, null);
                else if (item.EndTime is { } liveEnd)
                    yield return Window(item, date, item.StartTime, liveEnd);
                break;
            case ScheduleKind.Programme:
            case ScheduleKind.Movie:
                if (item.Sources.Count == 0) break;
                if (item.EndTime is { } end)
                    yield return Window(item, date, item.StartTime, end);
                else if (item.PlannedDurationSeconds > 0)
                    yield return new ScheduleOccurrence(item, date + item.StartTime, date + item.StartTime + TimeSpan.FromSeconds(item.PlannedDurationSeconds));
                break;
        }
    }

    private static ScheduleOccurrence Window(ChannelScheduleItem item, DateTime date, TimeSpan start, TimeSpan end)
    {
        var from = date + start;
        var to = date + end;
        if (to <= from) to = to.AddDays(1); // crosses midnight
        return new ScheduleOccurrence(item, from, to);
    }

    public static bool HasPlayableSource(ChannelScheduleItem item)
        => item.Sources.Any(s => !string.IsNullOrWhiteSpace(s) && (File.Exists(s) || Directory.Exists(s)));

    // ---- Filler slots ---------------------------------------------------------------------

    /// <summary>
    /// Filler slots in effect on a date: the union of every-day slots and that day's slots; where
    /// both start at the same time the day-specific slot wins. Ordered by start time.
    /// </summary>
    public static IReadOnlyList<ChannelScheduleItem> FillerSlots(ChannelSchedule schedule, DateTime date)
        => schedule.Items
            .Where(x => x.Kind == ScheduleKind.Filler && x.OccursOn(date))
            .GroupBy(x => x.StartTime)
            .Select(g => g.OrderBy(x => x.Days == ScheduleDays.All ? 1 : 0).ThenBy(x => x.IsDefault ? 1 : 0).First())
            .OrderBy(x => x.StartTime)
            .ToList();

    /// <summary>The filler slot running at <paramref name="now"/>: it lasts until the next slot starts.</summary>
    public static ScheduleOccurrence? ResolveFiller(ChannelSchedule schedule, DateTime now)
    {
        ChannelScheduleItem? slot = null;
        var start = DateTime.MinValue;
        for (var back = 0; back <= 7 && slot is null; back++)
        {
            var date = now.Date.AddDays(-back);
            var slots = FillerSlots(schedule, date);
            var candidate = back == 0 ? slots.LastOrDefault(x => x.StartTime <= now.TimeOfDay) : slots.LastOrDefault();
            if (candidate is null) continue;
            slot = candidate;
            start = date + candidate.StartTime;
        }
        return slot is null ? null : new ScheduleOccurrence(slot, start, NextFillerStart(schedule, start));
    }

    private static DateTime? NextFillerStart(ChannelSchedule schedule, DateTime after)
    {
        for (var ahead = 0; ahead <= 7; ahead++)
        {
            var date = after.Date.AddDays(ahead);
            var next = FillerSlots(schedule, date).FirstOrDefault(x => date + x.StartTime > after);
            if (next is not null) return date + next.StartTime;
        }
        return null;
    }

    // ---- Planning helpers -------------------------------------------------------------------

    /// <summary>Next start of a non-filler video occurrence after <paramref name="now"/> (looks 2 days ahead).</summary>
    public static ScheduleOccurrence? NextEvent(ChannelSchedule schedule, DateTime now)
    {
        ScheduleOccurrence? next = null;
        foreach (var item in schedule.Items)
        {
            if (item.Kind == ScheduleKind.Filler || ScheduleRules.IsOverlay(item)) continue;
            for (var date = now.Date; date <= now.Date.AddDays(2); date = date.AddDays(1))
            {
                if (!item.OccursOn(date)) continue;
                foreach (var occurrence in Occurrences(item, date))
                    if (occurrence.Start > now && (next is null || occurrence.Start < next.Start)) next = occurrence;
            }
        }
        return next;
    }

    /// <summary>The daily window of an overlay item (bottom-ad loop, L-shape interval). No end time = to midnight.</summary>
    public static (DateTime Start, DateTime End)? DailyWindow(ChannelScheduleItem item, DateTime date)
    {
        if (!item.OccursOn(date)) return null;
        var start = date + item.StartTime;
        var end = item.EndTime is { } e ? date + e : date.AddDays(1);
        if (end <= start) end = end.AddDays(1);
        return (start, end);
    }

    /// <summary>A full day of what plays when, for the editor's day lane (resolved in steps).</summary>
    public static IReadOnlyList<ScheduleOccurrence> DayPlan(ChannelSchedule schedule, DateTime date, TimeSpan step)
    {
        var plan = new List<ScheduleOccurrence>();
        for (var t = date; t < date.AddDays(1); t += step)
        {
            var active = ResolveVideo(schedule, t);
            if (active is null) continue;
            if (plan.Count > 0 && plan[^1].Key == active.Key) continue;
            plan.Add(active);
        }
        return plan;
    }
}
