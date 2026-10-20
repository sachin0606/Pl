using BroadcastPlayout.Services;

namespace BroadcastPlayout.Scheduling;

/// <summary>
/// Per-channel schedule persistence in the shared Kashtrix database. Every channel has its own key,
/// so several playout processes on one machine never read or overwrite each other's schedule.
/// </summary>
public static class ChannelScheduleStore
{
    private static string Key(string channelId) => "channel-schedule.v1:" + Normalize(channelId);
    private static string SignalFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KashtrixPlayout", "ChannelSchedule");

    public static string Normalize(string? channelId) => string.IsNullOrWhiteSpace(channelId) ? "DEFAULT" : channelId.Trim().ToUpperInvariant();

    public static ChannelSchedule Load(string? channelId)
    {
        var id = Normalize(channelId);
        ChannelSchedule schedule;
        try { schedule = new BroadcastDatabase().LoadState<ChannelSchedule?>(Key(id), null) ?? new ChannelSchedule(); }
        catch { schedule = new ChannelSchedule(); }
        schedule.ChannelId = id;
        schedule.EnsureDefaultFiller();
        return schedule;
    }

    /// <summary>Saves and tells the channel's playout process to reload.</summary>
    public static void Save(ChannelSchedule schedule)
    {
        schedule.ChannelId = Normalize(schedule.ChannelId);
        schedule.EnsureDefaultFiller();
        new BroadcastDatabase().SaveState(Key(schedule.ChannelId), schedule);
        try
        {
            Directory.CreateDirectory(SignalFolder);
            File.WriteAllText(SignalPath(schedule.ChannelId), DateTime.UtcNow.ToString("O"));
        }
        catch { /* the playout process also reloads on its own start */ }
    }

    private static string SignalPath(string channelId)
    {
        var safe = string.Concat(Normalize(channelId).Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        return Path.Combine(SignalFolder, safe + ".reload");
    }

    /// <summary>One lightweight watcher for this channel's signal file only. Dispose to stop.</summary>
    public static IDisposable Watch(string? channelId, Action onChanged)
    {
        Directory.CreateDirectory(SignalFolder);
        var watcher = new FileSystemWatcher(SignalFolder, Path.GetFileName(SignalPath(Normalize(channelId))))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };
        // Editors write the file once; FileSystemWatcher can raise twice, so collapse bursts.
        var last = DateTime.MinValue;
        void Fire(object? _, FileSystemEventArgs __)
        {
            var now = DateTime.UtcNow;
            if ((now - last).TotalMilliseconds < 300) return;
            last = now;
            try { onChanged(); } catch { }
        }
        watcher.Changed += Fire;
        watcher.Created += Fire;
        return watcher;
    }
}
