using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using BroadcastPlayout.Models;

namespace BroadcastPlayout.Professional;

public sealed class PtpMonitor : IDisposable
{
    private CancellationTokenSource? _cts; private UdpClient? _event,_general; private readonly object _gate=new();
    private DateTime _lastUtc; private double _offsetUs; private ushort _lastSequence; private string _lastMessage="OFF";
    public bool IsLocked { get { lock(_gate) return (DateTime.UtcNow-_lastUtc).TotalSeconds<3; } }
    public double OffsetMicroseconds { get { lock(_gate)return _offsetUs; } }
    public string Status { get { lock(_gate)return IsLocked?$"LOCK · {_offsetUs:0.0} us · seq {_lastSequence}":_lastMessage; } }

    public void Start(ProfessionalBroadcastSettings s)
    {
        Stop(); if(!s.EnablePtpMonitor)return; _cts=new CancellationTokenSource();
        try{_event=Bind(s.PtpEventPort);_general=Bind(s.PtpGeneralPort);_ = Task.Run(()=>Loop(_event,_cts.Token));_ = Task.Run(()=>Loop(_general,_cts.Token));lock(_gate)_lastMessage="LISTENING";}
        catch(Exception ex){lock(_gate)_lastMessage="ERROR · "+ex.Message;Stop();}
    }
    private static UdpClient Bind(int port){var u=new UdpClient(AddressFamily.InterNetwork);u.Client.SetSocketOption(SocketOptionLevel.Socket,SocketOptionName.ReuseAddress,true);u.Client.Bind(new IPEndPoint(IPAddress.Any,port));return u;}
    private async Task Loop(UdpClient u,CancellationToken ct){while(!ct.IsCancellationRequested){try{var r=await u.ReceiveAsync(ct).ConfigureAwait(false);Parse(r.Buffer,DateTime.UtcNow);}catch(OperationCanceledException){break;}catch(ObjectDisposedException){break;}catch{await Task.Delay(100,ct).ConfigureAwait(false);}}}
    private void Parse(byte[] b,DateTime receiveUtc)
    {
        if(b.Length<34)return; var type=b[0]&0x0F; var seq=(ushort)((b[30]<<8)|b[31]);
        // Sync (0) or Follow_Up (8): use preciseOriginTimestamp where available for an operator-visible offset estimate.
        if((type==0||type==8)&&b.Length>=44){var seconds=((long)b[34]<<40)|((long)b[35]<<32)|((long)b[36]<<24)|((long)b[37]<<16)|((long)b[38]<<8)|b[39];var nanos=(uint)((b[40]<<24)|(b[41]<<16)|(b[42]<<8)|b[43]);try{var epoch=DateTime.UnixEpoch.AddSeconds(seconds).AddTicks(nanos/100);var off=(receiveUtc-epoch).TotalMilliseconds*1000;lock(_gate){_offsetUs=off;_lastUtc=DateTime.UtcNow;_lastSequence=seq;_lastMessage="PTP";}}catch{}}
    }
    public void Stop(){var c=Interlocked.Exchange(ref _cts,null);try{c?.Cancel();}catch{}try{_event?.Dispose();}catch{}try{_general?.Dispose();}catch{} _event=_general=null;try{c?.Dispose();}catch{}}
    public void Dispose()=>Stop();
}

