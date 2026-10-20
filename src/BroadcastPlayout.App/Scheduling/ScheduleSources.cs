using System.Text.Json;
using BroadcastPlayout.Ffmpeg;
using BroadcastPlayout.Models;
using BroadcastPlayout.Services;

namespace BroadcastPlayout.Scheduling;

/// <summary>One playable entry of a schedule item: a media file, or an item from a Kashtrix playlist.</summary>
public sealed record ScheduledSource(string Path, PlaylistItem? Template);

/// <summary>Turns schedule sources into playlist items the engine can play.</summary>
public static class ScheduleSources
{
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".mxf", ".ts", ".m2ts", ".mpeg", ".mpg", ".avi", ".mkv", ".webm", ".m4v", ".wmv"
    };
    private static readonly JsonSerializerOptions CloneOptions = new() { PropertyNameCaseInsensitive = true };

    public static bool IsMediaFile(string path) => MediaExtensions.Contains(Path.GetExtension(path));
    public static bool IsPlaylistFile(string path)
        => path.EndsWith(".kashtrix", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".kashtrixplaylist", StringComparison.OrdinalIgnoreCase);

    /// <summary>Folders expand to their media files (name order), playlists to their items. Missing paths are skipped.</summary>
    public static List<ScheduledSource> Expand(IEnumerable<string> sources)
    {
        var result = new List<ScheduledSource>();
        foreach (var source in sources)
        {
            if (string.IsNullOrWhiteSpace(source)) continue;
            try
            {
                if (Directory.Exists(source))
                {
                    result.AddRange(Directory.EnumerateFiles(source)
                        .Where(IsMediaFile)
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                        .Select(x => new ScheduledSource(x, null)));
                }
                else if (File.Exists(source) && IsPlaylistFile(source))
                {
                    result.AddRange(PlaylistDocumentService.Load(source).Select(x => new ScheduledSource(x.FilePath, x)));
                }
                else if (File.Exists(source))
                {
                    result.Add(new ScheduledSource(source, null));
                }
            }
            catch { /* an unreadable folder/playlist never stops the channel */ }
        }
        return result;
    }

    public static string CategoryFor(ScheduleKind kind) => kind switch
    {
        ScheduleKind.Filler => "Filler",
        ScheduleKind.Programme => "Program",
        ScheduleKind.Movie => "Movie",
        ScheduleKind.News => "News",
        ScheduleKind.SpotAd => "Ads",
        ScheduleKind.Live => "Live",
        _ => "Program"
    };

    /// <summary>The item's own category when set (e.g. a requested song is "Music"), else the one for its kind.</summary>
    public static string CategoryOf(ChannelScheduleItem item)
        => string.IsNullOrWhiteSpace(item.Category) ? CategoryFor(item.Kind) : item.Category.Trim();

    /// <summary>Builds a fresh item for one source. Media files are probed once, here.</summary>
    public static PlaylistItem? Build(ScheduledSource source, ChannelScheduleItem owner)
    {
        try
        {
            if (source.Template is not null)
            {
                var copy = Clone(source.Template);
                // An explicit category on the schedule entry (e.g. a requested song) wins over the playlist's.
                if (!string.IsNullOrWhiteSpace(owner.Category)) copy.Category = owner.Category.Trim();
                else if (string.IsNullOrWhiteSpace(copy.Category)) copy.Category = CategoryFor(owner.Kind);
                copy.Request ??= owner.Request;
                return copy;
            }
            var info = MediaProbe.Read(source.Path);
            return new PlaylistItem
            {
                Title = Path.GetFileNameWithoutExtension(source.Path),
                FilePath = source.Path,
                SourceKind = "File",
                EventType = owner.Kind == ScheduleKind.SpotAd ? "COMMERCIAL" : owner.Kind.ToString().ToUpperInvariant(),
                Category = CategoryOf(owner),
                SourceDuration = info.Duration,
                InPoint = TimeSpan.Zero,
                OutPoint = info.Duration,
                SourceFrameRate = info.FramesPerSecond,
                Codec = info.VideoCodec,
                VideoFormat = $"{info.Width}x{info.Height} {info.FramesPerSecond:0.###}p",
                Request = owner.Request,
                Status = "Ready"
            };
        }
        catch { return null; }
    }

    /// <summary>A live stream (RTMP/SRT/UDP/HLS/any URL) that plays for <paramref name="length"/>.</summary>
    public static PlaylistItem BuildLive(ChannelScheduleItem owner, TimeSpan length)
    {
        var url = owner.Sources.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? string.Empty;
        var item = new PlaylistItem
        {
            Title = string.IsNullOrWhiteSpace(owner.Name) ? "LIVE" : owner.Name,
            FilePath = url,
            SourceKind = "URL",
            EventType = "LIVE",
            Category = "Live",
            IsLiveSource = true,
            Request = owner.Request,
            Status = "Ready"
        };
        item.SourceDuration = length;
        item.InPoint = TimeSpan.Zero;
        item.OutPoint = length;
        return item;
    }

    /// <summary>
    /// The same item starting at <paramref name="position"/> (resume after an interruption), or null
    /// when it cannot be resumed (live, multipart, or nearly finished).
    /// </summary>
    public static PlaylistItem? ResumeAt(PlaylistItem item, TimeSpan position)
    {
        if (item.IsLiveSource || item.HasParts) return null;
        if (position <= TimeSpan.FromSeconds(1)) return Clone(item);
        var inPoint = item.InPoint + position;
        if (inPoint >= item.OutPoint - TimeSpan.FromSeconds(1)) return null;
        var copy = Clone(item);
        copy.InPoint = inPoint;
        return copy;
    }

    /// <summary>Independent copy with its own identity (a deep copy, like the playlist database stores it).</summary>
    public static PlaylistItem Clone(PlaylistItem item)
    {
        var copy = JsonSerializer.Deserialize<PlaylistItem>(JsonSerializer.Serialize(item, CloneOptions), CloneOptions) ?? new PlaylistItem();
        copy.Id = Guid.NewGuid();
        copy.Status = "Ready";
        return copy;
    }
}
