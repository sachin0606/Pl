using System.Windows;
using System.Windows.Input;
using BroadcastPlayout.Services;

namespace BroadcastPlayout.Views;

public partial class ProgramLauncherWindow : Window
{
    public ProgramLauncherWindow()
    {
        InitializeComponent();
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string appKey) return;

        if (StandaloneAppLauncher.Launch(appKey, out var error))
        {
            StatusText.Text = $"Opened {FormatName(appKey)} as a standalone application.";
            return;
        }

        StatusText.Text = error;
        MessageBox.Show(error, "Kashtrix Program Launcher", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string FormatName(string appKey) => appKey switch
    {
        "CGEditor" => "CG Designer",
        "CGController" => "CG Controller",
        "PlaylistEditor" => "Playlist Editor",
        "ChannelController" => "Channel Controller",
        "FileManager" => "File Manager",
        "QCController" => "QC Controller",
        "MAM" => "Media Asset Management",
        "NRCS" => "NRCS",
        "Prompter" => "Prompter",
        "HAController" => "HA / Redundancy Controller",
        "ApiGateway" => "API Gateway / Web MCR",
        _ => appKey
    };

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => WindowChromeActions.Drag(this, e);
}