public sealed class NmosSenderServer : IDisposable
{
    private readonly HttpListener _listener=new(); private CancellationTokenSource? _cts; private readonly Guid _nodeId=Guid.NewGuid(),_deviceId=Guid.NewGuid(),_sourceId=Guid.NewGuid(),_flowId=Guid.NewGuid(),_senderId=Guid.NewGuid();
    private ProfessionalBroadcastSettings? _settings; public string LastError{get;private set;}=string.Empty; public event Action<string,int>? DestinationChanged;
    public string SenderId=>_senderId.ToString();
    public void Start(ProfessionalBroadcastSettings s,string channelName)
    {
        Stop();if(!s.EnableNmos)return;_settings=s;try{_listener.Prefixes.Clear();_listener.Prefixes.Add($"http://+:{Math.Clamp(s.NmosHttpPort,1024,65535)}/");_listener.Start();_cts=new CancellationTokenSource();_ = Task.Run(()=>Loop(channelName,_cts.Token));LastError=string.Empty;}catch(Exception ex){LastError=ex.Message;}
    }
    private async Task Loop(string channel,CancellationToken ct){while(!ct.IsCancellationRequested&&_listener.IsListening){try{var c=await _listener.GetContextAsync().WaitAsync(ct).ConfigureAwait(false);_ = Task.Run(()=>Handle(c,channel),ct);}catch(OperationCanceledException){break;}catch(HttpListenerException){break;}catch(Exception ex){LastError=ex.Message;}}}
    private async Task Handle(HttpListenerContext c,string channel)
    {
        try{var path=c.Request.Url?.AbsolutePath.TrimEnd('/')??"";object body;
            if(path.EndsWith("/x-nmos/node/v1.3/self"))body=new{id=_nodeId,label="Kashtrix Playout",href=$"http://{Environment.MachineName}:{_settings!.NmosHttpPort}",hostname=Environment.MachineName,api=new{versions=new[]{"v1.3"}},caps=new{},services=Array.Empty<object>()};
            else if(path.EndsWith("/x-nmos/node/v1.3/devices"))body=new[]{new{id=_deviceId,label=channel,type="urn:x-nmos:device:generic",node_id=_nodeId,senders=new[]{_senderId},receivers=Array.Empty<Guid>()}};
            else if(path.EndsWith("/x-nmos/node/v1.3/sources"))body=new[]{new{id=_sourceId,label=channel+" Program",format="urn:x-nmos:format:video",device_id=_deviceId,parents=Array.Empty<Guid>(),clock_name="clk0"}};
            else if(path.EndsWith("/x-nmos/node/v1.3/flows"))body=new[]{new{id=_flowId,label=channel+" 2110-20",source_id=_sourceId,device_id=_deviceId,format="urn:x-nmos:format:video",media_type="video/raw",frame_width=1920,frame_height=1080}};
            else if(path.EndsWith("/x-nmos/node/v1.3/senders"))body=new[]{new{id=_senderId,label=channel+" Sender",flow_id=_flowId,device_id=_deviceId,transport="urn:x-nmos:transport:rtp",manifest_href=$"http://{Environment.MachineName}:{_settings!.NmosHttpPort}/kashtrix/2110.sdp",interface_bindings=new[]{"default"}}};
            else if(path.EndsWith("/kashtrix/2110.sdp")){var text=$"v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns={channel}\r\nc=IN IP4 {_settings!.St2110VideoDestinationA}/32\r\nt=0 0\r\nm=video {_settings.St2110VideoPort} RTP/AVP {_settings.St2110VideoPayloadType}\r\na=rtpmap:{_settings.St2110VideoPayloadType} raw/90000\r\n";var bytes=Encoding.UTF8.GetBytes(text);c.Response.ContentType="application/sdp";await c.Response.OutputStream.WriteAsync(bytes);c.Response.Close();return;}
            else if(path.Contains("/x-nmos/connection/v1.1/single/senders/")&&path.EndsWith("/active"))body=ConnectionState();
            else if(path.Contains("/x-nmos/connection/v1.1/single/senders/")&&path.EndsWith("/staged")){
                if(c.Request.HttpMethod.Equals("PATCH",StringComparison.OrdinalIgnoreCase)){using var sr=new StreamReader(c.Request.InputStream,c.Request.ContentEncoding);var json=await sr.ReadToEndAsync();TryApplyPatch(json);}body=ConnectionState();}
            else if(path.Contains("/x-nmos/connection/v1.1/single/senders/")&&path.EndsWith("/constraints"))body=new[]{new{destination_ip=new{enum_values=new[]{_settings!.St2110VideoDestinationA}},destination_port=new{minimum=1,maximum=65535}}};
            else{c.Response.StatusCode=404;body=new{error="not found"};}
            await WriteJson(c,body);
        }catch(Exception ex){LastError=ex.Message;try{c.Response.StatusCode=500;c.Response.Close();}catch{}}
    }
    private object ConnectionState()=>new{master_enable=true,activation=new{mode="activate_immediate",requested_time=(string?)null,activation_time=(string?)null},transport_params=new[]{new{destination_ip=_settings!.St2110VideoDestinationA,destination_port=_settings.St2110VideoPort,rtp_enabled=true}}};
    private void TryApplyPatch(string json){try{using var d=JsonDocument.Parse(json);var t=d.RootElement.GetProperty("transport_params")[0];if(t.TryGetProperty("destination_ip",out var ip)&&t.TryGetProperty("destination_port",out var p)){_settings!.St2110VideoDestinationA=ip.GetString()??_settings.St2110VideoDestinationA;_settings.St2110VideoPort=p.GetInt32();DestinationChanged?.Invoke(_settings.St2110VideoDestinationA,_settings.St2110VideoPort);}}catch(Exception ex){LastError="NMOS PATCH · "+ex.Message;}}
    private static async Task WriteJson(HttpListenerContext c,object value){var b=JsonSerializer.SerializeToUtf8Bytes(value);c.Response.ContentType="application/json";c.Response.ContentLength64=b.Length;await c.Response.OutputStream.WriteAsync(b);c.Response.Close();}
    public void Stop(){var c=Interlocked.Exchange(ref _cts,null);try{c?.Cancel();}catch{}try{_listener.Stop();}catch{}try{c?.Dispose();}catch{}}
    public void Dispose(){Stop();_listener.Close();}
}

