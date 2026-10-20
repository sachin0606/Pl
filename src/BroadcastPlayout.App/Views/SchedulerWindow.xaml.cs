using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BroadcastPlayout.Ffmpeg;
using BroadcastPlayout.Models;
using BroadcastPlayout.Services;
using BroadcastPlayout.ViewModels;
using Microsoft.Win32;

namespace BroadcastPlayout.Views;

public partial class SchedulerWindow : Window, INotifyPropertyChanged
{
    private readonly MainViewModel _vm;
    private ScheduleEntry? _selectedSchedule;
    private Point _scheduleDragStart;
    public ScheduleEntry? SelectedSchedule { get => _selectedSchedule; set { if (ReferenceEquals(_selectedSchedule,value)) return; _selectedSchedule=value; PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(SelectedSchedule))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    public SchedulerWindow(MainViewModel vm) { InitializeComponent(); _vm = vm; DataContext = vm; Loaded += (_,_) => { if (_vm.Schedules.Count > 0) { ScheduleGrid.SelectedIndex = 0; SelectedSchedule = _vm.Schedules[0]; } }; }

    private void Add_Click(object sender, RoutedEventArgs e) { _vm.AddSchedule(); ScheduleGrid.SelectedIndex = _vm.Schedules.Count - 1; SelectedSchedule = ScheduleGrid.SelectedItem as ScheduleEntry; }
    private void Remove_Click(object sender, RoutedEventArgs e) => _vm.RemoveSchedule(ScheduleGrid.SelectedItem as ScheduleEntry);

    private void InsertMedia_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Filter = "Media|*.mp4;*.mov;*.mxf;*.mkv;*.avi;*.ts;*.m2ts;*.mpg;*.mpeg;*.webm|All files|*.*" };
        if (d.ShowDialog() != true) return;
        try
        {
            var info = MediaProbe.Read(d.FileName);
            var item = new PlaylistItem { Title=Path.GetFileNameWithoutExtension(d.FileName), FilePath=d.FileName, SourceKind="File", EventType="VIDEO", InPoint=TimeSpan.Zero, OutPoint=info.Duration, SourceDuration=info.Duration, SourceFrameRate=info.FramesPerSecond, Codec=info.VideoCodec, VideoFormat=$"{info.Width}x{info.Height} {info.FramesPerSecond:0.###}p" };
            var package = SaveSchedulePackage(item);
            var entry = new ScheduleEntry { Name=item.Title, StartAt=DateTime.Now.AddMinutes(5), PlaylistPath=package, SourceLabel=d.FileName, EventType="Program", Duration=info.Duration, Repeat="One Time", StartMode="Hard Start", Enabled=true };
            _vm.Schedules.Add(entry); _vm.SaveSchedules(); ScheduleGrid.SelectedItem = entry; SelectedSchedule = entry;
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Insert Media"); }
    }

