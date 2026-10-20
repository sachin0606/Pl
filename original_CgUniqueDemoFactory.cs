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

    public static List<CgProject> Create()
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
}
