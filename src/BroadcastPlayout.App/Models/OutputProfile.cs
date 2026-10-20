using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BroadcastPlayout.Models;

public sealed class OutputProfile : INotifyPropertyChanged
{
    private string _preset = "1920x1080";
    private int _width = 1920;
    private int _height = 1080;
    private string _scaling = "Fit";
    private double _framesPerSecond = 25.0;
    private string _scanMode = "Progressive";
    private string _alphaMode = "Straight (Unmatted)";
    private int _prerollFrames = 0;

    public static IReadOnlyList<string> Presets { get; } =
    [
        "Source",
        "3840x2160",
        "1920x1080",
        "1280x720",
        "720x576",
        "720x480",
        "Custom"
    ];

    public static IReadOnlyList<string> ScalingModes { get; } = ["Fit", "Fill", "Stretch"];
    public static IReadOnlyList<double> FrameRates { get; } = [23.976, 24, 25, 29.97, 30, 50, 59.94, 60];
    public static IReadOnlyList<string> ScanModes { get; } = ["Progressive", "Interlaced Upper First", "Interlaced Lower First"];
    public static IReadOnlyList<string> AlphaModes { get; } = ["Straight (Unmatted)", "Premultiplied"];

    public string Preset
    {
        get => _preset;
        set
        {
            if (!Set(ref _preset, value)) return;
            ApplyPreset(value);
            Raise(nameof(Summary));
        }
    }

    public int Width
    {
        get => _width;
        set
        {
            value = Math.Clamp(value, 16, 7680);
            if ((value & 1) != 0) value++;
            if (!Set(ref _width, value)) return;
            if (_preset != "Custom" && _preset != "Source") _preset = "Custom";
            Raise(nameof(Preset));
            Raise(nameof(Summary));
        }
    }

    public int Height
    {
        get => _height;
        set
        {
            value = Math.Clamp(value, 16, 4320);
            if ((value & 1) != 0) value++;
            if (!Set(ref _height, value)) return;
            if (_preset != "Custom" && _preset != "Source") _preset = "Custom";
            Raise(nameof(Preset));
            Raise(nameof(Summary));
        }
    }

    public string Scaling
    {
        get => _scaling;
        set => Set(ref _scaling, string.IsNullOrWhiteSpace(value) ? "Fit" : value);
    }

    public double FramesPerSecond
    {
        get => _framesPerSecond;
        set
        {
            var fps = Math.Clamp(value, 1.0, 120.0);
            if (!Set(ref _framesPerSecond, fps)) return;
            Raise(nameof(Summary));
        }
    }

    public string ScanMode
    {
        get => _scanMode;
        set
        {
            var normalized = ScanModes.Contains(value) ? value : "Progressive";
            if (!Set(ref _scanMode, normalized)) return;
            Raise(nameof(IsInterlaced));
            Raise(nameof(Summary));
        }
    }

    public string AlphaMode
    {
        get => _alphaMode;
        set => Set(ref _alphaMode, AlphaModes.Contains(value) ? value : "Straight (Unmatted)");
    }

    public int PrerollFrames
    {
        get => _prerollFrames;
        set => Set(ref _prerollFrames, Math.Clamp(value, 0, 120));
    }

    public bool IsInterlaced => !ScanMode.Equals("Progressive", StringComparison.OrdinalIgnoreCase);

    public bool UsesSourceResolution => Preset == "Source";
    public string Summary => (UsesSourceResolution ? "SOURCE" : $"{Width}x{Height}") + $" @ {FramesPerSecond:0.###} fps · {ScanMode}";

    public (int Width, int Height) Resolve(int sourceWidth, int sourceHeight) =>
        UsesSourceResolution ? (sourceWidth, sourceHeight) : (Width, Height);

    public void LoadFrom(OutputProfile? other)
    {
        if (other is null) return;
        _preset = other.Preset;
        _width = other.Width;
        _height = other.Height;
        _scaling = other.Scaling;
        _framesPerSecond = other.FramesPerSecond;
        _scanMode = ScanModes.Contains(other.ScanMode) ? other.ScanMode : "Progressive";
        _alphaMode = AlphaModes.Contains(other.AlphaMode) ? other.AlphaMode : "Straight (Unmatted)";
        _prerollFrames = Math.Clamp(other.PrerollFrames, 0, 120);
        Raise(nameof(Preset));
        Raise(nameof(Width));
        Raise(nameof(Height));
        Raise(nameof(Scaling));
        Raise(nameof(FramesPerSecond));
        Raise(nameof(ScanMode));
        Raise(nameof(AlphaMode));
        Raise(nameof(PrerollFrames));
        Raise(nameof(IsInterlaced));
        Raise(nameof(Summary));
    }

    public OutputProfile Clone()
    {
        var copy = new OutputProfile();
        copy._preset = _preset;
        copy._width = _width;
        copy._height = _height;
        copy._scaling = _scaling;
        copy._framesPerSecond = _framesPerSecond;
        copy._scanMode = _scanMode;
        copy._alphaMode = _alphaMode;
        copy._prerollFrames = _prerollFrames;
        return copy;
    }

    private void ApplyPreset(string value)
    {
        switch (value)
        {
            case "3840x2160": _width = 3840; _height = 2160; break;
            case "1920x1080": _width = 1920; _height = 1080; break;
            case "1280x720": _width = 1280; _height = 720; break;
            case "720x576": _width = 720; _height = 576; break;
            case "720x480": _width = 720; _height = 480; break;
            case "Source": break;
            case "Custom": break;
            default:
                _preset = "Custom";
                break;
        }

        Raise(nameof(Width));
        Raise(nameof(Height));
        Raise(nameof(UsesSourceResolution));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
