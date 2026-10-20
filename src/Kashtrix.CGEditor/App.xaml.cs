using System;
using System.Windows;
using BroadcastPlayout.Services;

namespace Kashtrix.CGEditor;

public partial class App : System.Windows.Application
{
    private BroadcastPlayout.ViewModels.MainViewModel? _vm;
    private const string AppName = "Kashtrix.CGEditor";

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        StandaloneAppDiagnostics.Attach(this, AppName);
        var startupSmoke = Array.Exists(e.Args, x => string.Equals(x, "--startup-smoke", StringComparison.OrdinalIgnoreCase));
        try
        {
            try { BroadcastPlayout.Ffmpeg.FfmpegRuntime.Initialize(System.IO.Path.Combine(AppContext.BaseDirectory, "native", "ffmpeg")); } catch { }
            try { BroadcastPlayout.Services.ChromiumCgRuntime.Initialize(); } catch { }
            _vm = new BroadcastPlayout.ViewModels.MainViewModel(enableAutomation: false);
            _vm.EnsureCgDemos();
            var window = new BroadcastPlayout.Views.CgEditorWindow(_vm);
            MainWindow = window;
            window.Show();
            StandaloneAppDiagnostics.Ready(AppName);
            if (startupSmoke)
            {
                // Verify Nepali Calendar engine
                var bs = NepaliCalendarService.ConvertToBs(DateTime.Now);
                if (bs.Year < 2070 || bs.Year > 2110) throw new InvalidOperationException($"Invalid BS Year: {bs.Year}");
                var nepFormatted = NepaliCalendarService.FormatNepaliDate(DateTime.Now, "dddd, DD MMMM YYYY");
                if (string.IsNullOrWhiteSpace(nepFormatted)) throw new InvalidOperationException("Failed to format Nepali date");

                // Verify Kashtrix proprietary binary file container (.kcg)
                var tempKcg = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"smoke_{Guid.NewGuid():N}.kcg");
                try
                {
                    var testProj = new BroadcastPlayout.Models.CgProject { Name = "Smoke Test Proj", DurationSeconds = 10 };
                    testProj.Layers.Add(new BroadcastPlayout.Models.CgLayer { Name = "PIP Layer", SqueezeProgram = true, SqueezeWidth = 1200 });
                    KashtrixCgFileService.SaveComposition(tempKcg, testProj);
                    var kcgBytes = System.IO.File.ReadAllBytes(tempKcg);
                    if (kcgBytes.Length < 8 || kcgBytes[0] != (byte)'K' || kcgBytes[1] != (byte)'C' || kcgBytes[2] != (byte)'G')
                        throw new InvalidOperationException("Missing KCG magic header");
                    var loadedProj = KashtrixCgFileService.LoadComposition(tempKcg);
                    if (loadedProj.Layers.Count != 1 || loadedProj.Layers[0].SqueezeWidth != 1200)
                        throw new InvalidOperationException("Failed to deserialize KCG binary layer");
                }
                finally
                {
                    try { if (System.IO.File.Exists(tempKcg)) System.IO.File.Delete(tempKcg); } catch { }
                }

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
        try { BroadcastPlayout.Services.ChromiumCgRuntime.Shutdown(); } catch { }
        base.OnExit(e);
    }
}
