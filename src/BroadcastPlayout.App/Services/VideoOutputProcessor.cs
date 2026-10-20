using BroadcastPlayout.Models;

namespace BroadcastPlayout.Services;

public readonly record struct VideoOutputAdjustments(
    bool Enabled,
    int Brightness,
    int Contrast,
    int Saturation,
    double Gamma,
    int HueDegrees,
    double ExposureStops,
    int Temperature,
    int Tint,
    int Sharpness,
    int BlackLevel,
    int WhiteLevel,
    int RedGainPercent,
    int GreenGainPercent,
    int BlueGainPercent)
{
    public bool IsNeutral => !Enabled ||
        (Brightness == 0 && Contrast == 100 && Saturation == 100 && Math.Abs(Gamma - 1.0) < 0.0001 &&
         HueDegrees == 0 && Math.Abs(ExposureStops) < 0.0001 && Temperature == 0 && Tint == 0 && Sharpness == 0 &&
         BlackLevel == 0 && WhiteLevel == 0 && RedGainPercent == 100 && GreenGainPercent == 100 && BlueGainPercent == 100);
}

/// <summary>
/// Final-program BGRA processor. The source frame is never mutated: when processing is enabled a new
/// frame buffer is returned, so Preview/source caching and CG source frames remain stable.
/// </summary>
public static class VideoOutputProcessor
{
    private static readonly object LutCacheSync = new();
    private static VideoOutputAdjustments _lutCacheKey;
    private static (byte[] Red, byte[] Green, byte[] Blue)? _lutCache;

    public static VideoFrameData Apply(VideoFrameData frame, VideoOutputAdjustments a)
    {
        if (a.IsNeutral || frame.Bgra.Length == 0 || frame.Width <= 0 || frame.Height <= 0) return frame;
        var dst = (byte[])frame.Bgra.Clone();
        Process(frame, frame.Bgra, dst, a);
        return new VideoFrameData(dst, frame.Width, frame.Height, frame.Stride, frame.PtsSeconds, frame.FrameRateNumerator, frame.FrameRateDenominator);
    }

    /// <summary>Processes the frame's own buffer. The caller must own it exclusively (Program cadence buffer).</summary>
    public static VideoFrameData ApplyInPlace(VideoFrameData frame, VideoOutputAdjustments a)
    {
        if (a.IsNeutral || frame.Bgra.Length == 0 || frame.Width <= 0 || frame.Height <= 0) return frame;
        Process(frame, frame.Bgra, frame.Bgra, a);
        return frame;
    }

    private static (byte[] Red, byte[] Green, byte[] Blue) GetLuts(VideoOutputAdjustments a, double redGain, double greenGain, double blueGain,
        double exposure, double contrast, double brightness, double black, double white, double invGamma)
    {
        lock (LutCacheSync)
        {
            if (_lutCache is { } cached && _lutCacheKey.Equals(a)) return cached;
            var luts = (BuildGradeLut(redGain, exposure, contrast, brightness, black, white, invGamma),
                BuildGradeLut(greenGain, exposure, contrast, brightness, black, white, invGamma),
                BuildGradeLut(blueGain, exposure, contrast, brightness, black, white, invGamma));
            _lutCacheKey = a;
            _lutCache = luts;
            return luts;
        }
    }

    // Per-pixel independent, so src and dst may be the same buffer. Sharpening uses a pooled
    // scratch copy because its kernel reads neighbouring pixels.
    private static void Process(VideoFrameData frame, byte[] src, byte[] dst, VideoOutputAdjustments a)
    {
        var stride = Math.Max(frame.Stride, frame.Width * 4);

        var brightness = Math.Clamp(a.Brightness, -100, 100) * 2.55;
        var contrast = Math.Clamp(a.Contrast, 0, 200) / 100.0;
        var saturation = Math.Clamp(a.Saturation, 0, 200) / 100.0;
        var gamma = Math.Clamp(a.Gamma, 0.10, 4.0);
        var invGamma = 1.0 / gamma;
        var exposure = Math.Pow(2.0, Math.Clamp(a.ExposureStops, -4.0, 4.0));
        var temp = Math.Clamp(a.Temperature, -100, 100) / 100.0;
        var tint = Math.Clamp(a.Tint, -100, 100) / 100.0;
        var black = Math.Clamp(a.BlackLevel, -100, 100) * 0.45;
        var white = Math.Clamp(a.WhiteLevel, -100, 100) * 0.45;
        var redGain = Math.Clamp(a.RedGainPercent, 0, 200) / 100.0;
        var greenGain = Math.Clamp(a.GreenGainPercent, 0, 200) / 100.0;
        var blueGain = Math.Clamp(a.BlueGainPercent, 0, 200) / 100.0;

        // Temperature/tint are intentionally conservative broadcast trims rather than aggressive color grading.
        redGain *= 1.0 + temp * 0.18 + tint * 0.08;
        blueGain *= 1.0 - temp * 0.18 + tint * 0.08;
        greenGain *= 1.0 - tint * 0.12;

        var hue = Math.Clamp(a.HueDegrees, -180, 180) * Math.PI / 180.0;
        var cosH = Math.Cos(hue);
        var sinH = Math.Sin(hue);
        var (redLut, greenLut, blueLut) = GetLuts(a, redGain, greenGain, blueGain, exposure, contrast, brightness, black, white, invGamma);


        // The overwhelmingly common broadcast correction path has neutral hue/saturation.
        // It now becomes three 256-entry table lookups per pixel instead of repeated floating
        // point YIQ transforms and Math.Pow calls at 25/50/60 fps.
        var simpleColorPath = Math.Abs(hue) < 0.0001 && Math.Abs(saturation - 1.0) < 0.0001;
        if (simpleColorPath)
            ApplyLuts(src, dst, frame.Width, frame.Height, stride, blueLut, greenLut, redLut);
        else
        for (var y = 0; y < frame.Height; y++)
        {
            var row = y * stride;
            var rowEnd = Math.Min(src.Length, row + frame.Width * 4);
            for (var i = row; i + 3 < rowEnd; i += 4)
            {
                var b = src[i + 0];
                var g = src[i + 1];
                var r = src[i + 2];
                var yy = 0.299 * r + 0.587 * g + 0.114 * b;
                var ii = 0.596 * r - 0.274 * g - 0.322 * b;
                var qq = 0.211 * r - 0.523 * g + 0.312 * b;
                var i2 = (ii * cosH - qq * sinH) * saturation;
                var q2 = (ii * sinH + qq * cosH) * saturation;
                var rr = yy + 0.956 * i2 + 0.621 * q2;
                var gg = yy - 0.272 * i2 - 0.647 * q2;
                var bb = yy - 1.106 * i2 + 1.703 * q2;

                dst[i + 0] = blueLut[ClampToByteIndex(bb)];
                dst[i + 1] = greenLut[ClampToByteIndex(gg)];
                dst[i + 2] = redLut[ClampToByteIndex(rr)];
                dst[i + 3] = src[i + 3];
            }
        }

        var sharpness = Math.Clamp(a.Sharpness, 0, 100);
        if (sharpness > 0 && frame.Width > 2 && frame.Height > 2)
            ApplySharpenInPlace(dst, frame.Width, frame.Height, stride, sharpness / 100.0);
    }


