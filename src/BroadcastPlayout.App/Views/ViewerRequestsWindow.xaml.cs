using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BroadcastPlayout.Scheduling;
using BroadcastPlayout.ViewModels;

namespace BroadcastPlayout.Views;

public partial class ViewerRequestsWindow : Window, INotifyPropertyChanged
{
    private readonly MainViewModel _vm;
    private ViewerRequest? _editing;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ViewerRequest> Requests => _vm.ViewerRequests;
    public string ModeText => _vm.ScheduleIsDriving ? "NEXT FOLLOWS: CHANNEL SCHEDULE" : "NEXT FOLLOWS: PLAYLIST";
    public string FormTitle => _editing is null ? "NEW REQUEST" : "REQUEST · " + _editing.ViewerText;

    public ViewerRequestsWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        WindowChromeActions.ApplyCleanBorder(this);
        DataContext = this;
        SourceBox.ItemsSource = new[] { "Manual", "Phone call", "SMS", "WhatsApp", "Telegram" };
        KindBox.ItemsSource = new[] { "Song", "Movie", "Programme" };
        AtDatePicker.SelectedDate = DateTime.Today;
        LoadForm(null);
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void LoadForm(ViewerRequest? request)
    {
        _editing = request;
        NameBox.Text = request?.ViewerName ?? string.Empty;
        CityBox.Text = request?.City ?? string.Empty;
        PhoneBox.Text = request?.Phone ?? string.Empty;
        SourceBox.SelectedItem = request?.Source ?? "Manual";
        KindBox.SelectedItem = request?.Kind ?? "Song";
        TitleBox.Text = request?.RequestedTitle ?? string.Empty;
        DedicationBox.Text = request?.Message ?? string.Empty;
        MediaBox.Text = request?.MediaPath ?? string.Empty;
        MatchesList.ItemsSource = null;
        Raise(nameof(FormTitle));
        Raise(nameof(ModeText));
    }

    /// <summary>Writes the form into the request being edited (or a new one). Returns null when the form is incomplete.</summary>
    private ViewerRequest? ReadForm()
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text) || string.IsNullOrWhiteSpace(TitleBox.Text))
        {
            StatusText.Text = "Enter at least the viewer name and what they requested.";
            return null;
        }
        var request = _editing ?? new ViewerRequest();
        request.ViewerName = NameBox.Text.Trim();
        request.City = CityBox.Text.Trim();
        request.Phone = PhoneBox.Text.Trim();
        request.Source = SourceBox.SelectedItem as string ?? "Manual";
        request.Kind = KindBox.SelectedItem as string ?? "Song";
        request.RequestedTitle = TitleBox.Text.Trim();
        request.Message = DedicationBox.Text.Trim();
        request.MediaPath = MediaBox.Text.Trim();
        if (_editing is null) _vm.AddViewerRequest(request);
        else _vm.SaveViewerRequests();
        _editing = request;
        RequestsGrid.SelectedItem = request;
        RequestsGrid.Items.Refresh();
        Raise(nameof(FormTitle));
        return request;
    }

    private void RequestsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RequestsGrid.SelectedItem is ViewerRequest request && !ReferenceEquals(request, _editing)) LoadForm(request);
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        RequestsGrid.SelectedItem = null;
        LoadForm(null);
        NameBox.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (ReadForm() is { } request) StatusText.Text = "Saved: " + request.ViewerText + " · " + request.RequestedTitle;
    }

    private async void Find_Click(object sender, RoutedEventArgs e)
    {
        var query = TitleBox.Text.Trim();
        if (query.Length == 0) { StatusText.Text = "Type the requested title first."; return; }
        StatusText.Text = "Searching ...";
        var found = await _vm.FindMediaAsync(query);
        MatchesList.ItemsSource = found.Select(Path.GetFileName).ToList();
        MatchesList.Tag = found;
        StatusText.Text = found.Count == 0 ? "No media file matches. Use BROWSE." : $"{found.Count} match(es). Pick one.";
    }

    private void MatchesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MatchesList.Tag is IReadOnlyList<string> found && MatchesList.SelectedIndex >= 0 && MatchesList.SelectedIndex < found.Count)
            MediaBox.Text = found[MatchesList.SelectedIndex];
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Media files|*.mp4;*.mov;*.mxf;*.ts;*.m2ts;*.mpeg;*.mpg;*.avi;*.mkv;*.webm;*.m4v;*.wmv|All files|*.*" };
        if (dialog.ShowDialog(this) == true) MediaBox.Text = dialog.FileName;
    }

    private async void PlayNext_Click(object sender, RoutedEventArgs e)
    {
        if (ReadForm() is not { } request) return;
        StatusText.Text = "Scheduling ...";
        StatusText.Text = await _vm.PlayViewerRequestNextAsync(request);
        RequestsGrid.Items.Refresh();
        Raise(nameof(ModeText));
    }

    private async void ScheduleAt_Click(object sender, RoutedEventArgs e)
    {
        if (ReadForm() is not { } request) return;
        if (AtDatePicker.SelectedDate is not { } date ||
            !TimeSpan.TryParseExact(AtTimeBox.Text.Trim(), new[] { @"hh\:mm", @"h\:mm", @"hh\:mm\:ss" }, CultureInfo.InvariantCulture, out var time))
        {
            StatusText.Text = "Pick a date and enter the time as HH:mm.";
            return;
        }
        var at = date.Date + time;
        if (at <= DateTime.Now) { StatusText.Text = "That time has already passed."; return; }
        StatusText.Text = "Scheduling ...";
        StatusText.Text = await _vm.ScheduleViewerRequestAtAsync(request, at);
        RequestsGrid.Items.Refresh();
    }

    private void Reject_Click(object sender, RoutedEventArgs e)
    {
        if (_editing is null) return;
        _vm.SetViewerRequestStatus(_editing, ViewerRequestStatus.Rejected);
        RequestsGrid.Items.Refresh();
        StatusText.Text = "Rejected. (A request already in the playlist or schedule stays there; remove it there if needed.)";
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_editing is null) return;
        _vm.RemoveViewerRequest(_editing);
        LoadForm(null);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => WindowChromeActions.Drag(this, e);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
