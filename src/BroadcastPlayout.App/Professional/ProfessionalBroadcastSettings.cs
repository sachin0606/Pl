namespace BroadcastPlayout.Models;

/// <summary>
/// FIX42 professional transmission/control settings. New network transmitters, GPIO and HA takeover
/// remain OFF until explicitly enabled; OutputArmed defaults on to preserve existing NDI/DeckLink behavior.
/// </summary>
public sealed class ProfessionalBroadcastSettings
{
    public bool OutputArmed { get; set; } = true;

    // SMPTE ST 2110 / ST 2022-7
    public bool EnableSt2110 { get; set; }
    public bool EnableSt2022_7 { get; set; }
    public string St2110VideoDestinationA { get; set; } = "239.100.20.1";
    public string St2110VideoDestinationB { get; set; } = "239.100.20.2";
    public int St2110VideoPort { get; set; } = 50020;
    public string St2110AudioDestinationA { get; set; } = "239.100.30.1";
    public string St2110AudioDestinationB { get; set; } = "239.100.30.2";
    public int St2110AudioPort { get; set; } = 50030;
    public string St2110AncDestinationA { get; set; } = "239.100.40.1";
    public string St2110AncDestinationB { get; set; } = "239.100.40.2";
    public int St2110AncPort { get; set; } = 50040;
    public int St2110Mtu { get; set; } = 1400;
    public int St2110VideoPayloadType { get; set; } = 96;
    public int St2110AudioPayloadType { get; set; } = 97;
    public int St2110AncPayloadType { get; set; } = 98;

    // PTP monitor / clock source. This package monitors PTP and timestamps RTP from the
    // disciplined local clock; it does not alter the Windows system clock itself.
    public bool EnablePtpMonitor { get; set; }
    public string PtpDomain { get; set; } = "0";
    public int PtpEventPort { get; set; } = 319;
    public int PtpGeneralPort { get; set; } = 320;

    // AMWA NMOS sender control.
    public bool EnableNmos { get; set; }
    public int NmosHttpPort { get; set; } = 3210;
    public string NmosRegistrationUrl { get; set; } = string.Empty;

    // SCTE-35 / SCTE-104 Transmission & Reception
    public bool EnableScte35Ts { get; set; } = true;
    public string Scte35TsDestination { get; set; } = "239.100.35.1";
    public int Scte35TsPort { get; set; } = 50350;
    public int Scte35Pid { get; set; } = 0x1F00;
    public int Scte35PmtPid { get; set; } = 0x1000;
    public int Scte35ProgramNumber { get; set; } = 1;
    public bool EnableScte104Anc { get; set; } = true;
    public int Scte104VancLine { get; set; } = 11;
    public bool EnableScte104Ip { get; set; } = true;
    public string Scte104IpDestination { get; set; } = "127.0.0.1";
    public int Scte104IpPort { get; set; } = 5167;

    // SCTE Receiver & Ad Break Automation
    public bool EnableScte35Receiver { get; set; } = true;
    public int Scte35ReceiverPort { get; set; } = 50350;
    public bool EnableScte104Receiver { get; set; } = true;
    public int Scte104ReceiverPort { get; set; } = 5167;
    public bool ScteAutoAdBreak { get; set; } = true;
    public string ScteOnBreakPlayMode { get; set; } = "Current File in Playlist"; // Current File in Playlist | Black Image | Custom Image | Custom Video
    public string ScteOnBreakMediaFile { get; set; } = string.Empty;
    public double ScteDefaultBreakDuration { get; set; } = 60.0;
    public bool ScteUseSameEventIdForCueOut { get; set; } = true;
    public double ScteScheduleSpliceInSeconds { get; set; } = 4.0;
    public double SctePtsAdjustmentSeconds { get; set; } = 0.0;