    // Three table lookups per pixel over a few horizontal bands in parallel. src and dst may alias.
    private static unsafe void ApplyLuts(byte[] src, byte[] dst, int width, int height, int stride, byte[] blue, byte[] green, byte[] red)
    {
        var rowBytes = Math.Min(width * 4, stride);
        var pixels = rowBytes / 4;
        height = Math.Min(height, Math.Min(src.Length, dst.Length) / Math.Max(1, stride));
        if (height <= 0 || pixels <= 0) return;
        var bands = Math.Clamp(Environment.ProcessorCount / 4, 1, 4);
        var rowsPerBand = (height + bands - 1) / bands;
        Parallel.For(0, bands, band =>
        {
            var y0 = band * rowsPerBand;
            var y1 = Math.Min(height, y0 + rowsPerBand);
            fixed (byte* s = src, d = dst, lb = blue, lg = green, lr = red)
            {
                for (var y = y0; y < y1; y++)
                {
                    var ps = s + (long)y * stride;
                    var pd = d + (long)y * stride;
                    for (var x = 0; x < pixels; x++, ps += 4, pd += 4)
                    {
                        pd[0] = lb[ps[0]];
                        pd[1] = lg[ps[1]];
                        pd[2] = lr[ps[2]];
                        pd[3] = ps[3];
                    }
                }
            }
        });
    }

    private static byte[] BuildGradeLut(double gain, double exposure, double contrast, double brightness, double black, double white, double invGamma)
    {
        var lut = new byte[256];
        for (var i = 0; i < lut.Length; i++)
            lut[i] = ToByte(Grade(i, gain, exposure, contrast, brightness, black, white, invGamma));
        return lut;
    }

    private static int ClampToByteIndex(double value)
        => value <= 0 ? 0 : value >= 255 ? 255 : (int)(value + 0.5);

    private static double Grade(double v, double gain, double exposure, double contrast, double brightness, double black, double white, double invGamma)
    {
        v *= gain * exposure;
        v = (v - 127.5) * contrast + 127.5 + brightness;
        // Black/white trims are endpoints, not just another brightness control.
        v = (v - black) * (255.0 / Math.Max(1.0, 255.0 + white - black));
        v = Math.Clamp(v, 0.0, 255.0);
        if (Math.Abs(invGamma - 1.0) > 0.0001)
            v = 255.0 * Math.Pow(v / 255.0, invGamma);
        return v;
    }

    private static byte ToByte(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);

    private static void ApplySharpenInPlace(byte[] buffer, int width, int height, int stride, double strength)
    {
        // One light unsharp pass. Keep a copy so the kernel never reads already-modified neighbours.
        var length = Math.Min(buffer.Length, stride * height);
        var src = System.Buffers.ArrayPool<byte>.Shared.Rent(length);
        try
        {
            Buffer.BlockCopy(buffer, 0, src, 0, length);
            var amount = 0.65 * strength;
            for (var y = 1; y < height - 1; y++)
            {
                var row = y * stride;
                for (var x = 1; x < width - 1; x++)
                {
                    var p = row + x * 4;
                    for (var c = 0; c < 3; c++)
                    {
                        var center = src[p + c];
                        var blur = (src[p - 4 + c] + src[p + 4 + c] + src[p - stride + c] + src[p + stride + c]) * 0.25;
                        buffer[p + c] = ToByte(center + (center - blur) * amount);
                    }
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(src);
        }
    }
}
