using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Runtime.InteropServices;

namespace BroadcastPlayout.Views;

public static class WindowChromeActions
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;
    private const uint DwmColorNone = 0xFFFFFFFE;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint value, int valueSize);

    public static void ApplyCleanBorder(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;
        void Apply()
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;
                var dark = 1u;
                _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(uint));
                var caption = 0x00140F0Bu; // #0B0F14 (BGR)
                _ = DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref caption, sizeof(uint));
                var border = 0x00332820u; // #202833 (BGR)
                _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref border, sizeof(uint));
                var text = 0x00E0E0E0u;
                _ = DwmSetWindowAttribute(hwnd, DwmwaTextColor, ref text, sizeof(uint));
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Apply();
        else window.SourceInitialized += (_, _) => Apply();
    }

    public static void Minimize(Window window) => window.WindowState = WindowState.Minimized;

    public static void ToggleMaximize(Window window) =>
        window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    public static void Drag(Window window, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2) { ToggleMaximize(window); e.Handled = true; return; }
        if (e.LeftButton != MouseButtonState.Pressed) return;
        try
        {
            if (window.WindowState == WindowState.Maximized)
            {
                var local = e.GetPosition(window);
                var screen = window.PointToScreen(local);
                var ratio = window.ActualWidth <= 1 ? .5 : Math.Clamp(local.X / window.ActualWidth, .05, .95);
                var restoreWidth = window.RestoreBounds.Width > 0 ? window.RestoreBounds.Width : Math.Max(window.MinWidth, 1100);
                window.WindowState = WindowState.Normal;
                window.Left = screen.X - restoreWidth * ratio;
                window.Top = Math.Max(0, screen.Y - 18);
            }
            window.DragMove();
        }
        catch (InvalidOperationException) { }
    }
}
