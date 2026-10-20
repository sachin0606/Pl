using System.Text.Json.Serialization;
using BroadcastPlayout.ViewModels;

namespace BroadcastPlayout.Scheduling;

/// <summary>What a schedule item is. The video kinds compete by priority; CG kinds are overlays.</summary>
public enum ScheduleKind { Filler, Programme, Movie, Live, News, SpotAd, CgBottomAd, CgLShape }

[Flags]
public enum ScheduleDays
{
    None = 0, Mon = 1, Tue = 2, Wed = 4, Thu = 8, Fri = 16, Sat = 32, Sun = 64,
    All = Mon | Tue | Wed | Thu | Fri | Sat | Sun
}

/// <summary>
/// How an item repeats, as the editor presents it. It is a view of Days + StartDate/EndDate, so the
/// stored schedule and the playout rules are unchanged: Once = StartDate == EndDate.
/// </summary>
public enum ScheduleRepeat { Once, EveryDay, SelectedDays }

public enum LiveEndMode { AtEndTime, UntilManualCut }
public enum SpotCutMode { Cut, AfterCurrentClip }
public enum SpotSourceKind { Video, Cg }
public enum LShapeTrigger { Interval, AtTimes, DuringProgramme }

public static class ScheduleRules
{
    /// <summary>Spot Ad > News > Live > Movie > Programme > Filler.</summary>
    public static int Priority(ScheduleKind kind) => kind switch
    {
        ScheduleKind.SpotAd => 60,
        ScheduleKind.News => 50,
        ScheduleKind.Live => 40,
        ScheduleKind.Movie => 30,
        ScheduleKind.Programme => 20,
        ScheduleKind.Filler => 10,
        _ => 0
    };

    public static bool IsOverlay(ChannelScheduleItem item)
        => item.Kind is ScheduleKind.CgBottomAd or ScheduleKind.CgLShape
           || (item.Kind == ScheduleKind.SpotAd && item.SpotSource == SpotSourceKind.Cg);

    public static ScheduleDays DayFlag(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => ScheduleDays.Mon,
        DayOfWeek.Tuesday => ScheduleDays.Tue,
        DayOfWeek.Wednesday => ScheduleDays.Wed,
        DayOfWeek.Thursday => ScheduleDays.Thu,
        DayOfWeek.Friday => ScheduleDays.Fri,
        DayOfWeek.Saturday => ScheduleDays.Sat,
        _ => ScheduleDays.Sun
    };

    public static string KindLabel(ScheduleKind kind) => kind switch
    {
        ScheduleKind.SpotAd => "SPOT AD",
        ScheduleKind.CgBottomAd => "CG BOTTOM AD",
        ScheduleKind.CgLShape => "CG L-SHAPE",
        _ => kind.ToString().ToUpperInvariant()
    };
}

