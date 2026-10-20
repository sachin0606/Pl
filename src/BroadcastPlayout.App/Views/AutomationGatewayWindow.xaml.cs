using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BroadcastPlayout.Services;
using BroadcastPlayout.ViewModels;

namespace BroadcastPlayout.Views;

public partial class AutomationGatewayWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly AutomationGatewayService _gateway;

    public AutomationGatewayWindow(MainViewModel vm, AutomationGatewayService gateway)
    {
        InitializeComponent();
        WindowChromeActions.ApplyCleanBorder(this);
        _vm = vm;
        _gateway = gateway;
        LoadProfile(gateway.Profile);
        LoadMosSample();
        _gateway.Log += OnLog;
        Closed += (_, _) => _gateway.Log -= OnLog;
        UpdateStatus();
    }

    private void LoadProfile(AutomationGatewayProfile p)
    {
        AutoStartCheckBox.IsChecked = p.AutoStart;
        CommandPortBox.Text = p.CommandPort.ToString();
        MosLowerPortBox.Text = p.MosLowerPort.ToString();
        MosUpperPortBox.Text = p.MosUpperPort.ToString();
        NcsHostBox.Text = p.NcsHost;
        MosIdBox.Text = p.MosId;
        NcsIdBox.Text = p.NcsId;
        TcpHostBox.Text = p.TcpTargetHost;
        TcpTargetPortBox.Text = p.TcpTargetPort.ToString();
        DtmfSequenceBox.Text = p.DtmfSequence;
        DtmfToneMsBox.Text = p.DtmfToneMilliseconds.ToString();
        DtmfGapMsBox.Text = p.DtmfGapMilliseconds.ToString();
        DtmfLevelBox.Text = p.DtmfLevelPercent.ToString();
        DialDurationBox.Text = p.DialToneMilliseconds.ToString();
        DialLevelBox.Text = p.DialToneLevelPercent.ToString();
    }

    private AutomationGatewayProfile ReadProfile()
    {
        static int I(string? text, int fallback) => int.TryParse(text, out var value) ? value : fallback;
        return new AutomationGatewayProfile
        {
            AutoStart = AutoStartCheckBox.IsChecked == true,
            CommandPort = I(CommandPortBox.Text, 9101),
            MosLowerPort = I(MosLowerPortBox.Text, 10540),
            MosUpperPort = I(MosUpperPortBox.Text, 10541),
            NcsHost = NcsHostBox.Text,
            MosId = MosIdBox.Text,
            NcsId = NcsIdBox.Text,
            TcpTargetHost = TcpHostBox.Text,
            TcpTargetPort = I(TcpTargetPortBox.Text, 9101),
            DtmfSequence = DtmfSequenceBox.Text,
            DtmfToneMilliseconds = I(DtmfToneMsBox.Text, 120),
            DtmfGapMilliseconds = I(DtmfGapMsBox.Text, 55),
            DtmfLevelPercent = I(DtmfLevelBox.Text, 72),
            DialToneMilliseconds = I(DialDurationBox.Text, 2500),
            DialToneLevelPercent = I(DialLevelBox.Text, 55)
        }.Normalize();
    }

    private void OnLog(string message)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => OnLog(message)); return; }
        LogList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss.fff}  {message}");
        while (LogList.Items.Count > 500) LogList.Items.RemoveAt(LogList.Items.Count - 1);
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (GatewayStatusText is null) return;
        GatewayStatusText.Text = _gateway.IsRunning
            ? $"RUNNING · TCP {_gateway.CommandPort} · MOS UPPER {_gateway.MosPort}"
            : "STOPPED";
        GatewayStatusText.Foreground = _gateway.IsRunning
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(91, 211, 124))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 184, 77));
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var profile = ReadProfile();
            _gateway.Start(profile);
            OnLog("PROFILE APPLIED · gateway listeners started");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Automation Gateway", MessageBoxButton.OK, MessageBoxImage.Warning); }
        UpdateStatus();
    }

    private void Stop_Click(object sender, RoutedEventArgs e) { _gateway.Stop(); UpdateStatus(); }
    private void SaveProfile_Click(object sender, RoutedEventArgs e) { _gateway.SaveProfile(ReadProfile()); OnLog("PROFILE SAVED"); }
    private void Execute_Click(object sender, RoutedEventArgs e) { _vm.ExecuteExternalCommand(TestCommandBox.Text); OnLog("LOCAL CMD · " + TestCommandBox.Text); }

    private async void SendTcp_Click(object sender, RoutedEventArgs e)
    {
        var p = ReadProfile();
        _gateway.SaveProfile(p);
        try
        {
            var reply = await _gateway.SendTcpAsync(p.TcpTargetHost, p.TcpTargetPort, TestCommandBox.Text);
            if (!string.IsNullOrWhiteSpace(reply)) OnLog("TCP REPLY · " + reply);
        }
        catch (Exception ex) { OnLog("TCP ERROR · " + ex.Message); MessageBox.Show(ex.Message, "TCP Test", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void LoadMosSample_Click(object sender, RoutedEventArgs e) => LoadMosSample();
    private void LoadMosSample()
    {
        var p = _gateway.Profile;
        MosXmlBox.Text = $"<mos>\r\n  <mosID>{p.MosId}</mosID>\r\n  <ncsID>{p.NcsId}</ncsID>\r\n  <roCreate>\r\n    <roID>KASHTRIX-DEMO-001</roID>\r\n    <roSlug>Kashtrix MOS Demo Rundown</roSlug>\r\n  </roCreate>\r\n</mos>";
    }

    private async void SendMos_Click(object sender, RoutedEventArgs e)
    {
        var p = ReadProfile();
        _gateway.SaveProfile(p);
        try { await _gateway.SendMosAsync(p.NcsHost, p.MosLowerPort, MosXmlBox.Text); }
        catch (Exception ex) { OnLog("MOS TX ERROR · " + ex.Message); MessageBox.Show(ex.Message, "MOS Test", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void DtmfKey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Content is not null) DtmfSequenceBox.Text += b.Content.ToString();
    }

    private void ClearDtmf_Click(object sender, RoutedEventArgs e) => DtmfSequenceBox.Clear();

    private async void TestDtmf_Click(object sender, RoutedEventArgs e)
    {
        var p = ReadProfile();
        _gateway.SaveProfile(p);
        try
        {
            OnLog($"DTMF TEST · {p.DtmfSequence} · tone={p.DtmfToneMilliseconds}ms gap={p.DtmfGapMilliseconds}ms level={p.DtmfLevelPercent}%");
            await _vm.TestDtmfAsync(p.DtmfSequence, p.DtmfToneMilliseconds, p.DtmfGapMilliseconds, p.DtmfLevelPercent);
        }
        catch (Exception ex) { OnLog("DTMF ERROR · " + ex.Message); }
    }

    private async void TestDialTone_Click(object sender, RoutedEventArgs e)
    {
        var p = ReadProfile();
        _gateway.SaveProfile(p);
        try
        {
            OnLog($"DIAL TONE TEST · 350+440 Hz · {p.DialToneMilliseconds}ms · {p.DialToneLevelPercent}%");
            await _vm.TestDialToneAsync(p.DialToneMilliseconds, p.DialToneLevelPercent);
        }
        catch (Exception ex) { OnLog("DIAL TONE ERROR · " + ex.Message); }
    }

    private async void TestLineTone_Click(object sender, RoutedEventArgs e)
    {
        var p = ReadProfile();
        try
        {
            OnLog("LINE-UP TONE TEST · 1000 Hz");
            await _vm.TestLineupToneAsync(1200, Math.Min(70, p.DialToneLevelPercent));
        }
        catch (Exception ex) { OnLog("LINE TONE ERROR · " + ex.Message); }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => LogList.Items.Clear();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) try { DragMove(); } catch { } }
}
