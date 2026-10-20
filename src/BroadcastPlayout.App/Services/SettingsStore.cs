using System.IO;
using System.Text.Json;
using BroadcastPlayout.Models;

namespace BroadcastPlayout.Services;

public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "KashtrixPlayout",
        "settings.json");

    public static string LocalSettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KashtrixPlayout",
        "settings.json");

    public static AppSettings Load()
    {
        try
        {
            var path = File.Exists(SettingsPath) ? SettingsPath :
                       File.Exists(LocalSettingsPath) ? LocalSettingsPath : null;
            if (path is null) return new AppSettings();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var roamingFolder = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(roamingFolder);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(SettingsPath, json);

        var localFolder = Path.GetDirectoryName(LocalSettingsPath)!;
        Directory.CreateDirectory(localFolder);
        try { File.WriteAllText(LocalSettingsPath, json); } catch { }

        // Touch settings.reload signals for immediate cross-process synchronization
        try
        {
            var nowTicks = DateTime.UtcNow.Ticks.ToString();
            File.WriteAllText(Path.Combine(roamingFolder, "settings.reload"), nowTicks);
            File.WriteAllText(Path.Combine(localFolder, "settings.reload"), nowTicks);
        }
        catch { }
    }
}
