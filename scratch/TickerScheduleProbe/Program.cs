using BroadcastPlayout.Services;
using BroadcastPlayout.Models;
using System.IO;

var project = CgUniqueDemoFactory.Create().Single(x => x.Name == CgUniqueDemoFactory.CategoryTickerDemoName);
var ticker = project.Layers.Single(x => x.Type == "Ticker");
var source = project.DataSources.Single(x => x.Id == ticker.DataSourceId);
var rows = CgDataSourceService.ReadCachedRows(source);
var schedule = CgDataSourceService.BuildCategoryFeedSchedule(project, ticker, rows, ticker.Width, ticker.TickerSpeed);

if (rows.Count != 4) throw new Exception($"Expected 4 JSON rows, got {rows.Count}.");
if (schedule.Categories.Count != 4) throw new Exception($"Expected 4 categories, got {schedule.Categories.Count}.");
var firstEnd = schedule.Categories[0].EndTime;
var before = schedule.Evaluate(firstEnd - .001);
var after = schedule.Evaluate(firstEnd + .001);
if (before.ActiveCategory.Category != "WORLD NEWS") throw new Exception("WORLD NEWS ended early.");
if (after.ActiveCategory.Category != "CRICKET") throw new Exception($"Expected CRICKET after WORLD NEWS, got {after.ActiveCategory.Category}.");

Console.WriteLine($"ROWS={rows.Count}; CATEGORIES={schedule.Categories.Count}; TOTAL={schedule.TotalCycleDuration:F2}s");
foreach (var item in schedule.Categories)
    Console.WriteLine($"{item.Category}: IN={item.IntroDuration:F2}s CRAWL={item.CrawlDuration:F2}s OUT={item.OutroDuration:F2}s START={item.StartTime:F2}s END={item.EndTime:F2}s");
Console.WriteLine($"BOUNDARY_OK={before.ActiveCategory.Category}->{after.ActiveCategory.Category}");

var pushProject = CgUniqueDemoFactory.Create().Single(x => x.Name == CgUniqueDemoFactory.CategoryPushTickerDemoName);
var pushTicker = pushProject.Layers.Single(x => x.Type == "Ticker");
var pushSource = pushProject.DataSources.Single(x => x.Id == pushTicker.DataSourceId);
var pushRows = CgDataSourceService.ReadCachedRows(pushSource);
var pushSchedule = CgDataSourceService.BuildCategoryFeedSchedule(pushProject, pushTicker, pushRows, pushTicker.Width, pushTicker.TickerSpeed);
if (pushTicker.TickerMode != "Push") throw new Exception("Push demo ticker mode is not Push.");
if (pushTicker.TickerDirection != "Up") throw new Exception($"Push demo direction was not preserved: {pushTicker.TickerDirection}.");
if (pushProject.TimelinePauseSeconds >= 0) throw new Exception("Push demo must not author a timeline pause.");
if (pushSchedule.Categories.Count != 4) throw new Exception("Push demo did not retain all JSON categories.");
Console.WriteLine($"PUSH_OK=categories:{pushSchedule.Categories.Count}; itemHold:{pushTicker.DataItemDurationSeconds:F2}s; pause:{pushProject.TimelinePauseSeconds:F2}");
var secondPushCategory = pushSchedule.Categories[1];
var pushHoldPoint = CgDataSourceService.ResolveEffectiveHoldPoint(pushProject);
var introMapped = CgDataSourceService.ResolveCategoryDrivenTimelineSeconds(pushProject, secondPushCategory.StartTime + secondPushCategory.IntroDuration * .5);
var contentMapped = CgDataSourceService.ResolveCategoryDrivenTimelineSeconds(pushProject, secondPushCategory.StartTime + secondPushCategory.IntroDuration + .001);
if (!(introMapped < pushHoldPoint && Math.Abs(contentMapped - pushHoldPoint) < .02))
    throw new Exception($"Push category intro was not sequenced before content: intro={introMapped:F3}, content={contentMapped:F3}, hold={pushHoldPoint:F3}.");
Console.WriteLine($"PUSH_CATEGORY_ORDER_OK=introTimeline:{introMapped:F2}<hold:{pushHoldPoint:F2}; contentStartsAtHold:{contentMapped:F2}");

