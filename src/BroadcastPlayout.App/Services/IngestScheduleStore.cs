using System.Text.Json;
using BroadcastPlayout.Models;

namespace BroadcastPlayout.Services;

public sealed class IngestScheduleRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; } = true;
    public DateTime StartLocal { get; set; }
    public DateTime? StopLocal { get; set; }
    public PlaylistItem Source { get; set; } = new();
    public string RecordFormat { get; set; } = "H.264 MP4 · Broadcast";
    public string OutputPath { get; set; } = "";
    public bool DualMasterProxy { get; set; }
    public string ProxyOutputPath { get; set; } = "";
    public bool ComplianceMode { get; set; }
    public int SegmentMinutes { get; set; } = 60;
    public bool GrowingFileMode { get; set; }
}

public static class IngestScheduleStore
{
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static string StorePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KashtrixPlayout", "ingest-schedule.json");

    public static List<IngestScheduleRecord> Load()
    {
        lock (Sync)
        {
            try
            {
                if (!File.Exists(StorePath)) return [];
                return JsonSerializer.Deserialize<List<IngestScheduleRecord>>(File.ReadAllText(StorePath), Json) ?? [];
            }
            catch { return []; }
        }
    }

    public static void Save(IEnumerable<IngestScheduleRecord> records)
    {
        lock (Sync)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            var temp = StorePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(records.ToArray(), Json));
            File.Move(temp, StorePath, true);
        }
    }
}
