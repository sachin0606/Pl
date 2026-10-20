using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using BroadcastPlayout.Models;

namespace BroadcastPlayout.Services;

public sealed class ChannelClientService : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _worker;
    private string _host = "127.0.0.1";
    private int _port = 9120;
    private string _channelId = "";
    private string _channelName = "";
    private Func<ChannelStatusSnapshot>? _statusProvider;
    private int _disposed;
    public string Status { get; private set; } = "OFF";
    public event Action<CgRemoteCommand>? CgCommandReceived;
    public event Action<string>? StatusChanged;

    public void Start(string host, int port, string channelId, string channelName, Func<ChannelStatusSnapshot> statusProvider)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _host = host;
        _port = port;
        _channelId = channelId;
        _channelName = channelName;
        _statusProvider = statusProvider;

        if (_worker is null || _worker.IsCompleted)
        {
            // Capture the token exactly once. Reading CancellationTokenSource.Token after
            // the source is disposed throws ObjectDisposedException.
            var token = _lifetime.Token;
            _worker = Task.Run(() => RunAsync(token));
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var retryDelayMs = 1500;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(_host, _port, ct).ConfigureAwait(false);
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, false, 8192, true);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), 8192, true) { AutoFlush = true };

                await writer.WriteLineAsync(JsonSerializer.Serialize(new Wire
                {
                    Type = "hello",
                    Role = "playout",
                    ChannelId = _channelId,
                    ChannelName = _channelName
                })).ConfigureAwait(false);

                retryDelayMs = 1500;
                SetStatus("CONNECTED");

                var sendTask = Task.Run(async () =>
                {
                    while (!ct.IsCancellationRequested && client.Connected)
                    {
                        var st = _statusProvider?.Invoke() ??
                                 new ChannelStatusSnapshot { ChannelId = _channelId, ChannelName = _channelName };
                        st.LastSeenUtc = DateTime.UtcNow;
                        await writer.WriteLineAsync(JsonSerializer.Serialize(new Wire
                        {
                            Type = "status",
                            ChannelId = _channelId,
                            Json = JsonSerializer.Serialize(st)
                        })).ConfigureAwait(false);

                        // Confidence thumbnails need a monitor cadence, not a one-frame-per-
                        // second telemetry cadence. Ten updates/sec is smooth enough for MCR
                        // preview while keeping JSON/base64 traffic bounded.
                        if (!await DelayWithoutCancellationException(100, ct).ConfigureAwait(false)) break;
                    }
                });

                while (!ct.IsCancellationRequested && client.Connected)
                {
                    var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                    if (line is null) break;

                    var msg = JsonSerializer.Deserialize<Wire>(line);
                    if (msg?.Type == "cg" && !string.IsNullOrWhiteSpace(msg.Json))
                    {
                        var cmd = JsonSerializer.Deserialize<CgRemoteCommand>(msg.Json);
                        if (cmd is not null) CgCommandReceived?.Invoke(cmd);
                    }
                }

                try { await sendTask.ConfigureAwait(false); } catch (OperationCanceledException) { } catch (IOException) { }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException)
            {
                // The controller is optional. Do not hammer an offline endpoint every
                // 1.5 seconds; back off while still showing an actionable operator state.
                SetStatus("RECONNECTING");
                if (!await DelayWithoutCancellationException(retryDelayMs, ct).ConfigureAwait(false)) break;
                retryDelayMs = Math.Min(15000, retryDelayMs * 2);
            }
            catch (IOException)
            {
                SetStatus("RECONNECTING");
                if (!await DelayWithoutCancellationException(retryDelayMs, ct).ConfigureAwait(false)) break;
                retryDelayMs = Math.Min(15000, retryDelayMs * 2);
            }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested || Volatile.Read(ref _disposed) != 0)
            {
                break;
            }
            catch
            {
                SetStatus("RECONNECTING");
                if (!await DelayWithoutCancellationException(retryDelayMs, ct).ConfigureAwait(false)) break;
                retryDelayMs = Math.Min(15000, retryDelayMs * 2);
            }
        }

        SetStatus("OFF");
    }

    private static async Task<bool> DelayWithoutCancellationException(int milliseconds, CancellationToken ct)
    {
        var remaining = Math.Max(0, milliseconds);
        while (remaining > 0 && !ct.IsCancellationRequested)
        {
            var slice = Math.Min(250, remaining);
            await Task.Delay(slice).ConfigureAwait(false);
            remaining -= slice;
        }
        return !ct.IsCancellationRequested;
    }

    private void SetStatus(string s)
    {
        if (Volatile.Read(ref _disposed) != 0 && s != "OFF") return;
        if (string.Equals(Status, s, StringComparison.Ordinal)) return;
        Status = s;
        try { StatusChanged?.Invoke(s); } catch { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }
        try { _worker?.Wait(2000); } catch { }
        try { _lifetime.Dispose(); } catch { }
    }
}

