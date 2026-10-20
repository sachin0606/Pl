using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BroadcastPlayout.Views;

internal sealed class NewCgProjectDialog : Window
{
    private readonly TextBox _name = new() { Text = "Untitled Graphic" };
    private readonly ComboBox _resolution = new();
    private readonly TextBox _width = new() { Text = "1920" };
    private readonly TextBox _height = new() { Text = "1080" };
    private readonly ComboBox _fps = new();
    private readonly TextBox _duration = new() { Text = "10" };

    public string ProjectName { get; private set; } = "Untitled Graphic";
    public int ProjectWidth { get; private set; } = 1920;
    public int ProjectHeight { get; private set; } = 1080;
    public double ProjectFrameRate { get; private set; } = 25.0;
    public double ProjectDurationSeconds { get; private set; } = 10.0;

    public NewCgProjectDialog(string suggestedName)
    {
        Title = "New CG Design";
        Width = 520;
        Height = 500;
        MinWidth = 480;
        MinHeight = 450;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(9, 13, 17));
        Foreground = Brushes.White;
        ShowInTaskbar = false;
        _name.Text = suggestedName;

        _resolution.ItemsSource = new[]
        {
            "1920 x 1080 · HD 16:9",
            "1280 x 720 · HD 16:9",
            "3840 x 2160 · UHD 16:9",
            "1080 x 1920 · VERTICAL 9:16",
            "1080 x 1080 · SQUARE 1:1",
            "CUSTOM"
        };
        _resolution.SelectedIndex = 0;
        _resolution.SelectionChanged += (_, _) => ApplyResolutionPreset();

        _fps.ItemsSource = new[] { "23.976", "24", "25", "29.97", "30", "50", "59.94", "60" };
        _fps.SelectedItem = "25";

        var root = new Grid { Margin = new Thickness(18) };
        for (var i = 0; i < 9; i++) root.RowDefinitions.Add(new RowDefinition { Height = i == 8 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        root.ColumnDefinitions.Add(new ColumnDefinition());

        AddRow(root, 0, "DESIGN NAME", _name);
        AddRow(root, 1, "RESOLUTION", _resolution);

        var sizeGrid = new Grid();
        sizeGrid.ColumnDefinitions.Add(new ColumnDefinition());
        sizeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        sizeGrid.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(_height, 2);
        sizeGrid.Children.Add(_width); sizeGrid.Children.Add(_height);
        AddRow(root, 2, "WIDTH / HEIGHT", sizeGrid);

        AddRow(root, 3, "FRAME RATE", _fps);
        AddRow(root, 4, "DURATION SEC", _duration);

        var hint = new TextBlock
        {
            Text = "Resolution and frame rate are stored with the CG design. You can still change element timing independently on the timeline.",
            Foreground = new SolidColorBrush(Color.FromRgb(145, 156, 166)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 14, 0, 14)
        };
        Grid.SetRow(hint, 5); Grid.SetColumnSpan(hint, 2); root.Children.Add(hint);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "CANCEL", Width = 92, Height = 34, Margin = new Thickness(4) };
        cancel.Click += (_, _) => DialogResult = false;
        var create = new Button { Content = "CREATE", Width = 112, Height = 34, Margin = new Thickness(4), FontWeight = FontWeights.SemiBold };
        create.Click += (_, _) => Accept();
        buttons.Children.Add(cancel); buttons.Children.Add(create);
        Grid.SetRow(buttons, 7); Grid.SetColumnSpan(buttons, 2); root.Children.Add(buttons);

        Content = root;
        Loaded += (_, _) => _name.Focus();
    }

    private static void AddRow(Grid root, int row, string label, FrameworkElement control)
    {
        var caption = new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(Color.FromRgb(190, 156, 231)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 7, 12, 7),
            FontWeight = FontWeights.SemiBold
        };
        control.Margin = new Thickness(0, 5, 0, 5);
        control.MinHeight = 30;
        Grid.SetRow(caption, row); Grid.SetColumn(caption, 0);
        Grid.SetRow(control, row); Grid.SetColumn(control, 1);
        root.Children.Add(caption); root.Children.Add(control);
    }

    private void ApplyResolutionPreset()
    {
        var text = _resolution.SelectedItem as string ?? string.Empty;
        if (text.StartsWith("1920", StringComparison.Ordinal)) { _width.Text = "1920"; _height.Text = "1080"; }
        else if (text.StartsWith("1280", StringComparison.Ordinal)) { _width.Text = "1280"; _height.Text = "720"; }
        else if (text.StartsWith("3840", StringComparison.Ordinal)) { _width.Text = "3840"; _height.Text = "2160"; }
        else if (text.StartsWith("1080 x 1920", StringComparison.Ordinal)) { _width.Text = "1080"; _height.Text = "1920"; }
        else if (text.StartsWith("1080 x 1080", StringComparison.Ordinal)) { _width.Text = "1080"; _height.Text = "1080"; }
    }

    private void Accept()
    {
        if (string.IsNullOrWhiteSpace(_name.Text))
        {
            MessageBox.Show(this, "Enter a design name.", "New CG Design", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!int.TryParse(_width.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) || width < 16 || width > 16384 ||
            !int.TryParse(_height.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var height) || height < 16 || height > 16384)
        {
            MessageBox.Show(this, "Width and height must be between 16 and 16384 pixels.", "New CG Design", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!double.TryParse(_fps.SelectedItem?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var fps) || fps < 1 || fps > 240 || !double.IsFinite(fps))
        {
            MessageBox.Show(this, "Choose a valid frame rate.", "New CG Design", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!double.TryParse(_duration.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var duration) || duration < .5 || duration > 3600 || !double.IsFinite(duration))
        {
            MessageBox.Show(this, "Duration must be between 0.5 and 3600 seconds.", "New CG Design", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ProjectName = _name.Text.Trim();
        ProjectWidth = width;
        ProjectHeight = height;
        ProjectFrameRate = fps;
        ProjectDurationSeconds = duration;
        DialogResult = true;
    }
}
