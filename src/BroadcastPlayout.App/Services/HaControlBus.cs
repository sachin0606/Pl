using System.Text.Json;
using BroadcastPlayout.Professional;

namespace BroadcastPlayout.Services;

public sealed class HaControlCommand
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Action { get; set; } = string.Empty; // force_active | release
    public string ChannelId { get; set; } = string.Empty;
    public string NodeId { get; set; } = string.Empty;
    public int DurationSeconds { get; set; } = 600;
    public string Reason { get; set; } = string.Empty;
    public string ReplyPath { get; set; } = string.Empty;
}

public sealed class HaControlResponse
{
    public string Id { get; set; } = string.Empty;
    public bool Ok { get; set; }
    public string Code { get; set; } = "OK";
    public string Message { get; set; } = string.Empty;
    public DateTime CompletedUtc { get; set; } = DateTime.UtcNow;
}

public static class HaControlBus
{
    private static readonly JsonSerializerOptions Json = new(){PropertyNameCaseInsensitive=true};
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"KashtrixPlayout","HaControlBus");
    public static string Inbox => Path.Combine(Root,"inbox");
    public static string Replies => Path.Combine(Root,"replies");
    public static string SnapshotPath => Path.Combine(Root,"nodes.json");

    public static string Submit(HaControlCommand command)
    {
        Directory.CreateDirectory(Inbox);Directory.CreateDirectory(Replies);
        command.Id=string.IsNullOrWhiteSpace(command.Id)?Guid.NewGuid().ToString("N"):command.Id.Trim();
        command.ReplyPath=string.IsNullOrWhiteSpace(command.ReplyPath)?Path.Combine(Replies,command.Id+".json"):command.ReplyPath;
        var target=Path.Combine(Inbox,command.Id+".json");var temp=target+".tmp";
        File.WriteAllText(temp,JsonSerializer.Serialize(command,Json));File.Move(temp,target,true);return command.ReplyPath;
    }

    public static async Task<HaControlResponse> SubmitAndWaitAsync(HaControlCommand command,TimeSpan timeout,CancellationToken ct=default)
    {
        var reply=Submit(command);var deadline=DateTime.UtcNow+timeout;
        while(DateTime.UtcNow<deadline)
        {
            ct.ThrowIfCancellationRequested();
            if(File.Exists(reply))
            {
                try{var text=await File.ReadAllTextAsync(reply,ct).ConfigureAwait(false);var r=JsonSerializer.Deserialize<HaControlResponse>(text,Json);try{File.Delete(reply);}catch{}if(r is not null)return r;}catch(IOException){}
            }
            await Task.Delay(40,ct).ConfigureAwait(false);
        }
        return new HaControlResponse{Id=command.Id,Ok=false,Code="TIMEOUT",Message="HA Controller did not respond."};
    }

    public static bool TryConsume(string path,out HaControlCommand? command)
    {
        command=null;try{command=JsonSerializer.Deserialize<HaControlCommand>(File.ReadAllText(path),Json);File.Delete(path);return command is not null;}catch{return false;}
    }

    public static void Complete(HaControlCommand command,bool ok,string code,string message)
    {
        if(string.IsNullOrWhiteSpace(command.ReplyPath))return;try{Directory.CreateDirectory(Path.GetDirectoryName(command.ReplyPath)!);File.WriteAllText(command.ReplyPath,JsonSerializer.Serialize(new HaControlResponse{Id=command.Id,Ok=ok,Code=code,Message=message},Json));}catch{}
    }

    public static void PublishSnapshot(IReadOnlyList<HaNodeSnapshot> rows)
    {
        try{Directory.CreateDirectory(Root);var temp=SnapshotPath+".tmp";File.WriteAllText(temp,JsonSerializer.Serialize(rows,Json));File.Move(temp,SnapshotPath,true);}catch{}
    }

    public static IReadOnlyList<HaNodeSnapshot> ReadSnapshot()
    {
        try{return File.Exists(SnapshotPath)?JsonSerializer.Deserialize<List<HaNodeSnapshot>>(File.ReadAllText(SnapshotPath),Json)??[]:[];}catch{return Array.Empty<HaNodeSnapshot>();}
    }
}