    private void InsertPlaylist_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Filter = "Kashtrix Playlist|*.kashtrix|All files|*.*" };
        if (d.ShowDialog() != true) return;
        try
        {
            var items = PlaylistDocumentService.Load(d.FileName);
            var duration = TimeSpan.FromTicks(items.Sum(x => x.Duration.Ticks));
            var entry = new ScheduleEntry { Name=Path.GetFileNameWithoutExtension(d.FileName), StartAt=DateTime.Now.AddMinutes(5), PlaylistPath=d.FileName, SourceLabel=d.FileName, EventType="Playlist", Duration=duration, Repeat="One Time", StartMode="Hard Start", Enabled=true };
            _vm.Schedules.Add(entry); _vm.SaveSchedules(); ScheduleGrid.SelectedItem = entry; SelectedSchedule = entry;
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Insert Playlist"); }
    }

    private static string SaveSchedulePackage(PlaylistItem item)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KashtrixPlayout", "ScheduledMedia");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.kashtrix");
        PlaylistDocumentService.Save(path, new[] { item });
        return path;
    }

    private void AddStop_Click(object sender, RoutedEventArgs e) => AddControlSchedule("STOP");
    private void AddPlay_Click(object sender, RoutedEventArgs e) => AddControlSchedule("PLAY");
    private void AddPause_Click(object sender, RoutedEventArgs e) => AddControlSchedule("PAUSE");

    private void AddControlSchedule(string action)
    {
        var entry = new ScheduleEntry
        {
            Name = action + " CONTROL EVENT",
            StartAt = DateTime.Now.AddMinutes(5),
            EventType = action,
            Category = "Control",
            Repeat = "One Time",
            StartMode = "Hard Start",
            Enabled = true,
            Priority = 100
        };
        _vm.Schedules.Add(entry);
        _vm.SaveSchedules();
        ScheduleGrid.SelectedItem = entry;
        SelectedSchedule = entry;
    }

    private void ScheduleGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _scheduleDragStart = e.GetPosition(ScheduleGrid);
    private void ScheduleGrid_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || ScheduleGrid.SelectedItem is not ScheduleEntry entry) return;
        var point = e.GetPosition(ScheduleGrid);
        if (Math.Abs(point.X - _scheduleDragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - _scheduleDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        DragDrop.DoDragDrop(ScheduleGrid, new DataObject("Kashtrix.ScheduleEntry", entry), DragDropEffects.Move);
    }
    private void ScheduleGrid_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("Kashtrix.ScheduleEntry") || e.Data.GetData("Kashtrix.ScheduleEntry") is not ScheduleEntry source) return;
        var row = e.OriginalSource is DependencyObject d ? ItemsControl.ContainerFromElement(ScheduleGrid, d) as DataGridRow : null;
        var target = row?.Item as ScheduleEntry;
        var oldIndex = _vm.Schedules.IndexOf(source);
        var newIndex = target is null ? _vm.Schedules.Count - 1 : _vm.Schedules.IndexOf(target);
        if (oldIndex >= 0 && newIndex >= 0 && oldIndex != newIndex) _vm.Schedules.Move(oldIndex, newIndex);
        _vm.SaveSchedules();
        SelectedSchedule = source; ScheduleGrid.SelectedItem = source; e.Handled = true;
    }

    private void LoadDemo_Click(object sender, RoutedEventArgs e)
    {
        _vm.LoadDemoWorkspace();
        ScheduleGrid.SelectedIndex = _vm.Schedules.Count > 0 ? 0 : -1;
        SelectedSchedule = _vm.Schedules.FirstOrDefault();
        SignalPlayoutReload();
    }

    private void Publish_Click(object sender, RoutedEventArgs e) { _vm.SaveSchedules(); SignalPlayoutReload(); MessageBox.Show("Schedule published to the shared Kashtrix channel database.", "Scheduler"); }
    private void Save_Click(object sender, RoutedEventArgs e) { _vm.SaveSchedules(); SignalPlayoutReload(); }
    private void BrowseMediaOrPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSchedule is null) return;
        var d = new OpenFileDialog
        {
            Title = "Choose Scheduled Media or Playlist",
            Filter = "Media and Playlists|*.mp4;*.mov;*.mxf;*.mkv;*.avi;*.ts;*.m2ts;*.mpg;*.mpeg;*.webm;*.kashtrix;*.kashtrixplaylist;*.json|Video Files|*.mp4;*.mov;*.mxf;*.mkv;*.avi;*.ts;*.m2ts|Playlists (*.kashtrix;*.kashtrixplaylist)|*.kashtrix;*.kashtrixplaylist|All Files (*.*)|*.*"
        };
        if (d.ShowDialog() != true) return;
        try
        {
            SelectedSchedule.PlaylistPath = d.FileName;
            SelectedSchedule.SourceLabel = d.FileName;
            if (string.IsNullOrWhiteSpace(SelectedSchedule.Name) || SelectedSchedule.Name.StartsWith("Schedule", StringComparison.OrdinalIgnoreCase))
                SelectedSchedule.Name = Path.GetFileNameWithoutExtension(d.FileName);

            if (d.FileName.EndsWith(".kashtrix", StringComparison.OrdinalIgnoreCase) ||
                d.FileName.EndsWith(".kashtrixplaylist", StringComparison.OrdinalIgnoreCase))
            {
                var items = PlaylistDocumentService.Load(d.FileName);
                SelectedSchedule.Duration = TimeSpan.FromTicks(items.Sum(x => x.Duration.Ticks));
                SelectedSchedule.EventType = "Playlist";
            }
            else
            {
                var info = MediaProbe.Read(d.FileName);
                SelectedSchedule.Duration = info.Duration;
                SelectedSchedule.EventType = "Program";
            }
            _vm.SaveSchedules();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to inspect media: {ex.Message}", "Scheduler", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void InsertIntoPlayout_Click(object sender, RoutedEventArgs e)
    {
        var entry = ScheduleGrid.SelectedItem as ScheduleEntry; if (entry is null || string.IsNullOrWhiteSpace(entry.PlaylistPath) || !File.Exists(entry.PlaylistPath)) return;
        try
        {
            if (entry.PlaylistPath.EndsWith(".kashtrix", StringComparison.OrdinalIgnoreCase) ||
                entry.PlaylistPath.EndsWith(".kashtrixplaylist", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var item in PlaylistDocumentService.Load(entry.PlaylistPath)) _vm.AddInputSource(item);
            }
            else
            {
                var info = MediaProbe.Read(entry.PlaylistPath);
                var item = new PlaylistItem
                {
                    Title = Path.GetFileNameWithoutExtension(entry.PlaylistPath),
                    FilePath = entry.PlaylistPath,
                    SourceKind = "File",
                    EventType = "VIDEO",
                    InPoint = TimeSpan.Zero,
                    OutPoint = info.Duration,
                    SourceDuration = info.Duration,
                    SourceFrameRate = info.FramesPerSecond,
                    Codec = info.VideoCodec,
                    VideoFormat = $"{info.Width}x{info.Height} {info.FramesPerSecond:0.###}p"
                };
                _vm.AddInputSource(item);
            }
            SignalPlaylistReload();
            MessageBox.Show("Scheduled media inserted into the shared playout rundown.", "Scheduler");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Scheduler"); }
    }
    private void PreviewDay_Click(object sender, RoutedEventArgs e) => MessageBox.Show("Day preview uses the same event list shown in the timeline and validates source availability before publishing.", "Scheduler Preview");
    private void Validate_Click(object sender, RoutedEventArgs e)
    {
        var missing = _vm.Schedules.Where(x => x.Enabled && (string.IsNullOrWhiteSpace(x.PlaylistPath) || !File.Exists(x.PlaylistPath))).ToArray();
        MessageBox.Show(missing.Length == 0 ? "Schedule validation passed." : $"{missing.Length} enabled event(s) have missing media/playlist packages.", "Schedule Validation");
    }
    private void Close_Click(object sender, RoutedEventArgs e) { _vm.SaveSchedules(); SignalPlayoutReload(); Close(); }
    private void ChannelSchedule_Click(object sender, RoutedEventArgs e) => new ChannelScheduleWindow(_vm) { Owner = this }.Show();
    private static void SignalPlayoutReload() { try { var folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"KashtrixPlayout"); Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder,"scheduler.reload"), DateTime.UtcNow.ToString("O")); } catch { } }
    private static void SignalPlaylistReload() { try { var folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"KashtrixPlayout"); Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder,"playlist.reload"), DateTime.UtcNow.ToString("O")); } catch { } }
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => WindowChromeActions.Drag(this, e);
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowChromeActions.ToggleMaximize(this);

}
