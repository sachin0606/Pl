using System.Windows;
using System.Windows.Input;
using BroadcastPlayout.Services;

namespace BroadcastPlayout.Views;

public partial class MultiviewTileFullscreenWindow : Window
{
    public MultiviewTileFullscreenWindow(MultiviewTile tile)
    {
        InitializeComponent();
        WindowChromeActions.ApplyCleanBorder(this);
        DataContext = tile;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void ToggleMaximize_Click(object sender, RoutedEventArgs e)
    {
        WindowChromeActions.ToggleMaximize(this);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }
}
