using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using BroadcastPlayout.Models;
using BroadcastPlayout.Ffmpeg;

namespace BroadcastPlayout.Services;

/// <summary>
/// Windows software compositor for broadcast-safe text, shapes, stills, image sequences,
/// FFmpeg video layers, tickers, rolls, date/time, timecode and clocks. The result is a BGRA
/// frame used by Program, NDI and DeckLink. Native layers are software-rendered while HTML/URL/.kashgfx
/// sources are rendered by Chromium off-screen and alpha-composited without timeline authoring.
/// </summary>
public sealed class CgCompositor : IDisposable
{
    private readonly object _imageSync = new();
    private readonly Dictionary<string, Image> _images = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, VideoLayerState> _videoLayers = [];

    public VideoFrameData Composite(VideoFrameData source, CgProject? project, double timelineSeconds, bool alphaSurface = false)
        => CompositeInternal(source, project, timelineSeconds, alphaSurface, requireOnAir: true);

    /// <summary>
    /// Draws the project directly onto <paramref name="target"/>, which the caller must own
    /// exclusively. Returns the target, or a new frame when a squeeze layer needs the untouched
    /// source while drawing.
    /// </summary>
    public VideoFrameData CompositeInPlace(VideoFrameData target, CgProject? project, double timelineSeconds, bool alphaSurface = false)
        => CompositeInternal(target, project, timelineSeconds, alphaSurface, requireOnAir: true, inPlace: true);

    /// <summary>Renders a nested/precomposition project to a transparent BGRA surface regardless of its OnAir flag.</summary>
    public VideoFrameData RenderProjectSurface(CgProject project, int width, int height, double timelineSeconds)
    {
        width = Math.Max(2, width);
        height = Math.Max(2, height);
        var stride = checked(width * 4);
        var (fpsN, fpsD) = ResolveProjectFrameRate(project.FrameRate);
        var transparent = new VideoFrameData(new byte[checked(stride * height)], width, height, stride, timelineSeconds, fpsN, fpsD);
        return CompositeInternal(transparent, project, timelineSeconds, alphaSurface: true, requireOnAir: false);
    }

