using BroadcastPlayout.Ffmpeg;
using BroadcastPlayout.Models;

namespace BroadcastPlayout.Services;

public static class MediaLibraryService
{
    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".mxf", ".mkv", ".avi", ".ts", ".m2ts", ".mpg", ".mpeg", ".webm", ".wmv",
        ".mp3", ".wav", ".flac", ".aac", ".m4a", ".ogg", ".opus",
        ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".gif"
    };

    /// <summary>
    /// Fault-tolerant media scan. Unlike SearchOption.AllDirectories, inaccessible folders do not
    /// cancel the entire drive scan. Reparse points are skipped to avoid junction/symlink loops.
    /// </summary>
    public static IReadOnlyList<MediaLibraryItem> ScanFolder(string folder, bool recursive = true, int maxItems = int.MaxValue)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder) || maxItems <= 0) return [];

        var items = new List<MediaLibraryItem>(Math.Min(maxItems, 4096));
        var pending = new Stack<string>();
        pending.Push(folder);

        while (pending.Count > 0 && items.Count < maxItems)
        {
            var current = pending.Pop();
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(current, "*.*", SearchOption.TopDirectoryOnly); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }
            catch { continue; }

            try
            {
                foreach (var path in files)
                {
                    if (!Extensions.Contains(Path.GetExtension(path))) continue;
                    try
                    {
                        var fi = new FileInfo(path);
                        items.Add(new MediaLibraryItem
                        {
                            FilePath = path,
                            SizeBytes = fi.Length,
                            ModifiedUtc = fi.LastWriteTimeUtc,
                            Category = MediaCategoryStore.Get(path)
                        });
                    }
                    catch { /* One broken/offline file must not abort the scan. */ }
                    if (items.Count >= maxItems) break;
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }

            if (!recursive || items.Count >= maxItems) continue;

            IEnumerable<string> directories;
            try { directories = Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }
            catch { continue; }

            try
            {
                foreach (var child in directories)
                {
                    try
                    {
                        var attributes = File.GetAttributes(child);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                        pending.Push(child);
                    }
                    catch { }
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }

        return items.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static async Task ProbeAsync(MediaLibraryItem item, CancellationToken ct = default)
    {
        try
        {
            var ext = Path.GetExtension(item.FilePath);
            if (new[] { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".gif" }.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                item.Duration = "STILL";
                item.Format = $"Still image · {item.Extension}";
                item.ThumbnailPath = item.FilePath;
                return;
            }
            var info = await Task.Run(() => MediaProbe.Read(item.FilePath), ct).ConfigureAwait(false);
            item.Duration = Timecode.Format(info.Duration);
            item.Format = info.Width > 0 && info.Height > 0
                ? $"{info.Width}x{info.Height} · {info.FramesPerSecond:0.###} fps · {info.VideoCodec}"
                : $"Audio · {info.VideoCodec}";
            if (info.Width > 0 && info.Height > 0)
            {
                var temp = new PlaylistItem { FilePath=item.FilePath, SourceKind="File", SourceDuration=info.Duration, OutPoint=info.Duration, SourceFrameRate=info.FramesPerSecond };
                item.ThumbnailPath = await MediaThumbnailService.EnsureThumbnailAsync(temp, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { item.Format = "Probe error · " + ex.Message; }
    }
}
