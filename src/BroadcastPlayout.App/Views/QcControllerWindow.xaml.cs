using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using BroadcastPlayout.Models;
using BroadcastPlayout.Services;
using BroadcastPlayout.ViewModels;
using Forms = System.Windows.Forms;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace BroadcastPlayout.Views;

public partial class QcControllerWindow : Window, INotifyPropertyChanged
{
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _clock = new(){Interval=TimeSpan.FromSeconds(1)};
    private MediaQcResult? _selectedResult;
    private CancellationTokenSource? _scanCts;
    public ObservableCollection<MediaQcResult> Results { get; }=[];
    public MediaQcPreset QcPreset => _vm.QcPreset;
    public MediaQcResult? SelectedResult { get=>_selectedResult; set{_selectedResult=value;Raise();} }
    public QcControllerWindow(MainViewModel vm){InitializeComponent();_vm=vm;DataContext=this;_clock.Tick+=(_,_)=>ClockText.Text=DateTime.Now.ToString("HH:mm:ss");_clock.Start();ClockText.Text=DateTime.Now.ToString("HH:mm:ss");Closed+=(_,_)=>{_clock.Stop();_scanCts?.Cancel();};}
    private void AddFiles_Click(object s,RoutedEventArgs e){var d=new OpenFileDialog{Multiselect=true,Filter="Media|*.mp4;*.mov;*.mxf;*.mkv;*.avi;*.ts;*.m2ts;*.mpg;*.mpeg;*.webm;*.wmv|All files|*.*"};if(d.ShowDialog()!=true)return;foreach(var f in d.FileNames)AddPlaceholder(f);}
    private void AddFolder_Click(object s,RoutedEventArgs e){using var d=new Forms.FolderBrowserDialog{Description="Add media folder to QC queue",UseDescriptionForTitle=true};if(d.ShowDialog()!=Forms.DialogResult.OK)return;foreach(var f in MediaLibraryService.ScanFolder(d.SelectedPath).Select(x=>x.FilePath))AddPlaceholder(f);}
    private void AddPlaceholder(string f){if(Results.Any(x=>string.Equals(x.FilePath,f,StringComparison.OrdinalIgnoreCase)))return;Results.Add(new MediaQcResult{FilePath=f,Passed=false,Summary="Queued",Detail="Not checked yet"});Raise(nameof(Results));}
    private async void FastQc_Click(object s,RoutedEventArgs e)=>await RunAsync(false);
    private async void DeepQc_Click(object s,RoutedEventArgs e)=>await RunAsync(true);
    private async Task RunAsync(bool deep)
    {
        _scanCts?.Cancel();_scanCts=new CancellationTokenSource();var ct=_scanCts.Token;var files=Results.Select(x=>x.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();if(files.Length==0)return;
        QcProgress.Value=0;StatusText.Text=deep?"Deep QC running…":"Fast QC running…";
        try
        {
            for(var i=0;i<files.Length;i++)
            {
                ct.ThrowIfCancellationRequested();var baseProgress=i/(double)files.Length;var p=new Progress<double>(v=>QcProgress.Value=Math.Clamp(baseProgress+v/files.Length,0,1));
                var result=await QualityControlService.CheckAsync(files[i],QcPreset,deep,p,ct);var old=Results.FirstOrDefault(x=>string.Equals(x.FilePath,files[i],StringComparison.OrdinalIgnoreCase));var idx=old is null?-1:Results.IndexOf(old);if(idx>=0)Results[idx]=result;else Results.Add(result);SelectedResult=result;QcProgress.Value=(i+1)/(double)files.Length;StatusText.Text=$"{i+1}/{files.Length} · {result.Status} · {result.FileName}";
            }
            StatusText.Text=$"Complete · {Results.Count(x=>x.Passed)} pass · {Results.Count(x=>x.Summary.StartsWith("FAIL",StringComparison.OrdinalIgnoreCase))} fail";
        }
        catch(OperationCanceledException){StatusText.Text="QC cancelled";}
        catch(Exception ex){StatusText.Text="QC error · "+ex.Message;}
    }
    private void Clear_Click(object s,RoutedEventArgs e){_scanCts?.Cancel();Results.Clear();SelectedResult=null;QcProgress.Value=0;StatusText.Text="Ready";}
    private void OpenSettings_Click(object s,RoutedEventArgs e){if(!StandaloneAppLauncher.Launch("Settings",out var error))MessageBox.Show(error,"Kashtrix");}
    public event PropertyChangedEventHandler? PropertyChanged;private void Raise([CallerMemberName]string? n=null)=>PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(n));

    private void TitleBar_MouseLeftButtonDown(object s, MouseButtonEventArgs e) => WindowChromeActions.Drag(this, e);
    private void Minimize_Click(object s, RoutedEventArgs e) => WindowChromeActions.Minimize(this);
    private void Maximize_Click(object s, RoutedEventArgs e) { WindowChromeActions.ToggleMaximize(this); if (MaxGlyph is not null) MaxGlyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922"; }
    private void Close_Click(object s, RoutedEventArgs e) => Close();
    private void Window_PreviewKeyDown(object s, KeyEventArgs e) { if (e.Key == Key.Escape) { Close(); e.Handled = true; } }
}
