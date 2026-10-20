using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;
using BroadcastPlayout.Ffmpeg;
using BroadcastPlayout.Models;

namespace BroadcastPlayout.Services;

public static class MediaThumbnailService
{
    public static string CacheFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KashtrixPlayout", "Thumbs");

    public static async Task<string> EnsureThumbnailAsync(PlaylistItem item, CancellationToken ct = default)
    {
        if (item.IsLiveSource || string.IsNullOrWhiteSpace(item.FilePath) || !File.Exists(item.FilePath)) return string.Empty;
        Directory.CreateDirectory(CacheFolder);
        var stamp = File.GetLastWriteTimeUtc(item.FilePath).Ticks;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(item.FilePath + "|" + stamp)))[..20];
        var target = Path.Combine(CacheFolder, hash + ".jpg");
        if (File.Exists(target)) return target;

        var sourceTime = item.SourceDuration > TimeSpan.Zero
            ? TimeSpan.FromSeconds(Math.Min(10, item.SourceDuration.TotalSeconds * .12))
            : TimeSpan.Zero;
        var frame = await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var decoder = new VideoDecoder(item);
            if (sourceTime > TimeSpan.Zero) decoder.Seek(sourceTime);
            while (decoder.TryRead(out var f))
            {
                ct.ThrowIfCancellationRequested();
                if (f.PtsSeconds + .05 >= sourceTime.TotalSeconds) return f;
            }
            return null;
        }, ct).ConfigureAwait(false);
        if (frame is null) return string.Empty;

        await Task.Run(() =>
        {
            var source = BitmapSource.Create(frame.Width, frame.Height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, frame.Bgra, frame.Stride);
            source.Freeze();
            var scale = Math.Min(320.0 / Math.Max(1, frame.Width), 180.0 / Math.Max(1, frame.Height));
            BitmapSource output = source;
            if (scale < .999)
            {
                var transformed = new TransformedBitmap(source, new System.Windows.Media.ScaleTransform(scale, scale));
                transformed.Freeze(); output = transformed;
            }
            var encoder = new JpegBitmapEncoder { QualityLevel = 78 };
            encoder.Frames.Add(BitmapFrame.Create(output));
            using var fs = File.Create(target); encoder.Save(fs);
        }, ct).ConfigureAwait(false);
        return target;
    }
}