    // Frame Comparison / Optical Cue Tone Ad Detection
    public bool EnableFrameComparison { get; set; }
    public bool CompareFramesOnLive { get; set; } = true;
    public bool CompareFramesOnUrl { get; set; } = true;
    public string FrameCompareType { get; set; } = "Recognize Both OUT and IN Frames"; // Recognize Both OUT and IN Frames | Recognize Only OUT Frame
    public string FrameCompareMethod { get; set; } = "CcoeffNormed"; // CcoeffNormed | SqdiffNormed
    public double FrameComparePrecisionOut { get; set; } = 82.0;
    public double FrameComparePrecisionIn { get; set; } = 88.0;
    public double FrameCompareTimeoutOutFoundSeconds { get; set; } = 20.0;
    public bool FrameCompareWaitSecondAfterFirst { get; set; } = true;
    public double FrameCompareEmergencyTimeMinutes { get; set; } = 5.0;
    public bool FrameCompareEmergencyReturn { get; set; } = true;
    public string FrameCompareOutImagePath { get; set; } = string.Empty;
    public string FrameCompareInImagePath { get; set; } = string.Empty;

    // Now / Next Field Selection
    public bool NowPlayingItem { get; set; } = true;
    public bool NowItemDescription { get; set; }
    public bool NowItemGenre { get; set; }
    public bool NowItemYear { get; set; }
    public bool NowItemPoster { get; set; }
    public bool NowItemVideo { get; set; }
    public bool NextItem { get; set; } = true;
    public bool NextItemStartTime { get; set; } = true;
    public bool NextItemDescription { get; set; }
    public bool NextItemGenre { get; set; }
    public bool NextItemYear { get; set; }
    public bool NextItemPoster { get; set; }
    public bool NextItemVideo { get; set; }

    // Weather Field Selection
    public bool WeatherTemperature { get; set; } = true;
    public bool WeatherTempMin { get; set; }
    public bool WeatherTempMax { get; set; }
    public bool WeatherFeelsLike { get; set; }
    public bool WeatherWindSpeed { get; set; }
    public bool WeatherCityName { get; set; }
    public bool WeatherCondition { get; set; } = true;
    public bool WeatherPressure { get; set; }
    public bool WeatherHumidity { get; set; }
    public bool WeatherClouds { get; set; }
    public bool WeatherImage { get; set; }
    public bool WeatherDate { get; set; }
    public bool WeatherDay { get; set; }
    public bool WeatherAddMultipleDays { get; set; } = true;
    public int WeatherMultipleDaysCount { get; set; } = 3;

    // Captions / subtitles.
    public bool EnableCea608 { get; set; } = true;
    public bool EnableCea708 { get; set; } = true;
    public int CaptionVancLine { get; set; } = 9;
    public bool EnableDvbSubtitles { get; set; }
    public int DvbSubtitlePid { get; set; } = 0x1200;

    // GPIO / serial / VDCP.
    public bool EnableGpio { get; set; }
    public string GpioComPort { get; set; } = "COM3";
    public bool EnableRs422 { get; set; }
    public string Rs422ComPort { get; set; } = "COM4";
    public int Rs422Baud { get; set; } = 38400;
    public bool EnableVdcp { get; set; }

    // SNMP v2c monitoring/notifications.
    public bool EnableSnmp { get; set; }
    public int SnmpAgentPort { get; set; } = 1161;
    public string SnmpReadCommunity { get; set; } = "public";
    public string SnmpTrapHost { get; set; } = "127.0.0.1";
    public int SnmpTrapPort { get; set; } = 162;
    public string SnmpTrapCommunity { get; set; } = "public";

    // Dolby integration is intentionally adapter/licence based. Kashtrix never pretends to
    // provide an encoder when a licensed SDK/adapter has not been installed.
    public string DolbyMode { get; set; } = "Passthrough"; // Off | Passthrough | LicensedAdapter
    public string DolbyAdapterPath { get; set; } = string.Empty;

    // 1+1 / N+M high availability.
    public string HaMode { get; set; } = "Standalone"; // Standalone | Primary | Backup | Pool
    public string HaNodeId { get; set; } = Environment.MachineName;
    public int HaPriority { get; set; } = 100;
    public string HaMulticastAddress { get; set; } = "239.255.42.42";
    public int HaHeartbeatPort { get; set; } = 42420;
    public int HaControlPort { get; set; } = 42421;
    public int HaHeartbeatMilliseconds { get; set; } = 500;
    public int HaFailoverMilliseconds { get; set; } = 1800;
    public int HaCoordinatorTimeoutMilliseconds { get; set; } = 2500;
    public bool HaUseCoordinator { get; set; } = true;
    public int HaCapacity { get; set; } = 1;
}
