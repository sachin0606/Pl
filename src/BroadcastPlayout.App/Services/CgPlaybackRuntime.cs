using System;
using System.Globalization;
using System.Text.RegularExpressions;
using BroadcastPlayout.Models;

namespace BroadcastPlayout.Services;

/// <summary>
/// Provides live runtime metadata from the Playout engine to CG Compositor and CG Editor
/// for dynamic variables ({Title}, {Artist}, {ProgramName}, {BackIn}, etc.) and commercial break countdowns.
/// </summary>
public static partial class CgPlaybackRuntime
{
    public static Func<double>? GetRemainingCommercialBreakSeconds { get; set; }
    public static Func<string>? GetCurrentTitle { get; set; }
    public static Func<string>? GetCurrentArtist { get; set; }
    public static Func<string>? GetCurrentProgramName { get; set; }
    public static Func<string>? GetCurrentCategory { get; set; }
    public static Func<string>? GetNextTitle { get; set; }
    public static Func<string>? GetNextArtist { get; set; }
    public static Func<string>? GetNextProgramName { get; set; }

    public static double RemainingCommercialBreakSeconds => Math.Max(0, GetRemainingCommercialBreakSeconds?.Invoke() ?? 0.0);
    public static string CurrentTitle => GetCurrentTitle?.Invoke() ?? string.Empty;
    public static string CurrentArtist => GetCurrentArtist?.Invoke() ?? string.Empty;
    public static string CurrentProgramName => GetCurrentProgramName?.Invoke() ?? string.Empty;
    public static string CurrentCategory => GetCurrentCategory?.Invoke() ?? string.Empty;
    public static string NextTitle => GetNextTitle?.Invoke() ?? string.Empty;
    public static string NextArtist => GetNextArtist?.Invoke() ?? string.Empty;
    public static string NextProgramName => GetNextProgramName?.Invoke() ?? string.Empty;

    public static string FormatTimer(double seconds, string? format = "mm:ss", string? prefix = null, string? suffix = null)
    {
        var clamped = Math.Max(0, seconds);
        var ts = TimeSpan.FromSeconds(clamped);
        var fmt = string.IsNullOrWhiteSpace(format) ? "mm:ss" : format.Trim();

        string formatted;
        int days = ts.Days;
        int hours = ts.Hours;
        int minutes = ts.Minutes;
        int secs = ts.Seconds;

        if (fmt.Equals("d days hh:mm:ss", StringComparison.OrdinalIgnoreCase))
        {
            formatted = $"{days} {(days == 1 ? "day" : "days")} {hours:D2}:{minutes:D2}:{secs:D2}";
        }
        else if (fmt.Equals("d days h hours m min s sec", StringComparison.OrdinalIgnoreCase) || fmt.Equals("days hours minutes seconds", StringComparison.OrdinalIgnoreCase))
        {
            formatted = days > 0
                ? $"{days} {(days == 1 ? "day" : "days")} {hours} {(hours == 1 ? "hour" : "hours")} {minutes} {(minutes == 1 ? "minute" : "minutes")} {secs} sec"
                : $"{hours} {(hours == 1 ? "hour" : "hours")} {minutes} {(minutes == 1 ? "minute" : "minutes")} {secs} sec";
        }
        else if (fmt.Equals("d'd' hh:mm:ss", StringComparison.OrdinalIgnoreCase) || fmt.Equals("d\\d hh:mm:ss", StringComparison.OrdinalIgnoreCase))
        {
            formatted = $"{days}d {hours:D2}:{minutes:D2}:{secs:D2}";
        }
        else if (fmt.Equals("dd:hh:mm:ss", StringComparison.OrdinalIgnoreCase))
        {
            formatted = $"{days:D2}:{hours:D2}:{minutes:D2}:{secs:D2}";
        }
        else if (fmt.Equals("hh:mm:ss", StringComparison.OrdinalIgnoreCase) || fmt.Equals("HH:mm:ss", StringComparison.OrdinalIgnoreCase))
        {
            var totalHours = (int)ts.TotalHours;
            formatted = $"{totalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
        }
        else if (fmt.Equals("ss", StringComparison.OrdinalIgnoreCase) || fmt.Equals("s", StringComparison.OrdinalIgnoreCase))
        {
            formatted = ((int)ts.TotalSeconds).ToString();
        }
        else if (fmt.Equals("m:ss", StringComparison.OrdinalIgnoreCase))
        {
            var totalMins = (int)ts.TotalMinutes;
            formatted = $"{totalMins}:{ts.Seconds:D2}";
        }
        else if (fmt.Contains("d", StringComparison.OrdinalIgnoreCase))
        {
            try { formatted = ts.ToString(fmt, CultureInfo.InvariantCulture); }
            catch { formatted = $"{days}d {hours:D2}:{minutes:D2}:{secs:D2}"; }
        }
        else // default mm:ss
        {
            var totalMins = (int)ts.TotalMinutes;
            formatted = $"{totalMins:D2}:{ts.Seconds:D2}";
        }

        var p = prefix ?? string.Empty;
        var s = suffix ?? string.Empty;
        return $"{p}{formatted}{s}";
    }

