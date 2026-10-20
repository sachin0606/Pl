using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Documents;
using Microsoft.Win32;
using System.Windows.Threading;
using BroadcastPlayout.Models;
using BroadcastPlayout.Outputs;
using BroadcastPlayout.Services;
using BroadcastPlayout.Views;
using Forms = System.Windows.Forms;

namespace Kashtrix.Prompter;

public partial class MainWindow : Window
{
    private readonly NrcsPlatformStore _store = new();
    private readonly BroadcastDatabase _database = new();
    private readonly DispatcherTimer _engineTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly NdiSender _ndi = new();
    private readonly DeckLinkOutputAdapter _deckLink = new();
    private readonly PrompterRemoteServer _remote = new();
    private readonly Stopwatch _ndiPacer = Stopwatch.StartNew();
    private PrompterDisplayWindow? _displayWindow;
    private NrcsRundown? _rundown;
    private NrcsStory? _story;
    private bool _loading;
    private bool _running;
    private bool _ndiEnabled;
    private bool _deckLinkEnabled;
    private double _scrollOffset;
    private int _cardIndex;
    private long _ndiFrames;
    private TimeSpan _elapsedBase;
    private DateTime _runStartedUtc;
    private DateTime _lastStatusUtc;
    private bool _offlineOverride;
    private string _offlineBody = "";
    private string _offlineSlug = "";
    private string _offlinePresenter = "";
    private readonly Dictionary<string, List<double>> _bookmarksByContext = new(StringComparer.Ordinal);

    public MainWindow()
    {
        InitializeComponent();
        WindowChromeActions.ApplyCleanBorder(this);
        PromptModeBox.ItemsSource = new[] { "FLOW", "CARDS" };
        PromptModeBox.SelectedIndex = 0;
        ResolutionBox.ItemsSource = new[] { "1920x1080", "1280x720", "720x576" };
        ResolutionBox.SelectedIndex = 0;
        FpsBox.ItemsSource = new[] { 25.0, 30.0, 50.0, 60.0 };
        FpsBox.SelectedItem = 25.0;
        DeckLinkBox.ItemsSource = DeckLinkOutputAdapter.EnumerateDevices();
        if (DeckLinkBox.Items.Count > 0) DeckLinkBox.SelectedIndex = 0;
        HeaderCheck.IsChecked = false;
        ClockCheck.IsChecked = false;
        TimerCheck.IsChecked = true;
        RefreshDisplays();
        _engineTimer.Tick += EngineTick;
        _refreshTimer.Tick += (_, _) => RefreshFromDb();
        _remote.CommandReceived += (command, endpoint) => Dispatcher.BeginInvoke(new Action(() => HandleRemoteCommand(command, endpoint.ToString())));
        _engineTimer.Start();
        _refreshTimer.Start();
        RefreshRundowns();
        RefreshFromDb();
        RefreshProductionContext();
        RenderOutputs(forceStatus: true);
    }

    private void RefreshDisplays()
    {
        var screens = Forms.Screen.AllScreens;
        DisplayBox.ItemsSource = screens.Select((s, i) => $"{i} · {s.DeviceName} · {s.Bounds.Width}x{s.Bounds.Height} @ {s.Bounds.Left},{s.Bounds.Top}").ToArray();
        if (screens.Length > 0 && DisplayBox.SelectedIndex < 0) DisplayBox.SelectedIndex = screens.ToList().FindIndex(x => !x.Primary) is var secondary && secondary >= 0 ? secondary : 0;
    }

    private void RefreshRundowns()
    {
        _loading = true;
        try
        {
            var rows = _store.ListRundowns();
            RundownBox.ItemsSource = rows;
            if (_rundown is not null) RundownBox.SelectedItem = rows.FirstOrDefault(x => x.Id == _rundown.Id);
            if (RundownBox.SelectedItem is null && rows.Count > 0)
            {
                _rundown = rows[0];
                RundownBox.SelectedItem = _rundown;
            }
        }
        finally { _loading = false; }
    }

