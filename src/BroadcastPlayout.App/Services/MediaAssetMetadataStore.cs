using System.Text.Json;

namespace BroadcastPlayout.Services;

public sealed class MediaAssetMetadata
{
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Tags { get; set; } = string.Empty;
    public string Rights { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
    public string Category { get; set; } = "Media";
    public string Notes { get; set; } = string.Empty;
    public int Rating { get; set; }
    public DateTime LastUsedUtc { get; set; }
}

public static class MediaAssetMetadataStore
{
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static Dictionary<string, MediaAssetMetadata>? _cache;

    public static MediaAssetMetadata Get(string path)
    {
        lock (Sync)
        {
            EnsureLoaded();
            var key = Normalize(path);
            if (_cache!.TryGetValue(key, out var existing)) return Clone(existing);
            return new MediaAssetMetadata { Title = Path.GetFileNameWithoutExtension(path), Category = MediaCategoryStore.Get(path) };
        }
    }

    public static void Set(string path, MediaAssetMetadata metadata)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        lock (Sync)
        {
            EnsureLoaded();
            var key = Normalize(path);
            _cache![key] = Clone(metadata);
            Save();
        }
        if (!string.IsNullOrWhiteSpace(metadata.Category)) MediaCategoryStore.Set(path, metadata.Category);
    }

    public static IReadOnlyDictionary<string, MediaAssetMetadata> Snapshot()
    {
        lock (Sync)
        {
            EnsureLoaded();
            return _cache!.ToDictionary(x => x.Key, x => Clone(x.Value), StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path).Trim(); }
        catch { return path.Trim(); }
    }

    private static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KashtrixPlayout", "mam-metadata.json");

    private static void EnsureLoaded()
    {
        if (_cache is not null) return;
        _cache = new Dictionary<string, MediaAssetMetadata>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(StorePath)) return;
            var loaded = JsonSerializer.Deserialize<Dictionary<string, MediaAssetMetadata>>(File.ReadAllText(StorePath), JsonOptions);
            if (loaded is null) return;
            foreach (var pair in loaded) _cache[pair.Key] = pair.Value;
        }
        catch { }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            var temp = StorePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_cache, JsonOptions));
            File.Move(temp, StorePath, true);
        }
        catch { }
    }

    private static MediaAssetMetadata Clone(MediaAssetMetadata x) => new()
    {
        Title = x.Title,
        Description = x.Description,
        Tags = x.Tags,
        Rights = x.Rights,
        Owner = x.Owner,
        Category = x.Category,
        Notes = x.Notes,
        Rating = x.Rating,
        LastUsedUtc = x.LastUsedUtc
    };
}
