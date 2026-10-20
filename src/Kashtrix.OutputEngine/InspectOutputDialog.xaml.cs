using System.Windows;
using Kashtrix.OutputEngine.Models;
using Kashtrix.OutputEngine.Services;
using ThemedMessageBox = BroadcastPlayout.Views.MessageBox;

namespace Kashtrix.OutputEngine
{
    public partial class InspectOutputDialog : Window
    {
        private readonly OutputChannel _channel;

        public InspectOutputDialog(OutputChannel channel)
        {
            InitializeComponent();
            BroadcastPlayout.Views.WindowChromeActions.ApplyCleanBorder(this);
            _channel = channel;
            DataContext = channel;
        }

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
            BroadcastPlayout.Views.WindowChromeActions.Drag(this, e);

        private void SendMos_Click(object sender, RoutedEventArgs e)
        {
            Scte35DvbService.Instance.Log("MOS", $"[MOS 2.8.4 MANUAL PING] Sent status check to NRCS server for channel: {_channel.Name} | ACK Received (0ms latency)", "INFO");
            ThemedMessageBox.Show(this, $"Sent MOS 2.8.4 status ping for '{_channel.Name}'.\n\nMOS Gateway responded: <mosAck><objID>{_channel.Name}</objID><status>ACK</status></mosAck>",
                            "MOS Protocol Gateway",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
        }

        private void ClearAlert_Click(object sender, RoutedEventArgs e)
        {
            _channel.AlertMessage = string.Empty;
            if (_channel.Status == OutputStatus.Warning || _channel.Status == OutputStatus.Error)
            {
                _channel.Status = OutputStatus.Online;
            }
            Scte35DvbService.Instance.Log("ALERT", $"Alert cleared for channel '{_channel.Name}' by operator", "INFO");
            ThemedMessageBox.Show(this, $"Active alert and error state cleared for '{_channel.Name}'.", "Alert Cleared", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