    private VideoFrameData CompositeInternal(VideoFrameData source, CgProject? project, double timelineSeconds, bool alphaSurface, bool requireOnAir, bool inPlace = false)
    {
        SweepIdlePreviews();
        if (project is null || (requireOnAir && !project.OnAir) || (project.Layers.Count == 0 && (project.HtmlSources?.Count ?? 0) == 0)) return source;

        double t;
        var maxOutDuration = project.Layers.Where(x => x.Visible).Select(x => x.AnimationOutSeconds).DefaultIfEmpty(0.5).Max();
        var holdPoint = Math.Max(0.1, project.DurationSeconds - maxOutDuration - 0.05);

        var dynamicTickerDuration = CgDataSourceService.ResolveEffectiveTickerCycleDuration(project);
        var hasDynamicTicker = dynamicTickerDuration > 0.5;
        var pausePoint = hasDynamicTicker ? Math.Min(holdPoint, CgDataSourceService.ResolveEffectiveHoldPoint(project)) : holdPoint;
        var categoryIntroLead = hasDynamicTicker ? Math.Min(CgDataSourceService.ResolveEffectiveCategoryIntroDuration(project), pausePoint) : 0;
        var tickerHoldDuration = hasDynamicTicker ? Math.Max(0.05, dynamicTickerDuration - categoryIntroLead) : 0;
        double tickerTimelineSeconds = timelineSeconds;

        if (project.IsStopping && project.StopRequestedTimelineSeconds >= 0)
        {
            var stopElapsed = Math.Max(0, timelineSeconds - project.StopRequestedTimelineSeconds);
            var outTime = (project.DurationSeconds - maxOutDuration) + stopElapsed;
            if (outTime >= project.DurationSeconds)
            {
                project.OnAir = false;
                return source;
            }
            t = Math.Clamp(outTime, project.DurationSeconds - maxOutDuration, project.DurationSeconds);
        }
        else if (hasDynamicTicker)
        {
            // Broadcast graphic lifecycle with dynamic ticker:
            // Phase 1 (0 .. pausePoint): In-animation plays
            // Phase 2 (pausePoint .. pausePoint + dynamicTickerDuration): Timeline PAUSES/HOLDS at pausePoint while all categories/items play completely from start to out of body
            // Phase 3 (pausePoint + dynamicTickerDuration .. end): Timeline RESUMES from pausePoint to play Out-animation
            var totalGraphicDuration = pausePoint + tickerHoldDuration + maxOutDuration;

            if (project.Loop || project.DurationSeconds <= 0)
            {
                var cycleTime = timelineSeconds % totalGraphicDuration;
                if (cycleTime < pausePoint)
                {
                    t = cycleTime;
                    tickerTimelineSeconds = cycleTime;
                }
                else if (cycleTime < pausePoint + tickerHoldDuration)
                {
                    t = pausePoint;
                    tickerTimelineSeconds = cycleTime;
                }
                else
                {
                    var outElapsed = cycleTime - (pausePoint + tickerHoldDuration);
                    t = Math.Min(project.DurationSeconds, (project.DurationSeconds - maxOutDuration) + outElapsed);
                    tickerTimelineSeconds = cycleTime;
                }
            }
            else
            {
                if (timelineSeconds < pausePoint)
                {
                    t = timelineSeconds;
                    tickerTimelineSeconds = timelineSeconds;
                }
                else if (timelineSeconds < pausePoint + tickerHoldDuration)
                {
                    t = pausePoint;
                    tickerTimelineSeconds = timelineSeconds;
                }
                else
                {
                    var outElapsed = timelineSeconds - (pausePoint + tickerHoldDuration);
                    if (outElapsed >= maxOutDuration)
                    {
                        project.OnAir = false;
                        return source;
                    }
                    t = Math.Min(project.DurationSeconds, (project.DurationSeconds - maxOutDuration) + outElapsed);
                    tickerTimelineSeconds = timelineSeconds;
                }
            }
        }
        else if (project.Loop || project.DurationSeconds <= 0)
        {
            t = project.DurationSeconds > 0 ? timelineSeconds % project.DurationSeconds : timelineSeconds;
        }
        else
        {
            // Non-looping graphic: animate in, then hold on-air until STOP command is issued.
            // If the project has ticker layers bound to data sources but data isn't
            // loaded yet (dynamicTickerDuration was 0), use a compact holdPoint instead
            // of one derived from a huge DurationSeconds (e.g. 3600) so that the
            // graphic doesn't appear permanently frozen.
            var effectiveHold = holdPoint;
            if (!hasDynamicTicker && project.DurationSeconds > 120)
            {
                var hasTickerDs = project.Layers.Any(l => l.Visible &&
                    string.Equals(l.Type, "Ticker", StringComparison.OrdinalIgnoreCase) &&
                    l.DataSourceId != Guid.Empty);
                if (hasTickerDs)
                {
                    var maxIn = project.Layers.Where(l => l.Visible)
                        .Select(l => l.StartSeconds + Math.Max(0.3, l.AnimationInSeconds)).DefaultIfEmpty(1.0).Max();
                    effectiveHold = Math.Max(0.5, maxIn);
                }
            }
            t = Math.Min(timelineSeconds, effectiveHold);
        }

        var sx = source.Width / (double)Math.Max(1, project.Width);
        var sy = source.Height / (double)Math.Max(1, project.Height);
        var squeeze = project.Layers.FirstOrDefault(x => x.Visible && x.SqueezeProgram && t >= x.StartSeconds && t <= x.EndSeconds);
        var bytes = squeeze is not null ? new byte[source.Bgra.Length]
            : inPlace ? source.Bgra
            : (byte[])source.Bgra.Clone();
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            using var bitmap = new Bitmap(source.Width, source.Height, source.Stride, PixelFormat.Format32bppArgb, handle.AddrOfPinnedObject());
            using var g = Graphics.FromImage(bitmap);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            if (alphaSurface) g.CompositingMode = CompositingMode.SourceOver;

            if (squeeze is not null)
                DrawSqueezedProgram(g, source, squeeze, t, sx, sy);

            var hasAnyTicker = project.Layers.Any(l => string.Equals(l.Type, "Ticker", StringComparison.OrdinalIgnoreCase));
            var tickerGroupId = project.Layers.FirstOrDefault(l => string.Equals(l.Type, "Ticker", StringComparison.OrdinalIgnoreCase))?.GroupId ?? Guid.Empty;

            bool IsTickerStripLayer(CgLayer l)
            {
                if (string.Equals(l.Type, "Ticker", StringComparison.OrdinalIgnoreCase)) return true;
                if (tickerGroupId != Guid.Empty && l.GroupId == tickerGroupId) return true;
                if (l.DataSourceId != Guid.Empty) return true;
                if (l.SequenceLoop && string.Equals(l.Type, "ImageSequence", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(l.Type, "Shape", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(l.Role, "Shape", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(l.Role, "Badge", StringComparison.OrdinalIgnoreCase)) return true;
                var n = l.Name ?? "";
                return n.Contains("Ticker", StringComparison.OrdinalIgnoreCase) ||
                       n.Contains("Badge", StringComparison.OrdinalIgnoreCase) ||
                       n.Contains("Category", StringComparison.OrdinalIgnoreCase) ||
                       n.Contains("Strip", StringComparison.OrdinalIgnoreCase) ||
                       n.Contains("Plate", StringComparison.OrdinalIgnoreCase) ||
                       n.Contains("Accent", StringComparison.OrdinalIgnoreCase) ||
                       n.Contains("Divider", StringComparison.OrdinalIgnoreCase) ||
                       n.Contains("Rectangle", StringComparison.OrdinalIgnoreCase) ||
                       n.Contains("Box", StringComparison.OrdinalIgnoreCase) ||
                       n.Contains("Background", StringComparison.OrdinalIgnoreCase) ||
                       n.Contains("Bar", StringComparison.OrdinalIgnoreCase);
            }

            foreach (var layer in project.Layers.Where(x => x.Visible && (
                (hasAnyTicker && IsTickerStripLayer(x))
                    ? (timelineSeconds >= x.StartSeconds)
                    : (hasDynamicTicker && (string.Equals(x.Type, "Ticker", StringComparison.OrdinalIgnoreCase) || x.DataSourceId != Guid.Empty || x.StartSeconds <= pausePoint || x.EndSeconds >= holdPoint || (tickerGroupId != Guid.Empty && x.GroupId == tickerGroupId)))
                        ? (t >= x.StartSeconds)
                        : x.SequenceAdvanceDataItem
                            ? (timelineSeconds >= x.StartSeconds && t >= x.StartSeconds && t <= x.EndSeconds)
                            : ((x.SequenceLoop && string.Equals(x.Type, "ImageSequence", StringComparison.OrdinalIgnoreCase)) || string.Equals(x.Type, "Ticker", StringComparison.OrdinalIgnoreCase))
                                ? (timelineSeconds >= x.StartSeconds)
                                : (t >= x.StartSeconds && t <= x.EndSeconds)
            )))
            {
                var isCategoryLayer = hasAnyTicker && IsTickerStripLayer(layer);
                var layerContinuousTime = hasDynamicTicker && isCategoryLayer ? tickerTimelineSeconds : timelineSeconds;
                DrawLayer(g, bytes, source.Width, source.Height, source.Stride, project, layer, t, sx, sy, layerContinuousTime);
            }

            foreach (var html in (project.HtmlSources ?? []).Where(x => x.Visible))
                DrawChromiumHtml(g, html, sx, sy);
        }
        finally
        {
            handle.Free();
        }

        if (ReferenceEquals(bytes, source.Bgra)) return source;
        return new VideoFrameData(bytes, source.Width, source.Height, source.Stride, source.PtsSeconds, source.FrameRateNumerator, source.FrameRateDenominator);
    }

    private static (int N, int D) ResolveProjectFrameRate(double fps)
    {
        if (Math.Abs(fps - 23.976) < .002) return (24000, 1001);
        if (Math.Abs(fps - 29.97) < .002) return (30000, 1001);
        if (Math.Abs(fps - 59.94) < .002) return (60000, 1001);
        return (Math.Max(1, (int)Math.Round(double.IsFinite(fps) && fps > 0 ? fps : 25)), 1);
    }

    /// <summary>
    /// Switcher-style transition between two complete CG compositions. Both projects are first
    /// rendered to transparent BGRA surfaces and then combined over the unchanged program frame.
    /// This keeps the controller transition independent from each project's authored IN/OUT motion.
    /// </summary>
    public VideoFrameData CompositeTransition(
        VideoFrameData source,
        CgProject? outgoing,
        CgProject incoming,
        double outgoingTimelineSeconds,
        double incomingTimelineSeconds,
        double progress,
        string? transition,
        bool alphaSurface = false)
    {
        var mode = (transition ?? "None").Trim();
        var p = SmoothStep(Math.Clamp(progress, 0, 1));
        if (mode.Equals("None", StringComparison.OrdinalIgnoreCase) || p >= .9999)
            return Composite(source, incoming, incomingTimelineSeconds, alphaSurface);

        var transparent = new VideoFrameData(new byte[source.Bgra.Length], source.Width, source.Height, source.Stride, source.PtsSeconds, source.FrameRateNumerator, source.FrameRateDenominator);
        var outFrame = outgoing is null ? null : Composite(transparent, outgoing, outgoingTimelineSeconds, alphaSurface: true);
        var inFrame = Composite(transparent, incoming, incomingTimelineSeconds, alphaSurface: true);
        var bytes = (byte[])source.Bgra.Clone();
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            using var bitmap = new Bitmap(source.Width, source.Height, source.Stride, PixelFormat.Format32bppArgb, handle.AddrOfPinnedObject());
            using var g = Graphics.FromImage(bitmap);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingMode = CompositingMode.SourceOver;

            if (mode.Equals("Fade", StringComparison.OrdinalIgnoreCase))
            {
                if (outFrame is not null) DrawVideoFrame(g, outFrame, new RectangleF(0, 0, source.Width, source.Height), 1 - p);
                DrawVideoFrame(g, inFrame, new RectangleF(0, 0, source.Width, source.Height), p);
            }
            else if (mode.StartsWith("Wipe", StringComparison.OrdinalIgnoreCase))
            {
                if (outFrame is not null) DrawVideoFrame(g, outFrame, new RectangleF(0, 0, source.Width, source.Height), 1);
                var reveal = Math.Max(0, Math.Min(source.Width, (int)Math.Round(source.Width * p)));
                var fromRight = mode.Contains("Right", StringComparison.OrdinalIgnoreCase);
                var clip = fromRight
                    ? new Rectangle(source.Width - reveal, 0, reveal, source.Height)
                    : new Rectangle(0, 0, reveal, source.Height);
                var saved = g.Save();
                g.SetClip(clip);
                DrawVideoFrame(g, inFrame, new RectangleF(0, 0, source.Width, source.Height), 1);
                g.Restore(saved);
            }
            else if (mode.Equals("Zoom", StringComparison.OrdinalIgnoreCase))
            {
                if (outFrame is not null)
                {
                    var outScale = 1.0 - .12 * p;
                    var outRect = CenterScaleRect(source.Width, source.Height, outScale);
                    DrawVideoFrame(g, outFrame, outRect, 1 - p);
                }
                var inScale = .82 + .18 * p;
                var inRect = CenterScaleRect(source.Width, source.Height, inScale);
                DrawVideoFrame(g, inFrame, inRect, Math.Min(1, .15 + .85 * p));
            }
            else
            {
                DrawVideoFrame(g, inFrame, new RectangleF(0, 0, source.Width, source.Height), p);
            }
        }
        finally
        {
            handle.Free();
        }

        return new VideoFrameData(bytes, source.Width, source.Height, source.Stride, source.PtsSeconds, source.FrameRateNumerator, source.FrameRateDenominator);
    }

    private static RectangleF CenterScaleRect(int width, int height, double scale)
    {
        var w = Math.Max(2, width * scale);
        var h = Math.Max(2, height * scale);
        return new RectangleF((float)((width - w) / 2), (float)((height - h) / 2), (float)w, (float)h);
    }

    private static void DrawVideoFrame(Graphics g, VideoFrameData frame, RectangleF destination, double opacity)
    {
        if (opacity <= .0001) return;
        var frameHandle = GCHandle.Alloc(frame.Bgra, GCHandleType.Pinned);
        try
        {
            using var bitmap = new Bitmap(frame.Width, frame.Height, frame.Stride, PixelFormat.Format32bppArgb, frameHandle.AddrOfPinnedObject());
            var matrix = new ColorMatrix { Matrix33 = (float)Math.Clamp(opacity, 0, 1) };
            using var attrs = new ImageAttributes();
            attrs.SetColorMatrix(matrix);
            g.DrawImage(bitmap, Rectangle.Round(destination), 0, 0, bitmap.Width, bitmap.Height, GraphicsUnit.Pixel, attrs);
        }
        finally { frameHandle.Free(); }
    }


    private static void DrawSqueezedProgram(Graphics g, VideoFrameData source, CgLayer layer, double t, double sx, double sy)
    {
        g.Clear(Color.Black);
        var input = Math.Clamp((t - layer.StartSeconds) / Math.Max(.05, layer.SqueezeInSeconds), 0, 1);
        var output = Math.Clamp((layer.EndSeconds - t) / Math.Max(.05, layer.SqueezeOutSeconds), 0, 1);
        var p = SmoothStep(Math.Min(input, output));
        var ty = layer.SqueezeY * sy;
        var tw = Math.Max(2, layer.SqueezeWidth * sx);
        var th = Math.Max(2, layer.SqueezeHeight * sy);
        var tx = layer.SqueezeX * sx;
        var mode = (layer.SqueezeHorizontalMode ?? "Both").Trim().ToUpperInvariant();
        if (tx <= 0)
        {
            tx = mode switch
            {
                "LEFT" => Math.Max(0, source.Width - tw),
                "RIGHT" => 0,
                _ => tx
            };
        }
        var x = Lerp(0, tx, p);
        var y = Lerp(0, ty, p);
        var w = Lerp(source.Width, tw, p);
        var h = Lerp(source.Height, th, p);

        var sourceHandle = GCHandle.Alloc(source.Bgra, GCHandleType.Pinned);
        try
        {
            using var srcBitmap = new Bitmap(source.Width, source.Height, source.Stride, PixelFormat.Format32bppArgb, sourceHandle.AddrOfPinnedObject());
            g.DrawImage(srcBitmap, Rectangle.Round(new RectangleF((float)x, (float)y, (float)w, (float)h)), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel);
        }
        finally { sourceHandle.Free(); }
    }

    private static double SmoothStep(double p) => p * p * (3 - 2 * p);
    private static double Lerp(double a, double b, double p) => a + (b - a) * p;

    private static void DrawChromiumHtml(Graphics g, CgHtmlSource source, double sx, double sy)
    {
        using var bitmap = ChromiumCgRenderer.Shared.GetLatest(source);
        if (bitmap is null) return;
        var rect = new Rectangle(
            (int)Math.Round(source.X * sx),
            (int)Math.Round(source.Y * sy),
            Math.Max(2, (int)Math.Round(source.Width * sx)),
            Math.Max(2, (int)Math.Round(source.Height * sy)));
        var matrix = new ColorMatrix { Matrix33 = (float)Math.Clamp(source.Opacity, 0, 1) };
        using var attrs = new ImageAttributes();
        attrs.SetColorMatrix(matrix);
        g.DrawImage(bitmap, rect, 0, 0, bitmap.Width, bitmap.Height, GraphicsUnit.Pixel, attrs);
    }

    private void DrawLayer(Graphics g, byte[] targetBytes, int targetWidth, int targetHeight, int targetStride, CgProject project, CgLayer layer, double t, double sx, double sy, double continuousSeconds = 0)
    {
        var visualLayer = CgAnimationEngine.CreateVisualLayer(layer, t);
        if (!visualLayer.MaskEnabled)
        {
            DrawLayerCore(g, targetBytes, targetWidth, targetHeight, targetStride, project, layer, t, sx, sy, forceNormalBlend: false, continuousSeconds);
            return;
        }

        // Masking is intentionally evaluated AFTER the element's authored motion. The layer is
        // rendered onto a full-frame transparent surface first; then a fixed project/canvas mask
        // clips the final pixels. A Slide Right/Push/Zoom can therefore start outside the mask,
        // while visibility begins only when the animated pixels actually enter the mask region.
        using var overlay = new Bitmap(targetWidth, targetHeight, PixelFormat.Format32bppArgb);
        using (var og = Graphics.FromImage(overlay))
        {
            og.Clear(Color.Transparent);
            og.SmoothingMode = g.SmoothingMode;
            og.InterpolationMode = g.InterpolationMode;
            og.PixelOffsetMode = g.PixelOffsetMode;
            og.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            og.CompositingMode = CompositingMode.SourceOver;
            DrawLayerCore(og, Array.Empty<byte>(), targetWidth, targetHeight, targetStride, project, layer, t, sx, sy, forceNormalBlend: true, continuousSeconds);
            og.Flush();
        }

        ApplyLayerMask(overlay, visualLayer, sx, sy);
        g.Flush();
        if (UsesCustomBlend(visualLayer))
            BlendBitmapIntoTarget(targetBytes, targetWidth, targetHeight, targetStride, overlay, visualLayer.BlendMode);
        else
            g.DrawImageUnscaled(overlay, 0, 0);
    }

    private void DrawLayerCore(Graphics g, byte[] targetBytes, int targetWidth, int targetHeight, int targetStride, CgProject project, CgLayer layer, double t, double sx, double sy, bool forceNormalBlend, double continuousSeconds = 0)
    {
        if (continuousSeconds <= 0) continuousSeconds = t;
        var motionTime = CgDataSourceService.ResolveCategoryLayerTimelineSeconds(project, layer, continuousSeconds, t);
        var motion = CgAnimationEngine.EvaluateLayer(project, layer, motionTime);
        var visualLayer = CgAnimationEngine.CreateVisualLayer(layer, motionTime);
        var isTextType = string.Equals(visualLayer.Type, "Text", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(visualLayer.Type, "Ticker", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(visualLayer.Type, "Roll", StringComparison.OrdinalIgnoreCase);
        var isOverlayCycle = CgDataSourceService.IsOverlayActiveForItem(project, layer, continuousSeconds);
        if (isTextType && isOverlayCycle)
        {
            if (visualLayer.TextAnimationDelaySeconds <= 0.001 && visualLayer.StartSeconds <= 0.001)
            {
                visualLayer.TextAnimationDelaySeconds = CgDataSourceService.ResolveEffectiveOverlayDuration(project, layer);
            }
        }

        var x = motion.X * sx;
        var y = motion.Y * sy;
        var w = Math.Max(2, motion.Width * sx);
        var h = Math.Max(2, motion.Height * sy);
        var opacity = Math.Clamp(motion.Opacity * AnimationOpacity(project, visualLayer, t, continuousSeconds), 0, 1);
        if (opacity <= .0001) return;
        ApplyMotion(project, visualLayer, motionTime, ref x, ref y, ref w, ref h, sx, sy, continuousSeconds);
        var layerType = (visualLayer.Type ?? "Text").Trim().ToUpperInvariant();
        CgAnimationEngine.TextVisualState? activeTextVisual = null;
        var textEvalTime = visualLayer.DataSourceId != Guid.Empty ? continuousSeconds : t;
        var categoryDriven = CgDataSourceService.TryGetActiveCategoryTiming(project, visualLayer, continuousSeconds, out _, out _, out _, out _, out _, out _);
        // Ticker/category motion is controlled by the category schedule. Applying the
        // generic per-item text OUT fade as well made both badge and news blink.
        if (layerType is not "TICKER" && !categoryDriven &&
            layerType is ("TEXT" or "ROLL" or "DIGITALCLOCK" or "DATETIME" or "NEPALIDATE" or "NEPALICLOCK" or "TIMECODE" or "TIMER" or "COUNTDOWN" or "BACKIN"))
        {
            var textVisual = CgAnimationEngine.EvaluateTextVisual(visualLayer, textEvalTime);
            activeTextVisual = textVisual;
            opacity *= textVisual.Opacity;
            x += textVisual.OffsetX * sx; y += textVisual.OffsetY * sy;
            if (opacity <= .0001) return;
        }
        var rect = new RectangleF((float)x, (float)y, (float)w, (float)h);
        var resolvedText = CgAnimationEngine.ResolveAnimatedText(visualLayer, CgDataSourceService.ResolveLayerText(project!, visualLayer, continuousSeconds), textEvalTime);
        resolvedText = CgPlaybackRuntime.SubstituteTokens(resolvedText, continuousSeconds, visualLayer);

        var anchorX = rect.Left + rect.Width * (float)Math.Clamp(motion.AnchorX, 0, 1);
        var anchorY = rect.Top + rect.Height * (float)Math.Clamp(motion.AnchorY, 0, 1);
        if (Math.Abs(motion.Rotation) > .001)
        {
            g.TranslateTransform(anchorX, anchorY);
            g.RotateTransform((float)motion.Rotation);
            g.TranslateTransform(-anchorX, -anchorY);
        }
        var effectiveSkewX = Math.Clamp(motion.SkewX + Math.Tan(motion.RotationY * Math.PI / 180.0) * 35.0, -75, 75);
        var effectiveSkewY = Math.Clamp(motion.SkewY - Math.Tan(motion.RotationX * Math.PI / 180.0) * 35.0, -75, 75);
        if (Math.Abs(effectiveSkewX) > .001 || Math.Abs(effectiveSkewY) > .001)
        {
            var shx = (float)Math.Tan(effectiveSkewX * Math.PI / 180.0);
            var shy = (float)Math.Tan(effectiveSkewY * Math.PI / 180.0);
            using var matrix = new Matrix();
            matrix.Translate(anchorX, anchorY);
            matrix.Shear(shx, shy);
            matrix.Translate(-anchorX, -anchorY);
            g.MultiplyTransform(matrix, MatrixOrder.Prepend);
        }

        var type = layerType;
        switch (type)
        {
            case "SHAPE": DrawShape(g, rect, visualLayer, opacity); break;
            case "ELLIPSE": DrawEllipse(g, rect, visualLayer, opacity); break;
            case "LINE": DrawLine(g, rect, visualLayer, opacity); break;
            case "IMAGE":
                if (!forceNormalBlend && UsesCustomBlend(visualLayer)) DrawBlendedMedia(g, targetBytes, targetWidth, targetHeight, targetStride, visualLayer.BlendMode, og => DrawImage(og, rect, visualLayer.Source, opacity));
                else DrawImage(g, rect, visualLayer.Source, opacity);
                break;
            case "IMAGESEQUENCE":
                var sequenceFrame = CgDataSourceService.ResolveSequenceFrame(project!, visualLayer, continuousSeconds);
                if (!forceNormalBlend && UsesCustomBlend(visualLayer)) DrawBlendedMedia(g, targetBytes, targetWidth, targetHeight, targetStride, visualLayer.BlendMode, og => DrawImage(og, rect, sequenceFrame, opacity));
                else DrawImage(g, rect, sequenceFrame, opacity);
                break;
            case "TICKER": DrawTicker(g, rect, visualLayer, continuousSeconds, opacity, resolvedText, project); break;
            case "ROLL": DrawRoll(g, rect, visualLayer, continuousSeconds, opacity, resolvedText); break;
            case "DIGITALCLOCK":
                var clockNow = CgClockTime.Now(visualLayer);
                if (!string.IsNullOrWhiteSpace(resolvedText) && (resolvedText.Contains("BS") || resolvedText.Contains("२०") || resolvedText.Contains("Nepali") || resolvedText.Contains("YYYY")))
                    DrawText(g, rect, NepaliCalendarService.ResolveBroadcastFormat(resolvedText, clockNow), visualLayer, opacity, activeTextVisual, sy);
                else
                    DrawText(g, rect, clockNow.ToString("HH:mm:ss"), visualLayer, opacity, activeTextVisual, sy);
                break;
            case "DATETIME":
                var dateNow = CgClockTime.Now(visualLayer);
                if (string.Equals(visualLayer.CalendarSystem, "BS", StringComparison.OrdinalIgnoreCase))
                {
                    var bsFormat = string.IsNullOrWhiteSpace(visualLayer.DataFormat) || visualLayer.DataFormat == "{0}" ? "dddd, DD MMMM YYYY  HH:mm:ss" : visualLayer.DataFormat;
                    DrawText(g, rect, NepaliCalendarService.ResolveBroadcastFormat(bsFormat, dateNow), visualLayer, opacity, activeTextVisual, sy);
                    break;
                }
                if (string.Equals(visualLayer.CalendarSystem, "AD", StringComparison.OrdinalIgnoreCase))
                {
                    var adFormat = string.IsNullOrWhiteSpace(visualLayer.DataFormat) || visualLayer.DataFormat == "{0}" ? "dd MMM yyyy  HH:mm:ss" : visualLayer.DataFormat;
                    DrawText(g, rect, FormatDateTime(adFormat, dateNow), visualLayer, opacity, activeTextVisual, sy);
                    break;
                }
                if (!string.IsNullOrWhiteSpace(resolvedText) && (resolvedText.Contains("BS") || resolvedText.Contains("२०") || resolvedText.Contains("Nepali") || resolvedText.Contains("YYYY")))
                    DrawText(g, rect, NepaliCalendarService.ResolveBroadcastFormat(resolvedText, dateNow), visualLayer, opacity, activeTextVisual, sy);
                else
                    DrawText(g, rect, FormatDateTime(resolvedText, dateNow), visualLayer, opacity, activeTextVisual, sy);
                break;
            case "NEPALIDATE":
            case "NEPALICLOCK":
                DrawText(g, rect, NepaliCalendarService.ResolveBroadcastFormat(resolvedText, CgClockTime.Now(visualLayer)), visualLayer, opacity, activeTextVisual, sy);
                break;
            case "TIMECODE": DrawText(g, rect, FormatTimelineTimecode(continuousSeconds), visualLayer, opacity, activeTextVisual, sy); break;
            case "TIMER":
            case "COUNTDOWN":
            case "BACKIN":
                DrawTimer(g, rect, visualLayer, continuousSeconds, opacity, activeTextVisual);
                break;
            case "ANALOGCLOCK": DrawAnalogClock(g, rect, visualLayer, opacity); break;
            case "WEATHERICON": DrawWeatherIcon(g, rect, resolvedText, visualLayer, t, opacity); break;
            case "HTML": DrawText(g, rect, RenderHtmlFallback(visualLayer), visualLayer, opacity * .90, activeTextVisual, sy); break;
            case "PRECOMP": DrawPrecomposition(g, rect, visualLayer, Math.Max(0, t - layer.StartSeconds), opacity); break;
            case "VIDEO":
                if (!forceNormalBlend && UsesCustomBlend(visualLayer)) DrawBlendedMedia(g, targetBytes, targetWidth, targetHeight, targetStride, visualLayer.BlendMode, og => DrawVideo(og, rect, visualLayer, t - visualLayer.StartSeconds, opacity));
                else DrawVideo(g, rect, visualLayer, t - visualLayer.StartSeconds, opacity);
                break;
            // Now/Next package elements (data from NowNextRuntime).
            case "PROGRESS": DrawProgress(g, rect, visualLayer, continuousSeconds, opacity); break;
            case "CREDITS": DrawCredits(g, rect, visualLayer, continuousSeconds, opacity, sy); break;
            case "UPNEXTVIDEO": DrawUpNextVideo(g, rect, visualLayer, opacity); break;
            default:
                if (visualLayer.IsTimerLayer && string.IsNullOrWhiteSpace(visualLayer.Text))
                    DrawTimer(g, rect, visualLayer, continuousSeconds, opacity, activeTextVisual);
                else
                    DrawText(g, rect, resolvedText, visualLayer, opacity, activeTextVisual, sy);
                break;
        }
    }

    private void DrawPrecomposition(Graphics g, RectangleF rect, CgLayer layer, double localTime, double opacity)
    {
        var nested = layer.Precomposition;
        if (nested is null || nested.Layers.Count == 0 && (nested.HtmlSources?.Count ?? 0) == 0) return;
        var width = Math.Max(2, (int)Math.Round(rect.Width));
        var height = Math.Max(2, (int)Math.Round(rect.Height));
        var frame = RenderProjectSurface(nested, width, height, localTime);
        DrawVideoFrame(g, frame, rect, opacity);
    }

    private static unsafe void ApplyLayerMask(Bitmap overlay, CgLayer layer, double sx, double sy)
    {
        var width = overlay.Width;
        var height = overlay.Height;
        var maskWidthProject = layer.MaskWidth > .0001 ? layer.MaskWidth : Math.Max(1.0, layer.Width);
        var maskHeightProject = layer.MaskHeight > .0001 ? layer.MaskHeight : Math.Max(1.0, layer.Height);
        var maskXProject = layer.MaskWidth > .0001 ? layer.MaskX : layer.X;
        var maskYProject = layer.MaskHeight > .0001 ? layer.MaskY : layer.Y;
        var mx = maskXProject * sx;
        var my = maskYProject * sy;
        var mw = Math.Max(1.0, maskWidthProject * sx);
        var mh = Math.Max(1.0, maskHeightProject * sy);
        var feather = Math.Max(0.0, layer.MaskFeather * Math.Min(sx, sy));
        var radius = Math.Clamp(layer.MaskCornerRadius * Math.Min(sx, sy), 0.0, Math.Min(mw, mh) / 2.0);
        var shape = (layer.MaskShape ?? "Rectangle").Trim().ToUpperInvariant();
        var customMask = shape is "CUSTOM PATH" or "PEN PATH"
            ? CgPathData.Parse(layer.MaskPathData)
                .Select(p => new CgPathData.Point(mx + p.X * mw, my + p.Y * mh)).ToArray()
            : [];

        var data = overlay.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            var basePtr = (byte*)data.Scan0.ToPointer();
            for (var y = 0; y < height; y++)
            {
                var row = basePtr + y * data.Stride;
                for (var x = 0; x < width; x++)
                {
                    var pixel = row + x * 4;
                    if (pixel[3] == 0) continue;
                    var px = x + .5;
                    var py = y + .5;
                    double coverage;
                    if (customMask.Length >= 3)
                    {
                        var inside = CgPathData.Contains(customMask, px, py);
                        if (feather <= .0001) coverage = inside ? 1.0 : 0.0;
                        else
                        {
                            var edgeDistance = CgPathData.DistanceToEdges(customMask, px, py);
                            var signedDistance = inside ? -edgeDistance : edgeDistance;
                            var ft = Math.Clamp(0.5 - signedDistance / feather, 0.0, 1.0);
                            coverage = ft * ft * (3.0 - 2.0 * ft);
                        }
                    }
                    else if (shape == "ELLIPSE")
                    {
                        var rx = mw / 2.0; var ry = mh / 2.0;
                        var nx = (px - (mx + rx)) / Math.Max(.0001, rx);
                        var ny = (py - (my + ry)) / Math.Max(.0001, ry);
                        var radial = Math.Sqrt(nx * nx + ny * ny);
                        if (feather <= .0001) coverage = radial <= 1.0 ? 1.0 : 0.0;
                        else
                        {
                            var distFromEdge = (1.0 - radial) * Math.Min(rx, ry);
                            var t = Math.Clamp(0.5 + distFromEdge / feather, 0.0, 1.0);
                            coverage = t * t * (3.0 - 2.0 * t);
                        }
                    }
                    else
                    {
                        var cx = mx + mw / 2.0; var cy = my + mh / 2.0;
                        var halfW = mw / 2.0; var halfH = mh / 2.0;
                        var r = shape == "ROUNDED RECTANGLE" ? radius : 0.0;
                        var qx = Math.Abs(px - cx) - Math.Max(0.0, halfW - r);
                        var qy = Math.Abs(py - cy) - Math.Max(0.0, halfH - r);
                        var ox = Math.Max(qx, 0.0); var oy = Math.Max(qy, 0.0);
                        var signedDistance = Math.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(qx, qy), 0.0) - r;
                        if (feather <= .0001) coverage = signedDistance <= 0.0 ? 1.0 : 0.0;
                        else
                        {
                            var t = Math.Clamp(0.5 - signedDistance / feather, 0.0, 1.0);
                            coverage = t * t * (3.0 - 2.0 * t);
                        }
                    }
                    if (layer.MaskInvert) coverage = 1.0 - coverage;
                    var oldA = pixel[3];
                    var newA = (byte)Math.Clamp((int)Math.Round(oldA * coverage), 0, 255);
                    if (newA == 0)
                    {
                        pixel[0] = 0; pixel[1] = 0; pixel[2] = 0; pixel[3] = 0;
                    }
                    else
                    {
                        var factor = (double)newA / Math.Max(1, (int)oldA);
                        pixel[0] = (byte)Math.Clamp((int)Math.Round(pixel[0] * factor), 0, 255);
                        pixel[1] = (byte)Math.Clamp((int)Math.Round(pixel[1] * factor), 0, 255);
                        pixel[2] = (byte)Math.Clamp((int)Math.Round(pixel[2] * factor), 0, 255);
                        pixel[3] = newA;
                    }
                }
            }
        }
        finally { overlay.UnlockBits(data); }
    }

    private static bool UsesCustomBlend(CgLayer layer)
        => !string.IsNullOrWhiteSpace(layer.BlendMode) && !layer.BlendMode.Equals("Normal", StringComparison.OrdinalIgnoreCase);

    private static void DrawBlendedMedia(Graphics targetGraphics, byte[] targetBytes, int targetWidth, int targetHeight, int targetStride, string? mode, Action<Graphics> draw)
    {
        targetGraphics.Flush();
        using var overlay = new Bitmap(targetWidth, targetHeight, PixelFormat.Format32bppArgb);
        using (var og = Graphics.FromImage(overlay))
        {
            og.Clear(Color.Transparent);
            og.SmoothingMode = targetGraphics.SmoothingMode;
            og.InterpolationMode = targetGraphics.InterpolationMode;
            og.PixelOffsetMode = targetGraphics.PixelOffsetMode;
            og.CompositingMode = CompositingMode.SourceOver;
            using var transform = targetGraphics.Transform;
            og.Transform = transform;
            draw(og);
            og.Flush();
        }
        BlendBitmapIntoTarget(targetBytes, targetWidth, targetHeight, targetStride, overlay, mode ?? "Normal");
    }

    private static unsafe void BlendBitmapIntoTarget(byte[] target, int width, int height, int targetStride, Bitmap overlay, string mode)
    {
        var data = overlay.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var srcBase = (byte*)data.Scan0.ToPointer();
            fixed (byte* dstBase = target)
            {
                for (var y = 0; y < height; y++)
                {
                    var src = srcBase + y * data.Stride;
                    var dst = dstBase + y * targetStride;
                    for (var x = 0; x < width; x++, src += 4, dst += 4)
                    {
                        var sa = src[3] / 255.0;
                        if (sa <= .0001) continue;
                        var da = dst[3] / 255.0;
                        var sb = src[0] / 255.0; var sg = src[1] / 255.0; var sr = src[2] / 255.0;
                        var db = dst[0] / 255.0; var dg = dst[1] / 255.0; var dr = dst[2] / 255.0;
                        var blended = BlendRgb(mode, sr, sg, sb, dr, dg, db);
                        var br = blended.R; var bg = blended.G; var bb = blended.B;
                        var outA = sa + da * (1 - sa);
                        if (outA <= .0001) { dst[0] = dst[1] = dst[2] = dst[3] = 0; continue; }
                        // W3C source-over blend composition: transparent destination preserves source,
                        // opaque destination applies the selected blend equation.
                        var ob = ((1-sa)*da*db + (1-da)*sa*sb + sa*da*bb) / outA;
                        var og = ((1-sa)*da*dg + (1-da)*sa*sg + sa*da*bg) / outA;
                        var orr = ((1-sa)*da*dr + (1-da)*sa*sr + sa*da*br) / outA;
                        dst[0] = ToByte(ob); dst[1] = ToByte(og); dst[2] = ToByte(orr); dst[3] = ToByte(outA);
                    }
                }
            }
        }
        finally { overlay.UnlockBits(data); }
    }