public sealed record HaHeartbeat(string NodeId,string ChannelId,int Priority,int Capacity,string ConfiguredMode,string Role,bool OutputArmed,string? CurrentItemId,double PositionSeconds,long UtcTicks);
public sealed record HaDirective(string ChannelId,string TargetNodeId,string CoordinatorId,string Reason,long IssuedUtcTicks,long ExpiresUtcTicks);

public sealed class HighAvailabilityService : IDisposable
{
    private CancellationTokenSource? _cts;
    private UdpClient? _tx,_rx,_control;
    private readonly Dictionary<string,(HaHeartbeat Beat,DateTime Seen)> _peers=new();
    private readonly object _gate=new();
    private ProfessionalBroadcastSettings? _s;
    private string _channel="";
    private Func<(string? ItemId,double Position)>? _state;
    private DateTime _lastDirectiveUtc;
    private string _directiveTarget=string.Empty;
    private string _directiveReason=string.Empty;
    public bool ShouldArmOutputs{get;private set;}=true;
    public string Role{get;private set;}="Standalone";
    public string Status{get;private set;}="OFF";
    public event Action<bool,string>? TakeoverChanged;
    public event Action<string?,double>? SyncStateReceived;

    public void Start(ProfessionalBroadcastSettings s,string channelId,Func<(string? ItemId,double Position)> state)
    {
        Stop(); _s=s; _channel=channelId; _state=state;
        if(s.HaMode.Equals("Standalone",StringComparison.OrdinalIgnoreCase))
        { ShouldArmOutputs=s.OutputArmed;Role="Standalone";Status="STANDALONE";return; }
        try
        {
            _cts=new CancellationTokenSource();
            _tx=new UdpClient(AddressFamily.InterNetwork);
            _rx=BindMulticast(s.HaHeartbeatPort,s.HaMulticastAddress);
            _control=BindMulticast(s.HaControlPort,s.HaMulticastAddress);
            _=Task.Run(()=>Receive(_cts.Token));
            _=Task.Run(()=>ReceiveDirectives(_cts.Token));
            _=Task.Run(()=>Transmit(_cts.Token));
            Evaluate();
        }
        catch(Exception ex){Status="ERROR · "+ex.Message;}
    }

    private static UdpClient BindMulticast(int port,string address)
    {
        var u=new UdpClient(AddressFamily.InterNetwork);
        u.Client.SetSocketOption(SocketOptionLevel.Socket,SocketOptionName.ReuseAddress,true);
        u.Client.Bind(new IPEndPoint(IPAddress.Any,port));
        u.JoinMulticastGroup(IPAddress.Parse(address));
        return u;
    }

