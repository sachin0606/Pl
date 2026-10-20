using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BroadcastPlayout.Models;

namespace BroadcastPlayout.Services;

/// <summary>What the NEXT / LATER box shows and how its time is written.</summary>
public sealed record UpNextBoxState(string Tag, AiringInfo Info, double PreviewStartSeconds, NowNextTimeDisplay TimeDisplay = NowNextTimeDisplay.None, double CountdownMinutes = 10)
{
    /// <summary>Play the airing's video in the box; otherwise (or for live / no file) a title card shows.</summary>
    public bool ShowVideo { get; init; } = true;
}

/// <summary>How a viewer request is written on screen ("Requested by Ramesh, Delhi").</summary>
public sealed record RequestFormat(bool Show, string Prefix, bool Name, bool City, bool Message)
{
    public static readonly RequestFormat Default = new(true, "Requested by", true, true, false);
}

/// <summary>
/// Live data for the Now/Next graphics. The playout writes it (one channel per process), the CG
/// compositor reads it every frame. Values are immutable records published with a single reference
/// write, so rendering never locks to read them.
///
/// CG text tokens, for the item on air (Now) and the box (Box):
///   {NowTitle} {NowFilm} {NowSinger} {NowLyrics} {NowMusic} {NowLabel} {NowKind} {NowSubtitle} {NowRequest}
///   {BoxTag} {BoxWhen} {BoxTitle} {BoxSubtitle} {BoxRequest} ... (same fields as Now)
/// Add ":upper" for capitals, e.g. {NowTitle:upper}.
/// </summary>
public static partial class NowNextRuntime
{
    /// <summary>Shown in the CG Designer when no playout feeds real data, so templates can be laid out.</summary>
    public static readonly AiringInfo Sample = new AiringInfo("Nazrein Mili Dil Dhadka", "Raja", "Udit Narayan & Alka Yagnik", "Sameer", "Nadeem-Shravan", "Shemaroo Entertainment Limited", "", 300)
        { Kind = "SONG", Request = new RequestTag(Guid.Empty, "Ramesh", "Delhi", "For my mother", "WhatsApp") };
    private static readonly UpNextBoxState SampleBox = new("NEXT",
        new AiringInfo("Kitni Hasrat Hai Humein", "Sainik", "Kumar Sanu", "Sameer", "Nadeem-Shravan", "", "", 300) { Kind = "SONG" }, 0);

    private static AiringInfo _now = AiringInfo.Empty;
    private static UpNextBoxState? _box;
    private static RequestFormat _requestFormat = RequestFormat.Default;
    private sealed record WhenCache(long Second, UpNextBoxState Box, string Text);
    private static WhenCache? _whenCache;

    /// <summary>True once a playout publishes data; until then the designer sees <see cref="Sample"/>.</summary>
    public static bool IsLive { get; set; }

    /// <summary>Position of the current item, 0..1. Set by the playout.</summary>
    public static Func<double>? GetProgress { get; set; }

    /// <summary>Wall clock used for countdowns (replaceable in tests).</summary>
    public static Func<DateTime> Clock { get; set; } = () => DateTime.Now;

    public static AiringInfo Now
    {
        get => IsLive ? Volatile.Read(ref _now) : Sample;
        set => Volatile.Write(ref _now, value ?? AiringInfo.Empty);
    }

    public static UpNextBoxState? Box
    {
        get => IsLive ? Volatile.Read(ref _box) : SampleBox;
        set => Volatile.Write(ref _box, value);
    }

    public static RequestFormat Requests
    {
        get => Volatile.Read(ref _requestFormat);
        set => Volatile.Write(ref _requestFormat, value ?? RequestFormat.Default);
    }

    /// <summary>Progress 0..1. Without a playout the designer gets a slow demo sweep so the knob is visible.</summary>
    public static double Progress(double timelineSeconds)
    {
        if (GetProgress is { } provider) return Math.Clamp(provider(), 0, 1);
        return IsLive ? 0 : (timelineSeconds / 60.0) % 1.0;
    }

    /// <summary>"Requested by Ramesh, Delhi" (per the request display options), or empty.</summary>
    public static string RequestText(RequestTag? request) => RequestText(request, Requests);

