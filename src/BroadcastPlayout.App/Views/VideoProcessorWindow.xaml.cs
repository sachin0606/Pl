using System.Windows;
using System.Windows.Input;
using BroadcastPlayout.ViewModels;

namespace BroadcastPlayout.Views;

public partial class VideoProcessorWindow : Window
{
    private readonly MainViewModel _vm;
    public VideoProcessorWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    private void Reset_Click(object sender, RoutedEventArgs e) => _vm.ResetVideoProcessing();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowChromeActions.Minimize(this);
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowChromeActions.ToggleMaximize(this);
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => WindowChromeActions.Drag(this, e);
}
