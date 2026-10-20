using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BroadcastPlayout.Models;
using BroadcastPlayout.Services;

namespace BroadcastPlayout.Views;

public partial class MultiviewSettingsWindow : Window
{
    private readonly MultiviewSettings _settings;
    private bool _loadingProfile;

    public MultiviewSettings Settings => _settings;

    public MultiviewSettingsWindow()
    {
        InitializeComponent();
        WindowChromeActions.ApplyCleanBorder(this);
        _settings = MultiviewSettings.Load();
        _settings.EnsureProfiles();
        RefreshProfiles(_settings.ActiveProfileId);
        LoadSettingsToUi();
    }

    private void LoadSettingsToUi()
    {
        EnableEmailAlertsCheck.IsChecked = _settings.EnableEmailAlerts;
        EnableDesktopNotificationsCheck.IsChecked = _settings.EnableDesktopNotifications;
        SmtpServerText.Text = _settings.SmtpServer;
        SmtpPortText.Text = _settings.SmtpPort.ToString();
        EnableSslCheck.IsChecked = _settings.EnableSsl;
        SmtpUserText.Text = _settings.SmtpUsername;
        SmtpPassBox.Password = _settings.SmtpPassword;
        SenderEmailText.Text = _settings.SenderEmail;
        RecipientEmailText.Text = _settings.RecipientEmail;

        AlertBlackCheck.IsChecked = _settings.AlertOnBlackFrame;
        AlertFreezeCheck.IsChecked = _settings.AlertOnFreeze;
        AlertSilenceCheck.IsChecked = _settings.AlertOnAudioSilence;
        AlertScteCheck.IsChecked = _settings.AlertOnScte35;
        AlertQualityCheck.IsChecked = _settings.AlertOnQualityDrop;

        QualityThresholdText.Text = _settings.QualityThresholdPercent.ToString("0.#", CultureInfo.InvariantCulture);
        MinBitrateText.Text = _settings.MinBitrateMbps.ToString("0.#", CultureInfo.InvariantCulture);
        BlackSecondsText.Text = _settings.BlackFrameThresholdSeconds.ToString("0.#", CultureInfo.InvariantCulture);
        BlackLumaText.Text = _settings.BlackLumaThreshold.ToString(CultureInfo.InvariantCulture);
        FreezeSecondsText.Text = _settings.FreezeThresholdSeconds.ToString("0.#", CultureInfo.InvariantCulture);
        SignalLossSecondsText.Text = _settings.SignalLossThresholdSeconds.ToString("0.#", CultureInfo.InvariantCulture);
        Scte35DetectionCheck.IsChecked = _settings.EnableScte35Detection;

        AudioSilenceSecondsText.Text = _settings.AudioSilenceThresholdSeconds.ToString("0.#", CultureInfo.InvariantCulture);
        AudioLowDbfsText.Text = _settings.AudioLowThresholdDbfs.ToString("0.#", CultureInfo.InvariantCulture);
        TargetLufsText.Text = _settings.AudioLoudnessTargetLufs.ToString("0.#", CultureInfo.InvariantCulture);
        LoudnessToleranceText.Text = _settings.AudioLoudnessToleranceLu.ToString("0.#", CultureInfo.InvariantCulture);
    }

    private void SaveUiToSettings()
    {
        _settings.EnableEmailAlerts = EnableEmailAlertsCheck.IsChecked == true;
        _settings.EnableDesktopNotifications = EnableDesktopNotificationsCheck.IsChecked == true;
        _settings.SmtpServer = SmtpServerText.Text.Trim();
        if (int.TryParse(SmtpPortText.Text.Trim(), out var port)) _settings.SmtpPort = port;
        _settings.EnableSsl = EnableSslCheck.IsChecked == true;
        _settings.SmtpUsername = SmtpUserText.Text.Trim();
        _settings.SmtpPassword = SmtpPassBox.Password;
        _settings.SenderEmail = SenderEmailText.Text.Trim();
        _settings.RecipientEmail = RecipientEmailText.Text.Trim();

        _settings.AlertOnBlackFrame = AlertBlackCheck.IsChecked == true;
        _settings.AlertOnFreeze = AlertFreezeCheck.IsChecked == true;
        _settings.AlertOnAudioSilence = AlertSilenceCheck.IsChecked == true;
        _settings.AlertOnScte35 = AlertScteCheck.IsChecked == true;
        _settings.AlertOnQualityDrop = AlertQualityCheck.IsChecked == true;

        if (double.TryParse(QualityThresholdText.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var quality))
            _settings.QualityThresholdPercent = Math.Clamp(quality, 1.0, 100.0);

        if (double.TryParse(MinBitrateText.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var bitrate))
            _settings.MinBitrateMbps = Math.Max(0.1, bitrate);

        if (double.TryParse(BlackSecondsText.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var blackSec))
            _settings.BlackFrameThresholdSeconds = Math.Max(0.5, blackSec);

        if (byte.TryParse(BlackLumaText.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var blackLuma))
            _settings.BlackLumaThreshold = blackLuma;

        if (double.TryParse(FreezeSecondsText.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var freezeSec))
            _settings.FreezeThresholdSeconds = Math.Max(0.5, freezeSec);

        if (double.TryParse(SignalLossSecondsText.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var signalLossSec))
            _settings.SignalLossThresholdSeconds = Math.Max(0.5, signalLossSec);

        _settings.EnableScte35Detection = Scte35DetectionCheck.IsChecked == true;

        if (double.TryParse(AudioSilenceSecondsText.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var silenceSec))
            _settings.AudioSilenceThresholdSeconds = Math.Max(0.5, silenceSec);

        if (double.TryParse(AudioLowDbfsText.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var lowDbfs))
            _settings.AudioLowThresholdDbfs = lowDbfs;

        if (double.TryParse(TargetLufsText.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var lufs))
            _settings.AudioLoudnessTargetLufs = lufs;

        if (double.TryParse(LoudnessToleranceText.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var tol))
            _settings.AudioLoudnessToleranceLu = Math.Max(0.1, tol);
    }

