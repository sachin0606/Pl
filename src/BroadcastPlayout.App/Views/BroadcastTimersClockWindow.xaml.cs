using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using BroadcastPlayout.ViewModels;

namespace BroadcastPlayout.Views;

public partial class BroadcastTimersClockWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _clockTimer;

    public BroadcastTimersClockWindow(MainViewModel vm)
    {
        InitializeComponent();
        WindowChromeActions.ApplyCleanBorder(this);
        _vm = vm;
        DataContext = _vm;

        _clockTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _clockTimer.Tick += ClockTimer_Tick;
        _clockTimer.Start();
        ClockTimer_Tick(null, EventArgs.Empty);

        Closed += (_, _) => _clockTimer.Stop();
    }

    private void ClockTimer_Tick(object? sender, EventArgs e)
    {
        var now = DateTime.Now;
        StationClockText.Text = now.ToString("HH:mm:ss");
        StationDateText.Text = now.ToString("ddd, dd MMM yyyy").ToUpperInvariant();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void Topmost_Click(object sender, RoutedEventArgs e)
    {
        Topmost = TopmostCheck.IsChecked == true;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
