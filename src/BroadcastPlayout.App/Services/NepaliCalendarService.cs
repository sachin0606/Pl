using System;
using System.Collections.Generic;
using System.Text;

namespace BroadcastPlayout.Services;

public readonly record struct NepaliDate(
    int Year,
    int Month,
    int Day,
    DayOfWeek DayOfWeek,
    string MonthNameEn,
    string MonthNameNp,
    string DayNameEn,
    string DayNameNp)
{
    public override string ToString() => $"{Year:0000}-{Month:00}-{Day:00}";
}

/// <summary>
/// Precision Bikram Sambat (BS) calendar engine with Devnagari &amp; English numeral support,
/// month/day names, broadcast formatting tokens and live date/time rendering.
/// </summary>
public static class NepaliCalendarService
{
    private static readonly string[] NepaliDigits = ["०", "१", "२", "३", "४", "५", "६", "७", "८", "९"];

    public static readonly string[] MonthsEn =
    [
        "Baisakh", "Jestha", "Ashar", "Shrawan", "Bhadra", "Ashoj",
        "Kartik", "Mangsir", "Poush", "Magh", "Falgun", "Chaitra"
    ];

    public static readonly string[] MonthsNp =
    [
        "बैशाख", "जेठ", "अषाढ", "श्रावण", "भाद्र", "आश्विन",
        "कार्तिक", "मङ्सिर", "पौष", "माघ", "फाल्गुन", "चैत्र"
    ];

    public static readonly string[] DaysEn =
    [
        "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"
    ];

    public static readonly string[] DaysNp =
    [
        "आइतबार", "सोमबार", "मङ्गलबार", "बुधबार", "बिहिबार", "शुक्रबार", "शनिबार"
    ];

    public static readonly string[] DaysNpShort = ["आ", "सो", "मं", "बु", "बि", "शु", "श"];

