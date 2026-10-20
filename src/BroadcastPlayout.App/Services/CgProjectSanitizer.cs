using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using BroadcastPlayout.Models;

namespace BroadcastPlayout.Services;

/// <summary>
/// Repairs persisted/legacy CG values before any WPF brush, canvas or timeline consumes them.
/// This is intentionally model-level so a corrupt saved graphic cannot crash CG Editor startup.
/// </summary>
public static class CgProjectSanitizer
{
    private static double Finite(double value, double fallback = 0) => double.IsFinite(value) ? value : fallback;
    private static double Unit(double value, double fallback = 0) => Math.Clamp(Finite(value, fallback), 0, 1);
    private static double Positive(double value, double fallback = 1) => Math.Max(.000001, Finite(value, fallback));

    public static void Sanitize(CgProject? project)
    {
        if (project is null) return;
        project.Width = Math.Clamp(project.Width <= 0 ? 1920 : project.Width, 16, 16384);
        project.Height = Math.Clamp(project.Height <= 0 ? 1080 : project.Height, 16, 16384);
        if (!double.IsFinite(project.FrameRate)) project.FrameRate = 25;
        if (!double.IsFinite(project.DurationSeconds)) project.DurationSeconds = 10;
        project.FrameRate = Math.Clamp(project.FrameRate, 1, 240);
        project.DurationSeconds = Math.Clamp(project.DurationSeconds, .5, 3600);
        project.Layers ??= [];
        project.Groups ??= [];
        project.DataSources ??= [];
        project.HtmlSources ??= [];
        project.TimelineEvents ??= [];

        RepairBundledDemoAssets(project);

        foreach (var evt in project.TimelineEvents)
        {
            evt.TimeSeconds = Math.Max(0, Finite(evt.TimeSeconds));
            if (string.IsNullOrWhiteSpace(evt.Type)) evt.Type = "Pause";
            if (string.IsNullOrWhiteSpace(evt.ResumeTrigger)) evt.ResumeTrigger = "crawlComplete";
        }

        foreach (var layer in project.Layers)
        {
            layer.X = Finite(layer.X); layer.Y = Finite(layer.Y); layer.Z = Finite(layer.Z);
            if (layer.Width <= 0 || layer.Height <= 0)
            {
                var (nw, nh) = TryResolveNativeImageSize(layer);
                if (nw > 0 && nh > 0)
                {
                    if (layer.Width <= 0) layer.Width = nw;
                    if (layer.Height <= 0) layer.Height = nh;
                }
            }
            layer.Width = Positive(layer.Width, 100); layer.Height = Positive(layer.Height, 100);
            layer.ScaleX = Positive(layer.ScaleX, 1); layer.ScaleY = Positive(layer.ScaleY, 1); layer.ScaleZ = Positive(layer.ScaleZ, 1);
            layer.RotationX = Finite(layer.RotationX); layer.RotationY = Finite(layer.RotationY); layer.Rotation = Finite(layer.Rotation);
            layer.AnchorX = Unit(layer.AnchorX, .5); layer.AnchorY = Unit(layer.AnchorY, .5); layer.AnchorZ = Finite(layer.AnchorZ);
            layer.Opacity = Unit(layer.Opacity, 1);
            layer.StartSeconds = Math.Max(0, Finite(layer.StartSeconds));
            layer.EndSeconds = Math.Max(layer.StartSeconds + .001, Finite(layer.EndSeconds, project.DurationSeconds));
            layer.AnimationInSeconds = Math.Max(.001, Finite(layer.AnimationInSeconds, .5));
            layer.AnimationOutSeconds = Math.Max(.001, Finite(layer.AnimationOutSeconds, .5));
            layer.GradientAngle = Finite(layer.GradientAngle);
            layer.MaskX = Finite(layer.MaskX, layer.X); layer.MaskY = Finite(layer.MaskY, layer.Y);
            layer.MaskWidth = Math.Max(0, Finite(layer.MaskWidth)); layer.MaskHeight = Math.Max(0, Finite(layer.MaskHeight));
            layer.MaskCornerRadius = Math.Max(0, Finite(layer.MaskCornerRadius)); layer.MaskFeather = Math.Max(0, Finite(layer.MaskFeather));
            layer.Perspective = Math.Clamp(Finite(layer.Perspective, 1200), 200, 10000);
            layer.GradientStops ??= [];
            for (var i = 0; i < layer.GradientStops.Count; i++)
            {
                var stop = layer.GradientStops[i];
                if (!double.IsFinite(stop.Offset)) stop.Offset = layer.GradientStops.Count <= 1 ? 0 : i / (double)(layer.GradientStops.Count - 1);
                stop.Offset = Unit(stop.Offset, layer.GradientStops.Count <= 1 ? 0 : i / (double)(layer.GradientStops.Count - 1));
                if (string.IsNullOrWhiteSpace(stop.Color)) stop.Color = "#FFFFFFFF";
            }
            layer.Keyframes ??= [];
            foreach (var key in layer.Keyframes)
            {
                key.TimeSeconds = Math.Max(0, Finite(key.TimeSeconds));
                key.X = Finite(key.X, layer.X); key.Y = Finite(key.Y, layer.Y); key.Z = Finite(key.Z, layer.Z);
                key.Width = Positive(key.Width, layer.Width); key.Height = Positive(key.Height, layer.Height);
                key.ScaleX = Positive(key.ScaleX, 1); key.ScaleY = Positive(key.ScaleY, 1); key.ScaleZ = Positive(key.ScaleZ, 1);
                key.RotationX = Finite(key.RotationX); key.RotationY = Finite(key.RotationY); key.Rotation = Finite(key.Rotation);
                key.AnchorX = Unit(key.AnchorX, .5); key.AnchorY = Unit(key.AnchorY, .5); key.AnchorZ = Finite(key.AnchorZ);
                key.Opacity = Unit(key.Opacity, 1);
                key.SkewX = Finite(key.SkewX); key.SkewY = Finite(key.SkewY); key.BlurRadius = Math.Max(0, Finite(key.BlurRadius));
            }
            if (layer.Precomposition is not null && !ReferenceEquals(layer.Precomposition, project))
                Sanitize(layer.Precomposition);
        }

        foreach (var group in project.Groups)
        {
            group.Opacity = Unit(group.Opacity, 1);
            group.OriginX = Finite(group.OriginX, project.Width / 2.0);
            group.OriginY = Finite(group.OriginY, project.Height / 2.0);
            group.StaggerSeconds = Math.Max(0, Finite(group.StaggerSeconds));
            group.Keyframes ??= [];
            foreach (var key in group.Keyframes)
            {
                key.TimeSeconds = Math.Max(0, Finite(key.TimeSeconds));
                key.X = Finite(key.X); key.Y = Finite(key.Y); key.Z = Finite(key.Z);
                key.Width = Positive(key.Width, 1); key.Height = Positive(key.Height, 1);
                key.ScaleX = Positive(key.ScaleX, 1); key.ScaleY = Positive(key.ScaleY, 1); key.ScaleZ = Positive(key.ScaleZ, 1);
                key.RotationX = Finite(key.RotationX); key.RotationY = Finite(key.RotationY); key.Rotation = Finite(key.Rotation);
                key.AnchorX = Unit(key.AnchorX, .5); key.AnchorY = Unit(key.AnchorY, .5); key.AnchorZ = Finite(key.AnchorZ);
                key.Opacity = Unit(key.Opacity, 1);
                key.SkewX = Finite(key.SkewX); key.SkewY = Finite(key.SkewY); key.BlurRadius = Math.Max(0, Finite(key.BlurRadius));
            }
        }
    }

