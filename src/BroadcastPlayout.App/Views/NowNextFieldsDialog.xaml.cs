using System.Windows;
using System.Windows.Input;
using BroadcastPlayout.ViewModels;

namespace BroadcastPlayout.Views;

public partial class NowNextFieldsDialog : Window
{
    private readonly MainViewModel _vm;

    public NowNextFieldsDialog(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        WindowChromeActions.ApplyCleanBorder(this);

        var s = _vm.Settings.ProfessionalBroadcast;
        NowPlayingCheck.IsChecked = s.NowPlayingItem;
        NowDescriptionCheck.IsChecked = s.NowItemDescription;
        NowGenreCheck.IsChecked = s.NowItemGenre;
        NowYearCheck.IsChecked = s.NowItemYear;
        NowPosterCheck.IsChecked = s.NowItemPoster;
        NowVideoCheck.IsChecked = s.NowItemVideo;

        NextItemCheck.IsChecked = s.NextItem;
        NextStartTimeCheck.IsChecked = s.NextItemStartTime;
        NextDescriptionCheck.IsChecked = s.NextItemDescription;
        NextGenreCheck.IsChecked = s.NextItemGenre;
        NextYearCheck.IsChecked = s.NextItemYear;
        NextPosterCheck.IsChecked = s.NextItemPoster;
        NextVideoCheck.IsChecked = s.NextItemVideo;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => WindowChromeActions.Drag(this, e);
    private void Close_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        Close();
    }

    private void SelectAllNow_Click(object sender, RoutedEventArgs e)
    {
        var allChecked = NowPlayingCheck.IsChecked == true && NowDescriptionCheck.IsChecked == true &&
                         NowGenreCheck.IsChecked == true && NowYearCheck.IsChecked == true &&
                         NowPosterCheck.IsChecked == true && NowVideoCheck.IsChecked == true;
        var target = !allChecked;
        NowPlayingCheck.IsChecked = target;
        NowDescriptionCheck.IsChecked = target;
        NowGenreCheck.IsChecked = target;
        NowYearCheck.IsChecked = target;
        NowPosterCheck.IsChecked = target;
        NowVideoCheck.IsChecked = target;
    }

    private void SelectAllNext_Click(object sender, RoutedEventArgs e)
    {
        var allChecked = NextItemCheck.IsChecked == true && NextStartTimeCheck.IsChecked == true &&
                         NextDescriptionCheck.IsChecked == true && NextGenreCheck.IsChecked == true &&
                         NextYearCheck.IsChecked == true && NextPosterCheck.IsChecked == true &&
                         NextVideoCheck.IsChecked == true;
        var target = !allChecked;
        NextItemCheck.IsChecked = target;
        NextStartTimeCheck.IsChecked = target;
        NextDescriptionCheck.IsChecked = target;
        NextGenreCheck.IsChecked = target;
        NextYearCheck.IsChecked = target;
        NextPosterCheck.IsChecked = target;
        NextVideoCheck.IsChecked = target;
    }

    private void SameInNext_Click(object sender, RoutedEventArgs e)
    {
        NextItemCheck.IsChecked = NowPlayingCheck.IsChecked;
        NextDescriptionCheck.IsChecked = NowDescriptionCheck.IsChecked;
        NextGenreCheck.IsChecked = NowGenreCheck.IsChecked;
        NextYearCheck.IsChecked = NowYearCheck.IsChecked;
        NextPosterCheck.IsChecked = NowPosterCheck.IsChecked;
        NextVideoCheck.IsChecked = NowVideoCheck.IsChecked;
    }

    private void SameInCurrent_Click(object sender, RoutedEventArgs e)
    {
        NowPlayingCheck.IsChecked = NextItemCheck.IsChecked;
        NowDescriptionCheck.IsChecked = NextDescriptionCheck.IsChecked;
        NowGenreCheck.IsChecked = NextGenreCheck.IsChecked;
        NowYearCheck.IsChecked = NextYearCheck.IsChecked;
        NowPosterCheck.IsChecked = NextPosterCheck.IsChecked;
        NowVideoCheck.IsChecked = NextVideoCheck.IsChecked;
    }

    private void SaveSettings()
    {
        var s = _vm.Settings.ProfessionalBroadcast;
        s.NowPlayingItem = NowPlayingCheck.IsChecked == true;
        s.NowItemDescription = NowDescriptionCheck.IsChecked == true;
        s.NowItemGenre = NowGenreCheck.IsChecked == true;
        s.NowItemYear = NowYearCheck.IsChecked == true;
        s.NowItemPoster = NowPosterCheck.IsChecked == true;
        s.NowItemVideo = NowVideoCheck.IsChecked == true;

        s.NextItem = NextItemCheck.IsChecked == true;
        s.NextItemStartTime = NextStartTimeCheck.IsChecked == true;
        s.NextItemDescription = NextDescriptionCheck.IsChecked == true;
        s.NextItemGenre = NextGenreCheck.IsChecked == true;
        s.NextItemYear = NextYearCheck.IsChecked == true;
        s.NextItemPoster = NextPosterCheck.IsChecked == true;
        s.NextItemVideo = NextVideoCheck.IsChecked == true;

        _vm.PersistSettings();
    }
}
