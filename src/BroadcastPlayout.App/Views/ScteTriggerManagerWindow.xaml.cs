using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using BroadcastPlayout.Models;
using BroadcastPlayout.Professional;
using BroadcastPlayout.ViewModels;

namespace BroadcastPlayout.Views;

public class ScteTriggerPreset
{
    public string Name { get; set; } = string.Empty;
    public double DurationSeconds { get; set; } = 60.0;
    public bool AutoReturn { get; set; } = true;
    public bool OutOfNetwork { get; set; } = true;
    public string SpliceType { get; set; } = "Start Normal";
    public string Summary => $"{SpliceType} · {DurationSeconds}s · {(AutoReturn ? "Auto-Return" : "Manual")}";
}

public partial class ScteTriggerManagerWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly ObservableCollection<ScteTriggerPreset> _presets = [];
    private uint _lastEventId = 1001;

    public ScteTriggerManagerWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        WindowChromeActions.ApplyCleanBorder(this);

        _presets.Add(new ScteTriggerPreset { Name = "Commercial Break 60s (Cue In)", DurationSeconds = 60.0, AutoReturn = true, OutOfNetwork = true, SpliceType = "Start Normal" });
        _presets.Add(new ScteTriggerPreset { Name = "Commercial Break 30s (Cue In)", DurationSeconds = 30.0, AutoReturn = true, OutOfNetwork = true, SpliceType = "Start Normal" });
        _presets.Add(new ScteTriggerPreset { Name = "Commercial Break 90s (Cue In)", DurationSeconds = 90.0, AutoReturn = true, OutOfNetwork = true, SpliceType = "Start Normal" });
        _presets.Add(new ScteTriggerPreset { Name = "Immediate Cue In (No Return)", DurationSeconds = 0.0, AutoReturn = false, OutOfNetwork = true, SpliceType = "Start Immediate" });
        _presets.Add(new ScteTriggerPreset { Name = "Emergency Program Return (Cue Out)", DurationSeconds = 0.0, AutoReturn = false, OutOfNetwork = false, SpliceType = "End Immediate" });

        TriggerListBox.ItemsSource = _presets;
        TriggerListBox.SelectedIndex = 0;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => WindowChromeActions.Drag(this, e);
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowChromeActions.Minimize(this);
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    private void Ok_Click(object sender, RoutedEventArgs e) => Close();

    private void TriggerListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (TriggerListBox.SelectedItem is ScteTriggerPreset p)
        {
            TriggerNameText.Text = p.Name;
            DurationText.Text = p.DurationSeconds.ToString("0");
            AutoReturnTrueRadio.IsChecked = p.AutoReturn;
            AutoReturnFalseRadio.IsChecked = !p.AutoReturn;
            OutOfNetTrueRadio.IsChecked = p.OutOfNetwork;
            OutOfNetFalseRadio.IsChecked = !p.OutOfNetwork;
            ImmediateTrueRadio.IsChecked = p.SpliceType.Contains("Immediate", StringComparison.OrdinalIgnoreCase);
            ImmediateFalseRadio.IsChecked = !p.SpliceType.Contains("Immediate", StringComparison.OrdinalIgnoreCase);
        }
    }

    private ScteSpliceCue BuildCurrentCue(string forceSpliceType = "")
    {
        _ = double.TryParse(DurationText.Text, out var duration);
        _ = double.TryParse(PtsAdjustmentText.Text, out var ptsAdj);
        _ = byte.TryParse(AvailNumText.Text, out var availNum);
        _ = byte.TryParse(AvailsExpectedText.Text, out var availsExp);

        var isCancel = CancelTrueRadio.IsChecked == true;
        var isOutOfNet = OutOfNetTrueRadio.IsChecked == true;
        var isImmediate = ImmediateTrueRadio.IsChecked == true;
        var autoReturn = AutoReturnTrueRadio.IsChecked == true;

        var spliceType = !string.IsNullOrWhiteSpace(forceSpliceType) 
            ? forceSpliceType 
            : (isCancel ? "Cancel" : (isOutOfNet ? (isImmediate ? "Start Immediate" : "Start Normal") : (isImmediate ? "End Immediate" : "End Normal")));

        var cue = new ScteSpliceCue
        {
            EventId = _lastEventId++,
            SpliceType = spliceType,
            IsCueIn = spliceType.StartsWith("Start", StringComparison.OrdinalIgnoreCase),
            IsCueOut = spliceType.StartsWith("End", StringComparison.OrdinalIgnoreCase),
            SpliceEventCancelIndicator = isCancel,
            ProgramSpliceFlag = ProgramSpliceCheck.IsChecked == true,
            SpliceImmediate = isImmediate,
            DurationSeconds = BreakDurationCheck.IsChecked == true ? duration : 0.0,
            AutoReturn = autoReturn,
            PtsAdjustmentSeconds = ptsAdj,
            AvailNum = availNum,
            AvailsExpected = availsExp,
            Source = "Playout Control Panel",
            Timestamp = DateTime.UtcNow
        };

        return cue;
    }

    private void StartImmediateNoAutoReturn_Click(object sender, RoutedEventArgs e)
    {
        var cue = BuildCurrentCue("Start Immediate");
        cue.AutoReturn = false;
        cue.DurationSeconds = 0;
        _lastEventId = cue.EventId;

        _vm.ExecuteManualScteCue(cue, GetSelectedOnBreakPlay());
        MessageBox.Show($"Triggered SCTE-35 / SCTE-104 Immediate Cue In:\n\nEvent ID: {cue.EventId}\nType: {cue.SpliceType}\nAuto Return: False\nOn-Break Mode: {GetSelectedOnBreakPlay()}",
            "SCTE-35 / 104 Cue In Sent", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void StopImmediateCueOut_Click(object sender, RoutedEventArgs e)
    {
        var cue = BuildCurrentCue("End Immediate");
        if (SameEventIdCheck.IsChecked == true && _lastEventId > 1)
        {
            cue.EventId = _lastEventId - 1;
        }

        _vm.ExecuteManualScteCue(cue, GetSelectedOnBreakPlay());
        MessageBox.Show($"Triggered SCTE-35 / SCTE-104 Immediate Cue Out:\n\nEvent ID: {cue.EventId}\nType: {cue.SpliceType}\nReturning to Program Playout",
            "SCTE-35 / 104 Cue Out Sent", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void StartImmediateWithDuration_Click(object sender, RoutedEventArgs e)
    {
        _ = double.TryParse(DurationText.Text, out var duration);
        if (duration <= 0) duration = 60.0;

        var cue = BuildCurrentCue("Start Immediate");
        cue.AutoReturn = true;
        cue.DurationSeconds = duration;
        _lastEventId = cue.EventId;

        _vm.ExecuteManualScteCue(cue, GetSelectedOnBreakPlay());
        MessageBox.Show($"Triggered SCTE-35 / SCTE-104 Cue In with Fixed Duration:\n\nEvent ID: {cue.EventId}\nDuration: {cue.DurationSeconds}s\nAuto Return: TRUE\nOn-Break Mode: {GetSelectedOnBreakPlay()}",
            "SCTE-35 / 104 Timed Cue In Sent", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void CreateScte35_Click(object sender, RoutedEventArgs e)
    {
        var cue = BuildCurrentCue();
        var item = new PlaylistItem
        {
            Title = string.IsNullOrWhiteSpace(TriggerNameText.Text) ? $"SCTE-35 {cue.SpliceType}" : TriggerNameText.Text.Trim(),
            EventType = "SCTE35",
            Category = "Signaling",
            ScteSpliceType = cue.SpliceType,
            ScteEventId = cue.EventId,
            ScteDurationSeconds = (int)Math.Round(cue.DurationSeconds),
            ScteAutoReturn = cue.AutoReturn,
            ScteAvailNum = cue.AvailNum,
            ScteAvailsExpected = cue.AvailsExpected
        };

        _vm.InsertPlaylistItem(item);
        MessageBox.Show($"Created SCTE-35 Event and added to Rundown Playlist:\n\nTitle: {item.Title}\nSplice: {item.ScteSpliceType}\nEvent ID: {item.ScteEventId}\nDuration: {item.ScteDurationSeconds}s",
            "SCTE-35 Event Created", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private string GetSelectedOnBreakPlay()
    {
        if (PlayBlackRadio.IsChecked == true) return "Black Image";
        if (PlayCustomImageRadio.IsChecked == true) return "Custom Image";
        if (PlayCustomVideoRadio.IsChecked == true) return "Custom Video";
        return "Current File in Playlist";
    }
}
