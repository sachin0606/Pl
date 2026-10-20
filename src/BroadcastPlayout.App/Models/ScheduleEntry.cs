using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BroadcastPlayout.Models;

public sealed class ScheduleEntry : INotifyPropertyChanged
{
    private string _name = "Morning Schedule";
    private DateTime _startAt = DateTime.Today.AddHours(10);
    private string _playlistPath = string.Empty;
    private string _repeat = "One Time";
    private bool _enabled = true;
    private string _startMode = "Hard Start";
    private string _eventType = "Playlist";
    private string _sourceLabel = "";
    private TimeSpan _duration = TimeSpan.Zero;
    private string _category = "Program";
    private string _customDaysCsv = "Monday,Tuesday,Wednesday,Thursday,Friday";
    private int _priority = 50;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get => _name; set => Set(ref _name, value); }
    public DateTime StartAt { get => _startAt; set => Set(ref _startAt, value); }
    public string PlaylistPath { get => _playlistPath; set => Set(ref _playlistPath, value); }
    public string Repeat { get => _repeat; set => Set(ref _repeat, value); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public string StartMode { get => _startMode; set => Set(ref _startMode, value); }
    public string EventType { get => _eventType; set => Set(ref _eventType, value); }
    public string SourceLabel { get => _sourceLabel; set => Set(ref _sourceLabel, value); }
    public TimeSpan Duration { get => _duration; set => Set(ref _duration, value); }
    public string Category { get => _category; set => Set(ref _category, value); }
    public string CustomDaysCsv { get => _customDaysCsv; set => Set(ref _customDaysCsv, value ?? string.Empty); }
    public int Priority { get => _priority; set => Set(ref _priority, Math.Clamp(value, 0, 999)); }
    public bool IsHardStart => StartMode.Equals("Hard Start", StringComparison.OrdinalIgnoreCase);
    public string DurationText => Duration <= TimeSpan.Zero ? "--:--:--" : $"{(int)Duration.TotalHours:00}:{Duration.Minutes:00}:{Duration.Seconds:00}";
    public string Status => Enabled ? "Ready" : "Disabled";
    public string StatusText => Enabled ? $"{Repeat} · {StartAt:dd MMM HH:mm:ss}" : "DISABLED";

    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DurationText)));
        return true;
    }
}
