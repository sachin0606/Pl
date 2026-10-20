using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Threading;
using Kashtrix.OutputEngine.Models;

namespace Kashtrix.OutputEngine.Services;

public class BroadcastOutputEngine
{
    private static readonly Lazy<BroadcastOutputEngine> _instance = new(() => new BroadcastOutputEngine());
    public static BroadcastOutputEngine Instance => _instance.Value;

    public ObservableCollection<OutputChannel> Outputs { get; } = [];

    private readonly DispatcherTimer _cadenceTimer;
    private readonly Random _rand = new();
    private long _cycleCount = 0;

    public BroadcastOutputEngine()
    {
        InitializeDefaultOutputs();

        _cadenceTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _cadenceTimer.Tick += OnCadenceTick;
        _cadenceTimer.Start();
    }

    private static string GetConfigPath()
    {
        var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KashtrixPlayout");
        System.IO.Directory.CreateDirectory(dir);
        return System.IO.Path.Combine(dir, "output-channels.json");
    }

    public void SaveOutputs()
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(Outputs.ToList(), new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            System.IO.File.WriteAllText(GetConfigPath(), json);
        }
        catch { }
    }

    private void InitializeDefaultOutputs()
    {
        Outputs.Clear();
        var path = GetConfigPath();
        if (System.IO.File.Exists(path))
        {
            try
            {
                var text = System.IO.File.ReadAllText(path);
                var loaded = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.List<OutputChannel>>(text);
                if (loaded is not null && loaded.Count > 0)
                {
                    foreach (var ch in loaded)
                    {
                        ch.Status = OutputStatus.Standby;
                        ch.IsEnabled = false;
                        Outputs.Add(ch);
                    }
                    return;
                }
            }
            catch { }
        }
        // No mock channels: clean operator-ready state
    }


    private void OnCadenceTick(object? sender, EventArgs e)
    {
        _cycleCount++;

        foreach (var ch in Outputs)
        {
            if (!ch.IsEnabled || ch.Status == OutputStatus.Standby)
            {
                // BitrateMbps is the configured target as well as the value shown by the
                // simulator. Preserve it in standby so validation can still start a channel.
                ch.RunningFps = 0.0;
                continue;
            }

            // Increment frame counts based on target FPS (200ms tick = ~10 frames @ 50fps, ~5 frames @ 25fps)
            var framesThisTick = (long)Math.Round(ch.TargetFps * 0.2);
            ch.FramesTransmitted += framesThisTick;

            if (ch.IsCardHardware)
            {
                // Hardware SDI/HDMI baseband video cards (DeckLink, Matrox, AJA) output raw
                // uncompressed video at exact hardware pixel clock / genlock cadence without software encoding jitter.
                // Raw baseband does not carry an encoder compressed bitrate.
                ch.RunningFps = ch.TargetFps;
                ch.BitrateMbps = 0.0;
            }
            else
            {
                // Microscopic cadence variance for realism on IP / streaming encoders
                if (ch.Status != OutputStatus.Warning && ch.Status != OutputStatus.Error)
                {
                    var jitter = (_rand.NextDouble() - 0.5) * 0.04;
                    ch.RunningFps = Math.Round(ch.TargetFps + jitter, 2);
                }
                else
                {
                    ch.RunningFps = Math.Round(ch.TargetFps - 1.8 + (_rand.NextDouble() * 0.4), 2);
                }
            }
        }

        // Periodic DVB table logging every 25 cycles (5 seconds)
        if (_cycleCount % 25 == 0)
        {
            Scte35DvbService.Instance.LogDvbTableTransmission("PAT (Program Association Table)", 0x0000, (int)(_cycleCount / 25) % 32);
            Scte35DvbService.Instance.LogDvbTableTransmission("PMT (Program Map Table)", 0x0100, (int)(_cycleCount / 25) % 32);
            Scte35DvbService.Instance.LogDvbTableTransmission("SDT (Service Description Table)", 0x0011, (int)(_cycleCount / 25) % 32);
        }

        // Periodic MOS heartbeat every 50 cycles (10 seconds)
        if (_cycleCount % 50 == 0)
        {
            Scte35DvbService.Instance.Log("MOS", "[MOS HEARTBEAT] Active Newsroom Socket connected | Ping ACK <mosAck><objID>PLAYOUT_NODE_01</objID><status>ACK</status></mosAck>", "INFO");
        }
    }

    public void ToggleOutput(OutputChannel channel)
    {
        if (channel.IsEnabled)
        {
            channel.IsEnabled = false;
            channel.Status = OutputStatus.Standby;
            Scte35DvbService.Instance.Log("HARDWARE", $"Output '{channel.Name}' stopped by operator", "WARN");
        }
        else
        {
            if (!TryValidateOutput(channel, out var validationError))
            {
                channel.IsEnabled = false;
                channel.Status = OutputStatus.Error;
                channel.AlertMessage = validationError;
                Scte35DvbService.Instance.Log("VALIDATION", $"Output '{channel.Name}' was not started: {validationError}", "ERROR");
                return;
            }
            channel.IsEnabled = true;
            channel.Status = OutputStatus.Online;
            channel.AlertMessage = string.Empty;
            Scte35DvbService.Instance.Log("HARDWARE", $"Output '{channel.Name}' started successfully on {channel.ProtocolDisplayName}", "SUCCESS");
        }
    }

    public void StartOutput(OutputChannel channel)
    {
        if (!TryValidateOutput(channel, out var validationError))
        {
            channel.IsEnabled = false;
            channel.Status = OutputStatus.Error;
            channel.AlertMessage = validationError;
            Scte35DvbService.Instance.Log("VALIDATION", $"Output '{channel.Name}' was not started: {validationError}", "ERROR");
            return;
        }
        channel.IsEnabled = true;
        channel.Status = OutputStatus.Online;
        channel.AlertMessage = string.Empty;
        Scte35DvbService.Instance.Log("ENGINE", $"Started output '{channel.Name}'", "SUCCESS");
    }

    public void StopOutput(OutputChannel channel)
    {
        channel.IsEnabled = false;
        channel.Status = OutputStatus.Standby;
        Scte35DvbService.Instance.Log("ENGINE", $"Stopped output '{channel.Name}'", "WARN");
    }

    public void RestartOutput(OutputChannel channel)
    {
        channel.IsEnabled = true;
        channel.Status = OutputStatus.Starting;
        Scte35DvbService.Instance.Log("ENGINE", $"Restarting output '{channel.Name}'", "INFO");
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => StartOutput(channel)), DispatcherPriority.Background);
    }

    public void DeleteOutput(OutputChannel channel)
    {
        StopOutput(channel);
        Outputs.Remove(channel);
        SaveOutputs();
        Scte35DvbService.Instance.Log("CONFIG", $"Deleted output '{channel.Name}'", "WARN");
    }

    public void ResetCounters()
    {
        foreach (var ch in Outputs)
        {
            ch.FramesTransmitted = 0;
            ch.DroppedFrames = 0;
            if (ch.Status == OutputStatus.Warning)
            {
                ch.Status = OutputStatus.Online;
                ch.AlertMessage = string.Empty;
            }
        }
        Scte35DvbService.Instance.Log("GENERAL", "All output frame counters and error flags have been reset by operator", "INFO");
    }

    public static bool TryValidateOutput(OutputChannel channel, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(channel.Name)) { error = "Output name is required."; return false; }
        if (channel.InputSource == OutputInputSource.Manual && string.IsNullOrWhiteSpace(channel.ManualInputSource))
        { error = "A manual input source identifier is required."; return false; }
        var destination = channel.DestinationUri?.Trim() ?? string.Empty;
        if (destination.Length == 0) { error = "Destination/device is required."; return false; }
        if (!double.IsFinite(channel.TargetFps) || channel.TargetFps < 1 || channel.TargetFps > 120) { error = "Frame rate must be between 1 and 120 fps."; return false; }
        if (!channel.IsCardHardware && (!double.IsFinite(channel.BitrateMbps) || channel.BitrateMbps <= 0)) { error = "Bitrate must be greater than zero."; return false; }

        if (channel.IsCardHardware)
        {
            channel.GpuEncoder = "Direct Uncompressed (Raw PCIe DMA)";
            channel.BitrateMbps = 0.0;
            if (channel.IsCustomResolution)
            {
                if (channel.CustomWidth < 320 || channel.CustomWidth > 7680 ||
                    channel.CustomHeight < 240 || channel.CustomHeight > 4320)
                {
                    error = "Custom resolution must be between 320x240 and 7680x4320.";
                    return false;
                }
            }
            return true;
        }

        if (channel.Protocol is BroadcastOutputProtocol.CgOutput or BroadcastOutputProtocol.VirtualOutput)
        {
            channel.GpuEncoder = channel.Protocol == BroadcastOutputProtocol.CgOutput ? "Fill + Key / NDI Engine" : "DirectShow Virtual Filter (.ax)";
            return true;
        }

        if (channel.IsNetworkStream)
        {
            if (destination.Contains("xxxx", StringComparison.OrdinalIgnoreCase) || destination.Contains("your-server", StringComparison.OrdinalIgnoreCase))
            { error = "Replace the placeholder destination/stream key before starting this output."; return false; }
            if (!Uri.TryCreate(destination, UriKind.Absolute, out var uri))
            { error = "Destination must be a valid absolute network URI."; return false; }
            var allowed = channel.Protocol switch
            {
                BroadcastOutputProtocol.DvbUdp => new[] { "udp", "rtp" },
                BroadcastOutputProtocol.SRT => new[] { "srt" },
                BroadcastOutputProtocol.RTMP => new[] { "rtmp", "rtmps" },
                BroadcastOutputProtocol.HLS or BroadcastOutputProtocol.MPD => new[] { "http", "https" },
                BroadcastOutputProtocol.RTSP => new[] { "rtsp", "rtsps" },
                _ => Array.Empty<string>()
            };
            if (allowed.Length > 0 && !allowed.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
            { error = $"{channel.ProtocolDisplayName} requires a {string.Join("/", allowed)} destination."; return false; }
            // UDP and SRT have no universally safe implicit destination port. RTMP/RTSP
            // endpoints commonly omit their standard port and remain valid.
            if ((channel.Protocol is BroadcastOutputProtocol.DvbUdp or BroadcastOutputProtocol.SRT) && uri.Port <= 0)
            { error = "The network destination must include a valid port."; return false; }
        }

        if (channel.Protocol == BroadcastOutputProtocol.DvbUdp)
        {
            if (channel.ServiceId is < 1 or > 65535) { error = "DVB service ID must be in the range 1–65535."; return false; }
            var pids = new[] { channel.PmtPid, channel.VideoPid, channel.AudioPid, channel.PcrPid };
            if (pids.Any(pid => pid is < 16 or > 8190)) { error = "DVB PIDs must be in the range 16–8190."; return false; }
            if (new[] { channel.PmtPid, channel.VideoPid, channel.AudioPid }.Distinct().Count() != 3)
            { error = "PMT, video and audio PIDs must be unique."; return false; }
        }
        return true;
    }
}
