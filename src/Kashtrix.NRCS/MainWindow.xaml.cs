using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Text.Json;
using BroadcastPlayout.Services;
using BroadcastPlayout.Models;
using BroadcastPlayout.Views;
using Microsoft.Win32;

namespace Kashtrix.NRCS;

public partial class MainWindow : Window
{
    private readonly NrcsPlatformStore _store = new();
    private readonly BroadcastDatabase _database = new();
    private readonly DispatcherTimer _liveRefresh = new() { Interval = TimeSpan.FromSeconds(1) };
    private NrcsRundown? _rundown;
    private NrcsStory? _story;
    private NrcsStory? _rundownStory;
    private NrcsAssignment? _assignment;
    private NrcsStoryVersion? _selectedVersion;
    private bool _loading;
    private List<CgProject> _cgCatalog = [];

    public MainWindow()
    {
        InitializeComponent();
        WindowChromeActions.ApplyCleanBorder(this);
        AssignmentDeskBox.ItemsSource = new[] { "NEWS", "POLITICS", "BUSINESS", "SPORTS", "INTERNATIONAL", "ENTERTAINMENT", "WEATHER", "DIGITAL" };
        AssignmentStatusBox.ItemsSource = new[] { "PLANNED", "ASSIGNED", "IN PROGRESS", "READY", "HOLD", "DONE", "CANCELLED" };
        AssignmentPriorityBox.ItemsSource = new[] { "LOW", "NORMAL", "HIGH", "URGENT", "BREAKING" };
        StoryStatusBox.ItemsSource = new[] { "DRAFT", "IN PROGRESS", "REVIEW", "APPROVED", "READY", "ON AIR", "COMPLETE" };
        StoryPriorityBox.ItemsSource = new[] { "LOW", "NORMAL", "HIGH", "URGENT", "BREAKING" };
        StoryCategoryBox.ItemsSource = new[] { "NEWS", "POLITICS", "BUSINESS", "SPORTS", "INTERNATIONAL", "ENTERTAINMENT", "WEATHER", "DIGITAL" };
        StoryTypeBox.ItemsSource = new[] { "ANCHOR", "PKG", "VO", "SOT", "LIVE", "REMOTE", "CG", "BREAK", "PROMO", "FILLER" };
        StartModeBox.ItemsSource = new[] { "FOLLOW", "HARD", "MANUAL", "JIP", "HOLD" };
        ItemStatusBox.ItemsSource = new[] { "READY", "PENDING", "PLAYED", "KILLED" };
        AssignmentFilterModeBox.ItemsSource = new[] { "ALL", "DAY", "NEXT 7 DAYS", "OVERDUE", "OPEN ONLY" };
        AssignmentFilterModeBox.SelectedItem = "ALL";
        AssignmentDateFilter.SelectedDate = DateTime.Today;
        DbText.Text = NrcsPlatformStore.DatabasePath;
        CgLayerBox.ItemsSource = Enumerable.Range(0, 100).Select(x => x.ToString()).ToArray();
        RefreshCgCatalog();
        _liveRefresh.Tick += (_, _) => RefreshLiveOnly();
        _liveRefresh.Start();
        RefreshAll();
    }


    private void RefreshCgCatalog()
    {
        try
        {
            var db = new BroadcastDatabase();
            _cgCatalog = db.LoadState("cg-projects", new List<CgProject>());
            if (_cgCatalog.Count == 0) _cgCatalog = CgDemoFactory.CreateDefaults();
            _cgCatalog = _cgCatalog.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
            CgTemplateBox.ItemsSource = _cgCatalog.Select(x => x.Name).ToArray();
            UpdateCgTemplateInfo(CgTemplateBox.Text);
        }
        catch
        {
            _cgCatalog = CgDemoFactory.CreateDefaults().OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
            CgTemplateBox.ItemsSource = _cgCatalog.Select(x => x.Name).ToArray();
        }
    }