    private void RefreshFromDb()
    {
        if (_loading) return;
        if (_rundown is null) { RefreshRundowns(); return; }
        _loading = true;
        try
        {
            var rows = _store.ListStories(_rundown.Id);
            var selectedItemId = _story?.RundownItemId;
            var live = _store.GetLiveState(_rundown.Id);
            if (FollowLiveCheck.IsChecked == true && live is not null && live.OnAir) selectedItemId = live.RundownItemId;
            StoryList.ItemsSource = rows;
            var selected = rows.FirstOrDefault(x => x.RundownItemId == selectedItemId) ?? rows.FirstOrDefault(x => !x.IsSkipped) ?? rows.FirstOrDefault();
            if (selected is not null)
            {
                var storyChanged = _story?.RundownItemId != selected.RundownItemId;
                _story = selected;
                StoryList.SelectedItem = selected;
                if (storyChanged && !_offlineOverride)
                {
                    ResetStoryPosition();
                    ApplyPresenterProfileInternal(selected.Presenter, silent: true);
                }
                UpdateStoryHeader();
                RefreshProductionContext();
            }
            RunOrderStatusText.Text = $"{_rundown.Name} · {rows.Count} items · live sync {(FollowLiveCheck.IsChecked == true ? "ON" : "OFF")}";
        }
        finally { _loading = false; }
    }

