using BroadcastPlayout.Models;

namespace BroadcastPlayout.Services;

/// <summary>
/// Fresh native broadcast-graphics demo catalog.  The designs deliberately use only editable
/// Kashtrix layers: no legacy demo artwork is required.  The visual language is modular,
/// asymmetric and motion-first: deep neutral plates, vivid gradients, editorial typography,
/// independent corner radii and clean information hierarchy.
/// </summary>
public static class CgUniqueDemoFactory
{
    #region Playout Template Names
    public const string PrimeBreakingDemoName = "Prime HD · Breaking News Live";
    public const string PrimeFlashDemoName = "Prime HD · Flash News Live";
    public const string PrimeBreakingScreenDemoName = "Prime HD · Breaking Screen Live";
    public const string PrimeLivePlateDemoName = "Prime HD · Live Plate";
    public const string PrimeNamePlateDemoName = "Prime HD · Name Plate Lower Third";
    public const string PrimeLogoDemoName = "Prime HD · Station Logo Bug";
    public const string PrimeTickerDemoName = "Prime HD · News Ticker Scroll";

    public const string Ap1hdBreakingDemoName = "AP1 HD · Breaking News Live";
    public const string Ap1hdFlashDemoName = "AP1 HD · Flash News Live";
    public const string Ap1hdThreeWindowsDemoName = "AP1 HD · 3 Windows Breaking";
    public const string Ap1hdFullBreakingDemoName = "AP1 HD · Full Breaking Screen";
    public const string Ap1hdTickerDemoName = "AP1 HD · News Ticker Scroll";
    public const string Ap1hdLivePlateDemoName = "AP1 HD · Live Plate";
    public const string Ap1hdLogoDemoName = "AP1 HD · Station Logo Bug";
    public const string Ap1hdWeatherDemoName = "AP1 HD · Weather Live";

    public const string Public4kNewsUpdateDemoName = "Public 4K · News Update Live";

    public const string Space4kBreakingDemoName = "Space 4K · Breaking News Live";
    public const string Space4kFlashDemoName = "Space 4K · Flash News Live";
    public const string Space4kTopBarDemoName = "Space 4K · TopBar Live News";
    public const string Space4kLiveDemoName = "Space 4K · Live Plate";
    public const string Space4kLogoDemoName = "Space 4K · Station Logo Bug";
    public const string Space4kSambadDemoName = "Space 4K · Sambad Live Plate";
    public const string Space4kTickerDemoName = "Space 4K · News Ticker Scroll";

    public const string MusicNowPlayingDemoName = "Music · Now Playing Lower Third";
    public const string MusicComingUpDemoName = "Music · Coming Up Lower Third";
    public const string ProgramNowPlayingDemoName = "Program · Now Playing Lower Third";
    public const string ProgramNextDemoName = "Program · Next Lower Third";
    public const string ProgramComingUpDemoName = "Program · Coming Up Lower Third";
    public const string CommercialBackInDemoName = "Commercial · Back In Countdown Lower Third";
    public const string EventTimerDemoName = "Broadcast · Event Timer Countdown";
    public const string NewsHighlightsDemoName = "News · Highlights 4-Line Showcase";
    public const string CategoryTickerDemoName = "News · Category Animated Ticker";
    public const string CategoryPushTickerDemoName = "News · Category Push Ticker";
    public const string LogoStationIdentDemoName = "Logo · Station Ident Bug";
    public const string FullscreenWorldClockDemoName = "Fullscreen · World Clock";
    public const string FullscreenWeatherDemoName = "Fullscreen · Weather Forecast";
    #endregion

    #region Playout Templates API
    public static List<CgProject> CreatePlayoutTemplates() => CreateCuratedTemplates();

    public static List<CgProject> CreateCuratedTemplates() =>
    [
        // Prime HD Channel Package
        CgDemoValidator.NormalizeDemoProject(CreatePrimeBreakingDemo()),
        CgDemoValidator.NormalizeDemoProject(CreatePrimeFlashDemo()),
        CgDemoValidator.NormalizeDemoProject(CreatePrimeBreakingScreenDemo()),
        CgDemoValidator.NormalizeDemoProject(CreatePrimeLivePlateDemo()),
        CgDemoValidator.NormalizeDemoProject(CreatePrimeNamePlateDemo()),
        CgDemoValidator.NormalizeDemoProject(CreatePrimeLogoDemo()),
        CgDemoValidator.NormalizeDemoProject(CreatePrimeTickerDemo()),

        // AP1 HD Channel Package
        CgDemoValidator.NormalizeDemoProject(CreateAp1hdBreakingDemo()),
        CgDemoValidator.NormalizeDemoProject(CreateAp1hdFlashDemo()),
        CgDemoValidator.NormalizeDemoProject(CreateAp1hdThreeWindowsDemo()),
        CgDemoValidator.NormalizeDemoProject(CreateAp1hdFullBreakingDemo()),
        CgDemoValidator.NormalizeDemoProject(CreateAp1hdTickerDemo()),
        CgDemoValidator.NormalizeDemoProject(CreateAp1hdLivePlateDemo()),
        CgDemoValidator.NormalizeDemoProject(CreateAp1hdLogoDemo()),
        CgDemoValidator.NormalizeDemoProject(CreateAp1hdWeatherDemo()),

        // Space 4K Channel Package
        CgDemoValidator.NormalizeDemoProject(CreateSpace4kBreakingDemo()),
        CgDemoValidator.NormalizeDemoProject(CreateSpace4kFlashDemo()),
        CgDemoValidator.NormalizeDemoProject(CreateSpace4kTopBarDemo()),
        CgDemoValidator.NormalizeDemoProject(CreateSpace4kLiveDemo()),
        CgDemoValidator.NormalizeDemoProject(CreateSpace4kLogoDemo()),
        CgDemoValidator.NormalizeDemoProject(CreateSpace4kSambadDemo()),
        CgDemoValidator.NormalizeDemoProject(CreateSpace4kTickerDemo()),

        // Public 4K Package
        CgDemoValidator.NormalizeDemoProject(CreatePublic4kNewsUpdateDemo()),

        // Music & Program suite
        CgDemoValidator.NormalizeDemoProject(CreateMusicNowPlayingDemo()),
        CgDemoValidator.NormalizeDemoProject(CreateMusicComingUpDemo()),
        CgDemoValidator.NormalizeDemoProject(CreateProgramNowPlayingDemo()),
        CgDemoValidator.NormalizeDemoProject(CreateProgramNextDemo()),
        CgDemoValidator.NormalizeDemoProject(CreateProgramComingUpDemo()),
        CgDemoValidator.NormalizeDemoProject(CreateCommercialBackInDemo()),
        CgDemoValidator.NormalizeDemoProject(CreateEventTimerDemo()),
        CgDemoValidator.NormalizeDemoProject(CreateNewsHighlightsDemo()),
        CgDemoValidator.NormalizeDemoProject(CreateCategoryTickerDemo()),
        CgDemoValidator.NormalizeDemoProject(CreateCategoryPushTickerDemo()),
        CgDemoValidator.NormalizeDemoProject(CreateLogoStationIdentDemo()),

        // Full-screen clock & weather
        CgDemoValidator.NormalizeDemoProject(CreateFullscreenWorldClockDemo()),
        CgDemoValidator.NormalizeDemoProject(CreateFullscreenWeatherDemo())
    ];

    public static CgProject? CreatePlayoutTemplate(string name)
    {
        if (name == CommercialBackInDemoName) return CreateCommercialBackInDemo();
        if (name == EventTimerDemoName) return CreateEventTimerDemo();
        if (name == MusicNowPlayingDemoName) return CreateMusicNowPlayingDemo();
        if (name == MusicComingUpDemoName) return CreateMusicComingUpDemo();
        if (name == ProgramNowPlayingDemoName) return CreateProgramNowPlayingDemo();
        if (name == ProgramNextDemoName) return CreateProgramNextDemo();
        if (name == ProgramComingUpDemoName) return CreateProgramComingUpDemo();
        if (name == NewsHighlightsDemoName) return CreateNewsHighlightsDemo();
        if (name == CategoryTickerDemoName) return CreateCategoryTickerDemo();
        if (name == CategoryPushTickerDemoName) return CreateCategoryPushTickerDemo();
        if (name == LogoStationIdentDemoName) return CreateLogoStationIdentDemo();
        if (name == FullscreenWorldClockDemoName) return CreateFullscreenWorldClockDemo();
        if (name == FullscreenWeatherDemoName) return CreateFullscreenWeatherDemo();

        if (name == PrimeBreakingDemoName) return CreatePrimeBreakingDemo();
        if (name == PrimeFlashDemoName) return CreatePrimeFlashDemo();
        if (name == PrimeBreakingScreenDemoName) return CreatePrimeBreakingScreenDemo();
        if (name == PrimeLivePlateDemoName) return CreatePrimeLivePlateDemo();
        if (name == PrimeNamePlateDemoName) return CreatePrimeNamePlateDemo();
        if (name == PrimeLogoDemoName) return CreatePrimeLogoDemo();
        if (name == PrimeTickerDemoName) return CreatePrimeTickerDemo();

        if (name == Ap1hdBreakingDemoName) return CreateAp1hdBreakingDemo();
        if (name == Ap1hdFlashDemoName) return CreateAp1hdFlashDemo();
        if (name == Ap1hdThreeWindowsDemoName) return CreateAp1hdThreeWindowsDemo();
        if (name == Ap1hdFullBreakingDemoName) return CreateAp1hdFullBreakingDemo();
        if (name == Ap1hdTickerDemoName) return CreateAp1hdTickerDemo();
        if (name == Ap1hdLivePlateDemoName) return CreateAp1hdLivePlateDemo();
        if (name == Ap1hdLogoDemoName) return CreateAp1hdLogoDemo();
        if (name == Ap1hdWeatherDemoName) return CreateAp1hdWeatherDemo();

        if (name == Public4kNewsUpdateDemoName) return CreatePublic4kNewsUpdateDemo();

        if (name == Space4kBreakingDemoName) return CreateSpace4kBreakingDemo();
        if (name == Space4kFlashDemoName) return CreateSpace4kFlashDemo();
        if (name == Space4kTopBarDemoName) return CreateSpace4kTopBarDemo();
        if (name == Space4kLiveDemoName) return CreateSpace4kLiveDemo();
        if (name == Space4kLogoDemoName) return CreateSpace4kLogoDemo();
        if (name == Space4kSambadDemoName) return CreateSpace4kSambadDemo();
        if (name == Space4kTickerDemoName) return CreateSpace4kTickerDemo();

        var idx = Array.IndexOf(Names, name);
        if (idx >= 0) return CgDemoValidator.NormalizeDemoProject(Build(idx, Names[idx]));
        return null;
    }
    #endregion


    private static readonly string[] Names =
    [
        "Election Flash · Elastic Native Overlay", "Breaking News · PNG Sequence Data Cycler", "News Update · Public4K Native Recreation",
        "Lower Third · Anchor Sweep", "Lower Third · Glass Interview", "Lower Third · Reporter Locator", "Lower Third · Guest Quote", "Lower Third · Two-Line Corporate",
        "Breaking · Red Alert Flip", "Breaking · Amber Urgent", "Breaking · Nepali Shiny Fullframe", "Breaking · Full Width Pulse", "Fullframe · Six Bullet Slate", "Headline · Dark Glass",
        "Ticker · Continuous Newswire", "Ticker · Push Headlines", "Ticker · Finance Tape", "Ticker · Sports Results", "Ticker · Election Counts", "Ticker · Weather Alerts",
        "Roll · Credits Clean", "Roll · Headlines Stack", "Roll · Election Candidates", "Roll · Sponsor Credits", "Scorebug · Football", "Scorebug · Cricket",
        "Scorebug · Basketball", "Scorebug · Tennis", "Sports · Match Intro", "Sports · Player Stat", "Sports · Fixture Card", "Sports · Result Fullframe",
        "Finance · Market Open", "Finance · Stock Movers", "Finance · Currency Board", "Finance · Commodity Strip", "Finance · Crypto Board", "Finance · Closing Bell",
        "Election · Candidate Card", "Election · Seat Counter", "Election · Constituency Result", "Election · Winner Banner", "Election · Vote Share", "Election · Map Legend",
        "Weather · Kathmandu Current", "Weather · Seven Day Forecast", "Weather · Storm Warning", "Weather · City Temperatures", "Weather · Sunrise Sunset", "Weather · Air Quality",
        "Promo · Tonight", "Promo · Next Program", "Promo · Tomorrow", "Promo · Weekend", "Promo · Countdown", "Promo · Coming Soon",
        "Music · Now Playing", "Music · Coming Up", "Music · Artist Card", "Music · Album Release", "Music · Chart Position", "Music · Lyrics Strap",
        "Social · Comment Card", "Social · Hashtag Wall", "Social · Poll Result", "Social · Viewer Message", "Social · QR Follow", "Social · Trending Topic",
        "Newsroom · Topic Bug", "Newsroom · Live Locator", "Newsroom · Reporter Two Box", "Newsroom · Quote Fullframe", "Newsroom · Fact Box", "Newsroom · Timeline Card",
        "Business · CEO Quote", "Business · Company Profile", "Business · Earnings Card", "Business · Deal Value", "Business · Data Table", "Business · KPI Dashboard",
        "L-Shape · Interview Squeeze", "U-Shape · Debate Squeeze", "L-Shape · Sports Squeeze", "U-Shape · Election Squeeze", "Shape · Sponsor Frame", "Shape · Picture Window",
        "Clock · Digital Clean", "Clock · Analog Studio", "Clock · Countdown Event", "Clock · Dual Timezone", "Clock · Date Time Ribbon", "Clock · Election Deadline",
        "Logo · Station Ident", "Logo · Sponsor Bug", "Logo · Watermark Glass", "Logo · Live Bug", "Fullframe · Bulletin Open", "Fullframe · End Board",
        "Data · URL JSON Headlines", "Data · RSS Story Rotator",
        "News · Crystal Headline Lower Third",
        "News · Blue Glass Anchor Lower Third",
        "News · Red Breaking Lower Third",
        "News · Live Reporter Locator",
        "News · Headline Fullscreen Crystal",
        "News · Top Stories Stack",
        "News · Over Shoulder Picture Box",
        "News · Interview Name Strap",
        "News · Bulletin Open Layers",
        "News · Live Bug + Clock",
        "News · Two Box Debate",
        "News · Three Box Panel",
        "News · Alert Ribbon",
        "News · Newswire Ticker Glass",
        "News · Quote Card",
        "News · Fact Check Card",
        "News · Timeline Explainer",
        "News · Data Headline",
        "News · Special Report Opener",
        "News · Coming Up Next",
        "Weather · Crystal Current Conditions",
        "Weather · Glass Seven Day Forecast",
        "Weather · Hourly Forecast Ribbon",
        "Weather · Animated Sun + Cloud",
        "Weather · Animated Rain Card",
        "Weather · Animated Thunder Alert",
        "Weather · Animated Snow Card",
        "Weather · Animated Fog Card",
        "Weather · Wind Compass",
        "Weather · UV Index Glass",
        "Weather · Humidity Pressure Card",
        "Weather · Sunrise Moon Phase",
        "Weather · Multi City Board",
        "Weather · Airport Weather",
        "Weather · Mountain Forecast",
        "Weather · Monsoon Alert",
        "Weather · Heat Warning",
        "Weather · Cold Warning",
        "Weather · Radar Lower Third",
        "Weather · Forecast Fullscreen Crystal",
        "Entertainment · Hollywood Live Lower Third",
        "Entertainment · Celebrity Interview Strap",
        "Entertainment · Red Carpet Opener",
        "Entertainment · Movie Premiere Fullscreen",
        "Entertainment · Coming Up Bumper",
        "Entertainment · Top Stories OTS",
        "Entertainment · Awards Nominee Card",
        "Entertainment · Winner Reveal",
        "Entertainment · Box Office Chart",
        "Entertainment · Streaming Picks",
        "Entertainment · Celebrity Quote",
        "Entertainment · Photo Gallery Frame",
        "Entertainment · Neon Gossip Ticker",
        "Entertainment · Tonight Promo",
        "Entertainment · Weekend Guide",
        "Entertainment · Live From Event",
        "Entertainment · Social Buzz Card",
        "Entertainment · Star Profile",
        "Entertainment · Behind The Scenes",
        "Entertainment · Endboard Crystal",
        "Music · Neon Live Opener",
        "Music · Artist Lower Third Crystal",
        "Music · Song Now Playing Glass",
        "Music · Album Artwork Card",
        "Music · Tour Dates Board",
        "Music · Countdown Top Ten",
        "Music · Lyrics Karaoke Strap",
        "Music · DJ Name Bug",
        "Music · Festival Lineup",
        "Music · Concert Coming Up",
        "Music · Equalizer Fullscreen",
        "Music · Radio Visualizer",
        "Music · New Release Promo",
        "Music · Chart Climber",
        "Music · Artist Quote",
        "Music · Stage Lower Third",
        "Music · Acoustic Session",
        "Music · Request Line Ticker",
        "Music · Music News OTS",
        "Music · End Credits Neon",
        "Morning Show · Sunrise Opener",
        "Morning Show · Host Lower Third",
        "Morning Show · Guest Lower Third",
        "Morning Show · Healthy Living Card",
        "Morning Show · Today Tips",
        "Morning Show · Recipe Steps",
        "Morning Show · Coming Up",
        "Morning Show · Location Weather",
        "Morning Show · Social Question",
        "Morning Show · Closing Board",
        "Sports · Crystal Scorebug",
        "Sports · Player Lower Third",
        "Sports · Team Lineup",
        "Sports · Match Stats",
        "Sports · Game Day Opener",
        "Sports · Result Board",
        "Sports · Fixture List",
        "Sports · Breaking Transfer",
        "Sports · Live Venue Locator",
        "Sports · Champions Fullscreen"
    ];

    private static readonly string[] EntryMotions = ["Fade", "Slide Left", "Slide Right", "Slide Up", "Slide Down", "Zoom", "Pop", "Push Left", "Push Right", "Wipe Left", "Wipe Right", "Blur"];
    private static readonly string[] ExitMotions = ["Fade", "Slide Left", "Slide Right", "Slide Up", "Slide Down", "Zoom", "Push Left", "Push Right", "Wipe Left", "Wipe Right", "Blur"];
    private static readonly string[] TextMotionPresets = ["Type On", "Fade Cascade", "Rise Cascade", "Tracking Reveal", "Pop Cascade"];

    private static readonly (string A, string B, string Ink, string Soft)[] Palettes =
    [
        ("#FF7A3CFF", "#FFFF3D91", "#FFFFFFFF", "#FF171426"),
        ("#FF04B8C7", "#FF55E5D6", "#FFFFFFFF", "#FF071B21"),
        ("#FFFF4C5F", "#FFFF9E3D", "#FFFFFFFF", "#FF241014"),
        ("#FF326BFF", "#FF8E5CFF", "#FFFFFFFF", "#FF0B1531"),
        ("#FFFFB12B", "#FFFFE27A", "#FF16130B", "#FF2A210B"),
        ("#FF16C784", "#FF78F2B5", "#FFFFFFFF", "#FF082219")
    ];

    public static List<CgProject> Create() => CreateCuratedTemplates();

    public static List<CgProject> CreateLegacy200Catalog()
    {
        var list = new List<CgProject>(Names.Length);
        for (var i = 0; i < Names.Length; i++)
            list.Add(CgDemoValidator.NormalizeDemoProject(Build(i, Names[i])));
        return list;
    }

