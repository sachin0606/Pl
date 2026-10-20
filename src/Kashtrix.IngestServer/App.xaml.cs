using System;
using BroadcastPlayout.Services;

namespace Kashtrix.IngestServer;

public partial class App : System.Windows.Application
{
    private const string AppName = "Kashtrix.IngestServer";
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        StandaloneAppDiagnostics.Attach(this, AppName);
        var startupSmoke = Array.Exists(e.Args, x => string.Equals(x, "--startup-smoke", StringComparison.OrdinalIgnoreCase));
        try
        {
            BroadcastPlayout.Ffmpeg.FfmpegRuntime.Initialize(System.IO.Path.Combine(AppContext.BaseDirectory, "native", "ffmpeg"));
            var window = new BroadcastPlayout.Views.IngestServerWindow();
            MainWindow = window;
            window.Show();
            StandaloneAppDiagnostics.Ready(AppName);
            if (startupSmoke)
            {
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
                timer.Tick += (_, _) => { timer.Stop(); try { window.Close(); } catch { } Shutdown(0); };
                timer.Start();
            }
        }
        catch (Exception ex)
        {
            StandaloneAppDiagnostics.Failure(AppName, ex);
            if (!startupSmoke)
            {
                try { BroadcastPlayout.Views.MessageBox.Show($"{AppName} could not start.\n\n{ex.GetBaseException().Message}\n\nLog: {StandaloneAppDiagnostics.LogPath(AppName)}", AppName, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error); } catch { }
            }
            Shutdown(-1);
        }
    }
}
