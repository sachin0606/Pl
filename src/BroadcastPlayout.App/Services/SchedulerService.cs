using BroadcastPlayout.Models;

namespace BroadcastPlayout.Services;

public sealed class SchedulerService : IDisposable
{
    private readonly object _sync = new();
    private readonly Timer _timer;
    private readonly Dictionary<Guid, DateTime> _lastFired = [];
    private List<ScheduleEntry> _entries = [];
    private bool _disposed;

    public event Action<ScheduleEntry>? Due;

    public SchedulerService() => _timer = new Timer(_ => Tick(), null, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200));

    public void SetEntries(IEnumerable<ScheduleEntry> entries)
    {
        lock (_sync) _entries = entries.ToList();
    }

    private void Tick()
    {
        if (_disposed) return;
        var now = DateTime.Now;
        List<ScheduleEntry> snapshot;
        lock (_sync) snapshot = _entries.Where(x => x.Enabled).ToList();

        foreach (var entry in snapshot)
        {
            if (!IsDue(entry, now)) continue;
            lock (_sync)
            {
                if (_lastFired.TryGetValue(entry.Id, out var fired) && (now - fired).TotalSeconds < 30) continue;
                _lastFired[entry.Id] = now;
            }
            try { Due?.Invoke(entry); } catch { }
        }
    }

    private static bool IsDue(ScheduleEntry entry, DateTime now)
    {
        var target = entry.StartAt;
        // Poll at 200 ms so hard starts land very close to the configured wall clock. A small
        // positive tolerance avoids missing an event if Windows briefly delays the timer.
        var clockDelta = (now.TimeOfDay - target.TimeOfDay).TotalSeconds;
        var sameClock = clockDelta >= -0.05 && clockDelta <= 0.85;
        if (!sameClock) return false;
        return entry.Repeat switch
        {
            "Daily" => true,
            "Weekdays" => now.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday,
            "Weekly" => now.DayOfWeek == target.DayOfWeek,
            "Custom Days" => entry.CustomDaysCsv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Any(x => x.Equals(now.DayOfWeek.ToString(), StringComparison.OrdinalIgnoreCase)),
            _ => now.Date == target.Date
        };
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
    }
}
