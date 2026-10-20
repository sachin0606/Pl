using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BroadcastPlayout.Models;

public sealed class OutputRoute : INotifyPropertyChanged
{
    private string _name = "NDI PROGRAM 2";
    private string _kind = "NDI";
    private bool _enabled;
    private string _sourceName = "KASHTRIX-OUTPUT-02";
    private string _status = "OFF";
    private double _framesPerSecond = 25.0;
    private int _deviceIndex;
    private OutputProfile _profile = new() { Preset = "1920x1080" };

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Kind { get => _kind; set => Set(ref _kind, string.IsNullOrWhiteSpace(value) ? "NDI" : value.Trim().ToUpperInvariant()); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public string SourceName { get => _sourceName; set => Set(ref _sourceName, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public double FramesPerSecond { get => _framesPerSecond; set => Set(ref _framesPerSecond, Math.Clamp(value, 1.0, 120.0)); }
    public int DeviceIndex { get => _deviceIndex; set => Set(ref _deviceIndex, Math.Clamp(value, 0, 31)); }
    public OutputProfile Profile { get => _profile; set => Set(ref _profile, value ?? new OutputProfile()); }
    public string Summary => $"{Kind} · {Profile.Summary} · {FramesPerSecond:0.###} fps";

    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (propertyName is nameof(Kind) or nameof(FramesPerSecond) or nameof(DeviceIndex) or nameof(Profile))
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
        return true;
    }
}
