using BroadcastPlayout.Models;

namespace BroadcastPlayout.Services;

/// <summary>Normalizes the bundled demonstration catalog so every visible layer remains valid for the full demo timeline.</summary>
public static class CgDemoValidator
{
    public static CgProject NormalizeDemoProject(CgProject project)
    {
        project.Width = Math.Clamp(project.Width, 320, 7680);
        project.Height = Math.Clamp(project.Height, 180, 4320);
        project.DurationSeconds = Math.Clamp(project.DurationSeconds, 2, 86400);
        if (string.IsNullOrWhiteSpace(project.Category))
        {
            var n = (project.Name ?? string.Empty).ToUpperInvariant();
            if (n.Contains("AP1") || n.Contains("AP 1")) project.Category = "AP1 HD";
            else if (n.Contains("PRIME")) project.Category = "PRIME HD";
            else if (n.Contains("SPACE")) project.Category = "SPACE 4K";
            else if (n.Contains("AAJ") || n.Contains("TAK")) project.Category = "AAJ TAK";
            else if (n.Contains("ABP")) project.Category = "ABP NEWS";
            else if (n.Contains("INDIA") || n.Contains("TV")) project.Category = "INDIA TV";
            else if (n.Contains("REPUBLIC")) project.Category = "REPUBLIC BHARAT";
            else if (n.Contains("BREAKING")) project.Category = "BREAKING NEWS";
            else if (n.Contains("WEATHER")) project.Category = "WEATHER";
            else if (n.Contains("SPORT") || n.Contains("CRICKET") || n.Contains("FOOTBALL") || n.Contains("MATCH")) project.Category = "SPORTS";
            else if (n.Contains("FINANCE") || n.Contains("STOCK") || n.Contains("MARKET") || n.Contains("SENSEX") || n.Contains("CRYPTO")) project.Category = "FINANCE";
            else if (n.Contains("ELECTION") || n.Contains("VOTE") || n.Contains("POLL")) project.Category = "ELECTION";
            else if (n.Contains("ENTERTAINMENT") || n.Contains("HOLLYWOOD") || n.Contains("BOLLYWOOD")) project.Category = "ENTERTAINMENT";
            else if (n.Contains("MUSIC") || n.Contains("SONG") || n.Contains("TRACK")) project.Category = "MUSIC";
            else if (n.Contains("TICKER")) project.Category = "TICKERS";
            else if (n.Contains("LOWER THIRD") || n.Contains("STRAP") || n.Contains("NAME CARD") || n.Contains("NAME PLATE") || n.Contains("SUPER")) project.Category = "LOWER THIRDS";
            else if (n.Contains("GRAPHICS PACK") || n.Contains("GFX PACK") || n.Contains("PACK ")) project.Category = "GRAPHICS PACK";
            else if (n.Contains("NEWS")) project.Category = "NEWS";
            else project.Category = "BROADCAST PACK";
        }
        var duration = project.DurationSeconds;
        var ids = new HashSet<Guid>();
        foreach(var group in project.Groups)
        {
            if(group.Id==Guid.Empty || !ids.Add(group.Id)){group.Id=Guid.NewGuid();ids.Add(group.Id);}
            group.Keyframes ??=[];
            foreach(var key in group.Keyframes)
            {
                key.TimeSeconds=Math.Clamp(key.TimeSeconds,0,duration); key.Opacity=Math.Clamp(key.Opacity,0,1);
                key.X=double.IsFinite(key.X)?key.X:0; key.Y=double.IsFinite(key.Y)?key.Y:0; key.Z=double.IsFinite(key.Z)?key.Z:0;
                key.RotationX=double.IsFinite(key.RotationX)?key.RotationX:0; key.RotationY=double.IsFinite(key.RotationY)?key.RotationY:0; key.Rotation=double.IsFinite(key.Rotation)?key.Rotation:0;
                key.ScaleX=double.IsFinite(key.ScaleX)&&Math.Abs(key.ScaleX)>.0001?key.ScaleX:1;
                key.ScaleY=double.IsFinite(key.ScaleY)&&Math.Abs(key.ScaleY)>.0001?key.ScaleY:1;
                key.ScaleZ=double.IsFinite(key.ScaleZ)&&Math.Abs(key.ScaleZ)>.0001?key.ScaleZ:1;
                key.AnchorX=double.IsFinite(key.AnchorX)?Math.Clamp(key.AnchorX,0,1):.5;
                key.AnchorY=double.IsFinite(key.AnchorY)?Math.Clamp(key.AnchorY,0,1):.5;
                key.AnchorZ=double.IsFinite(key.AnchorZ)?key.AnchorZ:0;
            }
            group.Keyframes=group.Keyframes.OrderBy(x=>x.TimeSeconds).ToList();
        }
        foreach(var layer in project.Layers)
        {
            if(layer.Id==Guid.Empty || !ids.Add(layer.Id)){layer.Id=Guid.NewGuid();ids.Add(layer.Id);}
            layer.Visible=true;
            layer.StartSeconds=Math.Clamp(layer.StartSeconds,0,Math.Max(0,duration-.05));
            if (layer.Type.Equals("ImageSequence", StringComparison.OrdinalIgnoreCase) && !layer.SequenceLoop && layer.SequenceEndFrame >= layer.SequenceStartFrame)
            {
                var seqSec = (layer.SequenceEndFrame - layer.SequenceStartFrame + 1) / Math.Max(1.0, layer.SequenceFps);
                layer.EndSeconds = Math.Clamp(layer.StartSeconds + seqSec, layer.StartSeconds + .05, duration);
            }
            else if (layer.EndSeconds <= layer.StartSeconds + .02) layer.EndSeconds = duration;
            else layer.EndSeconds = Math.Clamp(layer.EndSeconds, layer.StartSeconds + .05, duration);
            layer.AnimationInSeconds=Math.Clamp(layer.AnimationInSeconds,.05,Math.Max(.05,duration/2));
            layer.AnimationOutSeconds=Math.Clamp(layer.AnimationOutSeconds,.05,Math.Max(.05,duration/2));
            layer.Opacity=Math.Clamp(layer.Opacity,0,1);
            layer.Z=double.IsFinite(layer.Z)?layer.Z:0;
            layer.ScaleX=double.IsFinite(layer.ScaleX)&&Math.Abs(layer.ScaleX)>.0001?layer.ScaleX:1;
            layer.ScaleY=double.IsFinite(layer.ScaleY)&&Math.Abs(layer.ScaleY)>.0001?layer.ScaleY:1;
            layer.ScaleZ=double.IsFinite(layer.ScaleZ)&&Math.Abs(layer.ScaleZ)>.0001?layer.ScaleZ:1;
            layer.RotationX=double.IsFinite(layer.RotationX)?layer.RotationX:0; layer.RotationY=double.IsFinite(layer.RotationY)?layer.RotationY:0; layer.Rotation=double.IsFinite(layer.Rotation)?layer.Rotation:0;
            layer.AnchorX=double.IsFinite(layer.AnchorX)?Math.Clamp(layer.AnchorX,0,1):.5; layer.AnchorY=double.IsFinite(layer.AnchorY)?Math.Clamp(layer.AnchorY,0,1):.5; layer.AnchorZ=double.IsFinite(layer.AnchorZ)?layer.AnchorZ:0;
            layer.Perspective=double.IsFinite(layer.Perspective)?Math.Clamp(layer.Perspective,100,10000):1200;
            layer.VideoSourceKind=NormalizeVideoSourceKind(layer.VideoSourceKind);
            layer.BlendMode=NormalizeBlendMode(layer.BlendMode);
            layer.GradientAngle=double.IsFinite(layer.GradientAngle)?((layer.GradientAngle%360)+360)%360:0;
            layer.GradientStops ??=[];
            foreach(var stop in layer.GradientStops) { stop.Offset=double.IsFinite(stop.Offset)?Math.Clamp(stop.Offset,0,1):0; if(string.IsNullOrWhiteSpace(stop.Color)) stop.Color="#FFFFFFFF"; }
            var orderedStops=layer.GradientStops.OrderBy(x=>x.Offset).ToList(); layer.GradientStops.Clear(); foreach(var stop in orderedStops) layer.GradientStops.Add(stop);
            layer.MaskShape=NormalizeMaskShape(layer.MaskShape);
            if(layer.MaskEnabled)
            {
                if(!double.IsFinite(layer.MaskX)) layer.MaskX=layer.X;
                if(!double.IsFinite(layer.MaskY)) layer.MaskY=layer.Y;
                if(!double.IsFinite(layer.MaskWidth) || layer.MaskWidth<=0) layer.MaskWidth=layer.Width;
                if(!double.IsFinite(layer.MaskHeight) || layer.MaskHeight<=0) layer.MaskHeight=layer.Height;
                layer.MaskWidth=Math.Clamp(layer.MaskWidth,1,project.Width); layer.MaskHeight=Math.Clamp(layer.MaskHeight,1,project.Height);
                layer.MaskX=Math.Clamp(layer.MaskX,-project.Width,project.Width*2.0); layer.MaskY=Math.Clamp(layer.MaskY,-project.Height,project.Height*2.0);
                layer.MaskCornerRadius=double.IsFinite(layer.MaskCornerRadius)?Math.Clamp(layer.MaskCornerRadius,0,Math.Min(layer.MaskWidth,layer.MaskHeight)/2.0):0;
                layer.MaskFeather=double.IsFinite(layer.MaskFeather)?Math.Clamp(layer.MaskFeather,0,Math.Min(layer.MaskWidth,layer.MaskHeight)/2.0):0;
            }
            layer.VideoCaptureWidth=Math.Clamp(layer.VideoCaptureWidth,160,7680); layer.VideoCaptureHeight=Math.Clamp(layer.VideoCaptureHeight,90,4320);
            layer.VideoSourceFrameRate=double.IsFinite(layer.VideoSourceFrameRate)?Math.Clamp(layer.VideoSourceFrameRate,1,120):25;
            layer.SqueezeHorizontalMode=NormalizeSqueezeMode(layer.SqueezeHorizontalMode);
            layer.CornerRadiusTopLeftPercent=double.IsFinite(layer.CornerRadiusTopLeftPercent)?Math.Clamp(layer.CornerRadiusTopLeftPercent,0,50):0;
            layer.CornerRadiusTopRightPercent=double.IsFinite(layer.CornerRadiusTopRightPercent)?Math.Clamp(layer.CornerRadiusTopRightPercent,0,50):0;
            layer.CornerRadiusBottomRightPercent=double.IsFinite(layer.CornerRadiusBottomRightPercent)?Math.Clamp(layer.CornerRadiusBottomRightPercent,0,50):0;
            layer.CornerRadiusBottomLeftPercent=double.IsFinite(layer.CornerRadiusBottomLeftPercent)?Math.Clamp(layer.CornerRadiusBottomLeftPercent,0,50):0;
            // Keep bundled examples broadcast-safe. Generated templates must not render text or
            // geometry outside the 16:9 design raster, and single-line display text is scaled
            // down when its estimated glyph width would overflow its authored box.
            layer.Width=Math.Clamp(layer.Width,4,project.Width); layer.Height=Math.Clamp(layer.Height,4,project.Height);
            layer.X=Math.Clamp(layer.X,0,Math.Max(0,project.Width-layer.Width)); layer.Y=Math.Clamp(layer.Y,0,Math.Max(0,project.Height-layer.Height));
            if(layer.Type.Equals("Text",StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(layer.Text))
            {
                layer.FontSize=Math.Clamp(layer.FontSize,10,260);
                var longest=layer.Text.Replace("\r","").Split('\n').Select(x=>x.Length).DefaultIfEmpty(0).Max();
                var estimated=longest*layer.FontSize*.58;
                var singleLine=layer.Height <= layer.FontSize*2.35;
                if(singleLine && estimated>layer.Width*.94 && estimated>0) layer.FontSize=Math.Max(12,layer.FontSize*(layer.Width*.94/estimated));
            }
            layer.Keyframes ??=[];
            foreach(var key in layer.Keyframes)
            {
                key.TimeSeconds=Math.Clamp(key.TimeSeconds,0,duration); key.Opacity=Math.Clamp(key.Opacity,0,1);
                key.X=double.IsFinite(key.X)?key.X:layer.X; key.Y=double.IsFinite(key.Y)?key.Y:layer.Y; key.Z=double.IsFinite(key.Z)?key.Z:layer.Z;
                key.RotationX=double.IsFinite(key.RotationX)?key.RotationX:layer.RotationX; key.RotationY=double.IsFinite(key.RotationY)?key.RotationY:layer.RotationY; key.Rotation=double.IsFinite(key.Rotation)?key.Rotation:layer.Rotation;
                key.ScaleX=double.IsFinite(key.ScaleX)&&Math.Abs(key.ScaleX)>.0001?key.ScaleX:1;
                key.ScaleY=double.IsFinite(key.ScaleY)&&Math.Abs(key.ScaleY)>.0001?key.ScaleY:1;
                key.ScaleZ=double.IsFinite(key.ScaleZ)&&Math.Abs(key.ScaleZ)>.0001?key.ScaleZ:1;
                key.AnchorX=double.IsFinite(key.AnchorX)?Math.Clamp(key.AnchorX,0,1):layer.AnchorX;
                key.AnchorY=double.IsFinite(key.AnchorY)?Math.Clamp(key.AnchorY,0,1):layer.AnchorY;
                key.AnchorZ=double.IsFinite(key.AnchorZ)?key.AnchorZ:layer.AnchorZ;
            }
            layer.Keyframes=layer.Keyframes.OrderBy(x=>x.TimeSeconds).ToList();
            if(layer.Type.Equals("ImageSequence",StringComparison.OrdinalIgnoreCase))
            {
                layer.SequenceFps=Math.Clamp(layer.SequenceFps,1,120);
                if(layer.SequenceEndFrame>=0 && layer.SequenceEndFrame<layer.SequenceStartFrame) layer.SequenceEndFrame=layer.SequenceStartFrame;
            }
        }
        return project;
    }

    private static string NormalizeVideoSourceKind(string? value)
    {
        var v=(value??"File").Trim();
        return new[]{"File","URL","NDI","DirectShow","Screen","Custom"}.FirstOrDefault(x=>x.Equals(v,StringComparison.OrdinalIgnoreCase)) ?? "File";
    }

    private static string NormalizeMaskShape(string? value)
    {
        var v=(value??"Rectangle").Trim();
        return new[]{"Rectangle","Rounded Rectangle","Ellipse"}.FirstOrDefault(x=>x.Equals(v,StringComparison.OrdinalIgnoreCase)) ?? "Rectangle";
    }

    private static string NormalizeBlendMode(string? value)
    {
        var v=(value??"Normal").Trim();
        return new[]{"Normal","Multiply","Screen","Overlay","Darken","Lighten","Color Dodge","Color Burn","Linear Burn","Linear Dodge (Add)","Add","Hard Light","Soft Light","Vivid Light","Linear Light","Pin Light","Hard Mix","Difference","Exclusion","Subtract","Divide","Hue","Saturation","Color","Luminosity"}
            .FirstOrDefault(x=>x.Equals(v,StringComparison.OrdinalIgnoreCase)) ?? "Normal";
    }

    private static string NormalizeSqueezeMode(string? value)
    {
        var v=(value??"Both").Trim();
        return new[]{"Both","Left","Right"}.FirstOrDefault(x=>x.Equals(v,StringComparison.OrdinalIgnoreCase)) ?? "Both";
    }

    public static bool CatalogLooksHealthy(IEnumerable<CgProject> projects)
    {
        var list=projects.ToList();
        return (list.Count==200 || list.Count>=10) && list.All(p=>p.DurationSeconds>=2 && p.Layers.Count>0 && p.Layers.All(l=>l.EndSeconds>l.StartSeconds));
    }

    public static bool CatalogDesignsAreDistinct(IEnumerable<CgProject> projects)
    {
        var signatures = projects.Select(p => string.Join("|", p.Layers.Select(l =>
            $"{l.Name}:{l.Type}:{l.ShapeKind}:{l.X:0.0},{l.Y:0.0},{l.Width:0.0},{l.Height:0.0}:{l.FontFamily}:{l.FontSize:0.0}:{l.Fill}:{l.Background}:{l.GradientAngle:0.0}:{l.TextAnimationPreset}"))).ToList();
        return signatures.Count >= 1 && signatures.Distinct(StringComparer.Ordinal).Count() == signatures.Count;
    }
}
