using System;
using BroadcastPlayout.Services;

namespace Kashtrix.MAM;

public partial class App : System.Windows.Application
{
    private const string AppName = "Kashtrix.MAM";
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        StandaloneAppDiagnostics.Attach(this, AppName);
        var startupSmoke = Array.Exists(e.Args, x => string.Equals(x, "--startup-smoke", StringComparison.OrdinalIgnoreCase));
        try
        {
            try { BroadcastPlayout.Ffmpeg.FfmpegRuntime.Initialize(System.IO.Path.Combine(AppContext.BaseDirectory, "native", "ffmpeg")); } catch { }
            var window = new BroadcastPlayout.Views.MediaAssetManagementWindow();
            MainWindow = window; window.Show(); StandaloneAppDiagnostics.Ready(AppName);
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
            if (!startupSmoke) try { BroadcastPlayout.Views.MessageBox.Show($"{AppName} could not start.\n\n{ex.GetBaseException().Message}\n\nLog: {StandaloneAppDiagnostics.LogPath(AppName)}", AppName); } catch { }
            Shutdown(-1);
        }
    }
}