    // Days in each Bikram Sambat month from BS 1970 to BS 2099
    private static readonly Dictionary<int, int[]> BsDaysInMonth = new()
    {
        [1970] = [31, 31, 32, 31, 31, 31, 30, 29, 30, 29, 30, 30],
        [1971] = [31, 31, 32, 31, 32, 30, 30, 29, 30, 29, 30, 30],
        [1972] = [31, 32, 31, 32, 31, 30, 30, 30, 29, 29, 30, 31],
        [1973] = [30, 32, 31, 32, 31, 30, 30, 30, 29, 30, 29, 31],
        [1974] = [31, 31, 32, 31, 31, 31, 30, 29, 30, 29, 30, 30],
        [1975] = [31, 31, 32, 32, 31, 30, 30, 29, 30, 29, 30, 30],
        [1976] = [31, 32, 31, 32, 31, 30, 30, 30, 29, 29, 30, 31],
        [1977] = [30, 32, 31, 32, 31, 31, 29, 30, 29, 30, 29, 31],
        [1978] = [31, 31, 32, 31, 31, 31, 30, 29, 30, 29, 30, 30],
        [1979] = [31, 31, 32, 32, 31, 30, 30, 29, 30, 29, 30, 30],
        [1980] = [31, 32, 31, 32, 31, 30, 30, 30, 29, 29, 30, 31],
        [1981] = [31, 31, 31, 32, 31, 31, 29, 30, 30, 29, 30, 30],
        [1982] = [31, 31, 32, 31, 31, 31, 30, 29, 30, 29, 30, 30],
        [1983] = [31, 31, 32, 32, 31, 30, 30, 29, 30, 29, 30, 30],
        [1984] = [31, 32, 31, 32, 31, 30, 30, 30, 29, 29, 30, 31],
        [1985] = [31, 31, 31, 32, 31, 31, 29, 30, 30, 29, 30, 30],
        [1986] = [31, 31, 32, 31, 31, 31, 30, 29, 30, 29, 30, 30],
        [1987] = [31, 32, 31, 32, 31, 30, 30, 29, 30, 29, 30, 30],
        [1988] = [31, 32, 31, 32, 31, 30, 30, 30, 29, 29, 30, 31],
        [1989] = [31, 31, 31, 32, 31, 31, 30, 29, 30, 29, 30, 30],
        [1990] = [31, 31, 32, 31, 31, 31, 30, 29, 30, 29, 30, 30],
        [1991] = [31, 32, 31, 32, 31, 30, 30, 30, 29, 29, 30, 30],
        [1992] = [31, 32, 31, 32, 31, 30, 30, 30, 29, 30, 29, 31],
        [1993] = [31, 31, 31, 32, 31, 31, 30, 29, 30, 29, 30, 30],
        [1994] = [31, 31, 32, 31, 31, 31, 30, 29, 30, 29, 30, 30],
        [1995] = [31, 32, 31, 32, 31, 30, 30, 30, 29, 29, 30, 30],
        [1996] = [31, 32, 31, 32, 31, 30, 30, 30, 29, 30, 29, 31],
        [1997] = [31, 31, 32, 31, 31, 31, 30, 29, 30, 29, 30, 30],
        [1998] = [31, 31, 32, 31, 31, 31, 30, 29, 30, 29, 30, 30],
        [1999] = [31, 32, 31, 32, 31, 30, 30, 30, 29, 29, 30, 31],
        [2000] = [30, 32, 31, 32, 31, 30, 30, 30, 29, 30, 29, 31],
        [2001] = [31, 31, 32, 31, 31, 31, 30, 29, 30, 29, 30, 30],
        [2002] = [31, 31, 32, 32, 31, 30, 30, 29, 30, 29, 30, 30],
        [2003] = [31, 32, 31, 32, 31, 30, 30, 30, 29, 29, 30, 31],
        [2004] = [30, 32, 31, 32, 31, 30, 30, 30, 29, 30, 29, 31],
        [2005] = [31, 31, 32, 31, 31, 31, 30, 29, 30, 29, 30, 30],
        [2006] = [31, 31, 32, 32, 31, 30, 30, 29, 30, 29, 30, 30],
        [2007] = [31, 32, 31, 32, 31, 30, 30, 30, 29, 29, 30, 31],
        [2008] = [31, 31, 31, 32, 31, 31, 29, 30, 30, 29, 29, 31],
        [2009] = [31, 31, 32, 31, 31, 31, 30, 29, 30, 29, 30, 30],
        [2010] = [31, 31, 32, 32, 31, 30, 30, 29, 30, 29, 30, 30],
        [2011] = [31, 32, 31, 32, 31, 30, 30, 30, 29, 29, 30, 31],
        [2012] = [31, 31, 31, 32, 31, 31, 29, 30, 30, 29, 30, 30],
        [2013] = [31, 31, 32, 31, 31, 31, 30, 29, 30, 29, 30, 30],
        [2014] = [31, 31, 32, 32, 31, 30, 30, 29, 30, 29, 30, 30],
        [2015] = [31, 32, 31, 32, 31, 30, 30, 30, 29, 29, 30, 31],
        [2016] = [31, 31, 31, 32, 31, 31, 29, 30, 30, 29, 30, 30],
        [2017] = [31, 31, 32, 31, 31, 31, 30, 29, 30, 29, 30, 30],
        [2018] = [31, 32, 31, 32, 31, 30, 30, 29, 30, 29, 30, 30],
        [2019] = [31, 32, 31, 32, 31, 30, 30, 30, 29, 30, 29, 31],
        [2020] = [31, 31, 31, 32, 31, 31, 30, 29, 30, 29, 30, 30],
        [2021] = [31, 31, 32, 31, 31, 31, 30, 29, 30, 29, 30, 30],
        [2022] = [31, 32, 31, 32, 31, 30, 30, 30, 29, 29, 30, 30],
        [2023] = [31, 32, 31, 32, 31, 30, 30, 30, 29, 30, 29, 31],
        [2024] = [31, 31, 31, 32, 31, 31, 30, 29, 30, 29, 30, 30],
        [2025] = [31, 31, 32, 31, 31, 31, 30, 29, 30, 29, 30, 30],
        [2026] = [31, 32, 31, 32, 31, 30, 30, 30, 29, 29, 30, 31],
        [2027] = [30, 32, 31, 32, 31, 30, 30, 30, 29, 30, 29, 31],
        [2028] = [31, 31, 32, 31, 31, 31, 30, 29, 30, 29, 30, 30],
        [2029] = [31, 31, 32, 31, 32, 30, 30, 29, 30, 29, 30, 30],
        [2030] = [31, 32, 31, 32, 31, 30, 30, 30, 29, 29, 30, 31],
        [2031] = [30, 32, 31, 32, 31, 30, 30, 30, 29, 30, 29, 31],
        [2032] = [31, 31, 32, 31, 31, 31, 30, 29, 30, 29, 30, 30],
        [2033] = [31, 31, 32, 32, 31, 30, 30, 29, 30, 29, 30, 30],
        [2034] = [31, 32, 31, 32, 31, 30, 30, 30, 29, 29, 30, 31],
        [2035] = [30, 32, 31, 32, 31, 31, 29, 30, 30, 29, 29, 31],
        [2036] = [31, 31, 32, 31, 31, 31, 30, 29, 30, 29, 30, 30],
        [2037] = [31, 31, 32, 32, 31, 30, 30, 29, 30, 29, 30, 30],
        [2038] = [31, 32, 31, 32, 31, 30, 30, 30, 29, 29, 30, 31],
        [2039] = [31, 31, 31, 32, 31, 31, 29, 30, 30, 29, 30, 30],
        [2040] = [31, 31, 32, 31, 31, 31, 30, 29, 30, 29, 30, 30],
        [2041] = [31, 31, 32, 32, 31, 30, 30, 29, 30, 29, 30, 30],
        [2042] = [31, 32, 31, 32, 31, 30, 30, 30, 29, 29, 30, 31],
        [2043] = [31, 31, 31, 32, 31, 31, 29, 30, 30, 29, 30, 30],
        [2044] = [31, 31, 32, 31, 31, 31, 30, 29, 30, 29, 30, 30],
        [2045] = [31, 32, 31, 32, 31, 30, 30, 29, 30, 29, 30, 30],
        [2046] = [31, 32, 31, 32, 31, 30, 30, 30, 29, 29, 30, 31],
        [2047] = [31, 31, 31, 32, 31, 31, 30, 29, 30, 29, 30, 30],
        [2048] = [31, 31, 32, 31, 31, 31, 30, 29, 30, 29, 30, 30],
        [2049] = [31, 32, 31, 32, 31, 30, 30, 30, 29, 29, 30, 30],
        [2050] = [31, 32, 31, 32, 31, 30, 30, 30, 29, 30, 29, 31],
        [2051] = [31, 31, 31, 32, 31, 31, 30, 29, 30, 29, 30, 30],
        [2052] = [31, 31, 32, 31, 31, 31, 30, 29, 30, 29, 30, 30],
        [2053] = [31, 32, 31, 32, 31, 30, 30, 30, 29, 29, 30, 30],
        [2054] = [31, 32, 31, 32, 31, 30, 30, 30, 29, 30, 29, 31],
        [2055] = [31, 31, 32, 31, 31, 31, 30, 29, 30, 29, 30, 30],
        [2056] = [31, 31, 32, 31, 32, 30, 30, 29, 30, 29, 30, 30],
        [2057] = [31, 32, 31, 32, 31, 30, 30, 30, 29, 29, 30, 31],
        [2058] = [30, 32, 31, 32, 31, 30, 30, 30, 29, 30, 29, 31],
        [2059] = [31, 31, 32, 31, 31, 31, 30, 29, 30, 29, 30, 30],
        [2060] = [31, 31, 32, 32, 31, 30, 30, 29, 30, 29, 30, 30],
        [2061] = [31, 32, 31, 32, 31, 30, 30, 30, 29, 29, 30, 31],
        [2062] = [30, 32, 31, 32, 31, 31, 29, 30, 29, 30, 29, 31],
        [2063] = [31, 31, 32, 31, 31, 31, 30, 29, 30, 29, 30, 30],
        [2064] = [31, 31, 32, 32, 31, 30, 30, 29, 30, 29, 30, 30],
        [2065] = [31, 32, 31, 32, 31, 30, 30, 30, 29, 29, 30, 31],
        [2066] = [31, 31, 31, 32, 31, 31, 29, 30, 30, 29, 29, 31],
        [2067] = [31, 31, 32, 31, 31, 31, 30, 29, 30, 29, 30, 30],
        [2068] = [31, 31, 32, 32, 31, 30, 30, 29, 30, 29, 30, 30],
        [2069] = [31, 32, 31, 32, 31, 30, 30, 30, 29, 29, 30, 31],
        [2070] = [31, 31, 31, 32, 31, 31, 29, 30, 30, 29, 30, 30],
        [2071] = [31, 31, 32, 31, 31, 31, 30, 29, 30, 29, 30, 30],
        [2072] = [31, 32, 31, 32, 31, 30, 30, 29, 30, 29, 30, 30],
        [2073] = [31, 32, 31, 32, 31, 30, 30, 30, 29, 29, 30, 31],
        [2074] = [31, 31, 31, 32, 31, 31, 30, 29, 30, 29, 30, 30],
        [2075] = [31, 31, 32, 31, 31, 31, 30, 29, 30, 29, 30, 30],
        [2076] = [31, 32, 31, 32, 31, 30, 30, 30, 29, 29, 30, 30],
        [2077] = [31, 32, 31, 32, 31, 30, 30, 30, 29, 30, 29, 31],
        [2078] = [31, 31, 31, 32, 31, 31, 30, 29, 30, 29, 30, 30],
        [2079] = [31, 31, 32, 31, 31, 31, 30, 29, 30, 29, 30, 30],
        [2080] = [31, 32, 31, 32, 31, 30, 30, 30, 29, 29, 30, 30],
        [2081] = [31, 31, 32, 32, 31, 30, 30, 30, 29, 30, 29, 30],
        [2082] = [30, 32, 31, 32, 31, 30, 30, 30, 29, 30, 30, 30],
        [2083] = [31, 31, 32, 31, 31, 30, 30, 30, 29, 30, 30, 30],
        [2084] = [31, 31, 32, 31, 31, 30, 30, 30, 29, 30, 30, 30],
        [2085] = [31, 32, 31, 32, 30, 31, 30, 30, 29, 30, 30, 30],
        [2086] = [30, 32, 31, 32, 31, 30, 30, 30, 29, 30, 30, 30],
        [2087] = [31, 31, 32, 31, 31, 31, 30, 30, 29, 30, 30, 30],
        [2088] = [30, 31, 32, 32, 30, 31, 30, 30, 29, 30, 30, 30],
        [2089] = [30, 32, 31, 32, 31, 30, 30, 30, 29, 30, 30, 30],
        [2090] = [30, 32, 31, 32, 31, 30, 30, 30, 29, 30, 30, 30],
        [2091] = [31, 31, 32, 31, 31, 31, 30, 30, 29, 30, 30, 30],
        [2092] = [30, 31, 32, 32, 31, 30, 30, 30, 29, 30, 30, 30],
        [2093] = [30, 32, 31, 32, 31, 30, 30, 30, 29, 30, 30, 30],
        [2094] = [31, 31, 32, 31, 31, 30, 30, 30, 29, 30, 30, 30],
        [2095] = [31, 31, 32, 31, 31, 31, 30, 29, 30, 30, 30, 30],
        [2096] = [30, 31, 32, 32, 31, 30, 30, 29, 30, 29, 30, 30],
        [2097] = [31, 32, 31, 32, 31, 30, 30, 30, 29, 30, 30, 30],
        [2098] = [31, 31, 32, 31, 31, 31, 29, 30, 29, 30, 29, 31],
        [2099] = [31, 31, 32, 31, 31, 31, 30, 29, 29, 30, 30, 30]
    };