    private static CgProject Build(int index, string name)
    {
        var pal = Palettes[index % Palettes.Length];
        var duration = 10 + (index % 4) * 2;
        var p = new CgProject { Name = name, DurationSeconds = duration };
        var g = new CgGroup { Name = "Master Motion", OriginX = 960, OriginY = 540 };
        p.Groups.Add(g);

        if (name.StartsWith("News ·", StringComparison.OrdinalIgnoreCase)) NewsPackage(p,g.Id,index,pal);
        else if (name.StartsWith("Entertainment ·", StringComparison.OrdinalIgnoreCase)) EntertainmentPackage(p,g.Id,index,pal);
        else if (name.StartsWith("Morning Show ·", StringComparison.OrdinalIgnoreCase)) MorningShowPackage(p,g.Id,index,pal);
        else if (name.StartsWith("Lower Third", StringComparison.OrdinalIgnoreCase)) LowerThird(p,g.Id,index,pal);
        else if (name.StartsWith("Ticker", StringComparison.OrdinalIgnoreCase)) Ticker(p,g.Id,index,pal);
        else if (name.StartsWith("Breaking", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Headline", StringComparison.OrdinalIgnoreCase)) Breaking(p,g.Id,index,pal);
        else if (name.StartsWith("Sports", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Scorebug", StringComparison.OrdinalIgnoreCase)) Sports(p,g.Id,index,pal);
        else if (name.StartsWith("Finance", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Business", StringComparison.OrdinalIgnoreCase)) DataCards(p,g.Id,index,pal,"MARKET");
        else if (name.StartsWith("Election", StringComparison.OrdinalIgnoreCase)) Leaderboard(p,g.Id,index,pal,"ELECTION");
        else if (name.StartsWith("Weather", StringComparison.OrdinalIgnoreCase)) Weather(p,g.Id,index,pal);
        else if (name.StartsWith("Promo", StringComparison.OrdinalIgnoreCase)) Promo(p,g.Id,index,pal);
        else if (name.StartsWith("Music", StringComparison.OrdinalIgnoreCase)) Music(p,g.Id,index,pal);
        else if (name.StartsWith("Social", StringComparison.OrdinalIgnoreCase)) Social(p,g.Id,index,pal);
        else if (name.StartsWith("Newsroom", StringComparison.OrdinalIgnoreCase)) Editorial(p,g.Id,index,pal,"NEWSROOM");
        else if (name.StartsWith("L-Shape", StringComparison.OrdinalIgnoreCase) || name.StartsWith("U-Shape", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Shape", StringComparison.OrdinalIgnoreCase)) Squeeze(p,g.Id,index,pal,name.StartsWith("U-Shape",StringComparison.OrdinalIgnoreCase));
        else if (name.StartsWith("Clock", StringComparison.OrdinalIgnoreCase)) Clock(p,g.Id,index,pal);
        else if (name.StartsWith("Logo", StringComparison.OrdinalIgnoreCase)) Logo(p,g.Id,index,pal);
        else if (name.StartsWith("Roll", StringComparison.OrdinalIgnoreCase)) Roll(p,g.Id,index,pal);
        else if (name.StartsWith("Data", StringComparison.OrdinalIgnoreCase)) DataDemo(p,g.Id,index,pal);
        else FullFrame(p,g.Id,index,pal);

        ApplyUniqueDesignFingerprint(p, index, pal);
        ApplyProjectMotionProfile(p, index);

        // Every demo has an authored accent keyframe so the catalog demonstrates the
        // timeline as well as one-click IN/OUT presets. The path varies by project index.
        var accent = p.Layers.FirstOrDefault(x => x.Type.Equals("Shape", StringComparison.OrdinalIgnoreCase));
        if (accent is not null)
        {
            var variant = index % 4;
            var dx = variant switch { 0 => -36d, 1 => 36d, _ => 0d };
            var dy = variant switch { 2 => -24d, 3 => 24d, _ => 0d };
            var rotation = variant is 1 or 3 ? 2.5 : -2.5;
            accent.Keyframes =
            [
                K(0, accent.X + dx, accent.Y + dy, accent.Width * .96, accent.Height * .96, rotation, .15, "power3.out"),
                K(.55 + (index % 3) * .08, accent.X, accent.Y, accent.Width, accent.Height, 0, 1, "back.out")
            ];
        }
        return p;
    }

    private static void NewsPackage(CgProject p, Guid g, int i, (string A,string B,string Ink,string Soft) c)
    {
        var variant = i % 5;
        if (variant == 0)
        {
            var plate = CrystalCard("Crystal lower third", 92, 788, 1320, 176, "#E9111A2C", c.Soft, g, 24);
            Add(p, plate);
            var shine = CrystalCard("Moving shine", 104, 800, 1250, 34, "#38FFFFFF", "#04FFFFFF", g, 17);
            shine.Opacity = .75; shine.Rotation = -2; Add(p, shine);
            Add(p, Shape("Accent", 92, 788, 22, 176, c.A, c.B, g, 12, 2, 12));
            Add(p, Text("Kicker", "LIVE / NEWS", 148, 808, 360, 40, 24, c.A, g, "Tracking Reveal"));
            Add(p, Text("Headline", "HEADLINE GOES HERE", 148, 850, 1080, 64, 44, c.Ink, g, "Rise Cascade"));
            Add(p, Text("Subheadline", "Secondary line for additional information", 150, 910, 1020, 36, 22, "#FFC7D4E8", g, "Fade Cascade"));
        }
        else if (variant == 1)
        {
            Add(p, Shape("World field",0,0,1920,1080,"#FF07182B","#FF123C69",g,0,0,0));
            var card=CrystalCard("Headline glass",130,220,1660,560,"#C91A2B4A","#9A0A1325",g,36); Add(p,card);
            Add(p, Text("Kicker","BREAKING NEWS",190,275,650,70,36,c.A,g,"Tracking Reveal"));
            Add(p, Text("Headline","MAJOR STORY DEVELOPING",190,380,1480,185,92,c.Ink,g,"Rise Cascade"));
            Add(p, Text("Deck","A layered crystal fullscreen designed for fast newsroom updates.",195,615,1290,90,34,"#FFD5E4F2",g,"Fade Cascade"));
        }
        else if (variant == 2)
        {
            var imageBox=CrystalCard("OTS frame",1235,150,560,390,"#DD17263B","#AA07111D",g,26); Add(p,imageBox);
            Add(p, Shape("OTS inner",1262,178,506,334,"#FF193B5B","#FF071421",g,18,18,18));
            Add(p, Text("OTS label","NEW POLICIES",1300,390,420,56,34,c.Ink,g,"Rise Cascade"));
            Add(p, Text("Headline","TOP STORY",140,700,700,70,50,c.A,g,"Tracking Reveal"));
            Add(p, Text("Summary","Studio over-shoulder / picture-box template",140,790,980,65,34,c.Ink,g,"Fade Cascade"));
        }
        else if (variant == 3)
        {
            Add(p, Shape("Ticker base",0,900,1920,180,"#F20A101B","#E4141C2C",g,0,0,0));
            Add(p, Shape("Live tag",0,900,300,180,c.A,c.B,g,0,26,0));
            Add(p, Text("Live","BREAKING",44,948,220,52,30,c.Ink,g,"Type On"));
            var ticker=Text("News ticker","GLOBAL MARKETS • WEATHER • POLITICS • SPORTS • LIVE UPDATES",340,925,1530,110,32,c.Ink,g,"None");
            ticker.Type="Ticker"; ticker.TickerMode="Continuous"; ticker.Speed=150; ticker.TickerGap=120; Add(p,ticker);
        }
        else
        {
            for (var n=0;n<3;n++)
            {
                var x=90+n*600; var card=CrystalCard($"Story card {n+1}",x,250,540,470,n==0?c.A:"#D9152030",n==0?c.B:"#B20B111B",g,26); Add(p,card);
                Add(p,Text($"Story title {n+1}",$"TOP STORY {n+1}",x+35,300,455,75,42,c.Ink,g,"Rise Cascade"));
                Add(p,Text($"Story text {n+1}","Reusable newsroom panel",x+35,420,445,90,28,"#FFD1D9E4",g,"Fade Cascade"));
            }
        }
    }

    private static void EntertainmentPackage(CgProject p, Guid g, int i, (string A,string B,string Ink,string Soft) c)
    {
        Add(p, Shape("Backdrop",0,0,1920,1080,"#FF16081F","#FF4D0D68",g,0,0,0));
        var hero=CrystalCard("Entertainment glass",105,120,1710,780,"#B92D103B","#7A120B23",g,42); hero.GlowColor="#88FF65EA"; hero.GlowRadius=8; Add(p,hero);
        var flare=Shape("Shine ribbon",40,250,1840,90,"#20FFFFFF","#00FFFFFF",g,45,4,45,"Rectangle",-8); flare.GlossEnabled=true; flare.GlossOpacity=.38; Add(p,flare);
        Add(p, Text("Brand","ENTERTAINMENT",155,180,690,64,36,"#FFFFD5FA",g,"Tracking Reveal"));
        Add(p, Text("Title", i%2==0?"HOLLYWOOD LIVE":"TONIGHT / SPECIAL",155,305,1320,160,92,c.Ink,g,"Rise Cascade"));
        Add(p, Text("Deck","Stars · stories · premieres · culture",160,500,1100,70,34,"#FFE8CFF1",g,"Fade Cascade"));
        for(var n=0;n<3;n++){var x=160+n*510;var chip=CrystalCard($"Story chip {n}",x,660,450,120,"#9AFFFFFF","#20000000",g,24);Add(p,chip);Add(p,Text($"Chip text {n}",new[]{"TOP STORIES","COMING UP","LIVE EVENT"}[n],x+30,690,390,52,28,c.Ink,g,"Type On"));}
    }

    private static void MorningShowPackage(CgProject p, Guid g, int i, (string A,string B,string Ink,string Soft) c)
    {
        Add(p, Shape("Warm background",0,0,1920,1080,"#FFFFF6DD","#FFFFC66D",g,0,0,0));
        var card=CrystalCard("Morning card",110,150,1700,760,"#D9FFFFFF","#75FFF6D5",g,38); card.BorderColor="#99FFFFFF"; Add(p,card);
        Add(p, Shape("Sun disc",1420,180,210,210,"#FFFFC62E","#FFFFF09A",g,105,105,105));
        Add(p, Text("Kicker","GOOD MORNING",170,205,760,75,46,"#FF17508A",g,"Tracking Reveal"));
        Add(p, Text("Title",i%2==0?"A BRIGHTER DAY AHEAD":"COMING UP NEXT",170,330,1180,150,78,"#FF102E4E",g,"Rise Cascade"));
        Add(p, Text("Info","Weather · lifestyle · interviews · community",175,525,1160,65,32,"#FF446079",g,"Fade Cascade"));
        for(var n=0;n<3;n++){var x=175+n*450;var item=CrystalCard($"Morning info {n}",x,690,400,115,"#CCFFFFFF","#66FFF1C6",g,22);Add(p,item);Add(p,Text($"Morning text {n}",new[]{"TODAY'S TIPS","HEALTHY LIVING","YOUR WEATHER"}[n],x+24,722,350,42,24,"#FF184469",g,"Type On"));}
    }

    private static void LowerThird(CgProject p, Guid g, int i, (string A,string B,string Ink,string Soft) c)
    {
        Add(p, Shape("Accent rail", 88, 790, 18, 190, c.A, c.B, g, 9, 9, 2));
        Add(p, Shape("Identity plate", 122, 810, 1000, 150, c.Soft, "#EA25213A", g, 22, 4, 22));
        Add(p, Text("Name", i%2==0?"ALEX MORGAN":"LIVE INTERVIEW", 165, 826, 900, 65, 46, c.Ink, g, "Rise Cascade"));
        Add(p, Text("Role", "Senior Correspondent  •  Kathmandu", 168, 895, 840, 46, 26, "#FFC8C4D8", g, "Fade Cascade"));
        Add(p, Shape("Status chip", 930, 835, 160, 44, c.A, c.B, g, 18, 18, 18));
        Add(p, Text("Status", "LIVE", 950, 840, 120, 35, 21, c.Ink, g, "Type On"));
    }

    private static void Breaking(CgProject p, Guid g, int i, (string A,string B,string Ink,string Soft) c)
    {
        Add(p, Shape("Full alert field", 0, 0, 1920, 1080, "#FF10111A", c.Soft, g, 0, 0, 0));
        Add(p, Shape("Diagonal energy", -120, 80, 960, 160, c.A, c.B, g, 0, 40, 0, "Rectangle", -8));
        Add(p, Text("Alert kicker", "BREAKING / LIVE", 150, 150, 1100, 90, 44, c.A, g, "Tracking Reveal"));
        Add(p, Text("Headline", "MAJOR UPDATE DEVELOPING NOW", 150, 280, 1560, 250, 104, c.Ink, g, "Rise Cascade"));
        Add(p, Text("Summary", "A clean, editable broadcast composition with native motion and strong editorial hierarchy.", 155, 580, 1300, 110, 36, "#FFD9D9E5", g, "Fade Cascade"));
        Add(p, Shape("Bottom rule", 150, 770, 1280, 10, c.A, c.B, g, 5,5,5));
    }

    private static void Ticker(CgProject p, Guid g, int i, (string A,string B,string Ink,string Soft) c)
    {
        Add(p, Shape("Ticker glass", 0, 915, 1920, 165, "#F20B0E18", c.Soft, g, 0,0,0));
        Add(p, Shape("Topic tag", 0, 915, 315, 165, c.A, c.B, g, 0, 28, 0));
        Add(p, Text("Tag", i%2==0?"LATEST":"UPDATE", 40, 955, 235, 58, 30, c.Ink, g, "Type On"));
        var t = Text("Crawl", "KASHTRIX • MODERN BROADCAST GRAPHICS • LIVE DATA • SPORTS • NEWS • WEATHER", 350, 930, 1570, 120, 34, c.Ink, g, "None");
        t.Type="Ticker"; t.TickerMode=i%2==0?"Continuous":"Push"; t.TickerDirection="Left"; t.Speed=140+(i%4)*20; t.TickerGap=120; Add(p,t);
    }

    private static void Sports(CgProject p, Guid g, int i, (string A,string B,string Ink,string Soft) c)
    {
        Add(p, Text("Title", "WHO LEADS TONIGHT?", 150, 92, 800, 90, 50, c.Ink, g, "Tracking Reveal"));
        var teams = new[]{("01","NORTH CITY","35 PTS"),("02","UNITED","34 PTS"),("03","ATHLETIC","33 PTS"),("04","RACING","33 PTS"),("05","SPORTING","32 PTS")};
        for(int n=0;n<teams.Length;n++)
        {
            var y=230+n*145; var primary=n==0?c.A:(n%2==0?"#FF203050":"#FF171B31");
            Add(p, Shape($"Rank card {n+1}", 150, y, 1500, 112, primary, n==0?c.B:"#FF2B3355", g, n==0?20:8, 20, 8));
            Add(p, Text($"Rank {n+1}", teams[n].Item1, 188, y+20, 130, 68, 44, c.Ink, g, "Rise Cascade"));
            Add(p, Text($"Team {n+1}", teams[n].Item2, 380, y+18, 700, 70, 48, c.Ink, g, "Fade Cascade"));
            Add(p, Text($"Points {n+1}", teams[n].Item3, 1330, y+25, 230, 55, 31, c.Ink, g, "Type On"));
        }
    }

    private static void Leaderboard(CgProject p, Guid g, int i, (string A,string B,string Ink,string Soft) c, string label)
    {
        Add(p, Shape("Background", 0,0,1920,1080,"#FF101626",c.Soft,g,0,0,0));
        Add(p, Text("Section", label, 140,95,850,80,54,c.A,g,"Tracking Reveal"));
        for(int n=0;n<4;n++)
        {
            var y=250+n*160; var pct=72-n*9;
            Add(p, Text($"Candidate {n+1}", $"CANDIDATE {n+1}", 155,y,430,64,36,c.Ink,g,"Fade Cascade"));
            Add(p, Shape($"Bar base {n+1}", 600,y+12,920,48,"#FF252B3B","#FF252B3B",g,10,10,10));
            Add(p, Shape($"Bar fill {n+1}", 600,y+12,920*pct/100.0,48,c.A,c.B,g,10,10,10));
            Add(p, Text($"Value {n+1}", $"{pct}%", 1550,y,170,60,34,c.Ink,g,"Type On"));
        }
    }

    private static void Promo(CgProject p, Guid g, int i, (string A,string B,string Ink,string Soft) c)
    {
        Add(p, Shape("Backdrop",0,0,1920,1080,"#FFF0F2F5","#FFD5D9E1",g,0,0,0));
        Add(p, Text("Promo title", "TONIGHT ON KASHTRIX", 120,75,900,80,48,"#FF221B35",g,"Tracking Reveal"));
        var slots=new[]{("THE DARK TOWER","5:20 PM"),("FINDING THE QUEEN","8:30 PM"),("LATE SHOW","10:30 PM")};
        for(int n=0;n<3;n++)
        {
            var x=130+(n%2)*820; var y=220+n*225;
            Add(p, Shape($"Promo card {n+1}",x,y,740,165,n==1?c.A:"#FF351567",n==1?c.B:"#FF17114D",g,28,6,28));
            Add(p, Text($"Show {n+1}",slots[n].Item1,x+38,y+28,510,60,34,c.Ink,g,"Rise Cascade"));
            Add(p, Shape($"Time pill {n+1}",x+485,y+95,220,48,"#FFF0A72F","#FFFFC953",g,22,22,22));
            Add(p, Text($"Time {n+1}",slots[n].Item2,x+505,y+102,180,35,22,"#FFFFFFFF",g,"Type On"));
        }
    }

    private static void Weather(CgProject p, Guid g, int i, (string A,string B,string Ink,string Soft) c)
    {
        Add(p, Shape("Sky gradient",0,0,1920,1080,"#FF061A33","#FF1B7FC0",g,0,0,0));
        var main=CrystalCard("Crystal current conditions",95,100,700,820,"#A91A4772","#52102744",g,42); main.GlowColor="#665CCBFF"; main.GlowRadius=7; Add(p,main);
        var shine=Shape("Crystal shine",120,125,640,95,"#42FFFFFF","#00FFFFFF",g,40,12,40,"Rectangle",-4); shine.GlossEnabled=true; shine.GlossOpacity=.5; Add(p,shine);
        Add(p, Text("City","KATHMANDU",150,160,560,70,42,c.Ink,g,"Tracking Reveal"));
        Add(p, Text("Temp",$"{22 + i%9}°",145,300,360,190,142,c.Ink,g,"Rise Cascade"));
        var iconCodes=new[]{"01d","02d","10d","11d","13d","50d"};
        Add(p,new CgLayer{Name="Animated weather icon",Type="WeatherIcon",Text=iconCodes[i%iconCodes.Length],X=470,Y=285,Width=250,Height=230,GroupId=g,EndSeconds=p.DurationSeconds,GlowColor="#AAFFFFFF",GlowRadius=6,AnimationIn="Zoom",AnimationInSeconds=.55});
        Add(p, Text("Condition",new[]{"SUNNY","PARTLY CLOUDY","RAIN","THUNDERSTORM","SNOW","MIST"}[i%6],150,520,560,60,30,"#FFE6F5FF",g,"Fade Cascade"));
        Add(p, Text("Details","HUMIDITY 68%   WIND 12 km/h\nPRESSURE 1012 hPa   FEELS 23°",150,620,560,120,24,"#FFCDE8F8",g,"Fade Cascade"));

        for(int n=0;n<5;n++)
        {
            var x=845+n*198; var day=CrystalCard($"Forecast crystal {n}",x,250,174,410,"#78FFFFFF","#1A8EDCFF",g,24); day.GlossEnabled=true; day.GlossOpacity=.34; Add(p,day);
            Add(p,Text($"Day {n}",new[]{"MON","TUE","WED","THU","FRI"}[n],x+26,285,125,45,25,c.Ink,g,"Fade Cascade"));
            Add(p,new CgLayer{Name=$"Weather icon {n}",Type="WeatherIcon",Text=iconCodes[(i+n)%iconCodes.Length],X=x+18,Y=355,Width=138,Height=135,GroupId=g,EndSeconds=p.DurationSeconds,GlowRadius=4,GlowColor="#88FFFFFF",AnimationIn="Zoom",AnimationInSeconds=.45+n*.05});
            Add(p,Text($"Forecast temp {n}",$"{20+n+(i%4)}°",x+30,520,118,58,34,c.Ink,g,"Rise Cascade"));
        }
        var alert=CrystalCard("Weather data ribbon",845,705,965,135,"#A70A1727","#6A17496E",g,28); Add(p,alert);
        Add(p,Text("Weather data","LIVE FORECAST  •  RADAR  •  WIND  •  UV  •  AIR QUALITY",890,745,870,54,27,"#FFF1FAFF",g,"Tracking Reveal"));
    }

    private static void DataCards(CgProject p, Guid g, int i, (string A,string B,string Ink,string Soft) c, string title)
    {
        Add(p, Shape("Backdrop",0,0,1920,1080,"#FF0C1119",c.Soft,g,0,0,0)); Add(p, Text("Title",title,130,90,700,80,50,c.A,g,"Tracking Reveal"));
        var labels=new[]{"INDEX","REVENUE","GROWTH","VOLUME"}; var values=new[]{"18,420","$2.8B","+12.6%","4.31M"};
        for(int n=0;n<4;n++){var x=130+n*425;Add(p,Shape($"KPI {n}",x,280,365,300,"#FF161C29","#FF222B3E",g,18,18,18));Add(p,Text($"Label {n}",labels[n],x+35,325,280,50,25,"#FF96A1B5",g,"Fade Cascade"));Add(p,Text($"Value {n}",values[n],x+35,405,290,95,55,c.Ink,g,"Rise Cascade"));Add(p,Shape($"Rule {n}",x+35,525,220,7,c.A,c.B,g,4,4,4));}
    }

    private static void Music(CgProject p, Guid g, int i, (string A,string B,string Ink,string Soft) c)
    {
        Add(p,Shape("Music backdrop",0,0,1920,1080,"#FF08061B","#FF24105A",g,0,0,0));
        var album=CrystalCard("Album crystal",120,205,445,445,c.A,c.B,g,42); album.GlowColor="#996B6CFF"; album.GlowRadius=10; Add(p,album);
        Add(p,Text("Album glyph","♫",245,290,200,190,130,c.Ink,g,"Pop"));
        Add(p,Text("Now","NOW PLAYING",650,245,680,70,34,"#FFFF61E6",g,"Tracking Reveal"));
        Add(p,Text("Track",i%2==0?"MIDNIGHT SIGNAL":"NEON CITY",650,338,1000,120,72,c.Ink,g,"Rise Cascade"));
        Add(p,Text("Artist","KASHTRIX STUDIO SESSION",655,480,900,60,30,"#FFC7C7E6",g,"Fade Cascade"));
        for(var n=0;n<14;n++){var h=36+(n*37+i*23)%155;var bar=Shape($"EQ {n}",650+n*62,650-h,34,h,"#FF27D9FF","#FFFF3EDA",g,8,8,8);bar.GlowColor="#774EDCFF";bar.GlowRadius=4;bar.Keyframes=[K(0,bar.X,bar.Y,bar.Width,bar.Height,0,.35,"linear"),K(.7+(n%4)*.08,bar.X,bar.Y-h*.35,bar.Width,bar.Height+h*.35,0,1,"sine.inOut")];bar.KeyframeRepeat=99;bar.KeyframeYoyo=true;Add(p,bar);}
        var progress=CrystalCard("Progress",655,740,850,14,"#FF38DAFF","#FFFF4FD8",g,7); Add(p,progress);
    }
    private static void Social(CgProject p, Guid g, int i, (string A,string B,string Ink,string Soft) c)
    { Add(p,Shape("Comment card",240,220,1440,600,"#F51A1D2B","#F52B2340",g,34,10,34));Add(p,Shape("Avatar",310,300,120,120,c.A,c.B,g,60,60,60,"Ellipse"));Add(p,Text("Handle","@VIEWER_LIVE",480,300,700,65,34,c.A,g,"Type On"));Add(p,Text("Message","“Broadcast graphics should feel immediate, clean and alive.”",480,405,1000,200,58,c.Ink,g,"Rise Cascade"));Add(p,Text("Tag","#KASHTRIX",480,665,500,60,30,"#FFB5B2C8",g,"Fade Cascade")); }
    private static void Editorial(CgProject p, Guid g, int i, (string A,string B,string Ink,string Soft) c, string label)
    { Add(p,Shape("Editorial panel",90,120,1740,840,"#F3121620",c.Soft,g,28,6,28));Add(p,Text("Kicker",label,150,175,700,60,32,c.A,g,"Tracking Reveal"));Add(p,Text("Headline","THE STORY / EXPLAINED",150,285,1450,160,88,c.Ink,g,"Rise Cascade"));Add(p,Text("Body","One flexible composition can combine typography, geometry, data and keyframed motion without exposing unrelated controls.",155,505,1370,150,38,"#FFD0CFDA",g,"Fade Cascade")); }
    private static void Roll(CgProject p, Guid g, int i, (string A,string B,string Ink,string Soft) c)
    { Add(p,Text("Header","CREDITS / HEADLINES",420,130,1080,80,46,c.A,g,"Tracking Reveal")); var r=Text("Roll","PRODUCER   KASHTRIX\nDIRECTOR   BROADCAST TEAM\nGRAPHICS   CG EDITOR\nENGINEERING   PLAYOUT OPS\nTHANK YOU FOR WATCHING",500,260,920,650,42,c.Ink,g,"None");r.Type="Roll";r.Speed=75;Add(p,r); }
    private static void Squeeze(CgProject p, Guid g, int i, (string A,string B,string Ink,string Soft) c, bool u)
    { var sh=Shape("Program frame",40,40,1840,1000,"#50101724",c.A,g,26,26,26,u?"U-Shape":"L-Shape");sh.SqueezeProgram=true;sh.SqueezeHorizontalMode=u?"Both":"Left";sh.SqueezeX=260;sh.SqueezeY=110;sh.SqueezeWidth=1400;sh.SqueezeHeight=790;Add(p,sh);Add(p,Text("Label",u?"DEBATE / LIVE":"INTERVIEW / LIVE",110,870,700,70,38,c.Ink,g,"Rise Cascade")); }
    private static void Clock(CgProject p, Guid g, int i, (string A,string B,string Ink,string Soft) c)
    { Add(p,Shape("Clock plate",1220,70,560,180,"#EF111725",c.Soft,g,28,6,28));var clock=Text("Clock","00:00:00",1280,105,430,95,64,c.Ink,g,"Fade Cascade");clock.Type=i%3==1?"AnalogClock":"DigitalClock";Add(p,clock);Add(p,Text("Zone","KATHMANDU / LIVE",1285,196,390,35,19,c.A,g,"Type On")); }
    private static void Logo(CgProject p, Guid g, int i, (string A,string B,string Ink,string Soft) c)
    { Add(p,Shape("Bug plate",1510,70,300,130,"#E9131824",c.Soft,g,26,6,26));Add(p,Text("Wordmark","KX",1560,84,110,85,62,c.A,g,"Pop"));Add(p,Text("Channel","KASHTRIX",1660,103,120,45,21,c.Ink,g,"Tracking Reveal")); }
    private static void FullFrame(CgProject p, Guid g, int i, (string A,string B,string Ink,string Soft) c)
    { Add(p,Shape("Backdrop",0,0,1920,1080,"#FF0D1119",c.Soft,g,0,0,0));Add(p,Shape("Accent block",110,130,180,650,c.A,c.B,g,20,4,20));Add(p,Text("Headline","BULLETIN / OPEN",360,190,1280,160,92,c.Ink,g,"Rise Cascade"));Add(p,Text("Deck","Modern native broadcast graphics with editable color, gradient, outline and motion.",365,420,1120,150,42,"#FFD4D3DE",g,"Fade Cascade")); }

    private static void DataDemo(CgProject p, Guid g, int i, (string A,string B,string Ink,string Soft) c)
    {
        var d=new CgDataSource { Name=i%2==0?"URL JSON Headlines":"RSS Story Rotator", SourceType=i%2==0?"UrlJson":"Rss", Source=i%2==0?"https://jsonplaceholder.typicode.com/posts":"https://feeds.bbci.co.uk/news/rss.xml", RefreshSeconds=60, CachedItemsJson="[{\"title\":\"Fresh connected-data demo headline\"}]" };
        p.DataSources.Add(d); Add(p,Shape("Data plate",100,720,1720,250,"#F3131825",c.Soft,g,28,8,28));Add(p,Text("Data label",i%2==0?"LIVE JSON":"LIVE RSS",150,755,280,55,28,c.A,g,"Tracking Reveal"));var t=Text("Dynamic title","Connected data headline",150,830,1530,100,48,c.Ink,g,"Rise Cascade");t.DataSourceId=d.Id;t.DataField="title";t.DataItemDurationSeconds=4;Add(p,t);
    }


    private static void ApplyUniqueDesignFingerprint(CgProject project, int projectIndex, (string A,string B,string Ink,string Soft) palette)
    {
        // The catalog deliberately uses 200 unique design fingerprints, not a renamed clone set.
        // Each project gets its own geometry, typography, accent construction, corner language,
        // gradient direction and layer treatment while preserving broadcast-safe bounds.
        var layout = projectIndex % 20;
        var fontFamilies = new[] { "Segoe UI", "Arial", "Bahnschrift", "Trebuchet MS", "Georgia", "Verdana", "Tahoma", "Times New Roman", "Consolas", "Century Gothic" };
        var font = fontFamilies[projectIndex % fontFamilies.Length];
        var mirrorX = layout is 3 or 7 or 11 or 15 or 19;
        var compact = layout is 4 or 9 or 14 or 18;
        var angle = (projectIndex * 17 + layout * 11) % 360;
        var shapeKinds = new[] { "Rectangle", "Ellipse", "Line", "Rectangle", "Rectangle" };

        for (var i = 0; i < project.Layers.Count; i++)
        {
            var layer = project.Layers[i];
            var fullFrame = layer.X <= 1 && layer.Y <= 1 && layer.Width >= 1918 && layer.Height >= 1078;
            if (!fullFrame)
            {
                var dx = ((projectIndex * 31 + i * 47) % 91) - 45;
                var dy = ((projectIndex * 19 + i * 29) % 61) - 30;
                var scale = compact ? .93 : 1.0 + (((projectIndex + i) % 5) - 2) * .015;
                layer.Width = Math.Clamp(layer.Width * scale, 10, 1840);
                layer.Height = Math.Clamp(layer.Height * scale, 8, 1000);
                layer.X = Math.Clamp(layer.X + dx, 0, 1920 - layer.Width);
                layer.Y = Math.Clamp(layer.Y + dy, 0, 1080 - layer.Height);
                if (mirrorX) layer.X = Math.Clamp(1920 - layer.X - layer.Width, 0, 1920 - layer.Width);
            }

            layer.GradientAngle = angle;
            if (layer.Type.Equals("Text", StringComparison.OrdinalIgnoreCase))
            {
                layer.FontFamily = font;
                layer.LetterSpacing = ((projectIndex + i) % 5) * .35;
                layer.HorizontalTextAlignment = (layout % 4) switch { 1 => "Center", 2 => "Right", _ => "Left" };
                layer.Bold = (projectIndex + i) % 4 != 0;
                layer.Italic = (projectIndex + i) % 11 == 0;
                layer.OutlineWidth = layer.FontSize > 72 ? .8 + (projectIndex % 3) * .35 : (projectIndex % 2) * .35;
            }
            else if (layer.Type.Equals("Shape", StringComparison.OrdinalIgnoreCase) && !fullFrame)
            {
                var radius = 4 + ((projectIndex * 7 + i * 13) % 42);
                layer.CornerRadiusTopLeft = radius;
                layer.CornerRadiusTopRight = (radius + 8 + projectIndex % 12) % 48;
                layer.CornerRadiusBottomRight = (radius + 17 + i % 9) % 48;
                layer.CornerRadiusBottomLeft = (radius + 25 + projectIndex % 7) % 48;
                layer.BorderWidth = .5 + ((projectIndex + i) % 4) * .45;
                layer.GlossEnabled = (projectIndex + i) % 5 == 0;
                layer.GlossOpacity = layer.GlossEnabled ? .12 + (projectIndex % 4) * .07 : 0;
            }
        }

        var sigWidth = 90 + (projectIndex * 37) % 330;
        var sigHeight = 8 + (projectIndex * 11) % 56;
        var sigX = 45 + (projectIndex * 83) % Math.Max(1, (int)(1830 - sigWidth));
        var sigY = 45 + (projectIndex * 59) % Math.Max(1, (int)(990 - sigHeight));
        var signature = Shape($"Signature Accent {projectIndex + 1:000}", sigX, sigY, sigWidth, sigHeight,
            projectIndex % 2 == 0 ? palette.A : palette.B,
            projectIndex % 2 == 0 ? palette.B : palette.A,
            project.Groups[0].Id, 3 + projectIndex % 18, 3 + (projectIndex * 3) % 18, 3 + (projectIndex * 5) % 18,
            shapeKinds[projectIndex % shapeKinds.Length], projectIndex % 9 == 0 ? -6 + projectIndex % 13 : 0);
        signature.Name += $" · L{layout:00} · A{angle:000}";
        signature.GlowColor = projectIndex % 6 == 0 ? palette.A : "#00000000";
        signature.GlowRadius = projectIndex % 6 == 0 ? 3 + projectIndex % 5 : 0;
        project.Layers.Add(signature);
        project.Groups[0].Name = $"Design {projectIndex + 1:000} · Layout {layout + 1:00}";
    }

    private static void ApplyProjectMotionProfile(CgProject project, int projectIndex)
    {
        for (var layerIndex = 0; layerIndex < project.Layers.Count; layerIndex++)
        {
            var layer = project.Layers[layerIndex];
            var isTickerOrRoll = layer.Type.Equals("Ticker", StringComparison.OrdinalIgnoreCase) || layer.Type.Equals("Roll", StringComparison.OrdinalIgnoreCase);
            if (!isTickerOrRoll)
            {
                layer.AnimationIn = EntryMotions[(projectIndex * 3 + layerIndex) % EntryMotions.Length];
                layer.AnimationOut = ExitMotions[(projectIndex * 5 + layerIndex + 2) % ExitMotions.Length];
                layer.AnimationInSeconds = .32 + ((projectIndex + layerIndex) % 5) * .07;
                layer.AnimationOutSeconds = .28 + ((projectIndex + layerIndex * 2) % 4) * .07;
            }
            if (layer.Type.Equals("Text", StringComparison.OrdinalIgnoreCase) && layer.TextAnimationPreset.Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                layer.TextAnimationPreset = TextMotionPresets[(projectIndex + layerIndex) % TextMotionPresets.Length];
                layer.TextAnimationUnit = layer.TextAnimationPreset is "Type On" or "Tracking Reveal" ? "Letter" : "Word";
            }
            // Generated catalog text is clean by default; shadow remains an explicit operator choice.
            layer.ShadowColor = "#00000000"; layer.ShadowOffsetX = 0; layer.ShadowOffsetY = 0; layer.ShadowBlur = 0;
        }
    }

    private static CgLayer CrystalCard(string name,double x,double y,double w,double h,string a,string b,Guid g,double radius)
    {
        var layer=Shape(name,x,y,w,h,a,b,g,radius,radius,radius);
        layer.GlossEnabled=true; layer.GlossOpacity=.28; layer.BorderColor="#55FFFFFF"; layer.BorderWidth=1.2;
        layer.GlowColor="#335CCBFF"; layer.GlowRadius=3;
        return layer;
    }

    private static CgLayer Shape(string name,double x,double y,double w,double h,string a,string b,Guid g,double tl,double tr,double br,string kind="Rectangle",double rotation=0)
        => new() { Name=name,Type="Shape",ShapeKind=kind,X=x,Y=y,Width=w,Height=h,Background=a,UseGradient=a!=b,GradientColor2=b,GradientAngle=8,BorderColor="#22FFFFFF",BorderWidth=1,CornerRadiusTopLeft=tl,CornerRadiusTopRight=tr,CornerRadiusBottomRight=br,CornerRadiusBottomLeft=tl,GroupId=g,EndSeconds=60,AnimationIn="Fade",AnimationOut="Fade",AnimationInSeconds=.45,AnimationOutSeconds=.35,Rotation=rotation };

    private static CgLayer Text(string name,string text,double x,double y,double w,double h,double size,string fill,Guid g,string preset)
        => new() { Name=name,Type="Text",Text=text,X=x,Y=y,Width=w,Height=h,FontSize=size,FontFamily="Segoe UI",Bold=true,Fill=fill,Background="#00000000",OutlineColor="#66000000",OutlineWidth=size>60?1.4:.5,GroupId=g,EndSeconds=60,AnimationIn="Fade",AnimationOut="Fade",AnimationInSeconds=.5,AnimationOutSeconds=.35,TextAnimationPreset=preset,TextAnimationUnit=preset is "Type On" or "Tracking Reveal"?"Letter":"Word",TextAnimationDurationSeconds=.75,TextAnimationStaggerSeconds=.06 };

    private static void Add(CgProject p,CgLayer l){l.EndSeconds=p.DurationSeconds;p.Layers.Add(l);}
    private static CgKeyframe K(double t,double x,double y,double w,double h,double r,double opacity,string ease)=>new(){TimeSeconds=t,X=x,Y=y,Width=w,Height=h,Rotation=r,Opacity=opacity,ScaleX=1,ScaleY=1,Ease=ease};


    #region Playout Specific Demos and Resolvers
    #region Prime HD Native Demos
    public const string QuantumBreakingNewsUrl = "https://quantumcg.simulcast.com.np/api_data/data.php?content=breaking&token=asfhoasfajefaskdfhlashflkhauwehfwui32423kshdafaheaf&company=3";
    public const string QuantumBreakingNewsCachedJson = "[{\"id\":\"1352\",\"brk_text\":\"d]nDrLsf] kfOk km'6]\\\\/ e]n k;]kl5 ljz]if cbfntsf] ;'g'jfO \\\\/f]lsof]\"},{\"id\":\"1353\",\"brk_text\":\"kfFr b]zsf /fHosf]if k|d'vsf] cGt/f{li6«o ;Dd]ng sf7df8f}Fdf ;'?\"},{\"id\":\"1354\",\"brk_text\":\"k|wfgdGqLåf/f /fli6«o ljsf; cfof]hgfxi'sf] ultljlw lgl/If0f\"},{\"id\":\"1355\",\"brk_text\":\"s]Gb|Lo ax'If]qLo k|fjlws tyf cfly{s ;fem]bf/Lsf] a}7s ;DkGg\"},{\"id\":\"1356\",\"brk_text\":\"g]kfn / ef/TalLr pmhf{ Jofkf/ सम्झौता lj:tf/ ug]{ ;xdlt\"}]";

    private static CgProject CreatePrimeBreakingDemo()
    {
        const double duration = 3.90;
        const double burstDuration = 56.0 / 60.0;
        var project = new CgProject { Id = new Guid("11111111-2222-3333-4444-555555555555"), Name = PrimeBreakingDemoName, Width = 1920, Height = 1080, FrameRate = 50, DurationSeconds = duration, Loop = true };
        var liveNewsDs = new CgDataSource
        {
            Id = new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            Name = "Quantum Breaking News Live Feed",
            SourceType = "UrlJson",
            Source = QuantumBreakingNewsUrl,
            RefreshSeconds = 15,
            CachedItemsJson = QuantumBreakingNewsCachedJson
        };
        project.DataSources.Add(liveNewsDs);
        var group = new CgGroup { Id = Guid.NewGuid(), Name = "Breaking News Lower Third" };
        project.Groups.Add(group);
        var backPath = ResolvePrimePath("primeBack");
        var frontPath = ResolvePrimePath("primeFront");
        // Layer 1 (bottom/back): continuous 50 fps background loop from frame 56 to 194
        project.Layers.Add(new CgLayer { Id = Guid.NewGuid(), Name = "Prime Back (Loop)", Type = "ImageSequence", Source = backPath, X = 0, Y = 877, Width = 1920, Height = 93, SequenceFps = 50, SequenceStartFrame = 56, SequenceEndFrame = 194, SequenceLoop = true, SequenceHoldLastFrame = true, GroupId = group.Id, StartSeconds = 0, EndSeconds = duration, Visible = true, Opacity = 1.0 });
        // Layer 2 (middle/headline): starts immediately after burst wipe completes (at 0.93s) and animates in over background loop
        project.Layers.Add(new CgLayer { Id = Guid.NewGuid(), Name = "Breaking News Headline", Type = "Text", Text = "d]nDrLsf] kfOk km'6]\\/ e]n k;]kl5 ljz]if cbfntsf] ;'g'jfO \\/f]lsof]", X = 250, Y = 900, Width = 1630, Height = 58, FontSize = 44, FontFamily = "Kantipur", Bold = true, Fill = "#FFFFFFFF", ShadowColor = "#CC000000", ShadowOffsetX = 2, ShadowOffsetY = 2, ShadowBlur = 4, DataSourceId = liveNewsDs.Id, DataField = "brk_text", DataItemDurationSeconds = duration, TextAnimationDelaySeconds = 0, TextAnimationPreset = "Slide Left", TextAnimationDurationSeconds = 0.40, GroupId = group.Id, StartSeconds = Math.Round(burstDuration, 2), EndSeconds = duration, AnimationIn = "Slide Left", AnimationInSeconds = 0.40, AnimationOut = "Fade", AnimationOutSeconds = 0.35, Visible = true, Opacity = 1.0 });
        // Layer 3 (top/overlay): 60 fps intro burst wipe from frame 0 to 55 (0.93s). HoldLastFrame=false so it vanishes once burst is complete, never obscuring headline text
        project.Layers.Add(new CgLayer { Id = Guid.NewGuid(), Name = "Prime Front Burst (Overlay)", Type = "ImageSequence", Source = frontPath, X = 0, Y = 877, Width = 1920, Height = 93, SequenceFps = 60, SequenceStartFrame = 0, SequenceEndFrame = 55, SequenceLoop = false, SequenceHoldLastFrame = false, SequenceAdvanceDataItem = true, DataSourceId = liveNewsDs.Id, DataItemDurationSeconds = duration, GroupId = group.Id, StartSeconds = 0, EndSeconds = Math.Round(burstDuration, 2), Visible = true, Opacity = 1.0 });
        return project;
    }

    private static CgProject CreatePrimeFlashDemo()
    {
        const double duration = 3.90;
        const double burstDuration = 56.0 / 60.0;
        var project = new CgProject { Id = new Guid("33333333-4444-5555-6666-777777777777"), Name = PrimeFlashDemoName, Width = 1920, Height = 1080, FrameRate = 50, DurationSeconds = duration, Loop = true };
        var flashDs = new CgDataSource
        {
            Id = new Guid("cccccccc-dddd-eeee-ffff-000000000000"),
            Name = "Quantum Flash News Live Feed",
            SourceType = "UrlJson",
            Source = QuantumBreakingNewsUrl,
            RefreshSeconds = 15,
            CachedItemsJson = QuantumBreakingNewsCachedJson
        };
        project.DataSources.Add(flashDs);
        var group = new CgGroup { Id = Guid.NewGuid(), Name = "Flash News Lower Third" };
        project.Groups.Add(group);
        var backPath = ResolvePrimePath("FlashNews/FlashBack");
        var frontPath = ResolvePrimePath("FlashNews/FlashFront");
        // Layer 1 (bottom/back): continuous 50 fps background loop from frame 56 to 194
        project.Layers.Add(new CgLayer { Id = Guid.NewGuid(), Name = "Flash Back (Loop)", Type = "ImageSequence", Source = backPath, X = 0, Y = 877, Width = 1920, Height = 93, SequenceFps = 50, SequenceStartFrame = 56, SequenceEndFrame = 194, SequenceLoop = true, SequenceHoldLastFrame = true, GroupId = group.Id, StartSeconds = 0, EndSeconds = duration, Visible = true, Opacity = 1.0 });
        // Layer 2 (middle/headline): starts immediately after burst wipe completes (at 0.93s) and animates in over background loop
        project.Layers.Add(new CgLayer { Id = Guid.NewGuid(), Name = "Flash News Headline", Type = "Text", Text = "d]nDrLsf] kfOk km'6]\\/ e]n k;]kl5 ljz]if cbfntsf] ;'g'jfO \\/f]lsof]", X = 250, Y = 900, Width = 1630, Height = 58, FontSize = 44, FontFamily = "Kantipur", Bold = true, Fill = "#FFFFFFFF", ShadowColor = "#CC000000", ShadowOffsetX = 2, ShadowOffsetY = 2, ShadowBlur = 4, DataSourceId = flashDs.Id, DataField = "brk_text", DataItemDurationSeconds = duration, TextAnimationDelaySeconds = 0, TextAnimationPreset = "Slide Left", TextAnimationDurationSeconds = 0.40, GroupId = group.Id, StartSeconds = Math.Round(burstDuration, 2), EndSeconds = duration, AnimationIn = "Slide Left", AnimationInSeconds = 0.40, AnimationOut = "Fade", AnimationOutSeconds = 0.35, Visible = true, Opacity = 1.0 });
        // Layer 3 (top/overlay): 60 fps intro burst wipe from frame 0 to 55 (0.93s). HoldLastFrame=false so it vanishes once burst is complete, never obscuring headline text
        project.Layers.Add(new CgLayer { Id = Guid.NewGuid(), Name = "Flash Front Burst (Overlay)", Type = "ImageSequence", Source = frontPath, X = 0, Y = 877, Width = 1920, Height = 93, SequenceFps = 60, SequenceStartFrame = 0, SequenceEndFrame = 55, SequenceLoop = false, SequenceHoldLastFrame = false, SequenceAdvanceDataItem = true, DataSourceId = flashDs.Id, DataItemDurationSeconds = duration, GroupId = group.Id, StartSeconds = 0, EndSeconds = Math.Round(burstDuration, 2), Visible = true, Opacity = 1.0 });
        return project;
    }

    private static CgProject CreatePrimeBreakingScreenDemo()
    {
        const double duration = 15.0;
        var project = new CgProject { Id = new Guid("44444444-5555-6666-7777-888888888888"), Name = PrimeBreakingScreenDemoName, Width = 1920, Height = 1080, FrameRate = 50, DurationSeconds = duration, Loop = true };
        var liveNewsDs = new CgDataSource
        {
            Id = new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            Name = "Quantum Breaking News Live Feed",
            SourceType = "UrlJson",
            Source = QuantumBreakingNewsUrl,
            RefreshSeconds = 15,
            CachedItemsJson = QuantumBreakingNewsCachedJson
        };
        project.DataSources.Add(liveNewsDs);
        var seqPath = ResolvePrimePath("BreakingScreen");
        project.Layers.Add(new CgLayer { Id = Guid.NewGuid(), Name = "Prime Breaking Screen Sequence", Type = "ImageSequence", Source = seqPath, X = 0, Y = 0, Width = 1920, Height = 1080, SequenceFps = 50, SequenceStartFrame = 0, SequenceEndFrame = 249, SequenceLoopStartFrame = 100, SequenceLoop = true, SequenceHoldLastFrame = true, StartSeconds = 0, EndSeconds = 5.0, Visible = true, Opacity = 1.0 });
        project.Layers.Add(new CgLayer
        {
            Id = Guid.NewGuid(),
            Name = "Breaking Screen Headline",
            Type = "Text",
            Text = "d]nDrLsf] kfOk km'6]\\/ e]n k;]kl5 ljz]if cbfntsf] ;'g'jfO \\/f]lsof]",
            X = 220,
            Y = 820,
            Width = 1480,
            Height = 110,
            FontSize = 54,
            FontFamily = "Kantipur",
            Bold = true,
            Fill = "#FFFFFFFF",
            HorizontalTextAlignment = "Center",
            VerticalTextAlignment = "Center",
            ShadowEnabled = true,
            ShadowColor = "#CC000000",
            ShadowOffsetX = 3,
            ShadowOffsetY = 3,
            ShadowBlur = 6,
            DataSourceId = liveNewsDs.Id,
            DataField = "brk_text",
            DataItemDurationSeconds = 5.0,
            StartSeconds = 0,
            EndSeconds = duration,
            TextAnimationDelaySeconds = 2.0,
            AnimationIn = "Scale Down",
            AnimationInSeconds = 0.60,
            AnimationOut = "Fade",
            AnimationOutSeconds = 0.40,
            TextAnimationPreset = "Scale Down",
            TextAnimationDurationSeconds = 0.60,
            TextAnimationScaleStart = 1.35,
            TextAnimationScaleEnd = 1.0,
            Visible = true,
            Opacity = 1.0
        });
        return project;
    }

    private static CgProject CreatePrimeLivePlateDemo()
    {
        const double duration = 10.0;
        var project = new CgProject { Id = new Guid("55555555-6666-7777-8888-999999999999"), Name = PrimeLivePlateDemoName, Width = 1920, Height = 1080, FrameRate = 50, DurationSeconds = duration };
        var seqPath = ResolvePrimePath("Live_Plate");
        project.Layers.Add(new CgLayer { Id = Guid.NewGuid(), Name = "Prime Live Plate Sequence", Type = "ImageSequence", Source = seqPath, X = 80, Y = 80, Width = 160, Height = 115, SequenceFps = 25, SequenceStartFrame = 0, SequenceEndFrame = 125, SequenceLoop = true, SequenceHoldLastFrame = true, StartSeconds = 0, EndSeconds = duration, Visible = true, Opacity = 1.0 });
        project.Layers.Add(new CgLayer { Id = Guid.NewGuid(), Name = "Live Plate Text", Type = "Text", Text = "LIVE", X = 80, Y = 120, Width = 160, Height = 35, FontSize = 28, FontFamily = "Segoe UI", Bold = true, Fill = "#FFFFFFFF", StartSeconds = 0.50, EndSeconds = duration, AnimationIn = "Fade", AnimationInSeconds = 0.35, AnimationOut = "Fade", AnimationOutSeconds = 0.35, Visible = true, Opacity = 1.0 });
        return project;
    }

    private static CgProject CreatePrimeNamePlateDemo()
    {
        const double duration = 10.0;
        var project = new CgProject { Id = new Guid("66666666-7777-8888-9999-aaaaaaaaaaaa"), Name = PrimeNamePlateDemoName, Width = 1920, Height = 1080, FrameRate = 50, DurationSeconds = duration };
        var seqPath = ResolvePrimePath("Name_Plate_New");
        project.Layers.Add(new CgLayer { Id = Guid.NewGuid(), Name = "Prime Name Plate Sequence", Type = "ImageSequence", Source = seqPath, X = 100, Y = 820, Width = 600, Height = 150, SequenceFps = 30, SequenceStartFrame = 0, SequenceEndFrame = 199, SequenceLoop = false, SequenceHoldLastFrame = true, StartSeconds = 0, EndSeconds = duration, Visible = true, Opacity = 1.0 });
        project.Layers.Add(new CgLayer { Id = Guid.NewGuid(), Name = "Anchor / Guest Name", Type = "Text", Text = "सुशील शर्मा", X = 160, Y = 845, Width = 600, Height = 50, FontSize = 36, FontFamily = "Segoe UI", Bold = true, Fill = "#FFFFFFFF", StartSeconds = 0.80, EndSeconds = duration, AnimationIn = "Slide Left", AnimationInSeconds = 0.40, AnimationOut = "Fade", AnimationOutSeconds = 0.35, Visible = true, Opacity = 1.0 });
        project.Layers.Add(new CgLayer { Id = Guid.NewGuid(), Name = "Anchor / Guest Designation", Type = "Text", Text = "वरिष्ठ पत्रकार / समाचार सम्पादक", X = 160, Y = 905, Width = 600, Height = 36, FontSize = 22, FontFamily = "Segoe UI", Bold = false, Fill = "#FFCC00", StartSeconds = 1.05, EndSeconds = duration, AnimationIn = "Fade", AnimationInSeconds = 0.35, AnimationOut = "Fade", AnimationOutSeconds = 0.35, Visible = true, Opacity = 1.0 });
        return project;
    }

    private static CgProject CreatePrimeLogoDemo()
    {
        const double duration = 30.0;
        var project = new CgProject { Id = new Guid("77777777-8888-9999-aaaa-bbbbbbbbbbbb"), Name = PrimeLogoDemoName, Width = 1920, Height = 1080, FrameRate = 50, DurationSeconds = duration };
        var seqPath = ResolvePrimePath("primeLogo");
        project.Layers.Add(new CgLayer { Id = Guid.NewGuid(), Name = "Prime Logo Bug Sequence", Type = "ImageSequence", Source = seqPath, X = 1690, Y = 60, Width = 150, Height = 150, SequenceFps = 25, SequenceStartFrame = 0, SequenceEndFrame = 281, SequenceLoop = true, SequenceHoldLastFrame = true, StartSeconds = 0, EndSeconds = duration, Visible = true, Opacity = 1.0 });
        return project;
    }

    private static CgProject CreatePrimeTickerDemo()
    {
        const double duration = 30.0;
        var project = new CgProject { Id = new Guid("88888888-9999-aaaa-bbbb-cccccccccccc"), Name = PrimeTickerDemoName, Width = 1920, Height = 1080, FrameRate = 50, DurationSeconds = duration };
        var scrollPath = ResolvePrimePath("scrolling");
        project.Layers.Add(new CgLayer { Id = Guid.NewGuid(), Name = "Prime News Ticker", Type = "Ticker", Source = scrollPath, Text = "k|wfgdGqLåf/f /fli6«o ljsf; cfof]hgfxi'sf] ultljlw lgl/If0f · d]nDrLsf] kfOk km'6]\\/ e]n k;]kl5 ljz]if cbfntsf] ;'g'jfO \\/f]lsof] · kfFr b]zsf /fHosf]if k|d'vsf] cGt/f{li6«o ;Dd]ng sf7df8f}Fdf ;'?", X = 0, Y = 1030, Width = 1920, Height = 50, FontSize = 32, FontFamily = "Kantipur", Bold = true, Fill = "#FFFFFFFF", TickerSpeed = 120, TickerMode = "Continuous", TickerDirection = "Left", TickerRepeat = true, TickerGap = 120, AnimationIn = "None", AnimationOut = "None", AnimationInSeconds = 0, AnimationOutSeconds = 0, StartSeconds = 0, EndSeconds = duration, Visible = true, Opacity = 1.0 });
        return project;
    }
    #endregion

    #region Space 4K Native Demos
    private static CgProject CreateSpace4kBreakingDemo()
    {
        const double duration = 10.0;
        var project = new CgProject { Id = new Guid("22222222-3333-4444-5555-666666666666"), Name = Space4kBreakingDemoName, Width = 1920, Height = 1080, FrameRate = 50, DurationSeconds = duration, Loop = true };
        var liveNewsDs = new CgDataSource { Id = new Guid("bbbbbbbb-cccc-dddd-eeee-ffffffffffff"), Name = "Space 4K Live Feed", SourceType = "UrlJson", Source = QuantumBreakingNewsUrl, RefreshSeconds = 15, CachedItemsJson = QuantumBreakingNewsCachedJson };
        project.DataSources.Add(liveNewsDs);
        var group = new CgGroup { Id = Guid.NewGuid(), Name = "Space 4K Breaking News Group" };
        project.Groups.Add(group);
        var seqPath = ResolveSpace4kPath("BreakingNews");
        project.Layers.Add(new CgLayer { Id = Guid.NewGuid(), Name = "Space 4K Breaking News Loop", Type = "ImageSequence", Source = seqPath, X = 0, Y = 880, Width = 1920, Height = 200, SequenceFps = 50, SequenceStartFrame = 0, SequenceEndFrame = -1, SequenceLoop = true, SequenceHoldLastFrame = true, GroupId = group.Id, StartSeconds = 0, EndSeconds = duration, Visible = true, Opacity = 1.0 });
        project.Layers.Add(new CgLayer { Id = Guid.NewGuid(), Name = "Space 4K Headline Text", Type = "Text", Text = "d]nDrLsf] kfOk km'6]\\/ e]n k;]kl5 ljz]if cbfntsf] ;'g'jfO \\/f]lsof]", X = 250, Y = 905, Width = 1630, Height = 70, FontSize = 44, FontFamily = "Kantipur", Bold = true, Fill = "#FFFFFFFF", GroupId = group.Id, DataSourceId = liveNewsDs.Id, DataField = "brk_text", DataItemDurationSeconds = 3.90, StartSeconds = 0.85, EndSeconds = duration, AnimationIn = "Slide Left", AnimationInSeconds = 0.45, AnimationOut = "Fade", AnimationOutSeconds = 0.35, Visible = true, Opacity = 1.0 });
        return project;
    }

    private static CgProject CreateSpace4kFlashDemo()
    {
        const double duration = 10.0;
        var project = new CgProject { Id = new Guid("99999999-aaaa-bbbb-cccc-dddddddddddd"), Name = Space4kFlashDemoName, Width = 1920, Height = 1080, FrameRate = 50, DurationSeconds = duration, Loop = true };
        var liveNewsDs = new CgDataSource { Id = new Guid("dddddddd-eeee-ffff-0000-111111111111"), Name = "Space 4K Flash Feed", SourceType = "UrlJson", Source = QuantumBreakingNewsUrl, RefreshSeconds = 15, CachedItemsJson = QuantumBreakingNewsCachedJson };
        project.DataSources.Add(liveNewsDs);
        var group = new CgGroup { Id = Guid.NewGuid(), Name = "Space 4K Flash News Group" };
        project.Groups.Add(group);
        var seqPath = ResolveSpace4kPath("FlashNews");
        project.Layers.Add(new CgLayer { Id = Guid.NewGuid(), Name = "Space 4K Flash News Loop", Type = "ImageSequence", Source = seqPath, X = 0, Y = 880, Width = 1920, Height = 200, SequenceFps = 50, SequenceStartFrame = 0, SequenceEndFrame = -1, SequenceLoop = true, SequenceHoldLastFrame = true, GroupId = group.Id, StartSeconds = 0, EndSeconds = duration, Visible = true, Opacity = 1.0 });
        project.Layers.Add(new CgLayer { Id = Guid.NewGuid(), Name = "Space 4K Flash Headline", Type = "Text", Text = "d]nDrLsf] kfOk km'6]\\/ e]n k;]kl5 ljz]if cbfntsf] ;'g'jfO \\/f]lsof]", X = 250, Y = 905, Width = 1630, Height = 70, FontSize = 44, FontFamily = "Kantipur", Bold = true, Fill = "#FFFFFFFF", GroupId = group.Id, DataSourceId = liveNewsDs.Id, DataField = "brk_text", DataItemDurationSeconds = 3.90, StartSeconds = 0.85, EndSeconds = duration, AnimationIn = "Slide Left", AnimationInSeconds = 0.45, AnimationOut = "Fade", AnimationOutSeconds = 0.35, Visible = true, Opacity = 1.0 });
        return project;
    }

    private static CgProject CreateSpace4kTopBarDemo()
    {
        const double duration = 15.0;
        var project = new CgProject { Id = new Guid("aaaaaaaa-bbbb-cccc-dddd-000000000001"), Name = Space4kTopBarDemoName, Width = 1920, Height = 1080, FrameRate = 50, DurationSeconds = duration };
        var seqPath = ResolveSpace4kPath("topBar");
        project.Layers.Add(new CgLayer { Id = Guid.NewGuid(), Name = "Space 4K TopBar Sequence", Type = "ImageSequence", Source = seqPath, X = 0, Y = 0, Width = 1920, Height = 120, SequenceFps = 50, SequenceStartFrame = 0, SequenceEndFrame = -1, SequenceLoop = true, SequenceHoldLastFrame = true, StartSeconds = 0, EndSeconds = duration, Visible = true, Opacity = 1.0 });
        return project;
    }

    private static CgProject CreateSpace4kLiveDemo()
    {
        const double duration = 15.0;
        var project = new CgProject { Id = new Guid("aaaaaaaa-bbbb-cccc-dddd-000000000002"), Name = Space4kLiveDemoName, Width = 1920, Height = 1080, FrameRate = 50, DurationSeconds = duration };
        var seqPath = ResolveSpace4kPath("Live");
        project.Layers.Add(new CgLayer { Id = Guid.NewGuid(), Name = "Space 4K Live Plate Sequence", Type = "ImageSequence", Source = seqPath, X = 80, Y = 80, Width = 100, Height = 50, SequenceFps = 50, SequenceStartFrame = 0, SequenceEndFrame = -1, SequenceLoop = true, SequenceHoldLastFrame = true, StartSeconds = 0, EndSeconds = duration, Visible = true, Opacity = 1.0 });
        return project;
    }

    private static CgProject CreateSpace4kLogoDemo()
    {
        const double duration = 30.0;
        var project = new CgProject { Id = new Guid("aaaaaaaa-bbbb-cccc-dddd-000000000003"), Name = Space4kLogoDemoName, Width = 1920, Height = 1080, FrameRate = 50, DurationSeconds = duration };
        var seqPath = ResolveSpace4kPath("logo");
        project.Layers.Add(new CgLayer { Id = Guid.NewGuid(), Name = "Space 4K Station Logo Sequence", Type = "ImageSequence", Source = seqPath, X = 1650, Y = 50, Width = 210, Height = 210, SequenceFps = 50, SequenceStartFrame = 0, SequenceEndFrame = -1, SequenceLoop = true, SequenceHoldLastFrame = true, StartSeconds = 0, EndSeconds = duration, Visible = true, Opacity = 1.0 });
        return project;
    }

    private static CgProject CreateSpace4kSambadDemo()
    {
        const double duration = 15.0;
        var project = new CgProject { Id = new Guid("aaaaaaaa-bbbb-cccc-dddd-000000000004"), Name = Space4kSambadDemoName, Width = 1920, Height = 1080, FrameRate = 50, DurationSeconds = duration };
        var seqPath = ResolveSpace4kPath("Sambad");
        project.Layers.Add(new CgLayer { Id = Guid.NewGuid(), Name = "Space 4K Sambad Plate", Type = "ImageSequence", Source = seqPath, X = 0, Y = 880, Width = 1920, Height = 200, SequenceFps = 50, SequenceStartFrame = 0, SequenceEndFrame = -1, SequenceLoop = true, SequenceHoldLastFrame = true, StartSeconds = 0, EndSeconds = duration, Visible = true, Opacity = 1.0 });
        project.Layers.Add(new CgLayer { Id = Guid.NewGuid(), Name = "Sambad Guest Name", Type = "Text", Text = "डा. बाबुराम भट्टराई", X = 150, Y = 905, Width = 520, Height = 50, FontSize = 36, FontFamily = "Segoe UI", Bold = true, Fill = "#FFFFFFFF", StartSeconds = 0.80, EndSeconds = duration, AnimationIn = "Slide Left", AnimationInSeconds = 0.40, AnimationOut = "Fade", AnimationOutSeconds = 0.35, Visible = true, Opacity = 1.0 });
        project.Layers.Add(new CgLayer { Id = Guid.NewGuid(), Name = "Sambad Subtitle", Type = "Text", Text = "पूर्व प्रधानमन्त्री / राजनीतिक विश्लेषक", X = 150, Y = 955, Width = 520, Height = 36, FontSize = 22, FontFamily = "Segoe UI", Bold = false, Fill = "#FFD700", StartSeconds = 1.05, EndSeconds = duration, AnimationIn = "Fade", AnimationInSeconds = 0.35, AnimationOut = "Fade", AnimationOutSeconds = 0.35, Visible = true, Opacity = 1.0 });
        return project;
    }

    private static CgProject CreateSpace4kTickerDemo()
    {
        // Standard duration - the compositor's dynamic ticker pause-event mechanism
        // holds the timeline at pausePoint while all categories/items play through
        // completely, then resumes for the out-animation. Standard timeline event.
        const double duration = 15.0;
        var project = new CgProject { Id = new Guid("aaaaaaaa-bbbb-cccc-dddd-000000000005"), Name = Space4kTickerDemoName, Width = 1920, Height = 1080, FrameRate = 50, DurationSeconds = duration, Loop = true, OnAir = false };
        project.TimelineEvents.Add(new CgTimelineEvent { TimeSeconds = 1.0, Type = "Pause", Label = "HOLD / TICKER", ResumeTrigger = "TickerComplete" });
        var seqPath = ResolveSpace4kPath("scroll_img");
        project.Layers.Add(new CgLayer { Id = Guid.NewGuid(), Name = "Space 4K Ticker Plate Loop", Type = "ImageSequence", Source = seqPath, X = 0, Y = 980, Width = 1920, Height = 100, SequenceFps = 50, SequenceStartFrame = 0, SequenceEndFrame = -1, SequenceLoop = true, SequenceHoldLastFrame = true, StartSeconds = 0, EndSeconds = duration, DurationSeconds = duration, Visible = true, Opacity = 1.0 });
        project.Layers.Add(new CgLayer { Id = Guid.NewGuid(), Name = "Space 4K Ticker Scroll", Type = "Ticker", Text = "SPACE 4K ULTRA HD BROADCAST · समाचार र ताजा अपडेट्स · काठमाडौँ उपत्यकाको ताजा मौसम विवरण", X = 0, Y = 985, Width = 1920, Height = 85, FontSize = 34, FontFamily = "Segoe UI", Bold = true, Fill = "#FFFFFFFF", Speed = 190, TickerSpeed = 190, TickerMode = "Continuous", TickerRepeat = true, TickerGap = 120, StartSeconds = 0, EndSeconds = duration, DurationSeconds = duration, Visible = true, Opacity = 1.0 });
        return project;
    }
    #endregion

    #region Public 4K Native Demos
    private static CgProject CreatePublic4kNewsUpdateDemo()
    {
        const double duration = 15.0;
        var p = new CgProject { Id = new Guid("bbbb2222-cccc-3333-dddd-444455556666"), Name = Public4kNewsUpdateDemoName, Width = 1920, Height = 1080, FrameRate = 50, DurationSeconds = duration, Loop = true, OnAir = false };
        var ds = new CgDataSource
        {
            Id = Guid.NewGuid(),
            Name = "Public 4K Top News API",
            SourceType = "UrlJson",
            Source = "https://quantumcg.simulcast.com.np/api_data/data.php?content=top&token=aldfhalhiae342jdharhfk498234kh234fdasf&company=5",
            RefreshSeconds = 15,
            CachedItemsJson = "[{\"id\":\"501\",\"title\":\"सार्वजनिक सरोकारका विषयमा महत्वपूर्ण निर्णय, जनतालाई राहत दिने तयारी\" }]"
        };
        p.DataSources.Add(ds);
        var g = Guid.NewGuid();
        p.Groups.Add(new CgGroup { Id = g, Name = "Public 4K News Update" });

        Add(p, new CgLayer { Name = "Gradient Bar Base", Type = "Shape", X = 0, Y = 980, Width = 1920, Height = 75, Background = "#4444F3", UseGradient = true, GradientColor2 = "#1A1A88", GradientAngle = 0, GroupId = g });
        Add(p, new CgLayer { Name = "News Update Title Tag", Type = "Text", Text = "News Update", X = 80, Y = 992, Width = 260, Height = 50, FontSize = 32, FontFamily = "Segoe UI", Bold = true, Fill = "#FFFFFFFF", GroupId = g, TextAnimationPreset = "Fade Cascade" });
        Add(p, new CgLayer
        {
            Name = "News Update Text",
            Type = "Text",
            Text = "सार्वजनिक सरोकारका विषयमा महत्वपूर्ण निर्णय, जनतालाई राहत दिने तयारी",
            X = 360,
            Y = 992,
            Width = 1500,
            Height = 50,
            FontSize = 32,
            FontFamily = "Segoe UI",
            Bold = true,
            Fill = "#FFFFFFFF",
            DataSourceId = ds.Id,
            DataField = "title",
            DataItemDurationSeconds = duration,
            GroupId = g,
            AnimationIn = "Fade",
            AnimationInSeconds = 0.4,
            AnimationOut = "Fade",
            AnimationOutSeconds = 0.35,
            TextAnimationPreset = "Scale Down",
            TextAnimationScaleStart = 4.0,
            TextAnimationScaleEnd = 1.0,
            TextAnimationDurationSeconds = 1.2
        });
        return p;
    }
    #endregion

    #region AP1 HD Native Demos
    private static CgProject CreateAp1hdBreakingDemo()
    {
        const double duration = 10.0;
        var project = new CgProject { Id = new Guid("11112222-3333-4444-5555-666677778888"), Name = Ap1hdBreakingDemoName, Width = 1920, Height = 1080, FrameRate = 50, DurationSeconds = duration, Loop = true };
        var liveNewsDs = new CgDataSource
        {
            Id = new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            Name = "AP1 HD Breaking Feed",
            SourceType = "UrlJson",
            Source = QuantumBreakingNewsUrl,
            RefreshSeconds = 15,
            CachedItemsJson = QuantumBreakingNewsCachedJson
        };
        project.DataSources.Add(liveNewsDs);
        var group = new CgGroup { Id = Guid.NewGuid(), Name = "AP1 HD Breaking Lower Third" };
        project.Groups.Add(group);
        var seqPath = ResolveAp1hdPath("BreakingNews");
        project.Layers.Add(new CgLayer { Id = Guid.NewGuid(), Name = "AP1 HD Breaking Loop", Type = "ImageSequence", Source = seqPath, X = 0, Y = 880, Width = 1920, Height = 200, SequenceFps = 50, SequenceStartFrame = 0, SequenceEndFrame = 229, SequenceLoop = true, SequenceHoldLastFrame = true, GroupId = group.Id, StartSeconds = 0, EndSeconds = duration, Visible = true, Opacity = 1.0 });
        project.Layers.Add(new CgLayer
        {
            Id = Guid.NewGuid(),
            Name = "AP1 HD Headline Text",
            Type = "Text",
            Text = "d]nDrLsf] kfOk km'6]\\/ e]n k;]kl5 ljz]if cbfntsf] ;'g'jfO \\/f]lsof]",
            X = 260,
            Y = 905,
            Width = 1620,
            Height = 70,
            FontSize = 44,
            FontFamily = "Kantipur",
            Bold = true,
            Fill = "#FFFFFFFF",
            ShadowEnabled = true,
            ShadowColor = "#80000000",
            ShadowOffsetX = 2,
            ShadowOffsetY = 2,
            ShadowBlur = 3,
            GroupId = group.Id,
            DataSourceId = liveNewsDs.Id,
            DataField = "brk_text",
            DataItemDurationSeconds = 3.90,
            StartSeconds = 0.85,
            EndSeconds = duration,
            AnimationIn = "Slide Left",
            AnimationInSeconds = 0.45,
            AnimationOut = "Fade",
            AnimationOutSeconds = 0.35,
            Visible = true,
            Opacity = 1.0
        });
        return project;
    }

    private static CgProject CreateAp1hdFlashDemo()
    {
        const double duration = 10.0;
        var project = new CgProject { Id = new Guid("22223333-4444-5555-6666-777788889999"), Name = Ap1hdFlashDemoName, Width = 1920, Height = 1080, FrameRate = 50, DurationSeconds = duration, Loop = true };
        var liveNewsDs = new CgDataSource
        {
            Id = new Guid("bbbbbbbb-cccc-dddd-eeee-ffffffffffff"),
            Name = "AP1 HD Flash Feed",
            SourceType = "UrlJson",
            Source = QuantumBreakingNewsUrl,
            RefreshSeconds = 15,
            CachedItemsJson = QuantumBreakingNewsCachedJson
        };
        project.DataSources.Add(liveNewsDs);
        var group = new CgGroup { Id = Guid.NewGuid(), Name = "AP1 HD Flash Lower Third" };
        project.Groups.Add(group);
        var seqPath = ResolveAp1hdPath("FlashNews");
        project.Layers.Add(new CgLayer { Id = Guid.NewGuid(), Name = "AP1 HD Flash Loop", Type = "ImageSequence", Source = seqPath, X = 0, Y = 920, Width = 1920, Height = 132, SequenceFps = 50, SequenceStartFrame = 36, SequenceEndFrame = 199, SequenceLoop = true, SequenceHoldLastFrame = true, GroupId = group.Id, StartSeconds = 0, EndSeconds = duration, Visible = true, Opacity = 1.0 });
        project.Layers.Add(new CgLayer
        {
            Id = Guid.NewGuid(),
            Name = "AP1 HD Flash Text",
            Type = "Text",
            Text = "d]nDrLsf] kfOk km'6]\\/ e]n k;]kl5 ljz]if cbfntsf] ;'g'jfO \\/f]lsof]",
            X = 260,
            Y = 945,
            Width = 1620,
            Height = 60,
            FontSize = 40,
            FontFamily = "Kantipur",
            Bold = true,
            Fill = "#FFFFFFFF",
            ShadowEnabled = true,
            ShadowColor = "#80000000",
            ShadowOffsetX = 2,
            ShadowOffsetY = 2,
            ShadowBlur = 3,
            GroupId = group.Id,
            DataSourceId = liveNewsDs.Id,
            DataField = "brk_text",
            DataItemDurationSeconds = 3.90,
            StartSeconds = 0.85,
            EndSeconds = duration,
            AnimationIn = "Slide Left",
            AnimationInSeconds = 0.45,
            AnimationOut = "Fade",
            AnimationOutSeconds = 0.35,
            Visible = true,
            Opacity = 1.0
        });
        return project;
    }

    private static CgProject CreateAp1hdThreeWindowsDemo()
    {
        const double duration = 20.0;
        var project = new CgProject { Id = new Guid("33334444-5555-6666-7777-888899990000"), Name = Ap1hdThreeWindowsDemoName, Width = 1920, Height = 1080, FrameRate = 50, DurationSeconds = duration, Loop = true };
        var liveNewsDs = new CgDataSource
        {
            Id = new Guid("cccccccc-dddd-eeee-ffff-000011112222"),
            Name = "AP1 HD Quantum News Feed",
            SourceType = "UrlJson",
            Source = QuantumBreakingNewsUrl,
            RefreshSeconds = 15,
            CachedItemsJson = QuantumBreakingNewsCachedJson
        };
        project.DataSources.Add(liveNewsDs);

        // Top Breaking News Header Bar
        project.Layers.Add(new CgLayer
        {
            Id = Guid.NewGuid(),
            Name = "AP1 HD Top Bar Header",
            Type = "Shape",
            ShapeKind = "Rectangle",
            X = 0,
            Y = 0,
            Width = 1920,
            Height = 110,
            Background = "#800818",
            UseGradient = true,
            GradientColor2 = "#2D124D",
            GradientAngle = 90,
            BorderColor = "#EF233C",
            BorderWidth = 4,
            StartSeconds = 0,
            EndSeconds = duration,
            DurationSeconds = duration
        });

        project.Layers.Add(new CgLayer
        {
            Id = Guid.NewGuid(),
            Name = "AP1 HD Top Bar Title",
            Type = "Text",
            Text = "AP1 HD · BREAKING NEWS SPECIAL",
            X = 50,
            Y = 25,
            Width = 1820,
            Height = 60,
            FontSize = 42,
            FontFamily = "Segoe UI",
            Bold = true,
            Fill = "#FFFFFFFF",
            ShadowEnabled = true,
            ShadowColor = "#80000000",
            ShadowOffsetX = 2,
            ShadowOffsetY = 2,
            ShadowBlur = 4,
            StartSeconds = 0,
            EndSeconds = duration,
            DurationSeconds = duration
        });

        // 3 Windows for Studio, Field Reporter, and Expert Guest
        var windowDefs = new[]
        {
            (Name: "Window 1 (Studio Anchor)", X: 64, Label: "STUDIO 1 · KATHMANDU"),
            (Name: "Window 2 (Field Live)", X: 690, Label: "SPECIAL REPORT · ON SCENE"),
            (Name: "Window 3 (Panel Guest)", X: 1316, Label: "PARLIAMENT COMPLEX")
        };

        foreach (var w in windowDefs)
        {
            project.Layers.Add(new CgLayer
            {
                Id = Guid.NewGuid(),
                Name = w.Name,
                Type = "Shape",
                ShapeKind = "Rectangle",
                X = w.X,
                Y = 170,
                Width = 540,
                Height = 360,
                Background = "#180C1F",
                BorderColor = "#EF233C",
                BorderWidth = 3,
                StartSeconds = 0,
                EndSeconds = duration,
                DurationSeconds = duration
            });

            project.Layers.Add(new CgLayer
            {
                Id = Guid.NewGuid(),
                Name = w.Name + " Label Plate",
                Type = "Shape",
                ShapeKind = "Rectangle",
                X = w.X,
                Y = 490,
                Width = 540,
                Height = 40,
                Background = "#CC800818",
                StartSeconds = 0,
                EndSeconds = duration,
                DurationSeconds = duration
            });

            project.Layers.Add(new CgLayer
            {
                Id = Guid.NewGuid(),
                Name = w.Name + " Label Text",
                Type = "Text",
                Text = w.Label,
                X = w.X,
                Y = 495,
                Width = 540,
                Height = 30,
                FontSize = 18,
                FontFamily = "Segoe UI",
                Bold = true,
                Fill = "#FFD84A",
                HorizontalTextAlignment = "Center",
                StartSeconds = 0,
                EndSeconds = duration,
                DurationSeconds = duration
            });
        }

        // Lower Third Breaking Headline Plate
        project.Layers.Add(new CgLayer
        {
            Id = Guid.NewGuid(),
            Name = "Lower Third Card Background",
            Type = "Shape",
            ShapeKind = "Rectangle",
            X = 64,
            Y = 880,
            Width = 1792,
            Height = 120,
            Background = "#FAF7FF",
            BorderColor = "#EF233C",
            BorderWidth = 3,
            StartSeconds = 0,
            EndSeconds = duration,
            DurationSeconds = duration
        });

        project.Layers.Add(new CgLayer
        {
            Id = Guid.NewGuid(),
            Name = "Lower Third Badge Plate",
            Type = "Shape",
            ShapeKind = "Rectangle",
            X = 64,
            Y = 880,
            Width = 260,
            Height = 120,
            Background = "#800818",
            UseGradient = true,
            GradientColor2 = "#7E0919",
            StartSeconds = 0,
            EndSeconds = duration,
            DurationSeconds = duration
        });

        project.Layers.Add(new CgLayer
        {
            Id = Guid.NewGuid(),
            Name = "Lower Third Badge Text",
            Type = "Text",
            Text = "BREAKING\nNEWS",
            X = 64,
            Y = 895,
            Width = 260,
            Height = 90,
            FontSize = 26,
            FontFamily = "Segoe UI",
            Bold = true,
            Fill = "#FFFFFFFF",
            HorizontalTextAlignment = "Center",
            StartSeconds = 0,
            EndSeconds = duration,
            DurationSeconds = duration
        });

        project.Layers.Add(new CgLayer
        {
            Id = Guid.NewGuid(),
            Name = "Lower Third Headline Text",
            Type = "Text",
            Text = "d]nDrLsf] kfOk km'6]\\/ e]n k;]kl5 ljz]if cbfntsf] ;'g'jfO \\/f]lsof]",
            X = 350,
            Y = 905,
            Width = 1480,
            Height = 70,
            FontSize = 42,
            FontFamily = "Kantipur",
            Bold = true,
            Fill = "#17080A",
            DataSourceId = liveNewsDs.Id,
            DataField = "brk_text",
            DataItemDurationSeconds = 4.0,
            StartSeconds = 0.5,
            EndSeconds = duration,
            AnimationIn = "Slide Left",
            AnimationInSeconds = 0.4,
            AnimationOut = "Fade",
            AnimationOutSeconds = 0.35,
            DurationSeconds = duration
        });

        return project;
    }

    private static CgProject CreateAp1hdFullBreakingDemo()
    {
        const double duration = 15.0;
        var project = new CgProject { Id = new Guid("44445555-6666-7777-8888-999900001111"), Name = Ap1hdFullBreakingDemoName, Width = 1920, Height = 1080, FrameRate = 50, DurationSeconds = duration, Loop = true };
        var liveNewsDs = new CgDataSource
        {
            Id = new Guid("dddddddd-eeee-ffff-0000-111122223333"),
            Name = "AP1 HD Quantum Breaking Feed",
            SourceType = "UrlJson",
            Source = QuantumBreakingNewsUrl,
            RefreshSeconds = 15,
            CachedItemsJson = QuantumBreakingNewsCachedJson
        };
        project.DataSources.Add(liveNewsDs);

        // Full Screen Dark Purple/Violet Gradient
        project.Layers.Add(new CgLayer
        {
            Id = Guid.NewGuid(),
            Name = "Full Screen Background",
            Type = "Shape",
            ShapeKind = "Rectangle",
            X = 0,
            Y = 0,
            Width = 1920,
            Height = 1080,
            Background = "#2D124D",
            UseGradient = true,
            GradientColor2 = "#6F2DBD",
            GradientAngle = 135,
            StartSeconds = 0,
            EndSeconds = duration,
            DurationSeconds = duration
        });

        // Top Breaking News Bar
        project.Layers.Add(new CgLayer
        {
            Id = Guid.NewGuid(),
            Name = "Top Breaking Bar",
            Type = "Shape",
            ShapeKind = "Rectangle",
            X = 0,
            Y = 0,
            Width = 1920,
            Height = 180,
            Background = "#800818",
            UseGradient = true,
            GradientColor2 = "#2D124D",
            BorderColor = "#EF233C",
            BorderWidth = 6,
            StartSeconds = 0,
            EndSeconds = duration,
            DurationSeconds = duration
        });

        project.Layers.Add(new CgLayer
        {
            Id = Guid.NewGuid(),
            Name = "Top Breaking Bar Title",
            Type = "Text",
            Text = "AP1 HD · BREAKING NEWS",
            X = 0,
            Y = 50,
            Width = 1920,
            Height = 80,
            FontSize = 64,
            FontFamily = "Segoe UI",
            Bold = true,
            Fill = "#FFFFFFFF",
            HorizontalTextAlignment = "Center",
            ShadowEnabled = true,
            ShadowColor = "#80000000",
            ShadowOffsetX = 3,
            ShadowOffsetY = 3,
            ShadowBlur = 6,
            StartSeconds = 0,
            EndSeconds = duration,
            DurationSeconds = duration
        });

        // Center Story Card
        project.Layers.Add(new CgLayer
        {
            Id = Guid.NewGuid(),
            Name = "Breaking Story Card",
            Type = "Shape",
            ShapeKind = "Rectangle",
            X = 120,
            Y = 240,
            Width = 1680,
            Height = 740,
            Background = "#FAF7FF",
            BorderColor = "#EF233C",
            BorderWidth = 4,
            CornerRadius = 8,
            ShadowEnabled = true,
            ShadowColor = "#80000000",
            ShadowOffsetX = 4,
            ShadowOffsetY = 4,
            ShadowBlur = 12,
            StartSeconds = 0.3,
            EndSeconds = duration,
            DurationSeconds = duration
        });

        project.Layers.Add(new CgLayer
        {
            Id = Guid.NewGuid(),
            Name = "Breaking Headline Story",
            Type = "Text",
            Text = "d]nDrLsf] kfOk km'6]\\/ e]n k;]kl5 ljz]if cbfntsf] ;'g'jfO \\/f]lsof]",
            X = 180,
            Y = 320,
            Width = 1560,
            Height = 580,
            FontSize = 68,
            FontFamily = "Kantipur",
            Bold = true,
            Fill = "#17080A",
            HorizontalTextAlignment = "Center",
            VerticalTextAlignment = "Center",
            DataSourceId = liveNewsDs.Id,
            DataField = "brk_text",
            DataItemDurationSeconds = 5.0,
            StartSeconds = 0.5,
            EndSeconds = duration,
            AnimationIn = "Scale Down",
            AnimationInSeconds = 0.6,
            AnimationOut = "Fade",
            AnimationOutSeconds = 0.35,
            DurationSeconds = duration
        });

        return project;
    }

    private static CgProject CreateAp1hdTickerDemo()
    {
        const double duration = 30.0;
        var project = new CgProject { Id = new Guid("55556666-7777-8888-9999-000011112222"), Name = Ap1hdTickerDemoName, Width = 1920, Height = 1080, FrameRate = 50, DurationSeconds = duration, Loop = true, OnAir = false };
        var ds = new CgDataSource
        {
            Id = Guid.NewGuid(),
            Name = "AP1 HD Category Ticker Feed",
            SourceType = "LocalJson",
            Source = "ticker_categories.sample.json",
            CachedItemsJson = "[{\"category\":\"WORLD NEWS\",\"items\":[\"Global leaders conclude international sustainable energy accord with net zero milestones\",\"United Nations summit passes landmark multilateral digital safety framework\",\"International space agency completes construction of lunar gateway module\",\"European rail consortium launches high-speed zero-emission transnational transit\"]},{\"category\":\"CRICKET\",\"items\":[\"National team secures decisive 5-wicket victory in international series opener\",\"Star batter smashes fastest championship century in tournament history\",\"Strike bowler claims remarkable six-wicket haul in thrilling death overs\"]},{\"category\":\"BUSINESS\",\"items\":[\"Global equity markets climb to record heights led by AI infrastructure expansion\",\"Central banks maintain benchmark interest rate stability amid cooling inflation\",\"Clean technology investments surpass fossil fuels for third consecutive year\"]},{\"category\":\"WEATHER\",\"items\":[\"Clear blue skies and comfortable seasonal warmth forecasted across metro regions\",\"Mountain highways report optimal transit conditions with dry road surfaces\",\"Coastal districts enjoy gentle offshore breezes and calm maritime conditions\"]}]"
        };
        project.DataSources.Add(ds);
        var g = Guid.NewGuid();
        project.Groups.Add(new CgGroup { Id = g, Name = "AP1 HD Ticker Group" });

        var scrollBandPath = ResolveAp1hdPath("Scroll");
        project.Layers.Add(new CgLayer
        {
            Id = Guid.NewGuid(),
            Name = "AP1 HD Scroll Band",
            Type = "ImageSequence",
            Source = scrollBandPath,
            X = 0,
            Y = 998,
            Width = 1920,
            Height = 82,
            GroupId = g,
            StartSeconds = 0,
            EndSeconds = duration,
            DurationSeconds = duration
        });

        // AP1 HD Category Badge Plate
        Add(project, new CgLayer
        {
            Name = "Category Badge Plate",
            Role = "Badge",
            Type = "Shape",
            ShapeKind = "Rectangle",
            X = 0,
            Y = 998,
            Width = 260,
            Height = 82,
            Background = "#800818",
            UseGradient = true,
            GradientColor2 = "#7E0919",
            GradientAngle = 90,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomRight = 6,
            BorderWidth = 0,
            GroupId = g,
            AnimationIn = "Slide Left",
            AnimationInSeconds = 0.35,
            AnimationOut = "Slide Left",
            AnimationOutSeconds = 0.35,
            StartSeconds = 0,
            EndSeconds = duration,
            DurationSeconds = duration
        });

        // Category Badge Text
        Add(project, new CgLayer
        {
            Name = "Category Badge Text",
            Role = "Badge",
            Type = "Text",
            Text = "WORLD NEWS",
            X = 10,
            Y = 998,
            Width = 240,
            Height = 82,
            FontSize = 24,
            FontFamily = "Segoe UI",
            Bold = true,
            Fill = "#FFFFFFFF",
            HorizontalTextAlignment = "Center",
            VerticalTextAlignment = "Center",
            ShadowEnabled = true,
            ShadowColor = "#80000000",
            ShadowOffsetX = 1,
            ShadowOffsetY = 1,
            ShadowBlur = 2,
            DataSourceId = ds.Id,
            DataField = "category",
            GroupId = g,
            AnimationIn = "Fade",
            AnimationInSeconds = 0.25,
            AnimationOut = "Fade",
            AnimationOutSeconds = 0.25,
            StartSeconds = 0,
            EndSeconds = duration,
            DurationSeconds = duration
        });

        // Headline Ticker Layer
        Add(project, new CgLayer
        {
            Name = "Headline Ticker",
            Type = "Ticker",
            Text = "Global leaders conclude international sustainable energy accord with net zero milestones  ★  United Nations summit passes landmark multilateral digital safety framework  ★  International space agency completes construction of lunar gateway module",
            X = 275,
            Y = 998,
            Width = 1640,
            Height = 82,
            FontSize = 32,
            FontFamily = "Segoe UI",
            Bold = true,
            Fill = "#FFFFFFFF",
            Speed = 160,
            TickerSpeed = 160,
            TickerMode = "Continuous",
            TickerRepeat = true,
            TickerGap = 120,
            TickerSeparator = "  ★  ",
            TickerCategoriesEnabled = true,
            DataSourceId = ds.Id,
            DataField = "items",
            GroupId = g,
            StartSeconds = 0,
            EndSeconds = duration,
            DurationSeconds = duration
        });

        return project;
    }

    private static CgProject CreateAp1hdLivePlateDemo()
    {
        const double duration = 10.0;
        var project = new CgProject { Id = new Guid("66667777-8888-9999-0000-111122223333"), Name = Ap1hdLivePlateDemoName, Width = 1920, Height = 1080, FrameRate = 50, DurationSeconds = duration };
        var seqPath = ResolveAp1hdPath("Live");
        project.Layers.Add(new CgLayer { Id = Guid.NewGuid(), Name = "AP1 HD Live Plate Sequence", Type = "ImageSequence", Source = seqPath, X = 1640, Y = 180, Width = 200, Height = 112, SequenceFps = 25, SequenceStartFrame = 0, SequenceEndFrame = 304, SequenceLoop = true, SequenceHoldLastFrame = true, StartSeconds = 0, EndSeconds = duration, Visible = true, Opacity = 1.0 });
        project.Layers.Add(new CgLayer { Id = Guid.NewGuid(), Name = "Live Plate Text", Type = "Text", Text = "LIVE", X = 1640, Y = 215, Width = 200, Height = 40, FontSize = 28, FontFamily = "Segoe UI", Bold = true, Fill = "#FFFFFFFF", StartSeconds = 0.50, EndSeconds = duration, AnimationIn = "Fade", AnimationInSeconds = 0.35, AnimationOut = "Fade", AnimationOutSeconds = 0.35, Visible = true, Opacity = 1.0 });
        return project;
    }

    private static CgProject CreateAp1hdLogoDemo()
    {
        const double duration = 30.0;
        var project = new CgProject { Id = new Guid("77778888-9999-0000-1111-222233334444"), Name = Ap1hdLogoDemoName, Width = 1920, Height = 1080, FrameRate = 50, DurationSeconds = duration };
        var seqPath = ResolveAp1hdPath("logo");
        project.Layers.Add(new CgLayer { Id = Guid.NewGuid(), Name = "AP1 HD Station Logo Sequence", Type = "ImageSequence", Source = seqPath, X = 1550, Y = 40, Width = 320, Height = 180, SequenceFps = 25, SequenceStartFrame = 0, SequenceEndFrame = 193, SequenceLoop = true, SequenceHoldLastFrame = true, StartSeconds = 0, EndSeconds = duration, Visible = true, Opacity = 1.0 });
        return project;
    }

    private static CgProject CreateAp1hdWeatherDemo()
    {
        const double duration = 20.0;
        var project = new CgProject { Id = new Guid("88889999-0000-1111-2222-333344445555"), Name = Ap1hdWeatherDemoName, Width = 1920, Height = 1080, FrameRate = 50, DurationSeconds = duration, Loop = true };
        var weatherDs = new CgDataSource
        {
            Id = Guid.NewGuid(),
            Name = "AP1 HD Nepal Weather Feed",
            SourceType = "LocalJson",
            Source = "nepal_weather.sample.json",
            CachedItemsJson = "[{\"cityName\":\"Kathmandu\",\"temp\":24,\"min\":16,\"max\":27,\"condition\":\"Partly Cloudy\",\"humidity\":\"62%\",\"icon\":\"02d\"},{\"cityName\":\"Pokhara\",\"temp\":26,\"min\":18,\"max\":29,\"condition\":\"Sunny\",\"humidity\":\"55%\",\"icon\":\"01d\"},{\"cityName\":\"Biratnagar\",\"temp\":30,\"min\":22,\"max\":33,\"condition\":\"Cloudy\",\"humidity\":\"70%\",\"icon\":\"04d\"},{\"cityName\":\"Nepalgunj\",\"temp\":32,\"min\":24,\"max\":35,\"condition\":\"Clear Night\",\"humidity\":\"48%\",\"icon\":\"01n\"},{\"cityName\":\"Dhangadhi\",\"temp\":31,\"min\":23,\"max\":34,\"condition\":\"Rain\",\"humidity\":\"72%\",\"icon\":\"10d\"},{\"cityName\":\"Dharan\",\"temp\":28,\"min\":20,\"max\":31,\"condition\":\"Mist\",\"humidity\":\"75%\",\"icon\":\"50d\"},{\"cityName\":\"Chitwan\",\"temp\":29,\"min\":21,\"max\":32,\"condition\":\"Thunderstorm\",\"humidity\":\"78%\",\"icon\":\"11d\"},{\"cityName\":\"Butwal\",\"temp\":30,\"min\":22,\"max\":33,\"condition\":\"Showers\",\"humidity\":\"68%\",\"icon\":\"09d\"}]"
        };
        project.DataSources.Add(weatherDs);

        // Board background
        project.Layers.Add(new CgLayer
        {
            Id = Guid.NewGuid(),
            Name = "Weather Board Plate",
            Type = "Shape",
            ShapeKind = "Rectangle",
            X = 120,
            Y = 160,
            Width = 1680,
            Height = 820,
            Background = "#081422",
            UseGradient = true,
            GradientColor2 = "#10253D",
            GradientAngle = 135,
            BorderColor = "#FFD84A",
            BorderWidth = 2,
            CornerRadius = 12,
            ShadowEnabled = true,
            ShadowColor = "#80000000",
            ShadowOffsetX = 4,
            ShadowOffsetY = 4,
            ShadowBlur = 12,
            StartSeconds = 0,
            EndSeconds = duration,
            DurationSeconds = duration
        });

        // Board Header
        project.Layers.Add(new CgLayer
        {
            Id = Guid.NewGuid(),
            Name = "Weather Header Bar",
            Type = "Shape",
            ShapeKind = "Rectangle",
            X = 120,
            Y = 160,
            Width = 1680,
            Height = 80,
            Background = "#800818",
            UseGradient = true,
            GradientColor2 = "#7E0919",
            CornerRadiusTopLeft = 12,
            CornerRadiusTopRight = 12,
            StartSeconds = 0,
            EndSeconds = duration,
            DurationSeconds = duration
        });

        project.Layers.Add(new CgLayer
        {
            Id = Guid.NewGuid(),
            Name = "Weather Header Title",
            Type = "Text",
            Text = "AP1 HD · NEPAL WEATHER · नेपाल मौसम",
            X = 150,
            Y = 180,
            Width = 1620,
            Height = 50,
            FontSize = 32,
            FontFamily = "Segoe UI",
            Bold = true,
            Fill = "#FFD84A",
            StartSeconds = 0,
            EndSeconds = duration,
            DurationSeconds = duration
        });

        // City weather cards
        var cities = new[]
        {
            (City: "काठमाडौँ (Kathmandu)", Temp: "24°C", MinMax: "16° / 27°C", Cond: "आंशिक बदली (Partly Cloudy)", X: 160, Y: 280),
            (City: "पोखरा (Pokhara)", Temp: "26°C", MinMax: "18° / 29°C", Cond: "घाम लाग्ने (Sunny)", X: 980, Y: 280),
            (City: "विराटनगर (Biratnagar)", Temp: "30°C", MinMax: "22° / 33°C", Cond: "न्यानो (Warm)", X: 160, Y: 450),
            (City: "नेपालगन्ज (Nepalgunj)", Temp: "32°C", MinMax: "24° / 35°C", Cond: "सफा (Clear)", X: 980, Y: 450),
            (City: "धनगढी (Dhangadhi)", Temp: "31°C", MinMax: "23° / 34°C", Cond: "घाम लाग्ने (Sunny)", X: 160, Y: 620),
            (City: "धरान (Dharan)", Temp: "28°C", MinMax: "20° / 31°C", Cond: "सुहाउँदो (Pleasant)", X: 980, Y: 620),
            (City: "चितवन (Chitwan)", Temp: "29°C", MinMax: "21° / 32°C", Cond: "न्यानो (Warm)", X: 160, Y: 790),
            (City: "बुटवल (Butwal)", Temp: "30°C", MinMax: "22° / 33°C", Cond: "सफा (Clear)", X: 980, Y: 790)
        };
        var iconCodes = new[] { "02d", "01d", "04d", "01n", "10d", "50d", "11d", "09d" };

        for (var cityIndex = 0; cityIndex < cities.Length; cityIndex++)
        {
            var c = cities[cityIndex];
            project.Layers.Add(new CgLayer
            {
                Id = Guid.NewGuid(),
                Name = c.City + " Card Plate",
                Type = "Shape",
                ShapeKind = "Rectangle",
                X = c.X,
                Y = c.Y,
                Width = 780,
                Height = 140,
                Background = "#152436",
                BorderColor = "#2C4058",
                BorderWidth = 1,
                CornerRadius = 8,
                StartSeconds = 0.2,
                EndSeconds = duration,
                DurationSeconds = duration
            });

            project.Layers.Add(new CgLayer
            {
                Id = Guid.NewGuid(),
                Name = c.City + " Name",
                Type = "Text",
                Text = c.City,
                X = c.X + 24,
                Y = c.Y + 20,
                Width = 470,
                Height = 36,
                FontSize = 24,
                FontFamily = "Segoe UI",
                Bold = true,
                Fill = "#FFFFFFFF",
                StartSeconds = 0.3,
                EndSeconds = duration,
                DurationSeconds = duration
            });

            project.Layers.Add(new CgLayer
            {
                Id = Guid.NewGuid(),
                Name = c.City + " Animated Icon",
                Type = "WeatherIcon",
                Text = iconCodes[cityIndex],
                X = c.X + 500,
                Y = c.Y + 15,
                Width = 115,
                Height = 110,
                DataSourceId = weatherDs.Id,
                DataField = "icon",
                DataItemOffset = cityIndex,
                DataItemDurationSeconds = 3600,
                StartSeconds = 0.25,
                EndSeconds = duration,
                DurationSeconds = duration,
                AnimationIn = "Zoom",
                AnimationInSeconds = 0.45,
                GlowColor = "#66FFFFFF",
                GlowRadius = 4
            });

            project.Layers.Add(new CgLayer
            {
                Id = Guid.NewGuid(),
                Name = c.City + " Condition",
                Type = "Text",
                Text = c.Cond + "  ·  " + c.MinMax,
                X = c.X + 24,
                Y = c.Y + 68,
                Width = 500,
                Height = 30,
                FontSize = 18,
                FontFamily = "Segoe UI",
                Fill = "#B0BEC5",
                StartSeconds = 0.4,
                EndSeconds = duration,
                DurationSeconds = duration
            });

            project.Layers.Add(new CgLayer
            {
                Id = Guid.NewGuid(),
                Name = c.City + " Temp",
                Type = "Text",
                Text = c.Temp,
                X = c.X + 620,
                Y = c.Y + 25,
                Width = 130,
                Height = 80,
                FontSize = 52,
                FontFamily = "Segoe UI",
                Bold = true,
                Fill = "#FFD84A",
                HorizontalTextAlignment = "Right",
                StartSeconds = 0.3,
                EndSeconds = duration,
                DurationSeconds = duration
            });
        }

        return project;
    }
    #endregion

    #region Asset Path Resolvers
    public static string ResolvePrimePath(string subfolder)
    {
        var normalizedSub = subfolder.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var baseDirs = new List<string?>
        {
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory(),
            AppDomain.CurrentDomain.BaseDirectory,
            Path.GetDirectoryName(Environment.ProcessPath),
            Environment.GetEnvironmentVariable("KASHTRIX_WORKSPACE_ROOT"),
            @"c:\Users\karnk\Downloads\Kashtrix-COMPLETE-PROJECT-FIX49H-FULL-BUILD-DEBUG-RECOVERY-FINAL\Kashtrix-FIX49H"
        };

        foreach (var baseDir in baseDirs)
        {
            if (string.IsNullOrWhiteSpace(baseDir) || !Directory.Exists(baseDir)) continue;
            var dir = new DirectoryInfo(baseDir);
            for (int i = 0; i < 12 && dir != null; i++)
            {
                var candidates = new[]
                {
                    Path.Combine(dir.FullName, "cgdemo-templates", "prime", "assets", "breakingNews", normalizedSub),
                    Path.Combine(dir.FullName, "cgdemo-templates", "prime", "demo", "assets", "breakingNews", normalizedSub),
                    Path.Combine(dir.FullName, "cgdemo-templates", "prime", "assets", "FlashNews", normalizedSub),
                    Path.Combine(dir.FullName, "cgdemo-templates", "prime", "assets", normalizedSub),
                    Path.Combine(dir.FullName, "cgdemo-templates", "prime", normalizedSub),
                    Path.Combine(dir.FullName, "src", "BroadcastPlayout.App", "demos", "gfx", "prime", "assets", "breakingNews", normalizedSub),
                    Path.Combine(dir.FullName, "src", "BroadcastPlayout.App", "demos", "gfx", "prime", "demo", "assets", "breakingNews", normalizedSub),
                    Path.Combine(dir.FullName, "src", "BroadcastPlayout.App", "demos", "gfx", "prime", "assets", "FlashNews", normalizedSub),
                    Path.Combine(dir.FullName, "src", "BroadcastPlayout.App", "demos", "gfx", "prime", "assets", normalizedSub),
                    Path.Combine(dir.FullName, "demos", "gfx", "prime", "assets", normalizedSub)
                };
                foreach (var c in candidates)
                {
                    if (Directory.Exists(c)) return c;
                }

                var primeRoot = Path.Combine(dir.FullName, "cgdemo-templates", "prime");
                if (Directory.Exists(primeRoot))
                {
                    var targetLeaf = Path.GetFileName(normalizedSub);
                    try
                    {
                        var match = Directory.GetDirectories(primeRoot, targetLeaf, SearchOption.AllDirectories)
                            .FirstOrDefault(d => Directory.EnumerateFiles(d, "*.png").Any());
                        if (match != null) return match;
                    }
                    catch { }
                }

                dir = dir.Parent;
            }
        }

        return Path.Combine(AppContext.BaseDirectory, "cgdemo-templates", "prime", "assets", normalizedSub);
    }

    public static string ResolveSpace4kPath(string subfolder)
    {
        var normalizedSub = subfolder.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var baseDirs = new List<string?>
        {
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory(),
            AppDomain.CurrentDomain.BaseDirectory,
            Path.GetDirectoryName(Environment.ProcessPath),
            Environment.GetEnvironmentVariable("KASHTRIX_WORKSPACE_ROOT"),
            @"c:\Users\karnk\Downloads\Kashtrix-COMPLETE-PROJECT-FIX49H-FULL-BUILD-DEBUG-RECOVERY-FINAL\Kashtrix-FIX49H"
        };

        foreach (var baseDir in baseDirs)
        {
            if (string.IsNullOrWhiteSpace(baseDir) || !Directory.Exists(baseDir)) continue;
            var dir = new DirectoryInfo(baseDir);
            for (int i = 0; i < 12 && dir != null; i++)
            {
                var candidates = new[]
                {
                    Path.Combine(dir.FullName, "cgdemo-templates", "space4k", "assets", normalizedSub),
                    Path.Combine(dir.FullName, "cgdemo-templates", "space4k", "ALL CG PLATE", normalizedSub),
                    Path.Combine(dir.FullName, "cgdemo-templates", "space4k", normalizedSub),
                    Path.Combine(dir.FullName, "src", "BroadcastPlayout.App", "demos", "gfx", "space4k", "assets", normalizedSub),
                    Path.Combine(dir.FullName, "src", "BroadcastPlayout.App", "demos", "gfx", "html", "space4k", "assets", normalizedSub),
                    Path.Combine(dir.FullName, "src", "BroadcastPlayout.App", "demos", "gfx", "space4k", normalizedSub),
                    Path.Combine(dir.FullName, "demos", "gfx", "space4k", "assets", normalizedSub)
                };
                foreach (var c in candidates)
                {
                    if (Directory.Exists(c)) return c;
                }

                var spaceRoot = Path.Combine(dir.FullName, "cgdemo-templates", "space4k");
                if (Directory.Exists(spaceRoot))
                {
                    var targetLeaf = Path.GetFileName(normalizedSub);
                    try
                    {
                        var match = Directory.GetDirectories(spaceRoot, targetLeaf, SearchOption.AllDirectories)
                            .FirstOrDefault(d => Directory.EnumerateFiles(d, "*.png").Any());
                        if (match != null) return match;
                    }
                    catch { }
                }

                dir = dir.Parent;
            }
        }

        return Path.Combine(AppContext.BaseDirectory, "cgdemo-templates", "space4k", "assets", normalizedSub);
    }

    public static string ResolveAp1hdPath(string subfolder)
    {
        var normalizedSub = subfolder.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var baseDirs = new List<string?>
        {
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory(),
            AppDomain.CurrentDomain.BaseDirectory,
            Path.GetDirectoryName(Environment.ProcessPath),
            Environment.GetEnvironmentVariable("KASHTRIX_WORKSPACE_ROOT"),
            @"c:\Users\karnk\Downloads\Kashtrix-COMPLETE-PROJECT-FIX49H-FULL-BUILD-DEBUG-RECOVERY-FINAL\Kashtrix-FIX49H"
        };

        foreach (var baseDir in baseDirs)
        {
            if (string.IsNullOrWhiteSpace(baseDir) || !Directory.Exists(baseDir)) continue;
            var dir = new DirectoryInfo(baseDir);
            for (int i = 0; i < 12 && dir != null; i++)
            {
                var candidates = new[]
                {
                    Path.Combine(dir.FullName, "cgdemo-templates", "ap1hd", "assets", normalizedSub),
                    Path.Combine(dir.FullName, "cgdemo-templates", "ap1hd", "png", normalizedSub),
                    Path.Combine(dir.FullName, "cgdemo-templates", "ap1hd", normalizedSub),
                    Path.Combine(dir.FullName, "src", "BroadcastPlayout.App", "demos", "gfx", "ap1hd", "assets", normalizedSub),
                    Path.Combine(dir.FullName, "src", "BroadcastPlayout.App", "demos", "gfx", "ap1hd", "png", normalizedSub),
                    Path.Combine(dir.FullName, "src", "BroadcastPlayout.App", "demos", "gfx", "ap1hd", normalizedSub),
                    Path.Combine(dir.FullName, "demos", "gfx", "ap1hd", "assets", normalizedSub),
                    Path.Combine(dir.FullName, "demos", "gfx", "ap1hd", normalizedSub)
                };
                foreach (var c in candidates)
                {
                    if (Directory.Exists(c) || File.Exists(c)) return c;
                }

                var ap1Root = Path.Combine(dir.FullName, "cgdemo-templates", "ap1hd");
                if (Directory.Exists(ap1Root))
                {
                    var targetLeaf = Path.GetFileName(normalizedSub);
                    try
                    {
                        var matchDir = Directory.GetDirectories(ap1Root, targetLeaf, SearchOption.AllDirectories)
                            .FirstOrDefault(d => Directory.EnumerateFiles(d, "*.png").Any());
                        if (matchDir != null) return matchDir;

                        var matchFile = Directory.GetFiles(ap1Root, targetLeaf, SearchOption.AllDirectories).FirstOrDefault();
                        if (matchFile != null) return matchFile;
                    }
                    catch { }
                }

                dir = dir.Parent;
            }
        }

        return Path.Combine(AppContext.BaseDirectory, "cgdemo-templates", "ap1hd", "assets", normalizedSub);
    }

    public static string ResolvePublic4kPath(string subfolder)
    {
        var normalizedSub = subfolder.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var baseDirs = new List<string?>
        {
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory(),
            AppDomain.CurrentDomain.BaseDirectory,
            Path.GetDirectoryName(Environment.ProcessPath),
            Environment.GetEnvironmentVariable("KASHTRIX_WORKSPACE_ROOT"),
            @"c:\Users\karnk\Downloads\Kashtrix-COMPLETE-PROJECT-FIX49H-FULL-BUILD-DEBUG-RECOVERY-FINAL\Kashtrix-FIX49H"
        };

        foreach (var baseDir in baseDirs)
        {
            if (string.IsNullOrWhiteSpace(baseDir) || !Directory.Exists(baseDir)) continue;
            var dir = new DirectoryInfo(baseDir);
            for (int i = 0; i < 12 && dir != null; i++)
            {
                var candidates = new[]
                {
                    Path.Combine(dir.FullName, "cgdemo-templates", "public4k", "assets", normalizedSub),
                    Path.Combine(dir.FullName, "cgdemo-templates", "public4k", normalizedSub),
                    Path.Combine(dir.FullName, "src", "BroadcastPlayout.App", "demos", "gfx", "public4k", "assets", normalizedSub),
                    Path.Combine(dir.FullName, "demos", "gfx", "public4k", "assets", normalizedSub)
                };
                foreach (var c in candidates)
                {
                    if (Directory.Exists(c)) return c;
                }
                dir = dir.Parent;
            }
        }
        return Path.Combine(AppContext.BaseDirectory, "cgdemo-templates", "public4k", "assets", normalizedSub);
    }

    #endregion


    #region Playout Suite Templates
    private static CgProject CreateMusicNowPlayingDemo()
    {
        var p = new CgProject { Id = new Guid("11111111-aaaa-1111-aaaa-111111111111"), Name = MusicNowPlayingDemoName, Width = 1920, Height = 1080, FrameRate = 50, DurationSeconds = 12, OnAir = false };
        var g = Guid.NewGuid();
        p.Groups.Add(new CgGroup { Id = g, Name = "Music Lower Third" });
        Add(p, new CgLayer { Name = "Base Plate", Type = "Shape", ShapeKind = "Rounded Rectangle", X = 80, Y = 840, Width = 1020, Height = 160, Background = "#E50E1825", CornerRadius = 12, BorderWidth = 1, BorderColor = "#444A90E2", GroupId = g, AnimationIn = "Slide Left", AnimationOut = "Fade", AnimationInSeconds = 0.45, AnimationOutSeconds = 0.35 });
        Add(p, new CgLayer { Name = "Music Icon Plate", Type = "Shape", ShapeKind = "Rounded Rectangle", X = 95, Y = 855, Width = 60, Height = 60, Background = "#FF2872FA", CornerRadius = 8, GroupId = g, AnimationIn = "Slide Left", AnimationOut = "Fade" });
        Add(p, new CgLayer { Name = "Music Icon", Type = "Text", Text = "♪", X = 95, Y = 852, Width = 60, Height = 60, FontSize = 36, Fill = "#FFFFFFFF", HorizontalTextAlignment = "Center", GroupId = g, AnimationIn = "Fade", AnimationOut = "Fade" });
        Add(p, new CgLayer { Name = "Tag", Type = "Text", Text = "{Tag}", X = 170, Y = 855, Width = 400, Height = 32, FontSize = 20, Fill = "#FF64B5F6", Bold = true, GroupId = g, AnimationIn = "Fade", AnimationOut = "Fade" });
        Add(p, new CgLayer { Name = "Title", Type = "Text", Text = "{Title}", X = 170, Y = 890, Width = 910, Height = 54, FontSize = 38, Fill = "#FFFFFFFF", Bold = true, GroupId = g, AnimationIn = "Slide Up", AnimationOut = "Fade", TextAnimationPreset = "Slide Left" });
        Add(p, new CgLayer { Name = "Artist", Type = "Text", Text = "{Artist}", X = 170, Y = 946, Width = 910, Height = 36, FontSize = 24, Fill = "#FFB0BEC5", GroupId = g, AnimationIn = "Fade", AnimationOut = "Fade" });
        return p;
    }

    private static CgProject CreateMusicComingUpDemo()
    {
        var p = new CgProject { Id = new Guid("22222222-aaaa-1111-aaaa-222222222222"), Name = MusicComingUpDemoName, Width = 1920, Height = 1080, FrameRate = 50, DurationSeconds = 12, OnAir = false };
        var g = Guid.NewGuid();
        p.Groups.Add(new CgGroup { Id = g, Name = "Music Coming Up" });
        Add(p, new CgLayer { Name = "Base Plate", Type = "Shape", ShapeKind = "Rounded Rectangle", X = 80, Y = 840, Width = 1020, Height = 160, Background = "#E51A1426", CornerRadius = 12, BorderWidth = 1, BorderColor = "#44AB47BC", GroupId = g, AnimationIn = "Slide Left", AnimationOut = "Fade", AnimationInSeconds = 0.45, AnimationOutSeconds = 0.35 });
        Add(p, new CgLayer { Name = "Icon Plate", Type = "Shape", ShapeKind = "Rounded Rectangle", X = 95, Y = 855, Width = 60, Height = 60, Background = "#FFAB47BC", CornerRadius = 8, GroupId = g, AnimationIn = "Slide Left", AnimationOut = "Fade" });
        Add(p, new CgLayer { Name = "Icon", Type = "Text", Text = "⏭", X = 95, Y = 856, Width = 60, Height = 60, FontSize = 30, Fill = "#FFFFFFFF", HorizontalTextAlignment = "Center", GroupId = g, AnimationIn = "Fade", AnimationOut = "Fade" });
        Add(p, new CgLayer { Name = "Tag", Type = "Text", Text = "{Tag}", X = 170, Y = 855, Width = 400, Height = 32, FontSize = 20, Fill = "#FFCE93D8", Bold = true, GroupId = g, AnimationIn = "Fade", AnimationOut = "Fade" });
        Add(p, new CgLayer { Name = "Next Title", Type = "Text", Text = "{NextTitle}", X = 170, Y = 890, Width = 910, Height = 54, FontSize = 38, Fill = "#FFFFFFFF", Bold = true, GroupId = g, AnimationIn = "Slide Up", AnimationOut = "Fade", TextAnimationPreset = "Slide Left" });
        Add(p, new CgLayer { Name = "Next Artist", Type = "Text", Text = "{NextArtist}", X = 170, Y = 946, Width = 910, Height = 36, FontSize = 24, Fill = "#FFE1BEE7", GroupId = g, AnimationIn = "Fade", AnimationOut = "Fade" });
        return p;
    }

    private static CgProject CreateProgramNowPlayingDemo()
    {
        var p = new CgProject { Id = new Guid("33333333-bbbb-1111-bbbb-333333333333"), Name = ProgramNowPlayingDemoName, Width = 1920, Height = 1080, FrameRate = 50, DurationSeconds = 12, OnAir = false };
        var g = Guid.NewGuid();
        p.Groups.Add(new CgGroup { Id = g, Name = "Program Lower Third" });
        Add(p, new CgLayer { Name = "Base Plate", Type = "Shape", ShapeKind = "Rounded Rectangle", X = 80, Y = 840, Width = 1080, Height = 160, Background = "#E5141D2E", CornerRadius = 12, BorderWidth = 1, BorderColor = "#4442A5F5", GroupId = g, AnimationIn = "Slide Left", AnimationOut = "Fade", AnimationInSeconds = 0.45, AnimationOutSeconds = 0.35 });
        Add(p, new CgLayer { Name = "Accent Bar", Type = "Shape", ShapeKind = "Rounded Rectangle", X = 80, Y = 840, Width = 14, Height = 160, Background = "#FF1E88E5", GroupId = g, AnimationIn = "Slide Left", AnimationOut = "Fade" });
        Add(p, new CgLayer { Name = "Tag", Type = "Text", Text = "{Tag}", X = 110, Y = 855, Width = 400, Height = 32, FontSize = 20, Fill = "#FF90CAF9", Bold = true, GroupId = g, AnimationIn = "Fade", AnimationOut = "Fade" });
        Add(p, new CgLayer { Name = "Title", Type = "Text", Text = "{Title}", X = 110, Y = 890, Width = 1020, Height = 54, FontSize = 38, Fill = "#FFFFFFFF", Bold = true, GroupId = g, AnimationIn = "Slide Up", AnimationOut = "Fade", TextAnimationPreset = "Slide Left" });
        Add(p, new CgLayer { Name = "Description", Type = "Text", Text = "{Description}", X = 110, Y = 946, Width = 1020, Height = 36, FontSize = 24, Fill = "#FFB0BEC5", GroupId = g, AnimationIn = "Fade", AnimationOut = "Fade" });
        return p;
    }

    private static CgProject CreateProgramNextDemo()
    {
        var p = new CgProject { Id = new Guid("44444444-cccc-1111-cccc-444444444444"), Name = ProgramNextDemoName, Width = 1920, Height = 1080, FrameRate = 50, DurationSeconds = 12, OnAir = false };
        var g = Guid.NewGuid();
        p.Groups.Add(new CgGroup { Id = g, Name = "Program Next Lower Third" });
        Add(p, new CgLayer { Name = "Base Plate", Type = "Shape", ShapeKind = "Rounded Rectangle", X = 80, Y = 840, Width = 1080, Height = 160, Background = "#E512211C", CornerRadius = 12, BorderWidth = 1, BorderColor = "#4400E676", GroupId = g, AnimationIn = "Slide Left", AnimationOut = "Fade", AnimationInSeconds = 0.45, AnimationOutSeconds = 0.35 });
        Add(p, new CgLayer { Name = "Accent Bar", Type = "Shape", ShapeKind = "Rounded Rectangle", X = 80, Y = 840, Width = 14, Height = 160, Background = "#FF00C853", GroupId = g, AnimationIn = "Slide Left", AnimationOut = "Fade" });
        Add(p, new CgLayer { Name = "Tag", Type = "Text", Text = "NEXT ON KASHTRIX", X = 110, Y = 855, Width = 400, Height = 32, FontSize = 20, Fill = "#FFB9F6CA", Bold = true, GroupId = g, AnimationIn = "Fade", AnimationOut = "Fade" });
        Add(p, new CgLayer { Name = "Next Title", Type = "Text", Text = "{NextTitle}", X = 110, Y = 890, Width = 1020, Height = 54, FontSize = 38, Fill = "#FFFFFFFF", Bold = true, GroupId = g, AnimationIn = "Slide Up", AnimationOut = "Fade", TextAnimationPreset = "Slide Left" });
        Add(p, new CgLayer { Name = "Description", Type = "Text", Text = "{Description}", X = 110, Y = 946, Width = 1020, Height = 36, FontSize = 24, Fill = "#FFA7D7C5", GroupId = g, AnimationIn = "Fade", AnimationOut = "Fade" });
        return p;
    }

    private static CgProject CreateProgramComingUpDemo()
    {
        var p = new CgProject { Id = new Guid("44444444-bbbb-1111-bbbb-444444444444"), Name = ProgramComingUpDemoName, Width = 1920, Height = 1080, FrameRate = 50, DurationSeconds = 12, OnAir = false };
        var g = Guid.NewGuid();
        p.Groups.Add(new CgGroup { Id = g, Name = "Program Coming Up" });
        Add(p, new CgLayer { Name = "Base Plate", Type = "Shape", ShapeKind = "Rounded Rectangle", X = 80, Y = 840, Width = 1080, Height = 160, Background = "#E51D1E28", CornerRadius = 12, BorderWidth = 1, BorderColor = "#44FFB74D", GroupId = g, AnimationIn = "Slide Left", AnimationOut = "Fade", AnimationInSeconds = 0.45, AnimationOutSeconds = 0.35 });
        Add(p, new CgLayer { Name = "Accent Bar", Type = "Shape", ShapeKind = "Rounded Rectangle", X = 80, Y = 840, Width = 14, Height = 160, Background = "#FFF57C00", GroupId = g, AnimationIn = "Slide Left", AnimationOut = "Fade" });
        Add(p, new CgLayer { Name = "Tag", Type = "Text", Text = "{Tag}", X = 110, Y = 855, Width = 400, Height = 32, FontSize = 20, Fill = "#FFFFCC80", Bold = true, GroupId = g, AnimationIn = "Fade", AnimationOut = "Fade" });
        Add(p, new CgLayer { Name = "Next Title", Type = "Text", Text = "{NextTitle}", X = 110, Y = 890, Width = 1020, Height = 54, FontSize = 38, Fill = "#FFFFFFFF", Bold = true, GroupId = g, AnimationIn = "Slide Up", AnimationOut = "Fade", TextAnimationPreset = "Slide Left" });
        Add(p, new CgLayer { Name = "Description", Type = "Text", Text = "{Description}", X = 110, Y = 946, Width = 1020, Height = 36, FontSize = 24, Fill = "#FFE0E0E0", GroupId = g, AnimationIn = "Fade", AnimationOut = "Fade" });
        return p;
    }

    private static CgProject CreateCommercialBackInDemo()
    {
        var p = new CgProject { Id = new Guid("77777777-dddd-1111-dddd-777777777777"), Name = CommercialBackInDemoName, Width = 1920, Height = 1080, FrameRate = 50, DurationSeconds = 90, OnAir = false };
        var g = Guid.NewGuid();
        p.Groups.Add(new CgGroup { Id = g, Name = "Back In Countdown" });
        Add(p, new CgLayer { Name = "Base Plate", Type = "Shape", ShapeKind = "Rounded Rectangle", X = 80, Y = 840, Width = 960, Height = 160, Background = "#E61A1408", CornerRadius = 12, BorderWidth = 1.5, BorderColor = "#66F59E0B", GroupId = g, AnimationIn = "Slide Left", AnimationOut = "Fade", AnimationInSeconds = 0.45, AnimationOutSeconds = 0.35 });
        Add(p, new CgLayer { Name = "Badge Plate", Type = "Shape", ShapeKind = "Rounded Rectangle", X = 100, Y = 855, Width = 180, Height = 34, Background = "#FFF59E0B", CornerRadius = 6, GroupId = g, AnimationIn = "Fade", AnimationOut = "Fade" });
        Add(p, new CgLayer { Name = "Badge Text", Type = "Text", Text = "COMMERCIAL BREAK", X = 100, Y = 858, Width = 180, Height = 30, FontSize = 14, Fill = "#FF000000", Bold = true, HorizontalTextAlignment = "Center", GroupId = g, AnimationIn = "Fade", AnimationOut = "Fade" });
        Add(p, new CgLayer { Name = "Countdown Timer", Type = "Timer", TimerMode = "CommercialBackIn", TimerFormat = "mm:ss", TimerPrefix = "BACK IN ", Text = "BACK IN {BackIn}", X = 300, Y = 852, Width = 400, Height = 40, FontSize = 26, Fill = "#FFFBBF24", Bold = true, GroupId = g, AnimationIn = "Fade", AnimationOut = "Fade" });
        Add(p, new CgLayer { Name = "Next Title", Type = "Text", Text = "COMING UP NEXT: {Title}", X = 100, Y = 896, Width = 920, Height = 48, FontSize = 34, Fill = "#FFFFFFFF", Bold = true, GroupId = g, AnimationIn = "Slide Up", AnimationOut = "Fade", TextAnimationPreset = "Slide Left" });
        Add(p, new CgLayer { Name = "Description", Type = "Text", Text = "{Description}", X = 100, Y = 948, Width = 920, Height = 32, FontSize = 20, Fill = "#FFE0D5B8", GroupId = g, AnimationIn = "Fade", AnimationOut = "Fade" });
        return p;
    }

    private static CgProject CreateEventTimerDemo()
    {
        var p = new CgProject { Id = new Guid("88888888-eeee-2222-eeee-888888888888"), Name = EventTimerDemoName, Width = 1920, Height = 1080, FrameRate = 50, DurationSeconds = 15, Loop = true, OnAir = false };
        var g = Guid.NewGuid();
        p.Groups.Add(new CgGroup { Id = g, Name = "Event Timer Clock" });
        Add(p, new CgLayer { Name = "Timer Base Plate", Type = "Shape", ShapeKind = "Rounded Rectangle", X = 1440, Y = 80, Width = 400, Height = 100, Background = "#E6090D16", CornerRadius = 12, BorderWidth = 1.5, BorderColor = "#6600E5FF", GroupId = g, AnimationIn = "Slide Right", AnimationOut = "Fade", AnimationInSeconds = 0.45, AnimationOutSeconds = 0.35 });
        Add(p, new CgLayer { Name = "Timer Badge Tag", Type = "Text", Text = "EVENT COUNTDOWN", X = 1460, Y = 92, Width = 360, Height = 24, FontSize = 14, Fill = "#FF00E5FF", Bold = true, HorizontalTextAlignment = "Center", GroupId = g, AnimationIn = "Fade", AnimationOut = "Fade" });
        Add(p, new CgLayer { Name = "Timer Value", Type = "Timer", TimerMode = "Countdown", TimerStartSeconds = 3600, TimerFormat = "hh:mm:ss", TimerPrefix = "", TimerAutoStop = false, Text = "01:00:00", X = 1460, Y = 118, Width = 360, Height = 50, FontSize = 36, FontFamily = "Segoe UI", Fill = "#FFFFFFFF", Bold = true, HorizontalTextAlignment = "Center", GroupId = g, AnimationIn = "Fade", AnimationOut = "Fade" });
        return p;
    }

    private static CgLayer ClockText(string name, string format, string timeZoneId, double x, double y, double w, double h, double size, string fill, Guid g, string align = "Left", string calendar = "AD")
        => new() { Name = name, Type = "DateTime", CalendarSystem = calendar, DataFormat = format, Text = format, TimeZoneId = timeZoneId, X = x, Y = y, Width = w, Height = h, FontSize = size, FontFamily = "Segoe UI", Bold = true, Fill = fill, Background = "#00000000", HorizontalTextAlignment = align, GroupId = g, AnimationIn = "Fade", AnimationOut = "Fade", AnimationInSeconds = .5, AnimationOutSeconds = .35 };

    // Full-frame world clock. Not looped: animates in, then holds on air until TAKE OUT.
    // Every clock/date layer carries its own TimeZoneId, so cities can be changed in the inspector.
    private static CgProject CreateFullscreenWorldClockDemo()
    {
        const string mainZone = "Nepal Standard Time";
        var p = new CgProject { Id = new Guid("c10c0001-0000-4000-8000-000000000001"), Name = FullscreenWorldClockDemoName, Category = "CLOCK", Width = 1920, Height = 1080, FrameRate = 50, DurationSeconds = 10, OnAir = false };
        var g = Guid.NewGuid();
        p.Groups.Add(new CgGroup { Id = g, Name = "World Clock Fullscreen" });

        Add(p, Shape("Backdrop", 0, 0, 1920, 1080, "#FF06101D", "#FF142843", g, 0, 0, 0));
        Add(p, Shape("Top Accent", 0, 0, 1920, 10, "#FF00E5FF", "#FF2979FF", g, 0, 0, 0));
        Add(p, Text("Title", "WORLD CLOCK", 150, 52, 900, 76, 50, "#FFFFFFFF", g, "Tracking Reveal"));
        var station = Text("Station", "KASHTRIX  ·  LIVE TIME", 1070, 68, 700, 50, 24, "#FF00E5FF", g, "Fade Cascade");
        station.HorizontalTextAlignment = "Right";
        Add(p, station);

        Add(p, CrystalCard("Main Panel", 140, 160, 1640, 620, "#CC0B1828", "#AA16304D", g, 28));
        Add(p, new CgLayer { Name = "Analog Clock", Type = "AnalogClock", TimeZoneId = mainZone, X = 200, Y = 190, Width = 560, Height = 560, Fill = "#FFFFFFFF", Background = "#FF0A1626", GroupId = g, AnimationIn = "Zoom", AnimationInSeconds = .55, AnimationOut = "Fade", AnimationOutSeconds = .35 });
        Add(p, Text("Main City", "KATHMANDU  ·  NEPAL", 860, 215, 880, 50, 30, "#FF00E5FF", g, "Type On"));
        var mainTime = new CgLayer { Name = "Main Time", Type = "DigitalClock", Text = "00:00:00", DataFormat = "HH:mm:ss", TimeZoneId = mainZone, X = 850, Y = 265, Width = 900, Height = 200, FontSize = 150, FontFamily = "Segoe UI", Bold = true, Fill = "#FFFFFFFF", Background = "#00000000", GroupId = g, AnimationIn = "Fade", AnimationOut = "Fade", AnimationInSeconds = .5, AnimationOutSeconds = .35 };
        Add(p, mainTime);
        Add(p, ClockText("Main Date", "dddd, dd MMMM yyyy", mainZone, 860, 480, 880, 56, 36, "#FFE3F2FD", g));
        Add(p, ClockText("Main BS Date", "dddd, DD MMMM YYYY", mainZone, 860, 548, 880, 52, 32, "#FFFFD54F", g, calendar: "BS"));
        Add(p, Text("Main Offset", "UTC +05:45", 860, 640, 400, 44, 26, "#FF90A4AE", g, "Fade Cascade"));

        var cities = new[]
        {
            (Name: "NEW DELHI", Zone: "India Standard Time"),
            (Name: "DUBAI", Zone: "Arabian Standard Time"),
            (Name: "LONDON", Zone: "GMT Standard Time"),
            (Name: "NEW YORK", Zone: "Eastern Standard Time")
        };
        for (var n = 0; n < cities.Length; n++)
        {
            var c = cities[n];
            var x = 140 + n * 415;
            Add(p, CrystalCard($"{c.Name} Card", x, 815, 395, 195, "#CC0B1828", "#AA16304D", g, 20));
            var city = Text($"{c.Name} Name", c.Name, x, 833, 395, 40, 26, "#FF00E5FF", g, "Type On");
            city.HorizontalTextAlignment = "Center";
            Add(p, city);
            Add(p, ClockText($"{c.Name} Time", "HH:mm", c.Zone, x, 873, 395, 88, 70, "#FFFFFFFF", g, "Center"));
            Add(p, ClockText($"{c.Name} Day", "ddd  dd MMM", c.Zone, x, 960, 395, 36, 22, "#FF90A4AE", g, "Center"));
        }
        return p;
    }

    // Full-frame weather forecast: today's hero card (row 0) plus the next five days (rows 1..5).
    // Rows follow the OpenWeather loader's field names (cityName, day, date, temp, min, max,
    // feelsLike, humidity, wind, condition, description, icon), so switching the data source to
    // SourceType "OpenWeather" with Source "Kathmandu,NP" drives the same layout with live data.
    private static CgProject CreateFullscreenWeatherDemo()
    {
        const double fixedRow = 1_000_000_000; // item duration long enough that each layer keeps its row
        var p = new CgProject { Id = new Guid("c10c0002-0000-4000-8000-000000000002"), Name = FullscreenWeatherDemoName, Width = 1920, Height = 1080, FrameRate = 50, DurationSeconds = 10, OnAir = false };
        var ds = new CgDataSource
        {
            Id = new Guid("c10c0002-0000-4000-8000-0000000000d5"),
            Name = "Fullscreen Weather Feed",
            SourceType = "LocalJson",
            Source = "fullscreen_weather.sample.json",
            RefreshSeconds = 900,
            CachedItemsJson = "[{\"cityName\":\"Kathmandu\",\"day\":\"TODAY\",\"date\":\"\",\"temp\":\"24°C\",\"min\":\"15°C\",\"max\":\"27°C\",\"feelsLike\":\"25°C\",\"humidity\":\"62%\",\"wind\":\"9 km/h\",\"condition\":\"Clouds\",\"description\":\"Partly cloudy\",\"icon\":\"02d\"}," +
                              "{\"cityName\":\"Kathmandu\",\"day\":\"TUE\",\"date\":\"\",\"temp\":\"25°C\",\"min\":\"16°C\",\"max\":\"28°C\",\"feelsLike\":\"26°C\",\"humidity\":\"58%\",\"wind\":\"8 km/h\",\"condition\":\"Clear\",\"description\":\"Sunny\",\"icon\":\"01d\"}," +
                              "{\"cityName\":\"Kathmandu\",\"day\":\"WED\",\"date\":\"\",\"temp\":\"23°C\",\"min\":\"16°C\",\"max\":\"25°C\",\"feelsLike\":\"23°C\",\"humidity\":\"74%\",\"wind\":\"12 km/h\",\"condition\":\"Rain\",\"description\":\"Light rain\",\"icon\":\"10d\"}," +
                              "{\"cityName\":\"Kathmandu\",\"day\":\"THU\",\"date\":\"\",\"temp\":\"21°C\",\"min\":\"15°C\",\"max\":\"23°C\",\"feelsLike\":\"21°C\",\"humidity\":\"81%\",\"wind\":\"15 km/h\",\"condition\":\"Thunderstorm\",\"description\":\"Thunderstorms\",\"icon\":\"11d\"}," +
                              "{\"cityName\":\"Kathmandu\",\"day\":\"FRI\",\"date\":\"\",\"temp\":\"22°C\",\"min\":\"14°C\",\"max\":\"24°C\",\"feelsLike\":\"22°C\",\"humidity\":\"70%\",\"wind\":\"10 km/h\",\"condition\":\"Clouds\",\"description\":\"Overcast\",\"icon\":\"04d\"}," +
                              "{\"cityName\":\"Kathmandu\",\"day\":\"SAT\",\"date\":\"\",\"temp\":\"24°C\",\"min\":\"15°C\",\"max\":\"26°C\",\"feelsLike\":\"24°C\",\"humidity\":\"60%\",\"wind\":\"7 km/h\",\"condition\":\"Clouds\",\"description\":\"Few clouds\",\"icon\":\"02d\"}]"
        };
        p.DataSources.Add(ds);
        var g = Guid.NewGuid();
        p.Groups.Add(new CgGroup { Id = g, Name = "Weather Fullscreen" });

        CgLayer Bind(CgLayer layer, string field, int row)
        {
            layer.DataSourceId = ds.Id; layer.DataField = field; layer.DataItemOffset = row; layer.DataItemDurationSeconds = fixedRow;
            return layer;
        }
        CgLayer Icon(string name, int row, double x, double y, double w, double h)
            => Bind(new CgLayer { Name = name, Type = "WeatherIcon", Text = "02d", X = x, Y = y, Width = w, Height = h, GroupId = g, GlowColor = "#88FFFFFF", GlowRadius = 6, AnimationIn = "Zoom", AnimationInSeconds = .55, AnimationOut = "Fade", AnimationOutSeconds = .35 }, "icon", row);

        Add(p, Shape("Backdrop", 0, 0, 1920, 1080, "#FF0B3D6B", "#FF081627", g, 0, 0, 0));
        Add(p, Shape("Top Accent", 0, 0, 1920, 10, "#FFFFC107", "#FFFF7043", g, 0, 0, 0));
        Add(p, Text("Title", "WEATHER FORECAST", 120, 52, 1000, 76, 50, "#FFFFFFFF", g, "Tracking Reveal"));
        Add(p, ClockText("Header Date", "dddd, dd MMMM yyyy", string.Empty, 1100, 68, 700, 50, 26, "#FFFFD54F", g, "Right"));

        // Today hero card
        Add(p, CrystalCard("Today Panel", 120, 160, 760, 860, "#CC0B1F36", "#AA1B3B5E", g, 28));
        Add(p, Bind(Text("Today City", "Kathmandu", 170, 190, 660, 64, 46, "#FFFFFFFF", g, "Type On"), "cityName", 0));
        Add(p, Text("Today Label", "TODAY", 170, 256, 300, 40, 24, "#FFFFC107", g, "Fade Cascade"));
        Add(p, Icon("Today Icon", 0, 160, 300, 320, 300));
        var temp = Bind(Text("Today Temp", "24°C", 440, 340, 410, 170, 118, "#FFFFFFFF", g, "None"), "temp", 0);
        temp.HorizontalTextAlignment = "Right";
        Add(p, temp);
        var desc = Bind(Text("Today Condition", "Partly cloudy", 170, 610, 660, 56, 38, "#FFE3F2FD", g, "Fade Cascade"), "description", 0);
        desc.HorizontalTextAlignment = "Center";
        Add(p, desc);
        var stats = new[] { ("HIGH", "max"), ("LOW", "min"), ("HUMIDITY", "humidity"), ("WIND", "wind") };
        for (var n = 0; n < stats.Length; n++)
        {
            var (label, field) = stats[n];
            var x = 160 + (n % 2) * 350;
            var y = 700 + (n / 2) * 150;
            Add(p, CrystalCard($"Stat {label} Tile", x, y, 330, 130, "#AA0A1A2D", "#88163352", g, 18));
            Add(p, Text($"Stat {label} Label", label, x + 24, y + 12, 290, 32, 20, "#FF90CAF9", g, "Fade Cascade"));
            Add(p, Bind(Text($"Stat {label} Value", "--", x + 24, y + 54, 290, 62, 42, "#FFFFFFFF", g, "None"), field, 0));
        }

        // Next five days
        for (var n = 0; n < 5; n++)
        {
            var row = n + 1;
            var y = 160 + n * 176;
            Add(p, CrystalCard($"Day {row} Card", 940, y, 860, 156, "#CC0B1F36", "#AA1B3B5E", g, 22));
            Add(p, Bind(Text($"Day {row} Name", "DAY", 980, y + 46, 170, 64, 44, "#FFFFC107", g, "Type On"), "day", row));
            Add(p, Icon($"Day {row} Icon", row, 1160, y + 13, 140, 130));
            Add(p, Bind(Text($"Day {row} Condition", "--", 1320, y + 54, 280, 48, 26, "#FFE3F2FD", g, "Fade Cascade"), "description", row));
            var hi = Bind(Text($"Day {row} High", "--", 1590, y + 30, 180, 56, 44, "#FFFFFFFF", g, "None"), "max", row);
            hi.HorizontalTextAlignment = "Right";
            Add(p, hi);
            var lo = Bind(Text($"Day {row} Low", "--", 1590, y + 88, 180, 40, 28, "#FF90A4AE", g, "None"), "min", row);
            lo.HorizontalTextAlignment = "Right";
            Add(p, lo);
        }
        return p;
    }

    private static CgProject CreateNewsHighlightsDemo()
    {
        const double duration = 8.0;
        var p = new CgProject { Id = new Guid("99999999-ffff-3333-ffff-999999999999"), Name = NewsHighlightsDemoName, Width = 1920, Height = 1080, FrameRate = 50, DurationSeconds = duration, Loop = true, OnAir = false };
        var ds = new CgDataSource
        {
            Id = Guid.NewGuid(),
            Name = "News Highlights Feed",
            SourceType = "LocalJson",
            Source = "news_highlights.sample.json",
            CachedItemsJson = "[{\"headline\":\"PARLIAMENT PASSES LANDMARK INFRASTRUCTURE BILL WITH BIPARTISAN MAJORITY\"},{\"headline\":\"GLOBAL MARKETS SURGE FOLLOWING CENTRAL BANK MONETARY POLICY STATEMENT\"},{\"headline\":\"SPACE EXPLORATION MISSION DISCOVERS SUB-SURFACE ICE RESERVES ON MARS\"},{\"headline\":\"CHAMPIONSHIP FINALS SET FOR RECORD BROADCAST AUDIENCE THIS WEEKEND\"},{\"headline\":\"NATIONAL CLEAN ENERGY TRANSITION TARGETS 80% RENEWABLE OUTPUT BY 2030\"},{\"headline\":\"METRO TRANSIT AUTHORITY LAUNCHES FULLY AUTOMATED ELECTRIC TRAIN LINE\"},{\"headline\":\"INTERNATIONAL CYBERSECURITY SUMMIT RATIFIES CROSS-BORDER DATA ACCORD\"},{\"headline\":\"DEEP SEA RESEARCH EXPEDITION MAPS UNCHARTED PACIFIC HYDROTHERMAL VENTS\"},{\"headline\":\"AGRICULTURAL TECH REVOLUTION BOOSTS ORGANIC CROP YIELDS NATIONWIDE\"},{\"headline\":\"COMMERCIAL AVIATION CONSORTIUM UNVEILS HYDROGEN-POWERED JET PROTOTYPE\"},{\"headline\":\"DOMESTIC TOURISM REACHES FIVE-YEAR PEAK AS REGIONAL FESTIVALS OPEN\"},{\"headline\":\"HIGH-RESOLUTION SPACE TELESCOPE DETECTS EARTH-SIZED EXOPLANET CANDIDATE\"},{\"headline\":\"HEALTH MINISTRY INITIATES EXPANDED PREVENTATIVE PEDIATRIC SCREENINGS\"},{\"headline\":\"INNOVATION HUB OPENS NATIONWIDE ACCELERATOR FOR CLEAN WATER TECH\"},{\"headline\":\"GLOBAL CHIP ARCHITECTURE FORUM ESTABLISHES 2-NANOMETER STANDARDS\"},{\"headline\":\"COASTAL RESILIENCE INITIATIVE COMPLETES EXTENSIVE BARRIER RESTORATION\"},{\"headline\":\"CENTRAL UNIVERSITY RESEARCHERS SYNTHESIZE ULTRA-LIGHT BIO-POLYMER\"},{\"headline\":\"EMERGENCY RESPONSE PROTOCOLS UPGRADED WITH REAL-TIME SATELLITE COMMS\"},{\"headline\":\"NATIONAL DIGITAL ARCHIVE PRESERVES CENTURIES OF ENDANGERED HERITAGE\"},{\"headline\":\"INTERNATIONAL PEACE AND COMMERCE CONCLAVE REACHES STRATEGIC AGREEMENTS\"}]"
        };
        p.DataSources.Add(ds);
        var g = Guid.NewGuid();
        p.Groups.Add(new CgGroup { Id = g, Name = "Highlights Board" });
        Add(p, new CgLayer { Name = "Main Card Base", Type = "Shape", ShapeKind = "Rounded Rectangle", X = 160, Y = 680, Width = 1600, Height = 320, Background = "#EE0E141E", CornerRadius = 14, BorderWidth = 2, BorderColor = "#55FFD700", GroupId = g, AnimationIn = "Slide Up", AnimationOut = "Fade", AnimationInSeconds = 0.5, AnimationOutSeconds = 0.4, StartSeconds = 0, EndSeconds = duration });
        Add(p, new CgLayer { Name = "Header Bar", Type = "Shape", ShapeKind = "Rounded Rectangle", X = 160, Y = 680, Width = 1600, Height = 48, Background = "#FFD700", CornerRadiusTopLeft = 14, CornerRadiusTopRight = 14, GroupId = g, StartSeconds = 0, EndSeconds = duration });
        Add(p, new CgLayer { Name = "Header Title", Type = "Text", Text = "TOP NEWS HIGHLIGHTS  ★  EXCLUSIVE BULLETIN", FontSize = 22, Bold = true, Fill = "#FF000000", X = 190, Y = 690, Width = 1540, Height = 34, GroupId = g, StartSeconds = 0, EndSeconds = duration });

        Add(p, new CgLayer { Name = "Highlight Line 1", Type = "Text", Text = "1 • PARLIAMENT PASSES LANDMARK INFRASTRUCTURE BILL WITH BIPARTISAN MAJORITY", FontSize = 28, Bold = true, Fill = "#FFFFFFFF", X = 200, Y = 744, Width = 1520, Height = 44, DataSourceId = ds.Id, DataField = "line1", DataItemDurationSeconds = duration, GroupId = g, AnimationIn = "Slide Left", AnimationInSeconds = 0.35, AnimationOut = "Fade", AnimationOutSeconds = 0.3, StartSeconds = 0.2, EndSeconds = duration });
        Add(p, new CgLayer { Name = "Highlight Line 2", Type = "Text", Text = "2 • GLOBAL MARKETS SURGE FOLLOWING CENTRAL BANK MONETARY POLICY STATEMENT", FontSize = 28, Bold = true, Fill = "#FFFFE082", X = 200, Y = 804, Width = 1520, Height = 44, DataSourceId = ds.Id, DataField = "line2", DataItemDurationSeconds = duration, GroupId = g, AnimationIn = "Slide Left", AnimationInSeconds = 0.35, AnimationOut = "Fade", AnimationOutSeconds = 0.3, StartSeconds = 0.5, EndSeconds = duration });
        Add(p, new CgLayer { Name = "Highlight Line 3", Type = "Text", Text = "3 • SPACE EXPLORATION MISSION DISCOVERS SUB-SURFACE ICE RESERVES ON MARS", FontSize = 28, Bold = true, Fill = "#FFFFFFFF", X = 200, Y = 864, Width = 1520, Height = 44, DataSourceId = ds.Id, DataField = "line3", DataItemDurationSeconds = duration, GroupId = g, AnimationIn = "Slide Left", AnimationInSeconds = 0.35, AnimationOut = "Fade", AnimationOutSeconds = 0.3, StartSeconds = 0.8, EndSeconds = duration });
        Add(p, new CgLayer { Name = "Highlight Line 4", Type = "Text", Text = "4 • CHAMPIONSHIP FINALS SET FOR RECORD BROADCAST AUDIENCE THIS WEEKEND", FontSize = 28, Bold = true, Fill = "#FFFFE082", X = 200, Y = 924, Width = 1520, Height = 44, DataSourceId = ds.Id, DataField = "line4", DataItemDurationSeconds = duration, GroupId = g, AnimationIn = "Slide Left", AnimationInSeconds = 0.35, AnimationOut = "Fade", AnimationOutSeconds = 0.3, StartSeconds = 1.1, EndSeconds = duration });
        return p;
    }

    private static CgProject CreateCategoryTickerDemo()
    {
        // DurationSeconds covers only the non-ticker layer animations (in+out).
        // The compositor 3-phase lifecycle handles ticker crawl duration dynamically.
        const double duration = 5.0;
        var p = new CgProject { Id = new Guid("aaaa1111-bbbb-2222-cccc-333344445555"), Name = CategoryTickerDemoName, Width = 1920, Height = 1080, FrameRate = 50, DurationSeconds = duration, Loop = true, OnAir = false };
        p.TimelineEvents.Add(new CgTimelineEvent { TimeSeconds = 1.0, Type = "Pause", Label = "HOLD / CRAWL START", ResumeTrigger = "crawlComplete" });
        var ds = new CgDataSource
        {
            Id = Guid.NewGuid(),
            Name = "Category Ticker Feed",
            SourceType = "LocalJson",
            Source = "ticker_categories.sample.json",
            CachedItemsJson = "[{\"category\":\"WORLD NEWS\",\"items\":[\"Global leaders conclude international sustainable energy accord with net zero milestones\",\"United Nations summit passes landmark multilateral digital safety framework\",\"International space agency completes construction of lunar gateway module\",\"European rail consortium launches high-speed zero-emission transnational transit\",\"Global humanitarian forum deploys rapid disaster relief logistics network\"]},{\"category\":\"CRICKET\",\"items\":[\"National team secures decisive 5-wicket victory in international series opener\",\"Star batter smashes fastest championship century in tournament history\",\"Strike bowler claims remarkable six-wicket haul in thrilling death overs\",\"International cricket council announces expanded global tournament venues\",\"Historic super-over triumph crowns champions before sellout stadium crowd\"]},{\"category\":\"BUSINESS\",\"items\":[\"Global equity markets climb to record heights led by AI infrastructure expansion\",\"Central banks maintain benchmark interest rate stability amid cooling inflation\",\"Clean technology investments surpass fossil fuels for third consecutive year\",\"Major automotive manufacturer unveils affordable mass-market electric vehicle lineup\",\"Sovereign semiconductor manufacturing fund allocates multibillion innovation grants\"]},{\"category\":\"WEATHER\",\"items\":[\"Clear blue skies and comfortable seasonal warmth forecasted across metro regions\",\"Mountain highways report optimal transit conditions with dry road surfaces\",\"Coastal districts enjoy gentle offshore breezes and calm maritime conditions\",\"Agricultural heartland receives beneficial seasonal rainfall ahead of planting\",\"Weekend outlook indicates unbroken sunshine with crisp and pleasant evenings\"]}]"
        };
        p.DataSources.Add(ds);
        var g = Guid.NewGuid();
        p.Groups.Add(new CgGroup { Id = g, Name = "Category Ticker Strip" });
        // Full-width bottom ticker base plate
        Add(p, new CgLayer { Name = "Ticker Base Bar", Type = "Shape", X = 0, Y = 990, Width = 1920, Height = 90, Background = "#E6080E18", UseGradient = true, GradientColor2 = "#D902050A", GradientAngle = 90, BorderWidth = 1, BorderColor = "#3300E5FF", GroupId = g, StartSeconds = 0, EndSeconds = duration, DurationSeconds = duration });
        // Glowing cyan accent line along top edge
        Add(p, new CgLayer { Name = "Top Accent Line", Type = "Shape", X = 0, Y = 988, Width = 1920, Height = 3, Background = "#FF00E5FF", GroupId = g, StartSeconds = 0, EndSeconds = duration, DurationSeconds = duration });

        // Fixed-position Category Badge Plate on left (smoothly slides in/out on category switch)
        Add(p, new CgLayer
        {
            Name = "Category Badge Plate",
            Role = "Badge",
            Type = "Shape",
            ShapeKind = "Rectangle",
            X = 0,
            Y = 990,
            Width = 280,
            Height = 90,
            Background = "#FFE63946",
            UseGradient = true,
            GradientColor2 = "#FFB71C1C",
            GradientAngle = 90,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomRight = 8,
            BorderWidth = 0,
            GroupId = g,
            AnimationIn = "Slide Left",
            AnimationInSeconds = 0.35,
            AnimationOut = "Slide Left",
            AnimationOutSeconds = 0.35,
            StartSeconds = 0,
            EndSeconds = duration,
            DurationSeconds = duration
        });

        // Fixed-position Category Text inside the badge plate (smoothly fades when category transitions)
        Add(p, new CgLayer
        {
            Name = "Category Badge Text",
            Role = "Badge",
            Type = "Text",
            Text = "WORLD NEWS",
            X = 10,
            Y = 990,
            Width = 260,
            Height = 90,
            FontSize = 26,
            FontFamily = "Segoe UI",
            Bold = true,
            Fill = "#FFFFFFFF",
            HorizontalTextAlignment = "Center",
            VerticalTextAlignment = "Center",
            ShadowEnabled = true,
            ShadowColor = "#80000000",
            ShadowOffsetX = 1,
            ShadowOffsetY = 1,
            ShadowBlur = 2,
            DataSourceId = ds.Id,
            DataField = "category",
            GroupId = g,
            AnimationIn = "Fade",
            AnimationInSeconds = 0.25,
            AnimationOut = "Fade",
            AnimationOutSeconds = 0.25,
            StartSeconds = 0,
            EndSeconds = duration,
            DurationSeconds = duration
        });

        // Crisp vertical divider line between category plate and headline crawl
        Add(p, new CgLayer
        {
            Name = "Category Divider",
            Type = "Shape",
            X = 280,
            Y = 995,
            Width = 2,
            Height = 80,
            Background = "#4DFFFFFF",
            GroupId = g,
            AnimationIn = "Slide Left",
            AnimationInSeconds = 0.40,
            StartSeconds = 0,
            EndSeconds = duration,
            DurationSeconds = duration
        });

        // Headline Ticker Layer (starts to the right of category plate, scrolls across headline area with news items)
        Add(p, new CgLayer
        {
            Name = "Headline Ticker",
            Type = "Ticker",
            Text = "Global leaders conclude international sustainable energy accord with net zero milestones  ★  United Nations summit passes landmark multilateral digital safety framework  ★  International space agency completes construction of lunar gateway module  ★  European rail consortium launches high-speed zero-emission transnational transit  ★  Global humanitarian forum deploys rapid disaster relief logistics network",
            X = 295,
            Y = 990,
            Width = 1620,
            Height = 90,
            FontSize = 32,
            FontFamily = "Segoe UI",
            Bold = true,
            Fill = "#FFFFFFFF",
            Speed = 160,
            TickerSpeed = 160,
            TickerMode = "Continuous",
            TickerRepeat = true,
            TickerGap = 120,
            TickerSeparator = "  ★  ",
            TickerCategoriesEnabled = true,
            DataSourceId = ds.Id,
            DataField = "items",
            GroupId = g,
            StartSeconds = 0,
            EndSeconds = duration,
            DurationSeconds = duration
        });
        return p;
    }

    private static CgProject CreateCategoryPushTickerDemo()
    {
        // DurationSeconds covers only the non-ticker layer animations (in+out).
        // The compositor 3-phase lifecycle handles ticker push duration dynamically.
        const double duration = 5.0;
        var p = new CgProject { Id = new Guid("aaaa1111-bbbb-2222-cccc-333344445556"), Name = CategoryPushTickerDemoName, Width = 1920, Height = 1080, FrameRate = 50, DurationSeconds = duration, Loop = true, OnAir = false };
        p.TimelineEvents.Add(new CgTimelineEvent { TimeSeconds = 1.0, Type = "Pause", Label = "HOLD / PUSH START", ResumeTrigger = "pushSequenceComplete" });
        var ds = new CgDataSource
        {
            Id = Guid.NewGuid(),
            Name = "Category Push Feed",
            SourceType = "LocalJson",
            Source = "ticker_categories.sample.json",
            CachedItemsJson = "[{\"category\":\"WORLD NEWS\",\"items\":[\"Global leaders conclude international sustainable energy accord with net zero milestones\",\"United Nations summit passes landmark multilateral digital safety framework\",\"International space agency completes construction of lunar gateway module\",\"European rail consortium launches high-speed zero-emission transnational transit\",\"Global humanitarian forum deploys rapid disaster relief logistics network\"]},{\"category\":\"CRICKET\",\"items\":[\"National team secures decisive 5-wicket victory in international series opener\",\"Star batter smashes fastest championship century in tournament history\",\"Strike bowler claims remarkable six-wicket haul in thrilling death overs\",\"International cricket council announces expanded global tournament venues\",\"Historic super-over triumph crowns champions before sellout stadium crowd\"]},{\"category\":\"BUSINESS\",\"items\":[\"Global equity markets climb to record heights led by AI infrastructure expansion\",\"Central banks maintain benchmark interest rate stability amid cooling inflation\",\"Clean technology investments surpass fossil fuels for third consecutive year\",\"Major automotive manufacturer unveils affordable mass-market electric vehicle lineup\",\"Sovereign semiconductor manufacturing fund allocates multibillion innovation grants\"]},{\"category\":\"WEATHER\",\"items\":[\"Clear blue skies and comfortable seasonal warmth forecasted across metro regions\",\"Mountain highways report optimal transit conditions with dry road surfaces\",\"Coastal districts enjoy gentle offshore breezes and calm maritime conditions\",\"Agricultural heartland receives beneficial seasonal rainfall ahead of planting\",\"Weekend outlook indicates unbroken sunshine with crisp and pleasant evenings\"]}]"
        };
        p.DataSources.Add(ds);
        var g = Guid.NewGuid();
        p.Groups.Add(new CgGroup { Id = g, Name = "Category Push Strip" });
        Add(p, new CgLayer { Name = "Ticker Base Bar", Type = "Shape", X = 0, Y = 990, Width = 1920, Height = 90, Background = "#E6080E18", UseGradient = true, GradientColor2 = "#D902050A", GradientAngle = 90, BorderWidth = 1, BorderColor = "#3300E5FF", GroupId = g, StartSeconds = 0, EndSeconds = duration, DurationSeconds = duration });
        Add(p, new CgLayer { Name = "Top Accent Line", Type = "Shape", X = 0, Y = 988, Width = 1920, Height = 3, Background = "#FFFF3D00", GroupId = g, StartSeconds = 0, EndSeconds = duration, DurationSeconds = duration });
        Add(p, new CgLayer
        {
            Name = "Category Badge Plate",
            Type = "Shape",
            ShapeKind = "Rectangle",
            X = 0,
            Y = 990,
            Width = 280,
            Height = 90,
            Background = "#FFFF3D00",
            UseGradient = true,
            GradientColor2 = "#FFC62828",
            GradientAngle = 90,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomRight = 8,
            BorderWidth = 0,
            GroupId = g,
            AnimationIn = "Slide Left",
            AnimationInSeconds = 0.40,
            StartSeconds = 0,
            EndSeconds = duration,
            DurationSeconds = duration
        });
        Add(p, new CgLayer
        {
            Name = "Category Badge Text",
            Type = "Text",
            Text = "WORLD NEWS",
            X = 10,
            Y = 990,
            Width = 260,
            Height = 90,
            FontSize = 26,
            FontFamily = "Segoe UI",
            Bold = true,
            Fill = "#FFFFFFFF",
            HorizontalTextAlignment = "Center",
            VerticalTextAlignment = "Center",
            ShadowEnabled = true,
            ShadowColor = "#80000000",
            ShadowOffsetX = 1,
            ShadowOffsetY = 1,
            ShadowBlur = 2,
            DataSourceId = ds.Id,
            DataField = "category",
            GroupId = g,
            AnimationIn = "Fade",
            AnimationInSeconds = 0.25,
            AnimationOut = "Fade",
            AnimationOutSeconds = 0.25,
            StartSeconds = 0,
            EndSeconds = duration,
            DurationSeconds = duration
        });
        Add(p, new CgLayer
        {
            Name = "Category Divider",
            Type = "Shape",
            X = 280,
            Y = 995,
            Width = 2,
            Height = 80,
            Background = "#4DFFFFFF",
            GroupId = g,
            AnimationIn = "Slide Left",
            AnimationInSeconds = 0.40,
            StartSeconds = 0,
            EndSeconds = duration,
            DurationSeconds = duration
        });
        Add(p, new CgLayer
        {
            Name = "Headline Push Ticker",
            Type = "Ticker",
            Text = "Global leaders conclude international sustainable energy accord with net zero milestones",
            X = 295,
            Y = 990,
            Width = 1620,
            Height = 90,
            FontSize = 30,
            FontFamily = "Segoe UI",
            Bold = true,
            Fill = "#FFFFFFFF",
            Speed = 160,
            TickerSpeed = 160,
            TickerMode = "Push",
            TickerDirection = "Up",
            DataItemDurationSeconds = 4.0,
            TickerGap = 0.5,
            TickerRepeat = true,
            TickerCategoriesEnabled = true,
            DataSourceId = ds.Id,
            DataField = "items",
            GroupId = g,
            StartSeconds = 0,
            EndSeconds = duration,
            DurationSeconds = duration
        });
        return p;
    }

    private static CgProject CreateLogoStationIdentDemo()
    {
        var p = new CgProject { Id = new Guid("77777777-dddd-1111-dddd-777777777777"), Name = LogoStationIdentDemoName, Width = 1920, Height = 1080, FrameRate = 50, DurationSeconds = 10, Loop = true, OnAir = false };
        var g = Guid.NewGuid();
        p.Groups.Add(new CgGroup { Id = g, Name = "Station Logo Bug" });
        Add(p, new CgLayer { Name = "Plate", Type = "Shape", ShapeKind = "Rounded Rectangle", X = 1680, Y = 60, Width = 170, Height = 70, Background = "#800A0F14", CornerRadius = 8, BorderWidth = 1, BorderColor = "#33FFFFFF", GroupId = g });
        Add(p, new CgLayer { Name = "Station Logo Bug", Role = "Logo", Type = "Text", Text = "KASHTRIX", X = 1685, Y = 68, Width = 160, Height = 32, FontSize = 24, Fill = "#FFFFFFFF", Bold = true, HorizontalTextAlignment = "Center", GroupId = g });
        Add(p, new CgLayer { Name = "Channel Sub", Role = "Logo", Type = "Text", Text = "HD PLUTO", X = 1685, Y = 98, Width = 160, Height = 22, FontSize = 14, Fill = "#FF80D8FF", Bold = true, HorizontalTextAlignment = "Center", GroupId = g });
        return p;
    }
    #endregion
    #endregion
}
