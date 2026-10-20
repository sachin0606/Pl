using System;
using System.Windows;
using BroadcastPlayout.Services;

namespace Kashtrix.Multiview;

public partial class App : System.Windows.Application
{
    private BroadcastPlayout.ViewModels.MainViewModel? _vm;
    private const string AppName = "Kashtrix.Multiview";

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        StandaloneAppDiagnostics.Attach(this, AppName);
        var startupSmoke = Array.Exists(e.Args, x => string.Equals(x, "--startup-smoke", StringComparison.OrdinalIgnoreCase));
        try
        {
            try { BroadcastPlayout.Ffmpeg.FfmpegRuntime.Initialize(System.IO.Path.Combine(AppContext.BaseDirectory, "native", "ffmpeg")); } catch { }
            
            _vm = new BroadcastPlayout.ViewModels.MainViewModel(enableAutomation: false);
            
            var window = new BroadcastPlayout.Views.MultiviewWindow(_vm);
            MainWindow = window;
            window.Show();
            StandaloneAppDiagnostics.Ready(AppName);
            if (startupSmoke)
            {
                var smokeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
                smokeTimer.Tick += (_, _) =>
                {
                    smokeTimer.Stop();
                    try { window.Close(); } catch { }
                    Shutdown(0);
                };
                smokeTimer.Start();
            }
        }
        catch (Exception ex)
        {
            StandaloneAppDiagnostics.Failure(AppName, ex);
            if (!startupSmoke)
            {
                try { BroadcastPlayout.Views.MessageBox.Show($"{AppName} could not start.\n\n{ex.Message}\n\nLog: {StandaloneAppDiagnostics.LogPath(AppName)}", AppName, MessageBoxButton.OK, MessageBoxImage.Error); } catch { }
            }
            Shutdown(-1);
        }
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        try { _vm?.Dispose(); } catch { }
        _vm = null;
        
        base.OnExit(e);
    }
}
