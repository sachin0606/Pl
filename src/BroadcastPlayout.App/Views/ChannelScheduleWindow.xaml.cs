using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using BroadcastPlayout.ViewModels;
using Microsoft.Win32;

namespace BroadcastPlayout.Views;

/// <summary>Channel Schedule editor window. All logic lives in <see cref="ChannelScheduleEditorViewModel"/>.</summary>
public partial class ChannelScheduleWindow : Window
{
    private const string MediaFilter = "Media|*.mp4;*.mov;*.mxf;*.mkv;*.avi;*.ts;*.m2ts;*.mpg;*.mpeg;*.webm;*.m4v;*.wmv|All files|*.*";
    private readonly MainViewModel? _host;
    private readonly ChannelScheduleEditorViewModel _editor;

    public ChannelScheduleWindow(MainViewModel host)
        : this(new ChannelScheduleEditorViewModel(host.ChannelId, host.CgProjects.Select(p => p.Name), host), host) { }

    /// <summary>Without a playout host the on-air controls are hidden; editing and saving still work.</summary>
    public ChannelScheduleWindow(ChannelScheduleEditorViewModel editor, MainViewModel? host)
    {
        InitializeComponent();
        _host = host;
        _editor = editor;
        DataContext = _editor;
        Closing += OnClosing;
    }

    public ChannelScheduleEditorViewModel Editor => _editor;

    private void Add_Click(object sender, RoutedEventArgs e) { _editor.AddItem(); ScheduleGrid.ScrollIntoView(_editor.Selected!); }
    private void Delete_Click(object sender, RoutedEventArgs e) => _editor.DeleteSelected();
    private void Duplicate_Click(object sender, RoutedEventArgs e) => _editor.DuplicateSelected();
    private void Save_Click(object sender, RoutedEventArgs e) => _editor.Save();

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (_editor.IsDirty && MessageBox.Show(this, "Discard unsaved changes and reload?", "Channel Schedule", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _editor.Load();
    }

    private void Validate_Click(object sender, RoutedEventArgs e)
    {
        var issues = _editor.Validate();
        MessageBox.Show(this,
            issues.Count == 0 ? "Schedule validation passed." : string.Join(Environment.NewLine, issues.Take(25)) + (issues.Count > 25 ? $"{Environment.NewLine}… {issues.Count - 25} more" : string.Empty),
            "Channel Schedule · Validate", MessageBoxButton.OK, issues.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = MediaFilter, Multiselect = true, Title = "Add media files" };
        if (dialog.ShowDialog(this) == true) _editor.AddSources(dialog.FileNames);
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Add a media folder (all its media files play)", Multiselect = true };
        if (dialog.ShowDialog(this) == true) _editor.AddSources(dialog.FolderNames);
    }

    private void AddPlaylist_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Kashtrix Playlist|*.kashtrix;*.kashtrixplaylist|All files|*.*", Multiselect = true, Title = "Add Kashtrix playlists" };
        if (dialog.ShowDialog(this) == true) _editor.AddSources(dialog.FileNames);
    }

    private void SourceUp_Click(object sender, RoutedEventArgs e) => MoveSource(-1);
    private void SourceDown_Click(object sender, RoutedEventArgs e) => MoveSource(1);
    private void MoveSource(int delta)
    {
        var selected = SourceListBox.SelectedItem as string;
        _editor.MoveSource(selected, delta);
        SourceListBox.SelectedItem = selected;
    }

    private void RemoveSource_Click(object sender, RoutedEventArgs e) => _editor.RemoveSource(SourceListBox.SelectedItem as string);
    private void AddCg_Click(object sender, RoutedEventArgs e) => _editor.AddCg();
    private void RemoveCg_Click(object sender, RoutedEventArgs e) => _editor.RemoveCg(CgListBox.SelectedItem as string);

    private void ResumeSchedule_Click(object sender, RoutedEventArgs e) => _host?.ResumeSchedule();

    private void CutLive_Click(object sender, RoutedEventArgs e)
    {
        if (_host?.CutScheduledLive() != true) _editor.Message = "No scheduled live is on air.";
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_editor.IsDirty) return;
        var answer = MessageBox.Show(this, "Save changes to the channel schedule?", "Channel Schedule", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) { e.Cancel = true; return; }
        if (answer == MessageBoxResult.Yes) _editor.Save();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => WindowChromeActions.Drag(this, e);
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowChromeActions.ToggleMaximize(this);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
