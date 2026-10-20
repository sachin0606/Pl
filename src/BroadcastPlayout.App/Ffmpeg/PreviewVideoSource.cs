using System.Diagnostics;
using BroadcastPlayout.Models;
using BroadcastPlayout.Services;

namespace BroadcastPlayout.Ffmpeg;

/// <summary>
/// Plays a file as a small, silent, real-time preview for a CG box (the music package's NEXT / LATER
/// window). Decoding runs on its own background thread and only the newest frame is kept, so the
/// Program compositor never waits for it. Frames come from the decoder's ring buffer and are scaled
/// down while converting, so a preview costs a few hundred KB per frame instead of a full HD buffer.
/// Dispose stops the worker; nothing else is retained.
/// </summary>
public sealed class PreviewVideoSource : IDisposable
{
    private readonly string _path;
    private readonly double _startSeconds;
    private readonly int _maxWidth;
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _worker;
    private VideoFrameData? _latest;

    public PreviewVideoSource(string path, double startSeconds, int maxWidth)
    {
        _path = path;
        _startSeconds = Math.Max(0, startSeconds);
        _maxWidth = Math.Clamp(maxWidth, 64, 1920);
        _worker = new Thread(Run) { IsBackground = true, Name = "Kashtrix CG preview", Priority = ThreadPriority.BelowNormal };
        _worker.Start();
    }

    public string Path => _path;
    public double StartSeconds => _startSeconds;

    /// <summary>Newest decoded frame, or null until the first one is ready (or when the file cannot be read).</summary>
    public VideoFrameData? Latest => Volatile.Read(ref _latest);

    private void Run()
    {
        var token = _stop.Token;
        var failures = 0;
        var start = _startSeconds;
        try
        {
            while (!token.IsCancellationRequested && failures < 3)
            {
                var shown = 0;
                try
                {
                    // CPU decode on two threads: opening a GPU device for each short preview costs more
                    // (device + driver threads) than it saves, and leaves the GPU to the Program decoder.
                    using var decoder = new VideoDecoder(new PlaylistItem { FilePath = _path, SourceKind = "File", DecoderPreference = "CPU" }, null, _maxWidth);
                    if (start > 0) decoder.Seek(TimeSpan.FromSeconds(start));
                    var clock = Stopwatch.StartNew();
                    double? firstPts = null;
                    while (!token.IsCancellationRequested)
                    {
                        if (!decoder.TryRead(out var frame)) break; // end of file: reopen and loop from the start point
                        if (frame.PtsSeconds + .02 < start) continue; // decoding up to the seek target
                        firstPts ??= frame.PtsSeconds;
                        var due = frame.PtsSeconds - firstPts.Value - clock.Elapsed.TotalSeconds;
                        if (due > 0 && token.WaitHandle.WaitOne(TimeSpan.FromSeconds(Math.Min(due, 1.0)))) break;
                        Volatile.Write(ref _latest, frame);
                        shown++;
                    }
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    RuntimeDiagnosticsService.WriteException("CG_PREVIEW_VIDEO_ERROR", ex);
                }
                // Start point past the real end (wrong duration metadata): play from the beginning instead.
                if (shown == 0 && start > 0 && !token.IsCancellationRequested) { start = 0; continue; }
                // A pass that showed nothing (unreadable file) counts as a failure,
                // so a bad file stops after a few attempts instead of reopening in a loop.
                if (shown == 0) failures++; else failures = 0;
                if (shown == 0 && token.WaitHandle.WaitOne(500)) break;
            }
        }
        finally
        {
            _stop.Dispose();
        }
    }

    /// <summary>Signals the worker and returns at once; the worker closes the decoder within one frame.</summary>
    public void Dispose()
    {
        try { _stop.Cancel(); } catch (ObjectDisposedException) { }
    }
}