var configurablePush = CgDemoFactory.CloneProject(pushProject);
var configurableTicker = configurablePush.Layers.Single(x => x.Type == "Ticker");
configurableTicker.TickerDirection = "Down";
configurableTicker.TickerGap = configurableTicker.TickerSpeed;
var configurableSource = configurablePush.DataSources.Single(x => x.Id == configurableTicker.DataSourceId);
var configurableRows = CgDataSourceService.ReadCachedRows(configurableSource);
var configurableSchedule = CgDataSourceService.BuildCategoryFeedSchedule(configurablePush, configurableTicker, configurableRows, configurableTicker.Width, configurableTicker.TickerSpeed);
if (configurableSchedule.Categories.Any(x => Math.Abs(x.PushGapDuration) > .001))
    throw new Exception("Push mode inserted blank dead-air between consecutive items.");
Console.WriteLine($"PUSH_DYNAMIC_OK=direction:{configurableTicker.TickerDirection}; gapPx:{configurableTicker.TickerGap:F0}; gapSec:{configurableSchedule.Categories[0].PushGapDuration:F2}");

var timelineOutProject = new CgProject { Width = 640, Height = 100, DurationSeconds = 4, Loop = true };
var timelineOutSource = new CgDataSource
{
    SourceType = "LocalJson",
    Source = "timeline-out-probe.json",
    CachedItemsJson = "[{\"headline\":\"TIMELINE OUT TEST\"}]",
    RefreshSeconds = 0
};
timelineOutProject.DataSources.Add(timelineOutSource);
timelineOutProject.Layers.Add(new CgLayer
{
    Type = "Text", Text = "TIMELINE OUT TEST", DataSourceId = timelineOutSource.Id, DataField = "headline",
    X = 20, Y = 10, Width = 600, Height = 80, FontSize = 48, Bold = true, Fill = "#FFFFFFFF",
    DataItemDurationSeconds = 4, StartSeconds = 1, EndSeconds = 4,
    AnimationIn = "Fade", AnimationInSeconds = .25, AnimationOut = "Fade", AnimationOutSeconds = .5,
    Visible = true
});
using (var timelineOutCompositor = new CgCompositor())
{
    static long AlphaSum(VideoFrameData frame)
    {
        long alpha = 0;
        for (var i = 3; i < frame.Bgra.Length; i += 4) alpha += frame.Bgra[i];
        return alpha;
    }

    var heldAlpha = AlphaSum(timelineOutCompositor.RenderProjectSurface(timelineOutProject, 640, 100, 3.20));
    var outAlpha = AlphaSum(timelineOutCompositor.RenderProjectSurface(timelineOutProject, 640, 100, 3.90));
    var nextCycleBeforeInAlpha = AlphaSum(timelineOutCompositor.RenderProjectSurface(timelineOutProject, 640, 100, 4.05));
    if (!(heldAlpha > 0 && outAlpha > 0 && outAlpha < heldAlpha && nextCycleBeforeInAlpha == 0))
        throw new Exception($"Data-bound timeline OUT failed: hold={heldAlpha}, out={outAlpha}, next-before-in={nextCycleBeforeInAlpha}.");
    Console.WriteLine($"JSON_TIMELINE_OUT_OK=hold:{heldAlpha}>out:{outAlpha}>next-before-in:{nextCycleBeforeInAlpha}");
}

var prime = CgUniqueDemoFactory.Create().Single(x => x.Name == CgUniqueDemoFactory.PrimeBreakingDemoName);
var overlay = prime.Layers.Single(x => x.Name.Contains("Front Burst", StringComparison.OrdinalIgnoreCase));
overlay.SequenceAdvanceDataItem = false;
var initialOverlayFrame = CgDataSourceService.ResolveSequenceFrame(prime, overlay, .10);
var laterOverlayFrame = CgDataSourceService.ResolveSequenceFrame(prime, overlay, prime.DurationSeconds + .10);
if (!string.IsNullOrEmpty(initialOverlayFrame) && !string.IsNullOrEmpty(laterOverlayFrame))
    throw new Exception("Unchecked Cycle On Data Advance re-triggered the overlay sequence.");
Console.WriteLine(string.IsNullOrEmpty(initialOverlayFrame)
    ? "SEQUENCE_GATE_SKIPPED=asset folder unavailable"
    : "SEQUENCE_GATE_OK=unchecked overlay played once and stayed off");

