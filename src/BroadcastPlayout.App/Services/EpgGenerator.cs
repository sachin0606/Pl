using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using BroadcastPlayout.Models;

namespace BroadcastPlayout.Services;

public static class EpgGenerator
{
    public static void SaveXmlTv(string path, IEnumerable<PlaylistItem> items, string channelId, string channelName, DateTime? startAt = null)
    {
        var settings = SettingsStore.Load().Epg ?? new EpgOutputSettings();
        settings.Normalize();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, BuildXml(items, channelId, channelName, settings, startAt), new UTF8Encoding(false));
        AuditLogService.Write("EPG_EXPORT_XML", channelName, path);
    }

    public static string BuildPreview(IEnumerable<PlaylistItem> items, string channelId, string channelName, EpgOutputSettings? settings = null)
    {
        settings ??= SettingsStore.Load().Epg ?? new EpgOutputSettings();
        settings.Normalize();
        return BuildXml(items.Take(3), channelId, channelName, settings, DateTime.Now);
    }

    public static string BuildXml(IEnumerable<PlaylistItem> items, string channelId, string channelName, EpgOutputSettings settings, DateTime? startAt = null)
    {
        settings.Normalize();
        var snapshot = items.Where(x => !x.IsControlEvent).ToArray();
        var cursor = startAt ?? ResolveStart(snapshot.FirstOrDefault());
        var channelKey = string.IsNullOrWhiteSpace(channelId) ? "kashtrix.playout" : channelId.Trim();
        var tv = new XElement("tv", new XAttribute("generator-info-name", settings.ProviderInfoName));
        if (!string.IsNullOrWhiteSpace(settings.ProviderInfoUrl)) tv.Add(new XAttribute("generator-info-url", settings.ProviderInfoUrl));
        var channel = new XElement("channel", new XAttribute("id", channelKey));
        var display = new XElement("display-name", string.IsNullOrWhiteSpace(channelName) ? channelKey : channelName);
        if (settings.DisplayLanguageAttribute) display.SetAttributeValue("lang", settings.Language);
        channel.Add(display);
        tv.Add(channel);

        foreach (var item in snapshot)
        {
            var duration = item.Duration > TimeSpan.Zero ? item.Duration : TimeSpan.FromSeconds(1);
            var stop = cursor + duration;
            if (settings.RoundEpgTimes)
            {
                cursor = RoundSecond(cursor);
                stop = RoundSecond(stop);
            }
            var programme = new XElement("programme",
                new XAttribute("start", FormatEpgTime(cursor, settings.TimeFormat)),
                new XAttribute("channel", channelKey));
            if (!settings.RemoveEndTime) programme.Add(new XAttribute("stop", FormatEpgTime(stop, settings.TimeFormat)));

            AddValue(programme, "title", ResolveTitle(item, settings), settings.DisplayLanguageAttribute ? settings.Language : null, settings.SkipTagIfValueEmpty);
            AddValue(programme, "desc", ResolveDescription(item, settings), settings.DisplayLanguageAttribute ? settings.Language : null, settings.SkipTagIfValueEmpty);
            if (!string.IsNullOrWhiteSpace(item.Category)) AddValue(programme, "category", item.Category, null, true);

            foreach (var column in settings.Columns.Where(x => x.Show))
            {
                var value = ResolveColumn(item, column.Name);
                var tag = SanitizeElementName(column.Tag);
                if (tag is "title" or "desc" or "category") continue;
                AddValue(programme, tag, value, column.ShowLanguage ? (string.IsNullOrWhiteSpace(column.Language) ? settings.Language : column.Language) : null, settings.SkipTagIfValueEmpty);
            }
            tv.Add(programme);
            cursor = stop;
        }

        var doc = new XDocument(new XDeclaration("1.0", "utf-8", "yes"), tv);
        using var sw = new Utf8StringWriter();
        doc.Save(sw, SaveOptions.None);
        return sw.ToString();
    }

    public static void SaveJson(string path, IEnumerable<PlaylistItem> items, string channelId, string channelName, DateTime? startAt = null)
    {
        var settings = SettingsStore.Load().Epg ?? new EpgOutputSettings(); settings.Normalize();
        var cursor = startAt ?? DateTime.Now;
        var rows = new List<object>();
        foreach (var item in items.Where(x => !x.IsControlEvent))
        {
            var duration = item.Duration > TimeSpan.Zero ? item.Duration : TimeSpan.FromSeconds(1);
            rows.Add(new
            {
                channelId, channelName, item.Sequence, item.Title, item.OriginalTitle, item.OriginalDescription,
                item.EpisodeTitle, item.EpisodeDescription, item.EpisodeSeason, item.EpisodeNumber,
                item.Genre, item.ProductionYear, item.Country, item.Director, item.LeadActors, item.ImdbUrl,
                item.ImageUrl, item.ParentalAdvisory, item.Premiere, item.Category, item.EventType,
                Start = cursor, Stop = cursor + duration, DurationSeconds = duration.TotalSeconds,
                item.Notes, Source = item.SourceDescription, item.AspectRatioMode, item.InterlaceMode
            });
            cursor += duration;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
        AuditLogService.Write("EPG_EXPORT_JSON", channelName, path);
    }

    private static string ResolveTitle(PlaylistItem item, EpgOutputSettings s) => s.TitleSource switch
    {
        "Original Title" => string.IsNullOrWhiteSpace(item.OriginalTitle) ? item.Title : item.OriginalTitle,
        "Now/Next Promo Name" => item.Title,
        _ => item.Title
    };
    private static string ResolveDescription(PlaylistItem item, EpgOutputSettings s) => s.DescriptionSource switch
    {
        "Original Description" => item.OriginalDescription,
        "Notes" => item.Notes,
        _ => string.IsNullOrWhiteSpace(item.OriginalDescription) ? BuildDescription(item) : item.OriginalDescription
    };
    private static string ResolveColumn(PlaylistItem item, string name) => name switch
    {
        "Original Title" => item.OriginalTitle, "Original Description" => item.OriginalDescription,
        "Episode Title" => item.EpisodeTitle, "Episode Description" => item.EpisodeDescription,
        "Episode Season" => item.EpisodeSeason > 0 ? item.EpisodeSeason.ToString(CultureInfo.InvariantCulture) : string.Empty, "Episode No" => item.EpisodeNumber > 0 ? item.EpisodeNumber.ToString(CultureInfo.InvariantCulture) : string.Empty,
        "Genre" => item.Genre, "Production Year" => item.ProductionYear > 0 ? item.ProductionYear.ToString(CultureInfo.InvariantCulture) : string.Empty,
        "Country" => item.Country, "Director" => item.Director, "Lead Actors" => item.LeadActors,
        "IMDB" => item.ImdbUrl, "EPG Url Pic" => item.ImageUrl, "EPG Picture URL Large" => item.ImageUrl,
        "EPG Picture URL Small" => item.ImageUrl, "PA (Parental Advisory)" => item.ParentalAdvisory,
        "Premiere" => item.Premiere ? "true" : string.Empty, "Aspect Ratio" => item.AspectRatioMode, "Category" => item.Category,
        _ => string.Empty
    };
    private static void AddValue(XElement parent, string tag, string? value, string? lang, bool skipEmpty)
    {
        value ??= string.Empty;
        if (skipEmpty && string.IsNullOrWhiteSpace(value)) return;
        var element = new XElement(tag, value);
        if (!string.IsNullOrWhiteSpace(lang)) element.SetAttributeValue("lang", lang);
        parent.Add(element);
    }
    private static string SanitizeElementName(string? value)
    {
        var raw = string.IsNullOrWhiteSpace(value) ? "field" : value.Trim();
        try { XmlConvert.VerifyNCName(raw); return raw; } catch { }
        var sb = new StringBuilder();
        foreach (var c in raw) sb.Append(char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_');
        var name = sb.ToString(); if (name.Length == 0 || (!char.IsLetter(name[0]) && name[0] != '_')) name = "f_" + name;
        return name;
    }
    private static DateTime ResolveStart(PlaylistItem? first)
    {
        if (first is not null && TimeSpan.TryParse(first.StartTimeText, CultureInfo.InvariantCulture, out var time)) return DateTime.Today + time;
        return DateTime.Now;
    }
    private static DateTime RoundSecond(DateTime value) => new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, value.Kind);
    private static string FormatEpgTime(DateTime value, string mode) => mode switch
    {
        "UTC Z" => value.ToUniversalTime().ToString("yyyyMMddHHmmss 'Z'", CultureInfo.InvariantCulture),
        "Local Compact" => value.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture),
        "Human Readable" => value.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
        _ => value.ToString("yyyyMMddHHmmss zzz", CultureInfo.InvariantCulture).Replace(":", string.Empty)
    };
    private static string BuildDescription(PlaylistItem item)
    {
        var parts = new[] { item.Notes, item.MediaDetails, item.SourceDescription }.Where(x => !string.IsNullOrWhiteSpace(x));
        return string.Join(" · ", parts);
    }
    private sealed class Utf8StringWriter : StringWriter { public override Encoding Encoding => Encoding.UTF8; }
}
