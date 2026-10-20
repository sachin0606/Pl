using BroadcastPlayout.Models;

namespace BroadcastPlayout.Services;

public static class DemoDataFactory
{
    public static string ResolveDemoAsset(string relativeSubPath)
    {
        var normalized = relativeSubPath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        
        var cand = Path.Combine(AppContext.BaseDirectory, normalized);
        if (File.Exists(cand)) return cand;

        cand = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, normalized);
        if (File.Exists(cand)) return cand;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 7 && dir != null; i++)
        {
            var p1 = Path.Combine(dir.FullName, normalized);
            if (File.Exists(p1)) return p1;

            var p2 = Path.Combine(dir.FullName, "src", "BroadcastPlayout.App", normalized);
            if (File.Exists(p2)) return p2;

            var p3 = Path.Combine(dir.FullName, "demos", normalized.StartsWith("demos" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? normalized.Substring(6) : normalized);
            if (File.Exists(p3)) return p3;

            dir = dir.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, normalized);
    }

    public static IReadOnlyList<PlaylistItem> CreatePlaylist()
    {
        var demoVideo = ResolveDemoAsset("demos/gfx/video/kashtrix-video-layer.mp4");
        var demoThumb = ResolveDemoAsset("demos/ui/demo-city.png");
        var start = DateTime.Today.AddHours(22).AddMinutes(23).AddSeconds(45);
        var rows = new[]
        {
            ("MUSIC VIDEO", "Music",         "Latest Haryanvi Music Video",  225.0, "Completed", "/Media/Music/Haryanvi_01.mp4"),
            ("MUSIC VIDEO", "Punjabi",       "Punjabi Music Hit",            252.0, "Playing",   "/Media/Music/Punjabi_01.mp4"),
            ("MOVIE",       "Movie",         "Feature Movie",               8320.0, "Waiting",   "/Media/Movies/Movie_01.mkv"),
            ("PROMO",       "Channel",       "Channel Promo",                 30.0, "Waiting",   "/Media/Promos/Channel_Promo.mp4"),
            ("COMMERCIAL",  "Ads",           "30 Sec Commercial",             30.0, "Waiting",   "/Media/Ads/Commercial_01.mp4"),
            ("JINGLE",      "Station ID",    "Station ID",                    10.0, "Waiting",   "/Media/Jingles/ID_01.wav"),
            ("SCTE-35",     "Marker",        "SCTE-35 CUE OUT",                1.0, "Waiting",   "Cue Out"),
            ("BREAK",       "Break",         "Program Break",                  1.0, "Waiting",   "Break"),
            ("SCTE-35",     "Marker",        "SCTE-35 CUE IN",                 1.0, "Waiting",   "Cue In"),
            ("MUSIC VIDEO", "Bollywood",     "Bollywood Hit Song",           245.0, "Waiting",   "/Media/Music/Bollywood_01.mp4"),
            ("GRAPHICS",    "Channel",       "Coming Up Next",                35.0, "Waiting",   "/Media/Graphics/Coming_Next.mp4"),
            ("MUSIC VIDEO", "International", "English Music Video",          208.0, "Waiting",   "/Media/Music/English_01.mp4")
        };

        var result = new List<PlaylistItem>();
        var seq = 1;
        var cursor = start;
        foreach (var row in rows)
        {
            var displayDuration = TimeSpan.FromSeconds(row.Item4);
            var sourceDuration = TimeSpan.FromSeconds(6);
            var live = row.Item1 == "LIVE";
            result.Add(new PlaylistItem
            {
                Sequence = seq++,
                StartTimeText = cursor.ToString("HH:mm:ss:ff"),
                EventType = row.Item1,
                Category = row.Item2,
                Title = row.Item3,
                Notes = row.Item5,
                Location = row.Item6,
                DisplayDurationText = string.Empty,
                ThumbnailPath = demoThumb,
                FilePath = demoVideo,
                SourceKind = "File",
                SourceDuration = sourceDuration,
                InPoint = TimeSpan.Zero,
                OutPoint = sourceDuration,
                SourceFrameRate = 50,
                Codec = live ? "Live simulation / Demo" : "XDCAM HD 422 / Demo",
                VideoFormat = "1920x1080i50",
                Status = row.Item5
            });
            cursor += sourceDuration;
        }

        var feature = result.FirstOrDefault(x => x.Title == "Feature Movie");
        if (feature != null) feature.SourceDuration = TimeSpan.FromSeconds(8320);
        feature.Parts = new System.Collections.ObjectModel.ObservableCollection<MediaPart>
        {
            new() { Order = 1, Name = "Opening", InPoint = TimeSpan.FromSeconds(0.2), OutPoint = TimeSpan.FromSeconds(1.4), Enabled = true },
            new() { Order = 2, Name = "Interview", InPoint = TimeSpan.FromSeconds(2.0), OutPoint = TimeSpan.FromSeconds(3.8), Enabled = true },
            new() { Order = 3, Name = "Closing", InPoint = TimeSpan.FromSeconds(4.4), OutPoint = TimeSpan.FromSeconds(5.8), Enabled = true }
        };
        return result;
    }

    public static IReadOnlyList<ScheduleEntry> CreateSchedules()
    {
        var day = DateTime.Today;
        return new[]
        {
            NewSchedule("Morning News", day.AddHours(6), "Program", "Morning Headlines", TimeSpan.FromHours(1), "Daily"),
            NewSchedule("Weather Update", day.AddHours(7).AddMinutes(15), "Media", "Weather & Traffic", TimeSpan.FromMinutes(15), "Daily"),
            NewSchedule("Business Desk", day.AddHours(9), "Program", "Business Update", TimeSpan.FromMinutes(30), "Weekdays"),
            NewSchedule("Midday Bulletin", day.AddHours(12), "Program", "National News Package", TimeSpan.FromHours(1), "Daily"),
            NewSchedule("Prime Bulletin", day.AddHours(20), "Program", "Prime Bulletin Promo", TimeSpan.FromHours(1), "Daily"),
            NewSchedule("Music Prime", day.AddHours(21).AddMinutes(30), "Program", "Music Now Playing / Coming Up Auto CG", TimeSpan.FromHours(1), "Daily"),
            NewSchedule("Overnight Filler", day.AddHours(23), "Filler", "Configured filler folder", TimeSpan.FromHours(7), "Daily")
        };
    }

    public static IReadOnlyList<ChannelDefinition> CreateChannels()
    {
        return new[]
        {
            NewChannel("KTX-CH-001", "SPARKLE TV PLAYOUT", "1080p50", "Music & Movie Entertainment Service"),
            NewChannel("KTX-CH-002", "Kashtrix News +1", "1080p50", "Time-shift service"),
            NewChannel("KTX-CH-003", "Kashtrix Business", "1080p50", "Business service"),
            NewChannel("KTX-CH-004", "Kashtrix Sports", "1080p50", "Sports service"),
            NewChannel("KTX-CH-005", "Kashtrix Digital", "720p50", "Digital simulcast"),
            NewChannel("KTX-CH-006", "Kashtrix UHD", "2160p50", "UHD service"),
            NewChannel("KTX-MUSIC-01", "Kashtrix Music HD", "1080p50", "Music channel demo · filename metadata Auto CG")
        };
    }

    private static ScheduleEntry NewSchedule(string name, DateTime start, string type, string source, TimeSpan duration, string repeat) => new()
    {
        Name = name,
        StartAt = start,
        EventType = type,
        SourceLabel = source,
        Duration = duration,
        Repeat = repeat,
        StartMode = "Hard Start",
        Category = type,
        Enabled = true
    };

    private static ChannelDefinition NewChannel(string id, string name, string format, string notes) => new()
    {
        ChannelId = id,
        ChannelName = name,
        Format = format,
        Notes = notes,
        Enabled = true
    };
}
