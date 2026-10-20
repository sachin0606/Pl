using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BroadcastPlayout.Ffmpeg;
using BroadcastPlayout.Models;

namespace BroadcastPlayout.Views;

public partial class MediaTrimmerWindow : Window, INotifyPropertyChanged
{
    private readonly PlaylistItem _item;
    private readonly TimeSpan _sourceDuration;
    private readonly double _fps;
    private readonly DispatcherTimer _debounce;
    private readonly SemaphoreSlim _decodeGate = new(1,1);
    private int _previewGeneration;
    private TimeSpan _inPoint;
    private TimeSpan _outPoint;
    private WriteableBitmap? _bitmap;
    private MediaPart? _selectedPart;
    private bool _syncingMarkers;
    public bool SaveAsSeparateItems { get; private set; }

    public ObservableCollection<MediaPart> Parts { get; } = [];
    public MediaPart? SelectedPart { get => _selectedPart; set { _selectedPart=value; Raise(); if(value is not null){_inPoint=value.InPoint;_outPoint=value.OutPoint;SyncBoxes();} } }

    public MediaTrimmerWindow(PlaylistItem item)
    {
        if (item.IsLiveSource) throw new InvalidOperationException("Live input sources cannot be media-trimmed.");
        InitializeComponent(); DataContext=this;
        _item=item; _sourceDuration=item.SourceDuration>TimeSpan.Zero?item.SourceDuration:item.OutPoint;
        if(_sourceDuration<=TimeSpan.Zero)throw new InvalidOperationException("The source duration is not available for this item.");
        _fps=item.SourceFrameRate>0?item.SourceFrameRate:25;

        foreach(var part in item.Parts.OrderBy(x=>x.Order)) Parts.Add(part.Clone());
        if(Parts.Count==0)
        {
            var input=item.InPoint; var output=item.OutPoint>input?item.OutPoint:_sourceDuration;
            Parts.Add(new MediaPart{Name="Part 1",Order=1,InPoint=input,OutPoint=output,Enabled=true});
        }
        foreach(var part in Parts) part.PropertyChanged+=PartChanged;
        SelectedPart=Parts[0];

        FileText.Text=$"{item.Title}  ·  {item.SourceDescription}";
        SeekSlider.Minimum=0; SeekSlider.Maximum=_sourceDuration.TotalSeconds; InMarkerSlider.Minimum=0; InMarkerSlider.Maximum=_sourceDuration.TotalSeconds; OutMarkerSlider.Minimum=0; OutMarkerSlider.Maximum=_sourceDuration.TotalSeconds; SeekSlider.Value=Math.Clamp(_inPoint.TotalSeconds,0,_sourceDuration.TotalSeconds);
        EndText.Text=FormatMs(_sourceDuration); CurrentTimeText.Text=FormatMs(TimeSpan.FromSeconds(SeekSlider.Value));
        _debounce=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(90)};
        _debounce.Tick+=(_,_)=>{_debounce.Stop();_=LoadPreviewAsync(TimeSpan.FromSeconds(SeekSlider.Value));};
        RefreshSummary(); Loaded+=(_,_)=>_=LoadPreviewAsync(_inPoint);
    }

    private void PartChanged(object? sender,PropertyChangedEventArgs e)=>RefreshSummary();
    private void SeekSlider_ValueChanged(object sender,RoutedPropertyChangedEventArgs<double> e){if(!IsLoaded)return;CurrentTimeText.Text=FormatMs(TimeSpan.FromSeconds(e.NewValue));_debounce.Stop();_debounce.Start();}
    private async Task LoadPreviewAsync(TimeSpan target)
    {
        var generation=++_previewGeneration; LoadingText.Visibility=Visibility.Visible;
        try
        {
            await _decodeGate.WaitAsync();
            try
            {
                if(generation!=_previewGeneration)return;
                var frame=await Task.Run(()=>{using var decoder=new VideoDecoder(_item);decoder.Seek(target);while(decoder.TryRead(out var c))if(c.PtsSeconds+.05>=target.TotalSeconds)return c;return null;});
                if(generation!=_previewGeneration||frame is null)return;Present(frame);
            }
            finally{_decodeGate.Release();}
        }
        catch(Exception ex){if(generation==_previewGeneration){LoadingText.Text="PREVIEW ERROR · "+ex.Message;LoadingText.Visibility=Visibility.Visible;}return;}
        finally{if(generation==_previewGeneration&&LoadingText.Text.StartsWith("LOADING",StringComparison.Ordinal))LoadingText.Visibility=Visibility.Collapsed;}
    }
    private void Present(VideoFrameData frame){if(_bitmap is null||_bitmap.PixelWidth!=frame.Width||_bitmap.PixelHeight!=frame.Height){_bitmap=new WriteableBitmap(frame.Width,frame.Height,96,96,System.Windows.Media.PixelFormats.Bgra32,null);PreviewImage.Source=_bitmap;}_bitmap.WritePixels(new Int32Rect(0,0,frame.Width,frame.Height),frame.Bgra,frame.Stride,0);}

    private TimeSpan Position=>TimeSpan.FromSeconds(SeekSlider.Value); private double FrameStep=>1.0/Math.Max(1,_fps);
    private void Move(double seconds)=>SeekSlider.Value=Math.Clamp(SeekSlider.Value+seconds,0,SeekSlider.Maximum);
    private void Start_Click(object s,RoutedEventArgs e)=>SeekSlider.Value=0; private void End_Click(object s,RoutedEventArgs e)=>SeekSlider.Value=SeekSlider.Maximum;
    private void BackSecond_Click(object s,RoutedEventArgs e)=>Move(-1); private void ForwardSecond_Click(object s,RoutedEventArgs e)=>Move(1); private void BackFrame_Click(object s,RoutedEventArgs e)=>Move(-FrameStep); private void ForwardFrame_Click(object s,RoutedEventArgs e)=>Move(FrameStep);
    private void GoIn_Click(object s,RoutedEventArgs e)=>SeekSlider.Value=_inPoint.TotalSeconds; private void GoOut_Click(object s,RoutedEventArgs e)=>SeekSlider.Value=_outPoint.TotalSeconds;
    private void SetIn_Click(object s,RoutedEventArgs e){var v=Position;if(v>=_outPoint){MessageBox.Show("IN point must be before OUT point.","Multipart Trimmer");return;}_inPoint=v;SyncBoxes();}
    private void SetOut_Click(object s,RoutedEventArgs e){var v=Position;if(v<=_inPoint){MessageBox.Show("OUT point must be after IN point.","Multipart Trimmer");return;}_outPoint=v;SyncBoxes();}
    private void PointBox_LostFocus(object s,RoutedEventArgs e){if(!TryParse(InBox.Text,out var i)||!TryParse(OutBox.Text,out var o)){SyncBoxes();return;}i=Clamp(i);o=Clamp(o);if(o<=i){SyncBoxes();return;}_inPoint=i;_outPoint=o;SyncBoxes();}
    private void SyncBoxes(){if(InBox is null)return;_syncingMarkers=true;try{InBox.Text=FormatMs(_inPoint);OutBox.Text=FormatMs(_outPoint);InMarkerSlider.Value=_inPoint.TotalSeconds;OutMarkerSlider.Value=_outPoint.TotalSeconds;}finally{_syncingMarkers=false;}UpdateRangeInfo();}

    private void AddPart_Click(object s,RoutedEventArgs e){PointBox_LostFocus(s,e);if(_outPoint<=_inPoint)return;var p=new MediaPart{Name=$"Part {Parts.Count+1}",Order=Parts.Count+1,InPoint=_inPoint,OutPoint=_outPoint,Enabled=true};p.PropertyChanged+=PartChanged;Parts.Add(p);SelectedPart=p;RefreshSummary();}
    private void UpdatePart_Click(object s,RoutedEventArgs e){if(SelectedPart is null)return;PointBox_LostFocus(s,e);SelectedPart.InPoint=_inPoint;SelectedPart.OutPoint=_outPoint;RefreshSummary();}
    private void DuplicatePart_Click(object s,RoutedEventArgs e){if(SelectedPart is null)return;var p=SelectedPart.Clone();p.Name=SelectedPart.Name+" Copy";p.Order=Parts.Count+1;p.PropertyChanged+=PartChanged;Parts.Add(p);SelectedPart=p;Renumber();}
    private void RemovePart_Click(object s,RoutedEventArgs e){if(SelectedPart is null)return;var idx=Parts.IndexOf(SelectedPart);SelectedPart.PropertyChanged-=PartChanged;Parts.Remove(SelectedPart);Renumber();SelectedPart=Parts.Count==0?null:Parts[Math.Clamp(idx,0,Parts.Count-1)];RefreshSummary();}
    private void ClearParts_Click(object s,RoutedEventArgs e){if(MessageBox.Show("Remove every multipart range?","Multipart Trimmer",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;foreach(var p in Parts)p.PropertyChanged-=PartChanged;Parts.Clear();SelectedPart=null;RefreshSummary();}
    private void MoveUp_Click(object s,RoutedEventArgs e)=>MovePart(-1); private void MoveDown_Click(object s,RoutedEventArgs e)=>MovePart(1);
    private void MovePart(int d){if(SelectedPart is null)return;var i=Parts.IndexOf(SelectedPart);var n=i+d;if(n<0||n>=Parts.Count)return;Parts.Move(i,n);Renumber();RefreshSummary();}
    private void Renumber(){for(var i=0;i<Parts.Count;i++)Parts[i].Order=i+1;}
    private void GoPartIn_Click(object s,RoutedEventArgs e){if(SelectedPart is not null)SeekSlider.Value=SelectedPart.InPoint.TotalSeconds;}
    private void GoPartOut_Click(object s,RoutedEventArgs e){if(SelectedPart is not null)SeekSlider.Value=SelectedPart.OutPoint.TotalSeconds;}

    private void InMarkerSlider_ValueChanged(object s,RoutedPropertyChangedEventArgs<double> e){if(_syncingMarkers||!IsLoaded)return;var v=TimeSpan.FromSeconds(Math.Clamp(e.NewValue,0,_sourceDuration.TotalSeconds));if(v>=_outPoint){SyncBoxes();return;}_inPoint=v;SyncBoxes();}
    private void OutMarkerSlider_ValueChanged(object s,RoutedPropertyChangedEventArgs<double> e){if(_syncingMarkers||!IsLoaded)return;var v=TimeSpan.FromSeconds(Math.Clamp(e.NewValue,0,_sourceDuration.TotalSeconds));if(v<=_inPoint){SyncBoxes();return;}_outPoint=v;SyncBoxes();}
    private void TitleBar_MouseLeftButtonDown(object s,System.Windows.Input.MouseButtonEventArgs e)=>WindowChromeActions.Drag(this,e);
    private void Minimize_Click(object s,RoutedEventArgs e)=>WindowChromeActions.Minimize(this);
    private void Maximize_Click(object s,RoutedEventArgs e)=>WindowChromeActions.ToggleMaximize(this);
    private void Cancel_Click(object s,RoutedEventArgs e)=>DialogResult=false;
    private void Window_PreviewKeyDown(object s,System.Windows.Input.KeyEventArgs e){if(e.Key==System.Windows.Input.Key.Escape){DialogResult=false;e.Handled=true;}}
    private void SaveSeparate_Click(object s,RoutedEventArgs e){SaveAsSeparateItems=true;Save_Click(s,e);}

    private void AutoSplit_Click(object s, RoutedEventArgs e)
    {
        if (_sourceDuration <= TimeSpan.Zero)
        {
            MessageBox.Show("Source media duration is invalid or 0.", "Auto Split");
            return;
        }

        double intervalSec = 60;
        if (!string.IsNullOrWhiteSpace(AutoSplitIntervalBox?.Text))
        {
            if (!double.TryParse(AutoSplitIntervalBox.Text.Trim(), out intervalSec) || intervalSec <= 0)
            {
                MessageBox.Show("Please enter a valid split interval in seconds (e.g. 60).", "Auto Split");
                return;
            }
        }

        var partSpan = TimeSpan.FromSeconds(intervalSec);
        foreach (var p in Parts) p.PropertyChanged -= PartChanged;
        Parts.Clear();

        var currentIn = TimeSpan.Zero;
        int order = 1;
        while (currentIn < _sourceDuration)
        {
            var currentOut = currentIn + partSpan;
            if (currentOut > _sourceDuration) currentOut = _sourceDuration;

            var part = new MediaPart
            {
                Name = $"Part {order}",
                Order = order,
                InPoint = currentIn,
                OutPoint = currentOut,
                Enabled = true
            };
            part.PropertyChanged += PartChanged;
            Parts.Add(part);

            currentIn = currentOut;
            order++;
        }

        if (Parts.Count > 0)
        {
            SelectedPart = Parts[0];
            RefreshSummary();
            SaveAsSeparateItems = true;
            Save_Click(s, e);
        }
    }

    private void Save_Click(object s,RoutedEventArgs e)
    {
        if(Parts.Count==0){MessageBox.Show("Add at least one media part.","Multipart Trimmer");return;}
        var valid=Parts.Where(x=>x.Enabled&&x.OutPoint>x.InPoint).OrderBy(x=>x.Order).ToArray();
        if(valid.Length==0){MessageBox.Show("Enable at least one valid part.","Multipart Trimmer");return;}
        _item.SourceDuration=_sourceDuration;_item.Parts=new ObservableCollection<MediaPart>(Parts.OrderBy(x=>x.Order).Select(x=>x.Clone()));
        _item.InPoint=valid[0].InPoint;_item.OutPoint=valid[^1].OutPoint;DialogResult=true;
    }

    private void UpdateRangeInfo(){var d=_outPoint>_inPoint?_outPoint-_inPoint:TimeSpan.Zero;DurationText.Text=FormatMs(d);TrimInfoText.Text=$"IN {FormatMs(_inPoint)} · OUT {FormatMs(_outPoint)} · {_fps:0.###} fps";}
    private void RefreshSummary(){var duration=TimeSpan.FromTicks(Parts.Where(x=>x.Enabled&&x.OutPoint>x.InPoint).Sum(x=>x.Duration.Ticks));TotalDurationText.Text=FormatMs(duration);PartsCountText.Text=$"{Parts.Count} defined · {Parts.Count(x=>x.Enabled)} enabled";UpdateRangeInfo();}
    private TimeSpan Clamp(TimeSpan v)=>v<TimeSpan.Zero?TimeSpan.Zero:v>_sourceDuration?_sourceDuration:v;
    private static bool TryParse(string? t,out TimeSpan v)=>Timecode.TryParse(t,out v);
    private static string FormatMs(TimeSpan v)=>$"{(int)v.TotalHours:00}:{v.Minutes:00}:{v.Seconds:00}.{v.Milliseconds:000}";
    public event PropertyChangedEventHandler? PropertyChanged; private void Raise([CallerMemberName]string? n=null)=>PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(n));
}
