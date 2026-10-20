using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Kashtrix.Prompter;

public sealed record PrompterVisualState(
    string Body,
    string Slug,
    string Presenter,
    double ScrollOffset,
    double FontSize,
    double LineSpacing,
    bool MirrorHorizontal,
    bool FlipVertical,
    bool Invert,
    bool RightToLeft,
    bool Blank,
    bool CardMode,
    int CardIndex,
    string Message,
    bool ShowClock,
    bool ShowTimer,
    TimeSpan TimerElapsed,
    int SafeMargin,
    bool ShowStoryHeader);

public static class PrompterRenderer
{
    public static string[] SplitCards(string? body)
    {
        var text = (body ?? string.Empty).Replace("\r\n", "\n").Trim();
        if (text.Length == 0) return [""];
        return text.Split(["\n---\n", "\n\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public static FrameworkElement BuildVisual(PrompterVisualState s, int width, int height)
    {
        width = Math.Max(320, width); height = Math.Max(180, height);
        var background = s.Invert ? Brushes.White : Brushes.Black;
        var foreground = s.Invert ? Brushes.Black : Brushes.White;
        var accent = s.Invert ? Brushes.DarkRed : Brushes.Gold;
        var root = new Grid { Width = width, Height = height, Background = background, ClipToBounds = true };
        root.RenderTransformOrigin = new Point(0.5, 0.5);
        root.RenderTransform = new ScaleTransform(s.MirrorHorizontal ? -1 : 1, s.FlipVertical ? -1 : 1);

        if (!s.Blank)
        {
            var margin = Math.Clamp(s.SafeMargin, 20, Math.Max(20, Math.Min(width, height) / 3));
            var text = s.Body ?? string.Empty;
            if (s.CardMode)
            {
                var cards = SplitCards(text);
                var index = Math.Clamp(s.CardIndex, 0, Math.Max(0, cards.Length - 1));
                text = cards[index];
            }

            var canvas = new Canvas { ClipToBounds = true };
            root.Children.Add(canvas);
            var body = new TextBlock
            {
                Text = text,
                Foreground = foreground,
                FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.SemiBold,
                FontSize = Math.Clamp(s.FontSize, 18, 180),
                LineHeight = Math.Clamp(s.FontSize * s.LineSpacing, s.FontSize, s.FontSize * 2.5),
                TextWrapping = TextWrapping.Wrap,
                Width = Math.Max(100, width - (margin * 2)),
                TextAlignment = s.RightToLeft ? TextAlignment.Right : TextAlignment.Left,
                FlowDirection = s.RightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight
            };
            Canvas.SetLeft(body, margin);
            Canvas.SetTop(body, s.CardMode ? Math.Max(margin, (height * 0.18)) : margin - Math.Max(0, s.ScrollOffset));
            canvas.Children.Add(body);

            if (s.ShowStoryHeader)
            {
                var header = new Border { Background = new SolidColorBrush(Color.FromArgb(185, 0, 0, 0)), Padding = new Thickness(14, 8, 14, 8), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Top };
                header.Child = new TextBlock { Text = $"{s.Slug}   ·   {s.Presenter}".Trim(' ', '·'), Foreground = Brushes.White, FontSize = Math.Max(16, s.FontSize * 0.34), FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis };
                root.Children.Add(header);
            }
        }

        if (s.ShowClock)
        {
            root.Children.Add(new TextBlock { Text = DateTime.Now.ToString("HH:mm:ss"), Foreground = accent, FontSize = Math.Max(22, s.FontSize * 0.42), FontWeight = FontWeights.Bold, Margin = new Thickness(20), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top });
        }
        if (s.ShowTimer)
        {
            root.Children.Add(new TextBlock { Text = s.TimerElapsed.ToString(@"hh\:mm\:ss"), Foreground = accent, FontSize = Math.Max(22, s.FontSize * 0.42), FontWeight = FontWeights.Bold, Margin = new Thickness(20), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top });
        }
        if (!string.IsNullOrWhiteSpace(s.Message))
        {
            var msg = new Border { Background = new SolidColorBrush(Color.FromArgb(220, 140, 0, 0)), Padding = new Thickness(22, 12, 22, 12), Margin = new Thickness(30), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom };
            msg.Child = new TextBlock { Text = s.Message.Trim(), Foreground = Brushes.White, FontSize = Math.Max(20, s.FontSize * 0.42), FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = Math.Max(300, width - 120) };
            root.Children.Add(msg);
        }
        return root;
    }

    public static byte[] RenderBgra(PrompterVisualState state, int width, int height)
    {
        var visual = BuildVisual(state, width, height);
        visual.Measure(new Size(width, height));
        visual.Arrange(new Rect(0, 0, width, height));
        visual.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var stride = width * 4;
        var data = new byte[stride * height];
        bitmap.CopyPixels(data, stride, 0);
        return data;
    }
}
