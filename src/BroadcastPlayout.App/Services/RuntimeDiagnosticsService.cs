using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace BroadcastPlayout.Services;

/// <summary>
/// Lightweight append-only runtime telemetry used by LIVE-DEBUG.cmd. This service is intentionally
/// failure-isolated: diagnostics must never interrupt playout. Every record is one JSON object per
/// line so the PowerShell live recorder can tail the file while the process is running.
/// </summary>
public static class RuntimeDiagnosticsService
{
    private static readonly object Gate = new();
    private static string _appName = "";
    private static string _path = "";
    private static long _sequence;

    public static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KashtrixPlayout", "Logs", "Runtime");

    public static string CurrentPath
    {
        get
        {
            EnsureInitialized(null);
            return _path;
        }
    }

    public static void Initialize(string? appName)
    {
        EnsureInitialized(appName);
        Write("PROCESS_START", "Process diagnostics initialized.", new
        {
            process = Process.GetCurrentProcess().ProcessName,
            pid = Environment.ProcessId,
            architecture = Environment.Is64BitProcess ? "x64" : "x86",
            os = Environment.OSVersion.VersionString,
            runtime = Environment.Version.ToString(),
            commandLine = Environment.CommandLine
        });
    }

    public static void Write(string category, string? message = null, object? data = null)
    {
        try
        {
            EnsureInitialized(null);
            var record = new
            {
                timestampUtc = DateTime.UtcNow,
                sequence = Interlocked.Increment(ref _sequence),
                app = _appName,
                processId = Environment.ProcessId,
                threadId = Environment.CurrentManagedThreadId,
                category = string.IsNullOrWhiteSpace(category) ? "EVENT" : category.Trim(),
                message = message?.Trim() ?? "",
                data
            };
            var line = JsonSerializer.Serialize(record);
            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                File.AppendAllText(_path, line + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch { }
    }

    public static void WriteException(string category, Exception ex, string? context = null)
    {
        if (ex is null) return;
        try
        {
            Write(category, context ?? ex.Message, new
            {
                exceptionType = ex.GetType().FullName,
                ex.Message,
                ex.StackTrace,
                inner = ex.InnerException?.ToString()
            });
        }
        catch { }
    }

    public static void WriteProcessHeartbeat(string? state = null)
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            Write("PROCESS_HEARTBEAT", state ?? "", new
            {
                workingSetMb = Math.Round(p.WorkingSet64 / 1048576d, 1),
                privateMb = Math.Round(p.PrivateMemorySize64 / 1048576d, 1),
                totalProcessorSeconds = Math.Round(p.TotalProcessorTime.TotalSeconds, 3),
                threads = p.Threads.Count,
                handles = p.HandleCount,
                gcMb = Math.Round(GC.GetTotalMemory(false) / 1048576d, 1)
            });
        }
        catch { }
    }

    private static void EnsureInitialized(string? appName)
    {
        if (!string.IsNullOrWhiteSpace(_path)) return;
        lock (Gate)
        {
            if (!string.IsNullOrWhiteSpace(_path)) return;
            _appName = string.IsNullOrWhiteSpace(appName)
                ? Process.GetCurrentProcess().ProcessName
                : appName.Trim();
            var safe = string.Join("_", _appName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
            Directory.CreateDirectory(DirectoryPath);
            _path = Path.Combine(DirectoryPath, $"runtime-{safe}-{Environment.ProcessId}-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
        }
    }
}
