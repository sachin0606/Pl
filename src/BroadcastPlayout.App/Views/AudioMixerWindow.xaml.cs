using System.Windows;
using System.Windows.Input;
using BroadcastPlayout.ViewModels;

namespace BroadcastPlayout.Views;

public partial class AudioMixerWindow : Window
{
    private readonly MainViewModel _vm;
    public AudioMixerWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }
    private void Audition_Click(object sender, RoutedEventArgs e) => _vm.AuditionPreviewAudio();
    private void StopPreview_Click(object sender, RoutedEventArgs e) => _vm.StopPreviewPlayback();
    private void ActiveItemProperties_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.CurrentItem is null) { MessageBox.Show(this, "No item is currently on Program.", "Kashtrix Audio Mixer", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        new PlaylistItemPropertiesWindow(_vm.CurrentItem) { Owner = this }.ShowDialog();
    }
    private void SelectedItemProperties_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedItem is null) { MessageBox.Show(this, "Select a rundown item first.", "Kashtrix Audio Mixer", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        new PlaylistItemPropertiesWindow(_vm.SelectedItem) { Owner = this }.ShowDialog();
    }
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => WindowChromeActions.Drag(this, e);
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowChromeActions.Minimize(this);
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowChromeActions.ToggleMaximize(this);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