var breakingScreen = CgUniqueDemoFactory.Create().Single(x => x.Name == CgUniqueDemoFactory.PrimeBreakingScreenDemoName);
var breakingSequence = breakingScreen.Layers.Single(x => x.Type == "ImageSequence");
var breakingHeadline = breakingScreen.Layers.Single(x => x.DataSourceId != Guid.Empty);
var breakingRows = CgDataSourceService.ReadCachedRows(breakingScreen.DataSources.Single(x => x.Id == breakingHeadline.DataSourceId));
var trimmedFrameCount = CgDataSourceService.ResolveSequenceFrameCount(breakingSequence);
var expectedFrameCount = (int)Math.Ceiling((breakingSequence.EndSeconds - breakingSequence.StartSeconds) * breakingSequence.SequenceFps);
if (trimmedFrameCount != expectedFrameCount)
    throw new Exception($"Sequence duration trim failed: expected {expectedFrameCount}, got {trimmedFrameCount}.");
var firstCycleFrame = CgDataSourceService.ResolveSequenceFrame(breakingScreen, breakingSequence, 0);
var fullJsonCycle = (breakingSequence.SequenceLoopStartFrame - breakingSequence.SequenceStartFrame) / breakingSequence.SequenceFps
                    + breakingRows.Count * breakingHeadline.DataItemDurationSeconds;
var restartedFrame = CgDataSourceService.ResolveSequenceFrame(breakingScreen, breakingSequence, fullJsonCycle);
if (string.IsNullOrEmpty(firstCycleFrame) || !string.Equals(firstCycleFrame, restartedFrame, StringComparison.OrdinalIgnoreCase))
    throw new Exception("Breaking Screen did not restart from frame 0 after all JSON items completed.");
Console.WriteLine($"SEQUENCE_LOOP_OK=trimFrames:{trimmedFrameCount}; intro:{(breakingSequence.SequenceLoopStartFrame / breakingSequence.SequenceFps):F2}s; jsonItems:{breakingRows.Count}; restartAt:{fullJsonCycle:F2}s");

var directionProject = new CgProject { Width = 640, Height = 100, DurationSeconds = 10, Loop = true };
directionProject.Layers.Add(new CgLayer
{
    Type = "Ticker", Text = "FIRST ITEM | SECOND ITEM", X = 0, Y = 0, Width = 640, Height = 100,
    FontSize = 36, Bold = true, TickerMode = "Push", TickerDirection = "Up", TickerGap = 0,
    DataItemDurationSeconds = 3, StartSeconds = 0, EndSeconds = 10, Visible = true
});
using (var compositor = new CgCompositor())
{
    static double AlphaCentroidY(VideoFrameData frame)
    {
        double weighted = 0, alpha = 0;
        for (var y = 0; y < frame.Height; y++)
        for (var x = 0; x < frame.Width; x++)
        {
            var a = frame.Bgra[y * frame.Stride + x * 4 + 3];
            weighted += y * a;
            alpha += a;
        }
        return alpha > 0 ? weighted / alpha : double.NaN;
    }
    var enteringFrame = compositor.RenderProjectSurface(directionProject, 640, 100, .30);
    var heldFrame = compositor.RenderProjectSurface(directionProject, 640, 100, 1.50);
    var transitionFrame = compositor.RenderProjectSurface(directionProject, 640, 100, 3.70);
    var enteringY = AlphaCentroidY(enteringFrame);
    var heldY = AlphaCentroidY(heldFrame);
    var transitionPixelsChanged = heldFrame.Bgra.Zip(transitionFrame.Bgra, (a, b) => a != b).Count(x => x);
    // At the midpoint of a vertical push the outgoing and incoming items are symmetric,
    // so their combined alpha centroid can equal the held centroid. Pixel change is the
    // correct assertion for that phase; the initial entrance still moves bottom-to-centre.
    if (!double.IsFinite(enteringY) || !double.IsFinite(heldY) || !(enteringY > heldY) || transitionPixelsChanged < 100)
        throw new Exception($"Push Up frame motion failed: enter={enteringY:F2}, hold={heldY:F2}, changed={transitionPixelsChanged}.");
    Console.WriteLine($"PUSH_RENDER_OK=Up enterY:{enteringY:F1}>holdY:{heldY:F1}; transitionChanged:{transitionPixelsChanged}");
}

