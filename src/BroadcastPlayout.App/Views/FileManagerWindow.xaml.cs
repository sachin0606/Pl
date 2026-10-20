using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using BroadcastPlayout.Models;
using BroadcastPlayout.Services;
using BroadcastPlayout.ViewModels;
using Forms = System.Windows.Forms;

namespace BroadcastPlayout.Views;

public partial class FileManagerWindow : Window, INotifyPropertyChanged
{
    private readonly MainViewModel _vm;private readonly AppSettings _settings;private readonly DispatcherTimer _clock=new(){Interval=TimeSpan.FromSeconds(1)};
    private string? _selectedFolder;private MediaLibraryItem? _selectedMedia;private CancellationTokenSource? _probeCts;
    public ObservableCollection<string> Folders{get;}=[];public ObservableCollection<MediaLibraryItem> Media{get;}=[];
    public string? SelectedFolder{get=>_selectedFolder;set{_selectedFolder=value;Raise();_ = RefreshMediaAsync();}}
    public MediaLibraryItem? SelectedMedia{get=>_selectedMedia;set{_selectedMedia=value;Raise();}}
    public FileManagerWindow(MainViewModel vm){InitializeComponent();_vm=vm;_settings=SettingsStore.Load();DataContext=this;foreach(var f in _settings.MediaFolders.Where(Directory.Exists))Folders.Add(f);if(Folders.Count==0&&Directory.Exists(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)))Folders.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));SelectedFolder=Folders.FirstOrDefault();_clock.Tick+=(_,_)=>ClockText.Text=DateTime.Now.ToString("HH:mm:ss");_clock.Start();ClockText.Text=DateTime.Now.ToString("HH:mm:ss");Closed+=(_,_)=>{_clock.Stop();try{_probeCts?.Cancel();_probeCts?.Dispose();}catch{}};}
    private void AddFolder_Click(object s,RoutedEventArgs e){using var d=new Forms.FolderBrowserDialog{Description="Add Kashtrix media root",UseDescriptionForTitle=true};if(d.ShowDialog()!=Forms.DialogResult.OK)return;if(!Folders.Any(x=>string.Equals(x,d.SelectedPath,StringComparison.OrdinalIgnoreCase)))Folders.Add(d.SelectedPath);_settings.MediaFolders=Folders.ToList();SettingsStore.Save(_settings);SelectedFolder=d.SelectedPath;}
    private async void Refresh_Click(object s,RoutedEventArgs e)=>await RefreshMediaAsync();
    private async Task RefreshMediaAsync()
    {
        try{_probeCts?.Cancel();_probeCts?.Dispose();}catch{}
        _probeCts=new CancellationTokenSource();
        var ct=_probeCts.Token;
        Media.Clear();if(string.IsNullOrWhiteSpace(SelectedFolder))return;StatusText.Text="Scanning media…";
        var scanned=await Task.Run(()=>MediaLibraryService.ScanFolder(SelectedFolder).ToArray(),ct);
        foreach(var x in scanned)Media.Add(x);
        ApplyMediaFilter();
        StatusText.Text=$"{Media.Count} media file(s) · probing duration / format…";
        _=ProbeListedMediaAsync(scanned.Take(500).ToArray(),ct);
    }
    private async Task ProbeListedMediaAsync(MediaLibraryItem[] items,CancellationToken ct)
    {
        using var gate=new SemaphoreSlim(3);var done=0;
        var jobs=items.Select(async item=>
        {
            await gate.WaitAsync(ct);
            try{await MediaLibraryService.ProbeAsync(item,ct);Interlocked.Increment(ref done);if(done%10==0)_=Dispatcher.BeginInvoke(new Action(()=>StatusText.Text=$"{Media.Count} media file(s) · metadata {done}/{items.Length}"));}
            catch(OperationCanceledException){}
            finally{gate.Release();}
        }).ToArray();
        try{await Task.WhenAll(jobs);if(!ct.IsCancellationRequested)StatusText.Text=$"{Media.Count} media file(s) · metadata ready";}catch(OperationCanceledException){}
    }
    private async void MediaList_SelectionChanged(object s,SelectionChangedEventArgs e){if(SelectedMedia is null)return;StatusText.Text="Probing "+SelectedMedia.FileName;await MediaLibraryService.ProbeAsync(SelectedMedia);StatusText.Text=SelectedMedia.Format;}
    private void SearchBox_TextChanged(object s,TextChangedEventArgs e)=>ApplyMediaFilter();
    private void TypeFilter_SelectionChanged(object s,SelectionChangedEventArgs e)=>ApplyMediaFilter();
    private void ApplyMediaFilter()
    {
        var q=SearchBox?.Text?.Trim()??string.Empty;
        var type=(TypeFilter?.SelectedItem as ComboBoxItem)?.Content?.ToString()??"ALL";
        var view=CollectionViewSource.GetDefaultView(Media);
        view.Filter=o=>
        {
            if(o is not MediaLibraryItem m)return false;
            if(!string.IsNullOrWhiteSpace(q)&&!(m.FileName.Contains(q,StringComparison.OrdinalIgnoreCase)||m.Format.Contains(q,StringComparison.OrdinalIgnoreCase)||m.Category.Contains(q,StringComparison.OrdinalIgnoreCase)||m.ParsedName.Contains(q,StringComparison.OrdinalIgnoreCase)))return false;
            var ext=m.Extension.ToUpperInvariant();
            return type switch
            {
                "VIDEO"=>new[]{"MP4","MOV","MXF","MKV","AVI","TS","M2TS","MPG","MPEG","WEBM","WMV"}.Contains(ext),
                "AUDIO"=>new[]{"WAV","MP3","AAC","M4A","FLAC","OGG"}.Contains(ext),
                "GRAPHICS"=>new[]{"PNG","JPG","JPEG","BMP","TIF","TIFF","GIF"}.Contains(ext),
                _=>true
            };
        };
    }
    private async void Qc_Click(object s,RoutedEventArgs e){if(SelectedMedia is null)return;StatusText.Text="QC running…";var r=await QualityControlService.CheckAsync(SelectedMedia.FilePath,_vm.QcPreset,false);SelectedMedia.QcStatus=r.Status;StatusText.Text=r.Summary+(r.Passed?string.Empty:" · "+r.Detail.Replace(Environment.NewLine," "));}
    private async void AddToPlaylist_Click(object s,RoutedEventArgs e){if(SelectedMedia is null)return;try{StatusText.Text="Importing…";var item=await _vm.ImportMediaFileAsync(SelectedMedia.FilePath);item.Category=SelectedMedia.Category;_vm.NotifySelectedItemEdited();SignalReload();StatusText.Text=$"Added to playlist · {item.Title}";}catch(Exception ex){StatusText.Text="Import failed · "+ex.Message;}}
    private async void AddAndTrim_Click(object s,RoutedEventArgs e){if(SelectedMedia is null)return;try{var item=await _vm.ImportMediaFileAsync(SelectedMedia.FilePath);item.Category=SelectedMedia.Category;var w=new MediaTrimmerWindow(item){Owner=this};if(w.ShowDialog()==true){if(w.SaveAsSeparateItems)_vm.ReplaceWithPartEvents(item);else _vm.NotifySelectedItemEdited();}SignalReload();StatusText.Text=$"Trim workflow saved · {item.Title}";}catch(Exception ex){MessageBox.Show(ex.Message,"Multipart Import");}}
    private void SaveCategory_Click(object s,RoutedEventArgs e){if(SelectedMedia is null)return;var category=CategoryBox.Text?.Trim();if(string.IsNullOrWhiteSpace(category))category="Media";SelectedMedia.Category=category;MediaCategoryStore.Set(SelectedMedia.FilePath,category);StatusText.Text=$"Category saved · {category}";}
    private void MediaList_PreviewMouseMove(object s,MouseEventArgs e){if(e.LeftButton!=MouseButtonState.Pressed||SelectedMedia is null)return;var data=new DataObject();data.SetData("Kashtrix.MediaFile",SelectedMedia.FilePath);data.SetData(DataFormats.FileDrop,new[]{SelectedMedia.FilePath});DragDrop.DoDragDrop(MediaList,data,DragDropEffects.Copy);}
    private void OpenExplorer_Click(object s,RoutedEventArgs e){if(SelectedMedia is null)return;try{Process.Start(new ProcessStartInfo("explorer.exe",$"/select,\"{SelectedMedia.FilePath}\""){UseShellExecute=true});}catch{}}
    private void OpenPlaylistEditor_Click(object s,RoutedEventArgs e){if(!StandaloneAppLauncher.Launch("PlaylistEditor",out var error))MessageBox.Show(error,"Kashtrix");}
    private static void SignalReload(){try{var f=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"KashtrixPlayout");Directory.CreateDirectory(f);File.WriteAllText(Path.Combine(f,"playlist.reload"),DateTime.UtcNow.ToString("O"));}catch{}}
    public event PropertyChangedEventHandler? PropertyChanged;private void Raise([CallerMemberName]string? n=null)=>PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(n));

    private void TitleBar_MouseLeftButtonDown(object s, MouseButtonEventArgs e) => WindowChromeActions.Drag(this, e);
    private void Minimize_Click(object s, RoutedEventArgs e) => WindowChromeActions.Minimize(this);
    private void Maximize_Click(object s, RoutedEventArgs e) { WindowChromeActions.ToggleMaximize(this); if (MaxGlyph is not null) MaxGlyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922"; }
    private void Close_Click(object s, RoutedEventArgs e) => Close();
    private void Window_PreviewKeyDown(object s, KeyEventArgs e) { if (e.Key == Key.Escape) { Close(); e.Handled = true; } }
}
