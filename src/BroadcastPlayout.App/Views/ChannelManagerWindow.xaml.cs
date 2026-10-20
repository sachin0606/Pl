using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using BroadcastPlayout.Models;
using BroadcastPlayout.Services;

namespace BroadcastPlayout.Views;

public partial class ChannelManagerWindow : Window, INotifyPropertyChanged
{
    private ChannelDefinition? _selectedChannel;
    public ObservableCollection<ChannelDefinition> Channels { get; } = [];
    public ChannelDefinition? SelectedChannel { get => _selectedChannel; set { _selectedChannel = value; Raise(); } }

    public ChannelManagerWindow()
    {
        InitializeComponent();
        DataContext = this;
        foreach (var channel in ChannelRegistryStore.Load()) Channels.Add(channel);
        SelectedChannel = Channels.FirstOrDefault();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var channel = new ChannelDefinition { ChannelId = NextId(), ChannelName = $"Channel {Channels.Count + 1}" };
        Channels.Add(channel); SelectedChannel = channel;
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedChannel is null) return;
        var copy = new ChannelDefinition { ChannelId = NextId(), ChannelName = SelectedChannel.ChannelName + " Copy", Format = SelectedChannel.Format, Host = SelectedChannel.Host, Notes = SelectedChannel.Notes, Enabled = SelectedChannel.Enabled };
        Channels.Add(copy); SelectedChannel = copy;
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedChannel is null) return;
        var index = Channels.IndexOf(SelectedChannel);
        Channels.Remove(SelectedChannel);
        SelectedChannel = Channels.Count == 0 ? null : Channels[Math.Clamp(index, 0, Channels.Count - 1)];
    }

    private void GenerateId_Click(object sender, RoutedEventArgs e) { if (SelectedChannel is not null) SelectedChannel.ChannelId = NextId(); }

    private void LoadDemo_Click(object sender, RoutedEventArgs e)
    {
        Channels.Clear();
        foreach (var channel in DemoDataFactory.CreateChannels()) Channels.Add(channel);
        SelectedChannel = Channels.FirstOrDefault();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var invalid = Channels.FirstOrDefault(x => string.IsNullOrWhiteSpace(x.ChannelId) || string.IsNullOrWhiteSpace(x.ChannelName));
        if (invalid is not null) { MessageBox.Show("Every channel needs a Channel ID and name.", "Channel Manager"); return; }
        var duplicate = Channels.GroupBy(x => x.ChannelId, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) { MessageBox.Show($"Channel ID '{duplicate.Key}' is duplicated. IDs must be unique.", "Channel Manager"); return; }
        ChannelRegistryStore.Save(Channels);
        DialogResult = true;
    }

    private string NextId()
    {
        var used = Channels.Select(x => x.ChannelId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i <= 9999; i++) { var id = $"KTX-CH-{i:000}"; if (!used.Contains(id)) return id; }
        return $"KTX-CH-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";
    }
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => WindowChromeActions.Drag(this, e);
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowChromeActions.Minimize(this);
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowChromeActions.ToggleMaximize(this);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
