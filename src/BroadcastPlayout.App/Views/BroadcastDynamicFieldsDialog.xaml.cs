using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BroadcastPlayout.Models;
using BroadcastPlayout.ViewModels;

namespace BroadcastPlayout.Views;

public partial class BroadcastDynamicFieldsDialog : Window
{
    private readonly MainViewModel? _vm;
    private readonly ProfessionalBroadcastSettings _settings;

    public BroadcastDynamicFieldsDialog(MainViewModel? vm = null, ProfessionalBroadcastSettings? settings = null, int initialTab = 0)
    {
        InitializeComponent();
        WindowChromeActions.ApplyCleanBorder(this);

        _vm = vm ?? (Application.Current.MainWindow?.DataContext as MainViewModel);
        _settings = settings ?? _vm?.Settings?.ProfessionalBroadcast ?? new ProfessionalBroadcastSettings();

        LoadSettings();

        if (initialTab == 1)
        {
            SwitchTab(false);
        }
        else
        {
            SwitchTab(true);
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => WindowChromeActions.Drag(this, e);

    private void TabNowNext_Click(object sender, RoutedEventArgs e) => SwitchTab(true);

    private void TabWeather_Click(object sender, RoutedEventArgs e) => SwitchTab(false);

    private void SwitchTab(bool showNowNext)
    {
        if (NowNextPanel == null || WeatherPanel == null) return;

        NowNextPanel.Visibility = showNowNext ? Visibility.Visible : Visibility.Collapsed;
        WeatherPanel.Visibility = showNowNext ? Visibility.Collapsed : Visibility.Visible;

        if (showNowNext)
        {
            TabNowNextBtn.Background = new SolidColorBrush(Color.FromRgb(62, 35, 89));
            TabNowNextBtn.Foreground = Brushes.White;
            TabWeatherBtn.Background = new SolidColorBrush(Color.FromRgb(37, 27, 49));
            TabWeatherBtn.Foreground = new SolidColorBrush(Color.FromRgb(183, 139, 231));
        }
        else
        {
            TabWeatherBtn.Background = new SolidColorBrush(Color.FromRgb(62, 35, 89));
            TabWeatherBtn.Foreground = Brushes.White;
            TabNowNextBtn.Background = new SolidColorBrush(Color.FromRgb(37, 27, 49));
            TabNowNextBtn.Foreground = new SolidColorBrush(Color.FromRgb(183, 139, 231));
        }
    }

    private void LoadSettings()
    {
        // Now / Next
        NowPlayingCheck.IsChecked = _settings.NowPlayingItem;
        NowDescriptionCheck.IsChecked = _settings.NowItemDescription;
        NowGenreCheck.IsChecked = _settings.NowItemGenre;
        NowYearCheck.IsChecked = _settings.NowItemYear;
        NowPosterCheck.IsChecked = _settings.NowItemPoster;
        NowVideoCheck.IsChecked = _settings.NowItemVideo;

        NextItemCheck.IsChecked = _settings.NextItem;
        NextStartTimeCheck.IsChecked = _settings.NextItemStartTime;
        NextDescriptionCheck.IsChecked = _settings.NextItemDescription;
        NextGenreCheck.IsChecked = _settings.NextItemGenre;
        NextYearCheck.IsChecked = _settings.NextItemYear;
        NextPosterCheck.IsChecked = _settings.NextItemPoster;
        NextVideoCheck.IsChecked = _settings.NextItemVideo;

        // Weather
        TemperatureCheck.IsChecked = _settings.WeatherTemperature;
        TempMinCheck.IsChecked = _settings.WeatherTempMin;
        TempMaxCheck.IsChecked = _settings.WeatherTempMax;
        FeelsLikeCheck.IsChecked = _settings.WeatherFeelsLike;
        WindSpeedCheck.IsChecked = _settings.WeatherWindSpeed;
        CityNameCheck.IsChecked = _settings.WeatherCityName;

        ConditionCheck.IsChecked = _settings.WeatherCondition;
        PressureCheck.IsChecked = _settings.WeatherPressure;
        HumidityCheck.IsChecked = _settings.WeatherHumidity;
        CloudsCheck.IsChecked = _settings.WeatherClouds;
        WeatherImageCheck.IsChecked = _settings.WeatherImage;
        DateCheck.IsChecked = _settings.WeatherDate;
        DayCheck.IsChecked = _settings.WeatherDay;

        MultiDayCheck.IsChecked = _settings.WeatherAddMultipleDays;
        switch (_settings.WeatherMultipleDaysCount)
        {
            case 2: Days2Radio.IsChecked = true; break;
            case 4: Days4Radio.IsChecked = true; break;
            case 5: Days5Radio.IsChecked = true; break;
            default: Days3Radio.IsChecked = true; break;
        }
    }

    private void SaveSettings()
    {
        // Now / Next
        _settings.NowPlayingItem = NowPlayingCheck.IsChecked == true;
        _settings.NowItemDescription = NowDescriptionCheck.IsChecked == true;
        _settings.NowItemGenre = NowGenreCheck.IsChecked == true;
        _settings.NowItemYear = NowYearCheck.IsChecked == true;
        _settings.NowItemPoster = NowPosterCheck.IsChecked == true;
        _settings.NowItemVideo = NowVideoCheck.IsChecked == true;

        _settings.NextItem = NextItemCheck.IsChecked == true;
        _settings.NextItemStartTime = NextStartTimeCheck.IsChecked == true;
        _settings.NextItemGenre = NextGenreCheck.IsChecked == true;
        _settings.NextItemYear = NextYearCheck.IsChecked == true;
        _settings.NextItemPoster = NextPosterCheck.IsChecked == true;
        _settings.NextItemVideo = NextVideoCheck.IsChecked == true;

        // Weather
        _settings.WeatherTemperature = TemperatureCheck.IsChecked == true;
        _settings.WeatherTempMin = TempMinCheck.IsChecked == true;
        _settings.WeatherTempMax = TempMaxCheck.IsChecked == true;
        _settings.WeatherFeelsLike = FeelsLikeCheck.IsChecked == true;
        _settings.WeatherWindSpeed = WindSpeedCheck.IsChecked == true;
        _settings.WeatherCityName = CityNameCheck.IsChecked == true;

        _settings.WeatherCondition = ConditionCheck.IsChecked == true;
        _settings.WeatherPressure = PressureCheck.IsChecked == true;
        _settings.WeatherHumidity = HumidityCheck.IsChecked == true;
        _settings.WeatherClouds = CloudsCheck.IsChecked == true;
        _settings.WeatherImage = WeatherImageCheck.IsChecked == true;
        _settings.WeatherDate = DateCheck.IsChecked == true;
        _settings.WeatherDay = DayCheck.IsChecked == true;

        _settings.WeatherAddMultipleDays = MultiDayCheck.IsChecked == true;
        if (Days2Radio.IsChecked == true) _settings.WeatherMultipleDaysCount = 2;
        else if (Days4Radio.IsChecked == true) _settings.WeatherMultipleDaysCount = 4;
        else if (Days5Radio.IsChecked == true) _settings.WeatherMultipleDaysCount = 5;
        else _settings.WeatherMultipleDaysCount = 3;
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

    private void SelectAllWeather_Click(object sender, RoutedEventArgs e)
    {
        var allChecked = TemperatureCheck.IsChecked == true &&
                         TempMinCheck.IsChecked == true &&
                         TempMaxCheck.IsChecked == true &&
                         FeelsLikeCheck.IsChecked == true &&
                         WindSpeedCheck.IsChecked == true &&
                         CityNameCheck.IsChecked == true &&
                         ConditionCheck.IsChecked == true &&
                         PressureCheck.IsChecked == true &&
                         HumidityCheck.IsChecked == true &&
                         CloudsCheck.IsChecked == true &&
                         WeatherImageCheck.IsChecked == true &&
                         DateCheck.IsChecked == true &&
                         DayCheck.IsChecked == true;
        var target = !allChecked;
        TemperatureCheck.IsChecked = target;
        TempMinCheck.IsChecked = target;
        TempMaxCheck.IsChecked = target;
        FeelsLikeCheck.IsChecked = target;
        WindSpeedCheck.IsChecked = target;
        CityNameCheck.IsChecked = target;
        ConditionCheck.IsChecked = target;
        PressureCheck.IsChecked = target;
        HumidityCheck.IsChecked = target;
        CloudsCheck.IsChecked = target;
        WeatherImageCheck.IsChecked = target;
        DateCheck.IsChecked = target;
        DayCheck.IsChecked = target;
    }

    private void ApplySave_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        Close();
    }
}