    private void CgTemplateBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        Dispatcher.BeginInvoke(new Action(() => UpdateCgTemplateInfo(CgTemplateBox.Text)));
    }

    private void UpdateCgTemplateInfo(string? name)
    {
        var project = _cgCatalog.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (project is null)
        {
            CgTemplateInfoText.Text = "Choose from the canonical 200-template catalog, or type an exact project name.";
            return;
        }
        var textLayers = project.Layers.Count(x => x.Type.Equals("Text", StringComparison.OrdinalIgnoreCase));
        var animated = project.Layers.Count(x => x.Keyframes.Count > 0 || !x.AnimationIn.Equals("None", StringComparison.OrdinalIgnoreCase));
        CgTemplateInfoText.Text = $"{project.Layers.Count} layers · {textLayers} text · {animated} animated · {project.DurationSeconds:0.#} sec · ID {project.Id.ToString()[..8]}";
    }

    private void RefreshAll()
    {
        _loading = true;
        try
        {
            RefreshRundowns();
            RefreshStoryBank();
            RefreshAssignments();
            RefreshRundownStories();
            RefreshCgCatalog();
            AssignmentStoryBox.ItemsSource = _store.ListStoryBank();
        }
        finally { _loading = false; }
    }

    private void RefreshRundowns()
    {
        var rows = _store.ListRundowns();
        RundownList.ItemsSource = rows;
        if (_rundown is not null) RundownList.SelectedItem = rows.FirstOrDefault(x => x.Id == _rundown.Id);
        if (RundownList.SelectedItem is null && rows.Count > 0)
        {
            _rundown = rows[0];
            RundownList.SelectedItem = _rundown;
        }
    }

    private void RefreshStoryBank(string? search = null)
    {
        var rows = _store.ListStoryBank(search);
        StoryBankGrid.ItemsSource = rows;
        if (_story is not null) StoryBankGrid.SelectedItem = rows.FirstOrDefault(x => x.Id == _story.Id);
        AssignmentStoryBox.ItemsSource = _store.ListStoryBank();
    }

    private void RefreshAssignments()
    {
        IEnumerable<NrcsAssignment> query = _store.ListAssignments();
        var mode = Convert.ToString(AssignmentFilterModeBox.SelectedItem) ?? "ALL";
        var day = (AssignmentDateFilter.SelectedDate ?? DateTime.Today).Date;
        if (mode == "DAY") query = query.Where(x => x.DueUtc.ToLocalTime().Date == day);
        else if (mode == "NEXT 7 DAYS")
        {
            var start = DateTime.Today;
            var end = start.AddDays(7);
            query = query.Where(x => x.DueUtc.ToLocalTime() >= start && x.DueUtc.ToLocalTime() < end);
        }
        else if (mode == "OVERDUE") query = query.Where(x => x.DueUtc.ToLocalTime() < DateTime.Now && !IsClosedAssignment(x.Status));
        else if (mode == "OPEN ONLY") query = query.Where(x => !IsClosedAssignment(x.Status));
        var rows = query.OrderBy(x => x.DueUtc).ThenBy(x => x.Priority).ToList();
        AssignmentGrid.ItemsSource = rows;
        AssignmentCountText.Text = $"{rows.Count} shown · {mode}";
        if (_assignment is not null) AssignmentGrid.SelectedItem = rows.FirstOrDefault(x => x.Id == _assignment.Id);
    }

    private static bool IsClosedAssignment(string status) => status is "DONE" or "CANCELLED";

    private void AssignmentFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _loading) return;
        RefreshAssignments();
    }

    private void AssignmentToday_Click(object sender, RoutedEventArgs e)
    {
        AssignmentDateFilter.SelectedDate = DateTime.Today;
        AssignmentFilterModeBox.SelectedItem = "DAY";
        RefreshAssignments();
    }

    private void RefreshRundownStories()
    {
        if (_rundown is null)
        {
            RundownGrid.ItemsSource = null;
            RundownHeader.Text = "SELECT A RUNDOWN";
            RundownTimingText.Text = "";
            LiveStateText.Text = "OFF AIR";
            return;
        }

        var rows = _store.ListStories(_rundown.Id);
        RundownGrid.ItemsSource = rows;
        if (_rundownStory is not null) RundownGrid.SelectedItem = rows.FirstOrDefault(x => x.RundownItemId == _rundownStory.RundownItemId);
        var summary = _store.GetRundownSummary(_rundown.Id);
        RundownHeader.Text = $"{_rundown.Name} · {_rundown.ChannelId} · {_rundown.Status}";
        RundownTimingText.Text = $"Air {_rundown.AirDateUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {summary.Stories} items · {summary.Skipped} skipped · total {TimeSpan.FromSeconds(summary.DurationSeconds):hh\\:mm\\:ss}";
        RefreshLiveOnly();
    }

    private void RefreshLiveOnly()
    {
        if (_loading || _rundown is null) return;
        var live = _store.GetLiveState(_rundown.Id);
        if (live is null || !live.OnAir)
        {
            LiveStateText.Text = "OFF AIR · no live story selected";
            return;
        }
        var story = _store.GetStory(live.StoryId);
        LiveStateText.Text = story is null ? "ON AIR · unknown story" : $"ON AIR · {story.Slug} · {story.Presenter} · {live.UpdatedUtc.ToLocalTime():HH:mm:ss}";
    }

    private void NewAssignment_Click(object sender, RoutedEventArgs e)
    {
        _assignment = _store.CreateAssignment();
        RefreshAssignments();
        AssignmentGrid.SelectedItem = _assignment;
        WorkspaceTabs.SelectedIndex = 0;
        StatusText.Text = "New assignment created.";
    }

    private void NewStory_Click(object sender, RoutedEventArgs e)
    {
        _story = _store.CreateStoryDraft();
        RefreshStoryBank();
        StoryBankGrid.SelectedItem = _story;
        WorkspaceTabs.SelectedIndex = 1;
        StatusText.Text = "Independent story created in Story Bank.";
    }

    private void NewRundown_Click(object sender, RoutedEventArgs e)
    {
        _rundown = _store.CreateRundown("NEWS " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"), "KTX-PLAYOUT-01", DateTime.UtcNow);
        RefreshRundowns();
        RundownList.SelectedItem = _rundown;
        WorkspaceTabs.SelectedIndex = 2;
        StatusText.Text = "New rundown created.";
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshAll();
        StatusText.Text = "Refreshed from newsroom database.";
    }

    private void LoadProfessionalDemo_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _rundown = _store.RebuildProfessionalDemo();
            RefreshAll();
            RundownList.SelectedItem = _store.ListRundowns().FirstOrDefault(x => x.Id == _rundown.Id);
            WorkspaceTabs.SelectedIndex = 2;
            StatusText.Text = "Professional demo loaded: Anchor / PKG / VO / SOT / LIVE / CG with MOS, Playout and Prompter data.";
        }
        catch (Exception ex) { StatusText.Text = "Demo load failed: " + ex.GetBaseException().Message; }
    }

    private void OpenPrompter_Click(object sender, RoutedEventArgs e)
    {
        if (StandaloneAppLauncher.Launch("Prompter", out var error)) StatusText.Text = "Prompter opened as a separate application.";
        else StatusText.Text = error;
    }

    private void AssignmentGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _assignment = AssignmentGrid.SelectedItem as NrcsAssignment;
        if (_assignment is null) return;
        AssignmentTitleBox.Text = _assignment.Title;
        AssignmentAngleBox.Text = _assignment.Angle;
        AssignmentAssigneeBox.Text = _assignment.Assignee;
        AssignmentDeskBox.SelectedItem = _assignment.Desk;
        AssignmentLocationBox.Text = _assignment.Location;
        AssignmentStatusBox.SelectedItem = _assignment.Status;
        AssignmentPriorityBox.SelectedItem = _assignment.Priority;
        AssignmentDueBox.Text = _assignment.DueUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        AssignmentStoryBox.SelectedValue = _assignment.LinkedStoryId;
        AssignmentNotesBox.Text = _assignment.Notes;
    }

    private NrcsAssignment? AssignmentFromEditor()
    {
        if (_assignment is null) return null;
        var due = DateTime.TryParse(AssignmentDueBox.Text, out var d) ? d.ToUniversalTime() : DateTime.UtcNow.AddHours(2);
        return _assignment with
        {
            Title = AssignmentTitleBox.Text.Trim(), Angle = AssignmentAngleBox.Text, Assignee = AssignmentAssigneeBox.Text.Trim(),
            Desk = Convert.ToString(AssignmentDeskBox.SelectedItem) ?? "NEWS", Location = AssignmentLocationBox.Text.Trim(),
            Status = Convert.ToString(AssignmentStatusBox.SelectedItem) ?? "PLANNED", Priority = Convert.ToString(AssignmentPriorityBox.SelectedItem) ?? "NORMAL",
            DueUtc = due, LinkedStoryId = Convert.ToString(AssignmentStoryBox.SelectedValue) ?? "", Notes = AssignmentNotesBox.Text, UpdatedUtc = DateTime.UtcNow
        };
    }

    private void SaveAssignment_Click(object sender, RoutedEventArgs e)
    {
        var row = AssignmentFromEditor(); if (row is null) return;
        _assignment = row; _store.SaveAssignment(row); RefreshAssignments(); StatusText.Text = "Assignment saved.";
    }

    private void DeleteAssignment_Click(object sender, RoutedEventArgs e)
    {
        if (_assignment is null) return;
        _store.DeleteAssignment(_assignment.Id); _assignment = null; RefreshAssignments(); StatusText.Text = "Assignment deleted.";
    }

    private void CreateStoryFromAssignment_Click(object sender, RoutedEventArgs e)
    {
        var row = AssignmentFromEditor(); if (row is null) return;
        _store.SaveAssignment(row); _assignment = row;
        _story = _store.CreateStoryFromAssignment(row);
        RefreshAssignments(); RefreshStoryBank(); StoryBankGrid.SelectedItem = _story; WorkspaceTabs.SelectedIndex = 1;
        StatusText.Text = "Story created and linked to assignment.";
    }

    private void SearchStories_Click(object sender, RoutedEventArgs e) => RefreshStoryBank(StorySearchBox.Text);
    private void StorySearchBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { RefreshStoryBank(StorySearchBox.Text); e.Handled = true; } }

    private void StoryBankGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _story = StoryBankGrid.SelectedItem as NrcsStory;
        if (_story is null) return;
        LoadStoryEditor(_story);
    }

    private void LoadStoryEditor(NrcsStory s)
    {
        SlugBox.Text = s.Slug; PresenterBox.Text = s.Presenter; WriterBox.Text = s.Writer; EditorBox.Text = s.Editor;
        StoryStatusBox.SelectedItem = s.Status; StoryPriorityBox.SelectedItem = s.Priority; StoryCategoryBox.SelectedItem = s.Category; StoryTypeBox.SelectedItem = s.StoryType;
        DurationBox.Text = s.DurationSeconds.ToString(); LanguageBox.Text = s.Language; BodyBox.Text = s.Body; StoryNotesBox.Text = s.Notes;
        MediaPathBox.Text = s.MediaPath; CgTemplateBox.Text = s.CgTemplate; CgLayerBox.Text = s.CgLayer.ToString(); CgDataBox.Text = s.CgDataJson; UpdateCgTemplateInfo(s.CgTemplate);
        VersionGrid.ItemsSource = _store.ListStoryVersions(s.Id);
        CurrentRevisionBodyBox.Text = s.Body;
        SelectedRevisionBodyBox.Text = "";
        VersionCompareText.Text = "";
        _selectedVersion = null;
    }

    private void VersionGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _selectedVersion = VersionGrid.SelectedItem as NrcsStoryVersion;
        CurrentRevisionBodyBox.Text = BodyBox.Text;
        SelectedRevisionBodyBox.Text = _selectedVersion?.Body ?? "";
        if (_selectedVersion is null) { VersionCompareText.Text = ""; return; }
        var currentLines = NormalizeLines(BodyBox.Text);
        var revisionLines = NormalizeLines(_selectedVersion.Body);
        var common = currentLines.Zip(revisionLines).Count(x => string.Equals(x.First, x.Second, StringComparison.Ordinal));
        VersionCompareText.Text = $"v{_selectedVersion.VersionId} · current {currentLines.Length} lines · revision {revisionLines.Length} · same-position {common}";
    }

    private void RestoreRevision_Click(object sender, RoutedEventArgs e)
    {
        if (_story is null || _selectedVersion is null) { StatusText.Text = "Select a revision first."; return; }
        var restored = _story with
        {
            Slug = _selectedVersion.Slug,
            Body = _selectedVersion.Body,
            Writer = _selectedVersion.Writer,
            Editor = _selectedVersion.Editor,
            Status = _selectedVersion.Status,
            UpdatedUtc = DateTime.UtcNow
        };
        try
        {
            using var doc = JsonDocument.Parse(_selectedVersion.SnapshotJson);
            var root = doc.RootElement;
            restored = restored with
            {
                Presenter = JsonString(root, "Presenter", restored.Presenter),
                DurationSeconds = JsonInt(root, "DurationSeconds", restored.DurationSeconds),
                MediaPath = JsonString(root, "MediaPath", restored.MediaPath),
                CgTemplate = JsonString(root, "CgTemplate", restored.CgTemplate),
                CgLayer = JsonInt(root, "CgLayer", restored.CgLayer),
                CgDataJson = JsonString(root, "CgDataJson", restored.CgDataJson),
                Priority = JsonString(root, "Priority", restored.Priority),
                StoryType = JsonString(root, "StoryType", restored.StoryType),
                Category = JsonString(root, "Category", restored.Category),
                Language = JsonString(root, "Language", restored.Language),
                Notes = JsonString(root, "Notes", restored.Notes)
            };
        }
        catch { }
        var restoredVersionId = _selectedVersion.VersionId;
        _story = restored;
        _store.SaveStory(restored, createVersion: true);
        RefreshStoryBank(StorySearchBox.Text);
        StoryBankGrid.SelectedItem = _story;
        LoadStoryEditor(_story);
        RefreshRundownStories();
        StatusText.Text = $"Revision v{restoredVersionId} restored and captured as a new revision.";
    }

    private static string[] NormalizeLines(string? text) => (text ?? "").Replace("\r\n", "\n").Split('\n');
    private static string JsonString(JsonElement root, string name, string fallback) => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? fallback : fallback;
    private static int JsonInt(JsonElement root, string name, int fallback) => root.TryGetProperty(name, out var p) && p.TryGetInt32(out var value) ? value : fallback;

    private NrcsStory? StoryFromEditor()
    {
        if (_story is null) return null;
        var duration = int.TryParse(DurationBox.Text, out var d) ? Math.Max(1, d) : 30;
        var layer = int.TryParse(CgLayerBox.Text, out var l) ? Math.Clamp(l, 0, 999) : 20;
        return _story with
        {
            Slug = SlugBox.Text.Trim(), Presenter = PresenterBox.Text.Trim(), Writer = WriterBox.Text.Trim(), Editor = EditorBox.Text.Trim(),
            Status = Convert.ToString(StoryStatusBox.SelectedItem) ?? "DRAFT", Priority = Convert.ToString(StoryPriorityBox.SelectedItem) ?? "NORMAL",
            Category = Convert.ToString(StoryCategoryBox.SelectedItem) ?? "NEWS", StoryType = Convert.ToString(StoryTypeBox.SelectedItem) ?? "PKG", Language = string.IsNullOrWhiteSpace(LanguageBox.Text) ? "en" : LanguageBox.Text.Trim(),
            DurationSeconds = duration, Body = BodyBox.Text, Notes = StoryNotesBox.Text, MediaPath = MediaPathBox.Text.Trim(),
            CgTemplate = CgTemplateBox.Text.Trim(), CgLayer = layer, CgDataJson = CgDataBox.Text, UpdatedUtc = DateTime.UtcNow
        };
    }

    private void SaveStory_Click(object sender, RoutedEventArgs e)
    {
        var row = StoryFromEditor(); if (row is null) return;
        _story = row; _store.SaveStory(row); RefreshStoryBank(StorySearchBox.Text); StoryBankGrid.SelectedItem = _story; VersionGrid.ItemsSource = _store.ListStoryVersions(row.Id);
        RefreshRundownStories(); StatusText.Text = "Story saved and revision captured.";
    }

    private void DeleteStory_Click(object sender, RoutedEventArgs e)
    {
        if (_story is null) return;
        _store.DeleteStory(_story.Id); _story = null; RefreshStoryBank(StorySearchBox.Text); RefreshAssignments(); RefreshRundownStories(); StatusText.Text = "Story deleted from Story Bank and rundowns.";
    }

    private void BrowseMedia_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Title = "Attach MAM / media file", Filter = "Media files|*.mxf;*.mov;*.mp4;*.mkv;*.avi;*.ts;*.m2ts;*.wav;*.mpg;*.mpeg|All files|*.*" };
        if (d.ShowDialog() == true) MediaPathBox.Text = d.FileName;
    }

    private void AddStoryToRundown_Click(object sender, RoutedEventArgs e)
    {
        var row = StoryFromEditor(); if (row is not null) { _story = row; _store.SaveStory(row); }
        if (_story is null) { StatusText.Text = "Select a Story Bank story first."; return; }
        if (_rundown is null) { StatusText.Text = "Select/create a rundown in Rundown / Live first."; return; }
        _store.AddStoryToRundown(_story.Id, _rundown.Id); RefreshRundownStories(); WorkspaceTabs.SelectedIndex = 2; StatusText.Text = $"Added {_story.Slug} to {_rundown.Name}.";
    }

    private void RundownList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _rundown = RundownList.SelectedItem as NrcsRundown; _rundownStory = null; RefreshRundownStories();
    }

    private void RundownGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _rundownStory = RundownGrid.SelectedItem as NrcsStory;
        if (_rundownStory is null) return;
        RundownItemSlug.Text = $"{_rundownStory.Sequence:00} · {_rundownStory.Slug} · {_rundownStory.Presenter}";
        SegmentBox.Text = _rundownStory.Segment; StartModeBox.SelectedItem = _rundownStory.StartMode; ItemStatusBox.SelectedItem = _rundownStory.ItemStatus; SkipCheck.IsChecked = _rundownStory.IsSkipped; LockCheck.IsChecked = _rundownStory.IsLocked;
    }

    private void SaveRundownItem_Click(object sender, RoutedEventArgs e)
    {
        if (_rundownStory is null) return;
        _rundownStory = _rundownStory with { Segment = SegmentBox.Text.Trim(), StartMode = Convert.ToString(StartModeBox.SelectedItem) ?? "FOLLOW", ItemStatus = Convert.ToString(ItemStatusBox.SelectedItem) ?? "READY", IsSkipped = SkipCheck.IsChecked == true, IsLocked = LockCheck.IsChecked == true };
        _store.UpdateRundownItem(_rundownStory); RefreshRundownStories(); StatusText.Text = "Rundown item updated.";
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) { if (_rundown is null || _rundownStory is null || _rundownStory.IsLocked) return; _store.MoveRundownItem(_rundown.Id, _rundownStory.RundownItemId, -1); RefreshRundownStories(); }
    private void MoveDown_Click(object sender, RoutedEventArgs e) { if (_rundown is null || _rundownStory is null || _rundownStory.IsLocked) return; _store.MoveRundownItem(_rundown.Id, _rundownStory.RundownItemId, 1); RefreshRundownStories(); }
    private void RemoveRundownItem_Click(object sender, RoutedEventArgs e) { if (_rundownStory is null || _rundownStory.IsLocked) return; _store.RemoveRundownItem(_rundownStory.RundownItemId); _rundownStory = null; RefreshRundownStories(); StatusText.Text = "Story reference removed from rundown; Story Bank copy retained."; }

    private async void TakeLive_Click(object sender, RoutedEventArgs e)
    {
        if (_rundown is null || _rundownStory is null || _rundownStory.IsSkipped) return;
        _store.SetLiveStory(_rundown.Id, _rundownStory.Id, _rundownStory.RundownItemId, true); _store.SetRundownStatus(_rundown.Id, "ON AIR"); RefreshRundowns(); RefreshRundownStories(); StatusText.Text = $"Live story: {_rundownStory.Slug}";
        if (AutoAirCheck.IsChecked == true) await TriggerStoryAutomationAsync(_rundownStory);
    }

    private async void NextLive_Click(object sender, RoutedEventArgs e)
    {
        if (_rundown is null) return;
        var rows = _store.ListStories(_rundown.Id).Where(x => !x.IsSkipped).ToList(); if (rows.Count == 0) return;
        var live = _store.GetLiveState(_rundown.Id); var index = live is null ? -1 : rows.FindIndex(x => x.RundownItemId == live.RundownItemId); var next = rows[Math.Min(rows.Count - 1, index + 1)];
        _store.SetLiveStory(_rundown.Id, next.Id, next.RundownItemId, true); _rundownStory = next; RefreshRundownStories(); StatusText.Text = $"Next live: {next.Slug}";
        if (AutoAirCheck.IsChecked == true) await TriggerStoryAutomationAsync(next);
    }


    private string ResolveLiveChannelId()
    {
        var configured = _rundown?.ChannelId;
        if (!string.IsNullOrWhiteSpace(configured) && PlatformControlBus.ReadStatuses().Any(x => string.Equals(x.ChannelId, configured, StringComparison.OrdinalIgnoreCase))) return configured;
        return PlatformControlBus.ReadStatuses().OrderByDescending(x => x.UpdatedUtc).FirstOrDefault()?.ChannelId ?? configured ?? "KTX-PLAYOUT-01";
    }

    private async Task TriggerStoryAutomationAsync(NrcsStory story)
    {
        try
        {
            var channelId = ResolveLiveChannelId();
            var response = await PlatformControlBus.SubmitAndWaitAsync(new PlatformControlCommand
            {
                ChannelId = channelId, Action = "play_story",
                Parameters = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase) { ["storyId"] = story.Id, ["slug"] = story.Slug }
            }, TimeSpan.FromSeconds(3));
            if (!response.Ok) { StatusText.Text = $"Live state set; playout rejected story: {response.Message}"; return; }
            if (!string.IsNullOrWhiteSpace(story.CgTemplate))
            {
                var projects = _database.LoadState("cg-projects", new List<CgProject>());
                var project = projects.FirstOrDefault(x => string.Equals(x.Name, story.CgTemplate, StringComparison.OrdinalIgnoreCase));
                if (project is not null)
                {
                    project.ExternalLayer = story.CgLayer;
                    LocalCgCommandBus.Publish(new[] { channelId }, new CgRemoteCommand { Action = "PLAY", Bus = "PROGRAM", Layer = story.CgLayer, Project = project });
                }
            }
            StatusText.Text = $"ON AIR automation sent: {story.Slug} → {channelId} · CG {story.CgTemplate} layer {story.CgLayer}";
        }
        catch (Exception ex) { StatusText.Text = "Live automation failed: " + ex.GetBaseException().Message; }
    }

    private void ClearLive_Click(object sender, RoutedEventArgs e)
    {
        if (_rundown is null) return; _store.SetLiveStory(_rundown.Id, "", "", false); RefreshRundownStories(); StatusText.Text = "On-air story state cleared.";
    }

    private void PublishPlayout_Click(object sender, RoutedEventArgs e)
    {
        if (_rundown is null) return;
        try { var count = _store.PublishToPlayout(_rundown.Id); RefreshRundowns(); StatusText.Text = $"Published {count} playout events to {_rundown.ChannelId}."; }
        catch (Exception ex) { StatusText.Text = "Publish failed: " + ex.GetBaseException().Message; }
    }

    private async void PublishMos_Click(object sender, RoutedEventArgs e)
    {
        if (_rundown is null) return;
        try { var result = await _store.PublishMosAsync(_rundown.Id); StatusText.Text = result.Sent ? $"MOS sent · {result.Message} · {result.Path}" : $"MOS outbox saved; network send failed · {result.Message} · {result.Path}"; }
        catch (Exception ex) { StatusText.Text = "MOS publish failed: " + ex.GetBaseException().Message; }
    }
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => WindowChromeActions.Drag(this, e);
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowChromeActions.Minimize(this);
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowChromeActions.ToggleMaximize(this);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

}
