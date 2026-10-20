using BroadcastPlayout.Models;
using System.Reflection;
using System.Text.Json;
using System.Collections.ObjectModel;

namespace BroadcastPlayout.Services;

/// <summary>
/// Timeline/keyframe animation helper with a GSAP-style ease vocabulary. This is a native
/// Kashtrix animation engine; it does not embed or depend on the GSAP JavaScript library.
/// </summary>
public static class CgAnimationEngine
{
    // AUTO KEY can capture visual changes in addition to motion. Style snapshots are kept in
    // CgKeyframe.StyleJson for backward-compatible project files. The compositor/editor only
    // clones a layer when style-keyframes actually exist, so normal playback stays on the fast path.
    private static readonly string[] StylePropertyNames =
    [
        "Name","Type","Text","Source","BlendMode","MaskEnabled","MaskShape","MaskX","MaskY","MaskWidth","MaskHeight","MaskCornerRadius","MaskFeather","MaskInvert","MaskPathData","ShapePathData","ShapePathClosed","VideoSourceKind","VideoInputFormat","VideoInputOptions","VideoDevice","AudioDevice","AlternateAudioUrl","VideoIsLiveSource","VideoCaptureWidth","VideoCaptureHeight","VideoSourceFrameRate","FontSize","FontFamily","Fill","Background","Visible","Speed",
        "AnimationIn","AnimationOut","AnimationInSeconds","AnimationOutSeconds","Role","ShapeKind","CornerRadius",
        "CornerRadiusTopLeft","CornerRadiusTopRight","CornerRadiusBottomRight","CornerRadiusBottomLeft",
        "CornerRadiusAsPercent","CornerRadiusTopLeftPercent","CornerRadiusTopRightPercent","CornerRadiusBottomRightPercent","CornerRadiusBottomLeftPercent","Perspective",
        "BorderColor","BorderWidth","HorizontalTextAlignment","VerticalTextAlignment","Bold","Italic","Underline",
        "OutlineColor","OutlineWidth","ShadowEnabled","ShadowColor","ShadowOffsetX","ShadowOffsetY","ShadowBlur","GlowColor","GlowRadius",
        "MotionBlurX","MotionBlurY","MotionBlurSamples","LetterSpacing","LineSpacing","UseGradient","GradientColor2","GradientAngle","GradientStops","GlossEnabled","GlossOpacity",
        "TextAnimationPreset","TextAnimationUnit","TextAnimationDurationSeconds","TextAnimationStaggerSeconds",
        "TextAnimationScaleStart","TextAnimationScaleEnd","TimerAutoStop",
        "TickerSeparator","TickerSeparatorLogo","TickerCategoriesEnabled","TickerCategoryBadgeBackground",
        "UseBackground","AnimationGlowColor","AnimationGlowRadius","AnimationBlurRadius",
        "DataField","DataFormat","DataItemDurationSeconds","DataItemOffset","SequenceFps","SequenceStartFrame","SequenceEndFrame","SequenceLoop","SequenceHoldLastFrame","SequenceAdvanceDataItem",
        "TickerMode","TickerDirection","TickerGap","TickerRepeat","SqueezeProgram","SqueezeHorizontalMode","SqueezeX","SqueezeY","SqueezeWidth","SqueezeHeight","SqueezeInSeconds","SqueezeOutSeconds"
    ];
    private static readonly Dictionary<string,PropertyInfo> StyleProperties = typeof(CgLayer).GetProperties(BindingFlags.Public|BindingFlags.Instance)
        .Where(p => p.CanRead && p.CanWrite && StylePropertyNames.Contains(p.Name,StringComparer.Ordinal))
        .ToDictionary(p => p.Name, StringComparer.Ordinal);
    private static readonly PropertyInfo[] CloneProperties = typeof(CgLayer).GetProperties(BindingFlags.Public|BindingFlags.Instance)
        .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
        .ToArray();

    public static string CaptureStyleJson(CgLayer layer)
    {
        var map = new Dictionary<string,object?>(StringComparer.Ordinal);
        foreach (var name in StylePropertyNames)
            if (StyleProperties.TryGetValue(name,out var property)) map[name]=property.GetValue(layer);
        return JsonSerializer.Serialize(map);
    }

