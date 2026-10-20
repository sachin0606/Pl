using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace BroadcastPlayout.Services;

public static class StandaloneAppDiagnostics
{
    private static int _globalHandlersRegistered;
    private static System.Threading.Timer? _heartbeatTimer;
    public static string LogPath(string appName)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KashtrixPlayout", "Logs");
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, appName.Replace(' ', '_') + ".startup.log");
    }

    public static void Attach(Application app, string appName)
    {
        RuntimeDiagnosticsService.Initialize(appName);
        _heartbeatTimer ??= new System.Threading.Timer(_ => RuntimeDiagnosticsService.WriteProcessHeartbeat("APP_RUNNING"), null, 2000, 5000);
        Write(appName, "START", null);
        if (Interlocked.Exchange(ref _globalHandlersRegistered, 1) == 0)
        {
            EventManager.RegisterClassHandler(typeof(Window), Keyboard.PreviewKeyDownEvent, new KeyEventHandler((sender, e) =>
            {
                if (e.Handled || e.Key != Key.Escape || sender is not Window window || !window.IsActive) return;
                var name = window.GetType().Name;
                if (name is "MainWindow" or "SettingsWindow" or "MediaAssetManagementWindow" or "IngestServerWindow"
                    or "FrameComparisonWindow" or "MultiviewWindow" or "CgEditorWindow" or "CgControllerWindow"
                    or "SchedulerWindow" or "ChannelManagerWindow" or "ChannelControllerWindow" or "AuditLogWindow"
                    or "VideoProcessorWindow" or "InputSourceWindow" or "AudioMixerWindow" or "QcControllerWindow"
                    or "FileManagerWindow" or "UserManagementWindow" or "EpgOutputSettingsWindow" or "BroadcastTimersClockWindow")
                    return;
                try { window.Close(); e.Handled = true; } catch { }
            }));
        }
        app.DispatcherUnhandledException += (_, e) =>
        {
            Write(appName, "DISPATCHER UNHANDLED", e.Exception);
            var smoke = Environment.GetCommandLineArgs().Any(x => string.Equals(x, "--startup-smoke", StringComparison.OrdinalIgnoreCase));
            if (smoke)
            {
                e.Handled = true;
                Environment.ExitCode = -1;
                try { app.Shutdown(-1); } catch { }
                return;
            }
            try
            {
                var details = e.Exception.Message;
                var inner = e.Exception.InnerException;
                if (inner is not null) details += "\n\nCause: " + inner.Message;
                if (inner?.InnerException is not null) details += "\n\nRoot cause: " + inner.InnerException.Message;
                BroadcastPlayout.Views.MessageBox.Show($"{appName} encountered an error.\n\n{details}\n\nLog: {LogPath(appName)}", appName, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write(appName, "APPDOMAIN UNHANDLED", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => Write(appName, "TASK UNOBSERVED", e.Exception);
    }

    public static void Ready(string appName) { Write(appName, "WINDOW READY", null); RuntimeDiagnosticsService.Write("WINDOW_READY", appName); }
    public static void Failure(string appName, Exception ex) { Write(appName, "STARTUP FAILED", ex); RuntimeDiagnosticsService.WriteException("STARTUP_FAILED", ex, appName); }

    private static void Write(string appName, string state, Exception? ex)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(" | ").Append(state);
            if (ex is not null) sb.AppendLine().Append(ex);
            sb.AppendLine();
            File.AppendAllText(LogPath(appName), sb.ToString(), new UTF8Encoding(false));
            if (ex is null) RuntimeDiagnosticsService.Write("APP_STATE", state, new { appName });
            else RuntimeDiagnosticsService.WriteException(state.Replace(' ', '_'), ex, appName);
        }
        catch { }
    }
}
