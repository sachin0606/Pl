namespace BroadcastPlayout.Models;

/// <summary>When the NEXT / LATER box appears during an item.</summary>
public enum NowNextBoxMode
{
    Off,
    /// <summary>A few seconds after the item starts and again a few seconds before it ends.</summary>
    StartAndEnd,
    /// <summary>On screen for the whole item.</summary>
    Continuous,
    /// <summary>Every N minutes, N times (0 = until the item ends).</summary>
    Repeat
}

/// <summary>How the start of the upcoming airing is written in the box.</summary>
public enum NowNextTimeDisplay
{
    None,
    /// <summary>"20:00"</summary>
    Time,
    /// <summary>"IN 5 MIN"</summary>
    Countdown,
    /// <summary>"20:00 · IN 5 MIN"</summary>
    Both,
    /// <summary>The time, switching to a countdown in the last minutes.</summary>
    Auto
}

/// <summary>Now/Next behaviour for one type of item (songs, filler, movies ...), matched by playlist category.</summary>
public sealed class NowNextProfile
{
    public string Name { get; set; } = "Songs";
    /// <summary>Comma-separated category names; an item matches when its category contains one of them.</summary>
    public string Categories { get; set; } = "Music";
    /// <summary>Title, credits and progress bar while the item plays.</summary>
    public bool ShowInfo { get; set; } = true;
    public NowNextBoxMode BoxMode { get; set; } = NowNextBoxMode.StartAndEnd;
    /// <summary>First box this many seconds after the item starts (all modes). 0 with StartAndEnd skips the start box.</summary>
    public double StartAtSeconds { get; set; } = 20;
    /// <summary>How long NEXT stays on screen each time.</summary>
    public double HoldSeconds { get; set; } = 7;
    /// <summary>After NEXT, the same box shows LATER (the airing after next).</summary>
    public bool ShowLater { get; set; } = true;
    public double LaterHoldSeconds { get; set; } = 7;
    /// <summary>StartAndEnd: the box shows again this many seconds before the end. 0 = no end box.</summary>
    public double EndLeadSeconds { get; set; } = 40;
    /// <summary>Repeat: minutes between boxes.</summary>
    public double RepeatEveryMinutes { get; set; } = 10;
    /// <summary>Repeat: how many times; 0 = until the item ends.</summary>
    public int RepeatTimes { get; set; }
    public NowNextTimeDisplay TimeDisplay { get; set; } = NowNextTimeDisplay.Auto;
    /// <summary>Auto: switch from the time to a countdown inside this many minutes.</summary>
    public double CountdownMinutes { get; set; } = 10;

    public bool Matches(string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return false;
        foreach (var name in Categories.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            if (category.Contains(name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public NowNextProfile Clone() => (NowNextProfile)MemberwiseClone();
}

/// <summary>
/// Channel-wide Now/Next graphics: what is on air (title, credits, progress, viewer request) and what the
/// schedule or playlist plays next, shown in the NEXT / LATER box. One profile per type of item.
/// The video preview inside the box follows the existing "Next Item Video" option.
/// </summary>
public sealed class NowNextSettings
{
    public const string DefaultInfoTemplate = "Now Playing · Info Package";
    public const string DefaultBoxTemplate = "Coming Up · Video Box";

    /// <summary>Off by default, so existing channels look exactly as before.</summary>
    public bool Enabled { get; set; }
    public string InfoTemplate { get; set; } = DefaultInfoTemplate;
    public string BoxTemplate { get; set; } = DefaultBoxTemplate;
    /// <summary>Program CG layers; above tickers and bugs on the usual 10..30. The info sits above the box so a two-line title is never covered.</summary>
    public int InfoLayer { get; set; } = 41;
    public int BoxLayer { get; set; } = 40;
    /// <summary>Preview video starts this far into the upcoming airing (percent), past the intro.</summary>
    public double PreviewStartPercent { get; set; } = 30;

    // Viewer requests ("on demand")
    public bool ShowRequests { get; set; } = true;
    public string RequestPrefix { get; set; } = "Requested by";
    public bool RequestShowName { get; set; } = true;
    public bool RequestShowCity { get; set; } = true;
    public bool RequestShowMessage { get; set; }

    public List<NowNextProfile> Profiles { get; set; } = DefaultProfiles();

    /// <summary>The first profile whose categories match. Ads and promos never get Now/Next graphics.</summary>
    public NowNextProfile? ProfileFor(string? category)
    {
        if (string.IsNullOrWhiteSpace(category) || IsAdvert(category)) return null;
        return Profiles.FirstOrDefault(p => p.Matches(category));
    }

    public static bool IsAdvert(string category)
        => category.Contains("Commercial", StringComparison.OrdinalIgnoreCase) || category.Equals("Ads", StringComparison.OrdinalIgnoreCase)
           || category.Contains("Advert", StringComparison.OrdinalIgnoreCase) || category.Contains("Promo", StringComparison.OrdinalIgnoreCase);

    public static List<NowNextProfile> DefaultProfiles() =>
    [
        new() { Name = "Songs", Categories = "Music, Song", ShowInfo = true, BoxMode = NowNextBoxMode.StartAndEnd, StartAtSeconds = 20, HoldSeconds = 7, ShowLater = true, LaterHoldSeconds = 7, EndLeadSeconds = 40, TimeDisplay = NowNextTimeDisplay.None },
        new() { Name = "Filler", Categories = "Filler", ShowInfo = true, BoxMode = NowNextBoxMode.StartAndEnd, StartAtSeconds = 15, HoldSeconds = 7, ShowLater = true, LaterHoldSeconds = 7, EndLeadSeconds = 30, TimeDisplay = NowNextTimeDisplay.Auto },
        new() { Name = "Movies", Categories = "Movie, Film", ShowInfo = true, BoxMode = NowNextBoxMode.Repeat, StartAtSeconds = 30, HoldSeconds = 8, ShowLater = false, RepeatEveryMinutes = 15, RepeatTimes = 0, TimeDisplay = NowNextTimeDisplay.Auto },
        new() { Name = "Programmes", Categories = "Program, Show, Serial", ShowInfo = true, BoxMode = NowNextBoxMode.StartAndEnd, StartAtSeconds = 20, HoldSeconds = 8, ShowLater = true, LaterHoldSeconds = 6, EndLeadSeconds = 60, TimeDisplay = NowNextTimeDisplay.Auto },
        new() { Name = "News", Categories = "News", ShowInfo = false, BoxMode = NowNextBoxMode.StartAndEnd, StartAtSeconds = 0, HoldSeconds = 8, ShowLater = false, EndLeadSeconds = 45, TimeDisplay = NowNextTimeDisplay.Auto },
        new() { Name = "Live", Categories = "Live", ShowInfo = false, BoxMode = NowNextBoxMode.Off, TimeDisplay = NowNextTimeDisplay.Auto }
    ];
}