public sealed class ChannelControllerServer : IDisposable
{
    private readonly ConcurrentDictionary<string,ClientNode> _clients=new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts; private TcpListener? _listener; private Task? _accept;
    public IReadOnlyCollection<ChannelStatusSnapshot> Channels => _clients.Values.Where(x=>x.Role=="playout").Select(x=>x.Status ?? new ChannelStatusSnapshot{ChannelId=x.ChannelId,ChannelName=x.ChannelName,State="Connected"}).ToArray();
    public event Action? ChannelsChanged;
    public event Action<string>? Log;
    public void Start(int port=9120)
    {
        if(_listener is not null)return; _cts=new CancellationTokenSource(); _listener=new TcpListener(IPAddress.Any,port); _listener.Start(); _accept=Task.Run(()=>AcceptLoop(_cts.Token)); Log?.Invoke($"Controller listening on TCP {port}");
    }
    private async Task AcceptLoop(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            try{var tcp=await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);_=Task.Run(()=>Handle(tcp,ct),ct);}catch(OperationCanceledException){break;}catch(Exception ex){Log?.Invoke(ex.Message);}
        }
    }
    private async Task Handle(TcpClient tcp,CancellationToken ct)
    {
        ClientNode? node=null;
        try
        {
            using(tcp) using(var stream=tcp.GetStream()) using(var reader=new StreamReader(stream,Encoding.UTF8,false,8192,true)) using(var writer=new StreamWriter(stream,new UTF8Encoding(false),8192,true){AutoFlush=true})
            {
                var first=await reader.ReadLineAsync(ct).ConfigureAwait(false); if(first is null)return; var hello=JsonSerializer.Deserialize<Wire>(first); if(hello?.Type!="hello")return;
                var id=hello.Role=="playout" ? (hello.ChannelId??Guid.NewGuid().ToString("N")) : "OP-"+Guid.NewGuid().ToString("N")[..8];
                node=new ClientNode(id,hello.ChannelName??hello.Role??"Client",hello.Role??"operator",tcp,writer); _clients[id]=node; ChannelsChanged?.Invoke(); Log?.Invoke($"{node.Role} connected: {node.ChannelId}");
                while(!ct.IsCancellationRequested && tcp.Connected)
                {
                    var line=await reader.ReadLineAsync(ct).ConfigureAwait(false); if(line is null)break; var msg=JsonSerializer.Deserialize<Wire>(line); if(msg is null)continue;
                    if(msg.Type=="status" && !string.IsNullOrWhiteSpace(msg.Json)) { node.Status=JsonSerializer.Deserialize<ChannelStatusSnapshot>(msg.Json); ChannelsChanged?.Invoke(); }
                    else if(msg.Type=="query.channels") { var list=Channels.ToArray(); await writer.WriteLineAsync(JsonSerializer.Serialize(new Wire{Type="channels",Json=JsonSerializer.Serialize(list)})).ConfigureAwait(false); }
                    else if(msg.Type=="route.cg" && !string.IsNullOrWhiteSpace(msg.Json)) { var route=JsonSerializer.Deserialize<RouteCg>(msg.Json); if(route is not null) await RouteCgAsync(route,ct).ConfigureAwait(false); }
                }
            }
        }
        catch(OperationCanceledException){}catch(Exception ex){Log?.Invoke(ex.Message);}finally{if(node is not null){_clients.TryRemove(node.ChannelId,out _);ChannelsChanged?.Invoke();Log?.Invoke($"Disconnected: {node.ChannelId}");}}
    }
    public async Task RouteCgAsync(IEnumerable<string> targets,CgRemoteCommand command,CancellationToken ct=default)=>await RouteCgAsync(new RouteCg{Targets=targets.ToArray(),Command=command},ct).ConfigureAwait(false);
    private async Task RouteCgAsync(RouteCg route,CancellationToken ct)
    {
        var payload=JsonSerializer.Serialize(new Wire{Type="cg",Json=JsonSerializer.Serialize(route.Command)});
        foreach(var id in route.Targets.Distinct(StringComparer.OrdinalIgnoreCase)) if(_clients.TryGetValue(id,out var node) && node.Role=="playout")
        {
            var entered=false;
            try
            {
                await node.SendLock.WaitAsync(ct).ConfigureAwait(false); entered=true;
                await node.Writer.WriteLineAsync(payload).ConfigureAwait(false);
                Log?.Invoke($"CG {route.Command.Action} -> {id}");
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested) { }
            catch(Exception ex) { Log?.Invoke($"CG route to {id} failed: {ex.Message}"); }
            finally { if(entered) node.SendLock.Release(); }
        }
    }
    public void Dispose(){_cts?.Cancel();try{_listener?.Stop();}catch{}foreach(var c in _clients.Values)try{c.Tcp.Dispose();}catch{} _clients.Clear();_cts?.Dispose();}
    private sealed class ClientNode(string id,string name,string role,TcpClient tcp,StreamWriter writer){public string ChannelId=id;public string ChannelName=name;public string Role=role;public TcpClient Tcp=tcp;public StreamWriter Writer=writer;public ChannelStatusSnapshot? Status;public SemaphoreSlim SendLock=new(1,1);}
}