    /// <summary>
    /// Convert standard Gregorian (AD) Date to Bikram Sambat (BS) Nepali Date.
    /// Uses reference epoch: 1944-01-01 AD = 2000-09-17 BS.
    /// </summary>
    public static NepaliDate ConvertToBs(DateTime adDate)
    {
        var baseAd = new DateTime(1944, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var targetAd = new DateTime(adDate.Year, adDate.Month, adDate.Day, 0, 0, 0, DateTimeKind.Utc);
        var daysDiff = (int)Math.Round((targetAd - baseAd).TotalDays);

        var curBsYear = 2000;
        var curBsMonth = 9;
        var curBsDay = 17;

        var remainingDays = daysDiff;
        while (remainingDays > 0)
        {
            if (!BsDaysInMonth.TryGetValue(curBsYear, out var daysInYear))
            {
                // Fallback default 30 days if beyond table
                curBsDay += remainingDays;
                break;
            }

            var daysInCurrentMonth = daysInYear[curBsMonth - 1];
            var daysLeftInMonth = daysInCurrentMonth - curBsDay;

            if (remainingDays <= daysLeftInMonth)
            {
                curBsDay += remainingDays;
                remainingDays = 0;
            }
            else
            {
                remainingDays -= (daysLeftInMonth + 1);
                curBsDay = 1;
                curBsMonth++;
                if (curBsMonth > 12)
                {
                    curBsMonth = 1;
                    curBsYear++;
                }
            }
        }

        var monthIndex = Math.Clamp(curBsMonth - 1, 0, 11);
        var dayOfWeek = adDate.DayOfWeek;
        var dayIndex = (int)dayOfWeek;

        return new NepaliDate(
            curBsYear,
            curBsMonth,
            curBsDay,
            dayOfWeek,
            MonthsEn[monthIndex],
            MonthsNp[monthIndex],
            DaysEn[dayIndex],
            DaysNp[dayIndex]
        );
    }

    /// <summary>
    /// Convert any number or digit string to Devnagari numerals (०, १, २, ३, ४, ५, ६, ७, ८, ९).
    /// </summary>
    public static string ToNepaliDigits(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        var sb = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            if (ch >= '0' && ch <= '9') sb.Append(NepaliDigits[ch - '0']);
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Format a date using rich broadcast tokens:
    /// YYYY, MM, DD, MMMM (month name), dddd (day of week), HH:mm:ss, hh:mm:ss tt.
    /// Supports both Nepali Devnagari and English representations.
    /// </summary>
    public static string FormatNepaliDate(DateTime adDate, string? format, bool devnagariDigits = true, bool devnagariText = true)
    {
        var bs = ConvertToBs(adDate);
        var fmt = string.IsNullOrWhiteSpace(format) ? "YYYY-MM-DD" : format;
        // Strip descriptive parenthetical suffixes like (Full Broadcast Ribbon), (English: 2081-06-07), etc.
        fmt = System.Text.RegularExpressions.Regex.Replace(fmt, @"\s*\([^)]*\)", "").Trim();
        if (string.IsNullOrWhiteSpace(fmt)) fmt = "YYYY-MM-DD";

        var monthName = devnagariText ? bs.MonthNameNp : bs.MonthNameEn;
        var dayName = devnagariText ? bs.DayNameNp : bs.DayNameEn;

        var yyyy = bs.Year.ToString("0000");
        var mm = bs.Month.ToString("00");
        var dd = bs.Day.ToString("00");

        var time24 = adDate.ToString("HH:mm:ss");
        var time12 = adDate.ToString("hh:mm:ss tt");

        var result = fmt
            .Replace("YYYY", yyyy)
            .Replace("MMMM", monthName)
            .Replace("dddd", dayName)
            .Replace("MM", mm)
            .Replace("DD", dd)
            .Replace("HH:mm:ss", time24)
            .Replace("hh:mm:ss tt", time12);

        if (devnagariDigits)
        {
            result = ToNepaliDigits(result);
        }

        return result;
    }

    public static readonly string[] StandardFormats =
    [
        "YYYY-MM-DD (BS Digits: २०८१-०६-०७)",
        "DD-MM-YYYY (BS Digits: ०७-०६-२०८१)",
        "YYYY-MM-DD (English: 2081-06-07)",
        "DD-MM-YYYY (English: 07-06-2081)",
        "dddd, DD MMMM YYYY (बुधबार, ०७ असोज २०८१)",
        "dddd, DD MMMM YYYY (Wednesday, 07 Ashoj 2081)",
        "YYYY-MM-DD HH:mm:ss (Nepali Date + Time)",
        "dddd, DD MMMM YYYY · hh:mm:ss tt (Full Broadcast Ribbon)"
    ];

    public static string ResolveBroadcastFormat(string selectionOrTemplate, DateTime now)
    {
        var s = (selectionOrTemplate ?? "YYYY-MM-DD").Trim();
        if (s.Contains("BS Digits: २०८१-०६-०७")) return FormatNepaliDate(now, "YYYY-MM-DD", devnagariDigits: true, devnagariText: true);
        if (s.Contains("BS Digits: ०७-०६-२०८१")) return FormatNepaliDate(now, "DD-MM-YYYY", devnagariDigits: true, devnagariText: true);
        if (s.Contains("English: 2081-06-07")) return FormatNepaliDate(now, "YYYY-MM-DD", devnagariDigits: false, devnagariText: false);
        if (s.Contains("English: 07-06-2081")) return FormatNepaliDate(now, "DD-MM-YYYY", devnagariDigits: false, devnagariText: false);
        if (s.Contains("बुधबार, ०७ असोज २०८१")) return FormatNepaliDate(now, "dddd, DD MMMM YYYY", devnagariDigits: true, devnagariText: true);
        if (s.Contains("Nepali Date + Time")) return FormatNepaliDate(now, "YYYY-MM-DD HH:mm:ss", devnagariDigits: true, devnagariText: true);
        if (s.Contains("Full Broadcast Ribbon")) return FormatNepaliDate(now, "dddd, DD MMMM YYYY · hh:mm:ss tt", devnagariDigits: true, devnagariText: true);

        // Raw custom format string
        var useDevnagari = s.Contains("Np") || s.Contains("Devnagari") || !s.Contains("En");
        return FormatNepaliDate(now, s, devnagariDigits: useDevnagari, devnagariText: useDevnagari);
    }

    // Preeti / Kantipur TTF font conversion tables
    private static readonly string[] PreetiUnicodeSymbols =
    [
        "‘", "?", "क़", "ख़", "ग़", "ज़", "ड़", "ढ़", "फ़", "ॐ", "ऽ", "।", "m'", "m]", "mfF", "mF",
        "०", "१", "२", "३", "४", "५", "६", "७", "८", "९", "फ्र", "झ", "फ", "क्त", "क्र", "ल",
        "ज्ञ्", "द्घ", "ज्ञ", "द्द", "द्ध", "श्र", "रु", "द्य", "क्ष्", "क्ष", "त्त", "द्म", "त्र",
        "ध्र", "ङ्घ", "ड्ड", "द्र", "ट्ट", "ड्ढ", "ठ्ठ", "रू", "हृ", "ङ्ग", "त्र", "ङ्क", "ङ्ख",
        "ट्ठ", "द्व", "ट्र", "ठ्र", "ड्र", "ढ्र", "्र", "ड़", "ढ़", "क्", "क", "ख्", "ख", "ग्",
        "ग", "घ्", "घ", "ङ", "च्", "च", "छ", "ज्", "ज", "झ्", "झ", "ञ्", "ञ", "ट", "ठ",
        "ड", "ढ", "ण्", "ण", "त्", "त", "थ्", "थ", "द", "ध्", "ध", "न्", "न", "प्", "प",
        "फ्", "ब्", "ब", "भ्", "भ", "म्", "म", "य", "र", "ल्", "ल", "व्", "व", "श्", "श",
        "ष्", "ष", "स्", "स", "ह्", "ह", "्य", "ऑ", "ऑ", "औ", "ओ", "आ", "अ", "ई", "इ",
        "ऊ", "उ", "ऋ", "ऐ", "ए", "ॉ", "ू", "ु", "ं", "ा", "ृ", "्", "े", "ै", "ँ", "ी",
        "ः", "ो", "ौ"
    ];

    private static readonly string[] PreetiAsciiSymbols =
    [
        "…", "<", "क़", "ख़", "ग़", "ज़", "ड़", "ढ़", "फ़", "ç", "˜", ".", "'m", "]m", "Fmf", "Fm",
        ")", "!", "@", "#", "$", "%", "^", "&", "*", "(", "k|m", "em", "km", "Qm", "qm", "n",
        "¡", "¢", "1", "2", "4", ">", "?", "B", "I", "If", "Q", "ß", "q", "„", "‹", "•",
        "›", "§", "°", "¶", "¿", "Å", "Ë", "Ì", "Í", "Î", "Ý", "å", "6«", "7«", "8«", "9«",
        "|", "8Þ", "9Þ", "S", "s", "V", "v", "U", "u", "£", "3", "ª", "R", "r", "5", "H",
        "h", "‰", "´", "~", "`", "6", "7", "8", "9", "0", "0f", "T", "t", "Y", "y", "b",
        "W", "w", "G", "g", "K", "k", "ˆ", "A", "a", "E", "e", "D", "d", "o", "/", "N",
        "n", "J", "j", "Z", "z", "i", "if", ":", ";", "X", "x", "Ø", "cf‘", "c‘f", "cf}", "cf]",
        "cf", "c", "O{", "O", "pm", "p", "C", "P]", "P", "f‘", "\"", "'", "+", "f", "[", "\\",
        "]", "}", "F", "L", "M", "f]", "f"
    ];

    /// <summary>
    /// Converts Unicode Devanagari text to Preeti / Kantipur legacy TTF encoding for broadcast rendering.
    /// </summary>
    public static string ConvertToPreeti(string unicodeText)
    {
        if (string.IsNullOrEmpty(unicodeText)) return unicodeText ?? string.Empty;
        var s = unicodeText;

        // 1. Position short-i vowel (ि) before consonant or consonant cluster
        var posI = s.IndexOf('ि');
        while (posI != -1 && posI > 0)
        {
            var charLeft = s[posI - 1];
            s = s.Remove(posI - 1, 2).Insert(posI - 1, "l" + charLeft);
            var checkPos = posI - 1;
            while (checkPos > 1 && s[checkPos - 1] == '्')
            {
                var replaceCluster = s.Substring(checkPos - 2, 2);
                s = s.Remove(checkPos - 2, 3).Insert(checkPos - 2, "l" + replaceCluster);
                checkPos -= 2;
            }
            posI = s.IndexOf('ि', posI + 1);
        }

        // 2. Position reph (र्) after the host character and its matras
        const string setOfMatras = "ािीुूृेैोौं:ँॅ";
        s += "  ";
        var posR = s.IndexOf("र्", StringComparison.Ordinal);
        while (posR >= 0)
        {
            var posZ = posR + 2;
            if (posZ < s.Length)
            {
                while (posZ < s.Length && setOfMatras.Contains(s[posZ]))
                    posZ++;
                if (posZ + 1 < s.Length && s[posZ + 1] == '्')
                {
                    posZ += 2;
                    while (posZ < s.Length && setOfMatras.Contains(s[posZ]))
                        posZ++;
                }
            }
            if (posZ > posR + 2)
            {
                var cluster = s.Substring(posR + 2, posZ - (posR + 2));
                s = s.Remove(posR, (posZ - posR)).Insert(posR, cluster + "{");
            }
            else
            {
                s = s.Remove(posR, 2).Insert(posR, "{");
            }
            posR = s.IndexOf("र्", StringComparison.Ordinal);
        }
        s = s.Substring(0, s.Length - 2);

        // 3. Sequential symbol mapping
        for (var i = 0; i < PreetiUnicodeSymbols.Length && i < PreetiAsciiSymbols.Length; i++)
        {
            var target = PreetiUnicodeSymbols[i];
            var repl = PreetiAsciiSymbols[i];
            if (target != repl && s.Contains(target))
            {
                s = s.Replace(target, repl);
            }
        }

        return s;
    }

    public static string ResolveBroadcastFormat(string selectionOrTemplate, DateTime now, bool toPreeti = false)
    {
        var raw = ResolveBroadcastFormat(selectionOrTemplate, now);
        return toPreeti ? ConvertToPreeti(raw) : raw;
    }
}

