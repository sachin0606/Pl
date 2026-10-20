using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BroadcastPlayout.Models;

/// <summary>
/// A logical part of one source media file. A PlaylistItem can contain many enabled
/// parts so an operator can reuse a single source file without inserting it repeatedly.
/// Parts are played in Order and their durations are concatenated into one playout event.
/// </summary>
public sealed class MediaPart : INotifyPropertyChanged
{
    private string _name = "Part 1";
    private int _order = 1;
    private TimeSpan _inPoint;
    private TimeSpan _outPoint;
    private bool _enabled = true;
    private string _note = string.Empty;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get => _name; set => Set(ref _name, string.IsNullOrWhiteSpace(value) ? $"Part {_order}" : value.Trim()); }
    public int Order { get => _order; set => Set(ref _order, Math.Max(1, value)); }
    public bool Enabled { get => _enabled; set { if (Set(ref _enabled, value)) Raise(nameof(Duration)); } }
    public string Note { get => _note; set => Set(ref _note, value ?? string.Empty); }

    public TimeSpan InPoint
    {
        get => _inPoint;
        set
        {
            if (!Set(ref _inPoint, value < TimeSpan.Zero ? TimeSpan.Zero : value)) return;
            Raise(nameof(InPointText)); Raise(nameof(Duration)); Raise(nameof(DurationText));
        }
    }

    public TimeSpan OutPoint
    {
        get => _outPoint;
        set
        {
            if (!Set(ref _outPoint, value < TimeSpan.Zero ? TimeSpan.Zero : value)) return;
            Raise(nameof(OutPointText)); Raise(nameof(Duration)); Raise(nameof(DurationText));
        }
    }

    public TimeSpan Duration => Enabled && OutPoint > InPoint ? OutPoint - InPoint : TimeSpan.Zero;
    public string DurationText => Timecode.Format(Duration, includeFrames: false);
    public string InPointText { get => Timecode.Format(InPoint, includeFrames: false); set { if (Timecode.TryParse(value, out var t)) InPoint = t; else Raise(); } }
    public string OutPointText { get => Timecode.Format(OutPoint, includeFrames: false); set { if (Timecode.TryParse(value, out var t)) OutPoint = t; else Raise(); } }

    public MediaPart Clone() => new()
    {
        Id = Guid.NewGuid(), Name = Name, Order = Order, InPoint = InPoint,
        OutPoint = OutPoint, Enabled = Enabled, Note = Note
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Raise(propertyName); return true;
    }
    private void Raise([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>Shared clock parser/formatter used by playout, trimming and editor UIs.</summary>
public static class Timecode
{
    public static string Format(TimeSpan value, bool includeFrames = false, double fps = 25)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        var hours = (int)value.TotalHours;
        if (!includeFrames) return $"{hours:00}:{value.Minutes:00}:{value.Seconds:00}";
        var frames = (int)Math.Floor((value.TotalSeconds - Math.Floor(value.TotalSeconds)) * Math.Max(1, fps));
        return $"{hours:00}:{value.Minutes:00}:{value.Seconds:00}:{frames:00}";
    }

    public static bool TryParse(string? text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var clean = text.Trim().Replace(';', ':');
        var p = clean.Split(':');
        if (p.Length == 4 && int.TryParse(p[0], out var h) && int.TryParse(p[1], out var m) && int.TryParse(p[2], out var s) && int.TryParse(p[3], out var f))
        {
            value = TimeSpan.FromHours(h) + TimeSpan.FromMinutes(m) + TimeSpan.FromSeconds(s) + TimeSpan.FromMilliseconds(f * 40.0);
            return value >= TimeSpan.Zero;
        }
        return TimeSpan.TryParse(clean, out value) && value >= TimeSpan.Zero;
    }
}
