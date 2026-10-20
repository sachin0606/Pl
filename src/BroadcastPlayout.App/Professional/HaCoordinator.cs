using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using BroadcastPlayout.Models;

namespace BroadcastPlayout.Professional;

public sealed record HaNodeSnapshot(string NodeId,string ChannelId,string ConfiguredMode,string RuntimeRole,int Priority,int Capacity,bool OutputArmed,string CurrentItemId,double PositionSeconds,DateTime SeenUtc);

public sealed class HaCoordinatorService : IDisposable
{
    private readonly object _gate=new();
    private readonly Dictionary<string,(HaHeartbeat Beat,DateTime Seen)> _beats=new();
    private readonly Dictionary<string,(string NodeId,DateTime Until,string Reason)> _forced=new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts; private UdpClient? _rx,_tx; private ProfessionalBroadcastSettings _settings=new();
    public string CoordinatorId{get;private set;}=Environment.MachineName+"-HA";
    public event Action? SnapshotChanged;

    public void Start(ProfessionalBroadcastSettings settings,string? coordinatorId=null)
    {
        Stop();_settings=settings;CoordinatorId=string.IsNullOrWhiteSpace(coordinatorId)?Environment.MachineName+"-HA":coordinatorId.Trim();
        _cts=new CancellationTokenSource();_rx=new UdpClient(AddressFamily.InterNetwork);_rx.Client.SetSocketOption(SocketOptionLevel.Socket,SocketOptionName.ReuseAddress,true);_rx.Client.Bind(new IPEndPoint(IPAddress.Any,settings.HaHeartbeatPort));_rx.JoinMulticastGroup(IPAddress.Parse(settings.HaMulticastAddress));_tx=new UdpClient(AddressFamily.InterNetwork);
        _=Task.Run(()=>Receive(_cts.Token));_ = Task.Run(()=>Coordinate(_cts.Token));
    }

    public IReadOnlyList<HaNodeSnapshot> Snapshot()
    {
        var now=DateTime.UtcNow;lock(_gate)return _beats.Values.Where(x=>(now-x.Seen).TotalMilliseconds<=Math.Max(3000,_settings.HaFailoverMilliseconds*2)).Select(x=>new HaNodeSnapshot(x.Beat.NodeId,x.Beat.ChannelId,x.Beat.ConfiguredMode,x.Beat.Role,x.Beat.Priority,x.Beat.Capacity,x.Beat.OutputArmed,x.Beat.CurrentItemId??string.Empty,x.Beat.PositionSeconds,x.Seen)).OrderBy(x=>x.ChannelId).ThenByDescending(x=>x.Priority).ToArray();
    }

    public void ForceActive(string channelId,string nodeId,TimeSpan duration,string reason)
    {lock(_gate)_forced[channelId]=(nodeId,DateTime.UtcNow+duration,string.IsNullOrWhiteSpace(reason)?"MANUAL FAILOVER":reason.Trim());}
    public void Release(string channelId){lock(_gate)_forced.Remove(channelId);}

    private async Task Receive(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            try{var r=await _rx!.ReceiveAsync(ct);var h=JsonSerializer.Deserialize<HaHeartbeat>(r.Buffer);if(h is null)continue;lock(_gate)_beats[h.ChannelId+"|"+h.NodeId]=(h,DateTime.UtcNow);SnapshotChanged?.Invoke();}
            catch(OperationCanceledException){break;}catch(ObjectDisposedException){break;}catch{}
        }
    }

    private async Task Coordinate(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            try{await BroadcastDirectives(ct);await Task.Delay(Math.Clamp(_settings.HaHeartbeatMilliseconds,150,1500),ct);}catch(OperationCanceledException){break;}catch{await Task.Delay(500,ct);}
        }
    }

    private async Task BroadcastDirectives(CancellationToken ct)
    {
        var now=DateTime.UtcNow;List<HaHeartbeat> alive;Dictionary<string,(string NodeId,DateTime Until,string Reason)> forced;
        lock(_gate)
        {
            foreach(var key in _beats.Where(x=>(now-x.Value.Seen).TotalMilliseconds>Math.Max(3000,_settings.HaFailoverMilliseconds*2)).Select(x=>x.Key).ToArray())_beats.Remove(key);
            foreach(var key in _forced.Where(x=>x.Value.Until<=now).Select(x=>x.Key).ToArray())_forced.Remove(key);
            alive=_beats.Values.Select(x=>x.Beat).ToList();forced=new(_forced,StringComparer.OrdinalIgnoreCase);
        }
        var capacity=alive.GroupBy(x=>x.NodeId,StringComparer.OrdinalIgnoreCase).ToDictionary(g=>g.Key,g=>Math.Max(1,g.Max(x=>x.Capacity)),StringComparer.OrdinalIgnoreCase);
        var assigned=capacity.Keys.ToDictionary(x=>x,_=>0,StringComparer.OrdinalIgnoreCase);
        foreach(var group in alive.GroupBy(x=>x.ChannelId,StringComparer.OrdinalIgnoreCase).OrderBy(x=>x.Key,StringComparer.OrdinalIgnoreCase))
        {
            HaHeartbeat? winner=null;string reason="AUTO N+M";
            if(forced.TryGetValue(group.Key,out var f)){winner=group.FirstOrDefault(x=>x.NodeId.Equals(f.NodeId,StringComparison.OrdinalIgnoreCase));reason=f.Reason;}
            winner ??= group.OrderByDescending(x=>ModeRank(x.ConfiguredMode)).ThenByDescending(x=>x.Priority).ThenBy(x=>x.NodeId,StringComparer.Ordinal).FirstOrDefault(x=>assigned.TryGetValue(x.NodeId,out var used)&&used<capacity[x.NodeId]);
            winner ??= group.OrderByDescending(x=>x.Priority).FirstOrDefault(); if(winner is null)continue;
            assigned[winner.NodeId]=assigned.TryGetValue(winner.NodeId,out var n)?n+1:1;
            var directive=new HaDirective(group.Key,winner.NodeId,CoordinatorId,reason,now.Ticks,now.AddMilliseconds(Math.Max(2000,_settings.HaCoordinatorTimeoutMilliseconds*2)).Ticks);
            var bytes=JsonSerializer.SerializeToUtf8Bytes(directive);await _tx!.SendAsync(bytes,new IPEndPoint(IPAddress.Parse(_settings.HaMulticastAddress),_settings.HaControlPort),ct);
        }
    }
    private static int ModeRank(string mode)=>mode.ToUpperInvariant() switch{"PRIMARY"=>4,"BACKUP"=>3,"POOL"=>2,_=>1};
    public void Stop(){var c=Interlocked.Exchange(ref _cts,null);try{c?.Cancel();}catch{}try{_rx?.Dispose();}catch{}try{_tx?.Dispose();}catch{} _rx=_tx=null;try{c?.Dispose();}catch{}lock(_gate){_beats.Clear();_forced.Clear();}}
    public void Dispose()=>Stop();
}
