using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.IO.MemoryMappedFiles;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Kashtrix.OutputEngine.Models;
using Kashtrix.OutputEngine.Services;
using BroadcastPlayout.Outputs;
using ThemedMessageBox = BroadcastPlayout.Views.MessageBox;

namespace Kashtrix.OutputEngine
{
    public partial class MainWindow : Window
    {
        private readonly BroadcastOutputEngine _engine = BroadcastOutputEngine.Instance;
        private readonly Scte35DvbService _dvbService = Scte35DvbService.Instance;
        private DispatcherTimer? _uiRefreshTimer;
        private string _activeCategoryFilter = "All";
        private bool _initialized;
        private MemoryMappedFile? _programVideoMap;
        private MemoryMappedViewAccessor? _programVideoView;
        private WriteableBitmap? _programPreviewBitmap;
        private byte[]? _programPreviewPixels;
        private long _programPreviewSequence;
        private DateTime _lastProgramPreviewUtc = DateTime.MinValue;
        private bool _programIsLive;
        private string _activePreviewMapName = string.Empty;

        public ObservableCollection<BroadcastEventLog> FilteredLogs { get; } = new();

        public MainWindow()
        {
            InitializeComponent();
            BroadcastPlayout.Views.WindowChromeActions.ApplyCleanBorder(this);
            Loaded += MainWindow_Loaded;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _initialized = true;

            OutputsDataGrid.ItemsSource = _engine.Outputs;
            LogListBox.ItemsSource = FilteredLogs;

            _dvbService.LogAdded += OnLogReceived;

            _uiRefreshTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                // Follow the default 50 fps Program cadence. A 30 fps sampler creates an
                // uneven 1/2-frame skip pattern for 50 fps crawls and makes a correct ticker
                // look as though it is juddering in the confidence monitor.
                Interval = TimeSpan.FromMilliseconds(20)
            };
            _uiRefreshTimer.Tick += UiRefreshTimer_Tick;
            _uiRefreshTimer.Start();

            TabAll.IsChecked = true;
            RefreshLogView();
        }

        private void UiRefreshTimer_Tick(object? sender, EventArgs e)
        {
            int total = _engine.Outputs.Count;
            int running = _engine.Outputs.Count(c => c.IsEnabled && c.Status == OutputStatus.Online);
            ActiveOutputsText.Text = $"{running} / {total} ONLINE";

            double aggregateBitrate = _engine.Outputs.Where(c => c.IsEnabled && !c.IsCardHardware).Sum(c => c.BitrateMbps);
            AggregateBitrateText.Text = $"{aggregateBitrate:F2} Mbps";

            long totalFrames = _engine.Outputs.Sum(c => c.FramesTransmitted);
            TotalFramesText.Text = $"{totalFrames:N0} f";

            long totalDropped = _engine.Outputs.Sum(c => c.DroppedFrames);
            double dropPercent = totalFrames > 0 ? ((double)totalDropped / totalFrames) * 100.0 : 0.0;
            DroppedFramesText.Text = $"{totalDropped:N0} f ({dropPercent:F2}%)";

            DateTime now = DateTime.Now;
            int frameNumber = (int)(now.Millisecond / 20.0);
            MasterTimecodeText.Text = $"{now:HH:mm:ss}:{frameNumber:D2}";
            RefreshProgramPreview();
            var receiving = DateTime.UtcNow - _lastProgramPreviewUtc < TimeSpan.FromSeconds(1);
            var live = receiving && _programIsLive;
            ProgramLiveBadge.Background = live ? new SolidColorBrush(Color.FromRgb(183, 28, 28)) : new SolidColorBrush(Color.FromRgb(55, 65, 81));
            ProgramLiveDot.Fill = live ? new SolidColorBrush(Color.FromRgb(255, 82, 82)) : new SolidColorBrush(Color.FromRgb(156, 163, 175));
            ProgramLiveText.Text = live ? "PGM LIVE" : receiving ? "PGM STANDBY" : "NO PROGRAM SIGNAL";
        }

