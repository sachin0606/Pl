using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BroadcastPlayout.Models;
using BroadcastPlayout.Services;
using BroadcastPlayout.ViewModels;

namespace BroadcastPlayout.Views;

public partial class NowNextSettingsWindow : Window
{
    private static readonly RequestTag SampleRequest = new(Guid.Empty, "Ramesh", "Delhi", "Happy birthday Maa", "WhatsApp");
    private readonly MainViewModel _vm;

    public ObservableCollection<NowNextProfile> Profiles { get; }
    public NowNextBoxMode[] BoxModes { get; } = Enum.GetValues<NowNextBoxMode>();
    public NowNextTimeDisplay[] TimeDisplays { get; } = Enum.GetValues<NowNextTimeDisplay>();

    public NowNextSettingsWindow(MainViewModel vm)
    {
        _vm = vm;
        var s = vm.Settings.NowNext;
        // Edit copies; nothing changes on air until SAVE.
        Profiles = new ObservableCollection<NowNextProfile>(s.Profiles.Select(p => p.Clone()));
        InitializeComponent();
        WindowChromeActions.ApplyCleanBorder(this);
        DataContext = this;

        EnabledCheck.IsChecked = s.Enabled;
        VideoBoxCheck.IsChecked = vm.Settings.ProfessionalBroadcast.NextItemVideo;
        PreviewStartBox.Text = Format(s.PreviewStartPercent);
        ShowRequestsCheck.IsChecked = s.ShowRequests;
        RequestPrefixBox.Text = s.RequestPrefix;
        RequestNameCheck.IsChecked = s.RequestShowName;
        RequestCityCheck.IsChecked = s.RequestShowCity;
        RequestMessageCheck.IsChecked = s.RequestShowMessage;
        var names = vm.CgProjects.Select(x => x.Name).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        InfoTemplateBox.ItemsSource = names;
        BoxTemplateBox.ItemsSource = names;
        InfoTemplateBox.Text = s.InfoTemplate;
        BoxTemplateBox.Text = s.BoxTemplate;
        InfoLayerBox.Text = s.InfoLayer.ToString(CultureInfo.InvariantCulture);
        BoxLayerBox.Text = s.BoxLayer.ToString(CultureInfo.InvariantCulture);
        UpdateRequestPreview();
    }

    private static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private RequestFormat CurrentRequestFormat() => new(ShowRequestsCheck.IsChecked == true, RequestPrefixBox.Text ?? string.Empty,
        RequestNameCheck.IsChecked == true, RequestCityCheck.IsChecked == true, RequestMessageCheck.IsChecked == true);

    private void UpdateRequestPreview()
    {
        var text = NowNextRuntime.RequestText(SampleRequest, CurrentRequestFormat());
        RequestPreviewText.Text = text.Length == 0 ? "(not shown)" : "Preview: " + text;
    }

    private void RequestOption_Changed(object sender, RoutedEventArgs e) => UpdateRequestPreview();
    private void RequestPrefix_Changed(object sender, TextChangedEventArgs e) { if (IsLoaded) UpdateRequestPreview(); }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => WindowChromeActions.Drag(this, e);
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        var profile = new NowNextProfile { Name = "New type", Categories = "Category name", ShowInfo = true, BoxMode = NowNextBoxMode.StartAndEnd };
        Profiles.Add(profile);
        ProfilesGrid.SelectedItem = profile;
        ProfilesGrid.ScrollIntoView(profile);
    }

    private void RemoveProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesGrid.SelectedItem is NowNextProfile profile) Profiles.Remove(profile);
    }

    private void DefaultProfiles_Click(object sender, RoutedEventArgs e)
    {
        Profiles.Clear();
        foreach (var profile in NowNextSettings.DefaultProfiles()) Profiles.Add(profile);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ProfilesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (!TryRead(PreviewStartBox, 0, 90, out var previewStart) || !TryRead(InfoLayerBox, 0, 999, out var infoLayer) || !TryRead(BoxLayerBox, 0, 999, out var boxLayer))
            return;
        if ((int)infoLayer == (int)boxLayer)
        {
            StatusText.Text = "The two graphics need different CG layers.";
            return;
        }

        foreach (var p in Profiles)
        {
            p.Name = string.IsNullOrWhiteSpace(p.Name) ? "Type" : p.Name.Trim();
            p.Categories = p.Categories?.Trim() ?? string.Empty;
            p.StartAtSeconds = Math.Clamp(p.StartAtSeconds, 0, 86400);
            p.HoldSeconds = Math.Clamp(p.HoldSeconds, 2, 600);
            p.LaterHoldSeconds = Math.Clamp(p.LaterHoldSeconds, 0, 600);
            p.EndLeadSeconds = Math.Clamp(p.EndLeadSeconds, 0, 86400);
            p.RepeatEveryMinutes = Math.Clamp(p.RepeatEveryMinutes, 1, 1440);
            p.RepeatTimes = Math.Clamp(p.RepeatTimes, 0, 1000);
            p.CountdownMinutes = Math.Clamp(p.CountdownMinutes, 0, 1440);
        }

        var s = _vm.Settings.NowNext;
        s.Enabled = EnabledCheck.IsChecked == true;
        s.PreviewStartPercent = previewStart;
        s.Profiles = [.. Profiles];
        var request = CurrentRequestFormat();
        s.ShowRequests = request.Show;
        s.RequestPrefix = request.Prefix.Trim();
        s.RequestShowName = request.Name;
        s.RequestShowCity = request.City;
        s.RequestShowMessage = request.Message;
        s.InfoTemplate = string.IsNullOrWhiteSpace(InfoTemplateBox.Text) ? NowNextSettings.DefaultInfoTemplate : InfoTemplateBox.Text.Trim();
        s.BoxTemplate = string.IsNullOrWhiteSpace(BoxTemplateBox.Text) ? NowNextSettings.DefaultBoxTemplate : BoxTemplateBox.Text.Trim();
        s.InfoLayer = (int)infoLayer;
        s.BoxLayer = (int)boxLayer;
        _vm.Settings.ProfessionalBroadcast.NextItemVideo = VideoBoxCheck.IsChecked == true;
        _vm.ApplyNowNextSettings();
        Close();
    }

    private bool TryRead(TextBox box, double min, double max, out double value)
    {
        if (double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value >= min && value <= max)
            return true;
        StatusText.Text = $"Enter a number from {min} to {max}.";
        box.Focus();
        box.SelectAll();
        return false;
    }
}