    private static byte ToByte(double value) => (byte)Math.Clamp((int)Math.Round(value * 255), 0, 255);

    private static (double R,double G,double B) BlendRgb(string mode, double sr, double sg, double sb, double dr, double dg, double db)
    {
        var key=(mode??"Normal").Trim().ToUpperInvariant();
        return key switch
        {
            "HUE" => SetLum(SetSat((sr,sg,sb),Sat(dr,dg,db)),Lum(dr,dg,db)),
            "SATURATION" => SetLum(SetSat((dr,dg,db),Sat(sr,sg,sb)),Lum(dr,dg,db)),
            "COLOR" => SetLum((sr,sg,sb),Lum(dr,dg,db)),
            "LUMINOSITY" => SetLum((dr,dg,db),Lum(sr,sg,sb)),
            _ => (BlendChannel(key,sr,dr),BlendChannel(key,sg,dg),BlendChannel(key,sb,db))
        };
    }

    private static double Lum(double r,double g,double b) => .3*r + .59*g + .11*b;
    private static double Sat(double r,double g,double b) => Math.Max(r,Math.Max(g,b))-Math.Min(r,Math.Min(g,b));

    private static (double R,double G,double B) SetLum((double R,double G,double B) c,double targetLum)
    {
        var d=targetLum-Lum(c.R,c.G,c.B);
        return ClipColor((c.R+d,c.G+d,c.B+d));
    }

    private static (double R,double G,double B) ClipColor((double R,double G,double B) c)
    {
        var r=c.R; var g=c.G; var b=c.B;
        var l=Lum(r,g,b); var n=Math.Min(r,Math.Min(g,b)); var x=Math.Max(r,Math.Max(g,b));
        if(n<0)
        {
            var denom=l-n;
            if(Math.Abs(denom)>.000001) { r=l+((r-l)*l)/denom; g=l+((g-l)*l)/denom; b=l+((b-l)*l)/denom; }
            else r=g=b=0;
        }
        x=Math.Max(r,Math.Max(g,b));
        if(x>1)
        {
            var denom=x-l;
            if(Math.Abs(denom)>.000001) { r=l+((r-l)*(1-l))/denom; g=l+((g-l)*(1-l))/denom; b=l+((b-l)*(1-l))/denom; }
            else r=g=b=1;
        }
        return (Math.Clamp(r,0,1),Math.Clamp(g,0,1),Math.Clamp(b,0,1));
    }

    private static (double R,double G,double B) SetSat((double R,double G,double B) c,double targetSat)
    {
        var r=c.R; var g=c.G; var b=c.B;
        var minIndex = r<=g && r<=b ? 0 : (g<=r && g<=b ? 1 : 2);
        var maxIndex = r>=g && r>=b ? 0 : (g>=r && g>=b ? 1 : 2);
        var midIndex = 3-minIndex-maxIndex;
        double Get(int i) => i==0?r:i==1?g:b;
        void Set(int i,double v) { if(i==0) r=v; else if(i==1) g=v; else b=v; }
        var min=Get(minIndex); var mid=Get(midIndex); var max=Get(maxIndex);
        if(max>min+.000001)
        {
            mid=((mid-min)*targetSat)/(max-min);
            max=targetSat;
        }
        else mid=max=0;
        min=0;
        Set(minIndex,min); Set(midIndex,mid); Set(maxIndex,max);
        return (Math.Clamp(r,0,1),Math.Clamp(g,0,1),Math.Clamp(b,0,1));
    }

    private static double BlendChannel(string mode, double s, double d)
    {
        var key = (mode ?? "Normal").Trim().ToUpperInvariant();
        var value = key switch
        {
            "MULTIPLY" => s * d,
            "SCREEN" => 1 - (1-s) * (1-d),
            "OVERLAY" => d <= .5 ? 2*s*d : 1 - 2*(1-s)*(1-d),
            "DARKEN" => Math.Min(s,d),
            "LIGHTEN" => Math.Max(s,d),
            "COLOR DODGE" => s >= .9999 ? 1 : Math.Min(1, d / Math.Max(.0001, 1-s)),
            "COLOR BURN" => s <= .0001 ? 0 : 1 - Math.Min(1, (1-d) / Math.Max(.0001, s)),
            "LINEAR BURN" => Math.Max(0,d+s-1),
            "LINEAR DODGE (ADD)" or "ADD" or "LINEAR DODGE" => Math.Min(1,d+s),
            "HARD LIGHT" => s <= .5 ? 2*s*d : 1 - 2*(1-s)*(1-d),
            "SOFT LIGHT" => d <= .25
                ? d - (1-2*s)*d*(1-d)
                : d + (2*s-1)*(Math.Sqrt(d)-d),
            "VIVID LIGHT" => s <= .5
                ? (2*s <= .0001 ? 0 : 1-Math.Min(1,(1-d)/Math.Max(.0001,2*s)))
                : (2*(s-.5) >= .9999 ? 1 : Math.Min(1,d/Math.Max(.0001,1-2*(s-.5)))),
            "LINEAR LIGHT" => Math.Clamp(d+2*s-1,0,1),
            "PIN LIGHT" => s < .5 ? Math.Min(d,2*s) : Math.Max(d,2*s-1),
            "HARD MIX" => (s <= .5
                ? (2*s <= .0001 ? 0 : 1-Math.Min(1,(1-d)/Math.Max(.0001,2*s)))
                : (2*(s-.5) >= .9999 ? 1 : Math.Min(1,d/Math.Max(.0001,1-2*(s-.5))))) < .5 ? 0 : 1,
            "DIFFERENCE" => Math.Abs(d-s),
            "EXCLUSION" => d + s - 2*d*s,
            "SUBTRACT" => Math.Max(0, d-s),
            "DIVIDE" => s <= .0001 ? 1 : Math.Min(1, d/s),
            _ => s
        };
        return Math.Clamp(value, 0, 1);
    }

