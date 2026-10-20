using System.Text.Json;

namespace BroadcastPlayout.Services;

public static class MediaCategoryStore
{
    private static readonly object Sync = new();
    private static readonly string StorePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KashtrixPlayout", "media-categories.json");
    private static Dictionary<string,string>? _cache;

    public static string Get(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Media";
        lock (Sync)
        {
            EnsureLoaded();
            if (_cache!.TryGetValue(Normalize(path), out var category) && !string.IsNullOrWhiteSpace(category)) return category;
        }
        return MediaCategoryClassifier.Classify(path);
    }

    public static void Set(string path, string category)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        lock (Sync)
        {
            EnsureLoaded();
            _cache![Normalize(path)] = string.IsNullOrWhiteSpace(category) ? "Media" : category.Trim();
            Save();
        }
    }

    private static void EnsureLoaded()
    {
        if (_cache is not null) return;
        try
        {
            _cache = File.Exists(StorePath)
                ? JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(StorePath)) ?? new(StringComparer.OrdinalIgnoreCase)
                : new(StringComparer.OrdinalIgnoreCase);
        }
        catch { _cache = new(StringComparer.OrdinalIgnoreCase); }
        if (_cache.Comparer != StringComparer.OrdinalIgnoreCase)
            _cache = new Dictionary<string,string>(_cache, StringComparer.OrdinalIgnoreCase);
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path).Trim().ToUpperInvariant(); }
        catch { return path.Trim().ToUpperInvariant(); }
    }
}
