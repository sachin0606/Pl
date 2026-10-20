namespace BroadcastPlayout.Models;

/// <summary>
/// Per-channel XMLTV/EPG export policy.  Names intentionally mirror common automation
/// systems but the generated document remains ordinary XMLTV-compatible XML.
/// </summary>
public sealed class EpgOutputSettings
{
    public string ProviderInfoName { get; set; } = "Kashtrix Playout";
    public string ProviderInfoUrl { get; set; } = "";
    public string TimeFormat { get; set; } = "XMLTV UTC Offset"; // XMLTV UTC Offset, UTC Z, Local Compact, Human Readable
    public string ProgrammeKeyOrder { get; set; } = "Time - Channel";
    public bool DisplayLanguageAttribute { get; set; }
    public string Language { get; set; } = "en";
    public bool SkipTagIfValueEmpty { get; set; } = true;
    public bool IgnoreTimeForNoEpgFiles { get; set; } = true;
    public bool RoundEpgTimes { get; set; }
    public bool RemoveEndTime { get; set; }
    public string TitleSource { get; set; } = "Item Name"; // Item Name, Original Title, Now/Next Promo Name
    public string DescriptionSource { get; set; } = "Item Description"; // Item Description, Original Description, Notes
    public int NowNextEveryMinutes { get; set; } = 1;
    public int NowNextRepeatsBeforePromo { get; set; } = 2;
    public int ProgramTitleStartSeconds { get; set; }
    public int ProgramTitleEndSeconds { get; set; }
    public bool ProgramTitleHoldForItem { get; set; } = true;
    public int MusicTitleShowSecondsAfterStart { get; set; } = 5;
    public int MusicTitleShowSecondsBeforeEnd { get; set; } = 5;
    public string DefaultAutoGraphicsTemplate { get; set; } = "NONE";
    public string DefaultMusicTemplate { get; set; } = "Music · Now Playing";
    public string DefaultProgramTemplate { get; set; } = "Lower Third · Anchor Sweep";
    public string DefaultWeatherTemplate { get; set; } = "NONE";
    public string NowNextTemplate { get; set; } = "Promo · Next Program";
    public string PromoTemplate { get; set; } = "Promo · Tonight";
    public List<EpgColumnSetting> Columns { get; set; } = CreateDefaultColumns();

    public static List<EpgColumnSetting> CreateDefaultColumns() =>
    [
        new("Original Title", "originalni_naziv", true, true, ""),
        new("Original Description", "opis", true, true, ""),
        new("Episode Title", "original_episode_title", false, true, "en"),
        new("Episode Description", "episode_description", false, false, ""),
        new("Episode Season", "season_number", false, false, ""),
        new("Episode No", "episode_number", false, false, ""),
        new("Genre", "genre", false, false, ""),
        new("Production Year", "year", false, false, ""),
        new("Country", "country", false, false, ""),
        new("Director", "director", false, false, ""),
        new("Lead Actors", "lead_actors", false, false, ""),
        new("IMDB", "imdb_link", false, false, ""),
        new("EPG Url Pic", "image_url", false, false, ""),
        new("EPG Picture URL Large", "image_url_large", false, false, ""),
        new("EPG Picture URL Small", "image_url_small", false, false, ""),
        new("PA (Parental Advisory)", "pa", false, false, ""),
        new("Premiere", "premiere", false, false, ""),
        new("Aspect Ratio", "aspect_ratio", false, false, ""),
        new("Category", "category", false, false, "")
    ];

    public void Normalize()
    {
        ProviderInfoName = string.IsNullOrWhiteSpace(ProviderInfoName) ? "Kashtrix Playout" : ProviderInfoName.Trim();
        Language = string.IsNullOrWhiteSpace(Language) ? "en" : Language.Trim();
        NowNextEveryMinutes = Math.Clamp(NowNextEveryMinutes, 1, 120);
        NowNextRepeatsBeforePromo = Math.Clamp(NowNextRepeatsBeforePromo, 0, 99);
        ProgramTitleStartSeconds = Math.Clamp(ProgramTitleStartSeconds, -1, 86400);
        ProgramTitleEndSeconds = Math.Clamp(ProgramTitleEndSeconds, -1, 86400);
        MusicTitleShowSecondsAfterStart = Math.Clamp(MusicTitleShowSecondsAfterStart, -1, 86400);
        MusicTitleShowSecondsBeforeEnd = Math.Clamp(MusicTitleShowSecondsBeforeEnd, -1, 86400);
        Columns ??= CreateDefaultColumns();
        if (Columns.Count == 0) Columns = CreateDefaultColumns();
    }
}

public sealed class EpgColumnSetting
{
    public EpgColumnSetting() { }
    public EpgColumnSetting(string name, string tag, bool show, bool showLanguage, string language)
    {
        Name = name; Tag = tag; Show = show; ShowLanguage = showLanguage; Language = language;
    }
    public string Name { get; set; } = "Column";
    public bool Show { get; set; }
    public string Tag { get; set; } = "field";
    public bool ShowLanguage { get; set; }
    public string Language { get; set; } = "";
}