    private async Task Transmit(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            try
            {
                Evaluate(); var st=_state?.Invoke()??default;
                var beat=new HaHeartbeat(_s!.HaNodeId,_channel,_s.HaPriority,Math.Max(1,_s.HaCapacity),_s.HaMode,Role,ShouldArmOutputs,st.ItemId,st.Position,DateTime.UtcNow.Ticks);
                var b=JsonSerializer.SerializeToUtf8Bytes(beat);
                await _tx!.SendAsync(b,new IPEndPoint(IPAddress.Parse(_s.HaMulticastAddress),_s.HaHeartbeatPort),ct);
                await Task.Delay(Math.Clamp(_s.HaHeartbeatMilliseconds,100,5000),ct);
            }
            catch(OperationCanceledException){break;}catch{await Task.Delay(500,ct);}
        }
    }

    private async Task Receive(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            try
            {
                var r=await _rx!.ReceiveAsync(ct); var h=JsonSerializer.Deserialize<HaHeartbeat>(r.Buffer);
                if(h is null||h.ChannelId!=_channel||h.NodeId==_s!.HaNodeId)continue;
                lock(_gate)_peers[h.NodeId]=(h,DateTime.UtcNow);
                if(h.OutputArmed || h.Role.Equals("ACTIVE",StringComparison.OrdinalIgnoreCase)) SyncStateReceived?.Invoke(h.CurrentItemId,h.PositionSeconds);
                Evaluate();
            }
            catch(OperationCanceledException){break;}catch(ObjectDisposedException){break;}catch{}
        }
    }

    private async Task ReceiveDirectives(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            try
            {
                var r=await _control!.ReceiveAsync(ct); var d=JsonSerializer.Deserialize<HaDirective>(r.Buffer);
                if(d is null||d.ChannelId!=_channel||d.ExpiresUtcTicks<DateTime.UtcNow.Ticks)continue;
                lock(_gate){_directiveTarget=d.TargetNodeId;_directiveReason=d.Reason;_lastDirectiveUtc=DateTime.UtcNow;}
                Evaluate();
            }
            catch(OperationCanceledException){break;}catch(ObjectDisposedException){break;}catch{}
        }
    }

    private void Evaluate()
    {
        if(_s is null)return;
        var now=DateTime.UtcNow;
        List<HaHeartbeat> alive;
        string target,reason; DateTime directiveSeen;
        lock(_gate)
        {
            foreach(var k in _peers.Where(x=>(now-x.Value.Seen).TotalMilliseconds>_s.HaFailoverMilliseconds).Select(x=>x.Key).ToArray())_peers.Remove(k);
            alive=_peers.Values.Select(x=>x.Beat).ToList(); target=_directiveTarget;reason=_directiveReason;directiveSeen=_lastDirectiveUtc;
        }
        alive.Add(new HaHeartbeat(_s.HaNodeId,_channel,_s.HaPriority,Math.Max(1,_s.HaCapacity),_s.HaMode,Role,false,null,0,now.Ticks));

        bool arm; string elected; string source;
        if(_s.HaUseCoordinator && !string.IsNullOrWhiteSpace(target) && (now-directiveSeen).TotalMilliseconds <= Math.Max(_s.HaCoordinatorTimeoutMilliseconds,_s.HaHeartbeatMilliseconds*3))
        { elected=target;arm=target.Equals(_s.HaNodeId,StringComparison.OrdinalIgnoreCase);source="COORDINATOR"+(string.IsNullOrWhiteSpace(reason)?"":" · "+reason); }
        else
        {
            var winner=alive.OrderByDescending(x=>ModeRank(x.ConfiguredMode)).ThenByDescending(x=>x.Priority).ThenBy(x=>x.NodeId,StringComparer.Ordinal).First();
            elected=winner.NodeId;arm=elected.Equals(_s.HaNodeId,StringComparison.OrdinalIgnoreCase);source="LOCAL ELECTION";
        }
        var role=arm?"ACTIVE":"STANDBY";var changed=arm!=ShouldArmOutputs||role!=Role;
        ShouldArmOutputs=arm;Role=role;Status=$"{role} · elected {elected} · peers {Math.Max(0,alive.Count-1)} · {source}";
        if(changed)TakeoverChanged?.Invoke(arm,Status);
    }

    private static int ModeRank(string mode)=>mode.ToUpperInvariant() switch{"PRIMARY"=>4,"BACKUP"=>3,"POOL"=>2,_=>1};
    public void Stop(){var c=Interlocked.Exchange(ref _cts,null);try{c?.Cancel();}catch{}try{_rx?.Dispose();}catch{}try{_tx?.Dispose();}catch{}try{_control?.Dispose();}catch{} _rx=_tx=_control=null;try{c?.Dispose();}catch{}lock(_gate){_peers.Clear();_directiveTarget=string.Empty;_lastDirectiveUtc=default;}}
    public void Dispose()=>Stop();
}