    private static void ApplyMotion(CgProject? project, CgLayer layer, double t, ref double x, ref double y, ref double w, ref double h, double sx, double sy, double continuousSeconds = -1)
    {
        var evalTime = continuousSeconds >= 0 ? continuousSeconds : t;
        var categoryElapsed = 0.0;
        var categoryDuration = 0.0;
        var categoryIntro = 0.0;
        var categoryOutro = 0.0;
        var categoryDriven = project != null && CgDataSourceService.TryGetActiveCategoryTiming(project, layer, evalTime,
            out _, out categoryElapsed, out categoryDuration, out _, out categoryIntro, out categoryOutro);
        if (string.Equals(layer.Type, "Ticker", StringComparison.OrdinalIgnoreCase) && !categoryDriven) return;
        var hasAnyTicker = project?.Layers?.Any(l => string.Equals(l.Type, "Ticker", StringComparison.OrdinalIgnoreCase)) == true;
        var isBadgeText = string.Equals(layer.DataField, "category", StringComparison.OrdinalIgnoreCase) ||
                          (layer.Name?.Contains("Badge", StringComparison.OrdinalIgnoreCase) ?? false && string.Equals(layer.Type, "Text", StringComparison.OrdinalIgnoreCase));

        var delay = Math.Max(0, layer.TextAnimationDelaySeconds);
        if (hasAnyTicker && !isBadgeText && layer.DataSourceId == Guid.Empty)
        {
            var layerName = layer.Name ?? "";
            var isStrip = string.Equals(layer.Type, "Shape", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(layer.Type, "ImageSequence", StringComparison.OrdinalIgnoreCase) ||
                          layerName.Contains("Bar", StringComparison.OrdinalIgnoreCase) ||
                          layerName.Contains("Plate", StringComparison.OrdinalIgnoreCase) ||
                          layerName.Contains("Accent", StringComparison.OrdinalIgnoreCase) ||
                          layerName.Contains("Divider", StringComparison.OrdinalIgnoreCase);
            if (isStrip)
            {
                var inP = Math.Clamp((evalTime - layer.StartSeconds - delay) / Math.Max(0.05, layer.AnimationInSeconds), 0, 1);
                switch ((layer.AnimationIn ?? "").ToUpperInvariant())
                {
                    case "SLIDE LEFT": x += (1 - inP) * 220 * sx; break;
                    case "SLIDE RIGHT": x -= (1 - inP) * 220 * sx; break;
                    case "SLIDE UP": y += (1 - inP) * 120 * sy; break;
                    case "SLIDE DOWN": y -= (1 - inP) * 120 * sy; break;
                }
                return;
            }
        }

        double inProgress, outProgress;
        var isTickerLayer = string.Equals(layer.Type, "Ticker", StringComparison.OrdinalIgnoreCase);
        var isCategoryBadge = string.Equals(layer.DataField, "category", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(layer.Role, "Badge", StringComparison.OrdinalIgnoreCase) ||
                              ((layer.Name?.Contains("Badge", StringComparison.OrdinalIgnoreCase) ?? false) &&
                               (string.Equals(layer.Type, "Text", StringComparison.OrdinalIgnoreCase) || string.Equals(layer.Type, "Shape", StringComparison.OrdinalIgnoreCase))) ||
                              (string.Equals(layer.Type, "Text", StringComparison.OrdinalIgnoreCase) &&
                               (layer.Name?.Contains("Category", StringComparison.OrdinalIgnoreCase) ?? false)) ||
                              layer.TickerCategoriesEnabled;

        if (categoryDriven)
        {
            CgDataSourceService.GetCategoryAnimationProgress(categoryElapsed, categoryDuration, categoryIntro, categoryOutro,
                out _, out _, out inProgress, out outProgress);
        }
        else if (isCategoryBadge && project != null)
        {
            var pausePoint = CgDataSourceService.ResolveEffectiveHoldPoint(project);
            var maxOutDuration = project.Layers.Where(x => x.Visible).Select(x => x.AnimationOutSeconds).DefaultIfEmpty(0.5).Max();
            var outPhaseStart = Math.Max(pausePoint, project.DurationSeconds - maxOutDuration);
            if (t < pausePoint)
            {
                var introTime = Math.Max(0, t - layer.StartSeconds - delay);
                inProgress = layer.AnimationInSeconds > 0.01 ? Math.Clamp(introTime / Math.Max(0.05, layer.AnimationInSeconds), 0, 1) : 1.0;
                outProgress = 1.0;
            }
            else if (t >= outPhaseStart)
            {
                inProgress = 1.0;
                outProgress = layer.AnimationOutSeconds > 0.01 ? Math.Clamp((project.DurationSeconds - t) / Math.Max(0.05, layer.AnimationOutSeconds), 0, 1) : 1.0;
            }
            else
            {
                // During ticker playing: SOLID, NO fade out / in animation!
                inProgress = 1.0;
                outProgress = 1.0;
            }
        }
        else if (layer.DataSourceId != Guid.Empty && layer.DataItemDurationSeconds > 0 && !isTickerLayer)
        {
            var itemDur = layer.DataItemDurationSeconds;
            var cycleTime = continuousSeconds >= 0 ? Math.Max(0, continuousSeconds % itemDur) : Math.Max(0, t % itemDur);
            var effectiveStart = Math.Max(0, layer.StartSeconds);
            var effectiveEnd = layer.EndSeconds > effectiveStart ? Math.Min(itemDur, layer.EndSeconds) : itemDur;
            inProgress = Math.Clamp((cycleTime - effectiveStart - delay) / Math.Max(.05, layer.AnimationInSeconds), 0, 1);
            outProgress = Math.Clamp((effectiveEnd - cycleTime) / Math.Max(.05, layer.AnimationOutSeconds), 0, 1);
        }
        else
        {
            inProgress = Math.Clamp((t - layer.StartSeconds - delay) / Math.Max(.05, layer.AnimationInSeconds), 0, 1);
            outProgress = Math.Clamp((layer.EndSeconds - t) / Math.Max(.05, layer.AnimationOutSeconds), 0, 1);
        }
        var p = Math.Min(inProgress, outProgress);
        var animation = inProgress < outProgress ? layer.AnimationIn : layer.AnimationOut;
        switch ((animation ?? "").ToUpperInvariant())
        {
            case "SLIDE LEFT": x += (1 - p) * 220 * sx; break;
            case "SLIDE RIGHT": x -= (1 - p) * 220 * sx; break;
            case "SLIDE UP": y += (1 - p) * 120 * sy; break;
            case "SLIDE DOWN": y -= (1 - p) * 120 * sy; break;
            case "SCALE DOWN":
                var startScaleDown = layer.TextAnimationScaleStart > 1.0 ? layer.TextAnimationScaleStart : 2.0;
                var endScaleDown = layer.TextAnimationScaleEnd > 0 ? layer.TextAnimationScaleEnd : 1.0;
                var scaleDown = endScaleDown + (startScaleDown - endScaleDown) * (1.0 - SmoothStep(p));
                var scx = x + w / 2; var scy = y + h / 2;
                w *= scaleDown; h *= scaleDown; x = scx - w / 2; y = scy - h / 2;
                break;
            case "SCALE IN":
            case "SCALE UP":
                var startScaleIn = layer.TextAnimationScaleStart > 0 ? layer.TextAnimationScaleStart : 0.2;
                var endScaleIn = layer.TextAnimationScaleEnd > 0 ? layer.TextAnimationScaleEnd : 1.0;
                var scaleIn = startScaleIn + (endScaleIn - startScaleIn) * SmoothStep(p);
                var six = x + w / 2; var siy = y + h / 2;
                w *= scaleIn; h *= scaleIn; x = six - w / 2; y = siy - h / 2;
                break;
            case "ZOOM":
            case "SCALE":
            case "POP":
                var cx = x + w / 2; var cy = y + h / 2;
                var floor = string.Equals(animation, "Pop", StringComparison.OrdinalIgnoreCase) ? .45 : .72;
                var scale = floor + (1 - floor) * SmoothStep(p);
                w *= scale; h *= scale; x = cx - w / 2; y = cy - h / 2;
                break;
            case "PUSH LEFT": x += (1 - SmoothStep(p)) * Math.Max(w, 260 * sx); break;
            case "PUSH RIGHT": x -= (1 - SmoothStep(p)) * Math.Max(w, 260 * sx); break;
            case "PUSH UP": y += (1 - SmoothStep(p)) * Math.Max(h, 150 * sy); break;
            case "PUSH DOWN": y -= (1 - SmoothStep(p)) * Math.Max(h, 150 * sy); break;
            case "WIPE LEFT": w *= SmoothStep(p); break;
            case "WIPE RIGHT": var oldW = w; w *= SmoothStep(p); x += oldW - w; break;
            case "BLUR":
                var blurScale = .92 + .08 * SmoothStep(p);
                var bx = x + w / 2; var by = y + h / 2;
                w *= blurScale; h *= blurScale; x = bx - w / 2; y = by - h / 2;
                break;
        }
    }

    private static double AnimationOpacity(CgProject? project, CgLayer layer, double t, double continuousSeconds = -1)
    {
        var hasAnyTicker = project?.Layers?.Any(l => string.Equals(l.Type, "Ticker", StringComparison.OrdinalIgnoreCase)) == true;
        var isTicker = string.Equals(layer.Type, "Ticker", StringComparison.OrdinalIgnoreCase);
        var timeToEval = continuousSeconds >= 0 ? continuousSeconds : t;
        if (project != null && CgDataSourceService.TryGetActiveCategoryTiming(project, layer, timeToEval,
                out _, out var categoryElapsed, out var categoryDuration, out _, out var categoryIntro, out var categoryOutro))
        {
            CgDataSourceService.GetCategoryAnimationProgress(categoryElapsed, categoryDuration, categoryIntro, categoryOutro,
                out var isIntro, out var isOutro, out var inProgress, out var outProgress);
            var animation = isOutro ? layer.AnimationOut : layer.AnimationIn;
            var affectsOpacity = string.Equals(animation, "Fade", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(animation, "Blur", StringComparison.OrdinalIgnoreCase) ||
                                 (animation?.Contains("Scale", StringComparison.OrdinalIgnoreCase) ?? false);
            if (isIntro) return affectsOpacity ? inProgress : 1.0;
            if (isOutro) return affectsOpacity ? outProgress : 1.0;
            return 1.0;
        }
        if (isTicker)
        {
            var evalTime = timeToEval;
            if (evalTime < layer.StartSeconds) return 0;
            if (layer.AnimationInSeconds > 0.01)
            {
                return Math.Clamp((evalTime - layer.StartSeconds) / Math.Max(0.05, layer.AnimationInSeconds), 0, 1);
            }
            return 1;
        }

        if (layer.SequenceAdvanceDataItem)
        {
            if (project != null && !CgDataSourceService.IsOverlayActiveForItem(project, layer, continuousSeconds >= 0 ? continuousSeconds : t))
                return 0;
            var itemDur = project != null ? CgDataSourceService.ResolveEffectiveItemDuration(project, layer) : (layer.DataItemDurationSeconds > 0 ? layer.DataItemDurationSeconds : 4.0);
            var eval = continuousSeconds >= 0 ? continuousSeconds : t;
            var refStart = Math.Max(0, layer.StartSeconds);
            var cycleTime = Math.Max(0, (eval - refStart) % Math.Max(0.05, itemDur));
            var burstDur = project != null ? CgDataSourceService.ResolveEffectiveOverlayDuration(project, layer) : Math.Max(0.5, layer.EndSeconds - layer.StartSeconds);
            if (cycleTime > burstDur + 0.05) return 0;
            return 1.0;
        }

        var isBadgeText = string.Equals(layer.DataField, "category", StringComparison.OrdinalIgnoreCase) ||
                          (string.Equals(layer.Type, "Text", StringComparison.OrdinalIgnoreCase) &&
                           ((layer.Name?.Contains("Badge", StringComparison.OrdinalIgnoreCase) ?? false) ||
                            (layer.Name?.Contains("Category", StringComparison.OrdinalIgnoreCase) ?? false))) ||
                          layer.TickerCategoriesEnabled;
        var delay = Math.Max(0, layer.TextAnimationDelaySeconds);
        if (isBadgeText && project != null)
        {
            var pausePoint = CgDataSourceService.ResolveEffectiveHoldPoint(project);
            var maxOutDuration = project.Layers.Where(x => x.Visible).Select(x => x.AnimationOutSeconds).DefaultIfEmpty(0.5).Max();
            var outPhaseStart = Math.Max(pausePoint, project.DurationSeconds - maxOutDuration);
            if (t < pausePoint)
            {
                var introTime = Math.Max(0, t - layer.StartSeconds - delay);
                if (introTime <= 0) return 0;
                return layer.AnimationInSeconds > 0.01 ? Math.Clamp(introTime / Math.Max(0.05, layer.AnimationInSeconds), 0, 1) : 1.0;
            }
            if (t >= outPhaseStart)
            {
                return layer.AnimationOutSeconds > 0.01 ? Math.Clamp((project.DurationSeconds - t) / Math.Max(0.05, layer.AnimationOutSeconds), 0, 1) : 1.0;
            }
            // During ticker playing: NO fade out, NO fade in!
            return 1.0;
        }

        if (hasAnyTicker && !isBadgeText && layer.DataSourceId == Guid.Empty)
        {
            var layerName = layer.Name ?? "";
            var isStrip = string.Equals(layer.Type, "Shape", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(layer.Type, "ImageSequence", StringComparison.OrdinalIgnoreCase) ||
                          layerName.Contains("Bar", StringComparison.OrdinalIgnoreCase) ||
                          layerName.Contains("Plate", StringComparison.OrdinalIgnoreCase) ||
                          layerName.Contains("Accent", StringComparison.OrdinalIgnoreCase) ||
                          layerName.Contains("Divider", StringComparison.OrdinalIgnoreCase);
            if (isStrip)
            {
                if (timeToEval < layer.StartSeconds + delay) return 0;
                if (layer.AnimationInSeconds > 0.01)
                    return Math.Clamp((timeToEval - layer.StartSeconds - delay) / Math.Max(0.05, layer.AnimationInSeconds), 0, 1);
                return 1.0;
            }
        }

        double a, b;
        var isTickerLayer = string.Equals(layer.Type, "Ticker", StringComparison.OrdinalIgnoreCase);
        if (isTickerLayer && project != null && CgDataSourceService.TryGetActiveCategoryTiming(project, layer, timeToEval, out _, out var catElapsed, out var catDur, out _, out _, out _))
        {
            if (catElapsed < delay) return 0;
            a = Math.Clamp((catElapsed - delay) / Math.Max(.05, layer.AnimationInSeconds), 0, 1);
            b = 1;
        }
        else if (layer.DataSourceId != Guid.Empty && layer.DataItemDurationSeconds > 0 && !isTickerLayer)
        {
            var itemDur = layer.DataItemDurationSeconds;
            var cycleTime = continuousSeconds >= 0 ? Math.Max(0, continuousSeconds % itemDur) : Math.Max(0, t % itemDur);
            var effectiveStart = Math.Max(0, layer.StartSeconds);
            var effectiveEnd = layer.EndSeconds > effectiveStart ? Math.Min(itemDur, layer.EndSeconds) : itemDur;
            if (cycleTime < effectiveStart + delay || cycleTime > effectiveEnd) return 0;
            a = Math.Clamp((cycleTime - effectiveStart - delay) / Math.Max(.05, layer.AnimationInSeconds), 0, 1);
            b = Math.Clamp((effectiveEnd - cycleTime) / Math.Max(.05, layer.AnimationOutSeconds), 0, 1);
        }
        else
        {
            if (t < layer.StartSeconds + delay || t > layer.EndSeconds) return 0;
            a = Math.Clamp((t - layer.StartSeconds - delay) / Math.Max(.05, layer.AnimationInSeconds), 0, 1);
            b = Math.Clamp((layer.EndSeconds - t) / Math.Max(.05, layer.AnimationOutSeconds), 0, 1);
        }
        var fadeIn = (string.Equals(layer.AnimationIn, "Fade", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(layer.AnimationIn, "Blur", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(layer.AnimationIn, "Scale Down", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(layer.AnimationIn, "Scale In", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(layer.AnimationIn, "Scale Up", StringComparison.OrdinalIgnoreCase)) ? a : 1;
        var fadeOut = (string.Equals(layer.AnimationOut, "Fade", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer.AnimationOut, "Blur", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer.AnimationOut, "Scale Down", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer.AnimationOut, "Scale In", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer.AnimationOut, "Scale Up", StringComparison.OrdinalIgnoreCase)) ? b : 1;
        return Math.Min(fadeIn, fadeOut);
    }

    private static Brush CreateGradientBrush(RectangleF rect, CgLayer layer, string primary, string secondary, double opacity)
    {
        var safeRect = new RectangleF(rect.X, rect.Y, Math.Max(1f, rect.Width), Math.Max(1f, rect.Height));
        var gradientAngle = double.IsFinite(layer.GradientAngle) ? layer.GradientAngle : 0;
        var brush = new LinearGradientBrush(safeRect, ParseColor(primary, opacity), ParseColor(secondary, opacity), (float)gradientAngle);
        var stops = (layer.GradientStops ?? []).OrderBy(x => x.Offset).ToArray();
        if (stops.Length < 2) return brush;

        var normalized = new List<(float Position, Color Color)>();
        foreach (var stop in stops)
        {
            var position = (float)(double.IsFinite(stop.Offset) ? Math.Clamp(stop.Offset, 0, 1) : 0);
            var color = ParseColor(stop.Color, opacity);
            if (normalized.Count > 0 && Math.Abs(position - normalized[^1].Position) < .00001f)
            {
                normalized[^1] = (position, color);
                continue;
            }
            normalized.Add((position, color));
        }
        if (normalized.Count < 2) return brush;
        if (normalized[0].Position > 0) normalized.Insert(0, (0, normalized[0].Color));
        if (normalized[^1].Position < 1) normalized.Add((1, normalized[^1].Color));

        brush.InterpolationColors = new ColorBlend
        {
            Colors = normalized.Select(x => x.Color).ToArray(),
            Positions = normalized.Select(x => x.Position).ToArray()
        };
        return brush;
    }

    private static void DrawShape(Graphics g, RectangleF rect, CgLayer layer, double opacity)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;
        using Brush brush = layer.UseGradient
            ? CreateGradientBrush(rect, layer, layer.Background, layer.GradientColor2, opacity)
            : new SolidBrush(ParseColor(layer.Background, opacity));
        using var pen = new Pen(ParseColor(layer.BorderColor, opacity), Math.Max(0.5f, (float)layer.BorderWidth));
        var kind = (layer.ShapeKind ?? "Rectangle").Trim().ToUpperInvariant();
        using var path = new GraphicsPath();
        var arm = Math.Max(4f, Math.Min(rect.Width, rect.Height) * .18f);
        switch (kind)
        {
            case "CUSTOM PATH":
            case "PEN PATH":
                var customPoints = CgPathData.Parse(layer.ShapePathData)
                    .Select(p => new PointF(rect.Left + (float)(p.X * rect.Width), rect.Top + (float)(p.Y * rect.Height)))
                    .ToArray();
                if (customPoints.Length >= 3)
                {
                    path.AddLines(customPoints);
                    if (layer.ShapePathClosed) path.CloseFigure();
                }
                else if (customPoints.Length == 2)
                {
                    path.AddLine(customPoints[0], customPoints[1]);
                }
                else
                {
                    AddRoundedRectangle(path, rect, layer);
                }
                break;
            case "L-SHAPE":
            case "LSHAPE":
                path.AddPolygon([
                    new PointF(rect.Left, rect.Top), new PointF(rect.Left + arm, rect.Top),
                    new PointF(rect.Left + arm, rect.Bottom - arm), new PointF(rect.Right, rect.Bottom - arm),
                    new PointF(rect.Right, rect.Bottom), new PointF(rect.Left, rect.Bottom)
                ]);
                break;
            case "U-SHAPE":
            case "USHAPE":
                path.AddPolygon([
                    new PointF(rect.Left, rect.Top), new PointF(rect.Left + arm, rect.Top),
                    new PointF(rect.Left + arm, rect.Bottom - arm), new PointF(rect.Right - arm, rect.Bottom - arm),
                    new PointF(rect.Right - arm, rect.Top), new PointF(rect.Right, rect.Top),
                    new PointF(rect.Right, rect.Bottom), new PointF(rect.Left, rect.Bottom)
                ]);
                break;
            case "ELLIPSE":
            case "CIRCLE":
                if (kind == "CIRCLE")
                {
                    var s = Math.Min(rect.Width, rect.Height);
                    path.AddEllipse(new RectangleF(rect.Left + (rect.Width - s) / 2f, rect.Top + (rect.Height - s) / 2f, s, s));
                }
                else
                {
                    path.AddEllipse(rect);
                }
                break;
            case "TRIANGLE":
                path.AddPolygon([
                    new PointF(rect.Left + rect.Width / 2f, rect.Top),
                    new PointF(rect.Right, rect.Bottom),
                    new PointF(rect.Left, rect.Bottom)
                ]);
                break;
            case "INVERTED TRIANGLE":
            case "INVERTEDTRIANGLE":
                path.AddPolygon([
                    new PointF(rect.Left, rect.Top),
                    new PointF(rect.Right, rect.Top),
                    new PointF(rect.Left + rect.Width / 2f, rect.Bottom)
                ]);
                break;
            case "DIAMOND":
                path.AddPolygon([
                    new PointF(rect.Left + rect.Width / 2f, rect.Top),
                    new PointF(rect.Right, rect.Top + rect.Height / 2f),
                    new PointF(rect.Left + rect.Width / 2f, rect.Bottom),
                    new PointF(rect.Left, rect.Top + rect.Height / 2f)
                ]);
                break;
            case "STAR":
                var cx = rect.Left + rect.Width / 2f;
                var cy = rect.Top + rect.Height / 2f;
                var rx = rect.Width / 2f;
                var ry = rect.Height / 2f;
                var irx = rx * 0.4f;
                var iry = ry * 0.4f;
                var starPts = new PointF[10];
                for (int p = 0; p < 10; p++)
                {
                    var angle = (float)(p * Math.PI / 5.0 - Math.PI / 2.0);
                    var rCurX = (p % 2 == 0) ? rx : irx;
                    var rCurY = (p % 2 == 0) ? ry : iry;
                    starPts[p] = new PointF(cx + (float)Math.Cos(angle) * rCurX, cy + (float)Math.Sin(angle) * rCurY);
                }
                path.AddPolygon(starPts);
                break;
            case "PENTAGON":
                var pcx = rect.Left + rect.Width / 2f;
                var pcy = rect.Top + rect.Height / 2f;
                var prx = rect.Width / 2f;
                var pry = rect.Height / 2f;
                var pentPts = new PointF[5];
                for (int p = 0; p < 5; p++)
                {
                    var angle = (float)(p * 2.0 * Math.PI / 5.0 - Math.PI / 2.0);
                    pentPts[p] = new PointF(pcx + (float)Math.Cos(angle) * prx, pcy + (float)Math.Sin(angle) * pry);
                }
                path.AddPolygon(pentPts);
                break;
            case "HEXAGON":
                var hx = rect.Width * 0.25f;
                path.AddPolygon([
                    new PointF(rect.Left + hx, rect.Top),
                    new PointF(rect.Right - hx, rect.Top),
                    new PointF(rect.Right, rect.Top + rect.Height / 2f),
                    new PointF(rect.Right - hx, rect.Bottom),
                    new PointF(rect.Left + hx, rect.Bottom),
                    new PointF(rect.Left, rect.Top + rect.Height / 2f)
                ]);
                break;
            case "OCTAGON":
                var ox = rect.Width * 0.29f;
                var oy = rect.Height * 0.29f;
                path.AddPolygon([
                    new PointF(rect.Left + ox, rect.Top),
                    new PointF(rect.Right - ox, rect.Top),
                    new PointF(rect.Right, rect.Top + oy),
                    new PointF(rect.Right, rect.Bottom - oy),
                    new PointF(rect.Right - ox, rect.Bottom),
                    new PointF(rect.Left + ox, rect.Bottom),
                    new PointF(rect.Left, rect.Bottom - oy),
                    new PointF(rect.Left, rect.Top + oy)
                ]);
                break;
            case "HEART":
                var hw = rect.Width;
                var hh = rect.Height;
                path.AddBezier(new PointF(rect.Left + hw / 2f, rect.Top + hh * 0.3f),
                               new PointF(rect.Left + hw * 0.2f, rect.Top),
                               new PointF(rect.Left, rect.Top + hh * 0.35f),
                               new PointF(rect.Left, rect.Top + hh * 0.55f));
                path.AddBezier(new PointF(rect.Left, rect.Top + hh * 0.55f),
                               new PointF(rect.Left, rect.Top + hh * 0.75f),
                               new PointF(rect.Left + hw * 0.25f, rect.Top + hh * 0.9f),
                               new PointF(rect.Left + hw / 2f, rect.Bottom));
                path.AddBezier(new PointF(rect.Left + hw / 2f, rect.Bottom),
                               new PointF(rect.Right - hw * 0.25f, rect.Top + hh * 0.9f),
                               new PointF(rect.Right, rect.Top + hh * 0.75f),
                               new PointF(rect.Right, rect.Top + hh * 0.55f));
                path.AddBezier(new PointF(rect.Right, rect.Top + hh * 0.55f),
                               new PointF(rect.Right, rect.Top + hh * 0.35f),
                               new PointF(rect.Right - hw * 0.2f, rect.Top),
                               new PointF(rect.Left + hw / 2f, rect.Top + hh * 0.3f));
                path.CloseFigure();
                break;
            case "ARROW RIGHT":
            case "ARROWRIGHT":
                var stemY = rect.Height * 0.25f;
                var arrowHeadX = rect.Width * 0.6f;
                path.AddPolygon([
                    new PointF(rect.Left, rect.Top + stemY),
                    new PointF(rect.Left + arrowHeadX, rect.Top + stemY),
                    new PointF(rect.Left + arrowHeadX, rect.Top),
                    new PointF(rect.Right, rect.Top + rect.Height / 2f),
                    new PointF(rect.Left + arrowHeadX, rect.Bottom),
                    new PointF(rect.Left + arrowHeadX, rect.Bottom - stemY),
                    new PointF(rect.Left, rect.Bottom - stemY)
                ]);
                break;
            case "ARROW LEFT":
            case "ARROWLEFT":
                var stemYL = rect.Height * 0.25f;
                var headXL = rect.Left + rect.Width * 0.4f;
                path.AddPolygon([
                    new PointF(headXL, rect.Top),
                    new PointF(headXL, rect.Top + stemYL),
                    new PointF(rect.Right, rect.Top + stemYL),
                    new PointF(rect.Right, rect.Bottom - stemYL),
                    new PointF(headXL, rect.Bottom - stemYL),
                    new PointF(headXL, rect.Bottom),
                    new PointF(rect.Left, rect.Top + rect.Height / 2f)
                ]);
                break;
            case "TRAPEZOID":
                var trapW = rect.Width * 0.2f;
                path.AddPolygon([
                    new PointF(rect.Left + trapW, rect.Top),
                    new PointF(rect.Right - trapW, rect.Top),
                    new PointF(rect.Right, rect.Bottom),
                    new PointF(rect.Left, rect.Bottom)
                ]);
                break;
            case "CROSS / PLUS":
            case "CROSS":
            case "PLUS":
                var cThickness = Math.Min(rect.Width, rect.Height) * 0.3f;
                var cx1 = rect.Left + (rect.Width - cThickness) / 2f;
                var cx2 = cx1 + cThickness;
                var cy1 = rect.Top + (rect.Height - cThickness) / 2f;
                var cy2 = cy1 + cThickness;
                path.AddPolygon([
                    new PointF(cx1, rect.Top), new PointF(cx2, rect.Top),
                    new PointF(cx2, cy1), new PointF(rect.Right, cy1),
                    new PointF(rect.Right, cy2), new PointF(cx2, cy2),
                    new PointF(cx2, rect.Bottom), new PointF(cx1, rect.Bottom),
                    new PointF(cx1, cy2), new PointF(rect.Left, cy2),
                    new PointF(rect.Left, cy1), new PointF(cx1, cy1)
                ]);
                break;
            case "SPEECH BUBBLE":
            case "SPEECHBUBBLE":
                var bH = rect.Height * 0.8f;
                AddRoundedRectangle(path, new RectangleF(rect.Left, rect.Top, rect.Width, bH), layer);
                path.AddPolygon([
                    new PointF(rect.Left + rect.Width * 0.2f, rect.Top + bH),
                    new PointF(rect.Left + rect.Width * 0.15f, rect.Bottom),
                    new PointF(rect.Left + rect.Width * 0.35f, rect.Top + bH)
                ]);
                break;
            case "LINE":
                path.AddLine(rect.Left, rect.Top + rect.Height / 2f, rect.Right, rect.Top + rect.Height / 2f); break;
            default:
                AddRoundedRectangle(path, rect, layer);
                break;
        }
        if (kind == "LINE" || (kind is "CUSTOM PATH" or "PEN PATH" && !layer.ShapePathClosed))
        {
            using var linePen = new Pen(ParseColor(layer.Background, opacity), Math.Max(1f, (float)(layer.BorderWidth > 0 ? layer.BorderWidth : rect.Height)));
            g.DrawPath(linePen, path);
            return;
        }
        g.FillPath(brush, path);
        if (layer.GlossEnabled && layer.GlossOpacity > 0)
        {
            using var gloss = new LinearGradientBrush(
                new RectangleF(rect.Left, rect.Top, rect.Width, Math.Max(1, rect.Height * .55f)),
                Color.FromArgb((int)(Math.Clamp(layer.GlossOpacity,0,1) * 210 * opacity), 255, 255, 255),
                Color.FromArgb(0, 255, 255, 255), 90f);
            using var previousClip = g.Clip.Clone();
            g.SetClip(path);
            g.FillRectangle(gloss, rect.Left, rect.Top, rect.Width, rect.Height * .55f);
            g.SetClip(previousClip, CombineMode.Replace);
        }
        if (layer.BorderWidth > 0 && pen.Color.A > 0) g.DrawPath(pen, path);
    }

    private static void AddRoundedRectangle(GraphicsPath path, RectangleF rect, CgLayer layer)
    {
        var separate = layer.CornerRadiusTopLeft > 0 || layer.CornerRadiusTopRight > 0 || layer.CornerRadiusBottomRight > 0 || layer.CornerRadiusBottomLeft > 0;
        var max = Math.Min(rect.Width, rect.Height) / 2f;
        float Radius(double value) => Math.Clamp((float)value, 0, max);
        float PercentRadius(double value) => Math.Clamp((float)(Math.Clamp(value, 0, 50) / 100.0 * Math.Min(rect.Width, rect.Height)), 0, max);
        var tl = layer.CornerRadiusAsPercent ? PercentRadius(layer.CornerRadiusTopLeftPercent) : Radius(separate ? layer.CornerRadiusTopLeft : layer.CornerRadius);
        var tr = layer.CornerRadiusAsPercent ? PercentRadius(layer.CornerRadiusTopRightPercent) : Radius(separate ? layer.CornerRadiusTopRight : layer.CornerRadius);
        var br = layer.CornerRadiusAsPercent ? PercentRadius(layer.CornerRadiusBottomRightPercent) : Radius(separate ? layer.CornerRadiusBottomRight : layer.CornerRadius);
        var bl = layer.CornerRadiusAsPercent ? PercentRadius(layer.CornerRadiusBottomLeftPercent) : Radius(separate ? layer.CornerRadiusBottomLeft : layer.CornerRadius);
        if (tl <= 0 && tr <= 0 && br <= 0 && bl <= 0) { path.AddRectangle(rect); return; }

        path.StartFigure();
        path.AddLine(rect.Left + tl, rect.Top, rect.Right - tr, rect.Top);
        if (tr > 0) path.AddArc(rect.Right - tr * 2, rect.Top, tr * 2, tr * 2, 270, 90); else path.AddLine(rect.Right, rect.Top, rect.Right, rect.Top);
        path.AddLine(rect.Right, rect.Top + tr, rect.Right, rect.Bottom - br);
        if (br > 0) path.AddArc(rect.Right - br * 2, rect.Bottom - br * 2, br * 2, br * 2, 0, 90); else path.AddLine(rect.Right, rect.Bottom, rect.Right, rect.Bottom);
        path.AddLine(rect.Right - br, rect.Bottom, rect.Left + bl, rect.Bottom);
        if (bl > 0) path.AddArc(rect.Left, rect.Bottom - bl * 2, bl * 2, bl * 2, 90, 90); else path.AddLine(rect.Left, rect.Bottom, rect.Left, rect.Bottom);
        path.AddLine(rect.Left, rect.Bottom - bl, rect.Left, rect.Top + tl);
        if (tl > 0) path.AddArc(rect.Left, rect.Top, tl * 2, tl * 2, 180, 90); else path.AddLine(rect.Left, rect.Top, rect.Left, rect.Top);
        path.CloseFigure();
    }

    private static void DrawEllipse(Graphics g, RectangleF rect, CgLayer layer, double opacity)
    {
        using var brush = new SolidBrush(ParseColor(layer.Background, opacity));
        using var pen = new Pen(ParseColor(layer.Fill, opacity), 2f);
        g.FillEllipse(brush, rect);
        g.DrawEllipse(pen, rect);
    }

    private static void DrawLine(Graphics g, RectangleF rect, CgLayer layer, double opacity)
    {
        using var pen = new Pen(ParseColor(layer.Fill, opacity), Math.Max(1f, (float)(layer.FontSize / 14.0)));
        g.DrawLine(pen, rect.Left, rect.Top + rect.Height / 2f, rect.Right, rect.Top + rect.Height / 2f);
    }

    private static bool IsPreetiFont(string? font) =>
        font != null && (font.Contains("Preeti", StringComparison.OrdinalIgnoreCase) ||
                         font.Contains("Kantipur", StringComparison.OrdinalIgnoreCase) ||
                         font.Contains("Ganesh", StringComparison.OrdinalIgnoreCase) ||
                         font.Contains("Suvadin", StringComparison.OrdinalIgnoreCase) ||
                         font.Contains("Sagarmatha", StringComparison.OrdinalIgnoreCase));

    private static bool ContainsDevanagari(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var ch in text)
        {
            if (ch >= 0x0900 && ch <= 0x097F) return true;
        }
        return false;
    }

    private static void DrawText(Graphics g, RectangleF rect, string? text, CgLayer layer, double opacity, CgAnimationEngine.TextVisualState? textVisual = null, double sy = 1.0)
    {
        if (layer.AutoConvertToPreeti || (IsPreetiFont(layer.FontFamily) && ContainsDevanagari(text)))
            text = NepaliCalendarService.ConvertToPreeti(text ?? string.Empty);

        if (layer.UseBackground)
        {
            using var bg = new SolidBrush(ParseColor(layer.Background, opacity));
            if (bg.Color.A > 0) g.FillRectangle(bg, rect);
        }
        var textTransformState = g.Save();
        try
        {
            if (textVisual.HasValue && Math.Abs(textVisual.Value.Scale - 1.0) > 0.001)
            {
                var scale = (float)textVisual.Value.Scale;
                var align = (layer.HorizontalTextAlignment ?? "Left").Trim().ToUpperInvariant();
                var originX = align switch
                {
                    "CENTER" => rect.Left + rect.Width / 2f,
                    "RIGHT" => rect.Right,
                    _ => rect.Left
                };
                var originY = rect.Top + rect.Height / 2f;
                g.TranslateTransform(originX, originY);
                g.ScaleTransform(scale, scale);
                g.TranslateTransform(-originX, -originY);
            }
            var style = FontStyle.Regular;
        if (layer.Bold) style |= FontStyle.Bold;
        if (layer.Italic) style |= FontStyle.Italic;
        if (layer.Underline) style |= FontStyle.Underline;
        var effectiveSy = sy > 0.05 ? sy : 1.0;
        var fontScale = rect.Height / Math.Max(1.0, layer.Height * effectiveSy);
        using var font = CreateFont(layer.FontFamily, (float)Math.Max(6, layer.FontSize * effectiveSy * fontScale), style);
        using var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoClip };
        format.Alignment = (layer.HorizontalTextAlignment ?? "Left").ToUpperInvariant() switch
        {
            "CENTER" => StringAlignment.Center,
            "RIGHT" => StringAlignment.Far,
            _ => StringAlignment.Near
        };
        format.LineAlignment = (layer.VerticalTextAlignment ?? "Center").ToUpperInvariant() switch
        {
            "TOP" => StringAlignment.Near,
            "BOTTOM" => StringAlignment.Far,
            _ => StringAlignment.Center
        };

        var value = text ?? "";
        var effectiveGlowRadius = layer.GlowRadius;
        var effectiveGlowColor = layer.GlowColor;
        if (textVisual.HasValue && textVisual.Value.DynamicGlowRadius > 0)
        {
            effectiveGlowRadius = Math.Max(effectiveGlowRadius, textVisual.Value.DynamicGlowRadius);
            if (!string.IsNullOrWhiteSpace(textVisual.Value.DynamicGlowColor))
                effectiveGlowColor = textVisual.Value.DynamicGlowColor;
        }

        if (effectiveGlowRadius > 0)
        {
            var glowRadius = Math.Clamp((int)Math.Round(effectiveGlowRadius), 1, 24);
            var glowBase = ParseColor(effectiveGlowColor, opacity * .35);
            using var glowBrush = new SolidBrush(glowBase);
            for (var radius = glowRadius; radius >= 1; radius -= Math.Max(1, glowRadius / 4))
            {
                for (var angle = 0; angle < 360; angle += 45)
                {
                    var rad = angle * Math.PI / 180.0;
                    var gx = (float)(Math.Cos(rad) * radius);
                    var gy = (float)(Math.Sin(rad) * radius);
                    g.DrawString(value, font, glowBrush, new RectangleF(rect.X + gx, rect.Y + gy, rect.Width, rect.Height), format);
                }
            }
        }
        if (layer.MotionBlurSamples > 0 && (Math.Abs(layer.MotionBlurX) > .01 || Math.Abs(layer.MotionBlurY) > .01))
        {
            var samples = Math.Clamp(layer.MotionBlurSamples, 1, 12);
            for (var sample = samples; sample >= 1; sample--)
            {
                var f = sample / (double)(samples + 1);
                using var blurBrush = new SolidBrush(ParseColor(layer.Fill, opacity * .10 * (1.0 - f * .45)));
                g.DrawString(value, font, blurBrush, new RectangleF(rect.X + (float)(layer.MotionBlurX * f), rect.Y + (float)(layer.MotionBlurY * f), rect.Width, rect.Height), format);
            }
        }
        if (layer.ShadowEnabled && (layer.ShadowBlur > 0 || Math.Abs(layer.ShadowOffsetX) > .01 || Math.Abs(layer.ShadowOffsetY) > .01))
        {
            var baseShadow = ParseColor(layer.ShadowColor, opacity);
            var blur = Math.Clamp((int)Math.Round(layer.ShadowBlur), 0, 8);
            if (blur > 0)
            {
                var radius = blur;
                var sigma = Math.Max(0.8, radius / 2.0);
                var twoSigmaSq = 2.0 * sigma * sigma;
                for (var ox = -radius; ox <= radius; ox += Math.Max(1, radius / 3))
                {
                    for (var oy = -radius; oy <= radius; oy += Math.Max(1, radius / 3))
                    {
                        var distSq = ox * ox + oy * oy;
                        var weight = Math.Exp(-distSq / twoSigmaSq);
                        var a = (int)Math.Clamp(baseShadow.A * weight * 0.35, 1, 255);
                        using var sb = new SolidBrush(Color.FromArgb(a, baseShadow.R, baseShadow.G, baseShadow.B));
                        g.DrawString(value, font, sb, new RectangleF(rect.X + (float)layer.ShadowOffsetX + ox, rect.Y + (float)layer.ShadowOffsetY + oy, rect.Width, rect.Height), format);
                    }
                }
            }
            else
            {
                using var sb = new SolidBrush(baseShadow);
                g.DrawString(value, font, sb, new RectangleF(rect.X + (float)layer.ShadowOffsetX, rect.Y + (float)layer.ShadowOffsetY, rect.Width, rect.Height), format);
            }
        }

        if (layer.OutlineWidth > 0)
        {
            using var path = new GraphicsPath();
            var emSize = g.DpiY * font.Size / 72f;
            if (format.Alignment == StringAlignment.Near)
            {
                var origin = new PointF(rect.Left, rect.Top + Math.Max(0, (rect.Height - font.GetHeight(g)) / 2f));
                path.AddString(value, font.FontFamily, (int)style, emSize, origin, format);
            }
            else
            {
                // A point origin would centre/right-align the text on rect.Left; lay it out in the box instead.
                path.AddString(value, font.FontFamily, (int)style, emSize, rect, format);
            }
            using var outline = new Pen(ParseColor(layer.OutlineColor, opacity), Math.Max(1f, (float)layer.OutlineWidth)) { LineJoin = LineJoin.Round };
            using Brush fill = layer.UseGradient
                ? CreateGradientBrush(rect, layer, layer.Fill, layer.GradientColor2, opacity)
                : new SolidBrush(ParseColor(layer.Fill, opacity));
            g.DrawPath(outline, path);
            g.FillPath(fill, path);
        }
        else
        {
            using Brush brush = layer.UseGradient
                ? CreateGradientBrush(rect, layer, layer.Fill, layer.GradientColor2, opacity)
                : new SolidBrush(ParseColor(layer.Fill, opacity));
            g.DrawString(value, font, brush, rect, format);
        }
        }
        finally
        {
            g.Restore(textTransformState);
        }
    }

    private void DrawImage(Graphics g, RectangleF rect, string? path, double opacity)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        Image? image;
        lock (_imageSync)
        {
            if (!_images.TryGetValue(path, out image))
            {
                using var temp = Image.FromFile(path);
                image = new Bitmap(temp);
                _images[path] = image;
            }
        }
        var matrix = new ColorMatrix { Matrix33 = (float)opacity };
        using var attrs = new ImageAttributes();
        attrs.SetColorMatrix(matrix);
        g.DrawImage(image, Rectangle.Round(rect), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attrs);
    }

    private readonly record struct TickerSegment(string Category, string Text, bool IsCategoryBadge);

    private static List<TickerSegment> ParseTickerSegments(string raw, CgLayer layer)
    {
        var list = new List<TickerSegment>();
        var text = (raw ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(text)) return list;

        // 1. Try parsing JSON format
        if (text.StartsWith('[') && text.EndsWith(']'))
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
                            if (!string.IsNullOrWhiteSpace(cat))
                            {
                                list.Add(new TickerSegment(cat, cat.ToUpperInvariant(), true));
                            }
                            if (elem.TryGetProperty("items", out var itemsProp) && itemsProp.ValueKind == System.Text.Json.JsonValueKind.Array)
                            {
                                foreach (var sub in itemsProp.EnumerateArray())
                                {
                                    var itemStr = sub.GetString();
                                    if (!string.IsNullOrWhiteSpace(itemStr)) list.Add(new TickerSegment(cat, itemStr, false));
                                }
                            }
                            else if (elem.TryGetProperty("text", out var tp) || elem.TryGetProperty("title", out tp) || elem.TryGetProperty("headline", out tp))
                            {
                                var itemStr = tp.GetString();
                                if (!string.IsNullOrWhiteSpace(itemStr)) list.Add(new TickerSegment(cat, itemStr, false));
                            }
                        }
                        else if (elem.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            var s = elem.GetString();
                            if (!string.IsNullOrWhiteSpace(s)) list.Add(new TickerSegment("", s, false));
                        }
                    }
                    if (list.Count > 0) return list;
                }
            }
            catch { }
        }

