using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using BroadcastPlayout.Ffmpeg;
using BroadcastPlayout.Scheduling;
using BroadcastPlayout.Services;

namespace BroadcastPlayout.ViewModels;

/// <summary>One coloured block on the editor's 24-hour lane.</summary>
public sealed record ScheduleLaneBlock(double Left, double Width, string Label, string Color, string ToolTip);

/// <summary>Left-panel entry: a kind filter with its item count.</summary>
public sealed class ScheduleKindEntry : ObservableObject
{
    private int _count;
    public ScheduleKindEntry(ScheduleKind? kind, string label) { Kind = kind; Label = label; }
    public ScheduleKind? Kind { get; }
    public string Label { get; }
    public int Count { get => _count; set => Set(ref _count, value); }
}

/// <summary>
/// Channel Schedule editor. Works on an in-memory copy of one channel's schedule and writes it back
/// on SAVE; the channel's playout process picks the change up by itself. Has no timers and plays
/// nothing, so it is safe in every Kashtrix module window.
/// </summary>
public sealed class ChannelScheduleEditorViewModel : ObservableObject
{
    public const double LaneMinutesScale = 1.5; // px per minute → 2160 px per day
    public const double LaneWidth = 24 * 60 * LaneMinutesScale;

    private readonly MainViewModel? _host;
    private readonly ObservableCollection<ChannelScheduleItem> _items = [];
    private ChannelSchedule _schedule = new();
    private string _channelId;
    private ScheduleKindEntry? _selectedKind;
    private ChannelScheduleItem? _selected;
    private DateTime _planDate = DateTime.Today;
    private bool _isDirty;
    private string _message = string.Empty;
    private string? _selectedCgTemplate;

    public ChannelScheduleEditorViewModel(string channelId, IEnumerable<string> cgTemplates, MainViewModel? host = null)
    {
        _host = host;
        _channelId = ChannelScheduleStore.Normalize(channelId);
        CgTemplates = cgTemplates.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        Channels = new ObservableCollection<string>(new[] { _channelId }
            .Concat(ChannelRegistryStore.Load().Select(c => ChannelScheduleStore.Normalize(c.ChannelId)))
            .Distinct(StringComparer.OrdinalIgnoreCase));
        Kinds =
        [
            new ScheduleKindEntry(null, "ALL"),
            .. Enum.GetValues<ScheduleKind>().Select(k => new ScheduleKindEntry(k, ScheduleRules.KindLabel(k)))
        ];
        ItemsView = CollectionViewSource.GetDefaultView(_items);
        ItemsView.Filter = o => o is ChannelScheduleItem item && (_selectedKind?.Kind is null || item.Kind == _selectedKind.Kind);
        _selectedKind = Kinds[0];
        Load();
    }

    // ---- Lists --------------------------------------------------------------------------------

    public ObservableCollection<string> Channels { get; }
    public IReadOnlyList<ScheduleKindEntry> Kinds { get; }
    public ICollectionView ItemsView { get; }
    public IReadOnlyList<string> CgTemplates { get; }
    public IReadOnlyList<ScheduleKind> KindChoices { get; } = Enum.GetValues<ScheduleKind>();
    public ObservableCollection<ScheduleLaneBlock> VideoLane { get; } = [];
    public ObservableCollection<ScheduleLaneBlock> CgLane { get; } = [];
    public IReadOnlyList<string> LaneHours { get; } = Enumerable.Range(0, 24).Select(h => $"{h:00}:00").ToList();
    public double LaneTotalWidth => LaneWidth;
    public double LaneHourWidth => 60 * LaneMinutesScale;

    public string ChannelId
    {
        get => _channelId;
        set
        {
            var id = ChannelScheduleStore.Normalize(value);
            if (!Set(ref _channelId, id)) return;
            if (!Channels.Contains(id)) Channels.Add(id);
            Load();
        }
    }

    public ScheduleKindEntry? SelectedKind
    {
        get => _selectedKind;
        set { if (Set(ref _selectedKind, value)) { ItemsView.Refresh(); Raise(nameof(AddButtonText)); } }
    }

