namespace BroadcastPlayout.Models;

/// <summary>
/// Music channel Now/Next package: song info + progress bar for the whole song, and the NEXT / LATER
/// video box at set moments. The video box itself is switched by the existing
/// "Next Item Video" option (ProfessionalBroadcastSettings.NextItemVideo).
/// </summary>
public sealed class MusicPackageSettings
{
    public const string DefaultInfoTemplate = "Music · Now Playing Package";
    public const string DefaultBoxTemplate = "Music · Coming Up Video Box";

    /// <summary>Off by default, so existing channels look exactly as before.</summary>
    public bool Enabled { get; set; }
    /// <summary>Comma-separated playlist categories that get the package.</summary>
    public string Categories { get; set; } = "Music";
    public string InfoTemplate { get; set; } = DefaultInfoTemplate;
    public string BoxTemplate { get; set; } = DefaultBoxTemplate;
    /// <summary>Program CG layers; above tickers and bugs on the usual 10..30.</summary>
    public int InfoLayer { get; set; } = 40;
    public int BoxLayer { get; set; } = 41;

    /// <summary>NEXT box appears this many seconds into the song.</summary>
    public double FirstNextAtSeconds { get; set; } = 20;
    public double NextHoldSeconds { get; set; } = 7;
    /// <summary>The same box then shows LATER (the song after next) for this long. 0 skips LATER.</summary>
    public double LaterHoldSeconds { get; set; } = 7;
    /// <summary>NEXT shows again this many seconds before the song ends. 0 turns it off.</summary>
    public double EndNextBeforeSeconds { get; set; } = 40;
    /// <summary>Preview video starts this far into the upcoming song (percent), past the intro.</summary>
    public double PreviewStartPercent { get; set; } = 30;

    public bool AppliesTo(string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return false;
        foreach (var name in Categories.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            if (category.Contains(name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
