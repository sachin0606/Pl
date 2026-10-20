using BroadcastPlayout.Models;

namespace BroadcastPlayout.Professional;

/// <summary>
/// Coordinates FIX42 professional interfaces around the existing Kashtrix deterministic Program bus.
/// All transmitters are fail-safe gated by OutputArmed/HA state.
/// </summary>
public sealed class ProfessionalBroadcastRuntime : IDisposable
{
    private readonly St2110Runtime _st2110=new();
    private readonly Scte35TsSender _scte35=new();
    private readonly Scte104IpSender _scte104Ip=new();
    private readonly PtpMonitor _ptp=new();
    private readonly NmosSenderServer _nmos=new();
    private readonly HighAvailabilityService _ha=new();
    private readonly Rs422Transport _rs422=new();
    private readonly GpioSerialController _gpio=new();
    private readonly SnmpV2Agent _snmp=new();
    private readonly DolbyIntegration _dolby=new();
    private readonly AncillaryQueue _ancillary=new();
    private ProfessionalBroadcastSettings _settings=new();
    private bool _manualArm=true;
    private uint _scteEventId=1;
    private byte _scte104MessageNumber;
    public event Action<bool,string>? ArmStateChanged;
    public event Action<string?,double>? StandbySyncReceived;
    public bool OutputArmed => _manualArm && _ha.ShouldArmOutputs;
    public string HaStatus=>_ha.Status;
    public string PtpStatus=>_ptp.Status;
    public string NmosStatus=>string.IsNullOrWhiteSpace(_nmos.LastError)?(_settings.EnableNmos?"RUNNING":"OFF"):"ERROR · "+_nmos.LastError;
    public string St2110Status=>string.IsNullOrWhiteSpace(_st2110.LastError)?(_settings.EnableSt2110?"RUNNING":"OFF"):"ERROR · "+_st2110.LastError;
    public string DolbyStatus=>_dolby.Status;
    public AncillaryQueue Ancillary=>_ancillary;

