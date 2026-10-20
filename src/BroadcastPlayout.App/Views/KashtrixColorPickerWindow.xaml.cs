using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace BroadcastPlayout.Views;

public partial class KashtrixColorPickerWindow : Window
{
    private bool _initializing;
    private bool _isDraggingSpectrum;
    private double _currentHue;
    private double _currentSat = 1.0;
    private double _currentVal = 1.0;

    public string SelectedColorText { get; private set; } = "#FFFFFFFF";

    public KashtrixColorPickerWindow(string? initialColor)
    {
        InitializeComponent();
        var color = Parse(initialColor, Colors.White);
        _initializing = true;
        AlphaSlider.Value = color.A;
        RedSlider.Value = color.R;
        GreenSlider.Value = color.G;
        BlueSlider.Value = color.B;

        ColorToHsv(color, out _currentHue, out _currentSat, out _currentVal);
        HueSlider.Value = _currentHue;
        HueValueText.Text = $"{_currentHue:0}°";
        SpectrumHueRect.Fill = new SolidColorBrush(HsvToRgb(_currentHue, 1.0, 1.0, 255));

        _initializing = false;
        Loaded += (_, _) =>
        {
            UpdateThumbFromHsv();
            UpdatePreview();
        };
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => WindowChromeActions.Drag(this, e);

    private void Spectrum_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingSpectrum = true;
        SpectrumBorder.CaptureMouse();
        PickSpectrumPoint(e.GetPosition(SpectrumBorder));
    }