    private void RefreshProfiles(Guid selectedId)
    {
        _loadingProfile = true;
        ProfileComboBox.ItemsSource = null;
        ProfileComboBox.ItemsSource = _settings.Profiles;
        ProfileComboBox.SelectedItem = _settings.Profiles.FirstOrDefault(x => x.Id == selectedId) ?? _settings.Profiles.FirstOrDefault();
        if (ProfileComboBox.SelectedItem is MultiviewMonitoringProfile selected)
            ProfileNameText.Text = selected.Name;
        var active = _settings.Profiles.FirstOrDefault(x => x.Id == _settings.ActiveProfileId);
        ActiveProfileText.Text = $"ACTIVE · {active?.Name ?? "None"}";
        _loadingProfile = false;
    }

    private void ProfileComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loadingProfile || ProfileComboBox.SelectedItem is not MultiviewMonitoringProfile profile) return;
        _settings.ApplyProfile(profile);
        ProfileNameText.Text = profile.Name;
        LoadSettingsToUi();
        StatusInfoText.Text = $"Loaded profile '{profile.Name}'. Click ACTIVATE to arm its thresholds.";
    }

    private void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        SaveUiToSettings();
        var profile = _settings.SaveProfile($"Profile {_settings.Profiles.Count + 1}");
        RefreshProfiles(profile.Id);
        ProfileNameText.Focus();
        ProfileNameText.SelectAll();
        StatusInfoText.Text = "New monitoring profile created. Rename, adjust thresholds, then activate it.";
    }

    private void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        SaveUiToSettings();
        var selected = ProfileComboBox.SelectedItem as MultiviewMonitoringProfile;
        var profile = _settings.SaveProfile(ProfileNameText.Text, selected?.Id);
        if (_settings.ActiveProfileId == profile.Id) _settings.ApplyProfile(profile);
        _settings.Save();
        RefreshProfiles(profile.Id);
        StatusInfoText.Text = $"Saved profile '{profile.Name}'.";
    }

    private void ActivateProfile_Click(object sender, RoutedEventArgs e)
    {
        SaveUiToSettings();
        var selected = ProfileComboBox.SelectedItem as MultiviewMonitoringProfile;
        var profile = _settings.SaveProfile(ProfileNameText.Text, selected?.Id);
        _settings.ActivateProfile(profile);
        _settings.Save();
        RefreshProfiles(profile.Id);
        StatusInfoText.Text = $"Profile '{profile.Name}' is active; notifications and email now use these thresholds.";
    }

    private void TabSmtp_Click(object sender, RoutedEventArgs e)
    {
        SmtpPanel.Visibility = Visibility.Visible;
        QcPanel.Visibility = Visibility.Collapsed;

        TabSmtpBtn.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3E2359"));
        TabSmtpBtn.BorderThickness = new Thickness(1);
        TabSmtpBtn.Foreground = Brushes.White;

        TabQcBtn.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#251B31"));
        TabQcBtn.BorderThickness = new Thickness(0);
        TabQcBtn.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#B78BE7"));
    }

    private void TabQc_Click(object sender, RoutedEventArgs e)
    {
        SmtpPanel.Visibility = Visibility.Collapsed;
        QcPanel.Visibility = Visibility.Visible;

        TabQcBtn.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3E2359"));
        TabQcBtn.BorderThickness = new Thickness(1);
        TabQcBtn.Foreground = Brushes.White;

        TabSmtpBtn.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#251B31"));
        TabSmtpBtn.BorderThickness = new Thickness(0);
        TabSmtpBtn.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#B78BE7"));
    }

    private async void TestEmail_Click(object sender, RoutedEventArgs e)
    {
        SaveUiToSettings();
        StatusInfoText.Text = "Sending test alert email...";
        StatusInfoText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#B78BE7"));

        var (success, message) = await _settings.SendTestAlertEmailAsync();
        StatusInfoText.Text = message;
        StatusInfoText.Foreground = success
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#34D399"))
            : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F87171"));
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        SaveUiToSettings();
        if (ProfileComboBox.SelectedItem is MultiviewMonitoringProfile selected)
        {
            var saved = _settings.SaveProfile(ProfileNameText.Text, selected.Id);
            if (_settings.ActiveProfileId == saved.Id) _settings.ApplyProfile(saved);
        }
        _settings.Save();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }
}
