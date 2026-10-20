using System;
using System.Linq;
using BroadcastPlayout.Models;

namespace BroadcastPlayout.Services;

public static class AutoCgFactory
{
    public static CgProject CreateNowPlaying(PlaylistItem current, CgProject? template = null)
    {
        var resolved = template ?? ResolveDefaultTemplate(current.Category, comingUp: false);
        return PopulateTemplate(resolved, current, "NOW PLAYING");
    }

    public static CgProject CreateComingUp(PlaylistItem next, CgProject? template = null, PlaylistItem? current = null)
    {
        var resolved = template ?? ResolveDefaultTemplate(next.Category, comingUp: true);
        return PopulateTemplate(resolved, next, "COMING UP", current);
    }

    public static CgProject CreateBackIn(double remainingSeconds, PlaylistItem? nextItem = null, string? templateName = null)
    {
        var all = CgDemoFactory.CreateDefaults();
        var targetName = string.IsNullOrWhiteSpace(templateName) ? "Commercial · Back In Countdown Lower Third" : templateName.Trim();
        var template = all.FirstOrDefault(x => string.Equals(x.Name, targetName, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(x => x.Name.Contains("Back In", StringComparison.OrdinalIgnoreCase))
            ?? CgUniqueDemoFactory.CreatePlayoutTemplate(targetName)
            ?? new CgProject { Name = targetName, DurationSeconds = Math.Max(10, remainingSeconds), FrameRate = 50, Width = 1920, Height = 1080 };

        var dummy = new PlaylistItem
        {
            Title = nextItem != null ? nextItem.Title : "FEATURE PROGRAM",
            Category = "Commercial",
            OriginalDescription = nextItem != null ? $"UP NEXT: {nextItem.Title}" : "BACK TO BROADCAST"
        };

        var proj = PopulateTemplate(template, dummy, "BACK IN", currentItem: nextItem, remainingSeconds: remainingSeconds);
        proj.DurationSeconds = Math.Max(5, remainingSeconds + 2);
        return proj;
    }

    private static CgProject ResolveDefaultTemplate(string category, bool comingUp)
    {
        var isMusic = category.Contains("Music", StringComparison.OrdinalIgnoreCase);
        var isNews = category.Contains("News", StringComparison.OrdinalIgnoreCase);
        var isComm = category.Contains("Commercial", StringComparison.OrdinalIgnoreCase) || category.Contains("Ad", StringComparison.OrdinalIgnoreCase);

        var targetName = isMusic
            ? (comingUp ? "Music · Coming Up Lower Third" : "Music · Now Playing Lower Third")
            : isNews
            ? (comingUp ? "News · Coming Up Lower Third" : "News · Now Playing Lower Third")
            : isComm
            ? "Commercial · Back In Countdown Lower Third"
            : (comingUp ? "Program · Coming Up Lower Third" : "Program · Now Playing Lower Third");

        var all = CgDemoFactory.CreateDefaults();
        var found = all.FirstOrDefault(x => string.Equals(x.Name, targetName, StringComparison.OrdinalIgnoreCase));
        if (found != null) return found;

        return all.FirstOrDefault(x => x.Name.Contains(comingUp ? "Coming" : "Now", StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault()
            ?? new CgProject { Name = targetName, DurationSeconds = 12, FrameRate = 50, Width = 1920, Height = 1080 };
    }

    private static CgProject PopulateTemplate(CgProject template, PlaylistItem item, string tag, PlaylistItem? currentItem = null, double remainingSeconds = -1)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(template);
        var copy = System.Text.Json.JsonSerializer.Deserialize<CgProject>(json) ?? new CgProject();
        copy.Id = Guid.NewGuid();
        copy.Name = $"AUTO · {tag} · {template.Name}";
        copy.OnAir = true;

        var parsed = MediaNameParser.Parse(item.FilePath, item.Title);
        var isMusic = item.Category.Contains("Music", StringComparison.OrdinalIgnoreCase);
        var title = isMusic && !string.IsNullOrWhiteSpace(parsed.Title) ? parsed.Title : (!string.IsNullOrWhiteSpace(parsed.Title) ? parsed.Title : item.Title);
        var artist = isMusic
            ? (!string.IsNullOrWhiteSpace(parsed.Artist) ? parsed.Artist : (!string.IsNullOrWhiteSpace(item.OriginalDescription) ? item.OriginalDescription : "MUSIC"))
            : (!string.IsNullOrWhiteSpace(parsed.Artist) ? parsed.Artist : (!string.IsNullOrWhiteSpace(item.OriginalDescription) ? item.OriginalDescription : item.Category));
        var extra = parsed.Extra;
        var category = item.Category;
        var desc = !string.IsNullOrWhiteSpace(item.OriginalDescription) ? item.OriginalDescription : (!string.IsNullOrWhiteSpace(item.Notes) ? item.Notes : item.Category);

        var curParsed = currentItem != null ? MediaNameParser.Parse(currentItem.FilePath, currentItem.Title) : null;
        var curTitle = currentItem != null ? (!string.IsNullOrWhiteSpace(curParsed?.Title) ? curParsed.Title : currentItem.Title) : string.Empty;
        var curArtist = currentItem != null ? (!string.IsNullOrWhiteSpace(curParsed?.Artist) ? curParsed.Artist : currentItem.Category) : string.Empty;

        var backInStr = remainingSeconds >= 0 ? CgPlaybackRuntime.FormatTimer(remainingSeconds, "mm:ss") : "01:30";

        foreach (var layer in copy.Layers)
        {
            if (layer.IsTimerLayer || string.Equals(layer.Type, "Timer", StringComparison.OrdinalIgnoreCase) || string.Equals(layer.Type, "Countdown", StringComparison.OrdinalIgnoreCase))
            {
                if (remainingSeconds >= 0)
                {
                    layer.TimerStartSeconds = remainingSeconds;
                    layer.TimerMode = "CommercialBackIn";
                }
            }

            if (!layer.Type.Equals("Text", StringComparison.OrdinalIgnoreCase) &&
                !layer.Type.Equals("Ticker", StringComparison.OrdinalIgnoreCase) &&
                !layer.Type.Equals("Timer", StringComparison.OrdinalIgnoreCase) &&
                !layer.Type.Equals("Countdown", StringComparison.OrdinalIgnoreCase) &&
                !layer.Type.Equals("BackIn", StringComparison.OrdinalIgnoreCase)) continue;

            var text = layer.Text ?? string.Empty;
            if (text.Contains('{'))
            {
                text = text.Replace("{Title}", title, StringComparison.OrdinalIgnoreCase);
                text = text.Replace("{SongTitle}", title, StringComparison.OrdinalIgnoreCase);
                text = text.Replace("{MusicTitle}", title, StringComparison.OrdinalIgnoreCase);
                text = text.Replace("{ProgramName}", title, StringComparison.OrdinalIgnoreCase);
                text = text.Replace("{ProgramTitle}", title, StringComparison.OrdinalIgnoreCase);
                text = text.Replace("{Artist}", artist, StringComparison.OrdinalIgnoreCase);
                text = text.Replace("{Singer}", artist, StringComparison.OrdinalIgnoreCase);
                text = text.Replace("{Tag}", tag, StringComparison.OrdinalIgnoreCase);
                text = text.Replace("{Category}", category, StringComparison.OrdinalIgnoreCase);
                text = text.Replace("{Extra}", extra, StringComparison.OrdinalIgnoreCase);
                text = text.Replace("{Description}", desc, StringComparison.OrdinalIgnoreCase);
                text = text.Replace("{BackIn}", backInStr, StringComparison.OrdinalIgnoreCase);
                text = text.Replace("{CommercialRemaining}", backInStr, StringComparison.OrdinalIgnoreCase);
                text = text.Replace("{BreakRemaining}", backInStr, StringComparison.OrdinalIgnoreCase);
                text = text.Replace("{Timer}", backInStr, StringComparison.OrdinalIgnoreCase);
                text = text.Replace("{NextTitle}", title, StringComparison.OrdinalIgnoreCase);
                text = text.Replace("{NextArtist}", artist, StringComparison.OrdinalIgnoreCase);
                text = text.Replace("{CurrentTitle}", curTitle, StringComparison.OrdinalIgnoreCase);
                text = text.Replace("{CurrentArtist}", curArtist, StringComparison.OrdinalIgnoreCase);
                layer.Text = text;
            }
            else
            {
                var key = layer.Name.ToUpperInvariant();
                if (key.Contains("BACKIN") || key.Contains("COUNTDOWN") || key.Contains("TIMER"))
                {
                    if (string.IsNullOrWhiteSpace(layer.Text) || layer.Text.Contains(':'))
                        layer.Text = $"BACK IN {backInStr}";
                }
                else if (key.Contains("TAG") || key.Contains("LABEL")) layer.Text = tag;
                else if (key.Contains("ARTIST") || key.Contains("SECOND") || key.Contains("SUB")) layer.Text = artist;
                else if (key.Contains("TITLE") || key.Contains("HEADLINE") || key.Contains("NAME")) layer.Text = title;
                else if (key.Contains("DESC") || key.Contains("CAT")) layer.Text = desc;
            }
        }
        return copy;
    }
}
