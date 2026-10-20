using BroadcastPlayout.Ffmpeg;
using BroadcastPlayout.Models;

namespace BroadcastPlayout.Services;

/// <summary>
/// Safe per-rundown-item geometry/interlace processing.  It operates on decoded BGRA frames
/// before CG/output processing so Program, Preview, SDI, NDI and Virtual Output see the same
/// operator-selected aspect/scale policy.
/// </summary>
public static class ItemVideoProcessor
{
    public static VideoFrameData Apply(VideoFrameData source, PlaylistItem item, BgraFrameScaler scaler)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(item);
        var current = source;

        var aspect = ResolveAspect(item.AspectRatioMode, item.CustomAspectRatio);
        var scale = (item.ScaleMode ?? "Fit").Trim();
        if (aspect > 0.05 && !scale.Equals("Stretch", StringComparison.OrdinalIgnoreCase))
        {
            if (scale.Equals("Fill", StringComparison.OrdinalIgnoreCase))
                current = Fill(current, aspect, scaler);
            else if (!scale.Equals("Original", StringComparison.OrdinalIgnoreCase))
                current = Fit(current, aspect, scaler);
        }

        var interlace = (item.InterlaceMode ?? "Auto").Trim();
        if (interlace.Equals("Deinterlace", StringComparison.OrdinalIgnoreCase))
            current = Deinterlace(current);
        else if (interlace.Equals("Top Field First", StringComparison.OrdinalIgnoreCase))
            current = FieldOnly(current, evenField: true);
        else if (interlace.Equals("Bottom Field First", StringComparison.OrdinalIgnoreCase))
            current = FieldOnly(current, evenField: false);

        return current;
    }

    private static double ResolveAspect(string? mode, string? custom)
    {
        var value = string.IsNullOrWhiteSpace(mode) ? "Auto" : mode.Trim();
        if (value.Equals("Auto", StringComparison.OrdinalIgnoreCase)) return 0;
        if (value.Equals("Custom", StringComparison.OrdinalIgnoreCase)) value = custom ?? "";
        if (value.Equals("16:9", StringComparison.OrdinalIgnoreCase)) return 16.0 / 9.0;
        if (value.Equals("4:3", StringComparison.OrdinalIgnoreCase)) return 4.0 / 3.0;
        if (value.Equals("1:1", StringComparison.OrdinalIgnoreCase)) return 1.0;
        if (value.Equals("21:9", StringComparison.OrdinalIgnoreCase)) return 21.0 / 9.0;
        var parts = value.Split(':', '/', 'x', 'X');
        if (parts.Length == 2 && double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var a)
            && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var b) && a > 0 && b > 0)
            return a / b;
        return 0;
    }

    private static VideoFrameData Fit(VideoFrameData source, double aspect, BgraFrameScaler scaler)
    {
        var canvasAspect = source.Width / (double)Math.Max(1, source.Height);
        var w = source.Width; var h = source.Height;
        if (canvasAspect > aspect) w = Math.Max(2, (int)Math.Round(h * aspect));
        else h = Math.Max(2, (int)Math.Round(w / aspect));
        w &= ~1; h &= ~1;
        if (w <= 0 || h <= 0 || (w == source.Width && h == source.Height)) return source;
        var scaled = scaler.ScaleReusable(source, w, h);
        var output = new byte[source.Stride * source.Height];
        var x = Math.Max(0, (source.Width - w) / 2);
        var y = Math.Max(0, (source.Height - h) / 2);
        var rowBytes = Math.Min(w * 4, scaled.Stride);
        for (var row = 0; row < h; row++)
            Buffer.BlockCopy(scaled.Bgra, row * scaled.Stride, output, (y + row) * source.Stride + x * 4, rowBytes);
        return new VideoFrameData(output, source.Width, source.Height, source.Stride, source.PtsSeconds, source.FrameRateNumerator, source.FrameRateDenominator);
    }

    private static VideoFrameData Fill(VideoFrameData source, double aspect, BgraFrameScaler scaler)
    {
        var sourceAspect = source.Width / (double)Math.Max(1, source.Height);
        var cropW = source.Width; var cropH = source.Height;
        if (sourceAspect > aspect) cropW = Math.Max(2, (int)Math.Round(cropH * aspect));
        else cropH = Math.Max(2, (int)Math.Round(cropW / aspect));
        cropW &= ~1; cropH &= ~1;
        if (cropW == source.Width && cropH == source.Height) return source;
        var x = Math.Max(0, (source.Width - cropW) / 2);
        var y = Math.Max(0, (source.Height - cropH) / 2);
        var stride = cropW * 4;
        var cropped = new byte[stride * cropH];
        for (var row = 0; row < cropH; row++)
            Buffer.BlockCopy(source.Bgra, (y + row) * source.Stride + x * 4, cropped, row * stride, stride);
        var frame = new VideoFrameData(cropped, cropW, cropH, stride, source.PtsSeconds, source.FrameRateNumerator, source.FrameRateDenominator);
        return scaler.Scale(frame, source.Width, source.Height);
    }

    private static VideoFrameData Deinterlace(VideoFrameData source)
    {
        if (source.Height < 3) return source;
        var output = (byte[])source.Bgra.Clone();
        for (var y = 1; y < source.Height - 1; y += 2)
        {
            var above = (y - 1) * source.Stride;
            var row = y * source.Stride;
            var below = (y + 1) * source.Stride;
            for (var x = 0; x < source.Width * 4; x++) output[row + x] = (byte)((output[above + x] + output[below + x]) >> 1);
        }
        return source with { Bgra = output };
    }

    private static VideoFrameData FieldOnly(VideoFrameData source, bool evenField)
    {
        if (source.Height < 2) return source;
        var output = (byte[])source.Bgra.Clone();
        var keepParity = evenField ? 0 : 1;
        for (var y = 0; y < source.Height; y++)
        {
            if ((y & 1) == keepParity) continue;
            var copyFrom = y > 0 ? y - 1 : Math.Min(source.Height - 1, y + 1);
            Buffer.BlockCopy(output, copyFrom * source.Stride, output, y * source.Stride, source.Width * 4);
        }
        return source with { Bgra = output };
    }
}
