using System.Diagnostics;
using System.Text.Json;

namespace BroadcastPlayout.Services;

public static class StandaloneAppLauncher
{
    private sealed record AppInfo(string ProjectName, string ExeName, string? DisplayName = null)
    {
        public string UserFacingName => string.IsNullOrWhiteSpace(DisplayName) ? ProjectName : DisplayName;
    }

    private static readonly Dictionary<string, AppInfo> Apps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CGEditor"] = new("Kashtrix.CGEditor", "Kashtrix.CGEditor.exe"),
        ["Scheduler"] = new("Kashtrix.Scheduler", "Kashtrix.Scheduler.exe"),
        ["PlaylistEditor"] = new("Kashtrix.PlaylistEditor", "Kashtrix.PlaylistEditor.exe"),
        ["Settings"] = new("Kashtrix.Settings", "Kashtrix.Settings.exe"),
        ["Multiview"] = new("Kashtrix.Multiview", "Kashtrix.Multiview.exe"),
        ["ChannelController"] = new("Kashtrix.ChannelController", "Kashtrix.ChannelController.exe"),
        ["CGController"] = new("Kashtrix.CGController", "Kashtrix.CGController.exe"),
        ["FileManager"] = new("Kashtrix.FileManager", "Kashtrix.FileManager.exe"),
        ["QCController"] = new("Kashtrix.QCController", "Kashtrix.QCController.exe"),
        ["IngestServer"] = new("Kashtrix.IngestServer", "Kashtrix.IngestServer.exe"),
        ["MAM"] = new("Kashtrix.MAM", "Kashtrix.MAM.exe"),
        ["NRCS"] = new("Kashtrix.NRCS", "Kashtrix.NRCS.exe"),
        ["Prompter"] = new("Kashtrix.Prompter", "Kashtrix.Prompter.exe"),
        ["HAController"] = new("Kashtrix.HAController", "Kashtrix.HAController.exe"),
        ["ApiGateway"] = new("Kashtrix.ApiGateway", "Kashtrix.ApiGateway.exe"),
        ["OutputEngine"] = new("Kashtrix.OutputEngine", "Kashtrix.OutputEngine.exe", "Kashtrix Output Engine")
    };

    public static bool Launch(string appKey, string? arguments = null)
    {
        if (!Apps.TryGetValue(appKey, out var app))
            throw new ArgumentOutOfRangeException(nameof(appKey), appKey, "Unknown Kashtrix application.");

        var exe = ResolveExecutable(app);
        if (exe is null) return false;

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = arguments ?? string.Empty,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = false
        });
        return process is not null;
    }

    public static bool Launch(string appKey, out string error, string? arguments = null)
    {
        try
        {
            if (!Apps.TryGetValue(appKey, out var app))
            {
                error = $"Unknown Kashtrix application: {appKey}";
                return false;
            }

            var exe = ResolveExecutable(app);
            if (exe is null)
            {
                error = $"{app.UserFacingName} executable was not found. Run tools\\Verify-And-Build.ps1 once so the suite manifest is refreshed.";
                return false;
            }

            var process = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments ?? string.Empty,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
            CreateNoWindow = false
            });

            if (process is null)
            {
                error = $"Windows did not start {app.UserFacingName}.";
                return false;
            }

            // Detect immediate startup failures instead of making the launcher appear to do nothing.
            if (process.WaitForExit(900))
            {
                var log = StandaloneAppDiagnostics.LogPath(app.UserFacingName);
                error = $"{app.UserFacingName} exited during startup (exit code {process.ExitCode})." +
                        (File.Exists(log) ? $" Startup log: {log}" : string.Empty);
                return false;
            }

            AuditLogService.Write("APP_LAUNCH", app.UserFacingName, exe);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Unable to open {appKey}: {ex.Message}";
            return false;
        }
    }

    public static string? ResolveExecutable(string exeName)
    {
        var app = Apps.Values.FirstOrDefault(x => x.ExeName.Equals(exeName, StringComparison.OrdinalIgnoreCase));
        return app is null ? null : ResolveExecutable(app);
    }

    private static string? ResolveExecutable(AppInfo app)
    {
        // 1) Installed/published suites may place every app beside Playout.
        var local = Path.Combine(AppContext.BaseDirectory, app.ExeName);
        if (File.Exists(local)) return local;

        // 2) Verify-And-Build writes exact TargetPath values here. This avoids accidentally
        // launching an older build from a different Debug/Release or framework directory.
        var manifestPath = SuiteManifestPath;
        if (File.Exists(manifestPath))
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(manifestPath));
                if (manifest is not null && manifest.TryGetValue(app.ProjectName, out var exact) && File.Exists(exact))
                    return exact;
            }
            catch { }
        }

        // 3) Deterministic build-cache fallback restricted to this project's output tree.
        var buildRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KashtrixPlayout", "Build", "bin");
        var projectRoot = Path.Combine(buildRoot, app.ProjectName);
        if (Directory.Exists(projectRoot))
        {
            try
            {
                return Directory.EnumerateFiles(projectRoot, app.ExeName, SearchOption.AllDirectories)
                    .Select(path => new FileInfo(path))
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .Select(file => file.FullName)
                    .FirstOrDefault();
            }
            catch { }
        }

        // Playout itself uses BroadcastPlayout.App as the build-cache project directory.
        if (app.ProjectName.Equals("Kashtrix.Playout", StringComparison.OrdinalIgnoreCase))
        {
            var playoutRoot = Path.Combine(buildRoot, "BroadcastPlayout.App");
            if (Directory.Exists(playoutRoot))
            {
                try
                {
                    return Directory.EnumerateFiles(playoutRoot, app.ExeName, SearchOption.AllDirectories)
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .FirstOrDefault();
                }
                catch { }
            }
        }

        return null;
    }

    public static string SuiteManifestPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KashtrixPlayout", "suite-apps.json");
}