    private static void RepairBundledDemoAssets(CgProject project)
    {
        // Older saved demo packs could retain a missing absolute path. The generic
        // sequence fallback then found a same-named Space 4K folder, causing the
        // AP1 breaking graphic to render the wrong channel's frames. Canonical AP1
        // demos are always rebound to their own asset family during load/sanitize.
        if (string.Equals(project.Name, CgUniqueDemoFactory.Ap1hdBreakingDemoName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(project.Name, CgUniqueDemoFactory.Ap1hdFlashDemoName, StringComparison.OrdinalIgnoreCase))
        {
            var subfolder = string.Equals(project.Name, CgUniqueDemoFactory.Ap1hdBreakingDemoName, StringComparison.OrdinalIgnoreCase)
                ? "BreakingNews"
                : "FlashNews";
            var ap1Path = CgUniqueDemoFactory.ResolveAp1hdPath(subfolder);
            if (Directory.Exists(ap1Path))
            {
                foreach (var layer in project.Layers.Where(x => string.Equals(x.Type, "ImageSequence", StringComparison.OrdinalIgnoreCase)))
                {
                    if (string.IsNullOrWhiteSpace(layer.Source))
                        layer.Source = ap1Path;
                }
            }
        }

        // Migrate existing AP1 Weather demo files created before animated icon
        // layers were added. This keeps operator customizations while injecting
        // only missing canonical icon layers and their cached icon codes.
        if (string.Equals(project.Name, CgUniqueDemoFactory.Ap1hdWeatherDemoName, StringComparison.OrdinalIgnoreCase) &&
            project.Layers.Count(x => string.Equals(x.Type, "WeatherIcon", StringComparison.OrdinalIgnoreCase)) < 8)
        {
            var canonical = CgUniqueDemoFactory.CreatePlayoutTemplate(CgUniqueDemoFactory.Ap1hdWeatherDemoName);
            var canonicalSource = canonical?.DataSources.FirstOrDefault();
            var targetSource = project.DataSources.FirstOrDefault();
            if (canonical is not null && canonicalSource is not null)
            {
                if (targetSource is null)
                {
                    targetSource = canonicalSource;
                    project.DataSources.Add(targetSource);
                }
                else if (!targetSource.CachedItemsJson.Contains("\"icon\"", StringComparison.OrdinalIgnoreCase))
                {
                    targetSource.CachedItemsJson = canonicalSource.CachedItemsJson;
                }

                foreach (var canonicalIcon in canonical.Layers.Where(x => string.Equals(x.Type, "WeatherIcon", StringComparison.OrdinalIgnoreCase)))
                {
                    if (project.Layers.Any(x => string.Equals(x.Name, canonicalIcon.Name, StringComparison.OrdinalIgnoreCase))) continue;
                    var icon = CgDemoFactory.CloneLayer(canonicalIcon);
                    icon.DataSourceId = targetSource.Id;
                    project.Layers.Add(icon);
                }
            }
        }
    }

    private static (double Width, double Height) TryResolveNativeImageSize(CgLayer layer)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(layer.Source)) return (0, 0);
            string? filePath = null;
            if (File.Exists(layer.Source))
            {
                filePath = layer.Source;
            }
            else if (Directory.Exists(layer.Source))
            {
                var validExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".webp" };
                filePath = Directory.EnumerateFiles(layer.Source).FirstOrDefault(f => validExts.Contains(Path.GetExtension(f)));
            }

            if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.None);
                var frame = decoder.Frames.FirstOrDefault();
                if (frame != null && frame.PixelWidth > 0 && frame.PixelHeight > 0)
                {
                    return (frame.PixelWidth, frame.PixelHeight);
                }
            }
        }
        catch { }
        return (0, 0);
    }
}