    /// <summary>
    /// Performs token substitution on text for dynamic variables.
    /// Supports: {Title}, {Artist}, {ProgramName}, {Category}, {Tag}, {Description}, {BackIn}, {CommercialRemaining}, {NextTitle}, {NextArtist}, {Timer}.
    /// </summary>
    public static string SubstituteTokens(string? text, double timelineSeconds = 0, CgLayer? layer = null)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (!text.Contains('{')) return text;

        var result = text;

        // Commercial break countdown tokens
        if (result.Contains("{BackIn}", StringComparison.OrdinalIgnoreCase) ||
            result.Contains("{CommercialRemaining}", StringComparison.OrdinalIgnoreCase) ||
            result.Contains("{BreakRemaining}", StringComparison.OrdinalIgnoreCase))
        {
            var rem = RemainingCommercialBreakSeconds;
            if (rem <= 0 && layer != null && layer.TimerStartSeconds > 0)
            {
                // Fallback for demo / preview
                rem = Math.Max(0, layer.TimerStartSeconds - timelineSeconds);
            }
            var formattedBreak = FormatTimer(rem, layer?.TimerFormat ?? "mm:ss");
            result = ReplaceToken(result, "BackIn", formattedBreak);
            result = ReplaceToken(result, "CommercialRemaining", formattedBreak);
            result = ReplaceToken(result, "BreakRemaining", formattedBreak);
        }

        // Generic Timer token
        if (result.Contains("{Timer}", StringComparison.OrdinalIgnoreCase) && layer != null)
        {
            double sec;
            if (string.Equals(layer.TimerMode, "CountUp", StringComparison.OrdinalIgnoreCase))
                sec = layer.TimerStartSeconds + timelineSeconds;
            else if (string.Equals(layer.TimerMode, "CommercialBackIn", StringComparison.OrdinalIgnoreCase))
                sec = RemainingCommercialBreakSeconds > 0 ? RemainingCommercialBreakSeconds : Math.Max(0, layer.TimerStartSeconds - timelineSeconds);
            else
                sec = Math.Max(0, layer.TimerStartSeconds - timelineSeconds);

            var timerStr = FormatTimer(sec, layer.TimerFormat, layer.TimerPrefix, layer.TimerSuffix);
            result = ReplaceToken(result, "Timer", timerStr);
        }

        // Program and Title tokens
        var curTitle = CurrentTitle;
        if (!string.IsNullOrEmpty(curTitle))
        {
            result = ReplaceToken(result, "Title", curTitle);
            result = ReplaceToken(result, "SongTitle", curTitle);
            result = ReplaceToken(result, "MusicTitle", curTitle);
        }

        var curArtist = CurrentArtist;
        if (!string.IsNullOrEmpty(curArtist))
        {
            result = ReplaceToken(result, "Artist", curArtist);
            result = ReplaceToken(result, "Singer", curArtist);
            result = ReplaceToken(result, "Presenter", curArtist);
        }

        var curProg = CurrentProgramName;
        if (string.IsNullOrEmpty(curProg)) curProg = curTitle;
        if (!string.IsNullOrEmpty(curProg))
        {
            result = ReplaceToken(result, "ProgramName", curProg);
            result = ReplaceToken(result, "ProgramTitle", curProg);
            result = ReplaceToken(result, "Program", curProg);
        }

        var curCat = CurrentCategory;
        if (!string.IsNullOrEmpty(curCat))
        {
            result = ReplaceToken(result, "Category", curCat);
            result = ReplaceToken(result, "Genre", curCat);
        }

        var nxtTitle = NextTitle;
        if (!string.IsNullOrEmpty(nxtTitle))
        {
            result = ReplaceToken(result, "NextTitle", nxtTitle);
            result = ReplaceToken(result, "NextSong", nxtTitle);
            result = ReplaceToken(result, "NextProgram", nxtTitle);
        }

        var nxtArtist = NextArtist;
        if (!string.IsNullOrEmpty(nxtArtist))
        {
            result = ReplaceToken(result, "NextArtist", nxtArtist);
            result = ReplaceToken(result, "NextSinger", nxtArtist);
        }

        return result;
    }

    private static string ReplaceToken(string source, string tokenName, string replacement)
    {
        return Regex.Replace(source, @"\{" + Regex.Escape(tokenName) + @"\}", replacement ?? string.Empty, RegexOptions.IgnoreCase);
    }
}
