namespace BroadcastPlayout.Services;

public sealed record ParsedMediaName(string Title, string Artist, string Extra)
{
    public string Display => string.IsNullOrWhiteSpace(Artist) ? Title : $"{Title} - {Artist}";
}

public static class MediaNameParser
{
    public static ParsedMediaName Parse(string? filePath, string? fallbackTitle = null)
    {
        var raw = string.IsNullOrWhiteSpace(filePath) ? fallbackTitle ?? "Untitled" : Path.GetFileNameWithoutExtension(filePath);
        raw = raw.Replace('_', ' ').Trim();
        var parts = raw.Split(" - ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 1) return new ParsedMediaName(parts.FirstOrDefault() ?? fallbackTitle ?? "Untitled", "", "");
        return new ParsedMediaName(parts[0], parts[1], parts.Length > 2 ? string.Join(" - ", parts.Skip(2)) : "");
    }
}

public static class MediaCategoryClassifier
{
    public static string Classify(string? path, string? title = null)
    {
        var text = ((path ?? "") + " " + (title ?? "")).ToLowerInvariant();
        if (text.Contains("music") || text.Contains("song") || text.Contains("artist") || text.Contains("album")) return "Music";
        if (text.Contains("news") || text.Contains("headline") || text.Contains("bulletin") || text.Contains("story")) return "News";
        if (text.Contains("commercial") || text.Contains("advert") || text.Contains("promo") || text.Contains("spot") || text.Contains("_ad")) return "Commercial";
        if (text.Contains("graphic") || text.Contains("gfx") || text.Contains("lowerthird") || text.Contains("logo")) return "Graphics";
        return "Media";
    }
}
