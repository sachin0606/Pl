using System.Collections.Concurrent;
using System.Globalization;
using BroadcastPlayout.Models;

namespace BroadcastPlayout.Services;

/// <summary>
/// Wall-clock source for DigitalClock / AnalogClock / DateTime / Nepali layers.  An empty
/// TimeZoneId keeps the playout machine's local time.  Accepts Windows IDs ("Nepal Standard
/// Time"), IANA IDs ("Asia/Kathmandu") and fixed offsets ("UTC+05:45", "+5:30", "GMT-4").
/// </summary>
public static class CgClockTime
{
    private static readonly ConcurrentDictionary<string, TimeZoneInfo?> Zones = new(StringComparer.OrdinalIgnoreCase);

    public static DateTime Now(CgLayer? layer) => Now(layer?.TimeZoneId);

    public static DateTime Now(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId)) return DateTime.Now;
        var zone = Zones.GetOrAdd(timeZoneId.Trim(), FindZone);
        return zone is null ? DateTime.Now : TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
    }

    private static TimeZoneInfo? FindZone(string id)
    {
        if (TryParseOffset(id, out var offset))
            return TimeZoneInfo.CreateCustomTimeZone(id, offset, id, id);
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { return null; }
    }

    private static bool TryParseOffset(string id, out TimeSpan offset)
    {
        offset = TimeSpan.Zero;
        var s = id.Trim();
        if (s.StartsWith("UTC", StringComparison.OrdinalIgnoreCase) || s.StartsWith("GMT", StringComparison.OrdinalIgnoreCase)) s = s[3..].Trim();
        if (s.Length == 0) return id.Trim().Length == 3; // bare "UTC" / "GMT"
        if (s[0] is not ('+' or '-')) return false;
        var negative = s[0] == '-';
        var body = s[1..];
        if (!body.Contains(':')) body += ":00";
        if (!TimeSpan.TryParseExact(body, new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out var span) || span > TimeSpan.FromHours(14)) return false;
        offset = negative ? -span : span;
        return true;
    }
}
