using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace BroadcastPlayout.Views;

/// <summary>Theme-consistent Kashtrix alert box used by view code instead of native white dialogs.</summary>
public static class MessageBox
{
    public static MessageBoxResult Show(string message, string caption) => ShowCore(null, message, caption, MessageBoxButton.OK, MessageBoxImage.None);
    public static MessageBoxResult Show(string message, string caption, MessageBoxButton buttons) => ShowCore(null, message, caption, buttons, MessageBoxImage.None);
    public static MessageBoxResult Show(string message, string caption, MessageBoxButton buttons, MessageBoxImage image) => ShowCore(null, message, caption, buttons, image);
    public static MessageBoxResult Show(Window owner, string message, string caption, MessageBoxButton buttons, MessageBoxImage image) => ShowCore(owner, message, caption, buttons, image);

    private static MessageBoxResult ShowCore(Window? owner, string message, string caption, MessageBoxButton buttons, MessageBoxImage image)
    {
        var result = MessageBoxResult.None;
        var window = new Window
        {
            Title = caption,
            Width = 520,
            MinWidth = 400,
            SizeToContent = SizeToContent.Height,
            MaxHeight = 600,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = Brush("#0B0F14"),
            Foreground = Brush("#F0F3F6"),
            Owner = owner
        };

        var shell = new Border
        {
            Background = Brush("#0B0F14"),
            BorderBrush = Brush("#353F49"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5)
        };
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(56) });

        var header = new Grid { Background = Brush("#0E141B") };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        header.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(caption) ? "Kashtrix" : caption,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0)
        });
        var close = Button("\uE8BB", 40, "Segoe MDL2 Assets");
        close.Background = Brushes.Transparent;
        close.BorderBrush = Brushes.Transparent;
        close.Click += (_, _) => { result = MessageBoxResult.Cancel; window.DialogResult = false; };
        Grid.SetColumn(close, 1); header.Children.Add(close);
        header.MouseLeftButtonDown += (_, e) => WindowChromeActions.Drag(window, e);
        Grid.SetRow(header, 0); grid.Children.Add(header);

        var body = new Grid { Margin = new Thickness(18, 18, 18, 16) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(image == MessageBoxImage.None ? 0 : 42) });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        if (image != MessageBoxImage.None)
        {
            var symbol = image switch { MessageBoxImage.Error => "!", MessageBoxImage.Warning => "!", MessageBoxImage.Question => "?", _ => "i" };
            var color = image switch { MessageBoxImage.Error => "#E45C67", MessageBoxImage.Warning => "#E0B15A", _ => "#A86BE0" };
            body.Children.Add(new Border
            {
                Width = 28, Height = 28, CornerRadius = new CornerRadius(14), VerticalAlignment = VerticalAlignment.Top,
                BorderBrush = Brush(color), BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = symbol, Foreground = Brush(color), FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            });
        }
        var text = new TextBlock { Text = message ?? string.Empty, TextWrapping = TextWrapping.Wrap, LineHeight = 19, FontSize = 12.5, MaxWidth = 430, Foreground = Brush("#E4E8EB") };
        Grid.SetColumn(text, 1); body.Children.Add(text);
        Grid.SetRow(body, 1); grid.Children.Add(body);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
        void Add(string label, MessageBoxResult value, bool isDefault = false)
        {
            var button = Button(label, 92);
            button.IsDefault = isDefault;
            button.Click += (_, _) => { result = value; window.DialogResult = value is MessageBoxResult.OK or MessageBoxResult.Yes; };
            actions.Children.Add(button);
        }
        switch (buttons)
        {
            case MessageBoxButton.OKCancel: Add("CANCEL", MessageBoxResult.Cancel); Add("OK", MessageBoxResult.OK, true); break;
            case MessageBoxButton.YesNo: Add("NO", MessageBoxResult.No); Add("YES", MessageBoxResult.Yes, true); break;
            case MessageBoxButton.YesNoCancel: Add("CANCEL", MessageBoxResult.Cancel); Add("NO", MessageBoxResult.No); Add("YES", MessageBoxResult.Yes, true); break;
            default: Add("OK", MessageBoxResult.OK, true); break;
        }
        Grid.SetRow(actions, 2); grid.Children.Add(actions);
        shell.Child = grid; window.Content = shell;
        window.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { result = MessageBoxResult.Cancel; window.DialogResult = false; e.Handled = true; } };
        window.ShowDialog();
        return result == MessageBoxResult.None ? (buttons == MessageBoxButton.OK ? MessageBoxResult.OK : MessageBoxResult.Cancel) : result;
    }

    private static Button Button(string text, double width, string? fontFamily = null) => new()
    {
        Content = new TextBlock { Text = text, FontFamily = string.IsNullOrWhiteSpace(fontFamily) ? SystemFonts.MessageFontFamily : new FontFamily(fontFamily), HorizontalAlignment = HorizontalAlignment.Center },
        Width = width,
        Height = 32,
        Margin = new Thickness(4, 0, 0, 0),
        Foreground = Brush("#EFF2F5"),
        Background = Brush("#1A2027"),
        BorderBrush = Brush("#3C4650"),
        BorderThickness = new Thickness(1)
    };

    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
}