/// <summary>
/// One schedule entry. Fields that do not apply to a kind are simply ignored, which keeps a single
/// small type for storage, the editor and the resolver.
/// </summary>
public sealed class ChannelScheduleItem : ObservableObject
{
    private ScheduleKind _kind = ScheduleKind.Filler;
    private string _name = "New item";
    private bool _enabled = true;
    private ScheduleDays _days = ScheduleDays.All;
    private DateTime? _startDate;
    private DateTime? _endDate;
    private TimeSpan _startTime;
    private TimeSpan? _endTime;

    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>The always-present 00:00 every-day filler; it can be edited but not deleted.</summary>
    public bool IsDefault { get; set; }
    public ScheduleKind Kind { get => _kind; set { if (Set(ref _kind, value)) RaiseSummary(); } }
    public string Name { get => _name; set => Set(ref _name, string.IsNullOrWhiteSpace(value) ? "Untitled" : value.Trim()); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public ScheduleDays Days { get => _days; set { if (Set(ref _days, value == ScheduleDays.None ? ScheduleDays.All : value)) RaiseSummary(); } }
    /// <summary>First and last date this item is valid (inclusive); null means open-ended.</summary>
    public DateTime? StartDate { get => _startDate; set { if (Set(ref _startDate, value?.Date)) RaiseSummary(); } }
    public DateTime? EndDate { get => _endDate; set { if (Set(ref _endDate, value?.Date)) RaiseSummary(); } }
    public TimeSpan StartTime { get => _startTime; set { if (Set(ref _startTime, Normalize(value))) RaiseSummary(); } }
    /// <summary>End of the window. Null: filler runs to the next slot, programme/movie use their length.</summary>
    public TimeSpan? EndTime { get => _endTime; set { if (Set(ref _endTime, value is null ? null : Normalize(value.Value))) RaiseSummary(); } }

    /// <summary>Files, folders or playlists (video kinds), or the stream URL (live).</summary>
    public List<string> Sources { get; set; } = [];
    public bool Shuffle { get; set; }
    /// <summary>Measured length of the sources (set by the editor), used for programme/movie/spot windows.</summary>
    public double PlannedDurationSeconds { get; set; }

    public LiveEndMode LiveEnd { get; set; } = LiveEndMode.AtEndTime;
    public SpotCutMode SpotMode { get; set; } = SpotCutMode.Cut;
    public SpotSourceKind SpotSource { get; set; } = SpotSourceKind.Video;
    /// <summary>Exact air times (spot ads, L-shape "at times").</summary>
    public List<TimeSpan> Times { get; set; } = [];

    /// <summary>CG templates by name (bottom ads loop through all of them).</summary>
    public List<string> CgProjects { get; set; } = [];
    public int CgLayer { get; set; } = 10;
    /// <summary>0 = use the CG's own duration (a full ticker scroll for tickers).</summary>
    public double CgDurationOverrideSeconds { get; set; }
    public LShapeTrigger LTrigger { get; set; } = LShapeTrigger.Interval;
    public int RepeatMinutes { get; set; } = 15;
    /// <summary>Programme title or category text that triggers an L-shape.</summary>
    public string ProgrammeMatch { get; set; } = string.Empty;
    /// <summary>Playlist category for the items it plays (e.g. "Music" for a requested song). Empty = from the kind.</summary>
    public string Category { get; set; } = string.Empty;
    /// <summary>Set when this airing was scheduled for a viewer request; copied to the items it plays.</summary>
    public BroadcastPlayout.Models.RequestTag? Request { get; set; }

    [JsonIgnore] public string KindText => ScheduleRules.KindLabel(Kind);
    [JsonIgnore] public string DaysText => Days == ScheduleDays.All ? "Every day" : Days.ToString().Replace(", ", " ");
    [JsonIgnore] public string TimeText => Kind switch
    {
        ScheduleKind.CgLShape when LTrigger == LShapeTrigger.DuringProgramme => $"In '{ProgrammeMatch}' · every {RepeatMinutes} min",
        ScheduleKind.CgLShape when LTrigger == LShapeTrigger.AtTimes => string.Join(" ", Times.Select(t => t.ToString(@"hh\:mm"))),
        ScheduleKind.CgLShape => WindowText + $" · every {RepeatMinutes} min",
        ScheduleKind.SpotAd when Times.Count > 0 => string.Join(" ", Times.Select(t => t.ToString(@"hh\:mm"))),
        _ => WindowText
    };
    private string WindowText => StartTime.ToString(@"hh\:mm") + (EndTime is { } end ? " – " + end.ToString(@"hh\:mm") : "");
    [JsonIgnore] public string DatesText => StartDate is null && EndDate is null ? "Always"
        : $"{StartDate:dd MMM yyyy} – {(EndDate is null ? "open" : EndDate.Value.ToString("dd MMM yyyy"))}";

    [JsonIgnore] public ScheduleRepeat Repeat => StartDate is { } from && EndDate == from ? ScheduleRepeat.Once
        : Days == ScheduleDays.All ? ScheduleRepeat.EveryDay : ScheduleRepeat.SelectedDays;

    /// <summary>Short "when" for the list: "Once · Sat 25 Oct 2026", "Mon Wed Fri · until 30 Nov", "Every day".</summary>
    [JsonIgnore] public string WhenText
    {
        get
        {
            if (IsDefault) return "Every day (default)";
            if (Repeat == ScheduleRepeat.Once) return "Once · " + StartDate!.Value.ToString("ddd dd MMM yyyy");
            var days = Repeat == ScheduleRepeat.EveryDay ? "Every day" : DaysText;
            return (StartDate, EndDate) switch
            {
                (null, null) => days,
                ({ } from, null) => $"{days} · from {from:dd MMM yyyy}",
                (null, { } to) => $"{days} · until {to:dd MMM yyyy}",
                ({ } from, { } to) => $"{days} · {from:dd MMM} – {to:dd MMM yyyy}"
            };
        }
    }
    [JsonIgnore] public string SourceText => Kind is ScheduleKind.CgBottomAd or ScheduleKind.CgLShape || (Kind == ScheduleKind.SpotAd && SpotSource == SpotSourceKind.Cg)
        ? string.Join(", ", CgProjects)
        : Sources.Count == 0 ? "— no source —" : Sources.Count == 1 ? ShortName(Sources[0]) : $"{Sources.Count} sources";

    private static string ShortName(string source)
    {
        if (source.Contains("://", StringComparison.Ordinal)) return source;
        var name = Path.GetFileName(source.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(name) ? source : Path.HasExtension(name) ? name : name + " (folder)";
    }

    /// <summary>Call after editing list fields so the grid refreshes its text columns.</summary>
    public void RaiseSummary()
    {
        Raise(nameof(KindText)); Raise(nameof(DaysText)); Raise(nameof(TimeText)); Raise(nameof(DatesText)); Raise(nameof(SourceText)); Raise(nameof(WhenText));
    }

    public bool OccursOn(DateTime date)
    {
        if (!Enabled) return false;
        if (StartDate is { } from && date.Date < from) return false;
        if (EndDate is { } to && date.Date > to) return false;
        return (Days & ScheduleRules.DayFlag(date.DayOfWeek)) != 0;
    }

    public ChannelScheduleItem Clone()
    {
        var copy = (ChannelScheduleItem)MemberwiseClone();
        copy.Sources = [.. Sources];
        copy.Times = [.. Times];
        copy.CgProjects = [.. CgProjects];
        return copy;
    }

    private static TimeSpan Normalize(TimeSpan value)
        => TimeSpan.FromSeconds(Math.Clamp(Math.Round(value.TotalSeconds), 0, 86399));
}

/// <summary>The whole schedule of one channel.</summary>
public sealed class ChannelSchedule
{
    public string ChannelId { get; set; } = string.Empty;
    /// <summary>When off (default) the channel behaves exactly as without the schedule module.</summary>
    public bool AutomationEnabled { get; set; }
    public List<ChannelScheduleItem> Items { get; set; } = [];

    /// <summary>Guarantees the 00:00 every-day default filler slot exists.</summary>
    public void EnsureDefaultFiller()
    {
        if (Items.Any(x => x.IsDefault)) return;
        Items.Insert(0, new ChannelScheduleItem
        {
            IsDefault = true,
            Kind = ScheduleKind.Filler,
            Name = "Default Filler",
            Days = ScheduleDays.All,
            StartTime = TimeSpan.Zero
        });
    }
}