    public static CgLayer CreateVisualLayer(CgLayer layer, double timeSeconds)
    {
        var frames = layer.Keyframes?.Where(x => !string.IsNullOrWhiteSpace(x.StyleJson)).OrderBy(x => x.TimeSeconds).ToArray();
        if (frames is null || frames.Length <= 1) return layer;

        // Check if there is actual style variance across keyframes
        var firstJson = frames[0].StyleJson;
        bool hasVariance = false;
        for (int i = 1; i < frames.Length; i++)
        {
            if (!string.Equals(firstJson, frames[i].StyleJson, StringComparison.Ordinal))
            {
                hasVariance = true;
                break;
            }
        }
        if (!hasVariance) return layer;

        var clone = new CgLayer();
        foreach (var property in CloneProperties)
        {
            try { property.SetValue(clone, property.GetValue(layer)); } catch { }
        }

        if (timeSeconds >= frames[^1].TimeSeconds)
        {
            ApplyStyleJson(clone, frames[^1].StyleJson);
            return clone;
        }

        var rightIndex = Array.FindIndex(frames, x => x.TimeSeconds >= timeSeconds);
        if (rightIndex <= 0)
        {
            ApplyStyleJson(clone, frames[0].StyleJson);
            return clone;
        }

        var left = frames[rightIndex - 1];
        var right = frames[rightIndex];
        ApplyStyleJson(clone, left.StyleJson);

        if (left.Hold) return clone;

        var span = Math.Max(.000001, right.TimeSeconds - left.TimeSeconds);
        var raw = Math.Clamp((timeSeconds - left.TimeSeconds) / span, 0, 1);
        var eased = Ease(string.IsNullOrWhiteSpace(right.Ease) ? layer.DefaultEase : right.Ease, raw);

        var rightClone = new CgLayer();
        foreach (var property in CloneProperties)
        {
            try { property.SetValue(rightClone, property.GetValue(layer)); } catch { }
        }
        ApplyStyleJson(rightClone, right.StyleJson);

        // Interpolate colors across keyframe duration
        clone.Fill = InterpolateColor(clone.Fill, rightClone.Fill, eased);
        clone.Background = InterpolateColor(clone.Background, rightClone.Background, eased);
        clone.BorderColor = InterpolateColor(clone.BorderColor, rightClone.BorderColor, eased);
        clone.OutlineColor = InterpolateColor(clone.OutlineColor, rightClone.OutlineColor, eased);
        clone.GradientColor2 = InterpolateColor(clone.GradientColor2, rightClone.GradientColor2, eased);
        clone.ShadowColor = InterpolateColor(clone.ShadowColor, rightClone.ShadowColor, eased);
        clone.GlowColor = InterpolateColor(clone.GlowColor, rightClone.GlowColor, eased);

        // Interpolate numeric style properties
        clone.BorderWidth = Lerp(clone.BorderWidth, rightClone.BorderWidth, eased);
        clone.FontSize = Lerp(clone.FontSize, rightClone.FontSize, eased);
        clone.GradientAngle = Lerp(clone.GradientAngle, rightClone.GradientAngle, eased);
        clone.CornerRadius = Lerp(clone.CornerRadius, rightClone.CornerRadius, eased);
        clone.CornerRadiusTopLeft = Lerp(clone.CornerRadiusTopLeft, rightClone.CornerRadiusTopLeft, eased);
        clone.CornerRadiusTopRight = Lerp(clone.CornerRadiusTopRight, rightClone.CornerRadiusTopRight, eased);
        clone.CornerRadiusBottomRight = Lerp(clone.CornerRadiusBottomRight, rightClone.CornerRadiusBottomRight, eased);
        clone.CornerRadiusBottomLeft = Lerp(clone.CornerRadiusBottomLeft, rightClone.CornerRadiusBottomLeft, eased);
        clone.CornerRadiusTopLeftPercent = Lerp(clone.CornerRadiusTopLeftPercent, rightClone.CornerRadiusTopLeftPercent, eased);
        clone.CornerRadiusTopRightPercent = Lerp(clone.CornerRadiusTopRightPercent, rightClone.CornerRadiusTopRightPercent, eased);
        clone.CornerRadiusBottomRightPercent = Lerp(clone.CornerRadiusBottomRightPercent, rightClone.CornerRadiusBottomRightPercent, eased);
        clone.CornerRadiusBottomLeftPercent = Lerp(clone.CornerRadiusBottomLeftPercent, rightClone.CornerRadiusBottomLeftPercent, eased);

        // Mask properties keyframing
        clone.MaskFeather = Lerp(clone.MaskFeather, rightClone.MaskFeather, eased);
        clone.MaskCornerRadius = Lerp(clone.MaskCornerRadius, rightClone.MaskCornerRadius, eased);
        clone.MaskX = Lerp(clone.MaskX, rightClone.MaskX, eased);
        clone.MaskY = Lerp(clone.MaskY, rightClone.MaskY, eased);
        clone.MaskWidth = Lerp(clone.MaskWidth, rightClone.MaskWidth, eased);
        clone.MaskHeight = Lerp(clone.MaskHeight, rightClone.MaskHeight, eased);

        // Shape kind keyframing: switch cleanly at midpoint with smooth interpolation of geometry
        if (!string.Equals(clone.ShapeKind, rightClone.ShapeKind, StringComparison.OrdinalIgnoreCase))
        {
            clone.ShapeKind = eased >= 0.5 ? rightClone.ShapeKind : clone.ShapeKind;
        }

        return clone;
    }

    public static string InterpolateColor(string? hex1, string? hex2, double p)
    {
        if (string.IsNullOrWhiteSpace(hex1)) return hex2 ?? "#FFFFFFFF";
        if (string.IsNullOrWhiteSpace(hex2)) return hex1 ?? "#FFFFFFFF";
        p = Math.Clamp(p, 0.0, 1.0);
        if (p <= 0.0001) return hex1;
        if (p >= 0.9999) return hex2;
        if (!TryParseHexColor(hex1, out var a1, out var r1, out var g1, out var b1) ||
            !TryParseHexColor(hex2, out var a2, out var r2, out var g2, out var b2))
            return p < 0.5 ? hex1 : hex2;
        var a = (byte)Math.Clamp(Math.Round(a1 + (a2 - a1) * p), 0, 255);
        var r = (byte)Math.Clamp(Math.Round(r1 + (r2 - r1) * p), 0, 255);
        var g = (byte)Math.Clamp(Math.Round(g1 + (g2 - g1) * p), 0, 255);
        var b = (byte)Math.Clamp(Math.Round(b1 + (b2 - b1) * p), 0, 255);
        return $"#{a:X2}{r:X2}{g:X2}{b:X2}";
    }

    private static bool TryParseHexColor(string hex, out byte a, out byte r, out byte g, out byte b)
    {
        a = 255; r = 255; g = 255; b = 255;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var s = hex.Trim();
        if (s.StartsWith('#')) s = s[1..];
        try
        {
            if (s.Length == 8)
            {
                a = Convert.ToByte(s[..2], 16);
                r = Convert.ToByte(s.Substring(2, 2), 16);
                g = Convert.ToByte(s.Substring(4, 2), 16);
                b = Convert.ToByte(s.Substring(6, 2), 16);
                return true;
            }
            if (s.Length == 6)
            {
                a = 255;
                r = Convert.ToByte(s[..2], 16);
                g = Convert.ToByte(s.Substring(2, 2), 16);
                b = Convert.ToByte(s.Substring(4, 2), 16);
                return true;
            }
            if (s.Length == 3 || s.Length == 4)
            {
                var ca = s.Length == 4 ? Convert.ToByte($"{s[0]}{s[0]}", 16) : (byte)255;
                var offset = s.Length == 4 ? 1 : 0;
                var cr = Convert.ToByte($"{s[offset]}{s[offset]}", 16);
                var cg = Convert.ToByte($"{s[offset+1]}{s[offset+1]}", 16);
                var cb = Convert.ToByte($"{s[offset+2]}{s[offset+2]}", 16);
                a = ca; r = cr; g = cg; b = cb;
                return true;
            }
        }
        catch { }
        return false;
    }

