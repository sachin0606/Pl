using System;
using System.Windows;
using System.Windows.Input;
using BroadcastPlayout.ViewModels;

namespace BroadcastPlayout.Views;

public partial class FrameComparisonWindow : Window
{
    private readonly MainViewModel _vm;

    public FrameComparisonWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        WindowChromeActions.ApplyCleanBorder(this);

        var s = _vm.Settings.ProfessionalBroadcast;
        OperationsWithFramesCheck.IsChecked = s.EnableFrameComparison;
        CompareOnLiveCheck.IsChecked = s.CompareFramesOnLive;
        CompareOnUrlCheck.IsChecked = s.CompareFramesOnUrl;

        RecognizeBothRadio.IsChecked = s.FrameCompareType.Contains("Both", StringComparison.OrdinalIgnoreCase);
        RecognizeOnlyOutRadio.IsChecked = !s.FrameCompareType.Contains("Both", StringComparison.OrdinalIgnoreCase);

        CcoeffNormedRadio.IsChecked = s.FrameCompareMethod.Equals("CcoeffNormed", StringComparison.OrdinalIgnoreCase);
        SqdiffNormedRadio.IsChecked = !s.FrameCompareMethod.Equals("CcoeffNormed", StringComparison.OrdinalIgnoreCase);

        PrecisionOutText.Text = s.FrameComparePrecisionOut.ToString("0");
        PrecisionInText.Text = s.FrameComparePrecisionIn.ToString("0");
        TimeoutOutText.Text = s.FrameCompareTimeoutOutFoundSeconds.ToString("0");
        WaitSecondAfterFirstCheck.IsChecked = s.FrameCompareWaitSecondAfterFirst;
        EmergencyTimeText.Text = s.FrameCompareEmergencyTimeMinutes.ToString("0");
        EmergencyReturnCheck.IsChecked = s.FrameCompareEmergencyReturn;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => WindowChromeActions.Drag(this, e);
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowChromeActions.Minimize(this);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; return; }
        base.OnPreviewKeyDown(e);
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        var s = _vm.Settings.ProfessionalBroadcast;
        s.EnableFrameComparison = OperationsWithFramesCheck.IsChecked == true;
        s.CompareFramesOnLive = CompareOnLiveCheck.IsChecked == true;
        s.CompareFramesOnUrl = CompareOnUrlCheck.IsChecked == true;
        s.FrameCompareType = RecognizeBothRadio.IsChecked == true ? "Recognize Both OUT and IN Frames" : "Recognize Only OUT Frame";
        s.FrameCompareMethod = CcoeffNormedRadio.IsChecked == true ? "CcoeffNormed" : "SqdiffNormed";

        if (double.TryParse(PrecisionOutText.Text, out var pOut)) s.FrameComparePrecisionOut = pOut;
        if (double.TryParse(PrecisionInText.Text, out var pIn)) s.FrameComparePrecisionIn = pIn;
        if (double.TryParse(TimeoutOutText.Text, out var toOut)) s.FrameCompareTimeoutOutFoundSeconds = toOut;
        s.FrameCompareWaitSecondAfterFirst = WaitSecondAfterFirstCheck.IsChecked == true;
        if (double.TryParse(EmergencyTimeText.Text, out var emTime)) s.FrameCompareEmergencyTimeMinutes = emTime;
        s.FrameCompareEmergencyReturn = EmergencyReturnCheck.IsChecked == true;

        _vm.PersistSettings();
        _vm.AddRecentEvent($"FRAME COMPARISON UPDATED · {(s.EnableFrameComparison ? "ENABLED" : "DISABLED")} · {s.FrameCompareType} · Precision: {s.FrameComparePrecisionOut}%/{s.FrameComparePrecisionIn}%");

        MessageBox.Show("Operations with FRAMES settings confirmed and applied to active playout pipeline.",
            "Frame Comparison Settings Applied", MessageBoxButton.OK, MessageBoxImage.Information);

        Close();
    }
}
