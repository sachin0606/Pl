using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BroadcastPlayout.Views;

public sealed class ConfidenceMonitorWindow : Window
{
    private readonly Func<ImageSource?> _sourceProvider;
    private readonly Image _image;

    public ConfidenceMonitorWindow(string title, Func<ImageSource?> sourceProvider)
    {
        _sourceProvider = sourceProvider;
        Title = title;
        Width = 980; Height = 610; MinWidth = 480; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(5,8,12));
        ShowInTaskbar = true;
        try
        {
            var res = Application.GetResourceStream(new Uri("pack://application:,,,/Kashtrix.Playout;component/Branding/kashtrix-playout.ico", UriKind.Absolute));
            if (res?.Stream is not null) Icon = BitmapFrame.Create(res.Stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        }
        catch { }
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var head = new Border { Background = new SolidColorBrush(Color.FromRgb(12,18,25)), BorderBrush = new SolidColorBrush(Color.FromRgb(41,50,60)), BorderThickness = new Thickness(0,0,0,1), Padding = new Thickness(10,0,10,0) };
        head.Child = new TextBlock { Text = title.ToUpperInvariant(), Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        root.Children.Add(head);
        var host = new Viewbox { Stretch = Stretch.Uniform }; Grid.SetRow(host, 1);
        var frame = new Grid { Width = 1920, Height = 1080, Background = Brushes.Black };
        _image = new Image { Stretch = Stretch.Fill, SnapsToDevicePixels = true };
        frame.Children.Add(_image);
        host.Child = frame;
        root.Children.Add(host);
        Content = root;

        Loaded += (_, _) =>
        {
            try { _image.Source = _sourceProvider(); } catch { }
            CompositionTarget.Rendering += OnRendering;
        };
        Closed += (_, _) =>
        {
            CompositionTarget.Rendering -= OnRendering;
        };
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        try
        {
            var current = _sourceProvider();
            if (!ReferenceEquals(_image.Source, current))
            {
                _image.Source = current;
            }
        }
        catch { }
    }
}