    public static void ApplyStyleJson(CgLayer layer,string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            using var doc=JsonDocument.Parse(json);
            if(doc.RootElement.ValueKind!=JsonValueKind.Object)return;
            foreach(var item in doc.RootElement.EnumerateObject())
            {
                if(!StyleProperties.TryGetValue(item.Name,out var property))continue;
                object? value = property.PropertyType == typeof(string) ? item.Value.GetString() ?? string.Empty
                    : property.PropertyType == typeof(double) && item.Value.TryGetDouble(out var d) ? d
                    : property.PropertyType == typeof(int) && item.Value.TryGetInt32(out var i) ? i
                    : property.PropertyType == typeof(bool) && (item.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) ? item.Value.GetBoolean()
                    : property.PropertyType == typeof(ObservableCollection<CgGradientStop>) ? JsonSerializer.Deserialize<ObservableCollection<CgGradientStop>>(item.Value.GetRawText()) ?? []
                    : null;
                if(value is not null) property.SetValue(layer,value);
            }
        }
        catch { }
    }

    public readonly record struct MotionState(
        double X, double Y, double Z, double Width, double Height,
        double RotationX, double RotationY, double Rotation, double Opacity,
        double ScaleX, double ScaleY, double ScaleZ,
        double AnchorX, double AnchorY, double AnchorZ,
        double SkewX, double SkewY, double BlurRadius)
    {
        public static MotionState FromLayer(CgLayer layer) => new(
            layer.X, layer.Y, layer.Z, layer.Width, layer.Height,
            layer.RotationX, layer.RotationY, layer.Rotation, layer.Opacity,
            layer.ScaleX == 0 ? 1 : layer.ScaleX, layer.ScaleY == 0 ? 1 : layer.ScaleY, layer.ScaleZ == 0 ? 1 : layer.ScaleZ,
            Math.Clamp(layer.AnchorX, 0, 1), Math.Clamp(layer.AnchorY, 0, 1), layer.AnchorZ,
            layer.SkewX, layer.SkewY, Math.Max(0, layer.BlurRadius));
    }

    /// <summary>
    /// Evaluates only the layer's own keyframes, before parent-group transforms and before
    /// ScaleX/ScaleY are baked into geometry. Editor auto-key uses this so moving a point at
    /// the playhead records that point without double-applying a parent transform.
    /// </summary>
    public static MotionState EvaluateLayerLocal(CgLayer layer, double timeSeconds)
    {
        if (layer.Keyframes is null || layer.Keyframes.Count == 0)
            return MotionState.FromLayer(layer);

        var layerTime = ResolveCycleTime(layer.Keyframes, Math.Max(0, timeSeconds - layer.KeyframeDelaySeconds), layer.KeyframeRepeat, layer.KeyframeYoyo);
        var fallback = MotionState.FromLayer(layer);

        var hasTargetProps = layer.Keyframes.Any(k => !string.IsNullOrEmpty(k.TargetProperty) && !k.TargetProperty.Equals("All", StringComparison.OrdinalIgnoreCase));
        if (!hasTargetProps)
        {
            return EvaluateKeyframes(fallback, layer.Keyframes, layerTime, layer.DefaultEase);
        }

        var posFrames = layer.Keyframes.Where(k => string.IsNullOrEmpty(k.TargetProperty) || k.TargetProperty.Equals("All", StringComparison.OrdinalIgnoreCase) || k.TargetProperty.Contains("Position", StringComparison.OrdinalIgnoreCase)).ToList();
        var scaleFrames = layer.Keyframes.Where(k => string.IsNullOrEmpty(k.TargetProperty) || k.TargetProperty.Equals("All", StringComparison.OrdinalIgnoreCase) || k.TargetProperty.Contains("Scale", StringComparison.OrdinalIgnoreCase)).ToList();
        var rotFrames = layer.Keyframes.Where(k => string.IsNullOrEmpty(k.TargetProperty) || k.TargetProperty.Equals("All", StringComparison.OrdinalIgnoreCase) || k.TargetProperty.Contains("Rotation", StringComparison.OrdinalIgnoreCase)).ToList();
        var opFrames = layer.Keyframes.Where(k => string.IsNullOrEmpty(k.TargetProperty) || k.TargetProperty.Equals("All", StringComparison.OrdinalIgnoreCase) || k.TargetProperty.Contains("Opacity", StringComparison.OrdinalIgnoreCase)).ToList();
        var fxFrames = layer.Keyframes.Where(k => string.IsNullOrEmpty(k.TargetProperty) || k.TargetProperty.Equals("All", StringComparison.OrdinalIgnoreCase) || k.TargetProperty.Contains("Effects", StringComparison.OrdinalIgnoreCase)).ToList();

        var posState = EvaluateKeyframes(fallback, posFrames, layerTime, layer.DefaultEase);
        var scaleState = EvaluateKeyframes(fallback, scaleFrames, layerTime, layer.DefaultEase);
        var rotState = EvaluateKeyframes(fallback, rotFrames, layerTime, layer.DefaultEase);
        var opState = EvaluateKeyframes(fallback, opFrames, layerTime, layer.DefaultEase);
        var fxState = EvaluateKeyframes(fallback, fxFrames, layerTime, layer.DefaultEase);

        return fallback with
        {
            X = posState.X, Y = posState.Y, Z = posState.Z, Width = posState.Width, Height = posState.Height,
            AnchorX = posState.AnchorX, AnchorY = posState.AnchorY, AnchorZ = posState.AnchorZ,
            ScaleX = scaleState.ScaleX, ScaleY = scaleState.ScaleY, ScaleZ = scaleState.ScaleZ,
            Rotation = rotState.Rotation, RotationX = rotState.RotationX, RotationY = rotState.RotationY,
            SkewX = rotState.SkewX, SkewY = rotState.SkewY,
            Opacity = opState.Opacity,
            BlurRadius = fxState.BlurRadius
        };
    }

    public static MotionState EvaluateLayer(CgProject project, CgLayer layer, double timeSeconds)
    {
        var state = EvaluateLayerLocal(layer, timeSeconds);
        state = ApplyLayerTransform(state, layer.Perspective);
        if (layer.GroupId == Guid.Empty) return state;

        var visited = new HashSet<Guid>();
        var groupId = layer.GroupId;
        while (groupId != Guid.Empty && visited.Add(groupId))
        {
            var group = project.Groups?.FirstOrDefault(x => x.Id == groupId);
            if (group is null) break;
            if (!group.Visible) return state with { Opacity = 0 };
            var baseGroup = new MotionState(0, 0, 0, 0, 0, 0, 0, 0, group.Opacity, 1, 1, 1, .5, .5, 0, 0, 0, 0);
            var siblings = project.Layers.Where(x => x.GroupId == group.Id).ToArray();
            var siblingIndex = Array.IndexOf(siblings, layer);
            var staggerIndex = group.StaggerFrom.Equals("End", StringComparison.OrdinalIgnoreCase) ? Math.Max(0, siblings.Length - 1 - siblingIndex) : Math.Max(0, siblingIndex);
            var groupTime = Math.Max(0, timeSeconds - Math.Max(0, group.StaggerSeconds) * staggerIndex);
            groupTime = ResolveCycleTime(group.Keyframes, groupTime, group.KeyframeRepeat, group.KeyframeYoyo);
            var gs = EvaluateKeyframes(baseGroup, group.Keyframes, groupTime, "power2.out");

            var ox = group.OriginX;
            var oy = group.OriginY;
            var depth = PerspectiveScale(gs.Z, 1200);
            var gx = Math.Max(.001, gs.ScaleX) * depth * Math.Max(.04, Math.Abs(Math.Cos(gs.RotationY * Math.PI / 180.0)));
            var gy = Math.Max(.001, gs.ScaleY) * depth * Math.Max(.04, Math.Abs(Math.Cos(gs.RotationX * Math.PI / 180.0)));
            var cx = state.X + state.Width / 2.0;
            var cy = state.Y + state.Height / 2.0;
            cx = ox + (cx - ox) * gx + Math.Sin(gs.RotationY * Math.PI / 180.0) * state.Width * .16 + gs.X;
            cy = oy + (cy - oy) * gy - Math.Sin(gs.RotationX * Math.PI / 180.0) * state.Height * .16 + gs.Y;
            state = state with
            {
                X = cx - state.Width * gx / 2.0,
                Y = cy - state.Height * gy / 2.0,
                Z = state.Z + gs.Z,
                Width = state.Width * gx,
                Height = state.Height * gy,
                RotationX = state.RotationX + gs.RotationX,
                RotationY = state.RotationY + gs.RotationY,
                Rotation = state.Rotation + gs.Rotation,
                Opacity = state.Opacity * gs.Opacity,
                ScaleZ = state.ScaleZ * gs.ScaleZ,
                AnchorZ = state.AnchorZ + gs.AnchorZ,
                SkewX = state.SkewX + gs.SkewX,
                SkewY = state.SkewY + gs.SkewY,
                BlurRadius = Math.Max(state.BlurRadius, gs.BlurRadius)
            };
            groupId = group.ParentGroupId;
        }
        return state;
    }

    private static MotionState ApplyLayerTransform(MotionState state, double perspective)
    {
        perspective = Math.Clamp(double.IsFinite(perspective) ? perspective : 1200, 200, 10000);
        var depth = PerspectiveScale(state.Z + state.AnchorZ, perspective) * Math.Max(.001, state.ScaleZ);
        var rx = state.RotationX * Math.PI / 180.0;
        var ry = state.RotationY * Math.PI / 180.0;
        var sx = Math.Max(.001, state.ScaleX) * depth * Math.Max(.04, Math.Abs(Math.Cos(ry)));
        var sy = Math.Max(.001, state.ScaleY) * depth * Math.Max(.04, Math.Abs(Math.Cos(rx)));
        var ax = Math.Clamp(state.AnchorX, 0, 1);
        var ay = Math.Clamp(state.AnchorY, 0, 1);
        var anchorX = state.X + state.Width * ax;
        var anchorY = state.Y + state.Height * ay;
        var width = state.Width * sx;
        var height = state.Height * sy;
        var shiftX = Math.Sin(ry) * state.Width * .22 * depth;
        var shiftY = -Math.Sin(rx) * state.Height * .22 * depth;
        return state with
        {
            X = anchorX - width * ax + shiftX,
            Y = anchorY - height * ay + shiftY,
            Width = width, Height = height,
            ScaleX = sx, ScaleY = sy
        };
    }

    private static double PerspectiveScale(double z, double perspective)
    {
        var denominator = Math.Max(80.0, perspective + z);
        return Math.Clamp(perspective / denominator, .05, 12.0);
    }

    private static double ResolveCycleTime(IReadOnlyList<CgKeyframe>? frames, double time, int repeat, bool yoyo)
    {
        if (frames is null || frames.Count < 2) return time;
        var start = frames.Min(x => x.TimeSeconds); var end = frames.Max(x => x.TimeSeconds); var span = Math.Max(.000001, end - start);
        if (time <= end || repeat == 0) return time;
        var cycle = (int)Math.Floor((time - start) / span);
        if (repeat > 0 && cycle > repeat) return yoyo && repeat % 2 == 1 ? start : end;
        var local = (time - start) % span;
        if (yoyo && cycle % 2 == 1) local = span - local;
        return start + local;
    }

    public static MotionState EvaluateKeyframes(MotionState fallback, IReadOnlyList<CgKeyframe>? frames, double timeSeconds, string defaultEase)
    {
        if (frames is null || frames.Count == 0) return fallback;
        var ordered = frames.OrderBy(x => x.TimeSeconds).ToArray();
        if (ordered.Length == 1) return FromFrame(ordered[0], fallback);
        if (timeSeconds <= ordered[0].TimeSeconds) return FromFrame(ordered[0], fallback);
        if (timeSeconds >= ordered[^1].TimeSeconds) return FromFrame(ordered[^1], fallback);

        var rightIndex = Array.FindIndex(ordered, x => x.TimeSeconds >= timeSeconds);
        if (rightIndex <= 0) return FromFrame(ordered[0], fallback);
        var left = ordered[rightIndex - 1];
        var right = ordered[rightIndex];
        if (left.Hold) return FromFrame(left, fallback);

        var span = Math.Max(.000001, right.TimeSeconds - left.TimeSeconds);
        var raw = Math.Clamp((timeSeconds - left.TimeSeconds) / span, 0, 1);
        var eased = Ease(string.IsNullOrWhiteSpace(right.Ease) ? defaultEase : right.Ease, raw);
        return Interpolate(FromFrame(left, fallback), FromFrame(right, fallback), eased);
    }

    public static double Ease(string? name, double p)
    {
        p = Math.Clamp(p, 0, 1);
        var n = (name ?? "linear").Trim().ToLowerInvariant().Replace(" ", "");
        if (n is "none" or "linear") return p;

        var mode = n.EndsWith(".inout") ? "inout" : n.EndsWith(".in") ? "in" : "out";
        var stem = n.Replace(".inout", "").Replace(".out", "").Replace(".in", "");
        double Base(double x) => stem switch
        {
            "power1" or "quad" => x * x,
            "power2" or "cubic" => x * x * x,
            "power3" or "quart" => x * x * x * x,
            "power4" or "quint" => x * x * x * x * x,
            "sine" => 1 - Math.Cos(x * Math.PI / 2),
            "expo" => x <= 0 ? 0 : Math.Pow(2, 10 * x - 10),
            "circ" => 1 - Math.Sqrt(Math.Max(0, 1 - x * x)),
            "back" => BackIn(x),
            "elastic" => ElasticIn(x),
            "bounce" => 1 - BounceOut(1 - x),
            _ when stem.StartsWith("steps(") => Steps(stem, x),
            _ => x
        };

        return mode switch
        {
            "in" => Base(p),
            "inout" => p < .5 ? Base(p * 2) / 2 : 1 - Base((1 - p) * 2) / 2,
            _ => 1 - Base(1 - p)
        };
    }

    private static double BackIn(double x)
    {
        const double c1 = 1.70158;
        const double c3 = c1 + 1;
        return c3 * x * x * x - c1 * x * x;
    }

    private static double ElasticIn(double x)
    {
        if (x <= 0 || x >= 1) return x;
        const double c4 = 2 * Math.PI / 3;
        return -Math.Pow(2, 10 * x - 10) * Math.Sin((x * 10 - 10.75) * c4);
    }

    private static double BounceOut(double x)
    {
        const double n1 = 7.5625;
        const double d1 = 2.75;
        if (x < 1 / d1) return n1 * x * x;
        if (x < 2 / d1) { x -= 1.5 / d1; return n1 * x * x + .75; }
        if (x < 2.5 / d1) { x -= 2.25 / d1; return n1 * x * x + .9375; }
        x -= 2.625 / d1; return n1 * x * x + .984375;
    }

    private static double Steps(string stem, double x)
    {
        var start = stem.IndexOf('(');
        var end = stem.IndexOf(')');
        var count = 1;
        if (start >= 0 && end > start && int.TryParse(stem[(start + 1)..end], out var parsed)) count = Math.Max(1, parsed);
        return Math.Floor(x * count) / count;
    }

    private static MotionState FromFrame(CgKeyframe f, MotionState fallback) => new(
        f.X, f.Y, f.Z,
        f.Width <= 0 ? fallback.Width : f.Width,
        f.Height <= 0 ? fallback.Height : f.Height,
        f.RotationX, f.RotationY, f.Rotation, Math.Clamp(f.Opacity, 0, 1),
        f.ScaleX == 0 ? 1 : f.ScaleX,
        f.ScaleY == 0 ? 1 : f.ScaleY,
        f.ScaleZ == 0 ? 1 : f.ScaleZ,
        Math.Clamp(f.AnchorX, 0, 1), Math.Clamp(f.AnchorY, 0, 1), f.AnchorZ,
        f.SkewX, f.SkewY, Math.Max(0, f.BlurRadius));

    private static MotionState Interpolate(MotionState a, MotionState b, double p) => new(
        Lerp(a.X,b.X,p), Lerp(a.Y,b.Y,p), Lerp(a.Z,b.Z,p),
        Lerp(a.Width,b.Width,p), Lerp(a.Height,b.Height,p),
        Lerp(a.RotationX,b.RotationX,p), Lerp(a.RotationY,b.RotationY,p), Lerp(a.Rotation,b.Rotation,p),
        Lerp(a.Opacity,b.Opacity,p), Lerp(a.ScaleX,b.ScaleX,p), Lerp(a.ScaleY,b.ScaleY,p), Lerp(a.ScaleZ,b.ScaleZ,p),
        Lerp(a.AnchorX,b.AnchorX,p), Lerp(a.AnchorY,b.AnchorY,p), Lerp(a.AnchorZ,b.AnchorZ,p),
        Lerp(a.SkewX,b.SkewX,p), Lerp(a.SkewY,b.SkewY,p), Lerp(a.BlurRadius,b.BlurRadius,p));

    private static double Lerp(double a, double b, double p) => a + (b - a) * p;

    public readonly record struct TextVisualState(
        double Opacity,
        double OffsetX,
        double OffsetY,
        double Scale,
        double BlurRadius = 0,
        double DynamicGlowRadius = 0,
        string DynamicGlowColor = "#00000000");

    public static TextVisualState EvaluateTextVisual(CgLayer layer, double timeSeconds)
    {
        var preset = (layer.TextAnimationPreset ?? "None").Trim();
        var isNone = preset.Equals("None", StringComparison.OrdinalIgnoreCase);
        var isTypeOn = preset.Equals("Type On", StringComparison.OrdinalIgnoreCase) || preset.Equals("Typewriter", StringComparison.OrdinalIgnoreCase);

        var isTickerOrCategory = string.Equals(layer.Type, "Ticker", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(layer.DataField, "category", StringComparison.OrdinalIgnoreCase) ||
                                 (layer.Name?.Contains("Badge", StringComparison.OrdinalIgnoreCase) ?? false) ||
                                 (layer.Name?.Contains("Ticker", StringComparison.OrdinalIgnoreCase) ?? false);

        var localTime = Math.Max(0, timeSeconds - layer.StartSeconds);
        var itemDuration = (!isTickerOrCategory && layer.DataSourceId != Guid.Empty) ? (layer.DataItemDurationSeconds > 0.05 ? layer.DataItemDurationSeconds : 4.0) : 0;
        if (itemDuration > 0)
        {
            localTime %= itemDuration;
        }

        var delay = Math.Max(0, layer.TextAnimationDelaySeconds);
        // During burst wipe window, keep text hidden so it doesn't collide with wipe graphics
        if (delay > 0 && localTime < delay)
        {
            return new TextVisualState(0, 0, 0, 0, layer.BlurRadius, 0, layer.AnimationGlowColor);
        }

        var outDuration = layer.AnimationOutSeconds > 0 ? layer.AnimationOutSeconds : (itemDuration > 0 ? 0.35 : Math.Max(.05, layer.TextAnimationDurationSeconds));
        // Smooth exit fade in the last outDuration seconds of the data item cycle if data item is active
        var exitFade = 1.0;
        if (itemDuration > 0 && localTime > Math.Max(delay, itemDuration - outDuration))
        {
            exitFade = Math.Clamp((itemDuration - localTime) / Math.Max(0.05, outDuration), 0, 1);
        }

        if (isNone || isTypeOn)
        {
            return new TextVisualState(exitFade, 0, 0, 1, layer.BlurRadius, 0, layer.AnimationGlowColor);
        }

        var animTime = Math.Max(0, localTime - delay);
        var duration = Math.Max(.05, layer.TextAnimationDurationSeconds);

        // In-motion progress (0 -> 1 as animTime progresses)
        var rawIn = Math.Clamp(animTime / duration, 0, 1);
        var pIn = Ease(preset.Equals("Pop Cascade", StringComparison.OrdinalIgnoreCase) ? "back.out" : preset.Equals("Bounce / Wave", StringComparison.OrdinalIgnoreCase) ? "bounce.out" : "power3.out", rawIn);

        // Out-motion progress (1 during sustain, decreasing to 0 during layer or item exit)
        var totalLayerDuration = Math.Max(duration * 2, layer.EndSeconds - layer.StartSeconds);
        var remainingTime = itemDuration > 0
            ? Math.Max(0, itemDuration - localTime)
            : Math.Max(0, layer.EndSeconds - timeSeconds);
        var rawOut = Math.Clamp(remainingTime / Math.Max(0.05, outDuration), 0, 1);
        var pOut = Ease("power3.in", rawOut);

        // Active motion factor p (1 = settled static display, < 1 = in or out transition)
        var isEntering = rawIn < 1.0;
        var isExiting = rawOut < 1.0;
        var p = Math.Min(pIn, pOut);

        // Transition factor (0 = fully settled, 1 = maximum transition intensity)
        var transitionFactor = Math.Clamp(1.0 - p, 0.0, 1.0);

        // Dynamic animation blur and glow apply ONLY during the in/out transition
        var dynamicBlur = layer.BlurRadius + (layer.AnimationBlurRadius * transitionFactor);
        var dynamicGlow = layer.AnimationGlowRadius * transitionFactor;

        var state = preset.ToUpperInvariant() switch
        {
            "ZOOM" => isEntering
                ? new TextVisualState(Math.Clamp(.15 + .85 * pIn, 0, 1), 0, 0, Math.Clamp(1.0 + (1.0 - pIn) * (Math.Max(1.2, layer.TextAnimationScaleStart) - 1.0), 0.2, 4.0), dynamicBlur, dynamicGlow, layer.AnimationGlowColor)
                : new TextVisualState(Math.Clamp(.15 + .85 * pOut, 0, 1), 0, 0, Math.Clamp(1.0 + (1.0 - pOut) * (Math.Max(1.2, layer.TextAnimationScaleStart) - 1.0), 0.2, 4.0), dynamicBlur, dynamicGlow, layer.AnimationGlowColor),
            "WIGGLE" => isEntering
                ? new TextVisualState(Math.Clamp(.20 + .80 * pIn, 0, 1), (1 - pIn) * Math.Sin(animTime * 25.0) * 16.0, (1 - pIn) * Math.Cos(animTime * 20.0) * 8.0, 1, dynamicBlur, dynamicGlow, layer.AnimationGlowColor)
                : new TextVisualState(Math.Clamp(.20 + .80 * pOut, 0, 1), (1 - pOut) * Math.Sin(animTime * 25.0) * 16.0, (1 - pOut) * Math.Cos(animTime * 20.0) * 8.0, 1, dynamicBlur, dynamicGlow, layer.AnimationGlowColor),
            "JUMP" => isEntering
                ? new TextVisualState(Math.Clamp(.20 + .80 * pIn, 0, 1), 0, -(1 - pIn) * Math.Abs(Math.Sin(animTime * 14.0)) * 48.0, 1, dynamicBlur, dynamicGlow, layer.AnimationGlowColor)
                : new TextVisualState(Math.Clamp(.20 + .80 * pOut, 0, 1), 0, -(1 - pOut) * Math.Abs(Math.Sin(animTime * 14.0)) * 48.0, 1, dynamicBlur, dynamicGlow, layer.AnimationGlowColor),
            "SCALE UP" => isEntering
                ? new TextVisualState(Math.Clamp(.15 + .85 * pIn, 0, 1), 0, 0, Math.Clamp(layer.TextAnimationScaleStart < 1.0 ? layer.TextAnimationScaleStart + pIn * (1.0 - layer.TextAnimationScaleStart) : pIn, 0.0, 1.0), dynamicBlur, dynamicGlow, layer.AnimationGlowColor)
                : new TextVisualState(Math.Clamp(.15 + .85 * pOut, 0, 1), 0, 0, Math.Clamp(layer.TextAnimationScaleStart < 1.0 ? layer.TextAnimationScaleStart + pOut * (1.0 - layer.TextAnimationScaleStart) : pOut, 0.0, 1.0), dynamicBlur, dynamicGlow, layer.AnimationGlowColor),
            "SLIDE LEFT" => isEntering
                ? new TextVisualState(Math.Clamp(.15 + .85 * pIn, 0, 1), (1 - pIn) * Math.Max(80, layer.Width * 0.45), 0, 1, dynamicBlur, dynamicGlow, layer.AnimationGlowColor)
                : new TextVisualState(Math.Clamp(.15 + .85 * pOut, 0, 1), -(1 - pOut) * Math.Max(80, layer.Width * 0.45), 0, 1, dynamicBlur, dynamicGlow, layer.AnimationGlowColor),
            "SLIDE RIGHT" => isEntering
                ? new TextVisualState(Math.Clamp(.15 + .85 * pIn, 0, 1), -(1 - pIn) * Math.Max(80, layer.Width * 0.45), 0, 1, dynamicBlur, dynamicGlow, layer.AnimationGlowColor)
                : new TextVisualState(Math.Clamp(.15 + .85 * pOut, 0, 1), (1 - pOut) * Math.Max(80, layer.Width * 0.45), 0, 1, dynamicBlur, dynamicGlow, layer.AnimationGlowColor),
            "PUSH LEFT" => isEntering
                ? new TextVisualState(Math.Clamp(.20 + .80 * pIn, 0, 1), (1 - pIn) * Math.Max(160, layer.Width), 0, 1, dynamicBlur, dynamicGlow, layer.AnimationGlowColor)
                : new TextVisualState(Math.Clamp(.20 + .80 * pOut, 0, 1), -(1 - pOut) * Math.Max(160, layer.Width), 0, 1, dynamicBlur, dynamicGlow, layer.AnimationGlowColor),
            "PUSH RIGHT" => isEntering
                ? new TextVisualState(Math.Clamp(.20 + .80 * pIn, 0, 1), -(1 - pIn) * Math.Max(160, layer.Width), 0, 1, dynamicBlur, dynamicGlow, layer.AnimationGlowColor)
                : new TextVisualState(Math.Clamp(.20 + .80 * pOut, 0, 1), (1 - pOut) * Math.Max(160, layer.Width), 0, 1, dynamicBlur, dynamicGlow, layer.AnimationGlowColor),
            "SLIDE UP" => isEntering
                ? new TextVisualState(Math.Clamp(.15 + .85 * pIn, 0, 1), 0, (1 - pIn) * Math.Max(35, layer.Height * 0.5), 1, dynamicBlur, dynamicGlow, layer.AnimationGlowColor)
                : new TextVisualState(Math.Clamp(.15 + .85 * pOut, 0, 1), 0, -(1 - pOut) * Math.Max(35, layer.Height * 0.5), 1, dynamicBlur, dynamicGlow, layer.AnimationGlowColor),
            "SLIDE DOWN" => isEntering
                ? new TextVisualState(Math.Clamp(.15 + .85 * pIn, 0, 1), 0, -(1 - pIn) * Math.Max(35, layer.Height * 0.5), 1, dynamicBlur, dynamicGlow, layer.AnimationGlowColor)
                : new TextVisualState(Math.Clamp(.15 + .85 * pOut, 0, 1), 0, (1 - pOut) * Math.Max(35, layer.Height * 0.5), 1, dynamicBlur, dynamicGlow, layer.AnimationGlowColor),
            "FADE CASCADE" or "FADE" => new TextVisualState(Math.Clamp(.15 + .85 * p, 0, 1), 0, 0, 1, dynamicBlur, dynamicGlow, layer.AnimationGlowColor),
            "RISE CASCADE" => isEntering
                ? new TextVisualState(.20 + .80 * pIn, 0, 28 * (1 - pIn), 1, dynamicBlur, dynamicGlow, layer.AnimationGlowColor)
                : new TextVisualState(.20 + .80 * pOut, 0, -28 * (1 - pOut), 1, dynamicBlur, dynamicGlow, layer.AnimationGlowColor),
            "TRACKING REVEAL" => new TextVisualState(.30 + .70 * p, 0, 0, .94 + .06 * p, dynamicBlur, dynamicGlow, layer.AnimationGlowColor),
            "POP CASCADE" or "POP" => new TextVisualState(Math.Clamp(.15 + .85 * Math.Min(rawIn, rawOut), 0, 1), 0, 0, Math.Clamp(.78 + .22 * p, .65, 1.08), dynamicBlur, dynamicGlow, layer.AnimationGlowColor),
            "SLIDE LETTERS" => isEntering
                ? new TextVisualState(.18 + .82 * pIn, 52 * (1 - pIn), 0, .98 + .02 * pIn, dynamicBlur, dynamicGlow, layer.AnimationGlowColor)
                : new TextVisualState(.18 + .82 * pOut, -52 * (1 - pOut), 0, .98 + .02 * pOut, dynamicBlur, dynamicGlow, layer.AnimationGlowColor),
            "SOFT REVEAL" => new TextVisualState(Math.Clamp(Math.Min(rawIn, rawOut), 0, 1), 0, 8 * (1 - p), .90 + .10 * p, Math.Max(dynamicBlur, (1 - p) * 12.0), dynamicGlow, layer.AnimationGlowColor),
            "FLIP IN" or "DATE / TIME FLIP" => new TextVisualState(.15 + .85 * Math.Min(rawIn, rawOut), 0, 18 * (1 - p), Math.Clamp(.68 + .32 * p, .60, 1.04), dynamicBlur, dynamicGlow, layer.AnimationGlowColor),
            "WIPE WORDS" => isEntering
                ? new TextVisualState(.25 + .75 * pIn, -20 * (1 - pIn), 0, 1, dynamicBlur, dynamicGlow, layer.AnimationGlowColor)
                : new TextVisualState(.25 + .75 * pOut, 20 * (1 - pOut), 0, 1, dynamicBlur, dynamicGlow, layer.AnimationGlowColor),
            "BLUR DISSOLVE" or "BLUR IN" => new TextVisualState(Math.Clamp(.10 + .90 * p, 0, 1), 0, 0, 1, Math.Max(dynamicBlur, (1 - p) * 18.0), dynamicGlow, layer.AnimationGlowColor),
            "TYPEWRITER + BLUR" => new TextVisualState(Math.Clamp(.15 + .85 * p, 0, 1), 0, 0, 1, Math.Max(dynamicBlur, (1 - p) * 8.0), dynamicGlow, layer.AnimationGlowColor),
            "GLOW PULSE" => new TextVisualState(Math.Clamp(Math.Min(rawIn, rawOut) * 1.4, 0, 1), 0, 0, .92 + .08 * p, Math.Max(dynamicBlur, (1 - p) * 10.0), Math.Max(dynamicGlow, (1 - p) * 15.0), layer.AnimationGlowColor),
            "BOUNCE / WAVE" => isEntering
                ? new TextVisualState(Math.Clamp(rawIn * 1.5, 0, 1), 0, 35 * (1 - pIn), 1, dynamicBlur, dynamicGlow, layer.AnimationGlowColor)
                : new TextVisualState(Math.Clamp(rawOut * 1.5, 0, 1), 0, 35 * (1 - pOut), 1, dynamicBlur, dynamicGlow, layer.AnimationGlowColor),
            "SCALE DOWN (LETTER + BLUR + GLOW)" or "SCALE DOWN" or "SCALE CASCADE" => isEntering
                ? new TextVisualState(Math.Clamp(.15 + .85 * pIn, 0, 1), 0, 0, Math.Clamp(1.0 + (1.0 - pIn) * (Math.Max(1.2, layer.TextAnimationScaleStart) - 1.0), 1.0, 4.0), Math.Max(dynamicBlur, (1 - pIn) * 16.0), Math.Max(dynamicGlow, (1 - pIn) * 22.0), string.IsNullOrWhiteSpace(layer.AnimationGlowColor) ? "#8000E5FF" : layer.AnimationGlowColor)
                : new TextVisualState(Math.Clamp(.15 + .85 * pOut, 0, 1), 0, 0, Math.Clamp(1.0 + (1.0 - pOut) * (Math.Max(1.2, layer.TextAnimationScaleStart) - 1.0), 1.0, 4.0), Math.Max(dynamicBlur, (1 - pOut) * 16.0), Math.Max(dynamicGlow, (1 - pOut) * 22.0), string.IsNullOrWhiteSpace(layer.AnimationGlowColor) ? "#8000E5FF" : layer.AnimationGlowColor),
            "DATE / TIME SPLIT" => isEntering
                ? new TextVisualState(Math.Clamp(.10 + .90 * pIn, 0, 1), (1 - pIn) * 30, -(1 - pIn) * 15, 1, dynamicBlur, dynamicGlow, layer.AnimationGlowColor)
                : new TextVisualState(Math.Clamp(.10 + .90 * pOut, 0, 1), -(1 - pOut) * 30, (1 - pOut) * 15, 1, dynamicBlur, dynamicGlow, layer.AnimationGlowColor),
            _ => new TextVisualState(1, 0, 0, 1, dynamicBlur, dynamicGlow, layer.AnimationGlowColor)
        };
        return state with { Opacity = Math.Clamp(state.Opacity * exitFade, 0, 1) };
    }

    public static string ResolveAnimatedText(CgLayer layer, string? text, double timeSeconds)
    {
        var value = text ?? string.Empty;
        var preset = (layer.TextAnimationPreset ?? "None").Trim();
        var unit = (layer.TextAnimationUnit ?? "Whole").Trim();

        // For typewriter presets, if unit is left as "Whole", default to "Letter" so characters animate out individually
        if (unit.Equals("Whole", StringComparison.OrdinalIgnoreCase) &&
            (preset.Equals("Type On", StringComparison.OrdinalIgnoreCase) ||
             preset.Equals("Typewriter", StringComparison.OrdinalIgnoreCase) ||
             preset.Equals("Typewriter + Blur", StringComparison.OrdinalIgnoreCase)))
        {
            unit = "Letter";
        }

        var localTime = Math.Max(0, timeSeconds - layer.StartSeconds);
        var itemDuration = layer.DataSourceId != Guid.Empty ? (layer.DataItemDurationSeconds > 0.05 ? layer.DataItemDurationSeconds : 4.0) : 0;
        if (itemDuration > 0)
        {
            localTime %= itemDuration;
        }

        var delay = Math.Max(0, layer.TextAnimationDelaySeconds);
        if (delay > 0 && localTime < delay)
        {
            return string.Empty;
        }

        if (preset.Equals("None", StringComparison.OrdinalIgnoreCase) || unit.Equals("Whole", StringComparison.OrdinalIgnoreCase) || value.Length == 0)
            return value;

        var animLocal = Math.Max(0, localTime - delay);
        var stagger = Math.Max(.01, layer.TextAnimationStaggerSeconds);
        var duration = Math.Max(.05, layer.TextAnimationDurationSeconds);
        IReadOnlyList<string> units = unit.ToUpperInvariant() switch
        {
            "WORD" => System.Text.RegularExpressions.Regex.Matches(value, @"\S+\s*").Select(x => x.Value).ToArray(),
            "SENTENCE" => System.Text.RegularExpressions.Regex.Matches(value, @"[^.!?]+[.!?]*\s*").Select(x => x.Value).ToArray(),
            _ => value.Select(ch => ch.ToString()).ToArray()
        };
        if (units.Count == 0) return value;
        var byStagger = 1 + (int)Math.Floor(animLocal / stagger);
        var byDuration = (int)Math.Ceiling(Math.Clamp(animLocal / duration, 0, 1) * units.Count);
        var count = Math.Clamp(Math.Max(byStagger, byDuration), 0, units.Count);

        // Smooth out-motion for Type On: when exiting, progressively un-type
        var outDur = layer.AnimationOutSeconds > 0 ? layer.AnimationOutSeconds : duration;
        var rem = layer.EndSeconds - timeSeconds;
        if (rem < outDur && rem >= 0 && (preset.Equals("Type On", StringComparison.OrdinalIgnoreCase) || preset.Equals("Typewriter", StringComparison.OrdinalIgnoreCase)))
        {
            var outP = Math.Clamp(rem / Math.Max(0.05, outDur), 0, 1);
            count = Math.Min(count, (int)Math.Ceiling(outP * units.Count));
        }

        return string.Concat(units.Take(count));
    }
}
