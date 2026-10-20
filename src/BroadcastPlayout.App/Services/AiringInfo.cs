using BroadcastPlayout.Models;

namespace BroadcastPlayout.Services;

/// <summary>
/// What the Now/Next graphics show about one airing: the item on air, or something coming up from the
/// schedule or playlist. Immutable, safe to share across threads. Credit fields are empty for non-songs.
/// </summary>
public sealed record AiringInfo(string Title, string Film, string Singer, string Lyrics, string Music, string Label, string FilePath, double DurationSeconds)
{
    public static readonly AiringInfo Empty = new("", "", "", "", "", "", "", 0);

    /// <summary>Trim IN point inside the file; previews start relative to it.</summary>
    public double InPointSeconds { get; init; }
    /// <summary>Display type: SONG, MOVIE, PROGRAMME, NEWS, LIVE, FILLER (empty when unknown).</summary>
    public string Kind { get; init; } = string.Empty;
    /// <summary>Planned start for upcoming airings (null for the item on air).</summary>
    public DateTime? Start { get; init; }
    /// <summary>Live feeds get a title card instead of a video preview.</summary>
    public bool IsLive { get; init; }
    /// <summary>Optional still image (poster/thumbnail) for the title card.</summary>
    public string PosterPath { get; init; } = string.Empty;
    public RequestTag? Request { get; init; }

    /// <summary>Second line under a title: the film for songs, otherwise the type ("Movie", "News"). Empty for plain songs/filler.</summary>
    public string Subtitle => !string.IsNullOrWhiteSpace(Film) ? Film
        : Kind is "" or "SONG" or "FILLER" ? string.Empty : char.ToUpperInvariant(Kind[0]) + Kind[1..].ToLowerInvariant();

    public static string KindForCategory(string? category, bool isLive)
    {
        if (isLive) return "LIVE";
        var c = category ?? string.Empty;
        if (c.Contains("Music", StringComparison.OrdinalIgnoreCase) || c.Contains("Song", StringComparison.OrdinalIgnoreCase)) return "SONG";
        if (c.Contains("Movie", StringComparison.OrdinalIgnoreCase) || c.Contains("Film", StringComparison.OrdinalIgnoreCase)) return "MOVIE";
        if (c.Contains("News", StringComparison.OrdinalIgnoreCase)) return "NEWS";
        if (c.Contains("Live", StringComparison.OrdinalIgnoreCase)) return "LIVE";
        if (c.Contains("Filler", StringComparison.OrdinalIgnoreCase)) return "FILLER";
        if (c.Contains("Program", StringComparison.OrdinalIgnoreCase) || c.Contains("Show", StringComparison.OrdinalIgnoreCase)) return "PROGRAMME";
        return string.Empty;
    }
}
