using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using BroadcastPlayout.Models;
using BroadcastPlayout.Services;
using BroadcastPlayout.ViewModels;

namespace BroadcastPlayout.Views;

public partial class PlaylistEditorWindow : Window
{
    private readonly MainViewModel _vm;
    private Point _dragStart;
    public PlaylistEditorWindow(MainViewModel vm)
    {
        InitializeComponent(); _vm = vm; DataContext = vm;
        _vm.PreviewFrameReady += OnPreview; _vm.PreviewCleared += OnPreviewCleared;
        Closed += (_,_) => { _vm.StopPreviewPlayback(); _vm.PreviewFrameReady -= OnPreview; _vm.PreviewCleared -= OnPreviewCleared; };
    }
    private void OnPreview(VideoFrameData frame) => Dispatcher.BeginInvoke(() => { var img=BitmapSource.Create(frame.Width,frame.Height,96,96,System.Windows.Media.PixelFormats.Bgra32,null,frame.Bgra,frame.Stride); img.Freeze(); PreviewImage.Source=img; });
    private void OnPreviewCleared() => Dispatcher.BeginInvoke(() => PreviewImage.Source=null);
    private void LoadDemo_Click(object sender, RoutedEventArgs e)
    {
        _vm.LoadDemoWorkspace();
        _vm.SelectedItem = _vm.Playlist.FirstOrDefault();
        SignalPlayoutReload();
    }

