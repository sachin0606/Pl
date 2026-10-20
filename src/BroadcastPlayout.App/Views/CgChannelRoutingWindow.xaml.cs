using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using BroadcastPlayout.Models;
using BroadcastPlayout.Services;

namespace BroadcastPlayout.Views;

public partial class CgChannelRoutingWindow : Window, INotifyPropertyChanged
{
    private readonly CgProject _project;
    private readonly HashSet<string> _targetIds = new(StringComparer.OrdinalIgnoreCase);
    private ChannelControllerOperator? _operator;
    public ObservableCollection<CgTargetView> Channels { get; } = [];
    public int SelectedCount => _targetIds.Count;

    public CgChannelRoutingWindow(CgProject project)
    {
        InitializeComponent();
        DataContext = this;
        _project = project;
        var settings = SettingsStore.Load();
        HostBox.Text = settings.ControllerHost;
        PortBox.Text = settings.ControllerPort.ToString();
        GraphicNameText.Text = project.Name;
        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) => _operator?.Dispose();
    }

    private async Task RefreshAsync()
    {
        try
        {
            _operator?.Dispose();
            _operator = new ChannelControllerOperator();
            var port = int.TryParse(PortBox.Text, out var value) ? value : 9120;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await _operator.ConnectAsync(HostBox.Text.Trim(), port, cts.Token);
            var list = await _operator.GetChannelsAsync(cts.Token);
            SetChannels(list);
            StatusText.Text = $"Connected · {Channels.Count} playout client(s) · select target(s) below.";
        }
        catch (Exception ex)
        {
            _operator?.Dispose(); _operator = null;
            StatusText.Text = "Controller unavailable · " + Friendly(ex);
        }
    }

    private void SetChannels(IEnumerable<ChannelStatusSnapshot> statuses)
    {
        Channels.Clear();
        foreach (var status in statuses.OrderBy(x => x.ChannelId, StringComparer.OrdinalIgnoreCase))
        {
            var view = new CgTargetView(status, OnTargetChanged) { IsSelected = _targetIds.Contains(status.ChannelId) };
            Channels.Add(view);
        }
        Raise(nameof(SelectedCount));
    }

    private void OnTargetChanged(CgTargetView view, bool selected)
    {
        if (selected) _targetIds.Add(view.Status.ChannelId); else _targetIds.Remove(view.Status.ChannelId);
        Raise(nameof(SelectedCount));
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void SelectAll_Click(object sender, RoutedEventArgs e) { foreach (var channel in Channels) channel.IsSelected = true; }
    private void Clear_Click(object sender, RoutedEventArgs e) { foreach (var channel in Channels) channel.IsSelected = false; }

    private void Demo_Click(object sender, RoutedEventArgs e)
    {
        _operator?.Dispose(); _operator = null;
        var statuses = DemoDataFactory.CreateChannels().Select((channel, index) => new ChannelStatusSnapshot
        {
            ChannelId = channel.ChannelId, ChannelName = channel.ChannelName, Format = channel.Format,
            State = "DEMO", ProgramTitle = index % 2 == 0 ? "Morning News" : "Business Update", NextTitle = "Coming Up"
        }).ToArray();
        SetChannels(statuses);
        StatusText.Text = "Demo targets loaded for layout preview only · connect Channel Controller before sending commands.";
    }

    private async Task SendAsync(string action, string bus = "PROGRAM")
    {
        var targets = Channels.Where(x => x.IsSelected).Select(x => x.Status.ChannelId).ToArray();
        if (targets.Length == 0) { MessageBox.Show("Select one or more channel targets.", "CG Routing"); return; }
        if (_operator is null)
        {
            StatusText.Text = $"OFFLINE · {action} NOT SENT · connect Channel Controller first.";
            return;
        }
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _operator.SendCgAsync(targets, new CgRemoteCommand
            {
                Action = action,
                Bus = bus,
                Project = action == "STOP" ? null : _project
            }, cts.Token);
            StatusText.Text = $"{bus} {action} sent to {targets.Length} selected channel(s).";
        }
        catch (Exception ex) { StatusText.Text = action + " failed · " + Friendly(ex); }
    }

    private async void Preview_Click(object sender, RoutedEventArgs e) => await SendAsync("PLAY", "PREVIEW");
    private async void Play_Click(object sender, RoutedEventArgs e) => await SendAsync("PLAY", "PROGRAM");
    private async void Stop_Click(object sender, RoutedEventArgs e) => await SendAsync("STOP", "PROGRAM");
    private static string Friendly(Exception ex) => ex is OperationCanceledException ? "Controller operation timed out." : ex.Message;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowChromeActions.Minimize(this);
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowChromeActions.ToggleMaximize(this);
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => WindowChromeActions.Drag(this, e);
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class CgTargetView : INotifyPropertyChanged
{
    private readonly Action<CgTargetView, bool>? _changed;
    private bool _selected;
    public CgTargetView(ChannelStatusSnapshot status, Action<CgTargetView, bool>? changed = null) { Status = status; _changed = changed; }
    public ChannelStatusSnapshot Status { get; }
    public bool IsSelected { get => _selected; set { if (_selected == value) return; _selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); _changed?.Invoke(this, value); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}
