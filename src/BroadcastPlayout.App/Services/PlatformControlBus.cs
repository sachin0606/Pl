using System.Text.Json;

namespace BroadcastPlayout.Services;

public sealed class PlatformControlCommand
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ChannelId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string ReplyPath { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class PlatformControlResponse
{
    public string Id { get; set; } = string.Empty;
    public bool Ok { get; set; }
    public string Code { get; set; } = "OK";
    public string Message { get; set; } = string.Empty;
    public DateTime CompletedUtc { get; set; } = DateTime.UtcNow;
    public Dictionary<string, string> Data { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class PlatformChannelStatus
{
    public string ChannelId { get; set; } = string.Empty;
    public string ChannelName { get; set; } = string.Empty;
    public string Engine { get; set; } = "OFF";
    public string CurrentItemId { get; set; } = string.Empty;
    public string CurrentTitle { get; set; } = string.Empty;
    public string SelectedItemId { get; set; } = string.Empty;
    public string SelectedTitle { get; set; } = string.Empty;
    public double ElapsedSeconds { get; set; }
    public double DurationSeconds { get; set; }
    public bool OutputArmed { get; set; }
    public string HaStatus { get; set; } = string.Empty;
    public string CapabilityStatus { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

public static class PlatformControlBus
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false, PropertyNameCaseInsensitive = true };
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KashtrixPlayout", "PlatformBus");
    public static string Inbox(string channelId) => Path.Combine(Root, "inbox", Safe(channelId));
    public static string ReplyRoot => Path.Combine(Root, "replies");
    public static string StatusRoot => Path.Combine(Root, "status");

    public static string Submit(PlatformControlCommand command)
    {
        command.Id = string.IsNullOrWhiteSpace(command.Id) ? Guid.NewGuid().ToString("N") : command.Id.Trim();
        if (string.IsNullOrWhiteSpace(command.ChannelId)) throw new ArgumentException("ChannelId is required.", nameof(command));
        Directory.CreateDirectory(Inbox(command.ChannelId));
        Directory.CreateDirectory(ReplyRoot);
        command.ReplyPath = string.IsNullOrWhiteSpace(command.ReplyPath) ? Path.Combine(ReplyRoot, command.Id + ".json") : command.ReplyPath;
        var target = Path.Combine(Inbox(command.ChannelId), command.Id + ".json");
        var temp = target + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(command, Json));
        File.Move(temp, target, true);
        return command.ReplyPath;
    }

    public static async Task<PlatformControlResponse> SubmitAndWaitAsync(PlatformControlCommand command, TimeSpan timeout, CancellationToken ct = default)
    {
        var reply = Submit(command);
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(reply))
            {
                try
                {
                    var text = await File.ReadAllTextAsync(reply, ct).ConfigureAwait(false);
                    var result = JsonSerializer.Deserialize<PlatformControlResponse>(text, Json);
                    try { File.Delete(reply); } catch { }
                    if (result is not null) return result;
                }
                catch (IOException) { }
            }
            await Task.Delay(40, ct).ConfigureAwait(false);
        }
        return new PlatformControlResponse { Id = command.Id, Ok = false, Code = "TIMEOUT", Message = "No response from playout channel within the requested timeout." };
    }

    public static bool TryConsume(string path, out PlatformControlCommand? command)
    {
        command = null;
        try
        {
            var text = File.ReadAllText(path);
            command = JsonSerializer.Deserialize<PlatformControlCommand>(text, Json);
            File.Delete(path);
            return command is not null;
        }
        catch { return false; }
    }

    public static void Complete(PlatformControlCommand command, bool ok, string code, string message, IReadOnlyDictionary<string, string>? data = null)
    {
        if (string.IsNullOrWhiteSpace(command.ReplyPath)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(command.ReplyPath)!);
            var response = new PlatformControlResponse { Id = command.Id, Ok = ok, Code = code, Message = message };
            if (data is not null) foreach (var pair in data) response.Data[pair.Key] = pair.Value;
            File.WriteAllText(command.ReplyPath, JsonSerializer.Serialize(response, Json));
        }
        catch { }
    }

    public static void PublishStatus(PlatformChannelStatus status)
    {
        try
        {
            Directory.CreateDirectory(StatusRoot);
            status.UpdatedUtc = DateTime.UtcNow;
            var target = Path.Combine(StatusRoot, Safe(status.ChannelId) + ".json");
            var temp = target + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(status, Json));
            File.Move(temp, target, true);
        }
        catch { }
    }

    public static IReadOnlyList<PlatformChannelStatus> ReadStatuses()
    {
        if (!Directory.Exists(StatusRoot)) return Array.Empty<PlatformChannelStatus>();
        var result = new List<PlatformChannelStatus>();
        foreach (var file in Directory.EnumerateFiles(StatusRoot, "*.json"))
        {
            try
            {
                var status = JsonSerializer.Deserialize<PlatformChannelStatus>(File.ReadAllText(file), Json);
                if (status is not null) result.Add(status);
            }
            catch { }
        }
        return result.OrderBy(x => x.ChannelId, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string Safe(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string((value ?? string.Empty).Where(ch => !invalid.Contains(ch)).ToArray()).Trim();
    }
}
