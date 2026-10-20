namespace BroadcastPlayout.Services;

public static class RecoveryService
{
    private static string LocalRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KashtrixPlayout");
    private static string RoamingRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KashtrixPlayout");
    private static string MarkerPath => Path.Combine(LocalRoot, "pending-reset.txt");

    public static string CacheSummary => $"{Path.Combine(LocalRoot, "chromium-cache")} · {MediaThumbnailService.CacheFolder}";

    public static void ScheduleCacheClear()
    {
        Directory.CreateDirectory(LocalRoot);
        File.WriteAllText(MarkerPath, "cache");
    }

    public static void ScheduleFactoryReset()
    {
        Directory.CreateDirectory(LocalRoot);
        File.WriteAllText(MarkerPath, "factory");
    }

    /// <summary>Runs before CEF/database/settings are opened, so locked files can be removed safely.</summary>
    public static void ApplyPendingStartupReset()
    {
        try
        {
            if (!File.Exists(MarkerPath)) return;
            var mode = File.ReadAllText(MarkerPath).Trim();
            if (mode.Equals("factory", StringComparison.OrdinalIgnoreCase))
            {
                DeleteFile(SettingsStore.SettingsPath);
                DeleteFile(BroadcastDatabase.DatabasePath);
                DeleteFile(BroadcastDatabase.DatabasePath + "-wal");
                DeleteFile(BroadcastDatabase.DatabasePath + "-shm");
                DeleteFile(Path.Combine(LocalRoot, "media-categories.json"));
                DeleteDirectory(Path.Combine(LocalRoot, "LocalCg"));
                DeleteDirectory(Path.Combine(LocalRoot, "Demo"));
                DeleteDirectory(Path.Combine(LocalRoot, "html-packages"));
            }

            DeleteDirectory(Path.Combine(LocalRoot, "chromium-cache"));
            DeleteDirectory(MediaThumbnailService.CacheFolder);
            DeleteFile(Path.Combine(LocalRoot, "playlist.reload"));
            DeleteFile(Path.Combine(LocalRoot, "scheduler.reload"));
            DeleteFile(MarkerPath);
        }
        catch
        {
            // Leave the marker in place when cleanup fails so the next launch can retry.
        }
    }

    private static void DeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void DeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }
}