    private void RundownBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _rundown = RundownBox.SelectedItem as NrcsRundown;
        _story = null;
        ResetStoryPosition();
        RefreshFromDb();
    }

    private void StoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var selected = StoryList.SelectedItem as NrcsStory;
        if (selected is null) return;
        if (_story?.RundownItemId != selected.RundownItemId) ResetStoryPosition();
        _story = selected;
        UpdateStoryHeader();
        RefreshProductionContext();
        RenderOutputs(forceStatus: true);
    }

    private void UpdateStoryHeader()
    {
        if (_offlineOverride)
        {
            StoryHeader.Text = $"EMERGENCY / OFFLINE · {_offlineSlug} · {_offlinePresenter}";
            OfflineStatusText.Text = "EMERGENCY LOCAL SCRIPT ACTIVE · NRCS changes continue in background but do not replace prompt text.";
            UpdateCardStatus();
            return;
        }
        OfflineStatusText.Text = "NRCS SCRIPT MODE";
        if (_story is null) { StoryHeader.Text = "No story selected"; return; }
        StoryHeader.Text = $"{_story.Sequence:00} · {_story.Slug} · {_story.Presenter} · {_story.DurationSeconds}s · {_story.Status}";
        UpdateCardStatus();
    }

    private void ResetStoryPosition()
    {
        _scrollOffset = 0;
        _cardIndex = 0;
        _elapsedBase = TimeSpan.Zero;
        _runStartedUtc = DateTime.UtcNow;
        UpdateCardStatus();
        UpdateBookmarkStatus();
    }

    private void EngineTick(object? sender, EventArgs e)
    {
        if (_running && !IsCardMode) _scrollOffset += Math.Max(0.05, SpeedSlider.Value);
        RenderOutputs();
        ScrollStatusText.Text = IsCardMode ? $"CARD {_cardIndex + 1}/{CardCount} · {(_running ? "RUN" : "PAUSE")}" : $"{(_running ? "RUN" : "PAUSE")} · speed {SpeedSlider.Value:0.0} · offset {_scrollOffset:0}";
    }

    private bool IsCardMode => string.Equals(Convert.ToString(PromptModeBox.SelectedItem), "CARDS", StringComparison.OrdinalIgnoreCase);
    private string CurrentBody => _offlineOverride ? _offlineBody : (_story?.Body ?? "Select an NRCS rundown and story.");
    private string CurrentSlug => _offlineOverride ? _offlineSlug : (_story?.Slug ?? "NO STORY");
    private string CurrentPresenter => _offlineOverride ? _offlinePresenter : (_story?.Presenter ?? "");
    private string BookmarkContextKey => _offlineOverride ? "OFFLINE:" + _offlineSlug : (_story?.RundownItemId ?? "NONE");
    private List<double> CurrentBookmarks
    {
        get
        {
            if (!_bookmarksByContext.TryGetValue(BookmarkContextKey, out var list))
            {
                list = [];
                _bookmarksByContext[BookmarkContextKey] = list;
            }
            return list;
        }
    }
    private int CardCount => PrompterRenderer.SplitCards(CurrentBody).Length;

    private void RenderOutputs(bool forceStatus = false)
    {
        var state = BuildState();
        var pw = Math.Max(640, (int)Math.Round(PreviewHost.ActualWidth));
        var ph = Math.Max(360, (int)Math.Round(PreviewHost.ActualHeight));
        PreviewHost.Content = PrompterRenderer.BuildVisual(state, pw, ph);
        PreviewModeText.Text = IsCardMode ? "CARD PREVIEW" : "FLOW PREVIEW";

        if (_displayWindow is { IsVisible: true }) _displayWindow.UpdateFrame(state);

        if (_ndiEnabled || _deckLinkEnabled)
        {
            var fps = SelectedFps;
            if (_ndiPacer.Elapsed.TotalSeconds >= (1.0 / fps))
            {
                _ndiPacer.Restart();
                var (w, h) = SelectedResolution;
                try
                {
                    var bytes = PrompterRenderer.RenderBgra(state, w, h);
                    var frame = new VideoFrameData(bytes, w, h, w * 4, _ndiFrames / fps, (int)Math.Round(fps), 1);
                    var profile = new OutputProfile { Preset = Convert.ToString(ResolutionBox.SelectedItem) ?? "1920x1080", FramesPerSecond = fps };
                    if (_ndiEnabled) _ndi.SendVideo(frame, profile);
                    if (_deckLinkEnabled) _deckLink.SendVideo(frame, profile);
                    _ndiFrames++;
                }
                catch (Exception ex)
                {
                    NdiStatusText.Text = "Output render/send error · " + ex.GetBaseException().Message;
                }
            }
        }

        if (forceStatus || (DateTime.UtcNow - _lastStatusUtc).TotalSeconds >= 1)
        {
            _lastStatusUtc = DateTime.UtcNow;
            NdiStatusText.Text = _ndiEnabled ? $"NDI ON · {_ndi.ConnectionCount} receiver(s) · {_ndi.VideoFramesSent} frames · {_ndi.LastError}".TrimEnd(' ', '·') : "NDI OFF";
            DeckLinkStatusText.Text = _deckLinkEnabled ? $"SDI ON · device {_deckLink.DeviceIndex + 1} · {(_deckLink.IsSupportedBuild ? "SDK READY" : "SDK INTEROP REQUIRED")} · {_deckLink.LastError}".TrimEnd(' ', '·') : "SDI OFF";
            OutputStatusText.Text = $"Display {(_displayWindow is { IsVisible: true } ? _displayWindow.ScreenName : "OFF")} · NDI {(_ndiEnabled ? "ON" : "OFF")} · SDI {(_deckLinkEnabled ? "ON" : "OFF")} · {SelectedResolution.Width}x{SelectedResolution.Height}@{SelectedFps:0}";
        }
    }

    private PrompterVisualState BuildState()
    {
        var margin = int.TryParse(SafeMarginBox.Text, out var m) ? Math.Clamp(m, 20, 500) : 80;
        return new PrompterVisualState(
            CurrentBody,
            CurrentSlug,
            CurrentPresenter,
            _scrollOffset,
            FontSlider.Value,
            LineSpacingSlider.Value,
            MirrorCheck.IsChecked == true,
            FlipCheck.IsChecked == true,
            InvertCheck.IsChecked == true,
            RtlCheck.IsChecked == true,
            BlankCheck.IsChecked == true,
            IsCardMode,
            _cardIndex,
            MessageBox.Text,
            ClockCheck.IsChecked == true,
            TimerCheck.IsChecked == true,
            Elapsed,
            margin,
            HeaderCheck.IsChecked == true);
    }

    private TimeSpan Elapsed => _elapsedBase + (_running ? DateTime.UtcNow - _runStartedUtc : TimeSpan.Zero);

    private (int Width, int Height) SelectedResolution
    {
        get
        {
            var text = Convert.ToString(ResolutionBox.SelectedItem) ?? "1920x1080";
            var parts = text.Split('x');
            return parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h) ? (w, h) : (1920, 1080);
        }
    }

    private double SelectedFps => FpsBox.SelectedItem is double d ? d : 25.0;

    private void Refresh_Click(object sender, RoutedEventArgs e) { RefreshRundowns(); RefreshFromDb(); RenderOutputs(forceStatus: true); }

    private void Previous_Click(object sender, RoutedEventArgs e) => SelectRelativeStory(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => SelectRelativeStory(1);

    private void SelectRelativeStory(int direction)
    {
        var rows = (StoryList.ItemsSource as IEnumerable<NrcsStory>)?.ToList() ?? [];
        if (rows.Count == 0) return;
        var index = _story is null ? -1 : rows.FindIndex(x => x.RundownItemId == _story.RundownItemId);
        var next = index;
        do { next += Math.Sign(direction); } while (next >= 0 && next < rows.Count && rows[next].IsSkipped);
        if (next < 0 || next >= rows.Count) return;
        StoryList.SelectedItem = rows[next];
        StoryList.ScrollIntoView(rows[next]);
    }

    private async void StartStop_Click(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            _elapsedBase += DateTime.UtcNow - _runStartedUtc;
            _running = false;
            StartStopButton.Content = "START PROMPT";
        }
        else
        {
            _runStartedUtc = DateTime.UtcNow;
            _running = true;
            StartStopButton.Content = "STOP PROMPT";
            if (AutoMosCheck.IsChecked == true && _story is not null)
            {
                await PlayStoryOnPlayoutAsync();
                await TriggerCgAsync("PROGRAM");
            }
        }
        RenderOutputs(forceStatus: true);
    }

    private async void TakeLive_Click(object sender, RoutedEventArgs e)
    {
        if (_rundown is null || _story is null || _story.IsSkipped) return;
        _store.SetLiveStory(_rundown.Id, _story.Id, _story.RundownItemId, true);
        RunOrderStatusText.Text = $"NRCS LIVE · {_story.Slug}";
        RefreshFromDb();
        if (AutoMosCheck.IsChecked == true)
        {
            await PlayStoryOnPlayoutAsync();
            await TriggerCgAsync("PROGRAM");
        }
    }


    private string SelectedChannelId
    {
        get
        {
            if (ChannelBox.SelectedItem is PlatformChannelStatus status && !string.IsNullOrWhiteSpace(status.ChannelId)) return status.ChannelId;
            return PlatformControlBus.ReadStatuses().OrderByDescending(x => x.UpdatedUtc).FirstOrDefault()?.ChannelId ?? "KTX-PLAYOUT-01";
        }
    }

    private void RefreshProductionContext()
    {
        try
        {
            var statuses = PlatformControlBus.ReadStatuses().Where(x => (DateTime.UtcNow - x.UpdatedUtc).TotalSeconds < 15).ToList();
            var selectedId = (ChannelBox.SelectedItem as PlatformChannelStatus)?.ChannelId;
            ChannelBox.ItemsSource = statuses;
            ChannelBox.SelectedItem = statuses.FirstOrDefault(x => string.Equals(x.ChannelId, selectedId, StringComparison.OrdinalIgnoreCase)) ?? statuses.FirstOrDefault();
        }
        catch { }
        CgTemplateText.Text = string.IsNullOrWhiteSpace(_story?.CgTemplate) ? "—" : _story.CgTemplate;
        CgLayerText.Text = _story is null ? "—" : _story.CgLayer.ToString();
        MediaPathText.Text = string.IsNullOrWhiteSpace(_story?.MediaPath) ? "No attached media" : _story.MediaPath;
        MosDataText.Text = string.IsNullOrWhiteSpace(_story?.CgDataJson) ? "No CG data" : _story.CgDataJson;
    }

    private async Task TriggerCgAsync(string bus)
    {
        if (_story is null || string.IsNullOrWhiteSpace(_story.CgTemplate)) { ProductionStatusText.Text = "No CG template attached to this story."; return; }
        try
        {
            var projects = _database.LoadState("cg-projects", new List<CgProject>());
            var project = projects.FirstOrDefault(x => string.Equals(x.Name, _story.CgTemplate, StringComparison.OrdinalIgnoreCase));
            if (project is null) { ProductionStatusText.Text = "CG template not found in canonical catalog: " + _story.CgTemplate; return; }
            project.ExternalLayer = _story.CgLayer;
            LocalCgCommandBus.Publish(new[] { SelectedChannelId }, new CgRemoteCommand { Action = "PLAY", Bus = bus, Layer = _story.CgLayer, Project = project });
            ProductionStatusText.Text = $"CG {bus} · {_story.CgTemplate} · layer {_story.CgLayer} → {SelectedChannelId}";
            await Task.CompletedTask;
        }
        catch (Exception ex) { ProductionStatusText.Text = "CG trigger failed · " + ex.GetBaseException().Message; }
    }

    private async Task PlayStoryOnPlayoutAsync()
    {
        if (_story is null) return;
        var command = new PlatformControlCommand
        {
            ChannelId = SelectedChannelId, Action = "play_story",
            Parameters = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
            {
                ["storyId"] = _story.Id, ["rundownItemId"] = _story.RundownItemId, ["slug"] = _story.Slug
            }
        };
        var response = await PlatformControlBus.SubmitAndWaitAsync(command, TimeSpan.FromSeconds(3));
        ProductionStatusText.Text = response.Ok ? $"PLAYOUT · {_story.Slug} playing on {SelectedChannelId}" : $"PLAYOUT failed · {response.Code} · {response.Message}";
    }

    private async Task SendPlayoutActionAsync(string action)
    {
        var response = await PlatformControlBus.SubmitAndWaitAsync(new PlatformControlCommand { ChannelId = SelectedChannelId, Action = action }, TimeSpan.FromSeconds(3));
        ProductionStatusText.Text = response.Ok ? $"PLAYOUT {action.ToUpperInvariant()} accepted · {SelectedChannelId}" : $"PLAYOUT {action} failed · {response.Message}";
    }

    private async void TriggerCgPreview_Click(object sender, RoutedEventArgs e) => await TriggerCgAsync("PREVIEW");
    private async void TriggerCgProgram_Click(object sender, RoutedEventArgs e) => await TriggerCgAsync("PROGRAM");
    private async void PlayStory_Click(object sender, RoutedEventArgs e) => await PlayStoryOnPlayoutAsync();
    private async void PlayoutNext_Click(object sender, RoutedEventArgs e) => await SendPlayoutActionAsync("next");
    private async void PlayoutStop_Click(object sender, RoutedEventArgs e) => await SendPlayoutActionAsync("stop");

    private void PreviousCard_Click(object sender, RoutedEventArgs e) { _cardIndex = Math.Max(0, _cardIndex - 1); UpdateCardStatus(); RenderOutputs(); }
    private void NextCard_Click(object sender, RoutedEventArgs e) { _cardIndex = Math.Min(Math.Max(0, CardCount - 1), _cardIndex + 1); UpdateCardStatus(); RenderOutputs(); }
    private void UpdateCardStatus() => CardStatusText.Text = IsCardMode ? $"{_cardIndex + 1}/{Math.Max(1, CardCount)}" : "FLOW";

    private void OpenDisplay_Click(object sender, RoutedEventArgs e)
    {
        var screens = Forms.Screen.AllScreens;
        if (screens.Length == 0) { OutputStatusText.Text = "No Windows display detected."; return; }
        _displayWindow ??= new PrompterDisplayWindow();
        _displayWindow.ShowOnScreen(Math.Clamp(DisplayBox.SelectedIndex, 0, screens.Length - 1));
        _displayWindow.UpdateFrame(BuildState());
        RenderOutputs(forceStatus: true);
    }

    private void CloseDisplay_Click(object sender, RoutedEventArgs e)
    {
        if (_displayWindow is null) return;
        _displayWindow.Close(); _displayWindow = null; RenderOutputs(forceStatus: true);
    }

    private void ToggleNdi_Click(object sender, RoutedEventArgs e)
    {
        if (_ndiEnabled)
        {
            _ndiEnabled = false; _ndi.Close(); NdiToggleButton.Content = "START NDI"; RenderOutputs(forceStatus: true); return;
        }
        var name = string.IsNullOrWhiteSpace(NdiNameBox.Text) ? "KASHTRIX-PROMPTER-01" : NdiNameBox.Text.Trim();
        if (!_ndi.Open(name)) { NdiStatusText.Text = "NDI start failed · " + _ndi.LastError; return; }
        _ndiEnabled = true; _ndiFrames = 0; _ndiPacer.Restart(); NdiToggleButton.Content = "STOP NDI"; RenderOutputs(forceStatus: true);
    }


    private void ToggleDeckLink_Click(object sender, RoutedEventArgs e)
    {
        if (_deckLinkEnabled)
        {
            _deckLinkEnabled = false; _deckLink.Close(); DeckLinkToggleButton.Content = "START SDI"; RenderOutputs(forceStatus: true); return;
        }
        if (!_deckLink.IsSupportedBuild)
        {
            DeckLinkStatusText.Text = _deckLink.LastError;
            OutputStatusText.Text = "DeckLink output is not enabled in this build until Blackmagic SDK interop is generated.";
            return;
        }
        if (DeckLinkBox.SelectedItem is DeckLinkDeviceOption device) _deckLink.DeviceIndex = device.Index;
        _deckLink.KeyerMode = DeckLinkKeyerMode.Off;
        _deckLink.EnableAudio = false;
        _deckLinkEnabled = true;
        _ndiPacer.Restart();
        DeckLinkToggleButton.Content = "STOP SDI";
        RenderOutputs(forceStatus: true);
    }

    private void SavePresenterProfile_Click(object sender, RoutedEventArgs e)
    {
        var presenter = _story?.Presenter?.Trim();
        if (string.IsNullOrWhiteSpace(presenter)) { OutputStatusText.Text = "Set a presenter name in NRCS before saving a presenter profile."; return; }
        var margin = int.TryParse(SafeMarginBox.Text, out var m) ? Math.Clamp(m, 20, 500) : 80;
        PrompterProfileStore.Save(new PresenterPromptProfile(presenter, FontSlider.Value, LineSpacingSlider.Value, SpeedSlider.Value, MirrorCheck.IsChecked == true, FlipCheck.IsChecked == true, InvertCheck.IsChecked == true, RtlCheck.IsChecked == true, margin, Convert.ToString(ResolutionBox.SelectedItem) ?? "1920x1080", SelectedFps, DateTime.UtcNow));
        OutputStatusText.Text = $"Presenter profile saved · {presenter}";
    }

    private void ApplyPresenterProfile_Click(object sender, RoutedEventArgs e) => ApplyPresenterProfileInternal(_story?.Presenter, silent: false);

    private void ApplyPresenterProfileInternal(string? presenter, bool silent)
    {
        var profile = PrompterProfileStore.Find(presenter);
        if (profile is null) { if (!silent) OutputStatusText.Text = "No saved presenter profile for " + (presenter ?? "this story") + "."; return; }
        FontSlider.Value = profile.FontSize;
        LineSpacingSlider.Value = profile.LineSpacing;
        SpeedSlider.Value = profile.Speed;
        MirrorCheck.IsChecked = profile.MirrorHorizontal;
        FlipCheck.IsChecked = profile.FlipVertical;
        InvertCheck.IsChecked = profile.Invert;
        RtlCheck.IsChecked = profile.RightToLeft;
        SafeMarginBox.Text = profile.SafeMargin.ToString();
        ResolutionBox.SelectedItem = profile.Resolution;
        FpsBox.SelectedItem = profile.FramesPerSecond;
        if (!silent) OutputStatusText.Text = $"Presenter profile applied · {profile.Presenter}";
    }


    private void LoadEmergencyScript_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Load emergency / offline prompter script", Filter = "Prompt scripts|*.txt;*.rtf|Text files|*.txt|Rich Text Format|*.rtf|All files|*.*" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var ext = Path.GetExtension(dialog.FileName);
            string body;
            if (string.Equals(ext, ".rtf", StringComparison.OrdinalIgnoreCase))
            {
                var document = new FlowDocument();
                var range = new TextRange(document.ContentStart, document.ContentEnd);
                using var stream = File.OpenRead(dialog.FileName);
                range.Load(stream, DataFormats.Rtf);
                body = range.Text;
            }
            else body = File.ReadAllText(dialog.FileName);
            _offlineOverride = true;
            _offlineBody = body;
            _offlineSlug = Path.GetFileNameWithoutExtension(dialog.FileName).ToUpperInvariant();
            _offlinePresenter = "EMERGENCY";
            FollowLiveCheck.IsChecked = false;
            ResetStoryPosition();
            UpdateStoryHeader();
            RenderOutputs(forceStatus: true);
            OutputStatusText.Text = $"Emergency local script loaded · {dialog.FileName}";
        }
        catch (Exception ex) { OutputStatusText.Text = "Emergency script load failed · " + ex.GetBaseException().Message; }
    }

    private void ReturnToNrcs_Click(object sender, RoutedEventArgs e)
    {
        _offlineOverride = false;
        _offlineBody = "";
        _offlineSlug = "";
        _offlinePresenter = "";
        ResetStoryPosition();
        RefreshFromDb();
        UpdateStoryHeader();
        RenderOutputs(forceStatus: true);
        OutputStatusText.Text = "Returned to live NRCS script.";
    }

    private void ExportSrt_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new SaveFileDialog
            {
                Title = "Export rundown captions / SRT",
                Filter = "SubRip captions|*.srt",
                DefaultExt = ".srt",
                FileName = (_rundown?.Name ?? CurrentSlug).Replace(' ', '-') + ".srt"
            };
            if (dialog.ShowDialog() != true) return;
            var blocks = new List<(TimeSpan Start, TimeSpan End, string Text)>();
            if (_rundown is not null)
            {
                var cursor = TimeSpan.Zero;
                foreach (var row in _store.ListStories(_rundown.Id).Where(x => !x.IsSkipped))
                {
                    var dur = TimeSpan.FromSeconds(Math.Max(1, row.DurationSeconds));
                    blocks.Add((cursor, cursor + dur, row.Body));
                    cursor += dur;
                }
            }
            else
            {
                blocks.Add((TimeSpan.Zero, TimeSpan.FromSeconds(60), CurrentBody));
            }
            var output = new System.Text.StringBuilder();
            var n = 1;
            foreach (var block in blocks.Where(x => !string.IsNullOrWhiteSpace(x.Text)))
            {
                output.AppendLine((n++).ToString());
                output.AppendLine($"{FormatSrtTime(block.Start)} --> {FormatSrtTime(block.End)}");
                output.AppendLine(block.Text.Trim());
                output.AppendLine();
            }
            File.WriteAllText(dialog.FileName, output.ToString(), new System.Text.UTF8Encoding(false));
            OutputStatusText.Text = $"SRT / caption file exported · {dialog.FileName}";
        }
        catch (Exception ex) { OutputStatusText.Text = "SRT export failed · " + ex.GetBaseException().Message; }
    }

    private static string FormatSrtTime(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}";
    }

    private void AddBookmark_Click(object sender, RoutedEventArgs e)
    {
        var list = CurrentBookmarks;
        if (!list.Any(x => Math.Abs(x - _scrollOffset) < 2)) list.Add(_scrollOffset);
        list.Sort();
        UpdateBookmarkStatus();
    }

    private void PreviousBookmark_Click(object sender, RoutedEventArgs e)
    {
        var list = CurrentBookmarks;
        if (list.Count == 0) return;
        var target = list.Where(x => x < _scrollOffset - 1).DefaultIfEmpty(list[0]).Max();
        _scrollOffset = target;
        UpdateBookmarkStatus();
        RenderOutputs();
    }

    private void NextBookmark_Click(object sender, RoutedEventArgs e)
    {
        var list = CurrentBookmarks;
        if (list.Count == 0) return;
        var candidates = list.Where(x => x > _scrollOffset + 1).ToList();
        _scrollOffset = candidates.Count > 0 ? candidates.Min() : list[^1];
        UpdateBookmarkStatus();
        RenderOutputs();
    }

    private void UpdateBookmarkStatus()
    {
        var list = CurrentBookmarks;
        BookmarkStatusText.Text = $"{list.Count} bookmark{(list.Count == 1 ? "" : "s")}";
    }

    private void ToggleCloak_Click(object sender, RoutedEventArgs e)
    {
        if (_rundown is null || _story is null || string.IsNullOrWhiteSpace(_story.RundownItemId)) return;
        var updated = _story with { IsSkipped = !_story.IsSkipped };
        _store.UpdateRundownItem(updated);
        if (updated.IsSkipped && updated.IsLive) _store.SetLiveStory(_rundown.Id, "", "", false);
        _story = updated;
        RefreshFromDb();
        OutputStatusText.Text = updated.IsSkipped ? $"Cloaked from air · {updated.Slug}" : $"Returned to run order · {updated.Slug}";
    }

    private void ToggleRemote_Click(object sender, RoutedEventArgs e)
    {
        if (_remote.IsRunning)
        {
            _remote.Stop(); RemoteToggleButton.Content = "START REMOTE"; RemoteStatusText.Text = "OFF"; return;
        }
        var port = int.TryParse(RemotePortBox.Text, out var p) ? Math.Clamp(p, 1024, 65535) : 7788;
        try
        {
            _remote.Start(port); RemoteToggleButton.Content = "STOP REMOTE"; RemoteStatusText.Text = $"UDP {port}";
            OutputStatusText.Text = $"Remote commands listening on UDP {port}: START, STOP, TOGGLE, NEXT, PREV, CARDNEXT, CARDPREV, SPEED+, SPEED-, BLANK, LIVE, CLOAK, BOOKMARK, BOOKNEXT, BOOKPREV.";
        }
        catch (Exception ex) { RemoteStatusText.Text = "ERROR"; OutputStatusText.Text = "Remote start failed · " + ex.GetBaseException().Message; }
    }

    private void HandleRemoteCommand(string raw, string endpoint)
    {
        var command = raw.Trim().ToUpperInvariant();
        switch (command)
        {
            case "START": if (!_running) StartStop_Click(this, new RoutedEventArgs()); break;
            case "STOP": if (_running) StartStop_Click(this, new RoutedEventArgs()); break;
            case "TOGGLE": StartStop_Click(this, new RoutedEventArgs()); break;
            case "NEXT": Next_Click(this, new RoutedEventArgs()); break;
            case "PREV": case "PREVIOUS": Previous_Click(this, new RoutedEventArgs()); break;
            case "CARDNEXT": NextCard_Click(this, new RoutedEventArgs()); break;
            case "CARDPREV": PreviousCard_Click(this, new RoutedEventArgs()); break;
            case "SPEED+": SpeedSlider.Value = Math.Min(SpeedSlider.Maximum, SpeedSlider.Value + .2); break;
            case "SPEED-": SpeedSlider.Value = Math.Max(SpeedSlider.Minimum, SpeedSlider.Value - .2); break;
            case "BLANK": BlankCheck.IsChecked = !(BlankCheck.IsChecked == true); break;
            case "LIVE": TakeLive_Click(this, new RoutedEventArgs()); break;
            case "CLOAK": ToggleCloak_Click(this, new RoutedEventArgs()); break;
            case "BOOKMARK": AddBookmark_Click(this, new RoutedEventArgs()); break;
            case "BOOKNEXT": NextBookmark_Click(this, new RoutedEventArgs()); break;
            case "BOOKPREV": PreviousBookmark_Click(this, new RoutedEventArgs()); break;
            case "HOME": _scrollOffset = 0; _cardIndex = 0; break;
            default:
                if (command.StartsWith("SPEED ", StringComparison.Ordinal) && double.TryParse(command[6..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var speed)) SpeedSlider.Value = Math.Clamp(speed, SpeedSlider.Minimum, SpeedSlider.Maximum);
                else { RemoteStatusText.Text = $"UNKNOWN · {raw}"; return; }
                break;
        }
        RemoteStatusText.Text = $"{endpoint} · {raw}";
        RenderOutputs(forceStatus: true);
    }

    private void PromptSetting_Changed(object sender, RoutedEventArgs e) { if (!IsLoaded) return; if (IsCardMode) _scrollOffset = 0; UpdateCardStatus(); RenderOutputs(); }
    private void MessageBox_TextChanged(object sender, TextChangedEventArgs e) { if (IsLoaded) RenderOutputs(); }
    private void ClearMessage_Click(object sender, RoutedEventArgs e) { MessageBox.Text = ""; RenderOutputs(); }
    private void ResetTimer_Click(object sender, RoutedEventArgs e) { _elapsedBase = TimeSpan.Zero; _runStartedUtc = DateTime.UtcNow; RenderOutputs(); }

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        SpeedSlider.Value = Math.Clamp(SpeedSlider.Value + (e.Delta > 0 ? 0.15 : -0.15), SpeedSlider.Minimum, SpeedSlider.Maximum);
        e.Handled = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space) { StartStop_Click(sender, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key is Key.Right or Key.PageDown) { if (IsCardMode && _cardIndex < CardCount - 1) NextCard_Click(sender, new RoutedEventArgs()); else Next_Click(sender, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key is Key.Left or Key.PageUp) { if (IsCardMode && _cardIndex > 0) PreviousCard_Click(sender, new RoutedEventArgs()); else Previous_Click(sender, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.Up) { SpeedSlider.Value = Math.Min(SpeedSlider.Maximum, SpeedSlider.Value + .1); e.Handled = true; }
        else if (e.Key == Key.Down) { SpeedSlider.Value = Math.Max(SpeedSlider.Minimum, SpeedSlider.Value - .1); e.Handled = true; }
        else if (e.Key == Key.Home) { _scrollOffset = 0; _cardIndex = 0; RenderOutputs(); e.Handled = true; }
        else if (e.Key == Key.B) { BlankCheck.IsChecked = !(BlankCheck.IsChecked == true); e.Handled = true; }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _engineTimer.Stop(); _refreshTimer.Stop();
        try { _displayWindow?.Close(); } catch { }
        try { _ndi.Dispose(); } catch { }
        try { _deckLink.Dispose(); } catch { }
        try { _remote.Dispose(); } catch { }
    }
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => WindowChromeActions.Drag(this, e);
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowChromeActions.Minimize(this);
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowChromeActions.ToggleMaximize(this);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

}