    private void Spectrum_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isDraggingSpectrum && e.LeftButton == MouseButtonState.Pressed)
        {
            PickSpectrumPoint(e.GetPosition(SpectrumBorder));
        }
    }

    private void Spectrum_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDraggingSpectrum)
        {
            _isDraggingSpectrum = false;
            SpectrumBorder.ReleaseMouseCapture();
        }
    }

    private void PickSpectrumPoint(Point pt)
    {
        var w = Math.Max(1.0, SpectrumBorder.ActualWidth);
        var h = Math.Max(1.0, SpectrumBorder.ActualHeight);

        _currentSat = Math.Clamp(pt.X / w, 0.0, 1.0);
        _currentVal = Math.Clamp(1.0 - (pt.Y / h), 0.0, 1.0);

        PositionThumb(Math.Clamp(pt.X, 0, w), Math.Clamp(pt.Y, 0, h));

        var rgb = HsvToRgb(_currentHue, _currentSat, _currentVal, (byte)AlphaSlider.Value);
        _initializing = true;
        RedSlider.Value = rgb.R;
        GreenSlider.Value = rgb.G;
        BlueSlider.Value = rgb.B;
        _initializing = false;

        UpdatePreview();
    }

    private void PositionThumb(double x, double y)
    {
        Canvas.SetLeft(SpectrumThumb, x);
        Canvas.SetTop(SpectrumThumb, y);
    }

    private void UpdateThumbFromHsv()
    {
        var w = Math.Max(1.0, SpectrumBorder.ActualWidth);
        var h = Math.Max(1.0, SpectrumBorder.ActualHeight);
        var x = _currentSat * w;
        var y = (1.0 - _currentVal) * h;
        PositionThumb(x, y);
    }

    private void HueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _currentHue = HueSlider.Value;
        if (HueValueText is not null) HueValueText.Text = $"{_currentHue:0}°";
        if (SpectrumHueRect is not null) SpectrumHueRect.Fill = new SolidColorBrush(HsvToRgb(_currentHue, 1.0, 1.0, 255));

        if (!_initializing && IsLoaded)
        {
            var rgb = HsvToRgb(_currentHue, _currentSat, _currentVal, (byte)AlphaSlider.Value);
            _initializing = true;
            RedSlider.Value = rgb.R;
            GreenSlider.Value = rgb.G;
            BlueSlider.Value = rgb.B;
            _initializing = false;
            UpdatePreview();
        }
    }

    private void ChannelSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing || !IsLoaded) return;

        var color = Color.FromArgb((byte)AlphaSlider.Value, (byte)RedSlider.Value, (byte)GreenSlider.Value, (byte)BlueSlider.Value);
        ColorToHsv(color, out var h, out var s, out var v);
        _currentHue = h;
        _currentSat = s;
        _currentVal = v;

        _initializing = true;
        HueSlider.Value = _currentHue;
        HueValueText.Text = $"{_currentHue:0}°";
        SpectrumHueRect.Fill = new SolidColorBrush(HsvToRgb(_currentHue, 1.0, 1.0, 255));
        _initializing = false;

        UpdateThumbFromHsv();
        UpdatePreview();
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string value }) return;
        ApplyParsedColor(Parse(value, Colors.White));
    }

    private void HexSet_Click(object sender, RoutedEventArgs e)
    {
        ApplyParsedColor(Parse(HexInput.Text, Colors.White));
    }

    private void HexInput_LostFocus(object sender, RoutedEventArgs e)
    {
        ApplyParsedColor(Parse(HexInput.Text, Colors.White));
    }

    private void HexInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyParsedColor(Parse(HexInput.Text, Colors.White));
            e.Handled = true;
        }
    }

    private void ApplyParsedColor(Color color)
    {
        _initializing = true;
        AlphaSlider.Value = color.A;
        RedSlider.Value = color.R;
        GreenSlider.Value = color.G;
        BlueSlider.Value = color.B;

        ColorToHsv(color, out _currentHue, out _currentSat, out _currentVal);
        HueSlider.Value = _currentHue;
        HueValueText.Text = $"{_currentHue:0}°";
        SpectrumHueRect.Fill = new SolidColorBrush(HsvToRgb(_currentHue, 1.0, 1.0, 255));
        _initializing = false;

        UpdateThumbFromHsv();
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        var c = Color.FromArgb((byte)AlphaSlider.Value, (byte)RedSlider.Value, (byte)GreenSlider.Value, (byte)BlueSlider.Value);
        PreviewSwatch.Background = new SolidColorBrush(c);
        AlphaValue.Text = $"A {c.A}";
        RedValue.Text = $"R {c.R}";
        GreenValue.Text = $"G {c.G}";
        BlueValue.Text = $"B {c.B}";

        var hex = $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
        HexSummary.Text = hex;
        SelectedColorSummary.Text = hex;
        SelectedColorText = hex;

        if (!HexInput.IsFocused)
        {
            HexInput.Text = hex;
        }
    }

    private static Color Parse(string? text, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        var trimmed = text.Trim();
        if (!trimmed.StartsWith('#')) trimmed = "#" + trimmed;
        try { return (Color)ColorConverter.ConvertFromString(trimmed)!; }
        catch { return fallback; }
    }

    private static void ColorToHsv(Color color, out double hue, out double saturation, out double value)
    {
        double r = color.R / 255.0;
        double g = color.G / 255.0;
        double b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        value = max;
        saturation = max <= 0.0001 ? 0 : delta / max;

        if (delta <= 0.0001) hue = 0;
        else if (Math.Abs(max - r) < 0.0001) hue = 60 * (((g - b) / delta) % 6);
        else if (Math.Abs(max - g) < 0.0001) hue = 60 * (((b - r) / delta) + 2);
        else hue = 60 * (((r - g) / delta) + 4);

        if (hue < 0) hue += 360;
        hue = Math.Clamp(hue, 0, 360);
    }

    private static Color HsvToRgb(double hue, double saturation, double value, byte alpha = 255)
    {
        int hi = (int)Math.Floor(hue / 60) % 6;
        double f = (hue / 60) - Math.Floor(hue / 60);
        value = Math.Clamp(value, 0, 1);
        saturation = Math.Clamp(saturation, 0, 1);
        byte v = (byte)(value * 255);
        byte p = (byte)(value * (1 - saturation) * 255);
        byte q = (byte)(value * (1 - f * saturation) * 255);
        byte t = (byte)(value * (1 - (1 - f) * saturation) * 255);

        return hi switch
        {
            0 => Color.FromArgb(alpha, v, t, p),
            1 => Color.FromArgb(alpha, q, v, p),
            2 => Color.FromArgb(alpha, p, v, t),
            3 => Color.FromArgb(alpha, p, q, v),
            4 => Color.FromArgb(alpha, t, p, v),
            _ => Color.FromArgb(alpha, v, p, q)
        };
    }

    private void Apply_Click(object sender, RoutedEventArgs e) { UpdatePreview(); DialogResult = true; }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
