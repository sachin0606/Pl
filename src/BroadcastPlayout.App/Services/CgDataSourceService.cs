using System.Collections.Concurrent;
using System.Drawing;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using BroadcastPlayout.Models;

namespace BroadcastPlayout.Services;

/// <summary>Loads and normalizes CG text/data sources into JSON object rows.</summary>
public static class CgDataSourceService
{
    // Fallback transition windows. Authored layer animation durations override these
    // when a category feed is built, so every category repeats the same IN/OUT motion.
    public const double CategoryIntroSeconds = 0.35;
    public const double CategoryOutroSeconds = 0.35;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (string Signature, CategoryFeedSchedule Schedule)> CategoryScheduleCache = new();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    public static async Task<IReadOnlyList<Dictionary<string, string>>> LoadAsync(CgDataSource source, CancellationToken ct = default)
    {
        var type = (source.SourceType ?? "LocalJson").Trim().ToUpperInvariant();
        var raw = source.Source?.Trim() ?? string.Empty;
        if (raw.Length == 0) throw new InvalidOperationException("Data source path/URL is empty.");

        return type switch
        {
            "URLJSON" or "URL JSON" => ParseJson(await Http.GetStringAsync(raw, ct), source.Selector),
            "LOCALJSON" or "JSON" or "LOCAL JSON" => ParseJson(await File.ReadAllTextAsync(ResolveLocalSourcePath(raw), ct), source.Selector),
            "XML" => ParseXml(await ReadTextSourceAsync(raw, ct), source.Selector, rss: false),
            "RSS" => ParseXml(await ReadTextSourceAsync(raw, ct), source.Selector, rss: true),
            "TXT" or "TEXT" => ParseText(await ReadTextSourceAsync(raw, ct)),
            "CSV" => ParseDelimited(await ReadTextSourceAsync(raw, ct), source.Delimiter, source.FirstRowHeader),
            "EXCEL" or "XLSX" => ParseXlsx(ResolveLocalSourcePath(raw), source.FirstRowHeader),
            "OPENWEATHER" or "WEATHER" => await LoadOpenWeatherAsync(source, ct).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported CG data source type '{source.SourceType}'.")
        };
    }

    public static string ResolveLocalSourcePath(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return string.Empty;
        var s = source.Trim();
        if (File.Exists(s)) return Path.GetFullPath(s);

        var candidateDirs = new List<string>
        {
            AppContext.BaseDirectory,
            Path.Combine(AppContext.BaseDirectory, "demos"),
            Path.Combine(AppContext.BaseDirectory, "cg-demo"),
            Directory.GetCurrentDirectory(),
            Path.Combine(Directory.GetCurrentDirectory(), "demos"),
            Path.Combine(Directory.GetCurrentDirectory(), "cg-demo")
        };

        var cur = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (int i = 0; i < 4 && cur != null; i++)
        {
            candidateDirs.Add(cur.FullName);
            candidateDirs.Add(Path.Combine(cur.FullName, "demos"));
            candidateDirs.Add(Path.Combine(cur.FullName, "cg-demo"));
            cur = cur.Parent;
        }

        var fileName = Path.GetFileName(s);
        foreach (var dir in candidateDirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(dir)) continue;
            var full = Path.Combine(dir, s);
            if (File.Exists(full)) return full;
            var byName = Path.Combine(dir, fileName);
            if (File.Exists(byName)) return byName;
        }