    public void Start(ProfessionalBroadcastSettings settings,string channelId,string channelName,Func<(string? ItemId,double Position)> state)
    {
        Stop();_settings=settings??new();_manualArm=_settings.OutputArmed;_st2110.Enabled=_settings.EnableSt2110;_ptp.Start(_settings);_nmos.Start(_settings,channelName);_ha.TakeoverChanged+=(armed,status)=>ArmStateChanged?.Invoke(OutputArmed,status);_ha.SyncStateReceived+=(itemId,position)=>StandbySyncReceived?.Invoke(itemId,position);_ha.Start(_settings,channelId,state);_snmp.Start(_settings,()=>CapabilityStatus());_dolby.Configure(_settings);
        if(_settings.EnableGpio)_gpio.Open(_settings.GpioComPort);
        if(_settings.EnableRs422 || _settings.EnableVdcp)_rs422.Open(_settings.Rs422ComPort,_settings.Rs422Baud,_settings.EnableVdcp);
        ScteReceiverService.Instance.Start(_settings);
        ArmStateChanged?.Invoke(OutputArmed,_ha.Status);
    }
    public void SetManualArm(bool armed){_manualArm=armed;_settings.OutputArmed=armed;ArmStateChanged?.Invoke(OutputArmed,_ha.Status);}
    public void SubmitVideo(VideoFrameData frame){if(!OutputArmed)return;_st2110.SubmitVideo(frame,_settings);var anc=_ancillary.Drain();if(anc.Length>0)_st2110.SubmitAncillary(anc,_settings,(uint)Math.Max(0,Math.Round(frame.PtsSeconds*90000)));}
    public void SubmitAudio(AudioChunk chunk){if(!OutputArmed)return;_st2110.SubmitAudio(chunk,_settings);}
    public uint SendScte35(PlaylistItem item,long currentPts90k)
    {
        var id=item.ScteEventId!=0?item.ScteEventId:unchecked(_scteEventId++);
        if(OutputArmed)
        {
            _scte35.SendCue(_settings,item,id,currentPts90k);
            _ = _snmp.SendTrapAsync($"SCTE-35 {item.ScteSpliceType} event={id}");
        }
        return id;
    }
    public uint SendScte104(PlaylistItem item)
    {
        var id=item.ScteEventId!=0?item.ScteEventId:unchecked(_scteEventId++);
        if(OutputArmed)
        {
            if(_settings.EnableScte104Anc)
                _ancillary.Enqueue(AncillaryDataBuilder.BuildScte104(_settings,item,id,_scte104MessageNumber++));
            if(_settings.EnableScte104Ip)
                _scte104Ip.SendCue(_settings,item,id);
            _ = _snmp.SendTrapAsync($"SCTE-104 {item.ScteSpliceType} event={id}");
        }
        return id;
    }
    public void SendManualScte(ScteSpliceCue cue)
    {
        if(OutputArmed)
        {
            if(_settings.EnableScte35Ts) _scte35.SendManualCue(_settings, cue);
            if(_settings.EnableScte104Ip) _scte104Ip.SendManualCue(_settings, cue);
            _ = _snmp.SendTrapAsync($"SCTE MANUAL {cue.SpliceType} event={cue.EventId} dur={cue.DurationSeconds}s");
        }
    }
    public void SendCaption608(PlaylistItem item){if(OutputArmed && _settings.EnableCea608)_ancillary.Enqueue(AncillaryDataBuilder.BuildCea608(_settings,item));}
    public void SendCaption708(PlaylistItem item,double fps){if(OutputArmed && _settings.EnableCea708)_ancillary.Enqueue(AncillaryDataBuilder.BuildCea708Cdp(_settings,item,fps));}
    public Task ExecuteGpoAsync(PlaylistItem item,CancellationToken ct=default)
    {
        if(!OutputArmed || !_settings.EnableGpio)return Task.CompletedTask;
        var active=item.GpoActiveHigh;
        return item.GpoAction.ToUpperInvariant() switch
        {
            "SET HIGH" => Task.Run(()=>_gpio.SetOutput(item.GpoOutput,active),ct),
            "SET LOW" => Task.Run(()=>_gpio.SetOutput(item.GpoOutput,!active),ct),
            _ => _gpio.PulseAsync(item.GpoOutput,item.GpoPulseMilliseconds,active,ct)
        };
    }
    public bool SendRs422(PlaylistItem item)
    {
        if(!OutputArmed || !_settings.EnableRs422)return false;
        if(item.Rs422Mode.Equals("ASCII",StringComparison.OrdinalIgnoreCase))return _rs422.WriteAscii(item.Rs422Ascii);
        try{var clean=new string((item.Rs422Hex??"").Where(Uri.IsHexDigit).ToArray());if((clean.Length&1)!=0)return false;return _rs422.Write(Enumerable.Range(0,clean.Length/2).Select(i=>Convert.ToByte(clean.Substring(i*2,2),16)).ToArray());}catch{return false;}
    }
    public bool SendVdcp(PlaylistItem item)=>OutputArmed&&_settings.EnableVdcp&&new VdcpClient(_rs422).Send(item);
    public Task SendSnmpTrapAsync(PlaylistItem item,CancellationToken ct=default)=>OutputArmed&&_settings.EnableSnmp?_snmp.SendTrapAsync(item.SnmpHost,item.SnmpPort,item.SnmpCommunity,item.SnmpTrapOid,item.SnmpVarbindOid,item.SnmpValueType,item.SnmpValue,ct):Task.CompletedTask;
    public string CapabilityStatus()=>$"ARM={(OutputArmed?"ON":"INHIBIT")}; HA={_ha.Status}; 2110={St2110Status}; PTP={PtpStatus}; NMOS={NmosStatus}; Dolby={DolbyStatus}; SCTE35={(_settings.EnableScte35Ts?"ON":"OFF")}; 2022-7={(_settings.EnableSt2022_7?"ON":"OFF")}";
    public ProfessionalBroadcastSettings Settings=>_settings;
    public void Stop(){try{_ptp.Stop();}catch{}try{_nmos.Stop();}catch{}try{_ha.Stop();}catch{}try{_rs422.Close();}catch{}try{_gpio.Close();}catch{}try{_snmp.Stop();}catch{}try{ScteReceiverService.Instance.Stop();}catch{}}
    public void Dispose(){Stop();_st2110.Dispose();_scte35.Dispose();_scte104Ip.Dispose();_ptp.Dispose();_nmos.Dispose();_ha.Dispose();_rs422.Dispose();_gpio.Dispose();_snmp.Dispose();_dolby.Dispose();ScteReceiverService.Instance.Dispose();}
}