    private void AddInput_Click(object sender, RoutedEventArgs e) { var w=new InputSourceWindow{Owner=this}; if(w.ShowDialog()==true && w.ResultItem is not null) _vm.AddInputSource(w.ResultItem); }
    private void Trim_Click(object sender, RoutedEventArgs e) { if(_vm.SelectedItem is null) return; var item=_vm.SelectedItem; var w=new MediaTrimmerWindow(item){Owner=this}; if(w.ShowDialog()==true){ if(w.SaveAsSeparateItems)_vm.ReplaceWithPartEvents(item); else _vm.NotifySelectedItemEdited(); } }
    private void Duplicate_Click(object sender, RoutedEventArgs e) { var i=_vm.SelectedItem; if(i is null)return; var c=Clone(i); var idx=_vm.Playlist.IndexOf(i)+1; _vm.Playlist.Insert(Math.Clamp(idx,0,_vm.Playlist.Count),c); _vm.SelectedItem=c; _vm.NotifySelectedItemEdited(); Renumber(); }
    private void MoveUp_Click(object sender, RoutedEventArgs e) { Move(-1); }
    private void MoveDown_Click(object sender, RoutedEventArgs e) { Move(1); }
    private void Move(int delta){var i=_vm.SelectedItem;if(i is null)return;var idx=_vm.Playlist.IndexOf(i);var ni=idx+delta;if(ni<0||ni>=_vm.Playlist.Count)return;_vm.Playlist.Move(idx,ni);Renumber();_vm.NotifySelectedItemEdited();}
    private void Renumber(){for(var i=0;i<_vm.Playlist.Count;i++)_vm.Playlist[i].Sequence=i+1;}
    private void PreviewPlayPause_Click(object sender, RoutedEventArgs e) => _vm.TogglePreviewPlayback();
    private void PreviewStop_Click(object sender, RoutedEventArgs e) => _vm.StopPreviewPlayback();
    private void SetIn_Click(object sender,RoutedEventArgs e){var i=_vm.SelectedItem;if(i is null||!i.CanTrim)return;i.InPoint=TimeSpan.FromSeconds(Math.Clamp(_vm.PreviewSeekSeconds,0,i.SourceDuration.TotalSeconds));_vm.NotifySelectedItemEdited();}
    private void SetOut_Click(object sender,RoutedEventArgs e){var i=_vm.SelectedItem;if(i is null||!i.CanTrim)return;var p=TimeSpan.FromSeconds(Math.Clamp(_vm.PreviewSeekSeconds,0,i.SourceDuration.TotalSeconds));if(p>i.InPoint)i.OutPoint=p;_vm.NotifySelectedItemEdited();}
    private void AddStop_Click(object s,RoutedEventArgs e)=>_vm.AddControlEvent("STOP");
    private void AddPlay_Click(object s,RoutedEventArgs e)=>_vm.AddControlEvent("PLAY");
    private void AddPause_Click(object s,RoutedEventArgs e)=>_vm.AddControlEvent("PAUSE");
    private void AddNote_Click(object s,RoutedEventArgs e)=>_vm.AddControlEvent("NOTE");
    private void Properties_Click(object s,RoutedEventArgs e){var item=_vm.SelectedItem;if(item is null)return;var w=new PlaylistItemPropertiesWindow(item){Owner=this};if(w.ShowDialog()==true)_vm.NotifySelectedItemEdited();}
    private void PlaylistGrid_MouseDoubleClick(object s,MouseButtonEventArgs e)
    {
        if(e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(PlaylistGrid,d) is DataGridRow r)
        {
            _vm.SelectedItem=r.Item as PlaylistItem; Properties_Click(s,e); e.Handled=true;
        }
        else
        {
            _vm.AddCommand.Execute(null); e.Handled=true;
        }
    }
    private void PlaylistGrid_PreviewMouseLeftButtonDown(object s,MouseButtonEventArgs e)=>_dragStart=e.GetPosition(PlaylistGrid);
    private void PlaylistGrid_PreviewMouseMove(object s,MouseEventArgs e){if(e.LeftButton!=MouseButtonState.Pressed||_vm.SelectedItem is null)return;var p=e.GetPosition(PlaylistGrid);if(Math.Abs(p.X-_dragStart.X)<SystemParameters.MinimumHorizontalDragDistance&&Math.Abs(p.Y-_dragStart.Y)<SystemParameters.MinimumVerticalDragDistance)return;DragDrop.DoDragDrop(PlaylistGrid,new DataObject("Kashtrix.PlaylistItem",_vm.SelectedItem),DragDropEffects.Move);}
    private void PlaylistGrid_Drop(object s,DragEventArgs e){if(!e.Data.GetDataPresent("Kashtrix.PlaylistItem")||e.Data.GetData("Kashtrix.PlaylistItem") is not PlaylistItem item)return;var row=e.OriginalSource is DependencyObject d?ItemsControl.ContainerFromElement(PlaylistGrid,d) as DataGridRow:null;var target=row?.Item as PlaylistItem;var idx=target is null?_vm.Playlist.Count-1:_vm.Playlist.IndexOf(target);_vm.MoveItemTo(item,Math.Max(0,idx));e.Handled=true;}
    private void Open_Click(object sender,RoutedEventArgs e)=>_vm.OpenPlaylistDocument();
    private void Save_Click(object sender,RoutedEventArgs e)=>_vm.SavePlaylistDocument();
    private void Send_Click(object sender,RoutedEventArgs e)
    {
        _vm.NotifySelectedItemEdited();
        SignalPlayoutReload();
        MessageBox.Show("Playlist published to Kashtrix Playout. If a channel is currently on air, the refreshed rundown is applied safely when the active playback session ends.","Playlist Editor");
    }
    private static void SignalPlayoutReload()
    {
        try
        {
            var folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"KashtrixPlayout");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder,"playlist.reload"), DateTime.UtcNow.ToString("O"));
        }
        catch { }
    }
    private void OpenSettings_Click(object sender,RoutedEventArgs e)
    {
        if (!StandaloneAppLauncher.Launch("Settings", out var error)) MessageBox.Show(error, "Kashtrix", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private void Close_Click(object sender,RoutedEventArgs e)=>Close();
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowChromeActions.Minimize(this);
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowChromeActions.ToggleMaximize(this);
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => WindowChromeActions.Drag(this, e);
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { Close(); e.Handled = true; } }
    private static PlaylistItem Clone(PlaylistItem x)
    {
        // JSON clone keeps every per-item operator setting (audio/video/EPG/colors/parts)
        // instead of silently dropping newer fields when an item is duplicated in the editor.
        var clone = JsonSerializer.Deserialize<PlaylistItem>(JsonSerializer.Serialize(x)) ?? new PlaylistItem();
        clone.Id = Guid.NewGuid();
        clone.Title = x.Title + " Copy";
        clone.Status = "Ready";
        return clone;
    }
}