var ap1Breaking = CgUniqueDemoFactory.Create().Single(x => x.Name == CgUniqueDemoFactory.Ap1hdBreakingDemoName);
var ap1BreakingSequence = ap1Breaking.Layers.Single(x => x.Type == "ImageSequence");
if (!ap1BreakingSequence.Source.Replace('\\', '/').Contains("/ap1hd/", StringComparison.OrdinalIgnoreCase))
    throw new Exception($"AP1 breaking resolved outside the AP1 asset family: {ap1BreakingSequence.Source}");
var staleAp1Breaking = CgDemoFactory.CloneProject(ap1Breaking);
staleAp1Breaking.Layers.Single(x => x.Type == "ImageSequence").Source = Path.Combine("Z:\\stale-build", "space4k", "assets", "BreakingNews");
CgProjectSanitizer.Sanitize(staleAp1Breaking);
var repairedAp1Path = staleAp1Breaking.Layers.Single(x => x.Type == "ImageSequence").Source.Replace('\\', '/');
if (!repairedAp1Path.Contains("/ap1hd/", StringComparison.OrdinalIgnoreCase))
    throw new Exception($"Persisted AP1 breaking path was not repaired: {repairedAp1Path}");
Console.WriteLine($"AP1_SEQUENCE_OK={Path.GetFileName(ap1BreakingSequence.Source)}; family=ap1hd");

var ap1Weather = CgUniqueDemoFactory.Create().Single(x => x.Name == CgUniqueDemoFactory.Ap1hdWeatherDemoName);
var weatherIcons = ap1Weather.Layers.Where(x => x.Type == "WeatherIcon").ToList();
var weatherSource = ap1Weather.DataSources.Single();
var weatherRows = CgDataSourceService.ReadCachedRows(weatherSource);
if (weatherIcons.Count != 8 || weatherRows.Count != 8)
    throw new Exception($"AP1 weather icon/card mismatch: icons={weatherIcons.Count}, rows={weatherRows.Count}.");
var resolvedWeatherCodes = weatherIcons.Select(x => CgDataSourceService.ResolveLayerText(ap1Weather, x, 0)).ToList();
if (resolvedWeatherCodes.Any(string.IsNullOrWhiteSpace) || resolvedWeatherCodes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 8)
    throw new Exception("AP1 weather did not bind a distinct animated icon to every city card.");
Console.WriteLine($"AP1_WEATHER_OK=icons:{weatherIcons.Count}; codes:{string.Join(',', resolvedWeatherCodes)}");

var legacyWeather = CgDemoFactory.CloneProject(ap1Weather);
legacyWeather.Layers.RemoveAll(x => x.Type == "WeatherIcon");
legacyWeather.DataSources.Single().CachedItemsJson = "[{\"cityName\":\"Kathmandu\",\"temp\":24}]";
CgProjectSanitizer.Sanitize(legacyWeather);
if (legacyWeather.Layers.Count(x => x.Type == "WeatherIcon") != 8 ||
    !legacyWeather.DataSources.Single().CachedItemsJson.Contains("\"icon\"", StringComparison.OrdinalIgnoreCase))
    throw new Exception("Legacy AP1 weather demo migration did not restore all animated icons.");
Console.WriteLine("AP1_WEATHER_MIGRATION_OK=8 icons restored to persisted demo");

if (args.Contains("--write-demo", StringComparer.OrdinalIgnoreCase))
{
    foreach (var demo in new[] { pushProject, breakingScreen })
    {
        foreach (var folder in new[] { "cg-demo", "demos" })
        {
            Directory.CreateDirectory(folder);
            var path = Path.GetFullPath(Path.Combine(folder, demo.Name + ".kcg"));
            KashtrixCgFileService.SaveComposition(path, demo);
            var loaded = KashtrixCgFileService.LoadComposition(path);
            if (ReferenceEquals(demo, pushProject))
            {
                var loadedTicker = loaded.Layers.Single(x => x.Type == "Ticker");
                if (loadedTicker.TickerMode != "Push" || loadedTicker.TickerDirection != "Up" || loadedTicker.TickerGap != pushTicker.TickerGap)
                    throw new Exception("Saved Push demo did not retain editable ticker properties.");
            }
            Console.WriteLine($"DEMO_SAVED={path}");
        }
    }
}
