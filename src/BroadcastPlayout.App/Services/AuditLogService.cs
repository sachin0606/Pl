using System.Text.Json;

namespace BroadcastPlayout.Services;

public sealed class AuditRecord
{
    public DateTime TimestampUtc { get; set; }
    public string Action { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Details { get; set; } = "";
    public string User { get; set; } = "";
    public string Machine { get; set; } = "";
    public int ProcessId { get; set; }
}

/// <summary>
/// Append-only operator audit journal.  One JSON object is written per line so a sudden power
/// loss cannot corrupt the full log and external newsroom/MCR tools can tail it safely.
/// </summary>
public static class AuditLogService
{
    private static readonly object Gate = new();
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KashtrixPlayout", "Logs", "Audit");
    public static string CurrentPath => Path.Combine(DirectoryPath, $"audit-{DateTime.Now:yyyy-MM-dd}.jsonl");

    public static void Write(string action, string? subject = null, string? details = null)
    {
        try
        {
            var record = new AuditRecord
            {
                TimestampUtc = DateTime.UtcNow,
                Action = string.IsNullOrWhiteSpace(action) ? "EVENT" : action.Trim(),
                Subject = subject?.Trim() ?? "",
                Details = details?.Trim() ?? "",
                User = Environment.UserName,
                Machine = Environment.MachineName,
                ProcessId = Environment.ProcessId
            };
            var line = JsonSerializer.Serialize(record);
            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                File.AppendAllText(CurrentPath, line + Environment.NewLine);
            }
            RuntimeDiagnosticsService.Write("AUDIT", record.Action, record);
        }
        catch { /* audit must never interrupt playout */ }
    }

    public static IReadOnlyList<AuditRecord> ReadRecent(int max = 2000)
    {
        try
        {
            if (!System.IO.Directory.Exists(DirectoryPath)) return [];
            var files = System.IO.Directory.GetFiles(DirectoryPath, "audit-*.jsonl").OrderByDescending(x => x).Take(14).ToArray();
            var output = new List<AuditRecord>();
            foreach (var file in files)
            {
                foreach (var line in File.ReadLines(file).Reverse())
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try { var row = JsonSerializer.Deserialize<AuditRecord>(line); if (row is not null) output.Add(row); } catch { }
                    if (output.Count >= Math.Max(1, max)) return output;
                }
            }
            return output;
        }
        catch { return []; }
    }

    public static void ExportCsv(string path, IEnumerable<AuditRecord> records)
    {
        static string Csv(string? value) => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var writer = new StreamWriter(path, false, new System.Text.UTF8Encoding(true));
        writer.WriteLine("TimestampUtc,Action,Subject,Details,User,Machine,ProcessId");
        foreach (var r in records)
            writer.WriteLine($"{Csv(r.TimestampUtc.ToString("O"))},{Csv(r.Action)},{Csv(r.Subject)},{Csv(r.Details)},{Csv(r.User)},{Csv(r.Machine)},{r.ProcessId}");
    }
}