        private void RefreshProgramPreview()
        {
            if (ProgramPreviewImage == null) return;
            try
            {
                var selectedOutput = OutputsDataGrid?.SelectedItem as OutputChannel;
                var source = selectedOutput?.InputSource ?? OutputInputSource.PlayoutProgram;
                var requestedMap = source switch
                {
                    OutputInputSource.CgProgram => VirtualOutputBridge.CgProgramVideoMapName,
                    OutputInputSource.Manual when string.Equals(selectedOutput?.ManualInputSource, "CG PROGRAM", StringComparison.OrdinalIgnoreCase) => VirtualOutputBridge.CgProgramVideoMapName,
                    OutputInputSource.Manual when string.Equals(selectedOutput?.ManualInputSource, "PLAYOUT PROGRAM", StringComparison.OrdinalIgnoreCase) => VirtualOutputBridge.ProgramConfidenceVideoMapName,
                    OutputInputSource.Manual => string.Empty,
                    _ => VirtualOutputBridge.ProgramConfidenceVideoMapName
                };
                ProgramMonitorLabel.Text = source switch
                {
                    OutputInputSource.CgProgram => "CG PROGRAM BUS · ALPHA/FILL CONFIDENCE",
                    OutputInputSource.Manual => $"MANUAL INPUT · {selectedOutput?.ManualInputSource}",
                    _ => "PLAYOUT PROGRAM BUS · CONFIDENCE MONITOR"
                };
                if (!string.Equals(requestedMap, _activePreviewMapName, StringComparison.Ordinal))
                {
                    _programVideoView?.Dispose(); _programVideoView = null;
                    _programVideoMap?.Dispose(); _programVideoMap = null;
                    _programPreviewSequence = 0;
                    _lastProgramPreviewUtc = DateTime.MinValue;
                    _activePreviewMapName = requestedMap;
                }
                if (string.IsNullOrWhiteSpace(requestedMap)) return;
                _programVideoMap ??= MemoryMappedFile.OpenExisting(requestedMap, MemoryMappedFileRights.Read);
                _programVideoView ??= _programVideoMap.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                if (_programVideoView.ReadInt32(0) != 0x4B545856) return;
                var width = _programVideoView.ReadInt32(8);
                var height = _programVideoView.ReadInt32(12);
                var stride = _programVideoView.ReadInt32(16);
                var sequence = _programVideoView.ReadInt64(28);
                var length = _programVideoView.ReadInt32(44);
                var isLive = _programVideoView.ReadInt32(48) != 0;
                if (sequence == _programPreviewSequence || width <= 0 || height <= 0 || stride < width * 4 || length <= 0 || length > stride * height) return;
                var pixels = _programPreviewPixels;
                if (pixels is null || pixels.Length != length)
                    pixels = _programPreviewPixels = new byte[length];
                _programVideoView.ReadArray(256, pixels, 0, pixels.Length);
                // The writer commits by storing sequence after the pixel copy. If it changes
                // while we read, discard this sample and try again on the next render tick.
                if (_programVideoView.ReadInt64(28) != sequence) return;
                if (_programPreviewBitmap is null || _programPreviewBitmap.PixelWidth != width || _programPreviewBitmap.PixelHeight != height)
                {
                    _programPreviewBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
                    ProgramPreviewImage.Source = _programPreviewBitmap;
                }
                _programPreviewBitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
                _programPreviewSequence = sequence;
                _lastProgramPreviewUtc = DateTime.UtcNow;
                _programIsLive = isLive;
            }
            catch
            {
                _programVideoView?.Dispose(); _programVideoView = null;
                _programVideoMap?.Dispose(); _programVideoMap = null;
            }
        }

