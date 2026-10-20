using System.Windows;
using System.Windows.Controls;
using Forms = System.Windows.Forms;

namespace Kashtrix.Prompter;

public sealed class PrompterDisplayWindow : Window
{
    private readonly ContentControl _host = new();
    private int _screenIndex;

    public PrompterDisplayWindow()
    {
        Title = "Kashtrix Prompter Output";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = System.Windows.Media.Brushes.Black;
        Content = _host;
    }

    public void ShowOnScreen(int index)
    {
        var screens = Forms.Screen.AllScreens;
        if (screens.Length == 0) return;
        _screenIndex = Math.Clamp(index, 0, screens.Length - 1);
        var b = screens[_screenIndex].Bounds;
        WindowState = WindowState.Normal;
        Left = b.Left; Top = b.Top; Width = b.Width; Height = b.Height;
        if (!IsVisible) Show();
        Activate();
    }

    public void UpdateFrame(PrompterVisualState state)
    {
        var w = Math.Max(320, (int)Math.Round(ActualWidth > 50 ? ActualWidth : Width));
        var h = Math.Max(180, (int)Math.Round(ActualHeight > 50 ? ActualHeight : Height));
        _host.Content = PrompterRenderer.BuildVisual(state, w, h);
    }

    public string ScreenName
    {
        get
        {
            var screens = Forms.Screen.AllScreens;
            return screens.Length == 0 ? "No display" : screens[Math.Clamp(_screenIndex, 0, screens.Length - 1)].DeviceName;
        }
    }
}
