using System.Text.Json;
using BroadcastPlayout.Models;

namespace BroadcastPlayout.Services;

public static class ChannelRegistryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KashtrixPlayout");
    private static string FilePath => Path.Combine(Folder, "channels.json");

    public static List<ChannelDefinition> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            return JsonSerializer.Deserialize<List<ChannelDefinition>>(File.ReadAllText(FilePath), JsonOptions) ?? [];
        }
        catch { return []; }
    }

    public static void Save(IEnumerable<ChannelDefinition> channels)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(channels.ToList(), JsonOptions));
    }
}
