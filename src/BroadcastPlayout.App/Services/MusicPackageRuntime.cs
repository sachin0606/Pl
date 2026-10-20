using System.Text.RegularExpressions;

namespace BroadcastPlayout.Services;

/// <summary>What the NEXT / LATER box shows. <see cref="PreviewStartSeconds"/> is where its video preview starts.</summary>
public sealed record UpNextBoxState(string Tag, SongInfo Song, double PreviewStartSeconds);

/// <summary>
/// Live data for the music Now/Next package graphics. The playout writes it (one channel per process),
/// the CG compositor reads it every frame. Values are immutable records published with a single
/// reference write, so rendering never locks or allocates to read them.
///
/// Tokens for CG text layers: {NowTitle} {NowFilm} {NowSinger} {NowLyrics} {NowMusic} {NowLabel}
/// and the same with Box ({BoxTag} {BoxTitle} {BoxFilm} ...). Add ":upper" for capitals, e.g. {NowTitle:upper}.
/// </summary>
public static partial class MusicPackageRuntime
{
    /// <summary>Shown in the CG Designer when no playout feeds real data, so templates can be laid out.</summary>
    public static readonly SongInfo Sample = new("Nazrein Mili Dil Dhadka", "Raja", "Udit Narayan & Alka Yagnik", "Sameer", "Nadeem-Shravan", "Shemaroo Entertainment Limited", "", 300);
    private static readonly UpNextBoxState SampleBox = new("NEXT", new SongInfo("Kitni Hasrat Hai Humein", "Sainik", "Kumar Sanu", "Sameer", "Nadeem-Shravan", "", "", 300), 0);

    private static SongInfo _now = SongInfo.Empty;
    private static UpNextBoxState? _box;

    /// <summary>True once a playout publishes data; until then the designer sees <see cref="Sample"/>.</summary>
    public static bool IsLive { get; set; }

    /// <summary>Position of the current item, 0..1. Set by the playout.</summary>
    public static Func<double>? GetProgress { get; set; }

    public static SongInfo Now
    {
        get => IsLive ? Volatile.Read(ref _now) : Sample;
        set => Volatile.Write(ref _now, value ?? SongInfo.Empty);
    }

    public static UpNextBoxState? Box
    {
        get => IsLive ? Volatile.Read(ref _box) : SampleBox;
        set => Volatile.Write(ref _box, value);
    }

    /// <summary>Progress 0..1. Without a playout the designer gets a slow demo sweep so the knob is visible.</summary>
    public static double Progress(double timelineSeconds)
    {
        if (GetProgress is { } provider) return Math.Clamp(provider(), 0, 1);
        return IsLive ? 0 : (timelineSeconds / 60.0) % 1.0;
    }

    public static bool HasTokens(string text) => text.Contains("{Now", StringComparison.OrdinalIgnoreCase) || text.Contains("{Box", StringComparison.OrdinalIgnoreCase);

    public static string ResolveTokens(string text) => ResolveTokens(text, out _);

    /// <summary>Replaces package tokens. <paramref name="anyEmpty"/> tells the caller a token had no value (credit lines skip such lines).</summary>
    public static string ResolveTokens(string text, out bool anyEmpty)
    {
        anyEmpty = false;
        if (string.IsNullOrEmpty(text) || !HasTokens(text)) return text ?? string.Empty;
        var now = Now;
        var box = Box;
        var empty = false;
        var result = TokenPattern().Replace(text, m =>
        {
            var song = m.Groups[1].Value.Equals("Now", StringComparison.OrdinalIgnoreCase) ? now : box?.Song;
            string? value;
            switch (m.Groups[2].Value.ToUpperInvariant())
            {
                case "TAG": value = box?.Tag; break;
                case "TITLE": value = song?.Title; break;
                case "FILM" or "ALBUM": value = song?.Film; break;
                case "SINGER": value = song?.Singer; break;
                case "LYRICS": value = song?.Lyrics; break;
                case "MUSIC": value = song?.Music; break;
                case "LABEL": value = song?.Label; break;
                default: return m.Value; // not a package token, leave it for other resolvers
            }
            if (string.IsNullOrWhiteSpace(value)) { empty = true; return string.Empty; }
            return m.Groups[3].Success ? value.ToUpperInvariant() : value;
        });
        anyEmpty = empty;
        return result;
    }

    /// <summary>
    /// Credit lines for a CREDITS layer: one template line per row, e.g. "Lyrics : {NowLyrics}".
    /// A line whose token has no value is left out, so missing credits never show an empty "Music :".
    /// </summary>
    public static List<string> CreditLines(string? template)
    {
        var lines = new List<string>(6);
        if (string.IsNullOrWhiteSpace(template)) return lines;
        foreach (var raw in template.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;
            var resolved = ResolveTokens(line, out var anyEmpty);
            if (!anyEmpty && !string.IsNullOrWhiteSpace(resolved)) lines.Add(resolved.Trim());
        }
        return lines;
    }

    [GeneratedRegex(@"\{(Now|Box)(\w+)(:upper)?\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}
