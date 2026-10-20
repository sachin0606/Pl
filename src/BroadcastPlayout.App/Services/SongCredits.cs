using System.Text;
using BroadcastPlayout.Models;

namespace BroadcastPlayout.Services;

/// <summary>Everything the music Now/Next graphics show about one song. Immutable, safe to share across threads.</summary>
public sealed record SongInfo(string Title, string Film, string Singer, string Lyrics, string Music, string Label, string FilePath, double DurationSeconds)
{
    public static readonly SongInfo Empty = new("", "", "", "", "", "", "", 0);

    /// <summary>Trim IN point inside the file; previews start relative to it.</summary>
    public double InPointSeconds { get; init; }
}

/// <summary>
/// Resolves song credits for a playlist item. Each field is taken from the first source that has it:
/// 1. the item's own fields (Item Properties → EPG METADATA → SONG CREDITS),
/// 2. a <c>credits.csv</c> next to the media file (columns: File, Title, Film, Singer, Lyrics, Music, Label),
/// 3. the file name "Title - Film - Singer".
/// Runs when an item goes on air, never per frame. The CSV cache holds a few folders and re-reads a file only when it changes.
/// </summary>
public static class SongCredits
{
    public const string CsvFileName = "credits.csv";
    private const int MaxCachedFolders = 16;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (DateTime Stamp, Dictionary<string, string[]> Rows, string[] Header)> Folders = new(StringComparer.OrdinalIgnoreCase);

    public static SongInfo Resolve(PlaylistItem item)
    {
        var row = LookupCsv(item.FilePath);
        var parts = SplitName(item.FilePath, item.Title);
        // "Untitled" is PlaylistItem's placeholder for an empty title, not a real song name.
        var typed = SplitName(null, string.Equals(item.Title, "Untitled", StringComparison.Ordinal) ? null : item.Title);

        return new SongInfo(
            Title: First(Csv(row, "Title"), typed.Title, parts.Title),
            Film: First(item.Film, Csv(row, "Film"), Csv(row, "Album"), parts.Film),
            Singer: First(item.Singer, Csv(row, "Singer"), Csv(row, "Singers"), parts.Singer),
            Lyrics: First(item.Lyricist, Csv(row, "Lyrics"), Csv(row, "Lyricist")),
            Music: First(item.MusicDirector, Csv(row, "Music"), Csv(row, "Composer")),
            Label: First(item.MusicLabel, Csv(row, "Label"), Csv(row, "Copyright")),
            FilePath: item.FilePath ?? string.Empty,
            DurationSeconds: Math.Max(0, item.Duration.TotalSeconds))
        {
            InPointSeconds = Math.Max(0, item.InPoint.TotalSeconds)
        };
    }

    /// <summary>"Kitni Hasrat Hai Humein - Sainik - Kumar Sanu" → title, film, singer. Anything missing stays empty.</summary>
    public static (string Title, string Film, string Singer) SplitName(string? filePath, string? fallbackTitle)
    {
        var raw = string.IsNullOrWhiteSpace(filePath) || filePath.Contains("://", StringComparison.Ordinal)
            ? fallbackTitle ?? string.Empty
            : Path.GetFileNameWithoutExtension(filePath);
        var parts = raw.Replace('_', ' ').Split(" - ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return (parts.ElementAtOrDefault(0) ?? string.Empty, parts.ElementAtOrDefault(1) ?? string.Empty, parts.ElementAtOrDefault(2) ?? string.Empty);
    }

    private static string First(params string?[] values)
    {
        foreach (var value in values)
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        return string.Empty;
    }

    private static string Csv((string[] Header, string[] Values)? row, string column)
    {
        if (row is not { } r) return string.Empty;
        var index = Array.FindIndex(r.Header, h => h.Equals(column, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index < r.Values.Length ? r.Values[index] : string.Empty;
    }

    private static (string[] Header, string[] Values)? LookupCsv(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || filePath.Contains("://", StringComparison.Ordinal)) return null;
        try
        {
            var folder = Path.GetDirectoryName(filePath);
            if (string.IsNullOrEmpty(folder)) return null;
            var csv = Path.Combine(folder, CsvFileName);
            if (!File.Exists(csv)) return null;
            var stamp = File.GetLastWriteTimeUtc(csv);
            Dictionary<string, string[]> rows;
            string[] header;
            lock (Gate)
            {
                if (!Folders.TryGetValue(folder, out var cached) || cached.Stamp != stamp)
                {
                    if (Folders.Count >= MaxCachedFolders) Folders.Clear();
                    cached = (stamp, ReadCsv(csv, out var h), h);
                    Folders[folder] = cached;
                }
                rows = cached.Rows;
                header = cached.Header;
            }
            var file = Path.GetFileName(filePath);
            return rows.TryGetValue(file, out var values) || rows.TryGetValue(Path.GetFileNameWithoutExtension(file), out values)
                ? (header, values)
                : null;
        }
        catch (Exception ex)
        {
            RuntimeDiagnosticsService.WriteException("SONG_CREDITS_CSV_ERROR", ex);
            return null;
        }
    }

    /// <summary>Rows keyed by the File column (with or without extension). Excel's UTF-8 BOM and quoted cells are handled.</summary>
    internal static Dictionary<string, string[]> ReadCsv(string path, out string[] header)
    {
        var rows = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        header = [];
        var fileColumn = 0;
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cells = SplitCsvLine(line);
            if (header.Length == 0)
            {
                header = cells;
                fileColumn = Math.Max(0, Array.FindIndex(header, h => h.Equals("File", StringComparison.OrdinalIgnoreCase) || h.Equals("FileName", StringComparison.OrdinalIgnoreCase)));
                continue;
            }
            if (fileColumn >= cells.Length || string.IsNullOrWhiteSpace(cells[fileColumn])) continue;
            var key = cells[fileColumn].Trim();
            rows[key] = cells;
            rows.TryAdd(Path.GetFileNameWithoutExtension(key), cells);
        }
        return rows;
    }

    internal static string[] SplitCsvLine(string line)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { cells.Add(cell.ToString().Trim()); cell.Clear(); }
            else cell.Append(c);
        }
        cells.Add(cell.ToString().Trim());
        if (cells.Count > 0) cells[0] = cells[0].TrimStart('﻿');
        return [.. cells];
    }
}