    public static string RequestText(RequestTag? request, RequestFormat format)
    {
        if (request is null || !format.Show) return string.Empty;
        var parts = new List<string>(2);
        if (format.Name && !string.IsNullOrWhiteSpace(request.ViewerName)) parts.Add(request.ViewerName.Trim());
        if (format.City && !string.IsNullOrWhiteSpace(request.City)) parts.Add(request.City.Trim());
        var text = new StringBuilder();
        if (parts.Count > 0) text.Append((format.Prefix ?? string.Empty).Trim()).Append(' ').Append(string.Join(", ", parts));
        if (format.Message && !string.IsNullOrWhiteSpace(request.Message))
            text.Append(text.Length > 0 ? " · " : string.Empty).Append('"').Append(request.Message.Trim()).Append('"');
        return text.ToString().Trim();
    }

    /// <summary>The box's start time per its display option: "20:00", "IN 5 MIN", both, or empty. Cached per second.</summary>
    public static string WhenText(UpNextBoxState? box)
    {
        if (box?.Info.Start is not { } start || box.TimeDisplay == NowNextTimeDisplay.None) return string.Empty;
        var now = Clock();
        var second = now.Ticks / TimeSpan.TicksPerSecond;
        var cache = Volatile.Read(ref _whenCache);
        if (cache is not null && cache.Second == second && ReferenceEquals(cache.Box, box)) return cache.Text;
        var text = FormatWhen(start, now, box.TimeDisplay, box.CountdownMinutes);
        Volatile.Write(ref _whenCache, new WhenCache(second, box, text));
        return text;
    }

    public static string FormatWhen(DateTime start, DateTime now, NowNextTimeDisplay display, double countdownMinutes)
    {
        var left = start - now;
        var time = start.ToString(start.Date == now.Date ? "HH:mm" : "ddd HH:mm", CultureInfo.InvariantCulture).ToUpperInvariant();
        var countdown = left.TotalSeconds <= 5 ? "NOW"
            : left.TotalSeconds < 60 ? $"IN {Math.Ceiling(left.TotalSeconds):0} SEC"
            : left.TotalMinutes < 60 ? $"IN {Math.Ceiling(left.TotalMinutes):0} MIN"
            : $"IN {(int)left.TotalHours} HR {left.Minutes:00} MIN";
        return display switch
        {
            NowNextTimeDisplay.Time => time,
            NowNextTimeDisplay.Countdown => countdown,
            NowNextTimeDisplay.Both => $"{time} · {countdown}",
            NowNextTimeDisplay.Auto => left.TotalMinutes <= countdownMinutes ? countdown : time,
            _ => string.Empty
        };
    }

    public static bool HasTokens(string text) => text.Contains("{Now", StringComparison.OrdinalIgnoreCase) || text.Contains("{Box", StringComparison.OrdinalIgnoreCase);

    public static string ResolveTokens(string text) => ResolveTokens(text, out _);

    /// <summary>Replaces Now/Next tokens. <paramref name="anyEmpty"/> tells the caller a token had no value (credit lines skip such lines).</summary>
    public static string ResolveTokens(string text, out bool anyEmpty)
    {
        anyEmpty = false;
        if (string.IsNullOrEmpty(text) || !HasTokens(text)) return text ?? string.Empty;
        var now = Now;
        var box = Box;
        var empty = false;
        var result = TokenPattern().Replace(text, m =>
        {
            var isNow = m.Groups[1].Value.Equals("Now", StringComparison.OrdinalIgnoreCase);
            var info = isNow ? now : box?.Info;
            string? value;
            switch (m.Groups[2].Value.ToUpperInvariant())
            {
                case "TAG": value = isNow ? null : box?.Tag; break;
                case "WHEN": value = isNow ? null : WhenText(box); break;
                case "TITLE": value = info?.Title; break;
                case "FILM" or "ALBUM": value = info?.Film; break;
                case "SINGER": value = info?.Singer; break;
                case "LYRICS": value = info?.Lyrics; break;
                case "MUSIC": value = info?.Music; break;
                case "LABEL": value = info?.Label; break;
                case "KIND": value = info?.Kind; break;
                case "SUBTITLE": value = info?.Subtitle; break;
                case "REQUEST": value = RequestText(info?.Request); break;
                default: return m.Value; // not a Now/Next token, leave it for other resolvers
            }
            if (string.IsNullOrWhiteSpace(value)) { empty = true; return string.Empty; }
            return m.Groups[3].Success ? value.ToUpperInvariant() : value;
        });
        anyEmpty = empty;
        return result;
    }

    /// <summary>
    /// Lines for a CREDITS layer: one template line per row, e.g. "Lyrics : {NowLyrics}".
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
