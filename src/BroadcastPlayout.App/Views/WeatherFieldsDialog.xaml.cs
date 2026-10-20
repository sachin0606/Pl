using System.Windows;
using System.Windows.Input;
using BroadcastPlayout.Models;
using BroadcastPlayout.ViewModels;

namespace BroadcastPlayout.Views;

public partial class WeatherFieldsDialog : Window
{
    private readonly ProfessionalBroadcastSettings _settings;

    public WeatherFieldsDialog(MainViewModel? vm = null, ProfessionalBroadcastSettings? settings = null)
    {
        InitializeComponent();
        WindowChromeActions.ApplyCleanBorder(this);

        _settings = settings ?? vm?.Settings?.ProfessionalBroadcast ?? (Application.Current.MainWindow?.DataContext as MainViewModel)?.Settings?.ProfessionalBroadcast ?? new ProfessionalBroadcastSettings();
        LoadSettings();
    }

    private void LoadSettings()
    {
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

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => WindowChromeActions.Drag(this, e);

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        Close();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        DialogResult = true;
        Close();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
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
}