    public ChannelScheduleItem? Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value)) RaiseEditor(); }
    }

    public DateTime PlanDate
    {
        get => _planDate;
        set { if (Set(ref _planDate, value.Date)) RebuildLanes(); }
    }

    public bool IsDirty { get => _isDirty; private set { if (Set(ref _isDirty, value)) Raise(nameof(TitleText)); } }
    public string Message { get => _message; set => Set(ref _message, value ?? string.Empty); }
    public string TitleText => $"CHANNEL SCHEDULE · {_channelId}{(IsDirty ? " · unsaved" : string.Empty)}";
    public string AddButtonText => _selectedKind?.Kind is { } kind ? "+ ADD " + ScheduleRules.KindLabel(kind) : "+ ADD FILLER SLOT";

    /// <summary>The playout process of this channel follows this switch (saved immediately).</summary>
    public bool AutomationEnabled
    {
        get => _schedule.AutomationEnabled;
        set
        {
            if (_schedule.AutomationEnabled == value) return;
            _schedule.AutomationEnabled = value;
            Raise();
            Save();
            Message = value ? "Automation ON · the channel now follows this schedule." : "Automation OFF · the channel is operated manually.";
        }
    }

    public bool CanControlRunner => _host?.CanRunSchedule == true && string.Equals(_host.ChannelId, _channelId, StringComparison.OrdinalIgnoreCase);
    public string RunnerStatusText => CanControlRunner ? _host!.ScheduleStatusText : "Runs in the Playout app of " + _channelId;

    // ---- Load / save -------------------------------------------------------------------------------

    public void Load()
    {
        _schedule = ChannelScheduleStore.Load(_channelId);
        _items.Clear();
        foreach (var item in _schedule.Items) Attach(item);
        Selected = _items.FirstOrDefault();
        IsDirty = false;
        RefreshCounts();
        RebuildLanes();
        Raise(nameof(AutomationEnabled)); Raise(nameof(TitleText)); Raise(nameof(CanControlRunner)); Raise(nameof(RunnerStatusText));
        Message = $"{_items.Count} schedule item(s) loaded for {_channelId}.";
    }

    public void Save()
    {
        _schedule.ChannelId = _channelId;
        _schedule.Items = [.. _items];
        ChannelScheduleStore.Save(_schedule);
        IsDirty = false;
        Message = $"Saved · {_channelId} · {DateTime.Now:HH:mm:ss}";
        Raise(nameof(RunnerStatusText));
    }

    private void Attach(ChannelScheduleItem item)
    {
        item.PropertyChanged += OnItemChanged;
        _items.Add(item);
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChannelScheduleItem.KindText) or nameof(ChannelScheduleItem.DaysText) or nameof(ChannelScheduleItem.TimeText)
            or nameof(ChannelScheduleItem.DatesText) or nameof(ChannelScheduleItem.SourceText) or nameof(ChannelScheduleItem.WhenText)) return;
        MarkChanged();
        if (e.PropertyName == nameof(ChannelScheduleItem.Kind)) { RefreshCounts(); ItemsView.Refresh(); RaiseEditor(); }
        else if (ReferenceEquals(sender, _selected) && e.PropertyName is nameof(ChannelScheduleItem.StartDate) or nameof(ChannelScheduleItem.EndDate)
                 or nameof(ChannelScheduleItem.Days) or nameof(ChannelScheduleItem.StartTime) or nameof(ChannelScheduleItem.EndTime))
            RaiseWhen();
    }

    private void MarkChanged()
    {
        IsDirty = true;
        RebuildLanes();
        Raise(nameof(WhenSummary));
    }

    // ---- Add / remove ------------------------------------------------------------------------------

    public void AddItem()
    {
        var kind = _selectedKind?.Kind ?? ScheduleKind.Filler;
        var now = DateTime.Now;
        var start = new TimeSpan(now.Hour + 1 > 23 ? 23 : now.Hour + 1, 0, 0);
        var item = new ChannelScheduleItem
        {
            Kind = kind,
            Name = kind switch
            {
                ScheduleKind.Filler => $"Filler {start:hh\\:mm}",
                ScheduleKind.CgBottomAd => "Bottom Ads",
                ScheduleKind.CgLShape => "L-Shape Ad",
                _ => $"New {ScheduleRules.KindLabel(kind).ToLowerInvariant()}"
            },
            StartTime = start,
            // A movie or a live event usually airs on one date; everything else repeats every day.
            StartDate = kind is ScheduleKind.Movie or ScheduleKind.Live ? now.Date : null,
            EndDate = kind is ScheduleKind.Movie or ScheduleKind.Live ? now.Date : null,
            EndTime = kind is ScheduleKind.News or ScheduleKind.Live or ScheduleKind.CgBottomAd or ScheduleKind.CgLShape ? start + TimeSpan.FromMinutes(30) : null,
            Times = kind == ScheduleKind.SpotAd ? [start] : [],
            CgLayer = kind switch { ScheduleKind.CgBottomAd => 10, ScheduleKind.CgLShape => 11, _ => 12 }
        };
        Attach(item);
        Selected = item;
        RefreshCounts();
        MarkChanged();
    }

    public void DeleteSelected()
    {
        if (_selected is null) return;
        if (_selected.IsDefault) { Message = "The default filler slot is always present; edit its sources instead."; return; }
        _selected.PropertyChanged -= OnItemChanged;
        var index = _items.IndexOf(_selected);
        _items.Remove(_selected);
        Selected = _items.Count == 0 ? null : _items[Math.Clamp(index, 0, _items.Count - 1)];
        RefreshCounts();
        MarkChanged();
    }

    public void DuplicateSelected()
    {
        if (_selected is null) return;
        var copy = _selected.Clone();
        copy.Id = Guid.NewGuid();
        copy.IsDefault = false;
        copy.Name = _selected.Name + " (copy)";
        Attach(copy);
        Selected = copy;
        RefreshCounts();
        MarkChanged();
    }

    private void RefreshCounts()
    {
        foreach (var entry in Kinds)
            entry.Count = entry.Kind is null ? _items.Count : _items.Count(x => x.Kind == entry.Kind);
    }

    // ---- Editor fields (selected item) ---------------------------------------------------------

    public bool IsEditable => _selected is not null;
    public bool KindEditable => _selected is { IsDefault: false };
    public bool IsLive => _selected?.Kind == ScheduleKind.Live;
    public bool IsCgSource => _selected is not null && ScheduleRules.IsOverlay(_selected);
    public bool IsMediaSource => _selected is not null && !ScheduleRules.IsOverlay(_selected) && _selected.Kind != ScheduleKind.Live;
    public bool IsFiller => _selected?.Kind == ScheduleKind.Filler;
    public bool IsSpot => _selected?.Kind == ScheduleKind.SpotAd;
    public bool IsLShape => _selected?.Kind == ScheduleKind.CgLShape;
    public bool UsesTimes => IsSpot || (IsLShape && _selected!.LTrigger == LShapeTrigger.AtTimes);
    public bool UsesWindow => !UsesTimes && !(IsLShape && _selected!.LTrigger == LShapeTrigger.DuringProgramme);
    public bool UsesEndTime => UsesWindow && !IsFiller && !(IsLive && _selected!.LiveEnd == LiveEndMode.UntilManualCut);
    public bool UsesRepeat => IsLShape && _selected!.LTrigger != LShapeTrigger.AtTimes;
    public bool UsesProgrammeMatch => IsLShape && _selected!.LTrigger == LShapeTrigger.DuringProgramme;
    public string KindHint => _selected?.Kind switch
    {
        ScheduleKind.Filler => "Plays from its time until the next filler slot. A day-specific slot replaces the every-day slot at the same time.",
        ScheduleKind.Programme => "Starts at its time and plays its full content; resumes where it stopped after higher-priority events.",
        ScheduleKind.Movie => "Like a programme, one priority higher.",
        ScheduleKind.Live => "RTMP / SRT / UDP / HLS / any URL. Ends at the end time or when cut manually. A lost feed falls back to filler and retries every 10 s.",
        ScheduleKind.News => "Plays the news file inside its time slot. No file added → filler plays.",
        ScheduleKind.SpotAd => "Video or CG ad at exact times. Cut = on time; After current clip = waits for the clip to end.",
        ScheduleKind.CgBottomAd => "Loops through all ads in the window, each for its CG duration (a ticker completes one full scroll).",
        ScheduleKind.CgLShape => "Every N minutes in a window, at fixed times, or while a programme (title or category) is on air.",
        _ => string.Empty
    };
    public string PlannedDurationText => _selected is null || _selected.PlannedDurationSeconds <= 0
        ? "—"
        : TimeSpan.FromSeconds(_selected.PlannedDurationSeconds).ToString(@"hh\:mm\:ss");

    public string StartTimeText
    {
        get => _selected?.StartTime.ToString(@"hh\:mm\:ss") ?? string.Empty;
        set
        {
            if (_selected is null) return;
            if (_selected.IsDefault) { Message = "The default filler always starts at 00:00:00."; Raise(); return; }
            if (TryParseTime(value, out var t)) _selected.StartTime = t; else Message = "Time format: HH:mm or HH:mm:ss";
            Raise();
        }
    }

    public string EndTimeText
    {
        get => _selected?.EndTime?.ToString(@"hh\:mm\:ss") ?? string.Empty;
        set
        {
            if (_selected is null) return;
            if (string.IsNullOrWhiteSpace(value)) _selected.EndTime = null;
            else if (TryParseTime(value, out var t)) _selected.EndTime = t;
            else Message = "Time format: HH:mm or HH:mm:ss";
            Raise();
        }
    }

    /// <summary>Comma separated air times, e.g. "06:30, 12:00, 18:45".</summary>
    public string TimesText
    {
        get => _selected is null ? string.Empty : string.Join(", ", _selected.Times.Select(t => t.ToString(@"hh\:mm\:ss")));
        set
        {
            if (_selected is null) return;
            var parsed = new List<TimeSpan>();
            foreach (var part in (value ?? string.Empty).Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
            {
                if (TryParseTime(part, out var t)) parsed.Add(t);
                else { Message = $"Ignored '{part}': time format HH:mm or HH:mm:ss"; }
            }
            _selected.Times = parsed.Distinct().OrderBy(t => t).ToList();
            _selected.RaiseSummary();
            MarkChanged();
            Raise();
        }
    }

    public string LiveUrl
    {
        get => _selected?.Sources.FirstOrDefault() ?? string.Empty;
        set
        {
            if (_selected is null) return;
            _selected.Sources = string.IsNullOrWhiteSpace(value) ? [] : [value.Trim()];
            _selected.RaiseSummary();
            MarkChanged();
        }
    }

    public bool LiveUntilManualCut
    {
        get => _selected?.LiveEnd == LiveEndMode.UntilManualCut;
        set { if (_selected is null) return; _selected.LiveEnd = value ? LiveEndMode.UntilManualCut : LiveEndMode.AtEndTime; MarkChanged(); RaiseEditor(); }
    }

    public bool SpotIsCg
    {
        get => _selected?.SpotSource == SpotSourceKind.Cg;
        set { if (_selected is null) return; _selected.SpotSource = value ? SpotSourceKind.Cg : SpotSourceKind.Video; _selected.RaiseSummary(); MarkChanged(); RaiseEditor(); }
    }

    public bool SpotWaitsForClip
    {
        get => _selected?.SpotMode == SpotCutMode.AfterCurrentClip;
        set { if (_selected is null) return; _selected.SpotMode = value ? SpotCutMode.AfterCurrentClip : SpotCutMode.Cut; MarkChanged(); Raise(); }
    }

    public bool Shuffle
    {
        get => _selected?.Shuffle == true;
        set { if (_selected is null) return; _selected.Shuffle = value; MarkChanged(); Raise(); }
    }

    public LShapeTrigger LTrigger
    {
        get => _selected?.LTrigger ?? LShapeTrigger.Interval;
        set { if (_selected is null) return; _selected.LTrigger = value; _selected.RaiseSummary(); MarkChanged(); RaiseEditor(); }
    }
    public IReadOnlyList<LShapeTrigger> LTriggerChoices { get; } = Enum.GetValues<LShapeTrigger>();

    public string RepeatMinutesText
    {
        get => _selected?.RepeatMinutes.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        set { if (_selected is not null && int.TryParse(value, out var m)) { _selected.RepeatMinutes = Math.Clamp(m, 0, 1440); _selected.RaiseSummary(); MarkChanged(); } Raise(); }
    }

    public string ProgrammeMatch
    {
        get => _selected?.ProgrammeMatch ?? string.Empty;
        set { if (_selected is null) return; _selected.ProgrammeMatch = value?.Trim() ?? string.Empty; _selected.RaiseSummary(); MarkChanged(); }
    }

    public string CgLayerText
    {
        get => _selected?.CgLayer.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        set { if (_selected is not null && int.TryParse(value, out var l)) { _selected.CgLayer = Math.Clamp(l, 1, 99); MarkChanged(); } Raise(); }
    }

    public string CgDurationText
    {
        get => _selected is null || _selected.CgDurationOverrideSeconds <= 0 ? string.Empty : _selected.CgDurationOverrideSeconds.ToString("0.#", CultureInfo.InvariantCulture);
        set
        {
            if (_selected is null) return;
            _selected.CgDurationOverrideSeconds = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? Math.Clamp(s, 0, 7200) : 0;
            MarkChanged();
            Raise();
        }
    }

    public string? SelectedCgTemplate { get => _selectedCgTemplate; set => Set(ref _selectedCgTemplate, value); }

    // ---- When: repeat, days, dates --------------------------------------------------------------

    public sealed record RepeatOption(ScheduleRepeat Value, string Label)
    {
        // The themed ComboBox shows the item's text in its selection box.
        public override string ToString() => Label;
    }

    public IReadOnlyList<RepeatOption> RepeatChoices { get; } =
    [
        new(ScheduleRepeat.Once, "One date"),
        new(ScheduleRepeat.EveryDay, "Every day"),
        new(ScheduleRepeat.SelectedDays, "Selected days of the week")
    ];

    /// <summary>The default filler has no date/day choices: it always runs every day when nothing else plays.</summary>
    public bool ShowWhen => _selected is { IsDefault: false };
    public bool IsOnce => ShowWhen && _selected!.Repeat == ScheduleRepeat.Once;
    public bool IsSelectedDays => ShowWhen && _selected!.Repeat == ScheduleRepeat.SelectedDays;
    public bool CanLimitDates => ShowWhen && _selected!.Repeat != ScheduleRepeat.Once;
    public bool ShowDateRange => CanLimitDates && LimitDates;
    public bool ShowNoLimitText => CanLimitDates && !LimitDates;
    public bool IsAd => _selected?.Kind is ScheduleKind.SpotAd or ScheduleKind.CgBottomAd or ScheduleKind.CgLShape;
    public string LimitDatesLabel => IsAd ? "Campaign dates (start / end)" : "Only between dates (e.g. a season or festival week)";
    public string NoLimitText => IsAd ? "No campaign end date: the ad keeps running." : "No end date: repeats until you change it.";

    public RepeatOption? SelectedRepeat
    {
        get => _selected is null ? null : RepeatChoices.First(x => x.Value == _selected.Repeat);
        set
        {
            if (_selected is null || value is null || value.Value == _selected.Repeat) return;
            if (_selected.IsDefault) { Message = "The default filler runs every day; add another filler slot for specific days or dates."; Raise(); return; }
            switch (value.Value)
            {
                case ScheduleRepeat.Once:
                    var date = _selected.StartDate ?? DateTime.Today;
                    _selected.Days = ScheduleDays.All;
                    _selected.StartDate = date;
                    _selected.EndDate = date;
                    break;
                case ScheduleRepeat.EveryDay:
                    if (_selected.Repeat == ScheduleRepeat.Once) { _selected.StartDate = null; _selected.EndDate = null; }
                    _selected.Days = ScheduleDays.All;
                    break;
                case ScheduleRepeat.SelectedDays:
                    var onceDay = _selected.Repeat == ScheduleRepeat.Once ? _selected.StartDate : null;
                    if (onceDay is not null) { _selected.StartDate = null; _selected.EndDate = null; }
                    // Start with one day ticked (the one-date's weekday, else today) so the choice is visible.
                    _selected.Days = ScheduleRules.DayFlag((onceDay ?? DateTime.Today).DayOfWeek);
                    break;
            }
            RaiseWhen();
        }
    }

    /// <summary>The date of a one-date item.</summary>
    public DateTime? OnceDate
    {
        get => IsOnce ? _selected!.StartDate : null;
        set
        {
            if (_selected is null || value is null) return;
            _selected.StartDate = value.Value.Date;
            _selected.EndDate = value.Value.Date;
            RaiseWhen();
        }
    }

    /// <summary>Optional date limit for repeating items; off = no start or end date.</summary>
    public bool LimitDates
    {
        get => _selected is not null && _selected.Repeat != ScheduleRepeat.Once && (_selected.StartDate is not null || _selected.EndDate is not null);
        set
        {
            if (_selected is null || _selected.Repeat == ScheduleRepeat.Once || value == LimitDates) return;
            if (value) _selected.StartDate = DateTime.Today;
            else { _selected.StartDate = null; _selected.EndDate = null; }
            RaiseWhen();
        }
    }

    public DateTime? ValidFrom
    {
        get => _selected?.StartDate;
        set
        {
            if (_selected is null) return;
            if (value is { } from && _selected.EndDate is { } until && from.Date == until.Date) { Message = "To air on just one day, choose REPEAT · One date."; Raise(); return; }
            _selected.StartDate = value;
            RaiseWhen();
        }
    }

    public DateTime? ValidUntil
    {
        get => _selected?.EndDate;
        set
        {
            if (_selected is null) return;
            // A range that ends on its start day would read as "one date": keep it a range.
            if (value is { } until && _selected.StartDate is { } from && until.Date == from.Date) { Message = "To air on just one day, choose REPEAT · One date."; Raise(); return; }
            _selected.EndDate = value;
            RaiseWhen();
        }
    }

    /// <summary>One plain sentence: when exactly this item plays.</summary>
    public string WhenSummary
    {
        get
        {
            if (_selected is not { } item) return string.Empty;
            if (item.IsDefault) return "Runs every day, all day, whenever nothing else is scheduled. Other filler slots replace it from their start time.";
            var when = item.Repeat switch
            {
                ScheduleRepeat.Once => "Plays once on " + item.StartDate!.Value.ToString("dddd dd MMM yyyy"),
                ScheduleRepeat.EveryDay => "Plays every day",
                _ => "Plays every " + string.Join(", ", AllDays.Where(d => (item.Days & d.Flag) != 0).Select(d => d.Name))
            };
            var time = item.Kind switch
            {
                ScheduleKind.Filler => $" from {item.StartTime:hh\\:mm} until the next filler slot",
                _ when UsesTimes => item.Times.Count == 0 ? " (no air times yet)" : " at " + string.Join(", ", item.Times.Select(t => t.ToString(@"hh\:mm"))),
                ScheduleKind.CgLShape when item.LTrigger == LShapeTrigger.DuringProgramme => $" while '{item.ProgrammeMatch}' is on air",
                _ => item.EndTime is { } end ? $" {item.StartTime:hh\\:mm} – {end:hh\\:mm}" : $" from {item.StartTime:hh\\:mm} for its full length"
            };
            var dates = item.Repeat == ScheduleRepeat.Once ? string.Empty : (item.StartDate, item.EndDate) switch
            {
                (null, null) => ", with no end date",
                ({ } from, null) => $", starting {from:dd MMM yyyy}, with no end date",
                (null, { } to) => $", until {to:dd MMM yyyy}",
                ({ } from, { } to) => $", from {from:dd MMM yyyy} until {to:dd MMM yyyy}"
            };
            var past = item.Repeat == ScheduleRepeat.Once && item.StartDate < DateTime.Today ? "  ⚠ This date has passed." : string.Empty;
            return when + time + dates + "." + past;
        }
    }

    private static readonly (ScheduleDays Flag, string Name)[] AllDays =
        [(ScheduleDays.Mon, "Mon"), (ScheduleDays.Tue, "Tue"), (ScheduleDays.Wed, "Wed"), (ScheduleDays.Thu, "Thu"), (ScheduleDays.Fri, "Fri"), (ScheduleDays.Sat, "Sat"), (ScheduleDays.Sun, "Sun")];

    public bool Mon { get => HasDay(ScheduleDays.Mon); set => SetDay(ScheduleDays.Mon, value); }
    public bool Tue { get => HasDay(ScheduleDays.Tue); set => SetDay(ScheduleDays.Tue, value); }
    public bool Wed { get => HasDay(ScheduleDays.Wed); set => SetDay(ScheduleDays.Wed, value); }
    public bool Thu { get => HasDay(ScheduleDays.Thu); set => SetDay(ScheduleDays.Thu, value); }
    public bool Fri { get => HasDay(ScheduleDays.Fri); set => SetDay(ScheduleDays.Fri, value); }
    public bool Sat { get => HasDay(ScheduleDays.Sat); set => SetDay(ScheduleDays.Sat, value); }
    public bool Sun { get => HasDay(ScheduleDays.Sun); set => SetDay(ScheduleDays.Sun, value); }

    private bool HasDay(ScheduleDays day) => _selected is not null && _selected.Days != ScheduleDays.All && (_selected.Days & day) != 0;

    private void SetDay(ScheduleDays day, bool on)
    {
        if (_selected is null) return;
        var current = _selected.Days == ScheduleDays.All ? ScheduleDays.None : _selected.Days;
        current = on ? current | day : current & ~day;
        if (current == ScheduleDays.None)
        {
            // Unticking the last day would silently mean "every day": keep it, and say how to get every day.
            Message = "Keep at least one day ticked, or choose REPEAT · Every day.";
            RaiseWhen();
            return;
        }
        _selected.Days = current;
        RaiseWhen();
    }

    private void RaiseWhen()
    {
        foreach (var name in new[]
                 {
                     nameof(Mon), nameof(Tue), nameof(Wed), nameof(Thu), nameof(Fri), nameof(Sat), nameof(Sun),
                     nameof(ShowWhen), nameof(SelectedRepeat), nameof(IsOnce), nameof(IsSelectedDays), nameof(CanLimitDates), nameof(ShowDateRange), nameof(ShowNoLimitText),
                     nameof(LimitDates), nameof(OnceDate), nameof(ValidFrom), nameof(ValidUntil), nameof(WhenSummary), nameof(IsAd),
                     nameof(LimitDatesLabel), nameof(NoLimitText)
                 })
            Raise(name);
    }

    private void RaiseEditor()
    {
        foreach (var name in new[]
                 {
                     nameof(IsEditable), nameof(KindEditable), nameof(IsLive), nameof(IsCgSource), nameof(IsMediaSource), nameof(IsFiller),
                     nameof(IsSpot), nameof(IsLShape), nameof(UsesTimes), nameof(UsesWindow), nameof(UsesEndTime), nameof(UsesRepeat),
                     nameof(UsesProgrammeMatch), nameof(KindHint), nameof(PlannedDurationText), nameof(StartTimeText), nameof(EndTimeText),
                     nameof(TimesText), nameof(LiveUrl), nameof(LiveUntilManualCut), nameof(SpotIsCg), nameof(SpotWaitsForClip), nameof(Shuffle),
                     nameof(LTrigger), nameof(RepeatMinutesText), nameof(ProgrammeMatch), nameof(CgLayerText), nameof(CgDurationText), nameof(SourceList), nameof(CgList)
                 })
            Raise(name);
        RaiseWhen();
    }

    // ---- Sources and CG templates -------------------------------------------------------------

    public IReadOnlyList<string> SourceList => _selected?.Sources.ToList() ?? [];
    public IReadOnlyList<string> CgList => _selected?.CgProjects.ToList() ?? [];

    public void AddSources(IEnumerable<string> paths)
    {
        if (_selected is null) return;
        foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p)))
            if (!_selected.Sources.Contains(path, StringComparer.OrdinalIgnoreCase)) _selected.Sources.Add(path);
        SourcesChanged();
    }

    public void RemoveSource(string? path)
    {
        if (_selected is null || path is null) return;
        _selected.Sources.RemoveAll(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase));
        SourcesChanged();
    }

    public void MoveSource(string? path, int delta)
    {
        if (_selected is null || path is null) return;
        var index = _selected.Sources.FindIndex(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase));
        var target = index + delta;
        if (index < 0 || target < 0 || target >= _selected.Sources.Count) return;
        (_selected.Sources[index], _selected.Sources[target]) = (_selected.Sources[target], _selected.Sources[index]);
        SourcesChanged();
    }

    public void AddCg()
    {
        if (_selected is null || string.IsNullOrWhiteSpace(_selectedCgTemplate)) return;
        if (!_selected.CgProjects.Contains(_selectedCgTemplate, StringComparer.OrdinalIgnoreCase)) _selected.CgProjects.Add(_selectedCgTemplate);
        _selected.RaiseSummary();
        MarkChanged();
        Raise(nameof(CgList));
    }

    public void RemoveCg(string? name)
    {
        if (_selected is null || name is null) return;
        _selected.CgProjects.RemoveAll(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
        _selected.RaiseSummary();
        MarkChanged();
        Raise(nameof(CgList));
    }

    private void SourcesChanged()
    {
        var item = _selected!;
        item.RaiseSummary();
        MarkChanged();
        Raise(nameof(SourceList));
        if (item.Kind is ScheduleKind.Filler or ScheduleKind.Live) return;
        // Measure the content length off the UI thread; the resolver uses it for programme/spot windows.
        var sources = item.Sources.ToList();
        _ = Task.Run(() =>
        {
            double total = 0;
            foreach (var source in ScheduleSources.Expand(sources))
            {
                try { total += source.Template?.Duration.TotalSeconds ?? MediaProbe.Read(source.Path).Duration.TotalSeconds; } catch { }
            }
            return total;
        }).ContinueWith(t =>
        {
            if (t.Status != TaskStatus.RanToCompletion) return;
            item.PlannedDurationSeconds = Math.Round(t.Result, 2);
            if (ReferenceEquals(item, _selected)) Raise(nameof(PlannedDurationText));
            RebuildLanes();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ---- Lanes ------------------------------------------------------------------------------------

    public static string ColorFor(ScheduleKind kind) => kind switch
    {
        ScheduleKind.Filler => "#2E7D4F",
        ScheduleKind.Programme => "#1E5FA8",
        ScheduleKind.Movie => "#6A2C9A",
        ScheduleKind.Live => "#B3263A",
        ScheduleKind.News => "#C2601A",
        ScheduleKind.SpotAd => "#B08A12",
        _ => "#0E7C86"
    };

    public void RebuildLanes()
    {
        var schedule = new ChannelSchedule { ChannelId = _channelId, Items = [.. _items] };
        VideoLane.Clear();
        var date = _planDate;
        ScheduleOccurrence? run = null;
        var runStart = 0;
        for (var minute = 0; minute <= 24 * 60; minute++)
        {
            var active = minute < 24 * 60 ? ScheduleResolver.ResolveVideo(schedule, date.AddMinutes(minute)) : null;
            if (run is not null && active?.Key == run.Key) continue;
            if (run is not null) VideoLane.Add(Block(runStart, minute, run.Item));
            run = active;
            runStart = minute;
        }

        CgLane.Clear();
        foreach (var item in _items.Where(ScheduleRules.IsOverlay).Where(x => x.OccursOn(date)))
        {
            if (item.Kind == ScheduleKind.CgBottomAd || (item.Kind == ScheduleKind.CgLShape && item.LTrigger == LShapeTrigger.Interval))
            {
                if (ScheduleResolver.DailyWindow(item, date) is { } window)
                    CgLane.Add(Block((int)(window.Start - date).TotalMinutes, (int)Math.Min(24 * 60, (window.End - date).TotalMinutes), item));
            }
            else if (item.Kind == ScheduleKind.SpotAd || item.LTrigger == LShapeTrigger.AtTimes)
            {
                foreach (var time in item.Times.Count > 0 ? item.Times : new List<TimeSpan> { item.StartTime })
                    CgLane.Add(Block((int)time.TotalMinutes, (int)time.TotalMinutes + 4, item));
            }
        }
    }

    private static ScheduleLaneBlock Block(int fromMinute, int toMinute, ChannelScheduleItem item)
    {
        var width = Math.Max(4, (toMinute - fromMinute) * LaneMinutesScale);
        var tip = $"{ScheduleRules.KindLabel(item.Kind)} · {item.Name}\n{TimeSpan.FromMinutes(fromMinute):hh\\:mm} – {TimeSpan.FromMinutes(Math.Min(toMinute, 24 * 60 - 1)):hh\\:mm}";
        return new ScheduleLaneBlock(fromMinute * LaneMinutesScale, width, item.Name, ColorFor(item.Kind), tip);
    }

    // ---- Validation -----------------------------------------------------------------------------

    public IReadOnlyList<string> Validate()
    {
        var issues = new List<string>();
        foreach (var item in _items.Where(x => x.Enabled))
        {
            var label = $"{ScheduleRules.KindLabel(item.Kind)} '{item.Name}'";
            if (item.StartDate is { } from && item.EndDate is { } to && to < from)
                issues.Add($"{label}: the end date ({to:dd MMM yyyy}) is before the start date ({from:dd MMM yyyy}); it never plays.");
            else if (item.Repeat == ScheduleRepeat.Once && item.StartDate < DateTime.Today)
                issues.Add($"{label}: its date ({item.StartDate:dd MMM yyyy}) has passed; it will not play again.");
            else if (item.EndDate is { } ended && ended < DateTime.Today)
                issues.Add($"{label}: ended on {ended:dd MMM yyyy}; it will not play again.");
            if (ScheduleRules.IsOverlay(item))
            {
                if (item.CgProjects.Count == 0) issues.Add($"{label}: no CG template selected.");
                foreach (var missing in item.CgProjects.Where(p => !CgTemplates.Contains(p, StringComparer.OrdinalIgnoreCase)))
                    issues.Add($"{label}: CG template '{missing}' not found in the library.");
                if (item.Kind == ScheduleKind.CgLShape && item.LTrigger == LShapeTrigger.DuringProgramme && string.IsNullOrWhiteSpace(item.ProgrammeMatch))
                    issues.Add($"{label}: enter the programme title or category to match.");
                continue;
            }
            if (item.Kind == ScheduleKind.Live)
            {
                if (item.Sources.Count == 0) issues.Add($"{label}: enter the stream URL.");
                if (item.LiveEnd == LiveEndMode.AtEndTime && item.EndTime is null) issues.Add($"{label}: set an end time or choose 'until manual cut'.");
                continue;
            }
            if (item.Sources.Count == 0)
            {
                if (item.Kind == ScheduleKind.News) issues.Add($"{label}: no news file added yet (filler will play).");
                else issues.Add($"{label}: no source selected.");
                continue;
            }
            foreach (var source in item.Sources.Where(s => !File.Exists(s) && !Directory.Exists(s)))
                issues.Add($"{label}: missing '{source}'.");
            if (item.Kind == ScheduleKind.News && item.EndTime is null) issues.Add($"{label}: set the news slot end time.");
            if (item.Kind is ScheduleKind.Programme or ScheduleKind.Movie && item.EndTime is null && item.PlannedDurationSeconds <= 0)
                issues.Add($"{label}: length unknown (add sources again or set an end time).");
        }
        var layerClashes = _items.Where(x => x.Enabled && ScheduleRules.IsOverlay(x)).GroupBy(x => x.CgLayer).Where(g => g.Count() > 1);
        foreach (var clash in layerClashes)
            issues.Add($"CG layer {clash.Key} is shared by {string.Join(", ", clash.Select(x => x.Name))}; overlapping items replace each other.");
        return issues;
    }

    public static bool TryParseTime(string? text, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        var value = (text ?? string.Empty).Trim();
        if (value.Length == 0) return false;
        string[] formats = [@"hh\:mm\:ss", @"h\:mm\:ss", @"hh\:mm", @"h\:mm", @"hhmm"];
        if (TimeSpan.TryParseExact(value, formats, CultureInfo.InvariantCulture, out time) && time < TimeSpan.FromDays(1)) return true;
        return false;
    }
}
