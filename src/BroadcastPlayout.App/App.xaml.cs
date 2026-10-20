using System.IO;
using System.Windows;
using BroadcastPlayout.Ffmpeg;
using BroadcastPlayout.Models;
using BroadcastPlayout.Outputs;
using BroadcastPlayout.Services;

namespace BroadcastPlayout;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Any(x => string.Equals(x, "--ndi-selftest", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var exitCode = RunNdiSelfTest();
            Environment.ExitCode = exitCode;
            Shutdown(exitCode);
            return;
        }

        base.OnStartup(e);
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(ApplyCleanWindowBorder));
        RecoveryService.ApplyPendingStartupReset();
        StandaloneAppDiagnostics.Attach(this, "Kashtrix.Playout");
        var startupSmoke = e.Args.Any(x => string.Equals(x, "--startup-smoke", StringComparison.OrdinalIgnoreCase));
        if (e.Args.Any(x => string.Equals(x, "--chromium-selftest", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var exitCode = RunChromiumSelfTest();
            Environment.ExitCode = exitCode;
            Shutdown(exitCode);
            return;
        }
        if (e.Args.Any(x => string.Equals(x, "--seed-cg-demos", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var list = CgDemoFactory.CreateDefaults(forceRefresh: true);
            var dir = CgDemoFactory.GetCgDemoDirectory();
            Console.WriteLine($"Seeded {list.Count} CG demo templates into {dir}");
            Environment.ExitCode = 0;
            Shutdown(0);
            return;
        }
        try
        {
            var root = Path.Combine(AppContext.BaseDirectory, "native", "ffmpeg");
            FfmpegRuntime.Initialize(root);
        }
        catch (Exception ex)
        {
            if (startupSmoke)
            {
                StandaloneAppDiagnostics.Failure("Kashtrix.Playout", ex);
                Environment.ExitCode = -1;
                Shutdown(-1);
                return;
            }
            BroadcastPlayout.Views.MessageBox.Show(
                "The Kashtrix media runtime is not ready. The UI will open, but playback cannot start until media components are installed.\n\n" + ex.Message +
                "\n\nRun tools\\Setup-MediaRuntime.ps1 from the solution folder.",
                "Kashtrix Playout - Media setup", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }

        // Build verification uses a bounded startup smoke path.  Do not start Chromium,
        // controller sockets, hardware outputs, media scans or the full operator window from
        // SETUP-AND-BUILD: those can legitimately take longer than a smoke timeout on first
        // launch and previously made a successful compile look like a failed build.
        if (startupSmoke)
        {
            try
            {
                _ = SettingsStore.Load();
                StandaloneAppDiagnostics.Ready("Kashtrix.Playout");
                Environment.ExitCode = 0;
                Shutdown(0);
            }
            catch (Exception ex)
            {
                StandaloneAppDiagnostics.Failure("Kashtrix.Playout", ex);
                Environment.ExitCode = -1;
                Shutdown(-1);
            }
            return;
        }

        // Chromium/CEF is initialized once on normal interactive startup so HTML CG sources
        // can be preloaded and rendered off-screen with transparent BGRA output.
        ChromiumCgRuntime.Initialize();

        var settings = SettingsStore.Load();

        // The login/setup windows are modal bootstrap windows.  WPF defaults to
        // OnLastWindowClose, which can shut the dispatcher down in the small gap after the
        // successful LoginWindow closes and before MainWindow is shown.  Keep the app alive
        // explicitly during bootstrap, then hand lifetime ownership to the real Playout window.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var security = new SecurityStore();
        if (security.TryRestoreRememberedSession(out var remembered) && remembered is not null)
        {
            AppSession.Current = remembered;
        }
        else
        {
            var login = new Views.LoginWindow();
            if (login.ShowDialog() != true) { Shutdown(); return; }
        }

        settings = SettingsStore.Load();
        if (!settings.FirstRunCompleted && !startupSmoke)
        {
            var setup = new Views.FirstRunSetupWindow();
            if (setup.ShowDialog() != true) { Shutdown(); return; }
        }

        var mainWindow = new Views.MainWindow();
        MainWindow = mainWindow;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        mainWindow.Show();
        mainWindow.Activate();
        StandaloneAppDiagnostics.Ready("Kashtrix.Playout");
    }


    private static void ApplyCleanWindowBorder(object sender, RoutedEventArgs e)
    {
        if (sender is Window window) Views.WindowChromeActions.ApplyCleanBorder(window);
    }

    private static int RunNdiSelfTest()
    {
        var logPath = Path.Combine(Path.GetTempPath(), "KashtrixNdiSelfTest.log");
        try
        {
            using var ndi = new NdiSender();
            using var writer = new StreamWriter(logPath, append: false);
            writer.AutoFlush = true;
            writer.WriteLine($"Kashtrix NDI self-test {DateTime.Now:O}");
            writer.WriteLine($"Process: {(Environment.Is64BitProcess ? "x64" : "not-x64")}");

            if (!ndi.Open("KASHTRIX-NDI-SELFTEST"))
            {
                writer.WriteLine("OPEN FAILED: " + ndi.LastError);
                return 2;
            }

            writer.WriteLine("Runtime: " + ndi.RuntimeVersion);
            writer.WriteLine("Path: " + ndi.RuntimePath);

            const int width = 320;
            const int height = 180;
            const int fps = 25;
            var pixels = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var i = (y * width + x) * 4;
                    pixels[i] = (byte)(32 + (x * 160 / width));       // B
                    pixels[i + 1] = (byte)(40 + (y * 150 / height)); // G
                    pixels[i + 2] = 32;                              // R
                    pixels[i + 3] = 255;                             // A
                }
            }

            var profile = new OutputProfile { Preset = "Source" };
            var silence = new byte[480 * 2 * 2]; // 10 ms, stereo, 16-bit, 48 kHz

            for (var frameNo = 0; frameNo < fps * 2; frameNo++)
            {
                var pts = frameNo / (double)fps;
                ndi.SendVideo(new VideoFrameData(pixels, width, height, width * 4, pts, fps, 1), profile);
                for (var a = 0; a < 4; a++)
                    ndi.SendAudio(new AudioChunk(silence, pts + a * 0.010));

                if (!string.IsNullOrWhiteSpace(ndi.LastError))
                {
                    writer.WriteLine("SEND FAILED: " + ndi.LastError);
                    return 3;
                }

                Thread.Sleep(40);
            }

            ndi.Close();
            writer.WriteLine("PASS: video + interleaved 16-bit audio + destroy completed.");
            return 0;
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(logPath, "SELF-TEST EXCEPTION:\r\n" + ex); } catch { }
            return 4;
        }
    }

    private static int RunChromiumSelfTest()
    {
        var logPath = Path.Combine(Path.GetTempPath(), "KashtrixChromiumSelfTest.log");
        try
        {
            ChromiumCgRuntime.Initialize();
            using var writer = new StreamWriter(logPath, append: false) { AutoFlush = true };
            writer.WriteLine($"Kashtrix Chromium self-test {DateTime.Now:O}");
            writer.WriteLine(ChromiumCgRuntime.Status);
            if (!ChromiumCgRuntime.IsReady) return 2;

            var source = new CgHtmlSource
            {
                Name = "Chromium Self Test",
                Source = Path.Combine(AppContext.BaseDirectory, "demos", "gfx", "html", "breaking-news.html"),
                SourceKind = "File",
                CanvasWidth = 640,
                CanvasHeight = 360,
                Width = 640,
                Height = 360,
                BrowserFps = 25,
                Transparent = true,
                DataJson = "{\"headline\":\"CHROMIUM SELF TEST PASS\",\"subheadline\":\"Kashtrix HTML renderer\"}"
            };

            for (var i = 0; i < 80; i++)
            {
                using var frame = ChromiumCgRenderer.Shared.GetLatest(source);
                if (frame is not null)
                {
                    var png = Path.Combine(Path.GetTempPath(), "KashtrixChromiumSelfTest.png");
                    frame.Save(png, System.Drawing.Imaging.ImageFormat.Png);
                    writer.WriteLine($"PASS: {frame.Width}x{frame.Height} Chromium frame captured.");
                    writer.WriteLine("Screenshot: " + png);
                    ChromiumCgRenderer.Shared.Remove(source.Id);
                    return 0;
                }
                Thread.Sleep(100);
            }
            writer.WriteLine("FAILED: Chromium loaded but no frame was captured. Status=" + source.Status);
            return 3;
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(logPath, "CHROMIUM SELF-TEST EXCEPTION:\r\n" + ex); } catch { }
            return 4;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ChromiumCgRuntime.Shutdown();
        base.OnExit(e);
    }

}