        // 2. Check for bracketed category tags e.g. [WORLD NEWS] Headline 1 • Headline 2 [CRICKET] Match updates
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
                    if (!string.IsNullOrWhiteSpace(cat)) list.Add(new TickerSegment(cat, cat.ToUpperInvariant(), true));
                    var subItems = content.Split(sepChars.ToArray(), StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    foreach (var sub in subItems)
                    {
                        var clean = sub.Trim().Trim('•', '|', '★', '-', '—', ' ').Trim();
                        if (!string.IsNullOrWhiteSpace(clean)) list.Add(new TickerSegment(cat, clean, false));
                    }
                }
                if (list.Count > 0) return list;
            }
        }

        // 3. Check for double pipe category delimiter: CATEGORY: News 1 • News 2 || CATEGORY 2: News 3
        if (layer.TickerCategoriesEnabled && text.Contains("||"))
        {
            var blocks = text.Split("||", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var block in blocks)
            {
                var colonIdx = block.IndexOf(':');
                if (colonIdx > 0)
                {
                    var cat = block[..colonIdx].Trim();
                    var rest = block[(colonIdx + 1)..].Trim();
                    list.Add(new TickerSegment(cat, cat.ToUpperInvariant(), true));
                    var subItems = rest.Split(['•', '|', '\n', '★'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    foreach (var sub in subItems)
                    {
                        var clean = sub.Trim().Trim('•', '|', '★', '-', '—', ' ').Trim();
                        if (!string.IsNullOrWhiteSpace(clean)) list.Add(new TickerSegment(cat, clean, false));
                    }
                }
                else
                {
                    list.Add(new TickerSegment("", block, false));
                }
            }
            if (list.Count > 0) return list;
        }

        // 4. Default: split by separator bullets/pipes
        var items = text.Split(['•', '|', '\n', '★'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (items.Length == 0) items = [text];
        foreach (var item in items)
        {
            var clean = item.Trim().Trim('•', '|', '★', '-', '—', ' ').Trim();
            if (!string.IsNullOrWhiteSpace(clean)) list.Add(new TickerSegment("", clean, false));
        }
        return list;
    }

    private void DrawTicker(Graphics g, RectangleF rect, CgLayer layer, double t, double opacity, string? resolvedText = null, CgProject? project = null)
    {
        if (layer.UseBackground)
        {
            using var bg = new SolidBrush(ParseColor(layer.Background, opacity));
            if (bg.Color.A > 0) g.FillRectangle(bg, rect);
        }

        var style = layer.Bold ? FontStyle.Bold : FontStyle.Regular;
        if (layer.Italic) style |= FontStyle.Italic;
        using var font = CreateFont(layer.FontFamily, (float)Math.Max(10, layer.FontSize * rect.Height / Math.Max(1, layer.Height)), style);
        using var badgeFont = CreateFont(layer.FontFamily, (float)Math.Max(9, (layer.FontSize * 0.82) * rect.Height / Math.Max(1, layer.Height)), FontStyle.Bold);
        using var brush = new SolidBrush(ParseColor(layer.Fill, opacity));
        using var badgeTextBrush = new SolidBrush(Color.FromArgb((int)(255 * opacity), 255, 255, 255));
        var badgeBgColor = ParseColor(string.IsNullOrWhiteSpace(layer.TickerCategoryBadgeBackground) ? "#D32F2F" : layer.TickerCategoryBadgeBackground, opacity);
        using var badgeBgBrush = new SolidBrush(badgeBgColor);

        var raw = string.IsNullOrWhiteSpace(resolvedText) ? (string.IsNullOrWhiteSpace(layer.Text) ? "KASHTRIX NEWS • LIVE" : layer.Text) : resolvedText;
        // Use continuous time directly — never modulo by itemDur so ticker content
        // scrolls all the way through before repeating, regardless of timeline duration.
        var elapsed = Math.Max(0, t - layer.StartSeconds);
        var speed = Math.Max(20, layer.Speed);

        // Load separator logo if specified and exists
        Image? sepLogo = null;
        var hasSepLogo = !string.IsNullOrWhiteSpace(layer.TickerSeparatorLogo) && File.Exists(layer.TickerSeparatorLogo);
        if (hasSepLogo)
        {
            lock (_imageSync)
            {
                if (!_images.TryGetValue(layer.TickerSeparatorLogo!, out sepLogo))
                {
                    try
                    {
                        using var temp = Image.FromFile(layer.TickerSeparatorLogo!);
                        sepLogo = new Bitmap(temp);
                        _images[layer.TickerSeparatorLogo!] = sepLogo;
                    }
                    catch { }
                }
            }
        }

        var separatorText = string.IsNullOrEmpty(layer.TickerSeparator) ? "  ★  " : layer.TickerSeparator;
        if (!separatorText.EndsWith(' ')) separatorText += " ";
        if (!separatorText.StartsWith(' ')) separatorText = " " + separatorText;
        var sepTextSize = g.MeasureString(separatorText, font);
        var logoSize = hasSepLogo && sepLogo != null ? Math.Max(14f, rect.Height * 0.45f) : 0f;
        var logoPad = hasSepLogo ? 12f : 0f;
        var sepWidth = hasSepLogo ? (logoSize + logoPad * 2) : sepTextSize.Width;
        const float badgeGap = 16f;

        g.SetClip(rect);

        var isRight = (layer.TickerDirection ?? "Left").Equals("Right", StringComparison.OrdinalIgnoreCase);

        // Check if layer is bound to a dynamic data source with items/categories
        CgDataSource? ds = null;
        if (layer.DataSourceId != Guid.Empty && project?.DataSources != null)
            ds = project.DataSources.FirstOrDefault(x => x.Id == layer.DataSourceId);
        var rows = ds != null ? CgDataRuntime.Shared.GetRows(ds) : null;

        if (rows != null && rows.Count > 0)
        {
            var hasDedicatedCategory = project?.Layers?.Any(x => x.Visible && x.Id != layer.Id &&
                (string.Equals(x.DataField, "category", StringComparison.OrdinalIgnoreCase) ||
                 (x.Name?.Contains("Badge", StringComparison.OrdinalIgnoreCase) ?? false))) == true;
            var showInlineBadge = layer.TickerCategoriesEnabled && !hasDedicatedCategory;

            float badgeWidthEstimate = 0f;
            if (showInlineBadge)
            {
                var sampleCat = rows.Select(r => CgDataSourceService.ResolveField(r, "category"))
                    .Concat(rows.Select(r => CgDataSourceService.ResolveField(r, "badge")))
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .OrderByDescending(c => c.Length)
                    .FirstOrDefault() ?? "NEWS";
                var catSize = g.MeasureString(sampleCat, badgeFont);
                var badgeW = Math.Min(rect.Width * .42f, catSize.Width + 24f);
                badgeWidthEstimate = badgeW + badgeGap + 6f;
            }
            var crawlWindowWidth = Math.Max(100f, rect.Width - badgeWidthEstimate);
            var schedule = CgDataSourceService.BuildCategoryFeedSchedule(project ?? new CgProject(), layer, rows, crawlWindowWidth, speed);
            var (activeCategory, categoryElapsed, _) = schedule.Evaluate(elapsed);
            var newsElapsed = Math.Max(0, categoryElapsed - activeCategory.IntroDuration);
            var segments = activeCategory.Items
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => new TickerSegment(activeCategory.Category, x, false))
                .ToList();
            if (segments.Count == 0 && !string.IsNullOrWhiteSpace(activeCategory.CombinedItemsText))
                segments.Add(new TickerSegment(activeCategory.Category, activeCategory.CombinedItemsText, false));

            var contentRect = rect;
            if (showInlineBadge && !string.IsNullOrWhiteSpace(activeCategory.Category))
            {
                var catSize = g.MeasureString(activeCategory.Category, badgeFont);
                var badgeW = Math.Min(rect.Width * .42f, catSize.Width + 24f);
                var badgeRect = new RectangleF(rect.Left + 6f, rect.Top + 6f, badgeW, Math.Max(8f, rect.Height - 12f));
                g.FillRectangle(badgeBgBrush, badgeRect);
                using var badgeFormat = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(activeCategory.Category, badgeFont, badgeTextBrush, badgeRect, badgeFormat);
                contentRect = new RectangleF(badgeRect.Right + badgeGap, rect.Top, Math.Max(1f, rect.Right - badgeRect.Right - badgeGap), rect.Height);
            }

            if (segments.Count == 0)
            {
                g.ResetClip();
                return;
            }

            if (string.Equals(layer.TickerMode, "Push", StringComparison.OrdinalIgnoreCase))
            {
                DrawPushTicker(g, contentRect, layer, newsElapsed, speed, font, badgeFont, brush, badgeTextBrush, badgeBgBrush, segments, gapless: true);
            }
            else
            {
                // Crawl only the active category. It must leave the body completely
                // before the schedule advances to the next category.
                var measured = new List<(TickerSegment Seg, float Width)>();
                float totalContentWidth = 0;
                for (var i = 0; i < segments.Count; i++)
                {
                    var seg = segments[i];
                    float itemWidth;
                    if (seg.IsCategoryBadge)
                    {
                        var catSize = g.MeasureString(seg.Text, badgeFont);
                        itemWidth = catSize.Width + 24f;
                        totalContentWidth += itemWidth + badgeGap;
                    }
                    else
                    {
                        var txtSize = g.MeasureString(seg.Text, font);
                        itemWidth = txtSize.Width;
                        totalContentWidth += itemWidth + sepWidth;
                    }
                    measured.Add((seg, itemWidth));
                }

                var gap = (float)Math.Max(30, layer.TickerGap);

                var totalTravelSpan = Math.Max(1.0f, contentRect.Width + totalContentWidth);
                var travelled = (float)(newsElapsed * speed);
                if (travelled >= totalTravelSpan)
                {
                    // Category crawl reached end position - complete
                    g.ResetClip();
                    return;
                }

                var startX = isRight
                    ? (contentRect.Left - totalContentWidth + travelled)
                    : (contentRect.Right - travelled);

                DrawTickerStream(g, contentRect, font, badgeFont, brush, badgeTextBrush, badgeBgBrush, sepLogo, measured, hasSepLogo, logoSize, logoPad, separatorText, sepTextSize, gap, startX, isRight);
            }
        }
        else
        {
            // Static text ticker fallback
            var segments = ParseTickerSegments(raw, layer);
            if (segments.Count == 0) segments.Add(new TickerSegment("", raw, false));

            var measured = new List<(TickerSegment Seg, float Width)>();
            float totalContentWidth = 0;
            for (var i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];
                float itemWidth;
                if (seg.IsCategoryBadge)
                {
                    var catSize = g.MeasureString(seg.Text, badgeFont);
                    itemWidth = catSize.Width + 24f;
                    totalContentWidth += itemWidth + badgeGap;
                }
                else
                {
                    var txtSize = g.MeasureString(seg.Text, font);
                    itemWidth = txtSize.Width;
                    totalContentWidth += itemWidth + sepWidth;
                }
                measured.Add((seg, itemWidth));
            }

            var gap = (float)Math.Max(30, layer.TickerGap);
            totalContentWidth += gap;

            if (string.Equals(layer.TickerMode, "Push", StringComparison.OrdinalIgnoreCase))
            {
                DrawPushTicker(g, rect, layer, elapsed, speed, font, badgeFont, brush, badgeTextBrush, badgeBgBrush, segments);
            }
            else
            {
                // The first item must originate completely outside the clipping window.
                // Previously the repeat copy was drawn inside at t=0, making Prime and
                // other crawls appear fully populated instead of entering from off-screen.
                var distance = (float)(elapsed * speed);
                var repeatSpan = Math.Max(1f, totalContentWidth);
                var repeating = layer.TickerRepeat && distance >= repeatSpan;
                var phase = repeating ? (distance - repeatSpan) % repeatSpan : distance;
                var startX = isRight
                    ? (repeating ? rect.Left + phase : rect.Left - totalContentWidth + distance)
                    : (repeating ? rect.Right - totalContentWidth - phase : rect.Right - distance);

                DrawTickerStream(g, rect, font, badgeFont, brush, badgeTextBrush, badgeBgBrush, sepLogo, measured, hasSepLogo, logoSize, logoPad, separatorText, sepTextSize, gap, startX, isRight);

                if (repeating)
                {
                    if (isRight)
                    {
                        if (startX + totalContentWidth < rect.Right)
                            DrawTickerStream(g, rect, font, badgeFont, brush, badgeTextBrush, badgeBgBrush, sepLogo, measured, hasSepLogo, logoSize, logoPad, separatorText, sepTextSize, gap, startX + totalContentWidth, isRight);
                        if (startX > rect.Left)
                            DrawTickerStream(g, rect, font, badgeFont, brush, badgeTextBrush, badgeBgBrush, sepLogo, measured, hasSepLogo, logoSize, logoPad, separatorText, sepTextSize, gap, startX - totalContentWidth, isRight);
                    }
                    else
                    {
                        if (startX > rect.Left)
                            DrawTickerStream(g, rect, font, badgeFont, brush, badgeTextBrush, badgeBgBrush, sepLogo, measured, hasSepLogo, logoSize, logoPad, separatorText, sepTextSize, gap, startX - totalContentWidth, isRight);
                        if (startX + totalContentWidth < rect.Right)
                            DrawTickerStream(g, rect, font, badgeFont, brush, badgeTextBrush, badgeBgBrush, sepLogo, measured, hasSepLogo, logoSize, logoPad, separatorText, sepTextSize, gap, startX + totalContentWidth, isRight);
                    }
                }
            }
        }

        g.ResetClip();
    }

    private static void DrawPushTicker(
        Graphics g,
        RectangleF rect,
        CgLayer layer,
        double elapsed,
        double speed,
        Font font,
        Font badgeFont,
        Brush brush,
        Brush badgeTextBrush,
        Brush badgeBgBrush,
        List<TickerSegment> segments,
        bool gapless = false)
    {
        if (segments.Count == 0) return;

        var hold = layer.DataItemDurationSeconds > 0.05
            ? layer.DataItemDurationSeconds
            : Math.Max(1.5, 360.0 / Math.Max(20, speed));

        var gapSec = 0.0;
        if (!gapless && layer.TickerGap > 0 && layer.TickerGap <= 10.0) gapSec = layer.TickerGap;
        else if (!gapless && layer.TickerGap > 10.0) gapSec = layer.TickerGap / 1000.0;

        var itemTotalTime = Math.Max(0.1, hold + gapSec);
        var count = segments.Count;
        var index = (int)Math.Floor(elapsed / itemTotalTime) % count;
        var itemElapsed = elapsed % itemTotalTime;
        var next = (index + 1) % count;

        var transDur = Math.Min(0.6, hold * 0.28);
        var transStart = Math.Max(0.0, hold - transDur);
        double p = 0.0;
        if (itemElapsed >= hold)
        {
            p = 1.0;
        }
        else if (itemElapsed >= transStart)
        {
            p = SmoothStep((itemElapsed - transStart) / Math.Max(0.001, transDur));
        }

        var fontH = font.GetHeight(g);
        var centerY = rect.Top + rect.Height / 2f;
        var textY = centerY - fontH / 2f;

        void DrawPushSegment(TickerSegment seg, float drawX, float drawY)
        {
            if (seg.IsCategoryBadge)
            {
                var badgeH = Math.Min(rect.Height - 12f, fontH + 8f);
                var badgeY = centerY - badgeH / 2f;
                var catSize = g.MeasureString(seg.Text, badgeFont);
                var badgeW = catSize.Width + 24f;
                var badgeRect = new RectangleF(drawX, badgeY, badgeW, badgeH);
                using var path = new GraphicsPath();
                var r = 6f;
                path.AddArc(badgeRect.Left, badgeRect.Top, r * 2, r * 2, 180, 90);
                path.AddArc(badgeRect.Right - r * 2, badgeRect.Top, r * 2, r * 2, 270, 90);
                path.AddArc(badgeRect.Right - r * 2, badgeRect.Bottom - r * 2, r * 2, r * 2, 0, 90);
                path.AddArc(badgeRect.Left, badgeRect.Bottom - r * 2, r * 2, r * 2, 90, 90);
                path.CloseFigure();
                g.FillPath(badgeBgBrush, path);
                using var badgeFormat = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(seg.Text, badgeFont, badgeTextBrush, badgeRect, badgeFormat);
            }
            else
            {
                g.DrawString(seg.Text, font, brush, drawX, drawY);
            }
        }

        var dir = (layer.TickerDirection ?? "Left").Trim().ToUpperInvariant();
        if (elapsed < transDur)
        {
            var enter = SmoothStep(elapsed / Math.Max(.001, transDur));
            switch (dir)
            {
                case "UP": DrawPushSegment(segments[index], rect.Left + 10f, textY + (float)((1 - enter) * rect.Height)); break;
                case "DOWN": DrawPushSegment(segments[index], rect.Left + 10f, textY - (float)((1 - enter) * rect.Height)); break;
                case "RIGHT": DrawPushSegment(segments[index], rect.Left - (float)((1 - enter) * rect.Width), textY); break;
                default: DrawPushSegment(segments[index], rect.Left + (float)((1 - enter) * rect.Width), textY); break;
            }
            return;
        }
        switch (dir)
        {
            case "UP":
            {
                var outY = textY - (float)(p * rect.Height);
                var inY = textY + (float)((1.0 - p) * rect.Height);
                DrawPushSegment(segments[index], rect.Left + 10f, outY);
                if (p > 0.001) DrawPushSegment(segments[next], rect.Left + 10f, inY);
                break;
            }
            case "DOWN":
            {
                var outY = textY + (float)(p * rect.Height);
                var inY = textY - (float)((1.0 - p) * rect.Height);
                DrawPushSegment(segments[index], rect.Left + 10f, outY);
                if (p > 0.001) DrawPushSegment(segments[next], rect.Left + 10f, inY);
                break;
            }
            case "RIGHT":
            {
                var outX = rect.Left + (float)(p * rect.Width);
                var inX = rect.Left - (float)((1.0 - p) * rect.Width);
                DrawPushSegment(segments[index], outX, textY);
                if (p > 0.001) DrawPushSegment(segments[next], inX, textY);
                break;
            }
            case "LEFT":
            default:
            {
                var outX = rect.Left - (float)(p * rect.Width);
                var inX = rect.Left + (float)((1.0 - p) * rect.Width);
                DrawPushSegment(segments[index], outX, textY);
                if (p > 0.001) DrawPushSegment(segments[next], inX, textY);
                break;
            }
        }
    }

    private static void DrawTickerStream(Graphics g, RectangleF rect, Font font, Font badgeFont, Brush textBrush, Brush badgeTextBrush, Brush badgeBgBrush, Image? sepLogo, List<(TickerSegment Seg, float Width)> measured, bool hasSepLogo, float logoSize, float logoPad, string separatorText, SizeF sepTextSize, float gap, float startX, bool isRight)
    {
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        var curX = startX;
        var centerY = rect.Top + rect.Height / 2f;
        var fontH = font.GetHeight(g);
        var textY = centerY - fontH / 2f;
        const float badgeGap = 16f;

        for (var i = 0; i < measured.Count; i++)
        {
            var (seg, itemWidth) = measured[i];

            if (curX + itemWidth >= rect.Left - 60 && curX <= rect.Right + 60)
            {
                if (seg.IsCategoryBadge)
                {
                    var badgeH = Math.Min(rect.Height - 12f, fontH + 8f);
                    var badgeY = centerY - badgeH / 2f;
                    var badgeRect = new RectangleF(curX, badgeY, itemWidth, badgeH);

                    // Draw rounded badge background
                    using var path = new GraphicsPath();
                    var r = 6f;
                    path.AddArc(badgeRect.Left, badgeRect.Top, r * 2, r * 2, 180, 90);
                    path.AddArc(badgeRect.Right - r * 2, badgeRect.Top, r * 2, r * 2, 270, 90);
                    path.AddArc(badgeRect.Right - r * 2, badgeRect.Bottom - r * 2, r * 2, r * 2, 0, 90);
                    path.AddArc(badgeRect.Left, badgeRect.Bottom - r * 2, r * 2, r * 2, 90, 90);
                    path.CloseFigure();
                    g.FillPath(badgeBgBrush, path);

                    using var badgeFormat = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString(seg.Text, badgeFont, badgeTextBrush, badgeRect, badgeFormat);
                }
                else
                {
                    g.DrawString(seg.Text, font, textBrush, curX, textY, StringFormat.GenericTypographic);
                }
            }

            curX += itemWidth;

            // Spacing after badge OR separator after text
            if (seg.IsCategoryBadge)
            {
                curX += badgeGap;
            }
            else
            {
                if (hasSepLogo && sepLogo != null)
                {
                    curX += logoPad;
                    if (curX + logoSize >= rect.Left - 20 && curX <= rect.Right + 20)
                    {
                        var logoY = centerY - logoSize / 2f;
                        g.DrawImage(sepLogo, curX, logoY, logoSize, logoSize);
                    }
                    curX += logoSize + logoPad;
                }
                else
                {
                    if (curX + sepTextSize.Width >= rect.Left - 20 && curX <= rect.Right + 20)
                    {
                        g.DrawString(separatorText, font, textBrush, curX, textY, StringFormat.GenericTypographic);
                    }
                    curX += sepTextSize.Width;
                }
            }
        }
    }

    private static void DrawRoll(Graphics g, RectangleF rect, CgLayer layer, double t, double opacity, string? resolvedText = null)
    {
        if (layer.UseBackground)
        {
            using var bg = new SolidBrush(ParseColor(layer.Background, opacity));
            if (bg.Color.A > 0) g.FillRectangle(bg, rect);
        }
        using var brush = new SolidBrush(ParseColor(layer.Fill, opacity));
        using var font = CreateFont(layer.FontFamily, (float)Math.Max(10, layer.FontSize * rect.Height / Math.Max(1, layer.Height)), FontStyle.Regular);
        var text = string.IsNullOrWhiteSpace(resolvedText) ? (layer.Text ?? "") : resolvedText;
        var size = g.MeasureString(text, font, (int)rect.Width);
        var travelled = ((t - layer.StartSeconds) * Math.Max(10, layer.Speed)) % (rect.Height + size.Height);
        g.SetClip(rect);
        g.DrawString(text, font, brush, new RectangleF(rect.Left + 12, rect.Bottom - (float)travelled, rect.Width - 24, Math.Max(size.Height, rect.Height)), new StringFormat());
        g.ResetClip();
    }

    private static void DrawWeatherIcon(Graphics g, RectangleF rect, string? code, CgLayer layer, double t, double opacity)
    {
        code = (code ?? "na").Trim().ToLowerInvariant();
        var local = Math.Max(0, t - layer.StartSeconds);
        var cx = rect.Left + rect.Width / 2f;
        var cy = rect.Top + rect.Height / 2f;
        var scale = Math.Min(rect.Width, rect.Height) / 180f;
        using var state = new GraphicsStateScope(g);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Color C(string hex, double a = 1) => ParseColor(hex, opacity * a);
        void Cloud(float ox, float oy, double alpha = 1)
        {
            using var cloud = new SolidBrush(C("#FFFFFFFF", alpha));
            g.FillEllipse(cloud, cx - 58*scale + ox, cy - 16*scale + oy, 76*scale, 52*scale);
            g.FillEllipse(cloud, cx - 20*scale + ox, cy - 42*scale + oy, 72*scale, 74*scale);
            g.FillEllipse(cloud, cx + 18*scale + ox, cy - 12*scale + oy, 70*scale, 50*scale);
            g.FillRectangle(cloud, cx - 52*scale + ox, cy + 4*scale + oy, 128*scale, 30*scale);
        }
        void Sun(float ox, float oy, double alpha = 1)
        {
            var radius = 30*scale;
            using var sun = new SolidBrush(C("#FFFFC83D", alpha));
            using var pen = new Pen(C("#FFFFD85C", alpha), Math.Max(1f, 4*scale));
            g.FillEllipse(sun, cx - radius + ox, cy - radius + oy, radius*2, radius*2);
            var rot = local * 28.0 * Math.PI / 180.0;
            for (var i=0;i<8;i++)
            {
                var a = rot + i * Math.PI / 4;
                var r1=44*scale; var r2=62*scale;
                g.DrawLine(pen, cx + ox + (float)Math.Cos(a)*r1, cy + oy + (float)Math.Sin(a)*r1,
                                cx + ox + (float)Math.Cos(a)*r2, cy + oy + (float)Math.Sin(a)*r2);
            }
        }
        void Moon(float ox, float oy, double alpha = 1)
        {
            using var moon = new SolidBrush(C("#FFFFE8A3", alpha));
            var radius = 34 * scale;
            using var crescent = new GraphicsPath(FillMode.Alternate);
            crescent.AddEllipse(cx - radius + ox, cy - radius + oy, radius * 2, radius * 2);
            crescent.AddEllipse(cx - 9 * scale + ox, cy - 42 * scale + oy, 64 * scale, 64 * scale);
            g.FillPath(moon, crescent);
        }

        var prefix = code.Length >= 2 ? code[..2] : code;
        var bob = (float)(Math.Sin(local * 2.2) * 3.5 * scale);
        switch (prefix)
        {
            case "01":
                if (code.EndsWith('n')) Moon(0, bob); else Sun(0, bob);
                break;
            case "02":
                if (code.EndsWith('n')) Moon(-34*scale, -24*scale + bob, .9); else Sun(-34*scale, -24*scale + bob, .9);
                Cloud(18*scale, 12*scale + bob);
                break;
            case "03":
            case "04": Cloud(0, bob); break;
            case "09":
            case "10":
                if (prefix == "10") Sun(-36*scale, -28*scale + bob, .75);
                Cloud(12*scale, bob);
                using (var rain = new Pen(C("#FF5CCBFF"), Math.Max(2f, 5*scale)))
                {
                    for (var i=0;i<4;i++)
                    {
                        var phase = (float)((local*44 + i*25) % 38) * scale;
                        var x = cx - 48*scale + i*32*scale;
                        var y = cy + 38*scale + phase;
                        g.DrawLine(rain, x, y, x-7*scale, y+18*scale);
                    }
                }
                break;
            case "11":
                Cloud(0, bob);
                using (var bolt = new SolidBrush(C(local % .8 < .22 ? "#FFFFFFFF" : "#FFFFD54A")))
                {
                    var pts = new[] { new PointF(cx+2*scale,cy+24*scale),new PointF(cx-18*scale,cy+66*scale),new PointF(cx+2*scale,cy+60*scale),new PointF(cx-8*scale,cy+94*scale),new PointF(cx+32*scale,cy+48*scale),new PointF(cx+10*scale,cy+52*scale) };
                    g.FillPolygon(bolt, pts);
                }
                break;
            case "13":
                Cloud(0, bob);
                using (var snow = new SolidBrush(C("#FFDFF7FF")))
                {
                    for (var i=0;i<6;i++)
                    {
                        var phase = (float)((local*24 + i*17) % 48) * scale;
                        var x = cx - 58*scale + i*24*scale + (float)Math.Sin(local*1.8+i)*5*scale;
                        var y = cy + 34*scale + phase;
                        g.FillEllipse(snow, x, y, 7*scale, 7*scale);
                    }
                }
                break;
            case "50":
                using (var mist = new Pen(C("#FFD7E2EA"), Math.Max(2f, 5*scale)))
                {
                    for (var i=0;i<4;i++)
                    {
                        var slide = (float)(Math.Sin(local*1.4+i)*10*scale);
                        var y = cy - 36*scale + i*25*scale;
                        g.DrawLine(mist, cx-65*scale+slide, y, cx+65*scale+slide, y);
                    }
                }
                break;
            default:
                using (var pen = new Pen(C("#FFB7C6D1"), Math.Max(2f, 4*scale)))
                    g.DrawEllipse(pen, cx-50*scale, cy-50*scale, 100*scale, 100*scale);
                using (var font = CreateFont(layer.FontFamily, Math.Max(12f, 42*scale), FontStyle.Bold))
                using (var brush = new SolidBrush(C("#FFFFFFFF")))
                using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("?", font, brush, rect, fmt);
                break;
        }
    }

    private static void DrawTimer(Graphics g, RectangleF rect, CgLayer layer, double t, double opacity, CgAnimationEngine.TextVisualState? activeTextVisual)
    {
        double seconds;
        var elapsed = Math.Max(0, t - layer.StartSeconds);
        var mode = layer.TimerMode ?? string.Empty;
        var type = layer.Type ?? string.Empty;

        if (mode.Equals("CommercialBackIn", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("BACKIN", StringComparison.OrdinalIgnoreCase))
        {
            var breakRemaining = CgPlaybackRuntime.RemainingCommercialBreakSeconds;
            if (breakRemaining > 0)
            {
                seconds = breakRemaining;
            }
            else if (layer.TimerStartSeconds > 0)
            {
                // Standalone / Preview fallback
                seconds = Math.Max(0, layer.TimerStartSeconds - elapsed);
            }
            else
            {
                seconds = 0;
            }
        }
        else if (mode.Equals("CountUp", StringComparison.OrdinalIgnoreCase) ||
                 type.Equals("COUNTUP", StringComparison.OrdinalIgnoreCase))
        {
            seconds = layer.TimerStartSeconds + elapsed;
        }
        else // CountDown / Decrement
        {
            var start = layer.TimerStartSeconds > 0 ? layer.TimerStartSeconds : (layer.DurationSeconds > 0 ? layer.DurationSeconds : 60);
            seconds = Math.Max(0, start - elapsed);
        }

        var prefix = layer.TimerPrefix;
        if (string.IsNullOrEmpty(prefix) && (type.Equals("BACKIN", StringComparison.OrdinalIgnoreCase) || mode.Equals("CommercialBackIn", StringComparison.OrdinalIgnoreCase)))
        {
            prefix = "BACK IN ";
        }
        var text = CgPlaybackRuntime.FormatTimer(seconds, layer.TimerFormat, prefix, layer.TimerSuffix);
        DrawText(g, rect, text, layer, opacity, activeTextVisual);
    }

    private static void DrawAnalogClock(Graphics g, RectangleF rect, CgLayer layer, double opacity)
    {
        var center = new PointF(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
        var radius = Math.Min(rect.Width, rect.Height) / 2 - 5;
        using var bg = new SolidBrush(ParseColor(layer.Background, opacity));
        using var pen = new Pen(ParseColor(layer.Fill, opacity), Math.Max(1.5f, radius / 35));
        g.FillEllipse(bg, center.X - radius, center.Y - radius, radius * 2, radius * 2);
        g.DrawEllipse(pen, center.X - radius, center.Y - radius, radius * 2, radius * 2);
        var now = CgClockTime.Now(layer);
        DrawHand(g, pen, center, radius * .5f, (now.Hour % 12 + now.Minute / 60f) * 30);
        DrawHand(g, pen, center, radius * .72f, now.Minute * 6);
        using var secondPen = new Pen(Color.FromArgb((int)(255 * opacity), 255, 68, 61), Math.Max(1, radius / 60));
        DrawHand(g, secondPen, center, radius * .78f, now.Second * 6);
    }

    private static void DrawHand(Graphics g, Pen pen, PointF center, float length, float degrees)
    {
        var radians = (degrees - 90) * Math.PI / 180.0;
        g.DrawLine(pen, center, new PointF(center.X + (float)Math.Cos(radians) * length, center.Y + (float)Math.Sin(radians) * length));
    }

    public VideoFrameData? GetVideoLayerFrame(CgLayer layer, double localSeconds)
    {
        var sourceKind = string.IsNullOrWhiteSpace(layer.VideoSourceKind) ? "File" : layer.VideoSourceKind.Trim();
        var fileSource = sourceKind.Equals("File", StringComparison.OrdinalIgnoreCase);
        var runtimeReady = sourceKind.Equals("NDI", StringComparison.OrdinalIgnoreCase) || FfmpegRuntime.IsInitialized;
        var sourcePresent = sourceKind.Equals("Screen", StringComparison.OrdinalIgnoreCase)
            || (sourceKind.Equals("DirectShow", StringComparison.OrdinalIgnoreCase) && (!string.IsNullOrWhiteSpace(layer.VideoDevice) || !string.IsNullOrWhiteSpace(layer.Source)))
            || !string.IsNullOrWhiteSpace(layer.Source);
        if (!sourcePresent || (fileSource && !File.Exists(layer.Source)) || !runtimeReady) return null;

        try
        {
            var item = BuildVideoSourceItem(layer);
            var signature = VideoSourceSignature(layer);
            if (!_videoLayers.TryGetValue(layer.Id, out var state) || !string.Equals(state.Signature, signature, StringComparison.Ordinal))
            {
                state?.Dispose();
                state = new VideoLayerState(item, signature);
                _videoLayers[layer.Id] = state;
            }
            return state.GetFrame(Math.Max(0, localSeconds));
        }
        catch
        {
            return null;
        }
    }

    private void DrawVideo(Graphics g, RectangleF rect, CgLayer layer, double localSeconds, double opacity)
    {
        var frame = GetVideoLayerFrame(layer, localSeconds);
        if (frame is null)
        {
            DrawMediaPlaceholder(g, rect, "VIDEO", layer, opacity);
            return;
        }

        var handle = GCHandle.Alloc(frame.Bgra, GCHandleType.Pinned);
        try
        {
            using var bitmap = new Bitmap(frame.Width, frame.Height, frame.Stride, PixelFormat.Format32bppArgb, handle.AddrOfPinnedObject());
            var matrix = new ColorMatrix { Matrix33 = (float)opacity };
            using var attrs = new ImageAttributes();
            attrs.SetColorMatrix(matrix);
            g.DrawImage(bitmap, Rectangle.Round(rect), 0, 0, bitmap.Width, bitmap.Height, GraphicsUnit.Pixel, attrs);
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>
    /// Thin track with a square knob at the current item's position. Fill = track colour,
    /// Background = knob colour, BorderColor/BorderWidth = knob outline.
    /// </summary>
    private static void DrawProgress(Graphics g, RectangleF rect, CgLayer layer, double timelineSeconds, double opacity)
    {
        var progress = (float)NowNextRuntime.Progress(timelineSeconds);
        var track = Math.Max(1.5f, rect.Height * .16f);
        var middle = rect.Top + rect.Height / 2f;
        using (var trackBrush = new SolidBrush(ParseColor(layer.Fill, opacity)))
            g.FillRectangle(trackBrush, rect.Left, middle - track / 2f, rect.Width, track);

        var knobWidth = Math.Max(4f, rect.Height * .8f);
        var knob = new RectangleF(rect.Left + progress * Math.Max(0, rect.Width - knobWidth), rect.Top, knobWidth, rect.Height);
        using (var knobBrush = new SolidBrush(ParseColor(layer.Background, opacity)))
            g.FillRectangle(knobBrush, knob);
        if (layer.BorderWidth > 0)
        {
            using var pen = new Pen(ParseColor(layer.BorderColor, opacity), (float)layer.BorderWidth);
            g.DrawRectangle(pen, knob.X, knob.Y, knob.Width, knob.Height);
        }
    }

    private readonly Dictionary<Guid, (AiringInfo Now, UpNextBoxState? Box, RequestFormat Requests, string Template, List<string> Lines)> _creditLines = [];

    /// <summary>
    /// Credit lines that roll up one line at a time, as many as fit the layer height (two in the package).
    /// Text holds one template line per row ("Lyrics : {NowLyrics}"); lines without a value are skipped.
    /// DataItemDurationSeconds is the time each position holds. Lines are resolved once per song, not per frame.
    /// </summary>
    private void DrawCredits(Graphics g, RectangleF rect, CgLayer layer, double timelineSeconds, double opacity, double sy)
    {
        var now = NowNextRuntime.Now;
        var box = NowNextRuntime.Box;
        var requests = NowNextRuntime.Requests;
        var template = layer.Text ?? string.Empty;
        if (!_creditLines.TryGetValue(layer.Id, out var cached) || !ReferenceEquals(cached.Now, now) || !ReferenceEquals(cached.Box, box)
            || !ReferenceEquals(cached.Requests, requests) || !string.Equals(cached.Template, template, StringComparison.Ordinal))
        {
            if (_creditLines.Count > 64) _creditLines.Clear();
            cached = (now, box, requests, template, NowNextRuntime.CreditLines(template));
            _creditLines[layer.Id] = cached;
        }
        var lines = cached.Lines;
        if (lines.Count == 0) return;

        var style = FontStyle.Regular;
        if (layer.Bold) style |= FontStyle.Bold;
        if (layer.Italic) style |= FontStyle.Italic;
        var effectiveSy = sy > 0.05 ? sy : 1.0;
        var fontScale = rect.Height / Math.Max(1.0, layer.Height * effectiveSy);
        using var font = CreateFont(layer.FontFamily, (float)Math.Max(6, layer.FontSize * effectiveSy * fontScale), style);
        var lineHeight = font.GetHeight(g) * (float)Math.Max(.6, layer.LineSpacing);
        var visible = Math.Max(1, (int)Math.Floor(rect.Height / lineHeight + .05));

        // Roll: hold each position, then slide up by one line. Static when everything fits.
        var first = 0;
        var offset = 0f;
        if (lines.Count > visible)
        {
            var hold = Math.Max(1.0, layer.DataItemDurationSeconds);
            const double slide = .45;
            var step = (long)Math.Floor(Math.Max(0, timelineSeconds) / hold);
            var phase = Math.Max(0, timelineSeconds) - step * hold;
            first = (int)(step % lines.Count);
            if (phase > hold - slide) offset = (float)SmoothStep((phase - (hold - slide)) / slide);
        }

        using var brush = new SolidBrush(ParseColor(layer.Fill, opacity));
        using var shadow = layer.ShadowEnabled ? new SolidBrush(ParseColor(layer.ShadowColor, opacity)) : null;
        using var format = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter };
        var state = g.Save();
        try
        {
            // Clip to whole lines so the next line never peeks in below the last visible one.
            g.SetClip(new RectangleF(rect.Left - 4, rect.Top, rect.Width + 8, Math.Min(rect.Height, visible * lineHeight)));
            var rows = lines.Count > visible ? visible + 1 : lines.Count;
            for (var i = 0; i < rows; i++)
            {
                var text = lines[(first + i) % lines.Count];
                var row = new RectangleF(rect.Left, rect.Top + (i - offset) * lineHeight, rect.Width, lineHeight);
                if (shadow is not null)
                    g.DrawString(text, font, shadow, new RectangleF(row.X + (float)layer.ShadowOffsetX, row.Y + (float)layer.ShadowOffsetY, row.Width, row.Height), format);
                g.DrawString(text, font, brush, row, format);
            }
        }
        finally { g.Restore(state); }
    }

    private readonly Dictionary<Guid, (PreviewVideoSource Source, long LastUsed)> _previews = [];
    private long _lastPreviewSweep;

    /// <summary>
    /// The NEXT / LATER window. Plays the upcoming airing (NowNextRuntime.Box) muted, from its preview start
    /// point, decoded at about the window size on a background worker. Background colour shows until the
    /// first frame arrives. Live feeds, items without a file, or video switched off get a title card
    /// (poster, or the type in large letters). Without playout data the layer's Source file is used.
    /// </summary>
    private void DrawUpNextVideo(Graphics g, RectangleF rect, CgLayer layer, double opacity)
    {
        using (var fill = new SolidBrush(ParseColor(layer.Background, opacity)))
            if (fill.Color.A > 0) g.FillRectangle(fill, rect);

        var box = NowNextRuntime.Box;
        var info = box?.Info;
        var path = !string.IsNullOrWhiteSpace(info?.FilePath) ? info.FilePath : layer.Source;
        var start = box?.PreviewStartSeconds ?? 0;
        if (info is not null && (info.IsLive || !box!.ShowVideo || string.IsNullOrWhiteSpace(info.FilePath)))
        {
            DrawTitleCard(g, rect, layer, info, opacity);
            return;
        }
        if (string.IsNullOrWhiteSpace(path) || path.Contains("://", StringComparison.Ordinal)) return;

        var now = Environment.TickCount64;
        if (!_previews.TryGetValue(layer.Id, out var entry) || !string.Equals(entry.Source.Path, path, StringComparison.OrdinalIgnoreCase) || Math.Abs(entry.Source.StartSeconds - start) > .5)
        {
            entry.Source?.Dispose();
            _previews.Remove(layer.Id);
            if (!File.Exists(path)) return;
            var width = Math.Clamp(((int)Math.Ceiling(rect.Width / 64.0) + 1) * 64, 128, 960);
            entry = (new PreviewVideoSource(path, start, width), now);
        }
        _previews[layer.Id] = (entry.Source, now);

        if (entry.Source.Latest is not { } frame) return;
        // Cover the window: crop the source to the window's aspect ratio instead of stretching it.
        var srcAspect = frame.Width / (double)Math.Max(1, frame.Height);
        var dstAspect = rect.Width / Math.Max(1.0, rect.Height);
        var crop = srcAspect > dstAspect
            ? new RectangleF((float)((frame.Width - frame.Height * dstAspect) / 2), 0, (float)(frame.Height * dstAspect), frame.Height)
            : new RectangleF(0, (float)((frame.Height - frame.Width / dstAspect) / 2), frame.Width, (float)(frame.Width / dstAspect));
        var handle = GCHandle.Alloc(frame.Bgra, GCHandleType.Pinned);
        try
        {
            using var bitmap = new Bitmap(frame.Width, frame.Height, frame.Stride, PixelFormat.Format32bppArgb, handle.AddrOfPinnedObject());
            using var attrs = new ImageAttributes();
            attrs.SetColorMatrix(new ColorMatrix { Matrix33 = (float)Math.Clamp(opacity, 0, 1) });
            g.DrawImage(bitmap, Rectangle.Round(rect), crop.X, crop.Y, crop.Width, crop.Height, GraphicsUnit.Pixel, attrs);
        }
        finally { handle.Free(); }
    }

    // One poster at a time: replaced (and disposed) when the box shows another airing, so a channel
    // with hundreds of movie posters never keeps more than one in memory.
    private (string Path, Image? Image) _poster = (string.Empty, null);

    /// <summary>Box content without video: the poster when there is one, else the type ("LIVE", "NEWS") in large letters.</summary>
    private void DrawTitleCard(Graphics g, RectangleF rect, CgLayer layer, AiringInfo info, double opacity)
    {
        if (!string.IsNullOrWhiteSpace(info.PosterPath))
        {
            if (!string.Equals(_poster.Path, info.PosterPath, StringComparison.OrdinalIgnoreCase))
            {
                _poster.Image?.Dispose();
                Image? loaded = null;
                try
                {
                    if (File.Exists(info.PosterPath))
                    {
                        using var file = Image.FromFile(info.PosterPath);
                        loaded = new Bitmap(file, (int)Math.Max(16, rect.Width), (int)Math.Max(16, rect.Height));
                    }
                }
                catch { loaded = null; }
                _poster = (info.PosterPath, loaded);
            }
            if (_poster.Image is { } poster)
            {
                using var attrs = new ImageAttributes();
                attrs.SetColorMatrix(new ColorMatrix { Matrix33 = (float)Math.Clamp(opacity, 0, 1) });
                g.DrawImage(poster, Rectangle.Round(rect), 0, 0, poster.Width, poster.Height, GraphicsUnit.Pixel, attrs);
                return;
            }
        }
        var label = string.IsNullOrWhiteSpace(info.Kind) ? "COMING UP" : info.Kind;
        using var font = CreateFont(layer.FontFamily, Math.Max(12f, rect.Height * .24f), FontStyle.Bold);
        using var brush = new SolidBrush(ParseColor("#E6FFFFFF", opacity));
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        // Upper part of the window; the caption shade and title sit in the lower part.
        g.DrawString(label, font, brush, new RectangleF(rect.X, rect.Y, rect.Width, rect.Height * .62f), format);
    }

    /// <summary>Closes preview decoders whose window has not been drawn for a moment (box went off air).</summary>
    private void SweepIdlePreviews()
    {
        if (_previews.Count == 0) return;
        var now = Environment.TickCount64;
        if (now - _lastPreviewSweep < 1000) return;
        _lastPreviewSweep = now;
        foreach (var (id, entry) in _previews.Where(x => now - x.Value.LastUsed > 1500).ToArray())
        {
            entry.Source.Dispose();
            _previews.Remove(id);
        }
    }

    private static PlaylistItem BuildVideoSourceItem(CgLayer layer)
    {
        var kind=string.IsNullOrWhiteSpace(layer.VideoSourceKind)?"File":layer.VideoSourceKind.Trim();
        return new PlaylistItem
        {
            Title=layer.Name, EventType=layer.VideoIsLiveSource?"LIVE":"CLIP", SourceKind=kind, FilePath=layer.Source??string.Empty,
            InputFormat=layer.VideoInputFormat??string.Empty, InputOptions=layer.VideoInputOptions??string.Empty,
            VideoDevice=string.IsNullOrWhiteSpace(layer.VideoDevice)&&kind.Equals("DirectShow",StringComparison.OrdinalIgnoreCase)?(layer.Source??string.Empty):(layer.VideoDevice??string.Empty),
            AudioDevice=layer.AudioDevice??string.Empty, AlternateAudioUrl=layer.AlternateAudioUrl??string.Empty,
            IsLiveSource=layer.VideoIsLiveSource || kind.Equals("NDI",StringComparison.OrdinalIgnoreCase) || kind.Equals("DirectShow",StringComparison.OrdinalIgnoreCase) || kind.Equals("Screen",StringComparison.OrdinalIgnoreCase) || kind.Equals("Custom",StringComparison.OrdinalIgnoreCase),
            CaptureWidth=Math.Max(160,layer.VideoCaptureWidth), CaptureHeight=Math.Max(90,layer.VideoCaptureHeight),
            SourceFrameRate=layer.VideoSourceFrameRate>0?layer.VideoSourceFrameRate:25
        };
    }

    private static string VideoSourceSignature(CgLayer layer) => string.Join("|",layer.VideoSourceKind,layer.Source,layer.VideoInputFormat,layer.VideoInputOptions,layer.VideoDevice,layer.AudioDevice,layer.AlternateAudioUrl,layer.VideoIsLiveSource,layer.VideoCaptureWidth,layer.VideoCaptureHeight,layer.VideoSourceFrameRate);

    private static void DrawMediaPlaceholder(Graphics g, RectangleF rect, string tag, CgLayer layer, double opacity)
    {
        using var bg = new SolidBrush(Color.FromArgb((int)(150 * opacity), 8, 13, 18));
        using var pen = new Pen(Color.FromArgb((int)(220 * opacity), 37, 169, 255), 2);
        g.FillRectangle(bg, rect); g.DrawRectangle(pen, Rectangle.Round(rect));
        var kind=string.IsNullOrWhiteSpace(layer.VideoSourceKind)?"File":layer.VideoSourceKind;
        var label=kind.Equals("File",StringComparison.OrdinalIgnoreCase)?Path.GetFileName(layer.Source):$"{kind}: {layer.Source}";
        DrawText(g, rect, $"{tag}: {label}", layer, opacity);
    }

    private static string ResolveSequenceFrame(string? source, double elapsed)
    {
        if (string.IsNullOrWhiteSpace(source)) return "";
        var directory = Directory.Exists(source) ? source : Path.GetDirectoryName(source);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return source;
        var files = Directory.EnumerateFiles(directory).Where(x =>
            x.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
            x.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
            x.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            x.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase) ||
            x.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ||
            x.EndsWith(".tif", StringComparison.OrdinalIgnoreCase) ||
            x.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0) return "";
        var index = (int)Math.Floor(Math.Max(0, elapsed) * 25) % files.Length;
        return files[index];
    }

    private static string RenderHtmlFallback(CgLayer layer)
    {
        var html = layer.Text;
        if (string.IsNullOrWhiteSpace(html) && !string.IsNullOrWhiteSpace(layer.Source) && File.Exists(layer.Source))
        {
            try { html = File.ReadAllText(layer.Source); } catch { }
        }
        return StripHtml(html);
    }

    private static string StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "HTML LAYER";
        var text = Regex.Replace(html, @"<script[\s\S]*?</script>", " ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<style[\s\S]*?</style>", " ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", " ");
        return System.Net.WebUtility.HtmlDecode(Regex.Replace(text, @"\s+", " ")).Trim();
    }

    private static string FormatDateTime(string? format, DateTime now)
    {
        var f = string.IsNullOrWhiteSpace(format) ? "dd MMM yyyy  HH:mm:ss" : format.Trim();
        try { return now.ToString(f); }
        catch { return now.ToString("dd MMM yyyy  HH:mm:ss"); }
    }

    private static string FormatTimelineTimecode(double seconds)
    {
        seconds = Math.Max(0, seconds);
        var span = TimeSpan.FromSeconds(seconds);
        var frames = (int)Math.Floor((seconds - Math.Floor(seconds)) * 25.0);
        return $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}:{frames:00}";
    }

    private static readonly PrivateFontCollection _privateFonts = InitPrivateFonts();

    private static PrivateFontCollection InitPrivateFonts()
    {
        var pfc = new PrivateFontCollection();
        try
        {
            var fontNames = new[] { "suvadin.ttf", "arab.TTF", "GANESH.TTF" };
            foreach (var fn in fontNames)
            {
                var p = ResolveBundledFontPath(fn);
                if (!string.IsNullOrEmpty(p) && File.Exists(p))
                {
                    pfc.AddFontFile(p);
                }
            }
        }
        catch { }
        return pfc;
    }

    public static string ResolveBundledFontPath(string fontName)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "cgdemo-templates", "prime", "demo", "assets", "fonts", fontName),
            Path.Combine(AppContext.BaseDirectory, "cgdemo-templates", "space4k", "assets", "fonts", fontName),
            Path.Combine(AppContext.BaseDirectory, "demos", "gfx", "prime", "demo", "assets", "fonts", fontName),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cgdemo-templates", "prime", "demo", "assets", "fonts", fontName),
            Path.Combine(Directory.GetCurrentDirectory(), "cgdemo-templates", "prime", "demo", "assets", "fonts", fontName),
            Path.Combine(AppContext.BaseDirectory, fontName)
        };
        return candidates.FirstOrDefault(File.Exists) ?? string.Empty;
    }

    private static Font CreateFont(string? family, float size, FontStyle style)
    {
        var target = string.IsNullOrWhiteSpace(family) ? "Segoe UI" : family.Trim();
        try
        {
            var matched = _privateFonts.Families.FirstOrDefault(f => f.Name.Equals(target, StringComparison.OrdinalIgnoreCase));
            if (matched is not null)
            {
                return new Font(matched, Math.Max(7, size), style, GraphicsUnit.Pixel);
            }
            return new Font(target, Math.Max(7, size), style, GraphicsUnit.Pixel);
        }
        catch
        {
            return new Font("Arial", Math.Max(7, size), style, GraphicsUnit.Pixel);
        }
    }

    private static Color ParseColor(string? value, double opacity)
    {
        try
        {
            value = string.IsNullOrWhiteSpace(value) ? "#FFFFFFFF" : value.Trim();
            if (value.StartsWith('#')) value = value[1..];
            byte a = 255, r, g, b;
            if (value.Length == 8)
            {
                a = Convert.ToByte(value[..2], 16); r = Convert.ToByte(value.Substring(2, 2), 16); g = Convert.ToByte(value.Substring(4, 2), 16); b = Convert.ToByte(value.Substring(6, 2), 16);
            }
            else
            {
                r = Convert.ToByte(value.Substring(0, 2), 16); g = Convert.ToByte(value.Substring(2, 2), 16); b = Convert.ToByte(value.Substring(4, 2), 16);
            }
            a = (byte)Math.Clamp((int)Math.Round(a * opacity), 0, 255);
            return Color.FromArgb(a, r, g, b);
        }
        catch { return Color.FromArgb((int)(255 * opacity), 255, 255, 255); }
    }

    public void Dispose()
    {
        lock (_imageSync)
        {
            foreach (var image in _images.Values) image.Dispose();
            _images.Clear();
        }
        foreach (var state in _videoLayers.Values) state.Dispose();
        _videoLayers.Clear();
        foreach (var entry in _previews.Values) entry.Source.Dispose();
        _previews.Clear();
        _poster.Image?.Dispose();
        _poster = (string.Empty, null);
    }

    private sealed class VideoLayerState : IDisposable
    {
        private VideoDecoder? _decoder;
        private VideoFrameData? _lastFrame;
        private double _lastRequested = -1;

        private readonly PlaylistItem _item;
        private readonly CancellationTokenSource? _liveCts;
        public string Signature { get; }
        public VideoLayerState(PlaylistItem item,string signature)
        {
            _item=item; Signature=signature;
            if(_item.IsLiveSource)
            {
                _liveCts=new CancellationTokenSource();
                _=Task.Run(()=>LiveLoop(_liveCts.Token));
            }
        }

        private void LiveLoop(CancellationToken token)
        {
            while(!token.IsCancellationRequested)
            {
                try
                {
                    using var decoder=new VideoDecoder(_item);
                    while(!token.IsCancellationRequested)
                    {
                        if(decoder.TryRead(out var liveFrame)) _lastFrame=liveFrame;
                        else System.Threading.Thread.Sleep(10);
                    }
                }
                catch
                {
                    if(token.IsCancellationRequested) break;
                    System.Threading.Thread.Sleep(300);
                }
            }
        }

        public VideoFrameData? GetFrame(double seconds)
        {
            // Live NDI/card/screen inputs decode on a worker so a disconnected source cannot
            // stall the Program compositor while the native receiver waits for a frame.
            if(_item.IsLiveSource) return _lastFrame;

            if (_decoder is null || seconds + .08 < _lastRequested || seconds - _lastRequested > 1.5)
            {
                _decoder?.Dispose();
                _decoder = new VideoDecoder(_item);
                _decoder.Seek(TimeSpan.FromSeconds(Math.Max(0, seconds)));
                _lastFrame = null;
            }
            else if (_lastFrame is not null && _lastFrame.PtsSeconds + .025 >= seconds)
            {
                _lastRequested = seconds;
                return _lastFrame;
            }

            _lastRequested = seconds;
            while (_decoder.TryRead(out var candidate))
            {
                _lastFrame = candidate;
                if (candidate.PtsSeconds + .025 >= seconds) break;
            }
            return _lastFrame;
        }

        public void Dispose()
        {
            if(_liveCts is not null) _liveCts.Cancel();
            else _decoder?.Dispose();
        }
    }

    private sealed class GraphicsStateScope : IDisposable
    {
        private readonly Graphics _graphics;
        private readonly GraphicsState _state;
        public GraphicsStateScope(Graphics graphics) { _graphics = graphics; _state = graphics.Save(); }
        public void Dispose() => _graphics.Restore(_state);
    }
}
