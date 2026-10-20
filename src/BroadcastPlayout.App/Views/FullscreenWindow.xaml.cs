using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using BroadcastPlayout.Ffmpeg;
using BroadcastPlayout.Models;
using Forms = System.Windows.Forms;

namespace BroadcastPlayout.Views;

public partial class FullscreenWindow : Window
{
    private readonly BgraFrameScaler _scaler = new();
    private WriteableBitmap? _bitmap;

    public FullscreenWindow()
    {
        InitializeComponent();
        Closed += (_, _) => _scaler.Dispose();
        StateChanged += (_, _) => UpdateWindowGlyph();
        Loaded += (_, _) => UpdateWindowGlyph();
    }

    public void ShowWindowed(Window? owner = null)
    {
        Topmost = false;
        ShowInTaskbar = true;
        ResizeMode = ResizeMode.CanResize;
        WindowStyle = WindowStyle.None;
        Title = "Kashtrix Program Monitor";
        Width = 960;
        Height = 560;
        MinWidth = 480;
        MinHeight = 270;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (TitleBarHost is not null) TitleBarHost.Visibility = Visibility.Visible;
        if (TitleBarRow is not null) TitleBarRow.Height = new GridLength(34);
        if (owner is not null) Owner = owner;
        Show();
        UpdateWindowGlyph();
    }

    private void UpdateWindowGlyph()
    {
        if (MaximizeGlyph is null) return;
        MaximizeGlyph.Text = WindowState == WindowState.Maximized ? "" : "";
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        try { DragMove(); } catch { }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    public void ShowOnMonitor(int index)
    {
        var screens = Forms.Screen.AllScreens;
        if (screens.Length == 0) return;
        var bounds = screens[Math.Clamp(index, 0, screens.Length - 1)].Bounds;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        if (TitleBarHost is not null) TitleBarHost.Visibility = Visibility.Collapsed;
        if (TitleBarRow is not null) TitleBarRow.Height = new GridLength(0);
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
        Show();
        WindowState = WindowState.Normal;
    }

    public void SetScaling(string? scaling)
    {
        OutputImage.Stretch = scaling switch
        {
            "Fill" => System.Windows.Media.Stretch.UniformToFill,
            "Stretch" => System.Windows.Media.Stretch.Fill,
            _ => System.Windows.Media.Stretch.Uniform
        };
    }

    public void Present(VideoFrameData frame, OutputProfile profile)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Present(frame, profile));
            return;
        }

        var (width, height) = profile.Resolve(frame.Width, frame.Height);
        var output = _scaler.ScaleReusable(frame, width, height);

        if (_bitmap is null || _bitmap.PixelWidth != output.Width || _bitmap.PixelHeight != output.Height)
        {
            _bitmap = new WriteableBitmap(output.Width, output.Height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
            OutputImage.Source = _bitmap;
        }
        _bitmap.WritePixels(new Int32Rect(0, 0, output.Width, output.Height), output.Bgra, output.Stride, 0);
    }
}
