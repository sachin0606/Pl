using System.Windows;
using System.Windows.Input;
using BroadcastPlayout.Models;
using BroadcastPlayout.Services;
using BroadcastPlayout.ViewModels;

namespace BroadcastPlayout.Views;

public partial class EpgOutputSettingsWindow : Window
{
    private readonly MainViewModel _vm;
    private EpgOutputSettings Epg => _vm.Settings.Epg;
    public EpgOutputSettingsWindow(MainViewModel vm)
    {
        InitializeComponent(); _vm=vm; _vm.Settings.Epg ??= new EpgOutputSettings(); _vm.Settings.Epg.Normalize(); DataContext=_vm.Settings.Epg;
        Loaded += (_,_) => RefreshPreview();
    }
    private void RefreshPreview(){try{PreviewBox.Text=EpgGenerator.BuildPreview(_vm.Playlist,_vm.ChannelId,_vm.ChannelName,Epg);StatusText.Text="Preview generated from current rundown.";}catch(Exception ex){PreviewBox.Text=ex.Message;StatusText.Text="Preview error";}}
    private void RefreshPreview_Click(object sender,RoutedEventArgs e)=>RefreshPreview();
    private void Save_Click(object sender,RoutedEventArgs e){Epg.Normalize();_vm.SaveSettings();AuditLogService.Write("EPG_SETTINGS_SAVE",_vm.ChannelName,$"{Epg.ProviderInfoName} · {Epg.TimeFormat}");RefreshPreview();StatusText.Text="Saved.";}
    private void Reset_Click(object sender,RoutedEventArgs e){if(MessageBox.Show("Reset EPG/auto-graphics output settings to Kashtrix defaults?","EPG Settings",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;_vm.Settings.Epg=new EpgOutputSettings();DataContext=_vm.Settings.Epg;_vm.SaveSettings();RefreshPreview();}
    private void TitleBar_MouseLeftButtonDown(object sender,MouseButtonEventArgs e)=>WindowChromeActions.Drag(this,e);
    private void Minimize_Click(object sender,RoutedEventArgs e)=>WindowChromeActions.Minimize(this);
    private void Maximize_Click(object sender,RoutedEventArgs e)=>WindowChromeActions.ToggleMaximize(this);
    private void Close_Click(object sender,RoutedEventArgs e)=>Close();
}
