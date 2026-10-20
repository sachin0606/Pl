using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BroadcastPlayout.Models;

public sealed class MediaLibraryItem : INotifyPropertyChanged
{
    private string _duration = "--:--:--";
    private string _format = "Not probed";
    private string _thumbnailPath = string.Empty;
    private string _qcStatus = "—";
    private string _category = "Media";
    public string FilePath { get; set; } = string.Empty;
    public string Name => Path.GetFileNameWithoutExtension(FilePath);
    public string FileName => Path.GetFileName(FilePath);
    public string Extension => Path.GetExtension(FilePath).TrimStart('.').ToUpperInvariant();
    public string ParsedName => BroadcastPlayout.Services.MediaNameParser.Parse(FilePath, Name).Display;
    public string Category { get => _category; set => Set(ref _category, string.IsNullOrWhiteSpace(value) ? "Media" : value.Trim()); }
    public long SizeBytes { get; set; }
    public string SizeText => SizeBytes >= 1024L*1024*1024 ? $"{SizeBytes/(1024d*1024*1024):0.00} GB" : $"{SizeBytes/(1024d*1024):0.0} MB";
    public DateTime ModifiedUtc { get; set; }
    public string Duration { get => _duration; set => Set(ref _duration,value); }
    public string Format { get => _format; set => Set(ref _format,value); }
    public string ThumbnailPath { get => _thumbnailPath; set => Set(ref _thumbnailPath,value); }
    public string QcStatus { get => _qcStatus; set => Set(ref _qcStatus,value); }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field,T value,[CallerMemberName]string? n=null){if(EqualityComparer<T>.Default.Equals(field,value))return;field=value;PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(n));}
}