        return s;
    }


    private static async Task<IReadOnlyList<Dictionary<string, string>>> LoadOpenWeatherAsync(CgDataSource source, CancellationToken ct)
    {
        var city = string.IsNullOrWhiteSpace(source.Source) ? "Kathmandu,NP" : source.Source.Trim();
        var keyName = string.IsNullOrWhiteSpace(source.CredentialEnvironmentVariable) ? "OPENWEATHER_API_KEY" : source.CredentialEnvironmentVariable.Trim();
        var key = Environment.GetEnvironmentVariable(keyName);
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"OpenWeather API key is not configured. Set the {keyName} user environment variable (tools\\Set-OpenWeather-Key.ps1) and refresh the data source.");

        var units = (source.Units ?? "metric").Trim().ToLowerInvariant();
        if (units is not ("metric" or "imperial" or "standard")) units = "metric";
        var language = string.IsNullOrWhiteSpace(source.Language) ? "en" : source.Language.Trim();

        var geoUrl = $"https://api.openweathermap.org/geo/1.0/direct?q={Uri.EscapeDataString(city)}&limit=1&appid={Uri.EscapeDataString(key)}";
        var geoJson = await GetStringCheckedAsync(geoUrl, ct).ConfigureAwait(false);
        using var geoDoc = JsonDocument.Parse(geoJson);
        if (geoDoc.RootElement.ValueKind != JsonValueKind.Array || geoDoc.RootElement.GetArrayLength() == 0)
            throw new InvalidOperationException($"OpenWeather could not resolve city '{city}'.");
        var location = geoDoc.RootElement[0];
        var lat = location.GetProperty("lat").GetDouble();
        var lon = location.GetProperty("lon").GetDouble();
        var resolvedName = TryJsonString(location, "name") ?? city;
        var country = TryJsonString(location, "country") ?? string.Empty;
        var displayCity = string.IsNullOrWhiteSpace(country) ? resolvedName : $"{resolvedName}, {country}";

        var oneCallUrl = $"https://api.openweathermap.org/data/3.0/onecall?lat={lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}&lon={lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}&exclude=minutely,hourly,alerts&units={Uri.EscapeDataString(units)}&lang={Uri.EscapeDataString(language)}&appid={Uri.EscapeDataString(key)}";
        try
        {
            var oneCallJson = await GetStringCheckedAsync(oneCallUrl, ct).ConfigureAwait(false);
            var rows = ParseOneCallDaily(oneCallJson, displayCity, units);
            if (rows.Count >= 7) return rows.Take(7).ToArray();
        }
        catch (HttpRequestException)
        {
            // One Call 3.0 can require a separate subscription on some OpenWeather accounts.
            // Fall back to the 5-day/3-hour endpoint and make unavailable days explicit.
        }

        var forecastUrl = $"https://api.openweathermap.org/data/2.5/forecast?lat={lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}&lon={lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}&units={Uri.EscapeDataString(units)}&lang={Uri.EscapeDataString(language)}&appid={Uri.EscapeDataString(key)}";
        var forecastJson = await GetStringCheckedAsync(forecastUrl, ct).ConfigureAwait(false);
        return ParseFiveDayForecast(forecastJson, displayCity, units);
    }

    private static async Task<string> GetStringCheckedAsync(string url, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string detail = string.Empty;
            try
            {
                using var doc = JsonDocument.Parse(body);
                detail = TryJsonString(doc.RootElement, "message") ?? string.Empty;
            }
            catch { }
            throw new HttpRequestException($"OpenWeather HTTP {(int)response.StatusCode}{(detail.Length > 0 ? ": " + detail : string.Empty)}");
        }
        return body;
    }

    private static IReadOnlyList<Dictionary<string, string>> ParseOneCallDaily(string json, string city, string units)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var timezoneOffset = root.TryGetProperty("timezone_offset", out var tz) && tz.TryGetInt32(out var offset) ? offset : 0;
        if (!root.TryGetProperty("daily", out var daily) || daily.ValueKind != JsonValueKind.Array) return [];
        var rows = new List<Dictionary<string, string>>();
        foreach (var day in daily.EnumerateArray().Take(7))
        {
            var dt = day.TryGetProperty("dt", out var dtNode) && dtNode.TryGetInt64(out var seconds) ? seconds : 0;
            var local = DateTimeOffset.FromUnixTimeSeconds(dt).ToOffset(TimeSpan.FromSeconds(timezoneOffset));
            var weather = day.TryGetProperty("weather", out var wa) && wa.ValueKind == JsonValueKind.Array && wa.GetArrayLength() > 0 ? wa[0] : default;
            var temp = day.TryGetProperty("temp", out var t) ? t : default;
            var min = temp.ValueKind == JsonValueKind.Object && temp.TryGetProperty("min", out var tmin) ? tmin.GetDouble() : double.NaN;
            var max = temp.ValueKind == JsonValueKind.Object && temp.TryGetProperty("max", out var tmax) ? tmax.GetDouble() : double.NaN;
            var dayTemp = temp.ValueKind == JsonValueKind.Object && temp.TryGetProperty("day", out var tday) ? tday.GetDouble() : (double.IsNaN(max) || double.IsNaN(min) ? double.NaN : (min + max) / 2);
            var feels = day.TryGetProperty("feels_like", out var fl) && fl.ValueKind == JsonValueKind.Object && fl.TryGetProperty("day", out var fld) ? fld.GetDouble() : dayTemp;
            var humidity = day.TryGetProperty("humidity", out var hum) ? hum.GetInt32() : 0;
            var pressure = day.TryGetProperty("pressure", out var pr) ? pr.GetDouble() : 1013.0;
            var clouds = day.TryGetProperty("clouds", out var cl) ? cl.GetDouble() : 0.0;
            var wind = day.TryGetProperty("wind_speed", out var ws) ? ws.GetDouble() : 0;
            var pop = day.TryGetProperty("pop", out var popNode) ? popNode.GetDouble() * 100.0 : 0;
            rows.Add(WeatherRow(city, local, units, dayTemp, min, max, feels, humidity, pressure, clouds, wind, pop,
                weather.ValueKind == JsonValueKind.Object ? TryJsonString(weather, "main") : null,
                weather.ValueKind == JsonValueKind.Object ? TryJsonString(weather, "description") : null,
                weather.ValueKind == JsonValueKind.Object ? TryJsonString(weather, "icon") : null));
        }
        return rows;
    }

    private static IReadOnlyList<Dictionary<string, string>> ParseFiveDayForecast(string json, string city, string units)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var timezoneOffset = 0;
        if (root.TryGetProperty("city", out var cityNode) && cityNode.ValueKind == JsonValueKind.Object && cityNode.TryGetProperty("timezone", out var tz) && tz.TryGetInt32(out var off)) timezoneOffset = off;
        if (!root.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array) return [];

        var samples = new List<WeatherSample>();
        foreach (var item in list.EnumerateArray())
        {
            var dt = item.TryGetProperty("dt", out var dtNode) && dtNode.TryGetInt64(out var seconds) ? seconds : 0;
            var local = DateTimeOffset.FromUnixTimeSeconds(dt).ToOffset(TimeSpan.FromSeconds(timezoneOffset));
            var mainNode = item.TryGetProperty("main", out var mn) ? mn : default;
            var temp = mainNode.ValueKind == JsonValueKind.Object && mainNode.TryGetProperty("temp", out var tn) ? tn.GetDouble() : double.NaN;
            var min = mainNode.ValueKind == JsonValueKind.Object && mainNode.TryGetProperty("temp_min", out var minNode) ? minNode.GetDouble() : temp;
            var max = mainNode.ValueKind == JsonValueKind.Object && mainNode.TryGetProperty("temp_max", out var maxNode) ? maxNode.GetDouble() : temp;
            var feels = mainNode.ValueKind == JsonValueKind.Object && mainNode.TryGetProperty("feels_like", out var fln) ? fln.GetDouble() : temp;
            var humidity = mainNode.ValueKind == JsonValueKind.Object && mainNode.TryGetProperty("humidity", out var hn) ? hn.GetInt32() : 0;
            var pressure = mainNode.ValueKind == JsonValueKind.Object && mainNode.TryGetProperty("pressure", out var prn) ? prn.GetDouble() : 1013.0;
            var cloudsNode = item.TryGetProperty("clouds", out var cln) ? cln : default;
            var clouds = cloudsNode.ValueKind == JsonValueKind.Object && cloudsNode.TryGetProperty("all", out var can) ? can.GetDouble() : 0.0;
            var windNode = item.TryGetProperty("wind", out var wn) ? wn : default;
            var wind = windNode.ValueKind == JsonValueKind.Object && windNode.TryGetProperty("speed", out var sn) ? sn.GetDouble() : 0;
            var pop = item.TryGetProperty("pop", out var pn) ? pn.GetDouble() * 100.0 : 0;
            var weather = item.TryGetProperty("weather", out var wa) && wa.ValueKind == JsonValueKind.Array && wa.GetArrayLength() > 0 ? wa[0] : default;
            samples.Add(new WeatherSample(local, temp, min, max, feels, humidity, pressure, clouds, wind, pop,
                weather.ValueKind == JsonValueKind.Object ? TryJsonString(weather, "main") ?? string.Empty : string.Empty,
                weather.ValueKind == JsonValueKind.Object ? TryJsonString(weather, "description") ?? string.Empty : string.Empty,
                weather.ValueKind == JsonValueKind.Object ? TryJsonString(weather, "icon") ?? "na" : "na"));
        }

        var rows = new List<Dictionary<string, string>>();
        foreach (var group in samples.GroupBy(x => x.Local.Date).OrderBy(x => x.Key).Take(7))
        {
            var all = group.ToArray();
            var representative = all.OrderBy(x => Math.Abs(x.Local.Hour - 12)).First();
            rows.Add(WeatherRow(city, representative.Local, units,
                all.Where(x => !double.IsNaN(x.Temp)).Select(x => x.Temp).DefaultIfEmpty(double.NaN).Average(),
                all.Where(x => !double.IsNaN(x.Min)).Select(x => x.Min).DefaultIfEmpty(double.NaN).Min(),
                all.Where(x => !double.IsNaN(x.Max)).Select(x => x.Max).DefaultIfEmpty(double.NaN).Max(),
                all.Where(x => !double.IsNaN(x.FeelsLike)).Select(x => x.FeelsLike).DefaultIfEmpty(double.NaN).Average(),
                (int)Math.Round(all.Select(x => x.Humidity).DefaultIfEmpty(0).Average()),
                all.Select(x => x.Pressure).DefaultIfEmpty(1013.0).Average(),
                all.Select(x => x.Clouds).DefaultIfEmpty(0.0).Average(),
                all.Select(x => x.Wind).DefaultIfEmpty(0).Average(),
                all.Select(x => x.Pop).DefaultIfEmpty(0).Max(), representative.Main, representative.Description, representative.Icon));
        }
        while (rows.Count < 7)
        {
            var date = (rows.Count == 0 ? DateTimeOffset.Now : DateTimeOffset.Now.AddDays(rows.Count));
            rows.Add(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["city"] = city, ["cityName"] = city, ["day"] = date.ToString("ddd").ToUpperInvariant(), ["date"] = date.ToString("dd MMM"),
                ["temp"] = "--", ["temperature"] = "--", ["tempMin"] = "--", ["min"] = "--", ["tempMax"] = "--", ["max"] = "--",
                ["feelsLike"] = "--", ["feels_like"] = "--", ["humidity"] = "--", ["pressure"] = "1013 hPa", ["clouds"] = "0%",
                ["wind"] = "--", ["windSpeed"] = "--", ["pop"] = "--", ["condition"] = "Unavailable",
                ["description"] = "Forecast unavailable", ["icon"] = "01d",
                ["weatherImage"] = "https://openweathermap.org/img/wn/01d@2x.png",
                ["weather_image"] = "https://openweathermap.org/img/wn/01d@2x.png"
            });
        }
        return rows.Take(7).ToArray();
    }

    private static Dictionary<string, string> WeatherRow(
        string city,
        DateTimeOffset local,
        string units,
        double temp,
        double min,
        double max,
        double feelsLike,
        int humidity,
        double pressure,
        double clouds,
        double wind,
        double pop,
        string? condition,
        string? description,
        string? icon)
    {
        var degree = units == "imperial" ? "°F" : units == "standard" ? "K" : "°C";
        var windValue = units == "imperial" ? wind * 2.2369362920544 : wind * 3.6;
        var windUnit = units == "imperial" ? "mph" : "km/h";
        string F(double value) => double.IsNaN(value) ? "--" : $"{Math.Round(value):0}{degree}";
        var iconCode = string.IsNullOrWhiteSpace(icon) || icon == "na" ? "01d" : icon;
        var iconUrl = $"https://openweathermap.org/img/wn/{iconCode}@2x.png";

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["city"] = city,
            ["cityName"] = city,
            ["day"] = local.ToString("ddd").ToUpperInvariant(),
            ["date"] = local.ToString("dd MMM"),
            ["temp"] = F(temp),
            ["temperature"] = F(temp),
            ["tempMin"] = F(min),
            ["min"] = F(min),
            ["tempMax"] = F(max),
            ["max"] = F(max),
            ["feelsLike"] = F(double.IsNaN(feelsLike) ? temp : feelsLike),
            ["feels_like"] = F(double.IsNaN(feelsLike) ? temp : feelsLike),
            ["humidity"] = $"{humidity}%",
            ["pressure"] = double.IsNaN(pressure) || pressure <= 0 ? "1013 hPa" : $"{Math.Round(pressure):0} hPa",
            ["clouds"] = $"{Math.Clamp(clouds, 0, 100):0}%",
            ["wind"] = $"{windValue:0.#} {windUnit}",
            ["windSpeed"] = $"{windValue:0.#} {windUnit}",
            ["pop"] = $"{Math.Clamp(pop, 0, 100):0}%",
            ["condition"] = string.IsNullOrWhiteSpace(condition) ? "Weather" : condition,
            ["description"] = string.IsNullOrWhiteSpace(description) ? "Forecast" : description,
            ["icon"] = iconCode,
            ["weatherImage"] = iconUrl,
            ["weather_image"] = iconUrl
        };
    }

    private static string? TryJsonString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var node)) return null;
        return node.ValueKind == JsonValueKind.String ? node.GetString() : node.ToString();
    }

    private sealed record WeatherSample(DateTimeOffset Local, double Temp, double Min, double Max, double FeelsLike, int Humidity, double Pressure, double Clouds, double Wind, double Pop, string Main, string Description, string Icon);

    public static string SerializeRows(IReadOnlyList<Dictionary<string, string>> rows) => JsonSerializer.Serialize(rows);

    private static Task<string> ReadTextSourceAsync(string source, CancellationToken ct) =>
        source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? Http.GetStringAsync(source, ct)
            : File.ReadAllTextAsync(ResolveLocalSourcePath(source), ct);

    public static IReadOnlyList<Dictionary<string, string>> ReadCachedRows(CgDataSource source)
    {
        if (string.IsNullOrWhiteSpace(source.CachedItemsJson)) return [];
        try
        {
            var parsed = ParseJson(source.CachedItemsJson, source.Selector);
            if (parsed.Count > 0) return parsed;
        }
        catch { }
        try { return JsonSerializer.Deserialize<List<Dictionary<string, string>>>(source.CachedItemsJson) ?? []; }
        catch { return []; }
    }

    public static double ResolveEffectiveItemDuration(CgProject project, CgLayer layer)
    {
        if (layer.DataItemDurationSeconds > 0.05) return layer.DataItemDurationSeconds;
        if (layer.GroupId != Guid.Empty && project.Layers != null)
        {
            var sibling = project.Layers.FirstOrDefault(x => x.GroupId == layer.GroupId && x.DataItemDurationSeconds > 0.05);
            if (sibling != null) return sibling.DataItemDurationSeconds;
        }
        if (layer.DataSourceId != Guid.Empty && project.Layers != null)
        {
            var sibling = project.Layers.FirstOrDefault(x => x.DataSourceId == layer.DataSourceId && x.DataItemDurationSeconds > 0.05);
            if (sibling != null) return sibling.DataItemDurationSeconds;
        }
        return project.DurationSeconds > 0.05 ? project.DurationSeconds : 4.0;
    }

    public static string ResolveLayerText(CgProject project, CgLayer layer, double timelineSeconds)
    {
        if (layer.DataSourceId == Guid.Empty) return layer.Text ?? string.Empty;
        var source = project.DataSources?.FirstOrDefault(x => x.Id == layer.DataSourceId);
        if (source is null) return layer.Text ?? string.Empty;
        var rows = CgDataRuntime.Shared.GetRows(source);
        if (rows.Count == 0) return layer.Text ?? string.Empty;

        var distinctCategories = rows.Select(r => ResolveField(r, "category"))
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinctCategories.Count == 0)
        {
            distinctCategories = rows.Select(r => ResolveField(r, "badge"))
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var isCategoryBadge = string.Equals(layer.DataField, "category", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(layer.Role, "Badge", StringComparison.OrdinalIgnoreCase) ||
                              (layer.Name?.Contains("Badge", StringComparison.OrdinalIgnoreCase) ?? false) ||
                              (layer.Name?.Contains("Category", StringComparison.OrdinalIgnoreCase) ?? false && !string.Equals(layer.Type, "Ticker", StringComparison.OrdinalIgnoreCase));

        var isTickerLayer = string.Equals(layer.Type, "Ticker", StringComparison.OrdinalIgnoreCase);

        // 1. Category feed handling: each category plays completely one at a time (from right to left out of body)
        if ((isCategoryBadge || isTickerLayer) && (distinctCategories.Count > 0 || layer.TickerCategoriesEnabled))
        {
            var schedule = BuildCategoryFeedSchedule(project, layer, rows);
            var (activeCat, localElapsed, catIdx) = schedule.Evaluate(Math.Max(0, timelineSeconds));

            if (isCategoryBadge)
            {
                var catText = activeCat.Category;
                try { return string.Format(layer.DataFormat ?? "{0}", catText); } catch { return catText; }
            }

            if (isTickerLayer)
            {
                var hasDedicatedCategory = project.Layers?.Any(x => x.Visible && x.Id != layer.Id &&
                    (string.Equals(x.DataField, "category", StringComparison.OrdinalIgnoreCase) ||
                     (x.Name?.Contains("Badge", StringComparison.OrdinalIgnoreCase) ?? false))) == true;

                if (layer.TickerCategoriesEnabled && !hasDedicatedCategory && !string.IsNullOrWhiteSpace(activeCat.Category))
                {
                    return $"[{activeCat.Category}] {activeCat.CombinedItemsText}";
                }
                return activeCat.CombinedItemsText;
            }
        }
        else if (isTickerLayer)
        {
            // Non-category continuous ticker: stream all news items cleanly
            var schedule = BuildCategoryFeedSchedule(project, layer, rows);
            var (activeCat, localElapsed, catIdx) = schedule.Evaluate(Math.Max(0, timelineSeconds));
            return activeCat.CombinedItemsText;
        }

        var itemDuration = ResolveEffectiveItemDuration(project, layer);

        // Headline 4-line showcase handling: e.g. 20 items in feed, show 4 items each page!
        int lineSlot = -1;
        if (!string.IsNullOrWhiteSpace(layer.DataField))
        {
            var df = layer.DataField.Trim().ToLowerInvariant();
            if (df == "line1" || df == "item1" || df == "row1" || df == "0" || df == "1") lineSlot = 0;
            else if (df == "line2" || df == "item2" || df == "row2" || df == "2") lineSlot = 1;
            else if (df == "line3" || df == "item3" || df == "row3" || df == "3") lineSlot = 2;
            else if (df == "line4" || df == "item4" || df == "row4" || df == "4") lineSlot = 3;
            else if (df.StartsWith("line") && int.TryParse(df[4..], out var lNum)) lineSlot = lNum - 1;
        }
        if (lineSlot < 0 && !string.IsNullOrWhiteSpace(layer.Name))
        {
            var nm = layer.Name.Trim().ToLowerInvariant();
            if (nm.Contains("line 1") || nm.Contains("line1") || nm.Contains("headline 1") || nm.Contains("item 1")) lineSlot = 0;
            else if (nm.Contains("line 2") || nm.Contains("line2") || nm.Contains("headline 2") || nm.Contains("item 2")) lineSlot = 1;
            else if (nm.Contains("line 3") || nm.Contains("line3") || nm.Contains("headline 3") || nm.Contains("item 3")) lineSlot = 2;
            else if (nm.Contains("line 4") || nm.Contains("line4") || nm.Contains("headline 4") || nm.Contains("item 4")) lineSlot = 3;
        }

        if (lineSlot >= 0)
        {
            // Case 1: Multiple rows (e.g. 20 json items)
            if (rows.Count > 1)
            {
                var activeTimeline = Math.Max(0, timelineSeconds - layer.StartSeconds);
                // If row has the exact DataField directly (and it's not generic lineN on multi-row)
                if (!string.IsNullOrWhiteSpace(layer.DataField) && rows[0].ContainsKey(layer.DataField) && !layer.DataField.StartsWith("line", StringComparison.OrdinalIgnoreCase))
                {
                    var directRowIndex = (int)Math.Floor(activeTimeline / Math.Max(.05, itemDuration)) + layer.DataItemOffset;
                    directRowIndex = ((directRowIndex % rows.Count) + rows.Count) % rows.Count;
                    var val = ResolveField(rows[directRowIndex], layer.DataField);
                    try { return string.Format(layer.DataFormat ?? "{0}", val); } catch { return val; }
                }

                // Show items per page cycling across all rows (dynamically sized based on authored lines)
                var projectLineSlots = project.Layers?.Count(x => x.DataSourceId == layer.DataSourceId && (x.DataField.StartsWith("line", StringComparison.OrdinalIgnoreCase) || x.DataField.StartsWith("item", StringComparison.OrdinalIgnoreCase) || x.Name.Contains("Line") || x.Name.Contains("Headline"))) ?? 0;
                var pageSize = projectLineSlots > 0 ? projectLineSlots : 4;
                var totalPages = (int)Math.Max(1, Math.Ceiling(rows.Count / (double)pageSize));
                var pageIndex = (int)Math.Floor(activeTimeline / Math.Max(.05, itemDuration));
                pageIndex = ((pageIndex % totalPages) + totalPages) % totalPages;
                var targetIndex = (pageIndex * pageSize + lineSlot) % rows.Count;
                var r = rows[targetIndex];

                var headline = ResolveField(r, layer.DataField);
                if (string.IsNullOrWhiteSpace(headline) || headline.Equals(layer.DataField, StringComparison.OrdinalIgnoreCase)) headline = ResolveField(r, "headline");
                if (string.IsNullOrWhiteSpace(headline)) headline = ResolveField(r, "title");
                if (string.IsNullOrWhiteSpace(headline)) headline = ResolveField(r, "text");
                if (string.IsNullOrWhiteSpace(headline)) headline = r.Values.FirstOrDefault() ?? string.Empty;

                var prefix = $"{lineSlot + 1} • ";
                if (!string.IsNullOrWhiteSpace(headline) && !headline.StartsWith(prefix) && !headline.StartsWith($"{lineSlot + 1}."))
                    headline = prefix + headline;

                try { return string.Format(layer.DataFormat ?? "{0}", headline); } catch { return headline; }
            }
            else if (rows.Count == 1)
            {
                var r = rows[0];
                var totalNumberedLines = r.Keys.Count(k => k.StartsWith("line", StringComparison.OrdinalIgnoreCase) || k.StartsWith("item", StringComparison.OrdinalIgnoreCase));
                if (totalNumberedLines > 4)
                {
                    const int pageSize = 4;
                    var totalPages = (int)Math.Max(1, Math.Ceiling(totalNumberedLines / (double)pageSize));
                    var pageIndex = (int)Math.Floor(Math.Max(0, timelineSeconds) / Math.Max(.05, itemDuration));
                    pageIndex = ((pageIndex % totalPages) + totalPages) % totalPages;
                    var targetLineNum = pageIndex * pageSize + lineSlot + 1;
                    var key = $"line{targetLineNum}";
                    if (!r.ContainsKey(key)) key = $"item{targetLineNum}";
                    if (r.TryGetValue(key, out var lineVal) && !string.IsNullOrWhiteSpace(lineVal))
                    {
                        var prefix = $"{lineSlot + 1} • ";
                        if (!lineVal.StartsWith(prefix) && !lineVal.StartsWith($"{lineSlot + 1}."))
                            lineVal = prefix + lineVal;
                        try { return string.Format(layer.DataFormat ?? "{0}", lineVal); } catch { return lineVal; }
                    }
                }

                // Standard single row with line1, line2, line3, line4
                var standardVal = ResolveField(r, layer.DataField);
                if (string.IsNullOrEmpty(standardVal)) standardVal = r.Values.ElementAtOrDefault(lineSlot) ?? layer.Text ?? string.Empty;
                var stdPrefix = $"{lineSlot + 1} • ";
                if (!string.IsNullOrWhiteSpace(standardVal) && !standardVal.StartsWith(stdPrefix) && !standardVal.StartsWith($"{lineSlot + 1}."))
                    standardVal = stdPrefix + standardVal;
                try { return string.Format(layer.DataFormat ?? "{0}", standardVal); } catch { return standardVal; }
            }
        }

        if (string.Equals(layer.DataField, "category", StringComparison.OrdinalIgnoreCase))
        {
            var schedule = BuildCategoryFeedSchedule(project, layer, rows);
            var (activeCat, localElapsed, catIdx) = schedule.Evaluate(Math.Max(0, timelineSeconds));
            var catVal = activeCat.Category;
            try { return string.Format(layer.DataFormat ?? "{0}", catVal); } catch { return catVal; }
        }

        var activeSeconds = Math.Max(0, timelineSeconds - layer.StartSeconds);
        var index = (int)Math.Floor(activeSeconds / Math.Max(.05, itemDuration)) + layer.DataItemOffset;
        index = ((index % rows.Count) + rows.Count) % rows.Count;
        var row = rows[index];
        var value = ResolveField(row, layer.DataField);
        if (string.IsNullOrEmpty(value)) value = row.Values.FirstOrDefault() ?? layer.Text ?? string.Empty;
        try { return string.Format(layer.DataFormat ?? "{0}", value); }
        catch { return value; }
    }

    public static int ResolveDataItemIndex(CgProject project, CgLayer layer, double timelineSeconds)
    {
        if (layer.DataSourceId == Guid.Empty) return 0;
        var source = project.DataSources?.FirstOrDefault(x => x.Id == layer.DataSourceId);
        if (source is null) return 0;
        var rows = CgDataRuntime.Shared.GetRows(source);
        if (rows.Count == 0) return 0;
        var itemDuration = ResolveEffectiveItemDuration(project, layer);
        var i = (int)Math.Floor(Math.Max(0, timelineSeconds - layer.StartSeconds) / Math.Max(.05, itemDuration)) + layer.DataItemOffset;
        return ((i % rows.Count) + rows.Count) % rows.Count;
    }

    public static int ResolveSequenceFrameCount(CgLayer layer)
    {
        var files = SequenceFiles(layer.Source);
        if (files.Length == 0) return 0;
        var (start, end) = ResolveSequenceBounds(layer, files);
        return end - start + 1;
    }

    public static int ResolveEffectiveOverlayInterval(CgProject project, CgLayer layer)
    {
        if (layer.SequenceOverlayIntervalItems > 1) return layer.SequenceOverlayIntervalItems;
        if (layer.GroupId != Guid.Empty && project.Layers != null)
        {
            var sibling = project.Layers.FirstOrDefault(x => x.GroupId == layer.GroupId && x.SequenceOverlayIntervalItems > 1);
            if (sibling != null) return sibling.SequenceOverlayIntervalItems;
        }
        if (layer.DataSourceId != Guid.Empty && project.Layers != null)
        {
            var sibling = project.Layers.FirstOrDefault(x => x.DataSourceId == layer.DataSourceId && x.SequenceOverlayIntervalItems > 1);
            if (sibling != null) return sibling.SequenceOverlayIntervalItems;
        }
        return 1;
    }

    public static double ResolveEffectiveOverlayDuration(CgProject project, CgLayer layer)
    {
        if (project.Layers is null) return 1.2;
        var overlay = project.Layers.FirstOrDefault(x =>
            x.Visible &&
            (string.Equals(x.Type, "Sequence", StringComparison.OrdinalIgnoreCase) ||
             (x.Name?.Contains("Overlay", StringComparison.OrdinalIgnoreCase) ?? false) ||
             (x.Name?.Contains("Wipe", StringComparison.OrdinalIgnoreCase) ?? false) ||
             (x.Role?.Contains("Overlay", StringComparison.OrdinalIgnoreCase) ?? false)) &&
            (x.GroupId == layer.GroupId || x.DataSourceId == layer.DataSourceId || layer.SequenceOverlayIntervalItems > 1 || x.SequenceOverlayIntervalItems > 1));

        if (overlay is not null)
        {
            var frameCount = ResolveSequenceFrameCount(overlay);
            if (frameCount > 0)
                return frameCount / (double)Math.Max(1, overlay.SequenceFps);
        }
        return 1.2;
    }

    public static bool IsOverlayActiveForItem(CgProject project, CgLayer layer, double timelineSeconds)
    {
        var overlay = project.Layers?.FirstOrDefault(x => x.Visible && string.Equals(x.Type, "ImageSequence", StringComparison.OrdinalIgnoreCase) && x.SequenceAdvanceDataItem);
        if (overlay == null && !layer.SequenceAdvanceDataItem)
        {
            return false;
        }

        var interval = ResolveEffectiveOverlayInterval(project, layer);
        // Interval 1 deliberately means every data item.  Do not derive this from
        // row count: that changes the editor/default setting into "once per feed".
        if (interval <= 1) return true;
        var itemDuration = ResolveEffectiveItemDuration(project, layer);
        var refStart = overlay?.StartSeconds ?? layer.StartSeconds;
        var itemCycle = (int)Math.Floor(Math.Max(0, timelineSeconds - refStart) / Math.Max(0.05, itemDuration));
        return (itemCycle % interval) == 0;
    }

    private static List<string> ExtractNewsItemsFromRow(Dictionary<string, string> row, string? preferredField)
    {
        var result = new List<string>();

        void AddClean(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            var s = raw.Trim().Trim('•', '|', '★', '-', '—', '"', '\'').Trim();
            if (!string.IsNullOrWhiteSpace(s) && !result.Contains(s, StringComparer.OrdinalIgnoreCase))
                result.Add(s);
        }

        void ParseAndAdd(string? val)
        {
            if (string.IsNullOrWhiteSpace(val)) return;
            var s = val.Trim();
            if (s.StartsWith('[') && s.EndsWith(']'))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(s);
                    if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var el in doc.RootElement.EnumerateArray())
                        {
                            if (el.ValueKind == System.Text.Json.JsonValueKind.String)
                                AddClean(el.GetString());
                            else if (el.ValueKind == System.Text.Json.JsonValueKind.Object)
                            {
                                if (el.TryGetProperty("headline", out var hp) || el.TryGetProperty("title", out hp) ||
                                    el.TryGetProperty("text", out hp) || el.TryGetProperty("item", out hp) ||
                                    el.TryGetProperty("news", out hp))
                                    AddClean(hp.GetString());
                            }
                        }
                        if (result.Count > 0) return;
                    }
                }
                catch { }
            }

            var parts = s.Split(['•', '|', '\n', '★'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var p in parts)
            {
                AddClean(p);
            }
        }

        // 1. Check preferredField if specified (unless generic category or badge)
        if (!string.IsNullOrWhiteSpace(preferredField) &&
            !string.Equals(preferredField, "category", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(preferredField, "badge", StringComparison.OrdinalIgnoreCase) &&
            row.TryGetValue(preferredField, out var prefVal) && !string.IsNullOrWhiteSpace(prefVal))
        {
            ParseAndAdd(prefVal);
            if (result.Count > 0) return result;
        }

        // 2. Check well-known collection keys first: items, news, headlines, lines
        foreach (var key in new[] { "items", "news", "headlines", "lines" })
        {
            if (row.TryGetValue(key, out var collVal) && !string.IsNullOrWhiteSpace(collVal))
            {
                ParseAndAdd(collVal);
                if (result.Count > 0) return result;
            }
        }

        // 3. Check for numbered items: item1, item2... headline1, headline2...
        var numberedKeys = row.Keys
            .Where(k => System.Text.RegularExpressions.Regex.IsMatch(k, @"^(item|headline|news|line|title)\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (numberedKeys.Count > 0)
        {
            foreach (var k in numberedKeys)
                ParseAndAdd(row[k]);
            if (result.Count > 0) return result;
        }

        // 4. Check standard single fields: brk_text, top, item, text, headline, title
        foreach (var key in new[] { "brk_text", "top", "item", "text", "headline", "title", "description", "summary", "body", "story", "breaking" })
        {
            if (row.TryGetValue(key, out var fVal) && !string.IsNullOrWhiteSpace(fVal))
            {
                ParseAndAdd(fVal);
                if (result.Count > 0) return result;
            }
        }

        // 5. Fallback: all non-category values
        foreach (var kv in row)
        {
            if (kv.Key.Equals("category", StringComparison.OrdinalIgnoreCase) ||
                kv.Key.Equals("badge", StringComparison.OrdinalIgnoreCase) ||
                kv.Key.Equals("id", StringComparison.OrdinalIgnoreCase))
                continue;
            ParseAndAdd(kv.Value);
        }

        return result;
    }

    private static readonly ConcurrentDictionary<string, string[]> _sequenceFilesCache = new(StringComparer.OrdinalIgnoreCase);

    public static string ResolveSequenceFrame(CgProject project, CgLayer layer, double timelineSeconds)
    {
        if (layer.SequenceAdvanceDataItem && !IsOverlayActiveForItem(project, layer, timelineSeconds))
        {
            return string.Empty;
        }

        var files = SequenceFiles(layer.Source);
        if (files.Length == 0) return string.Empty;
        var (start, end) = ResolveSequenceBounds(layer, files);
        var count = Math.Max(1, end - start + 1);
        var fps = Math.Max(1, layer.SequenceFps);

        double local;
        if (layer.SequenceAdvanceDataItem)
        {
            var itemDuration = ResolveEffectiveItemDuration(project, layer);
            local = (timelineSeconds - layer.StartSeconds) % Math.Max(.001, itemDuration);
            if (local < 0) local += itemDuration;
        }
        else
        {
            if (timelineSeconds < layer.StartSeconds) return string.Empty;
            local = timelineSeconds - layer.StartSeconds;
            if (layer.EndSeconds > layer.StartSeconds && timelineSeconds > layer.EndSeconds && !layer.SequenceLoop && !layer.SequenceHoldLastFrame)
                return string.Empty;
        }

        var totalFrame = (int)Math.Floor(local * fps);
        int frame;
        if (layer.SequenceLoop)
        {
            // Check if project or layer has data source items to cycle through (e.g. Breaking Screen)
            var dsLayer = project.Layers?.FirstOrDefault(x => x.DataSourceId != Guid.Empty);
            var ds = dsLayer != null ? project.DataSources?.FirstOrDefault(x => x.Id == dsLayer.DataSourceId) : project.DataSources?.FirstOrDefault();
            var rows = ds != null ? CgDataRuntime.Shared.GetRows(ds) : null;
            var hasJsonCycle = rows != null && rows.Count > 0;
            var itemDur = hasJsonCycle ? ResolveEffectiveItemDuration(project, dsLayer ?? layer) : 0;
            var introDuration = layer.SequenceLoopStartSeconds >= 0
                ? layer.SequenceLoopStartSeconds
                : layer.SequenceLoopStartFrame > layer.SequenceStartFrame
                    ? (layer.SequenceLoopStartFrame - layer.SequenceStartFrame) / fps
                    : 0;
            var fullCycleDuration = hasJsonCycle ? (introDuration + rows!.Count * itemDur) : 0;

            double cycleLocal = local;
            if (fullCycleDuration > 0.05)
            {
                // After all JSON items complete one full cycle, restart PNG from
                // the very beginning (intro frame 0). The intro plays once at the
                // start of each data cycle, then the loop section fills the rest.
                cycleLocal = local % fullCycleDuration;
            }

            int loopStartFrame = 0;
            int loopEndFrame = count - 1;

            if (layer.SequenceLoopStartSeconds >= 0)
                loopStartFrame = Math.Clamp((int)Math.Round(layer.SequenceLoopStartSeconds * fps) - start, 0, count - 1);
            else if (layer.SequenceLoopStartFrame >= layer.SequenceStartFrame && layer.SequenceLoopStartFrame > 0)
                loopStartFrame = Math.Clamp(layer.SequenceLoopStartFrame - start, 0, count - 1);

            if (layer.SequenceLoopEndSeconds > 0)
                loopEndFrame = Math.Clamp((int)Math.Round(layer.SequenceLoopEndSeconds * fps) - start, loopStartFrame, count - 1);
            else if (layer.SequenceEndFrame >= layer.SequenceStartFrame && layer.SequenceEndFrame > 0)
                loopEndFrame = Math.Clamp(layer.SequenceEndFrame - start, loopStartFrame, count - 1);

            var cycleTotalFrame = (int)Math.Floor(cycleLocal * fps);
            if (loopStartFrame > 0 && cycleTotalFrame < loopStartFrame)
            {
                // Still in the intro portion — play sequentially from frame 0
                frame = cycleTotalFrame;
            }
            else
            {
                var loopLen = Math.Max(1, loopEndFrame - loopStartFrame + 1);
                frame = loopStartFrame + ((cycleTotalFrame - loopStartFrame) % loopLen);
            }
        }
        else if (totalFrame >= count)
        {
            if (!layer.SequenceHoldLastFrame) return string.Empty;
            frame = count - 1;
        }
        else
        {
            frame = totalFrame;
        }
        return files[start + Math.Clamp(frame, 0, count - 1)];
    }

    private static (int Start, int End) ResolveSequenceBounds(CgLayer layer, string[] files)
    {
        var start = Math.Clamp(layer.SequenceStartFrame, 0, files.Length - 1);
        if (layer.SequenceStartFrame > 0)
        {
            var byNumber = Array.FindIndex(files, x => NaturalFrameNumber(x) == layer.SequenceStartFrame);
            if (byNumber >= 0) start = byNumber;
        }
        var end = layer.SequenceEndFrame < 0 ? files.Length - 1 : Math.Clamp(layer.SequenceEndFrame, start, files.Length - 1);
        if (layer.SequenceEndFrame >= 0)
        {
            var byNumber = Array.FindIndex(files, x => NaturalFrameNumber(x) == layer.SequenceEndFrame);
            if (byNumber >= start) end = byNumber;
        }
        return (start, Math.Max(start, end));
    }

    private static string[] SequenceFiles(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return [];
        if (_sequenceFilesCache.TryGetValue(folder, out var cached) && cached.Length > 0) return cached;

        var targetFolder = folder;
        if (!Directory.Exists(targetFolder))
        {
            var folderName = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var normalizedSource = folder.Replace('\\', '/');

            // Preserve the authored channel family when rebasing a stale absolute
            // path. A name such as BreakingNews exists in several template packs;
            // blindly probing another pack can silently show the wrong sequence.
            if (normalizedSource.Contains("/ap1hd/", StringComparison.OrdinalIgnoreCase))
            {
                var ap1Candidate = CgUniqueDemoFactory.ResolveAp1hdPath(folderName);
                if (Directory.Exists(ap1Candidate)) targetFolder = ap1Candidate;
            }
            else if (normalizedSource.Contains("/space4k/", StringComparison.OrdinalIgnoreCase))
            {
                var spaceCandidate = CgUniqueDemoFactory.ResolveSpace4kPath(folderName);
                if (Directory.Exists(spaceCandidate)) targetFolder = spaceCandidate;
            }
            else if (normalizedSource.Contains("/prime/", StringComparison.OrdinalIgnoreCase))
            {
                var primeCandidate = CgUniqueDemoFactory.ResolvePrimePath(folderName);
                if (Directory.Exists(primeCandidate)) targetFolder = primeCandidate;
            }
            else
            {
                var ap1Candidate = CgUniqueDemoFactory.ResolveAp1hdPath(folderName);
                var primeCandidate = CgUniqueDemoFactory.ResolvePrimePath(folderName);
                var spaceCandidate = CgUniqueDemoFactory.ResolveSpace4kPath(folderName);
                if (Directory.Exists(ap1Candidate)) targetFolder = ap1Candidate;
                else if (Directory.Exists(primeCandidate)) targetFolder = primeCandidate;
                else if (Directory.Exists(spaceCandidate)) targetFolder = spaceCandidate;
            }
        }
        if (!Directory.Exists(targetFolder)) return [];
        try
        {
            var result = Directory.EnumerateFiles(targetFolder)
                .Where(x => new[] { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff" }.Contains(Path.GetExtension(x), StringComparer.OrdinalIgnoreCase))
                .OrderBy(NaturalFrameNumber).ThenBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            _sequenceFilesCache[folder] = result;
            if (!string.Equals(folder, targetFolder, StringComparison.OrdinalIgnoreCase))
                _sequenceFilesCache[targetFolder] = result;
            return result;
        }
        catch { return []; }
    }

    private static long NaturalFrameNumber(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var digits = new string(name.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        return long.TryParse(digits, out var n) ? n : long.MaxValue;
    }

    public static string ResolveField(Dictionary<string, string> row, string? field)
    {
        if (string.IsNullOrWhiteSpace(field))
        {
            var nonIdFirst = row.FirstOrDefault(x => !x.Key.Equals("id", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(x.Value));
            return nonIdFirst.Value ?? row.Values.FirstOrDefault() ?? string.Empty;
        }
        if (row.TryGetValue(field, out var direct) && !string.IsNullOrWhiteSpace(direct)) return direct;
        var match = row.FirstOrDefault(x => x.Key.Equals(field, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(match.Value)) return match.Value;

        // Cross-compatible feed field fallbacks for breaking and flash news
        if (string.Equals(field, "flash_text", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(field, "brk_text", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(field, "headline", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(field, "title", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(field, "text", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(field, "news", StringComparison.OrdinalIgnoreCase))
        {
            if (row.TryGetValue("brk_text", out var brk) && !string.IsNullOrWhiteSpace(brk)) return brk;
            if (row.TryGetValue("flash_text", out var flsh) && !string.IsNullOrWhiteSpace(flsh)) return flsh;
            if (row.TryGetValue("headline", out var hd) && !string.IsNullOrWhiteSpace(hd)) return hd;
            if (row.TryGetValue("title", out var ttl) && !string.IsNullOrWhiteSpace(ttl)) return ttl;
            if (row.TryGetValue("text", out var tx) && !string.IsNullOrWhiteSpace(tx)) return tx;
            var nonId = row.FirstOrDefault(x => !x.Key.Equals("id", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(x.Value));
            if (!string.IsNullOrWhiteSpace(nonId.Value)) return nonId.Value;
        }

        return match.Value ?? string.Empty;
    }

    private static IReadOnlyList<Dictionary<string, string>> ParseJson(string json, string? selector)
    {
        using var doc = JsonDocument.Parse(json);
        var element = SelectJson(doc.RootElement, selector);
        var rows = new List<Dictionary<string, string>>();
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) rows.Add(JsonRow(item));
        }
        else if (element.ValueKind == JsonValueKind.Object) rows.Add(JsonRow(element));
        else rows.Add(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["value"] = element.ToString() });
        return rows;
    }

    private static JsonElement SelectJson(JsonElement element, string? selector)
    {
        if (string.IsNullOrWhiteSpace(selector)) return element;
        foreach (var part in selector.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(part, out var child)) element = child;
            else return element;
        }
        return element;
    }

    private static Dictionary<string, string> JsonRow(JsonElement element)
    {
        var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (element.ValueKind != JsonValueKind.Object) { row["value"] = element.ToString(); return row; }
        foreach (var p in element.EnumerateObject()) FlattenJsonValue(row, p.Name, p.Value);
        return row;
    }

    private static void FlattenJsonValue(Dictionary<string, string> row, string key, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var child in value.EnumerateObject()) FlattenJsonValue(row, key + "." + child.Name, child.Value);
            return;
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            row[key] = value.ToString();
            int idx = 1;
            foreach (var child in value.EnumerateArray())
            {
                if (child.ValueKind == JsonValueKind.String)
                {
                    var str = child.GetString() ?? string.Empty;
                    row[$"item{idx}"] = str;
                    row[$"headline{idx}"] = str;
                    row[$"news{idx}"] = str;
                    row[$"{key}{idx}"] = str;
                }
                idx++;
            }
            return;
        }
        row[key] = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Null => string.Empty,
            _ => value.ToString()
        };
    }

    private static IReadOnlyList<Dictionary<string, string>> ParseXml(string xml, string? selector, bool rss)
    {
        var doc = XDocument.Parse(xml);
        IEnumerable<XElement> items;
        if (rss) items = doc.Descendants().Where(x => x.Name.LocalName.Equals("item", StringComparison.OrdinalIgnoreCase));
        else if (!string.IsNullOrWhiteSpace(selector)) items = doc.Descendants().Where(x => x.Name.LocalName.Equals(selector, StringComparison.OrdinalIgnoreCase));
        else items = doc.Root?.Elements() ?? [];
        return items.Select(x => x.Elements().ToDictionary(e => e.Name.LocalName, e => e.Value, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    private static IReadOnlyList<Dictionary<string, string>> ParseText(string text) =>
        text.Replace("\r", string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select((line, i) => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["index"] = i.ToString(),
                ["line"] = line.Trim(),
                ["text"] = line.Trim(),
                ["value"] = line.Trim(),
                ["headline"] = line.Trim(),
                ["title"] = line.Trim(),
                ["brk_text"] = line.Trim(),
                ["flash_text"] = line.Trim()
            }).ToList();

    private static IReadOnlyList<Dictionary<string, string>> ParseDelimited(string text, string? delimiter, bool firstHeader)
    {
        var delim = string.IsNullOrEmpty(delimiter) ? ',' : delimiter[0];
        var lines = text.Replace("\r", string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return [];
        var parsed = lines.Select(x => SplitDelimited(x, delim)).ToArray();
        var headers = firstHeader ? parsed[0] : Enumerable.Range(1, parsed.Max(x => x.Count)).Select(x => $"Column{x}").ToList();
        var start = firstHeader ? 1 : 0;
        var rows = new List<Dictionary<string, string>>();
        for (var i = start; i < parsed.Length; i++)
        {
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var c = 0; c < parsed[i].Count; c++) row[c < headers.Count ? headers[c] : $"Column{c + 1}"] = parsed[i][c];
            rows.Add(row);
        }
        return rows;
    }

    private static List<string> SplitDelimited(string line, char delim)
    {
        var result = new List<string>(); var sb = new StringBuilder(); var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"') { if (quoted && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else quoted = !quoted; }
            else if (ch == delim && !quoted) { result.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        result.Add(sb.ToString()); return result;
    }

    private static IReadOnlyList<Dictionary<string, string>> ParseXlsx(string path, bool firstHeader)
    {
        using var archive = ZipFile.OpenRead(path);
        var shared = new List<string>();
        var sharedEntry = archive.GetEntry("xl/sharedStrings.xml");
        if (sharedEntry is not null)
        {
            using var s = sharedEntry.Open(); var xd = XDocument.Load(s);
            shared.AddRange(xd.Descendants().Where(x => x.Name.LocalName == "si").Select(si => string.Concat(si.Descendants().Where(x => x.Name.LocalName == "t").Select(x => x.Value))));
        }
        var sheet = archive.Entries.Where(x => x.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase) && x.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.FullName).FirstOrDefault();
        if (sheet is null) return [];
        using var stream = sheet.Open(); var doc = XDocument.Load(stream);
        var rows = new List<List<string>>();
        foreach (var row in doc.Descendants().Where(x => x.Name.LocalName == "row"))
        {
            var values = new List<string>();
            foreach (var cell in row.Elements().Where(x => x.Name.LocalName == "c"))
            {
                var type = cell.Attribute("t")?.Value; var v = cell.Elements().FirstOrDefault(x => x.Name.LocalName == "v")?.Value ?? string.Empty;
                if (type == "s" && int.TryParse(v, out var si) && si >= 0 && si < shared.Count) v = shared[si];
                else if (type == "inlineStr") v = string.Concat(cell.Descendants().Where(x => x.Name.LocalName == "t").Select(x => x.Value));
                values.Add(v);
            }
            rows.Add(values);
        }
        if (rows.Count == 0) return [];
        var headers = firstHeader ? rows[0] : Enumerable.Range(1, rows.Max(x => x.Count)).Select(x => $"Column{x}").ToList();
        var start = firstHeader ? 1 : 0;
        var result = new List<Dictionary<string, string>>();
        for (var r = start; r < rows.Count; r++)
        {
            var item = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var c = 0; c < rows[r].Count; c++) item[c < headers.Count ? headers[c] : $"Column{c + 1}"] = rows[r][c];
            result.Add(item);
        }
        return result;
    }

    public static List<(string Category, string Text, bool IsBadge)> ParseTickerItems(string text, CgLayer layer)
    {
        var list = new List<(string Category, string Text, bool IsBadge)>();
        if (string.IsNullOrWhiteSpace(text)) return list;

        // 1. JSON format
        if (text.TrimStart().StartsWith('[') && text.TrimEnd().EndsWith(']'))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var elem in doc.RootElement.EnumerateArray())
                    {
                        if (elem.ValueKind == System.Text.Json.JsonValueKind.Object)
                        {
                            var cat = elem.TryGetProperty("category", out var cp) ? cp.GetString() ?? "" : (elem.TryGetProperty("badge", out var bp) ? bp.GetString() ?? "" : "");
                            if (elem.TryGetProperty("items", out var itemsProp) && itemsProp.ValueKind == System.Text.Json.JsonValueKind.Array)
                            {
                                foreach (var sub in itemsProp.EnumerateArray())
                                {
                                    var itemStr = sub.GetString();
                                    if (!string.IsNullOrWhiteSpace(itemStr)) list.Add((cat, itemStr.Trim(), false));
                                }
                            }
                            else if (elem.TryGetProperty("text", out var tp) || elem.TryGetProperty("title", out tp) || elem.TryGetProperty("headline", out tp))
                            {
                                var itemStr = tp.GetString();
                                if (!string.IsNullOrWhiteSpace(itemStr)) list.Add((cat, itemStr.Trim(), false));
                            }
                        }
                    }
                    if (list.Count > 0) return list;
                }
            }
            catch { }
        }

        // 2. Bracketed category tags: [CATEGORY] Item 1 ★ Item 2 [CATEGORY 2] Item 3
        if (text.Contains('[') && text.Contains(']'))
        {
            var matches = System.Text.RegularExpressions.Regex.Matches(text, @"\[(.*?)\]([^\[]*)");
            if (matches.Count > 0)
            {
                var sepChars = new List<char> { '•', '|', '\n', '★' };
                if (!string.IsNullOrWhiteSpace(layer.TickerSeparator))
                {
                    foreach (var c in layer.TickerSeparator)
                        if (!char.IsWhiteSpace(c) && !sepChars.Contains(c)) sepChars.Add(c);
                }
                foreach (System.Text.RegularExpressions.Match m in matches)
                {
                    var cat = m.Groups[1].Value.Trim();
                    var content = m.Groups[2].Value.Trim();
                    var subItems = content.Split(sepChars.ToArray(), StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    foreach (var sub in subItems)
                    {
                        var clean = sub.Trim().Trim('•', '|', '★', '-', '—', ' ').Trim();
                        if (!string.IsNullOrWhiteSpace(clean)) list.Add((cat, clean, false));
                    }
                }
                if (list.Count > 0) return list;
            }
        }

        // 3. Delimited plain text
        var plainItems = text.Split(['•', '|', '\n', '★'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var item in plainItems)
        {
            var clean = item.Trim().Trim('•', '|', '★', '-', '—', ' ').Trim();
            if (!string.IsNullOrWhiteSpace(clean)) list.Add(("", clean, false));
        }
        return list;
    }

    public static double MeasureTickerTextWidth(string text, string? fontFamily, double fontSize, bool bold, bool italic)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        try
        {
            var style = bold ? FontStyle.Bold : FontStyle.Regular;
            if (italic) style |= FontStyle.Italic;
            using var font = new Font(string.IsNullOrWhiteSpace(fontFamily) ? "Segoe UI" : fontFamily, (float)Math.Max(8, fontSize), style, GraphicsUnit.Pixel);
            using var bmp = new Bitmap(1, 1);
            using var g = Graphics.FromImage(bmp);
            return g.MeasureString(text, font).Width;
        }
        catch
        {
            return text.Length * (fontSize * 0.58);
        }
    }

    public static CategoryFeedSchedule BuildCategoryFeedSchedule(
        CgProject project,
        CgLayer tickerOrBadgeLayer,
        IReadOnlyList<Dictionary<string, string>> rows,
        double windowWidth = 1620,
        double speed = 160)
    {
        var tickerLayer = string.Equals(tickerOrBadgeLayer.Type, "Ticker", StringComparison.OrdinalIgnoreCase)
            ? tickerOrBadgeLayer
            : project.Layers?.FirstOrDefault(x => x.Visible && string.Equals(x.Type, "Ticker", StringComparison.OrdinalIgnoreCase) &&
                                                  (x.DataSourceId == tickerOrBadgeLayer.DataSourceId || x.GroupId == tickerOrBadgeLayer.GroupId));

        tickerLayer ??= tickerOrBadgeLayer;

        var categoryAnimatedLayers = (project.Layers ?? [])
            .Where(x => x.Visible &&
                (string.Equals(x.Type, "Ticker", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(x.DataField, "category", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(x.Role, "Badge", StringComparison.OrdinalIgnoreCase) ||
                 (x.Name?.Contains("Badge", StringComparison.OrdinalIgnoreCase) ?? false) ||
                 (x.Name?.Contains("Category", StringComparison.OrdinalIgnoreCase) ?? false) ||
                 x.TickerCategoriesEnabled) &&
                (x.DataSourceId == tickerLayer.DataSourceId || x.GroupId == tickerLayer.GroupId || x.Id == tickerLayer.Id))
            .ToArray();
        var categoryIntroDuration = Math.Clamp(
            categoryAnimatedLayers.Select(x => x.AnimationInSeconds).DefaultIfEmpty(CategoryIntroSeconds).Max(),
            CategoryIntroSeconds, 5.0);
        var categoryOutroDuration = Math.Clamp(
            categoryAnimatedLayers.Select(x => x.AnimationOutSeconds).DefaultIfEmpty(CategoryOutroSeconds).Max(),
            CategoryOutroSeconds, 5.0);

        var sourceStamp = project.DataSources?.FirstOrDefault(x => x.Id == tickerLayer.DataSourceId)?.LastRefreshUtc.Ticks ?? 0;
        var cacheSignature = string.Join("|", sourceStamp, rows.Count, windowWidth.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            speed.ToString("R", System.Globalization.CultureInfo.InvariantCulture), tickerLayer.Width, tickerLayer.Speed, tickerLayer.TickerSpeed,
            tickerLayer.FontSize, tickerLayer.FontFamily, tickerLayer.Bold, tickerLayer.Italic, tickerLayer.TickerMode,
            tickerLayer.DataField, tickerLayer.DataItemDurationSeconds, tickerLayer.TickerGap, tickerLayer.TickerSeparator,
            tickerLayer.TickerSeparatorLogo, categoryIntroDuration, categoryOutroDuration);
        if (CategoryScheduleCache.TryGetValue(tickerLayer.Id, out var cached) && cached.Signature == cacheSignature)
            return cached.Schedule;

        var effectiveSpeed = Math.Max(20.0, tickerLayer.Speed > 0 ? tickerLayer.Speed : (tickerLayer.TickerSpeed > 0 ? tickerLayer.TickerSpeed : speed));
        var effectiveWidth = windowWidth > 10 ? windowWidth : (tickerLayer.Width > 10 ? tickerLayer.Width : 1620);
        var sep = string.IsNullOrWhiteSpace(tickerLayer.TickerSeparator) ? "  ★  " : (tickerLayer.TickerSeparator.Contains(' ') ? tickerLayer.TickerSeparator : $"  {tickerLayer.TickerSeparator.Trim()}  ");

        var distinctCategories = rows.Select(r => ResolveField(r, "category"))
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinctCategories.Count == 0)
        {
            distinctCategories = rows.Select(r => ResolveField(r, "badge"))
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var schedule = new CategoryFeedSchedule();
        var isPush = string.Equals(tickerLayer.TickerMode, "Push", StringComparison.OrdinalIgnoreCase);
        var pushHold = tickerLayer.DataItemDurationSeconds > 0.05 ? tickerLayer.DataItemDurationSeconds : 5.0;
        var fontSize = tickerLayer.FontSize > 8 ? tickerLayer.FontSize : 32.0;

        var hasSepLogo = !string.IsNullOrWhiteSpace(tickerLayer.TickerSeparatorLogo) && File.Exists(tickerLayer.TickerSeparatorLogo);
        var sepTextWidth = MeasureTickerTextWidth(sep, tickerLayer.FontFamily, fontSize, tickerLayer.Bold, tickerLayer.Italic);
        var logoSize = hasSepLogo ? Math.Max(14.0, (tickerLayer.Height > 10 ? tickerLayer.Height : 90.0) * 0.45) : 0.0;
        var logoPad = hasSepLogo ? 12.0 : 0.0;
        var sepWidth = hasSepLogo ? (logoSize + logoPad * 2) : sepTextWidth;
        // TickerGap is spacing used when a static ticker repeats. Category feeds
        // do not repeat the active category: once its final visible separator has
        // left the window the next category must start immediately. Do not append
        // an invisible trailing gap to the category timing calculation.
        const double categoryEndGap = 0.0;

        if (distinctCategories.Count > 0)
        {
            double cumulativeTime = 0.0;
            foreach (var cat in distinctCategories)
            {
                var catRows = rows.Where(r => string.Equals(ResolveField(r, "category"), cat, StringComparison.OrdinalIgnoreCase) ||
                                              string.Equals(ResolveField(r, "badge"), cat, StringComparison.OrdinalIgnoreCase)).ToList();
                var items = new List<string>();
                foreach (var cr in catRows)
                {
                    var extracted = ExtractNewsItemsFromRow(cr, tickerLayer.DataField);
                    foreach (var itm in extracted)
                    {
                        if (!items.Contains(itm, StringComparer.OrdinalIgnoreCase) &&
                            !string.Equals(itm, cat, StringComparison.OrdinalIgnoreCase))
                            items.Add(itm);
                    }
                }

                if (items.Count == 0)
                {
                    foreach (var cr in catRows)
                    {
                        var val = ResolveField(cr, "headline");
                        if (string.IsNullOrWhiteSpace(val)) val = ResolveField(cr, "items");
                        if (string.IsNullOrWhiteSpace(val)) val = ResolveField(cr, "text");
                        if (!string.IsNullOrWhiteSpace(val) && !items.Contains(val)) items.Add(val);
                    }
                }

                var combinedText = items.Count > 0 ? string.Join(sep, items) : cat;
                double itemTotalWidth = 0;
                foreach (var itm in items)
                {
                    itemTotalWidth += MeasureTickerTextWidth(itm, tickerLayer.FontFamily, fontSize, tickerLayer.Bold, tickerLayer.Italic);
                }
                var totalSepWidth = items.Count * sepWidth;
                var textWidth = Math.Max(1.0, itemTotalWidth + totalSepWidth + categoryEndGap);
                var travelDist = effectiveWidth + textWidth;
                var scrollDuration = travelDist / effectiveSpeed;
                // Category push is gapless; TickerGap remains a spatial crawl setting.
                var pushGapSeconds = 0.0;
                var pushDuration = Math.Max(1.0, items.Count * (pushHold + pushGapSeconds + 0.6));
                // Give the category badge a clean entrance before the first headline.
                // This lead is part of the schedule so category changes remain perfectly
                // aligned across the badge, crawl and push renderers.
                var crawlDuration = isPush ? pushDuration : scrollDuration;
                var effectiveDur = categoryIntroDuration + crawlDuration + categoryOutroDuration;

                var feedItem = new CategoryFeedItem
                {
                    Category = cat.ToUpperInvariant(),
                    Items = items,
                    CombinedItemsText = combinedText,
                    EstimatedTextWidth = textWidth,
                    TravelDistance = travelDist,
                    ScrollDuration = scrollDuration,
                    PushDuration = pushDuration,
                    IntroDuration = categoryIntroDuration,
                    CrawlDuration = crawlDuration,
                    OutroDuration = categoryOutroDuration,
                    PushGapDuration = pushGapSeconds,
                    EffectiveDuration = effectiveDur,
                    StartTime = cumulativeTime
                };
                schedule.Categories.Add(feedItem);
                cumulativeTime += effectiveDur;
            }
            schedule.TotalCycleDuration = cumulativeTime;
        }
        else
        {
            var allItems = new List<string>();
            foreach (var r in rows)
            {
                var extracted = ExtractNewsItemsFromRow(r, tickerLayer.DataField);
                foreach (var itm in extracted)
                {
                    if (!allItems.Contains(itm, StringComparer.OrdinalIgnoreCase))
                        allItems.Add(itm);
                }
            }
            var combinedText = allItems.Count > 0 ? string.Join(sep, allItems) : (tickerLayer.Text ?? string.Empty);
            double itemTotalWidth = 0;
            foreach (var itm in allItems)
            {
                itemTotalWidth += MeasureTickerTextWidth(itm, tickerLayer.FontFamily, fontSize, tickerLayer.Bold, tickerLayer.Italic);
            }
            var totalSepWidth = allItems.Count * sepWidth;
            var textWidth = Math.Max(1.0, itemTotalWidth + totalSepWidth);
            var travelDist = effectiveWidth + textWidth;
            var scrollDuration = travelDist / effectiveSpeed;
            var pushGapSeconds = tickerLayer.TickerGap > 10 ? tickerLayer.TickerGap / 1000.0 : Math.Max(0.0, tickerLayer.TickerGap);
            var pushDuration = Math.Max(1.0, allItems.Count * (pushHold + pushGapSeconds + 0.6));
            var effectiveDur = isPush ? pushDuration : scrollDuration;

            var feedItem = new CategoryFeedItem
            {
                Category = string.Empty,
                Items = allItems,
                CombinedItemsText = combinedText,
                EstimatedTextWidth = textWidth,
                TravelDistance = travelDist,
                ScrollDuration = scrollDuration,
                PushDuration = pushDuration,
                EffectiveDuration = effectiveDur,
                StartTime = 0
            };
            schedule.Categories.Add(feedItem);
            schedule.TotalCycleDuration = effectiveDur;
        }

        CategoryScheduleCache[tickerLayer.Id] = (cacheSignature, schedule);
        return schedule;
    }

    public static List<CgTickerSegment> BuildAllTickerSegments(
        CgProject project,
        CgLayer tickerLayer,
        IReadOnlyList<Dictionary<string, string>> rows,
        bool showInlineBadge)
    {
        var segments = new List<CgTickerSegment>();
        var distinctCategories = rows.Select(r => ResolveField(r, "category"))
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinctCategories.Count == 0)
        {
            distinctCategories = rows.Select(r => ResolveField(r, "badge"))
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (distinctCategories.Count > 0)
        {
            foreach (var cat in distinctCategories)
            {
                var catRows = rows.Where(r => string.Equals(ResolveField(r, "category"), cat, StringComparison.OrdinalIgnoreCase) ||
                                              string.Equals(ResolveField(r, "badge"), cat, StringComparison.OrdinalIgnoreCase)).ToList();
                var items = new List<string>();
                foreach (var cr in catRows)
                {
                    var extracted = ExtractNewsItemsFromRow(cr, tickerLayer.DataField);
                    foreach (var itm in extracted)
                    {
                        if (!items.Contains(itm, StringComparer.OrdinalIgnoreCase) &&
                            !string.Equals(itm, cat, StringComparison.OrdinalIgnoreCase))
                            items.Add(itm);
                    }
                }
                if (items.Count == 0)
                {
                    foreach (var cr in catRows)
                    {
                        var val = ResolveField(cr, "headline");
                        if (string.IsNullOrWhiteSpace(val)) val = ResolveField(cr, "items");
                        if (string.IsNullOrWhiteSpace(val)) val = ResolveField(cr, "text");
                        if (!string.IsNullOrWhiteSpace(val) && !items.Contains(val)) items.Add(val);
                    }
                }

                if (showInlineBadge && !string.IsNullOrWhiteSpace(cat))
                {
                    segments.Add(new CgTickerSegment(cat.ToUpperInvariant(), cat.ToUpperInvariant(), true));
                }
                foreach (var itm in items)
                {
                    segments.Add(new CgTickerSegment(cat.ToUpperInvariant(), itm, false));
                }
            }
        }
        else
        {
            var allItems = new List<string>();
            foreach (var r in rows)
            {
                var extracted = ExtractNewsItemsFromRow(r, tickerLayer.DataField);
                foreach (var itm in extracted)
                {
                    if (!allItems.Contains(itm, StringComparer.OrdinalIgnoreCase))
                        allItems.Add(itm);
                }
            }
            foreach (var itm in allItems)
            {
                segments.Add(new CgTickerSegment(string.Empty, itm, false));
            }
        }

        if (segments.Count == 0 && !string.IsNullOrWhiteSpace(tickerLayer.Text))
        {
            segments.Add(new CgTickerSegment(string.Empty, tickerLayer.Text, false));
        }

        return segments;
    }

    public static double ResolveEffectiveTickerCycleDuration(CgProject? project)
    {
        if (project?.Layers is null) return 0;
        var tickerLayer = project.Layers.FirstOrDefault(x => x.Visible && string.Equals(x.Type, "Ticker", StringComparison.OrdinalIgnoreCase));
        if (tickerLayer is null) return 0;

        // 1. Dynamic data source ticker
        if (tickerLayer.DataSourceId != Guid.Empty && project.DataSources is not null)
        {
            var ds = project.DataSources.FirstOrDefault(x => x.Id == tickerLayer.DataSourceId);
            if (ds is not null)
            {
                var rows = CgDataRuntime.Shared.GetRows(ds);
                if (rows.Count > 0)
                {
                    var windowW = tickerLayer.Width > 10 ? tickerLayer.Width : (project.Width > 10 ? project.Width : 1920);
                    var spd = tickerLayer.Speed > 0 ? tickerLayer.Speed : (tickerLayer.TickerSpeed > 0 ? tickerLayer.TickerSpeed : 160.0);
                    var schedule = BuildCategoryFeedSchedule(project, tickerLayer, rows, windowW, spd);
                    if (schedule.TotalCycleDuration > 0.5) return schedule.TotalCycleDuration;
                }
            }
        }

        // 2. Static / embedded text ticker
        var text = tickerLayer.Text;
        if (!string.IsNullOrWhiteSpace(text))
        {
            var fontSize = tickerLayer.FontSize > 8 ? tickerLayer.FontSize : 32.0;
            var windowW = tickerLayer.Width > 10 ? tickerLayer.Width : (project.Width > 10 ? project.Width : 1920);
            var spd = Math.Max(20.0, tickerLayer.Speed > 0 ? tickerLayer.Speed : (tickerLayer.TickerSpeed > 0 ? tickerLayer.TickerSpeed : 160.0));
            var textWidth = MeasureTickerTextWidth(text, tickerLayer.FontFamily, fontSize, tickerLayer.Bold, tickerLayer.Italic);
            var gap = Math.Max(30.0, tickerLayer.TickerGap);
            var travelDist = windowW + textWidth + gap;
            return Math.Max(1.0, travelDist / spd);
        }

        return 0;
    }

    public static double ResolveEffectiveHoldPoint(CgProject? project)
    {
        if (project?.Layers is null) return 1.2;
        var pauseEvt = project.TimelineEvents?.FirstOrDefault(e => e.Enabled && (string.Equals(e.Type, "Pause", StringComparison.OrdinalIgnoreCase) || string.Equals(e.Type, "Hold", StringComparison.OrdinalIgnoreCase)));
        if (pauseEvt != null && pauseEvt.TimeSeconds > 0)
            return pauseEvt.TimeSeconds;
        var nonTickers = project.Layers.Where(x => x.Visible && !string.Equals(x.Type, "Ticker", StringComparison.OrdinalIgnoreCase)).ToList();
        if (nonTickers.Count == 0) return 1.0;
        var maxIn = nonTickers.Select(x => x.StartSeconds + Math.Max(0.3, x.AnimationInSeconds)).Max();
        return Math.Max(0.5, maxIn);
    }

    public static double ResolveEffectiveCategoryIntroDuration(CgProject? project)
    {
        if (project?.Layers is null) return CategoryIntroSeconds;
        var ticker = project.Layers.FirstOrDefault(x => x.Visible && string.Equals(x.Type, "Ticker", StringComparison.OrdinalIgnoreCase));
        if (ticker is null || ticker.DataSourceId == Guid.Empty) return CategoryIntroSeconds;
        var source = project.DataSources?.FirstOrDefault(x => x.Id == ticker.DataSourceId);
        IReadOnlyList<Dictionary<string, string>> rows = source is null ? [] : CgDataRuntime.Shared.GetRows(source);
        if (rows.Count == 0) return CategoryIntroSeconds;
        var schedule = BuildCategoryFeedSchedule(project, ticker, rows, ticker.Width, ticker.Speed);
        return schedule.Categories.FirstOrDefault()?.IntroDuration ?? CategoryIntroSeconds;
    }

    public static double ResolveCategoryDrivenTimelineSeconds(CgProject project, double categoryCycleSeconds)
    {
        var hold = ResolveEffectiveHoldPoint(project);
        var ticker = project.Layers?.FirstOrDefault(x => x.Visible && string.Equals(x.Type, "Ticker", StringComparison.OrdinalIgnoreCase));
        if (ticker is null || ticker.DataSourceId == Guid.Empty) return Math.Max(0, categoryCycleSeconds);
        var source = project.DataSources?.FirstOrDefault(x => x.Id == ticker.DataSourceId);
        IReadOnlyList<Dictionary<string, string>> rows = source is null ? [] : CgDataRuntime.Shared.GetRows(source);
        var schedule = BuildCategoryFeedSchedule(project, ticker, rows);
        var (active, local, _) = schedule.Evaluate(Math.Max(0, categoryCycleSeconds));
        var intro = active.IntroDuration > 0 ? active.IntroDuration : CategoryIntroSeconds;
        return local < intro
            ? Math.Max(0, hold - intro + local)
            : hold;
    }

    public static bool TryGetActiveCategoryTiming(
        CgProject? project,
        CgLayer? layer,
        double continuousSeconds,
        out string categoryName,
        out double localElapsed,
        out double categoryDuration,
        out int categoryIndex,
        out double introDuration,
        out double outroDuration)
    {
        categoryName = string.Empty;
        localElapsed = 0;
        categoryDuration = 0;
        categoryIndex = 0;
        introDuration = 0;
        outroDuration = 0;
        if (project is null || layer is null) return false;

        CgDataSource? ds = null;
        if (layer.DataSourceId != Guid.Empty && project.DataSources != null)
        {
            ds = project.DataSources.FirstOrDefault(x => x.Id == layer.DataSourceId);
        }
        else if (project.DataSources != null && project.DataSources.Count > 0)
        {
            var sibling = project.Layers?.FirstOrDefault(x => x.Visible && x.DataSourceId != Guid.Empty &&
                (string.Equals(x.Type, "Ticker", StringComparison.OrdinalIgnoreCase) || (x.GroupId != Guid.Empty && x.GroupId == layer.GroupId)));
            if (sibling != null)
                ds = project.DataSources.FirstOrDefault(x => x.Id == sibling.DataSourceId);
        }

        if (ds is null) return false;
        var rows = CgDataRuntime.Shared.GetRows(ds);
        if (rows.Count == 0) return false;

        var isTickerOrBadge = string.Equals(layer.Type, "Ticker", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(layer.DataField, "category", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(layer.Role, "Badge", StringComparison.OrdinalIgnoreCase) ||
                              ((layer.Name?.Contains("Badge", StringComparison.OrdinalIgnoreCase) ?? false) &&
                               (string.Equals(layer.Type, "Text", StringComparison.OrdinalIgnoreCase) || string.Equals(layer.Type, "Shape", StringComparison.OrdinalIgnoreCase))) ||
                              (string.Equals(layer.Type, "Text", StringComparison.OrdinalIgnoreCase) &&
                               (layer.Name?.Contains("Category", StringComparison.OrdinalIgnoreCase) ?? false)) ||
                              layer.TickerCategoriesEnabled;

        // A category-capable feed does not make every shape in the ticker group a
        // category animation. Base bars/plates/dividers remain continuously visible;
        // only ticker/category text participates in category-local timing.
        if (!isTickerOrBadge) return false;

        var schedule = BuildCategoryFeedSchedule(project, layer, rows);
        if (schedule.Categories.Count == 0) return false;

        var (activeCat, catElapsed, idx) = schedule.Evaluate(Math.Max(0, continuousSeconds));
        categoryName = activeCat.Category;
        localElapsed = catElapsed;
        categoryDuration = activeCat.EffectiveDuration;
        categoryIndex = idx;
        introDuration = activeCat.IntroDuration;
        outroDuration = activeCat.OutroDuration;
        return true;
    }

    public static double ResolveCategoryLayerTimelineSeconds(CgProject project, CgLayer layer, double continuousSeconds, double fallbackSeconds)
    {
        if (!TryGetActiveCategoryTiming(project, layer, continuousSeconds, out _, out var local, out var duration, out _, out var intro, out var outro))
            return fallbackSeconds;

        intro = Math.Max(0.05, intro);
        outro = Math.Max(0.05, outro);
        if (local < intro)
        {
            var progress = Math.Clamp(local / intro, 0, 1);
            return layer.StartSeconds + progress * Math.Max(0.05, layer.AnimationInSeconds);
        }
        if (local >= duration - outro)
        {
            var progress = Math.Clamp((local - (duration - outro)) / outro, 0, 1);
            var outDuration = Math.Max(0.05, layer.AnimationOutSeconds);
            return Math.Max(layer.StartSeconds, layer.EndSeconds - outDuration) + progress * outDuration;
        }
        return Math.Min(layer.EndSeconds, layer.StartSeconds + Math.Max(0.05, layer.AnimationInSeconds));
    }

    public static void GetCategoryAnimationProgress(
        double localElapsed,
        double categoryDuration,
        double introDuration,
        double outroDuration,
        out bool isIntro,
        out bool isOutro,
        out double inProgress,
        out double outProgress)
    {
        introDuration = Math.Max(0.05, introDuration);
        outroDuration = Math.Max(0.05, outroDuration);
        var outroStart = Math.Max(introDuration, categoryDuration - outroDuration);

        if (localElapsed < introDuration)
        {
            isIntro = true;
            isOutro = false;
            inProgress = Math.Clamp(localElapsed / introDuration, 0, 1);
            outProgress = 1.0;
        }
        else if (localElapsed >= outroStart)
        {
            isIntro = false;
            isOutro = true;
            inProgress = 1.0;
            outProgress = Math.Clamp((categoryDuration - localElapsed) / outroDuration, 0, 1);
        }
        else
        {
            isIntro = false;
            isOutro = false;
            inProgress = 1.0;
            outProgress = 1.0;
        }
    }

    public static double ResolveTickerRequiredDuration(CgProject project, CgLayer layer)
    {
        if (layer == null) return 10.0;
        if (layer.DataSourceId != Guid.Empty && project.DataSources != null)
        {
            var ds = project.DataSources.FirstOrDefault(x => x.Id == layer.DataSourceId);
            if (ds != null)
            {
                var rows = CgDataRuntime.Shared.GetRows(ds);
                if (rows.Count > 0)
                {
                    var sched = BuildCategoryFeedSchedule(project, layer, rows, layer.Width, layer.Speed);
                    var outPad = Math.Max(0.5, layer.AnimationOutSeconds);
                    return Math.Max(5.0, layer.StartSeconds + sched.TotalCycleDuration + outPad);
                }
            }
        }
        var raw = ResolveLayerText(project, layer, 0);
        if (string.IsNullOrWhiteSpace(raw)) raw = layer.Text ?? "KASHTRIX NEWS • LIVE";

        var isPush = string.Equals(layer.TickerMode, "Push", StringComparison.OrdinalIgnoreCase);
        if (isPush)
        {
            var items = ParseTickerItems(raw, layer);
            var count = items.Count;
            if (count <= 0) count = 1;
            var hold = layer.DataItemDurationSeconds > 0.05 ? layer.DataItemDurationSeconds : Math.Max(2.5, 360.0 / Math.Max(20, layer.Speed));
            var totalTime = count * hold;
            var outPadding = Math.Max(0.5, layer.AnimationOutSeconds);
            return Math.Max(5.0, layer.StartSeconds + totalTime + outPadding);
        }
        else
        {
            var fs = Math.Max(12, layer.FontSize);
            var charWidth = fs * 0.58;
            var textWidth = raw.Length * charWidth;
            var gap = Math.Max(30, layer.TickerGap);
            var totalContentWidth = textWidth + gap;

            var screenWidth = layer.Width > 0 ? layer.Width : (project?.Width > 0 ? project.Width : 1920.0);
            var totalTravelSpan = screenWidth + totalContentWidth;
            var speed = Math.Max(20, layer.Speed);
            var travelTime = totalTravelSpan / speed;
            var outPadding = Math.Max(0.5, layer.AnimationOutSeconds);
            return Math.Max(5.0, layer.StartSeconds + travelTime + outPadding);
        }
    }
}

public sealed class CategoryFeedItem
{
    public string Category { get; set; } = string.Empty;
    public List<string> Items { get; set; } = [];
    public string CombinedItemsText { get; set; } = string.Empty;
    public double EstimatedTextWidth { get; set; }
    public double TravelDistance { get; set; }
    public double ScrollDuration { get; set; }
    public double PushDuration { get; set; }
    public double IntroDuration { get; set; }
    public double CrawlDuration { get; set; }
    public double OutroDuration { get; set; }
    public double PushGapDuration { get; set; }
    public double EffectiveDuration { get; set; }
    public double StartTime { get; set; }
    public double EndTime => StartTime + EffectiveDuration;
}

public sealed class CategoryFeedSchedule
{
    public List<CategoryFeedItem> Categories { get; set; } = [];
    public double TotalCycleDuration { get; set; }

    public (CategoryFeedItem ActiveCategory, double LocalElapsed, int Index) Evaluate(double timelineSeconds)
    {
        if (Categories.Count == 0)
        {
            var fallback = new CategoryFeedItem { Category = "", Items = [], EffectiveDuration = 10 };
            return (fallback, 0, 0);
        }
        var elapsed = TotalCycleDuration > 0.05 ? (timelineSeconds % TotalCycleDuration) : timelineSeconds;
        if (elapsed < 0 && TotalCycleDuration > 0.05) elapsed += TotalCycleDuration;
        for (int i = 0; i < Categories.Count; i++)
        {
            var cat = Categories[i];
            if (elapsed >= cat.StartTime && elapsed < cat.EndTime)
            {
                return (cat, Math.Max(0, elapsed - cat.StartTime), i);
            }
        }
        return (Categories[0], 0, 0);
    }
}

/// <summary>Non-blocking runtime cache so network data never blocks the on-air compositor.</summary>
public sealed class CgDataRuntime
{
    private sealed record Entry(DateTime LoadedUtc, IReadOnlyList<Dictionary<string, string>> Rows);
    private readonly ConcurrentDictionary<Guid, Entry> _cache = [];
    private readonly ConcurrentDictionary<Guid, byte> _loading = [];
    public static CgDataRuntime Shared { get; } = new();

    public IReadOnlyList<Dictionary<string, string>> GetRows(CgDataSource source)
    {
        if (_cache.TryGetValue(source.Id, out var cached))
        {
            var age = DateTime.UtcNow - cached.LoadedUtc;
            if (source.RefreshSeconds <= 0 || age.TotalSeconds < source.RefreshSeconds) return cached.Rows;
        }

        var persisted = CgDataSourceService.ReadCachedRows(source);
        if (persisted.Count > 0 && !_cache.ContainsKey(source.Id)) _cache[source.Id] = new Entry(source.LastRefreshUtc == default ? DateTime.UtcNow : source.LastRefreshUtc, persisted);
        QueueRefresh(source);
        return _cache.TryGetValue(source.Id, out cached) ? cached.Rows : persisted;
    }

    public async Task<IReadOnlyList<Dictionary<string, string>>> RefreshAsync(CgDataSource source, CancellationToken ct = default)
    {
        var rows = await CgDataSourceService.LoadAsync(source, ct).ConfigureAwait(false);
        _cache[source.Id] = new Entry(DateTime.UtcNow, rows);
        var serialized = CgDataSourceService.SerializeRows(rows);
        if (!string.Equals(source.CachedItemsJson, serialized, StringComparison.Ordinal))
        {
            source.CachedItemsJson = serialized;
        }
        source.LastRefreshUtc = DateTime.UtcNow;
        var status = $"READY · {rows.Count} item(s)";
        if (!string.Equals(source.Status, status, StringComparison.Ordinal))
        {
            source.Status = status;
        }
        return rows;
    }

    private void QueueRefresh(CgDataSource source)
    {
        if (!_loading.TryAdd(source.Id, 0)) return;
        _ = Task.Run(async () =>
        {
            try { await RefreshAsync(source).ConfigureAwait(false); }
            catch (Exception ex) { source.Status = "ERROR · " + ex.Message; }
            finally { _loading.TryRemove(source.Id, out _); }
        });
    }
}

public readonly record struct CgTickerSegment(string Category, string Text, bool IsCategoryBadge);
