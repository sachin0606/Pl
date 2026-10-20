using System.Windows;
using BroadcastPlayout.Services;
using Forms = System.Windows.Forms;

namespace BroadcastPlayout.Views;

public partial class FirstRunSetupWindow : Window
{
    public FirstRunSetupWindow()
    {
        InitializeComponent();
        var settings = SettingsStore.Load();
        ChannelNameBox.Text = settings.ChannelName;
        ChannelIdBox.Text = string.IsNullOrWhiteSpace(settings.ChannelId) ? $"KTX-PL-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}" : settings.ChannelId;
        FillerFolderBox.Text = settings.FillerFolder;
        FillerCheck.IsChecked = settings.EnableFiller;
        ControllerCheck.IsChecked = settings.ConnectToController;
        ControllerHostBox.Text = settings.ControllerHost;
        ControllerPortBox.Text = settings.ControllerPort.ToString();
    }

    private void BrowseFiller_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = "Select Kashtrix filler media folder", UseDescriptionForTitle = true };
        if (dialog.ShowDialog() == Forms.DialogResult.OK) FillerFolderBox.Text = dialog.SelectedPath;
    }

    private void Complete_Click(object sender, RoutedEventArgs e)
    {
        var settings = SettingsStore.Load();
        settings.ChannelName = string.IsNullOrWhiteSpace(ChannelNameBox.Text) ? "KASHTRIX NEWS HD" : ChannelNameBox.Text.Trim();
        settings.ChannelId = string.IsNullOrWhiteSpace(ChannelIdBox.Text) ? $"KTX-PL-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}" : ChannelIdBox.Text.Trim().ToUpperInvariant();
        settings.EnableFiller = FillerCheck.IsChecked == true;
        settings.FillerFolder = FillerFolderBox.Text.Trim();
        settings.ConnectToController = ControllerCheck.IsChecked == true;
        settings.ControllerHost = string.IsNullOrWhiteSpace(ControllerHostBox.Text) ? "127.0.0.1" : ControllerHostBox.Text.Trim();
        settings.ControllerPort = int.TryParse(ControllerPortBox.Text, out var port) ? Math.Clamp(port, 1024, 65535) : 9120;
        settings.PlayoutPreset = Preset4K.IsChecked == true ? "2160p50" : Preset1080i.IsChecked == true ? "1080i50" : Preset720.IsChecked == true ? "720p50" : "1080p50";
        ApplyPreset(settings);
        settings.FirstRunCompleted = true;
        SettingsStore.Save(settings);
        DialogResult = true;
    }

    private static void ApplyPreset(BroadcastPlayout.Models.AppSettings s)
    {
        ChannelFormatPreset.ApplyToProfile(s.NdiProfile, s.PlayoutPreset);
        ChannelFormatPreset.ApplyToProfile(s.DeckLinkProfile, s.PlayoutPreset);
        ChannelFormatPreset.ApplyToProfile(s.DisplayProfile, s.PlayoutPreset);
    }
}
