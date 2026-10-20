using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BroadcastPlayout.Models;

namespace BroadcastPlayout.Services;

public static class CgDemoFactory
{
    private static readonly string[] VariantNames = ["Classic", "Clean", "Glass", "Neon", "Newsroom", "Sports", "Finance", "Election", "Music", "Minimal"];
    private static readonly string[] VariantAnimations = ["Fade", "Push Left", "Slide Up", "Zoom", "Wipe Left", "Push Up", "Slide Right", "Pop", "Blur", "Scale"];
    private static readonly string[] AccentPalette = ["#FF8F4FD4", "#FF32D1C6", "#FF66B7FF", "#FFFF4FD8", "#FFE44A4A", "#FF7BD85B", "#FFFFC857", "#FF5C7CFA", "#FFB978FF", "#FFFFFFFF"];
    private static readonly string[] PlatePalette = ["#E6121720", "#E80D151A", "#D8172130", "#D80B0C18", "#E8141518", "#E8151C17", "#E81D1B13", "#E8131625", "#E8171120", "#E80D1014"];

    public static string GetCgDemoDirectory()
    {
        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("KASHTRIX_WORKSPACE_ROOT") != null ? Path.Combine(Environment.GetEnvironmentVariable("KASHTRIX_WORKSPACE_ROOT")!, "cg-demo") : null,
            Path.Combine(Directory.GetCurrentDirectory(), "cg-demo"),
            @"c:\Users\karnk\Downloads\Kashtrix-COMPLETE-PROJECT-FIX49H-FULL-BUILD-DEBUG-RECOVERY-FINAL\Kashtrix-FIX49H\cg-demo",
            Path.Combine(AppContext.BaseDirectory, "cg-demo")
        };
        foreach (var c in candidates)
        {
            if (!string.IsNullOrWhiteSpace(c) && Directory.Exists(c)) return c;
        }
        return Path.Combine(AppContext.BaseDirectory, "cg-demo");
    }

    public static List<CgProject> CreateDefaults(bool forceRefresh = false)
    {
        var dir = GetCgDemoDirectory();
        var canonicalProjects = CgUniqueDemoFactory.Create();
        var loaded = new List<CgProject>();
        var loadedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!forceRefresh)
        {
            var candidateDirs = new List<string>();
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir)) candidateDirs.Add(dir);
            var wsDemos = Path.Combine(Directory.GetCurrentDirectory(), "demos");
            if (Directory.Exists(wsDemos) && !candidateDirs.Contains(wsDemos, StringComparer.OrdinalIgnoreCase)) candidateDirs.Add(wsDemos);
            var baseDemos = Path.Combine(AppContext.BaseDirectory, "demos");
            if (Directory.Exists(baseDemos) && !candidateDirs.Contains(baseDemos, StringComparer.OrdinalIgnoreCase)) candidateDirs.Add(baseDemos);

            foreach (var d in candidateDirs)
            {
                var kcgFiles = Directory.GetFiles(d, "*.kcg");
                foreach (var file in kcgFiles.OrderBy(f => Path.GetFileName(f)))
                {
                    try
                    {
                        var proj = KashtrixCgFileService.LoadComposition(file);
                        if (proj != null && !string.IsNullOrWhiteSpace(proj.Name) && loadedNames.Add(proj.Name))
                        {
                            CgProjectSanitizer.Sanitize(proj);
                            loaded.Add(proj);
                        }
                    }
                    catch { }
                }
            }
        }

        // Ensure every canonical template from CgUniqueDemoFactory (including AP1 HD, Prime HD, Space 4K)
        // is present in the collection. If any are missing or if forceRefresh is true, merge them.
        var toPersist = new List<CgProject>();
        foreach (var canonical in canonicalProjects)
        {
            if (forceRefresh)
            {
                var idx = loaded.FindIndex(x => string.Equals(x.Name, canonical.Name, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) loaded[idx] = canonical;
                else { loaded.Add(canonical); loadedNames.Add(canonical.Name); }
                toPersist.Add(canonical);
            }
            else if (!loadedNames.Contains(canonical.Name))
            {
                loaded.Add(canonical);
                loadedNames.Add(canonical.Name);
                toPersist.Add(canonical);
            }
        }

        try
        {
            var targetDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(dir)) targetDirs.Add(dir);
            var wsDemos = Path.Combine(Directory.GetCurrentDirectory(), "demos");
            if (Directory.Exists(wsDemos)) targetDirs.Add(wsDemos);

            var projectsToSave = forceRefresh ? canonicalProjects : toPersist;
            foreach (var targetDir in targetDirs)
            {
                Directory.CreateDirectory(targetDir);
                foreach (var p in projectsToSave)
                {
                    var safeName = SanitizeFileName(p.Name) + ".kcg";
                    var filePath = Path.Combine(targetDir, safeName);
                    if (forceRefresh || !File.Exists(filePath))
                    {
                        KashtrixCgFileService.SaveComposition(filePath, p);
                    }
                }
                // Never delete any existing files: all user and demo .kcg files are preserved permanently.
            }
        }
        catch { }

        return loaded.Count > 0 ? loaded : canonicalProjects;
    }

    public static CgLayer CloneLayer(CgLayer l)
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(l);
            return System.Text.Json.JsonSerializer.Deserialize<CgLayer>(json) ?? l;
        }
        catch
        {
            return l;
        }
    }

    public static CgProject CloneProject(CgProject p)
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(p);
            return System.Text.Json.JsonSerializer.Deserialize<CgProject>(json) ?? p;
        }
        catch
        {
            return p;
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars).Trim();
    }

    private static CgProject CloneVariant(CgProject source, int family, int variant)
    {
        var project = new CgProject
        {
            Name = $"{source.Name} · {VariantNames[variant]} {variant + 1:00}",
            Width = source.Width,
            Height = source.Height,
            DurationSeconds = source.DurationSeconds,
            Layers = source.Layers.Select((layer, index) => new CgLayer
            {
                Name = layer.Name,
                Type = layer.Type,
                Text = layer.Text,
                Source = layer.Source,
                X = layer.X,
                Y = layer.Y,
                Width = layer.Width,
                Height = layer.Height,
                FontSize = layer.FontSize,
                FontFamily = layer.FontFamily,
                Fill = layer.Fill,
                Background = layer.Background,
                StartSeconds = layer.StartSeconds + Math.Min(.35, variant * .025 * index),
                EndSeconds = layer.EndSeconds,
                AnimationIn = variant == 0 ? layer.AnimationIn : VariantAnimations[(variant + index + family) % VariantAnimations.Length],
                AnimationOut = variant == 0 ? layer.AnimationOut : VariantAnimations[(variant + index + family + 3) % VariantAnimations.Length],
                AnimationInSeconds = 0.45 + variant * 0.05,
                AnimationOutSeconds = 0.45 + ((variant + 3) % 5) * 0.07,
                Visible = layer.Visible,
                Speed = layer.Speed * (0.9 + variant * 0.025),
                Rotation = layer.Rotation,
                Opacity = layer.Opacity,
                Role = layer.Role,
                ShapeKind = layer.ShapeKind,
                ShapePathData = layer.ShapePathData,
                ShapePathClosed = layer.ShapePathClosed,
                CornerRadius = layer.CornerRadius,
                BorderColor = layer.BorderColor,
                BorderWidth = layer.BorderWidth,
                HorizontalTextAlignment = layer.HorizontalTextAlignment,
                VerticalTextAlignment = layer.VerticalTextAlignment,
                Bold = layer.Bold,
                Italic = layer.Italic,
                Underline = layer.Underline,
                OutlineColor = layer.OutlineColor,
                OutlineWidth = layer.OutlineWidth,
                ShadowColor = layer.ShadowColor,
                ShadowOffsetX = layer.ShadowOffsetX,
                ShadowOffsetY = layer.ShadowOffsetY,
                ShadowBlur = layer.ShadowBlur,
                LetterSpacing = layer.LetterSpacing,
                LineSpacing = layer.LineSpacing,
                TickerMode = layer.TickerMode,
                TickerDirection = layer.TickerDirection,
                TickerGap = layer.TickerGap,
                TickerRepeat = layer.TickerRepeat,
                MaskEnabled = layer.MaskEnabled,
                MaskShape = layer.MaskShape,
                MaskPathData = layer.MaskPathData,
                MaskX = layer.MaskX,
                MaskY = layer.MaskY,
                MaskWidth = layer.MaskWidth,
                MaskHeight = layer.MaskHeight,
                MaskCornerRadius = layer.MaskCornerRadius,
                MaskFeather = layer.MaskFeather,
                MaskInvert = layer.MaskInvert,
                VideoSourceKind = layer.VideoSourceKind,
                VideoInputFormat = layer.VideoInputFormat,
                VideoInputOptions = layer.VideoInputOptions,
                VideoDevice = layer.VideoDevice,
                AudioDevice = layer.AudioDevice,
                AlternateAudioUrl = layer.AlternateAudioUrl,
                VideoIsLiveSource = layer.VideoIsLiveSource,
                VideoCaptureWidth = layer.VideoCaptureWidth,
                VideoCaptureHeight = layer.VideoCaptureHeight,
                VideoSourceFrameRate = layer.VideoSourceFrameRate,
                SqueezeProgram = layer.SqueezeProgram,
                SqueezeHorizontalMode = layer.SqueezeHorizontalMode,
                SqueezeX = layer.SqueezeX,
                SqueezeY = layer.SqueezeY,
                SqueezeWidth = layer.SqueezeWidth,
                SqueezeHeight = layer.SqueezeHeight,
                SqueezeInSeconds = layer.SqueezeInSeconds,
                SqueezeOutSeconds = layer.SqueezeOutSeconds
            }).ToList(),
            HtmlSources = source.HtmlSources.Select(html => new CgHtmlSource
            {
                Name = html.Name,
                Source = html.Source,
                SourceKind = html.SourceKind,
                X = html.X,
                Y = html.Y,
                Width = html.Width,
                Height = html.Height,
                CanvasWidth = html.CanvasWidth,
                CanvasHeight = html.CanvasHeight,
                BrowserFps = html.BrowserFps,
                Visible = html.Visible,
                Transparent = html.Transparent,
                JavaScriptEnabled = html.JavaScriptEnabled,
                KeepAlive = html.KeepAlive,
                ReloadOnShow = html.ReloadOnShow,
                Opacity = html.Opacity,
                DataJson = html.DataJson
            }).ToList()
        };

        // Every demo variant changes more than its name: palette, geometry, typography,
        // motion and ticker treatment are deliberately varied so the 200 bundled projects
        // are useful starting points instead of cosmetic duplicates.
        var accent = AccentPalette[variant % AccentPalette.Length];
        var plate = PlatePalette[variant % PlatePalette.Length];
        foreach (var layer in project.Layers.Select((value, index) => (value, index)))
        {
            var l = layer.value;
            if (l.Type.Equals("Shape", StringComparison.OrdinalIgnoreCase))
            {
                l.Background = layer.index % 2 == 0 ? plate : accent;
                l.CornerRadius = variant is 1 or 2 or 9 ? 18 + variant : variant is 3 ? 6 : 0;
                l.BorderColor = variant is 2 or 3 ? accent : "#00000000";
                l.BorderWidth = variant is 2 or 3 ? 2 : 0;
            }
            else if (l.Type.Equals("Text", StringComparison.OrdinalIgnoreCase) || l.Type.Contains("Clock", StringComparison.OrdinalIgnoreCase))
            {
                if (layer.index % 3 == 0) l.Fill = accent;
                l.OutlineWidth = variant is 3 or 5 ? 2 : 0;
                l.ShadowBlur = variant is 2 or 3 ? 3 : 0;
                l.ShadowOffsetX = variant is 2 or 3 ? 4 : 2;
                l.ShadowOffsetY = variant is 2 or 3 ? 4 : 2;
                l.HorizontalTextAlignment = variant is 9 ? "Center" : l.HorizontalTextAlignment;
            }
            else if (l.Type.Equals("Ticker", StringComparison.OrdinalIgnoreCase))
            {
                l.TickerMode = variant is 1 or 6 or 8 ? "Push" : "Continuous";
                l.TickerDirection = variant is 4 ? "Right" : "Left";
                l.Fill = variant % 2 == 0 ? "#FFFFFFFF" : accent;
                l.Background = plate;
            }
            l.X += variant * (layer.index % 2 == 0 ? 2 : -1);
            l.Y += variant * (layer.index % 3 == 0 ? 1 : 0);
        }

        return project;
    }

    private static string DemoPath(params string[] parts)
    {
        var all = new[] { AppContext.BaseDirectory, "demos", "gfx" }.Concat(parts).ToArray();
        return Path.Combine(all);
    }

    private static CgProject LowerThird() => new()
    {
        Name = "Demo · Modern Lower Third",
        DurationSeconds = 12,
        Layers =
        [
            new CgLayer { Name="Blue Bar", Type="Shape", X=85, Y=790, Width=830, Height=142, Background="#E6102032", StartSeconds=0, EndSeconds=12, AnimationIn="Slide Left", AnimationOut="Slide Left" },
            new CgLayer { Name="Accent", Type="Shape", X=85, Y=790, Width=16, Height=142, Background="#FF68DE2E", StartSeconds=.1, EndSeconds=12, AnimationIn="Slide Up" },
            new CgLayer { Name="Name", Type="Text", Text="ANIL SHARMA", X=130, Y=808, Width=700, Height=60, FontSize=46, Fill="#FFFFFFFF", StartSeconds=.18, EndSeconds=12, AnimationIn="Fade" },
            new CgLayer { Name="Designation", Type="Text", Text="Senior Correspondent · Kashtrix News", X=132, Y=868, Width=720, Height=44, FontSize=25, Fill="#FFB7C5CF", StartSeconds=.32, EndSeconds=12, AnimationIn="Slide Up" }
        ]
    };

    private static CgProject BreakingNews() => new()
    {
        Name = "Demo · Breaking News",
        DurationSeconds = 20,
        Layers =
        [
            new CgLayer { Name="Breaking Plate", Type="Shape", X=0, Y=850, Width=1920, Height=230, Background="#E915171B", EndSeconds=20, AnimationIn="Slide Up" },
            new CgLayer { Name="Breaking Tag", Type="Shape", X=0, Y=850, Width=360, Height=82, Background="#FFF0443D", EndSeconds=20, AnimationIn="Slide Left" },
            new CgLayer { Name="Breaking", Type="Text", Text="BREAKING NEWS", X=42, Y=865, Width=310, Height=58, FontSize=38, Fill="#FFFFFFFF", EndSeconds=20, AnimationIn="Fade" },
            new CgLayer { Name="Headline", Type="Text", Text="Kashtrix demonstration graphic · edit this headline in CG Editor", X=390, Y=868, Width=1460, Height=55, FontSize=35, Fill="#FFFFFFFF", EndSeconds=20, AnimationIn="Slide Left" },
            new CgLayer { Name="Ticker", Type="Ticker", Text="LIVE  •  NATIONAL  •  BUSINESS  •  SPORTS  •  WEATHER  •  KASHTRIX NEWS AUTOMATION DEMO", X=0, Y=945, Width=1920, Height=62, FontSize=27, Fill="#FFFFD35A", Background="#FF080D12", Speed=150, EndSeconds=20 }
        ]
    };

    private static CgProject MusicNowPlaying() => AutoDemo(
        "Demo · Music · Now Playing", "NOW PLAYING", "Midnight Drive", "Aarav & The City Lights", "#FFBC6CFF");

    private static CgProject MusicComingUp() => AutoDemo(
        "Demo · Music · Coming Up", "COMING UP", "Neon Rain", "Maya Rai", "#FFFFB04F");

    private static CgProject NewsNowPlaying() => AutoDemo(
        "Demo · News · Now Playing", "NOW PLAYING", "TOP STORIES", "Kashtrix Newsroom", "#FF5C9CFF");

    private static CgProject NewsComingUp() => AutoDemo(
        "Demo · News · Coming Up", "COMING UP", "BUSINESS UPDATE", "Next in the rundown", "#FFFFB04F");

    private static CgProject AutoDemo(string name, string tag, string title, string second, string accent) => new()
    {
        Name = name,
        DurationSeconds = 12,
        Layers =
        [
            new CgLayer { Name="Plate", Type="Shape", X=72, Y=820, Width=1120, Height=180, Background="#E5141921", StartSeconds=0, EndSeconds=12, AnimationIn="Slide Left", AnimationOut="Fade" },
            new CgLayer { Name="Accent", Type="Shape", X=72, Y=820, Width=18, Height=180, Background=accent, StartSeconds=0, EndSeconds=12 },
            new CgLayer { Name="Tag", Type="Text", Text=tag, X=120, Y=836, Width=360, Height=44, FontSize=25, Fill=accent, StartSeconds=.05, EndSeconds=12, AnimationIn="Fade" },
            new CgLayer { Name="Title", Type="Text", Text=title, X=120, Y=880, Width=980, Height=64, FontSize=42, Fill="#FFFFFFFF", StartSeconds=.12, EndSeconds=12, AnimationIn="Slide Up" },
            new CgLayer { Name="Second", Type="Text", Text=second, X=122, Y=945, Width=930, Height=38, FontSize=24, Fill="#FFBBC4CC", StartSeconds=.2, EndSeconds=12, AnimationIn="Fade" }
        ]
    };

    private static CgProject LogoClock() => new()
    {
        Name = "Demo · Logo + Clocks",
        DurationSeconds = 3600,
        Layers =
        [
            new CgLayer { Name="Logo Bug", Role="Logo", Type="Image", Source=DemoPath("stills", "kashtrix-bug.png"), X=1490, Y=45, Width=370, Height=128, EndSeconds=3600, AnimationIn="Zoom" },
            new CgLayer { Name="Digital Clock", Type="DigitalClock", X=1540, Y=185, Width=310, Height=65, FontSize=32, Fill="#FFFFFFFF", Background="#C50A1017", EndSeconds=3600 },
            new CgLayer { Name="Date / Time", Type="DateTime", Text="dd MMM yyyy  HH:mm:ss", X=1390, Y=260, Width=460, Height=60, FontSize=28, Fill="#FFFFFFFF", Background="#B50A1017", EndSeconds=3600 },
            new CgLayer { Name="Analog Clock", Type="AnalogClock", X=1740, Y=335, Width=110, Height=110, Fill="#FFFFFFFF", Background="#B50A1017", EndSeconds=3600 },
            new CgLayer { Name="Timeline TC", Type="Timecode", X=1450, Y=455, Width=400, Height=64, FontSize=30, Fill="#FF68DE2E", Background="#B50A1017", EndSeconds=3600 }
        ]
    };

    private static CgProject TickerRoll() => new()
    {
        Name = "Demo · Ticker + Roll",
        DurationSeconds = 30,
        Layers =
        [
            new CgLayer { Name="Ticker", Type="Ticker", Text="MARKET UPDATE  NIFTY  +1.20%     WEATHER  28°C     NEXT: BUSINESS NEWS     KASHTRIX PLAYOUT", X=0, Y=1000, Width=1920, Height=80, FontSize=31, Fill="#FFFFFFFF", Background="#E60B1118", Speed=180, EndSeconds=30 },
            new CgLayer { Name="Roll", Type="Roll", Text="HEADLINES\nMorning News\nWeather Update\nBusiness News\nSports Roundup\nNational Bulletin", X=1370, Y=300, Width=470, Height=540, FontSize=34, Fill="#FFFFFFFF", Background="#C80B1118", Speed=48, EndSeconds=30 }
        ]
    };

    private static CgProject PromoBug() => new()
    {
        Name = "Demo · Shape Squeeze + Promo",
        DurationSeconds = 8,
        Layers =
        [
            new CgLayer { Name="L-Shape Squeeze", Role="Shape", Type="Shape", ShapeKind="L-Shape", X=0, Y=0, Width=1920, Height=1080, Background="#D516212C", EndSeconds=8, AnimationIn="Push Left", AnimationOut="Push Left", SqueezeProgram=true, SqueezeHorizontalMode="Left", SqueezeX=110, SqueezeY=70, SqueezeWidth=1450, SqueezeHeight=815, SqueezeInSeconds=.75, SqueezeOutSeconds=.75 },
            new CgLayer { Name="Promo Title", Type="Text", Text="UP NEXT", X=1325, Y=835, Width=460, Height=45, FontSize=25, Fill="#FF68DE2E", EndSeconds=8, AnimationIn="Fade" },
            new CgLayer { Name="Promo Text", Type="Text", Text="KASHTRIX PRIME", X=1325, Y=882, Width=460, Height=62, FontSize=42, Fill="#FFFFFFFF", EndSeconds=8, AnimationIn="Slide Up" }
        ]
    };

    private static CgProject MediaShowcase() => new()
    {
        Name = "Demo · Media + Chromium HTML",
        DurationSeconds = 12,
        Layers =
        [
            new CgLayer { Name="Demo Video", Type="Video", Source=DemoPath("video", "kashtrix-video-layer.mp4"), X=95, Y=105, Width=760, Height=430, StartSeconds=0, EndSeconds=12, AnimationIn="Zoom", AnimationOut="Fade" },
            new CgLayer { Name="PNG Sequence", Type="ImageSequence", Source=DemoPath("image-sequence"), X=80, Y=790, Width=960, Height=180, StartSeconds=1, EndSeconds=12, AnimationIn="Slide Left", AnimationOut="Fade" },
            new CgLayer { Name="Alpha Bug", Role="Logo", Type="Image", Source=DemoPath("stills", "kashtrix-bug.png"), X=1360, Y=85, Width=470, Height=162, StartSeconds=.5, EndSeconds=12, AnimationIn="Zoom" }
        ],
        HtmlSources =
        [
            new CgHtmlSource { Name="Chromium Breaking News", Source=DemoPath("html", "breaking-news.html"), SourceKind="File", X=0, Y=0, Width=1920, Height=1080, CanvasWidth=1920, CanvasHeight=1080, BrowserFps=30, Transparent=true, Visible=true, DataJson="{\"headline\":\"Kashtrix Chromium HTML demo\",\"subheadline\":\"CSS + JavaScript graphics are live\"}" }
        ]
    };
}