        private void OnLogReceived(BroadcastEventLog log)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (MatchesFilter(log))
                {
                    FilteredLogs.Insert(0, log);
                    if (FilteredLogs.Count > 500)
                    {
                        FilteredLogs.RemoveAt(FilteredLogs.Count - 1);
                    }
                }
                if (LogCountText != null)
                {
                    LogCountText.Text = $"Events: {_dvbService.Logs.Count}";
                }
            });
        }

        private bool MatchesFilter(BroadcastEventLog log)
        {
            if (_activeCategoryFilter == "All") return true;
            if (_activeCategoryFilter == "DVB" && log.Category.Contains("DVB", StringComparison.OrdinalIgnoreCase)) return true;
            if (_activeCategoryFilter == "SCTE-35" && log.Category.Contains("SCTE-35", StringComparison.OrdinalIgnoreCase)) return true;
            if (_activeCategoryFilter == "MOS" && log.Category.Contains("MOS", StringComparison.OrdinalIgnoreCase)) return true;
            if (_activeCategoryFilter == "HLS/MPD" && (log.Category.Contains("HLS", StringComparison.OrdinalIgnoreCase) || log.Category.Contains("MPD", StringComparison.OrdinalIgnoreCase))) return true;
            return false;
        }

        private void RefreshLogView()
        {
            if (!_initialized || _dvbService == null || FilteredLogs == null) return;
            FilteredLogs.Clear();
            foreach (var log in _dvbService.Logs.Where(MatchesFilter).Take(500))
            {
                FilteredLogs.Add(log);
            }
            if (LogCountText != null)
            {
                LogCountText.Text = $"Events: {_dvbService.Logs.Count}";
            }
        }

        private void Filter_Changed(object sender, RoutedEventArgs e)
        {
            if (!_initialized || _dvbService == null || FilteredLogs == null) return;
            if (sender is not RadioButton rb) return;
            string name = rb.Name;

            _activeCategoryFilter = name switch
            {
                "TabDvb" => "DVB",
                "TabScte35" => "SCTE-35",
                "TabMos" => "MOS",
                "TabHlsMpd" => "HLS/MPD",
                _ => "All"
            };

            RefreshLogView();
        }

        private void AddOutput_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new AddOutputDialog { Owner = this };
            if (dialog.ShowDialog() == true && dialog.ResultChannel != null)
            {
                _engine.Outputs.Add(dialog.ResultChannel);
                _engine.SaveOutputs();
                _dvbService.Log("CONFIG", $"Provisioned new output destination: {dialog.ResultChannel.Name} [{dialog.ResultChannel.ProtocolDisplayName}] ({dialog.ResultChannel.GpuEncoder})", "SUCCESS");
            }
        }

        private void InspectAlert_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is OutputChannel channel)
            {
                var dialog = new InspectOutputDialog(channel) { Owner = this };
                dialog.ShowDialog();
            }
        }

        private void ToggleChannel_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is OutputChannel channel)
            {
                _engine.ToggleOutput(channel);
            }
        }

        private void StartChannel_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is OutputChannel channel) _engine.StartOutput(channel);
        }

        private void StopChannel_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is OutputChannel channel) _engine.StopOutput(channel);
        }

        private void RestartChannel_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is OutputChannel channel) _engine.RestartOutput(channel);
        }

        private void DeleteChannel_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not OutputChannel channel) return;
            if (ThemedMessageBox.Show(this, $"Delete output '{channel.Name}'?", "Kashtrix Output Engine", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                _engine.DeleteOutput(channel);
        }

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => BroadcastPlayout.Views.WindowChromeActions.Drag(this, e);
        private void Minimize_Click(object sender, RoutedEventArgs e) => BroadcastPlayout.Views.WindowChromeActions.Minimize(this);
        private void Maximize_Click(object sender, RoutedEventArgs e) => BroadcastPlayout.Views.WindowChromeActions.ToggleMaximize(this);
        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void StartAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var ch in _engine.Outputs)
                _engine.StartOutput(ch);
            _dvbService.Log("ENGINE", "All broadcast outputs resumed by operator", "INFO");
        }

        private void StopAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var ch in _engine.Outputs)
            {
                ch.IsEnabled = false;
                ch.Status = OutputStatus.Standby;
            }
            _dvbService.Log("ENGINE", "All broadcast outputs halted by operator", "WARN");
        }

        private void ClearLogs_Click(object sender, RoutedEventArgs e)
        {
            _dvbService.Logs.Clear();
            FilteredLogs.Clear();
            if (LogCountText != null)
            {
                LogCountText.Text = "Events: 0";
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _initialized = false;
            try { _uiRefreshTimer?.Stop(); } catch { }
            try { _programVideoView?.Dispose(); } catch { }
            _programVideoView = null;
            try { _programVideoMap?.Dispose(); } catch { }
            _programVideoMap = null;
            try { if (_dvbService != null) _dvbService.LogAdded -= OnLogReceived; } catch { }
            base.OnClosed(e);
        }
    }
}
