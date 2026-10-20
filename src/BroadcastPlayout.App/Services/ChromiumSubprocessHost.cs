using System.Runtime.CompilerServices;

namespace BroadcastPlayout.Services;

/// <summary>
/// Chromium (CEF) starts helper processes for rendering, GPU and utility work. CefSharp's own helper,
/// CefSharp.BrowserSubprocess.exe, is framework-dependent and cannot start from a self-contained
/// install (the bundled host treats it as a "dotnet" command and fails). In that layout the
/// installed suite uses Kashtrix.Playout.exe itself as the helper ("SelfHost"), as CefSharp documents
/// for self-contained apps. Development builds keep the stock helper.
/// </summary>
public static class ChromiumSubprocessHost
{
    private const string PlayoutExe = "Kashtrix.Playout.exe";

    /// <summary>
    /// Runs before any other code of this assembly. When this process was started by Chromium as a
    /// helper (arguments contain --type=...), run the helper and exit; no WPF window is ever created.
    /// </summary>
    [ModuleInitializer]
    internal static void RunAsChromiumHelperIfRequested()
    {
        var args = Environment.GetCommandLineArgs();
        if (!args.Skip(1).Any(a => a.StartsWith("--type=", StringComparison.Ordinal))) return;
        if (!string.Equals(Path.GetFileName(Environment.ProcessPath), PlayoutExe, StringComparison.OrdinalIgnoreCase)) return;
        Environment.Exit(RunHelper(args.Skip(1).ToArray()));
    }

    // Kept out of the initializer: the JIT loads CefSharp only when this method compiles, and the
    // satellite apps ship without CefSharp files, so the initializer itself must never mention it.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunHelper(string[] args) => CefSharp.BrowserSubprocess.SelfHost.Main(args);

    /// <summary>The helper to give CEF, or null for CefSharp's default (development builds).</summary>
    public static string? HelperPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var selfContained = File.Exists(Path.Combine(baseDir, "hostfxr.dll"));
        var playout = Path.Combine(baseDir, PlayoutExe);
        return selfContained && File.Exists(playout) ? playout : null;
    }
}
