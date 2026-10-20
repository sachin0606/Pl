using System.IO;
using System.Text.Json;
using BroadcastPlayout.Models;

namespace BroadcastPlayout.Services;

public static class PlaylistDocumentService
{
    private sealed class Document
    {
        public string Format { get; set; } = "KASHTRIX-PLAYLIST";
        public int Version { get; set; } = 3;
        public DateTime SavedUtc { get; set; } = DateTime.UtcNow;
        public List<PlaylistItem> Items { get; set; } = [];
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static void Save(string path, IEnumerable<PlaylistItem> items)
    {
        var doc = new Document { Items = items.ToList(), SavedUtc = DateTime.UtcNow };
        File.WriteAllText(path, JsonSerializer.Serialize(doc, Json));
    }

    public static List<PlaylistItem> Load(string path)
    {
        var doc = JsonSerializer.Deserialize<Document>(File.ReadAllText(path), Json)
                  ?? throw new InvalidDataException("Invalid Kashtrix playlist document.");
        if (!string.Equals(doc.Format, "KASHTRIX-PLAYLIST", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The selected file is not a Kashtrix playlist.");
        return doc.Items ?? [];
    }
}
