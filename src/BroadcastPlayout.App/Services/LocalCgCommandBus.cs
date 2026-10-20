using System.Text.Json;
using BroadcastPlayout.Models;

namespace BroadcastPlayout.Services;

/// <summary>
/// Same-workstation CG transport used when the Channel Controller is not running.
/// Each playout channel owns a private inbox under LocalAppData, so PREVIEW and PROGRAM
/// commands can be delivered between the standalone CG Controller and Playout processes.
/// </summary>
public static class LocalCgCommandBus
{
    private sealed class Envelope
    {
        public string ChannelId { get; set; } = string.Empty;
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public CgRemoteCommand Command { get; set; } = new();
    }

    public static string GetInbox(string channelId)
    {
        var safe = string.Concat((channelId ?? string.Empty).Trim().ToUpperInvariant().Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        if (string.IsNullOrWhiteSpace(safe)) safe = "DEFAULT";
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KashtrixPlayout", "LocalCg", safe);
    }

    public static int Publish(IEnumerable<string> channelIds, CgRemoteCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var delivered = 0;
        foreach (var channelId in channelIds.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var folder = GetInbox(channelId);
            Directory.CreateDirectory(folder);
            var envelope = new Envelope { ChannelId = channelId.Trim(), CreatedUtc = DateTime.UtcNow, Command = command };
            var name = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.json";
            var final = Path.Combine(folder, name);
            var temp = final + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(envelope), new System.Text.UTF8Encoding(false));
            File.Move(temp, final);
            delivered++;
        }
        return delivered;
    }

    public static bool TryConsume(string path, out CgRemoteCommand? command)
    {
        command = null;
        try
        {
            if (!File.Exists(path)) return false;
            var json = File.ReadAllText(path);
            var envelope = JsonSerializer.Deserialize<Envelope>(json);
            try { File.Delete(path); } catch { }
            if (envelope is null || envelope.CreatedUtc < DateTime.UtcNow.AddMinutes(-2)) return false;
            command = envelope.Command;
            return command is not null;
        }
        catch
        {
            return false;
        }
    }
}
