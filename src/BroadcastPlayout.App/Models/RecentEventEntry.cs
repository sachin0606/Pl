namespace BroadcastPlayout.Models;

/// <summary>
/// One row of the operator EVENT LOG. Level drives the status dot and the log filter:
/// Info, OnAir (takes / on-air transitions), Warning, Error.
/// </summary>
public sealed class RecentEventEntry
{
    public RecentEventEntry(DateTime time, string message)
    {
        Time = time;
        Message = message;
        Level = Classify(message);
    }

    public DateTime Time { get; }
    public string TimeText => Time.ToString("HH:mm:ss");
    public string Message { get; }
    public string Level { get; }

    public string DotColor => Level switch
    {
        "Error" => "#FF5A3C",
        "Warning" => "#F5B83D",
        "OnAir" => "#EF4444",
        _ => "#55C878"
    };

    public string MessageColor => Level switch
    {
        "Error" => "#FF8A73",
        "Warning" => "#F5CD7A",
        "OnAir" => "#F28B8B",
        _ => "#D9DEE3"
    };

    public override string ToString() => $"{TimeText}  {Message}";

    private static string Classify(string message)
    {
        var m = message.ToUpperInvariant();
        if (m.Contains("ERROR") || m.Contains("FAIL") || m.Contains("EXCEPTION") || m.Contains("INVALID") || m.Contains("CRASH") || m.Contains("LOST")) return "Error";
        if (m.Contains("WARN") || m.Contains("MISSING") || m.Contains("TIMEOUT") || m.Contains("UNDERRUN") || m.Contains("DROPPED") ||
            m.Contains("INHIBITED") || m.Contains("EMERGENCY") || m.Contains("NO FOLLOWING")) return "Warning";
        if (m.StartsWith("TAKE") || m.StartsWith("ON AIR") || m.StartsWith("AD BREAK") || m.StartsWith("RESUME")) return "OnAir";
        return "Info";
    }
}