public sealed class ChannelControllerOperator : IDisposable
{
    private TcpClient? _client; private StreamWriter? _writer; private StreamReader? _reader; private readonly SemaphoreSlim _io=new(1,1);
    public async Task ConnectAsync(string host,int port,CancellationToken ct=default){_client=new TcpClient();await _client.ConnectAsync(host,port,ct);var st=_client.GetStream();_reader=new StreamReader(st,Encoding.UTF8,false,8192,true);_writer=new StreamWriter(st,new UTF8Encoding(false),8192,true){AutoFlush=true};await _writer.WriteLineAsync(JsonSerializer.Serialize(new Wire{Type="hello",Role="operator",ChannelName="CG Editor"}));}
    public async Task<IReadOnlyList<ChannelStatusSnapshot>> GetChannelsAsync(CancellationToken ct=default){if(_writer is null||_reader is null)throw new InvalidOperationException("Channel Controller is not connected.");await _io.WaitAsync(ct);try{await _writer.WriteLineAsync(JsonSerializer.Serialize(new Wire{Type="query.channels"}));var line=await _reader.ReadLineAsync(ct);var msg=line is null?null:JsonSerializer.Deserialize<Wire>(line);return msg?.Type=="channels"&&!string.IsNullOrWhiteSpace(msg.Json)?JsonSerializer.Deserialize<List<ChannelStatusSnapshot>>(msg.Json)??[]:[];}finally{_io.Release();}}
    public async Task SendCgAsync(IEnumerable<string> targets,CgRemoteCommand command,CancellationToken ct=default){if(_writer is null)throw new InvalidOperationException("Channel Controller is not connected.");await _io.WaitAsync(ct);try{var route=new RouteCg{Targets=targets.ToArray(),Command=command};await _writer.WriteLineAsync(JsonSerializer.Serialize(new Wire{Type="route.cg",Json=JsonSerializer.Serialize(route)}));}finally{_io.Release();}}
    public void Dispose(){try{_client?.Dispose();}catch{} _io.Dispose();}
}

internal sealed class Wire{public string Type{get;set;}="";public string? Role{get;set;}public string? ChannelId{get;set;}public string? ChannelName{get;set;}public string? Json{get;set;}}
internal sealed class RouteCg{public string[] Targets{get;set;}=[];public CgRemoteCommand Command{get;set;}=new();}
