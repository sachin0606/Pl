using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using BroadcastPlayout.Services;
using BroadcastPlayout.Models;

namespace Kashtrix.ApiGateway;

public sealed class GatewaySettings
{
    public string BindHost { get; set; } = "127.0.0.1";
    public int HttpPort { get; set; } = 9080;
    public int TcpPort { get; set; } = 9090;
    public int UdpPort { get; set; } = 9091;
    public string ApiKey { get; set; } = "kashtrix-local-test";
}

public sealed class GatewayHost : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(){PropertyNameCaseInsensitive=true,WriteIndented=false};
    private CancellationTokenSource? _cts; private HttpListener? _http; private TcpListener? _tcp; private UdpClient? _udp;
    public GatewaySettings Settings { get; } = LoadSettings();
    public event Action<string>? Log;
    public bool IsRunning => _cts is not null;
    public static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"KashtrixPlayout","api-gateway.json");

    public void Start()
    {
        if(IsRunning)return;_cts=new CancellationTokenSource();
        _http=new HttpListener();_http.Prefixes.Add($"http://{Settings.BindHost}:{Settings.HttpPort}/");_http.Start();
        _tcp=new TcpListener(IPAddress.Parse(Settings.BindHost),Settings.TcpPort);_tcp.Start();
        _udp=new UdpClient(new IPEndPoint(IPAddress.Parse(Settings.BindHost),Settings.UdpPort));
        _=Task.Run(()=>HttpLoop(_cts.Token));_ = Task.Run(()=>TcpLoop(_cts.Token));_ = Task.Run(()=>UdpLoop(_cts.Token));
        Write($"REST/WS http://{Settings.BindHost}:{Settings.HttpPort} · TCP {Settings.TcpPort} · UDP {Settings.UdpPort} · MCP /mcp");
    }

    public void Stop(){var c=Interlocked.Exchange(ref _cts,null);try{c?.Cancel();}catch{}try{_http?.Stop();}catch{}try{_tcp?.Stop();}catch{}try{_udp?.Dispose();}catch{} _http=null;_tcp=null;_udp=null;try{c?.Dispose();}catch{}Write("Gateway stopped.");}

    private async Task HttpLoop(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested&&_http?.IsListening==true)
        {
            try{var context=await _http.GetContextAsync().WaitAsync(ct);_ = Task.Run(()=>HandleHttp(context,ct),ct);}catch(OperationCanceledException){break;}catch(HttpListenerException){break;}catch(Exception ex){Write("HTTP: "+ex.Message);}
        }
    }

    private async Task HandleHttp(HttpListenerContext c,CancellationToken ct)
    {
        try
        {
            var path=c.Request.Url?.AbsolutePath??"/";
            if(path.Equals("/",StringComparison.OrdinalIgnoreCase)){await WriteHtml(c,DashboardHtml());return;}
            if(path.Equals("/api/v1/health",StringComparison.OrdinalIgnoreCase)){await WriteJson(c,new{ok=true,service="Kashtrix.ApiGateway",utc=DateTime.UtcNow,channels=PlatformControlBus.ReadStatuses().Count});return;}
            if(path.Equals("/api/v1/events/ws",StringComparison.OrdinalIgnoreCase)&&c.Request.IsWebSocketRequest){if(!Authorized(c.Request)){c.Response.StatusCode=401;c.Response.Close();return;}var ws=await c.AcceptWebSocketAsync(null);await WebSocketSession(ws.WebSocket,ct);return;}
            if(path.Equals("/mcp",StringComparison.OrdinalIgnoreCase)&&c.Request.HttpMethod=="POST"){if(!Authorized(c.Request)){c.Response.StatusCode=401;c.Response.Close();return;}await HandleMcp(c,ct);return;}
            if(!Authorized(c.Request)){c.Response.StatusCode=401;await WriteJson(c,new{ok=false,code="UNAUTHENTICATED"});return;}
            if(path.Equals("/api/v1/demo/load",StringComparison.OrdinalIgnoreCase)&&c.Request.HttpMethod=="POST")
            {
                var result=new DemoIntegrationService().SeedAndValidate();
                c.Response.StatusCode=result.Ok?200:500;
                await WriteJson(c,new{ok=result.Ok,data=result});
                return;
            }
            if(path.Equals("/api/v1/playout/channels",StringComparison.OrdinalIgnoreCase)){await WriteJson(c,new{ok=true,data=PlatformControlBus.ReadStatuses()});return;}
            if(path.Equals("/api/v1/playout/asrun",StringComparison.OrdinalIgnoreCase)&&c.Request.HttpMethod=="GET"){var channel=c.Request.QueryString["channelId"];var limit=int.TryParse(c.Request.QueryString["limit"],out var n)?Math.Clamp(n,1,10000):500;await WriteJson(c,new{ok=true,data=new AsRunLogService().List(channel,limit)});return;}
            if(path.Equals("/api/v1/channel-controller/channels",StringComparison.OrdinalIgnoreCase)){await WriteJson(c,new{ok=true,data=PlatformControlBus.ReadStatuses()});return;}
            if(path.Equals("/api/v1/channel-controller/commands",StringComparison.OrdinalIgnoreCase)&&c.Request.HttpMethod=="POST"){using var doc=await ReadJson(c.Request,ct);await WriteJson(c,await DispatchMultiChannelCommand(doc,ct));return;}
            if(path.Equals("/api/v1/cg-controller/targets",StringComparison.OrdinalIgnoreCase)){await WriteJson(c,new{ok=true,data=PlatformControlBus.ReadStatuses().Select(x=>new{x.ChannelId,x.ChannelName,x.Engine,x.OutputArmed,x.UpdatedUtc})});return;}
            if(path.Equals("/api/v1/cg/templates",StringComparison.OrdinalIgnoreCase)){var projects=LoadCgProjectsForApi(out var catalogFallback);await WriteJson(c,new{ok=true,source=catalogFallback?"canonical-fallback":"database",data=projects.Select(x=>new{x.Id,x.Name,x.Width,x.Height,x.DurationSeconds,layers=x.Layers.Count})});return;}
            if(path.StartsWith("/api/v1/cg-controller/",StringComparison.OrdinalIgnoreCase)&&c.Request.HttpMethod=="POST"){using var doc=await ReadJson(c.Request,ct);var operation=path["/api/v1/cg-controller/".Length..].Trim('/');await WriteJson(c,HandleCgControllerCommand(operation,doc));return;}
            if(path.Equals("/api/v1/mam/assets",StringComparison.OrdinalIgnoreCase)&&c.Request.HttpMethod=="GET")
            {
                var search=c.Request.QueryString["q"];
                var limit=int.TryParse(c.Request.QueryString["limit"],out var parsedLimit)?Math.Clamp(parsedLimit,1,100000):5000;
                await WriteJson(c,new{ok=true,data=new EnterpriseMamCatalog().List(limit,search)});return;
            }
            var mamSeg=path.Trim('/').Split('/');
            if(mamSeg.Length>=5&&mamSeg[0].Equals("api",StringComparison.OrdinalIgnoreCase)&&mamSeg[1].Equals("v1",StringComparison.OrdinalIgnoreCase)&&mamSeg[2].Equals("mam",StringComparison.OrdinalIgnoreCase)&&mamSeg[3].Equals("assets",StringComparison.OrdinalIgnoreCase)&&Guid.TryParse(Uri.UnescapeDataString(mamSeg[4]),out var mamAssetId))
            {
                var mam=new EnterpriseMamCatalog();var asset=mam.Get(mamAssetId);
                if(asset is null){c.Response.StatusCode=404;await WriteJson(c,new{ok=false,code="NOT_FOUND",message="MAM asset not found."});return;}
                if(mamSeg.Length==5&&c.Request.HttpMethod=="GET"){await WriteJson(c,new{ok=true,data=new{asset,history=mam.History(mamAssetId,100)}});return;}
                if(mamSeg.Length==6&&mamSeg[5].Equals("verify",StringComparison.OrdinalIgnoreCase)&&c.Request.HttpMethod=="POST"){await WriteJson(c,new{ok=true,data=await mam.VerifyIntegrityAsync(mamAssetId,ct)});return;}
                if(mamSeg.Length==6&&mamSeg[5].Equals("workflow",StringComparison.OrdinalIgnoreCase)&&c.Request.HttpMethod=="POST")
                {
                    using var doc=await ReadJson(c.Request,ct);var root=doc.RootElement;
                    if(root.TryGetProperty("approvalState",out var approval)&&approval.ValueKind==JsonValueKind.String)asset.ApprovalState=approval.GetString()??asset.ApprovalState;
                    if(root.TryGetProperty("rightsStartUtc",out var rs))asset.RightsStartUtc=ReadNullableUtc(rs);
                    if(root.TryGetProperty("rightsEndUtc",out var re))asset.RightsEndUtc=ReadNullableUtc(re);
                    if(root.TryGetProperty("title",out var title)&&title.ValueKind==JsonValueKind.String)asset.Title=title.GetString()??asset.Title;
                    mam.UpdateWorkflow(asset,"REST API");await WriteJson(c,new{ok=true,data=mam.Get(mamAssetId)});return;
                }
            }
            if(path.Equals("/api/v1/nrcs/rundowns",StringComparison.OrdinalIgnoreCase)){await WriteJson(c,new{ok=true,data=new NrcsPlatformStore().ListRundowns()});return;}
            if(path.Equals("/api/v1/nrcs/stories",StringComparison.OrdinalIgnoreCase)&&c.Request.HttpMethod=="GET"){var q=c.Request.QueryString["q"];await WriteJson(c,new{ok=true,data=new NrcsPlatformStore().ListStoryBank(q)});return;}
            if(path.Equals("/api/v1/nrcs/assignments",StringComparison.OrdinalIgnoreCase)&&c.Request.HttpMethod=="GET"){await WriteJson(c,new{ok=true,data=new NrcsPlatformStore().ListAssignments()});return;}
            if(path.Equals("/api/v1/ha/nodes",StringComparison.OrdinalIgnoreCase)){await WriteJson(c,new{ok=true,data=HaControlBus.ReadSnapshot()});return;}
            if(path.Equals("/api/v1/ha/failover",StringComparison.OrdinalIgnoreCase)&&c.Request.HttpMethod=="POST"){using var doc=await ReadJson(c.Request,ct);await WriteJson(c,await DispatchHaCommand(doc,true,ct));return;}
            if(path.Equals("/api/v1/ha/release",StringComparison.OrdinalIgnoreCase)&&c.Request.HttpMethod=="POST"){using var doc=await ReadJson(c.Request,ct);await WriteJson(c,await DispatchHaCommand(doc,false,ct));return;}
            var seg=path.Trim('/').Split('/');
            if(seg.Length==6&&seg[0]=="api"&&seg[1]=="v1"&&seg[2]=="nrcs"&&seg[3]=="rundowns"&&seg[5].Equals("live",StringComparison.OrdinalIgnoreCase))
            {
                var rundownId=Uri.UnescapeDataString(seg[4]);var store=new NrcsPlatformStore();
                if(c.Request.HttpMethod=="GET"){await WriteJson(c,new{ok=true,data=store.GetLiveState(rundownId)});return;}
                if(c.Request.HttpMethod=="POST")
                {
                    using var doc=await ReadJson(c.Request,ct);var root=doc.RootElement;var storyId=root.TryGetProperty("storyId",out var si)&&si.ValueKind==JsonValueKind.String?si.GetString()??"":"";var itemId=root.TryGetProperty("rundownItemId",out var ii)&&ii.ValueKind==JsonValueKind.String?ii.GetString()??"":"";var onAir=!root.TryGetProperty("onAir",out var oa)||oa.ValueKind!=JsonValueKind.False;store.SetLiveStory(rundownId,storyId,itemId,onAir);await WriteJson(c,new{ok=true,data=store.GetLiveState(rundownId)});return;
                }
            }
            if(seg.Length==6&&seg[0]=="api"&&seg[1]=="v1"&&seg[2]=="nrcs"&&seg[3]=="rundowns"&&seg[5].Equals("publish",StringComparison.OrdinalIgnoreCase)&&c.Request.HttpMethod=="POST")
            {
                var rundownId=Uri.UnescapeDataString(seg[4]);using var doc=await ReadJson(c.Request,ct);var publishMos=doc.RootElement.ValueKind==JsonValueKind.Object&&doc.RootElement.TryGetProperty("publishMos",out var pm)&&pm.ValueKind==JsonValueKind.True;
                var store=new NrcsPlatformStore();var count=store.PublishToPlayout(rundownId);object? mos=null;if(publishMos){var result=await store.PublishMosAsync(rundownId,ct);mos=new{result.Path,result.Sent,result.Message};}
                await WriteJson(c,new{ok=true,data=new{rundownId,playoutItems=count,mos}});return;
            }
            if(path.StartsWith("/api/v1/prompter/",StringComparison.OrdinalIgnoreCase)&&seg.Length==4)
            {
                var rundownId=Uri.UnescapeDataString(seg[3]);var store=new NrcsPlatformStore();var rundown=store.ListRundowns().FirstOrDefault(x=>x.Id.Equals(rundownId,StringComparison.OrdinalIgnoreCase));
                if(rundown is null){c.Response.StatusCode=404;await WriteJson(c,new{ok=false,code="NOT_FOUND",message="Rundown not found."});return;}
                await WriteJson(c,new{ok=true,data=new{rundown,stories=store.ListStories(rundownId)}});return;
            }
            if(seg.Length>=6&&seg[0]=="api"&&seg[1]=="v1"&&seg[2]=="playout"&&seg[3]=="channels")
            {
                var channel=Uri.UnescapeDataString(seg[4]);var tail=seg[5];
                if(tail.Equals("status",StringComparison.OrdinalIgnoreCase)){var status=PlatformControlBus.ReadStatuses().FirstOrDefault(x=>x.ChannelId.Equals(channel,StringComparison.OrdinalIgnoreCase));if(status is null){c.Response.StatusCode=404;await WriteJson(c,new{ok=false,code="NOT_FOUND"});}else await WriteJson(c,new{ok=true,data=status});return;}
                if(tail.Equals("commands",StringComparison.OrdinalIgnoreCase)&&c.Request.HttpMethod=="POST"){var doc=await ReadJson(c.Request,ct);var response=await DispatchJsonCommand(channel,doc,ct);c.Response.StatusCode=response.Ok?200:422;await WriteJson(c,response);return;}
            }
            c.Response.StatusCode=404;await WriteJson(c,new{ok=false,code="NOT_FOUND",path});
        }
        catch(Exception ex){try{c.Response.StatusCode=500;await WriteJson(c,new{ok=false,code="INTERNAL_ERROR",message=ex.Message});}catch{}Write("HTTP handler: "+ex.Message);}
    }

    private bool Authorized(HttpListenerRequest r)
    {
        var key=r.Headers["X-Kashtrix-Api-Key"];
        if(string.IsNullOrWhiteSpace(key)) key=r.QueryString["apiKey"];
        var auth=r.Headers["Authorization"];
        if(string.IsNullOrWhiteSpace(key)&&auth is not null&&auth.StartsWith("Bearer ",StringComparison.OrdinalIgnoreCase)) key=auth[7..].Trim();
        if(string.Equals(key,Settings.ApiKey,StringComparison.Ordinal)) return true; // legacy local admin key
        if(string.IsNullOrWhiteSpace(key)) return false;
        try
        {
            var security=new SecurityStore();
            if(!security.VerifyApiToken(key,out var principal)||principal is null)return false;
            return SecurityStore.HasScope(principal,RequiredScope(r.Url?.AbsolutePath??"/",r.HttpMethod));
        }
        catch{return false;}
    }

    private static string RequiredScope(string path,string method)
    {
        var write=!string.Equals(method,"GET",StringComparison.OrdinalIgnoreCase);
        if(path.Equals("/mcp",StringComparison.OrdinalIgnoreCase))return "mcp:access";
        if(path.Contains("/cg",StringComparison.OrdinalIgnoreCase))return write?"cg:write":"cg:read";
        if(path.Contains("/mam",StringComparison.OrdinalIgnoreCase))return write?"mam:write":"mam:read";
        if(path.Contains("/nrcs",StringComparison.OrdinalIgnoreCase))return write?"nrcs:write":"nrcs:read";
        if(path.Contains("/ha/",StringComparison.OrdinalIgnoreCase))return write?"ha:write":"ha:read";
        if(path.Contains("prompter",StringComparison.OrdinalIgnoreCase))return write?"prompter:write":"prompter:read";
        return write?"playout:write":"playout:read";
    }

    private bool AuthorizedToken(string? key,string requiredScope)
    {
        if(string.Equals(key,Settings.ApiKey,StringComparison.Ordinal))return true;
        if(string.IsNullOrWhiteSpace(key))return false;
        try{var security=new SecurityStore();return security.VerifyApiToken(key,out var p)&&p is not null&&SecurityStore.HasScope(p,requiredScope);}catch{return false;}
    }

    private async Task WebSocketSession(WebSocket ws,CancellationToken serverCt)
    {
        using(ws){var receive=new byte[65536];using var linked=CancellationTokenSource.CreateLinkedTokenSource(serverCt);var sendTask=Task.Run(async()=>{while(!linked.IsCancellationRequested&&ws.State==WebSocketState.Open){var payload=JsonSerializer.SerializeToUtf8Bytes(new{type="event",name="platform.status",ts=DateTime.UtcNow,data=PlatformControlBus.ReadStatuses()},Json);await ws.SendAsync(payload,WebSocketMessageType.Text,true,linked.Token);await Task.Delay(1000,linked.Token);}},linked.Token);
            try{while(ws.State==WebSocketState.Open&&!linked.IsCancellationRequested){var r=await ws.ReceiveAsync(receive,linked.Token);if(r.MessageType==WebSocketMessageType.Close)break;var text=Encoding.UTF8.GetString(receive,0,r.Count);using var d=JsonDocument.Parse(text);if(d.RootElement.TryGetProperty("type",out var t)&&t.GetString()=="command"){var channel=d.RootElement.TryGetProperty("channelId",out var ch)?ch.GetString()??"":"";var resp=await DispatchJsonCommand(channel,d,linked.Token);var bytes=JsonSerializer.SerializeToUtf8Bytes(resp,Json);await ws.SendAsync(bytes,WebSocketMessageType.Text,true,linked.Token);}}}catch{}finally{linked.Cancel();try{await sendTask;}catch{}try{if(ws.State==WebSocketState.Open)await ws.CloseAsync(WebSocketCloseStatus.NormalClosure,"bye",CancellationToken.None);}catch{}}}
    }

    private async Task TcpLoop(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            try{var client=await _tcp!.AcceptTcpClientAsync(ct);_ = Task.Run(()=>TcpClientSession(client,ct),ct);}catch(OperationCanceledException){break;}catch(ObjectDisposedException){break;}catch(Exception ex){Write("TCP: "+ex.Message);}
        }
    }
    private async Task TcpClientSession(TcpClient client,CancellationToken serverCt)
    {
        using var clientLifetime=client;
        using var stream=client.GetStream();
        using var reader=new StreamReader(stream,Encoding.UTF8,false,8192,true);
        using var writer=new StreamWriter(stream,new UTF8Encoding(false),8192,true){AutoFlush=true};
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(serverCt);
        using var writerGate=new SemaphoreSlim(1,1);
        var authed=false;
        Task? publisher=null;

        async Task SendLine(object value)
        {
            await writerGate.WaitAsync(linked.Token);
            try { await writer.WriteLineAsync(JsonSerializer.Serialize(value,Json)); }
            finally { writerGate.Release(); }
        }

        try
        {
            while(!linked.IsCancellationRequested&&client.Connected)
            {
                string? line;
                try{line=await reader.ReadLineAsync(linked.Token);}catch{break;}
                if(line is null)break;
                try
                {
                    using var d=JsonDocument.Parse(line);var root=d.RootElement;
                    if(!authed)
                    {
                        authed=root.TryGetProperty("type",out var ty)&&ty.GetString()=="auth"&&root.TryGetProperty("apiKey",out var k)&&AuthorizedToken(k.GetString(),"playout:write");
                        await SendLine(new{ok=authed,code=authed?"OK":"UNAUTHENTICATED"});
                        if(!authed)break;
                        continue;
                    }
                    if(root.TryGetProperty("type",out var typeElement)&&typeElement.GetString()=="subscribe")
                    {
                        await SendLine(new{ok=true,code="SUBSCRIBED",topics=root.TryGetProperty("topics",out var topics)?topics.Clone():default});
                        if(publisher is null)
                        {
                            publisher=Task.Run(async()=>
                            {
                                while(!linked.IsCancellationRequested&&client.Connected)
                                {
                                    await SendLine(new{type="event",name="platform.status",ts=DateTime.UtcNow,data=PlatformControlBus.ReadStatuses()});
                                    await Task.Delay(1000,linked.Token);
                                }
                            },linked.Token);
                        }
                        continue;
                    }
                    if(root.TryGetProperty("type",out typeElement)&&typeElement.GetString()=="ping")
                    {
                        await SendLine(new{type="pong",ts=DateTime.UtcNow});
                        continue;
                    }
                    var channel=root.TryGetProperty("channelId",out var ch)?ch.GetString()??"":"";
                    var resp=await DispatchJsonCommand(channel,d,linked.Token);
                    await SendLine(resp);
                }
                catch(Exception ex){await SendLine(new{ok=false,code="INVALID_REQUEST",message=ex.Message});}
            }
        }
        finally
        {
            linked.Cancel();
            if(publisher is not null)try{await publisher;}catch{}
        }
    }

    private async Task UdpLoop(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            try{var r=await _udp!.ReceiveAsync(ct);using var d=JsonDocument.Parse(r.Buffer);var root=d.RootElement;if(!root.TryGetProperty("apiKey",out var key)||!AuthorizedToken(key.GetString(),"playout:write"))continue;var channel=root.TryGetProperty("channelId",out var ch)?ch.GetString()??"":"";var action=root.TryGetProperty("action",out var a)?a.GetString()??"":"";if(action is "take" or "output_arm")continue;var resp=await DispatchJsonCommand(channel,d,ct);if(root.TryGetProperty("ackRequested",out var ack)&&ack.ValueKind==JsonValueKind.True){var b=JsonSerializer.SerializeToUtf8Bytes(resp,Json);await _udp.SendAsync(b,r.RemoteEndPoint,ct);}}catch(OperationCanceledException){break;}catch(ObjectDisposedException){break;}catch(Exception ex){Write("UDP: "+ex.Message);}
        }
    }

    private async Task<PlatformControlResponse> DispatchJsonCommand(string channel,JsonDocument d,CancellationToken ct)
    {
        var root=d.RootElement;var action=root.TryGetProperty("action",out var a)?a.GetString()??"":root.TryGetProperty("params",out var p)&&p.TryGetProperty("action",out var pa)?pa.GetString()??"":"";
        var id=root.TryGetProperty("id",out var i)?i.GetString()??Guid.NewGuid().ToString("N"):root.TryGetProperty("requestId",out var ri)?ri.GetString()??Guid.NewGuid().ToString("N"):Guid.NewGuid().ToString("N");
        var pars=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        if(root.TryGetProperty("params",out var paramsEl)&&paramsEl.ValueKind==JsonValueKind.Object)foreach(var prop in paramsEl.EnumerateObject())pars[prop.Name]=prop.Value.ValueKind==JsonValueKind.String?prop.Value.GetString()??"":prop.Value.ToString();
        foreach(var name in new[]{"itemId","offsetSeconds","armed"})if(root.TryGetProperty(name,out var el))pars[name]=el.ValueKind==JsonValueKind.String?el.GetString()??"":el.ToString();
        if(string.IsNullOrWhiteSpace(channel))return new PlatformControlResponse{Id=id,Ok=false,Code="INVALID_REQUEST",Message="channelId is required."};
        return await PlatformControlBus.SubmitAndWaitAsync(new PlatformControlCommand{Id=id,ChannelId=channel,Action=action,Parameters=pars},TimeSpan.FromSeconds(3),ct);
    }

    private async Task<object> DispatchMultiChannelCommand(JsonDocument document, CancellationToken ct)
    {
        var root = document.RootElement;
        if (!root.TryGetProperty("targets", out var targetsElement) || targetsElement.ValueKind != JsonValueKind.Array)
            return new { ok = false, code = "INVALID_REQUEST", message = "targets array is required." };
        var action = root.TryGetProperty("action", out var actionElement) ? actionElement.GetString() ?? string.Empty : string.Empty;
        if (string.IsNullOrWhiteSpace(action)) return new { ok = false, code = "INVALID_REQUEST", message = "action is required." };
        var parameters = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("params", out var paramsElement) && paramsElement.ValueKind == JsonValueKind.Object)
            foreach (var property in paramsElement.EnumerateObject()) parameters[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? string.Empty : property.Value.ToString();
        var requestId = root.TryGetProperty("requestId", out var req) ? req.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N");
        var channels = targetsElement.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? string.Empty).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var tasks = channels.Select(channel => PlatformControlBus.SubmitAndWaitAsync(new PlatformControlCommand{Id=requestId+"-"+channel,ChannelId=channel,Action=action,Parameters=new Dictionary<string,string>(parameters,StringComparer.OrdinalIgnoreCase)},TimeSpan.FromSeconds(3),ct)).ToArray();
        var results = await Task.WhenAll(tasks);
        return new { ok = results.All(x => x.Ok), accepted = results.Count(x => x.Ok), failed = results.Count(x => !x.Ok), results };
    }

    private async Task<object> DispatchHaCommand(JsonDocument document,bool forceActive,CancellationToken ct)
    {
        var root=document.RootElement;
        var channelId=root.TryGetProperty("channelId",out var ch)?ch.GetString()??string.Empty:string.Empty;
        if(string.IsNullOrWhiteSpace(channelId))return new{ok=false,code="INVALID_REQUEST",message="channelId is required."};
        var nodeId=root.TryGetProperty("targetNode",out var tn)?tn.GetString()??string.Empty:root.TryGetProperty("nodeId",out var ni)?ni.GetString()??string.Empty:string.Empty;
        if(forceActive&&string.IsNullOrWhiteSpace(nodeId))return new{ok=false,code="INVALID_REQUEST",message="targetNode/nodeId is required."};
        var duration=root.TryGetProperty("durationSeconds",out var du)&&du.TryGetInt32(out var sec)?Math.Clamp(sec,5,86400):600;
        var reason=root.TryGetProperty("reason",out var re)?re.GetString()??string.Empty:string.Empty;
        var response=await HaControlBus.SubmitAndWaitAsync(new HaControlCommand{Action=forceActive?"force_active":"release",ChannelId=channelId,NodeId=nodeId,DurationSeconds=duration,Reason=reason},TimeSpan.FromSeconds(3),ct);
        string? targetNode=forceActive?nodeId:null;
        var data=new{channelId,targetNode,durationSeconds=forceActive?duration:0};
        return new{ok=response.Ok,code=response.Code,message=response.Message,data};
    }

    private object HandleCgControllerCommand(string operation, JsonDocument document)
    {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return new { ok = false, code = "INVALID_REQUEST", message = "CG Controller request must be a JSON object." };

        var targets = ReadCgTargets(root);
        if (targets.Count == 0) return new { ok = false, code = "INVALID_REQUEST", message = "At least one CG target is required." };
        var bus = ReadJsonString(root, "bus", "PROGRAM").Trim().ToUpperInvariant();
        if (bus is not "PROGRAM" and not "PREVIEW")
            return new { ok = false, code = "INVALID_REQUEST", message = "bus must be PROGRAM or PREVIEW." };
        var layer = root.TryGetProperty("layer", out var layerEl) && layerEl.TryGetInt32(out var l) ? Math.Clamp(l,0,999) : -1;
        var action = operation.ToLowerInvariant() switch { "play" => "PLAY", "update" => "UPDATE", "take" => "TAKE", "stop" => "STOP", "clear" => "CLEAR", _ => string.Empty };
        if (string.IsNullOrWhiteSpace(action)) return new { ok = false, code = "NOT_FOUND", message = "Unknown CG Controller operation." };

        var failures = new List<string>();
        if (action is "STOP" or "CLEAR" or "TAKE")
        {
            var delivered = 0;
            foreach (var target in targets)
            {
                try
                {
                    var count = LocalCgCommandBus.Publish(new[]{target.ChannelId},new CgRemoteCommand{CommandId=Guid.NewGuid().ToString("N"),Action=action,Bus=bus,Layer=layer});
                    delivered += count;
                    if (count != 1) failures.Add(target.ChannelId + ": command was not queued");
                }
                catch (Exception ex)
                {
                    failures.Add(target.ChannelId + ": " + ex.GetBaseException().Message);
                    Write("CG " + action + " delivery failed for " + target.ChannelId + ": " + ex.GetBaseException().Message);
                }
            }
            return new { ok = delivered == targets.Count, delivered, failed = targets.Count - delivered, action, bus, layer, failures };
        }

        var template = ReadJsonString(root, "template", string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(template)) return new { ok = false, code = "INVALID_REQUEST", message = "template is required." };
        var projects = LoadCgProjectsForApi(out var catalogFallback);
        var source = projects.FirstOrDefault(x=>x.Name.Equals(template,StringComparison.OrdinalIgnoreCase));
        if (source is null) return new { ok = false, code = "NOT_FOUND", message = "CG template/project not found: "+template };

        var deliveredCount = 0;
        foreach (var target in targets)
        {
            try
            {
                var project = CloneCgProject(source);
                project.ExternalLayer = layer;
                ApplyCgData(project,target.DataJson ?? ReadRootDataJson(root));
                var count = LocalCgCommandBus.Publish(new[]{target.ChannelId},new CgRemoteCommand{CommandId=Guid.NewGuid().ToString("N"),Action=action,Bus=bus,Layer=layer,Project=project});
                deliveredCount += count;
                if (count != 1) failures.Add(target.ChannelId + ": command was not queued");
            }
            catch (Exception ex)
            {
                failures.Add(target.ChannelId + ": " + ex.GetBaseException().Message);
                Write("CG " + action + " delivery failed for " + target.ChannelId + ": " + ex.GetBaseException().Message);
            }
        }
        return new { ok = deliveredCount == targets.Count, delivered = deliveredCount, failed = targets.Count - deliveredCount, action, bus, layer, template, source = catalogFallback ? "canonical-fallback" : "database", failures };
    }

    private List<CgProject> LoadCgProjectsForApi(out bool canonicalFallback)
    {
        canonicalFallback = false;
        try
        {
            var projects = new BroadcastDatabase().LoadState("cg-projects",new List<CgProject>());
            if (projects.Count > 0) return projects;
            Write("CG catalog database was empty; serving the canonical in-memory catalog.");
        }
        catch (Exception ex)
        {
            Write("CG catalog database read failed; serving canonical fallback: " + ex.GetBaseException().Message);
        }
        canonicalFallback = true;
        return CgDemoFactory.CreateDefaults();
    }

    private static string ReadJsonString(JsonElement root, string property, string fallback)
        => root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;

    private sealed record CgTarget(string ChannelId, string? DataJson);
    private static List<CgTarget> ReadCgTargets(JsonElement root)
    {
        var result = new List<CgTarget>();
        if (!root.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Array) return result;
        foreach (var entry in targets.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String)
            {
                var id=entry.GetString(); if(!string.IsNullOrWhiteSpace(id)) result.Add(new CgTarget(id.Trim(),null));
            }
            else if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("channelId",out var c) && c.ValueKind == JsonValueKind.String)
            {
                var id=c.GetString(); if(string.IsNullOrWhiteSpace(id)) continue;
                var data=entry.TryGetProperty("data",out var d) ? d.GetRawText() : null;
                result.Add(new CgTarget(id.Trim(),data));
            }
        }
        return result;
    }

    private static string? ReadRootDataJson(JsonElement root) => root.TryGetProperty("data",out var data) ? data.GetRawText() : null;
    private static CgProject CloneCgProject(CgProject source) => JsonSerializer.Deserialize<CgProject>(JsonSerializer.Serialize(source,Json),Json) ?? throw new InvalidOperationException("Unable to clone CG project.");
    private static void ApplyCgData(CgProject project,string? json)
    {
        if(string.IsNullOrWhiteSpace(json))return;
        try
        {
            using var doc=JsonDocument.Parse(json);
            if(doc.RootElement.ValueKind!=JsonValueKind.Object)return;
            foreach(var layer in project.Layers)
            {
                if(!layer.Type.Equals("Text",StringComparison.OrdinalIgnoreCase) && !layer.Type.Equals("Ticker",StringComparison.OrdinalIgnoreCase) && !layer.Type.Equals("Roll",StringComparison.OrdinalIgnoreCase))continue;
                var key=string.IsNullOrWhiteSpace(layer.DataField)?layer.Name:layer.DataField;
                if(TryResolveJson(doc.RootElement,key,out var value))layer.Text=value;
            }
        }
        catch(JsonException) { }
    }
    private static bool TryResolveJson(JsonElement root,string? path,out string value)
    {
        value=string.Empty;if(root.ValueKind!=JsonValueKind.Object||string.IsNullOrWhiteSpace(path))return false;var current=root;
        foreach(var segment in path.Split('.',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries))
        {
            if(current.ValueKind!=JsonValueKind.Object)return false;JsonElement next=default;var found=false;
            foreach(var p in current.EnumerateObject())if(p.Name.Equals(segment,StringComparison.OrdinalIgnoreCase)){next=p.Value;found=true;break;}
            if(!found)return false;current=next;
        }
        value=current.ValueKind==JsonValueKind.String?current.GetString()??string.Empty:current.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False?current.ToString():current.GetRawText();return true;
    }

    private async Task HandleMcp(HttpListenerContext c,CancellationToken ct)
    {
        using var d=await ReadJson(c.Request,ct);
        var root=d.RootElement;
        var id=root.TryGetProperty("id",out var idEl)?idEl.Clone():default;
        var method=root.TryGetProperty("method",out var m)?m.GetString()??string.Empty:string.Empty;
        if(method=="tools/list")
        {
            var result=new{tools=new object[]{
                Tool("playout_get_status","Read live channel status"),
                Tool("playout_command","Execute validated playout command"),
                Tool("channel_controller_list","List connected playout channels"),
                Tool("channel_controller_command","Execute the same validated command on one or more channels"),
                Tool("cg_controller_play","Play an authored CG template on one or more channels"),
                Tool("demo_load_and_test","Load Kashtrix professional demo data and validate CG/MOS/NRCS/Playout/Prompter integration"),
                Tool("nrcs_list_rundowns","List NRCS rundowns"),
                Tool("nrcs_list_stories","List/search independent NRCS Story Bank stories"),
                Tool("nrcs_list_assignments","List newsroom planning assignments"),
                Tool("nrcs_get_live","Read the live story state for a rundown"),
                Tool("nrcs_set_live","Set/clear the live story state for a rundown"),
                Tool("nrcs_publish_rundown","Publish NRCS rundown to playout and optionally MOS/NCS"),
                Tool("prompter_get_rundown","Read a rundown and prompter story body"),
                Tool("ha_get_state","Read HA node/election state"),
                Tool("ha_force_active","Force a channel active on a selected HA node"),
                Tool("ha_release","Return a channel to automatic HA election")
            }};
            await WriteJson(c,new{jsonrpc="2.0",id,result});return;
        }
        if(method=="tools/call"&&root.TryGetProperty("params",out var ps))
        {
            var name=ps.TryGetProperty("name",out var n)?n.GetString()??string.Empty:string.Empty;
            var args=ps.TryGetProperty("arguments",out var ar)&&ar.ValueKind==JsonValueKind.Object?ar:default;
            if(name=="playout_get_status")
            {
                var channel=ArgString(args,"channelId");var st=PlatformControlBus.ReadStatuses().FirstOrDefault(x=>x.ChannelId.Equals(channel,StringComparison.OrdinalIgnoreCase));
                await WriteMcpResult(c,id,st);return;
            }
            if(name=="playout_command")
            {
                var channel=ArgString(args,"channelId");var action=ArgString(args,"action");
                var resp=await PlatformControlBus.SubmitAndWaitAsync(new PlatformControlCommand{ChannelId=channel,Action=action},TimeSpan.FromSeconds(3),ct);await WriteMcpResult(c,id,resp);return;
            }
            if(name=="channel_controller_list"){await WriteMcpResult(c,id,PlatformControlBus.ReadStatuses());return;}
            if(name=="channel_controller_command")
            {
                var targets=ArgStrings(args,"targets");var action=ArgString(args,"action");var tasks=targets.Select(ch=>PlatformControlBus.SubmitAndWaitAsync(new PlatformControlCommand{ChannelId=ch,Action=action},TimeSpan.FromSeconds(3),ct));await WriteMcpResult(c,id,await Task.WhenAll(tasks));return;
            }
            if(name=="cg_controller_play")
            {
                var targets=ArgStrings(args,"targets");var template=ArgString(args,"template");var layer=ArgInt(args,"layer",20);var data=args.ValueKind==JsonValueKind.Object&&args.TryGetProperty("data",out var dataEl)?dataEl.GetRawText():"{}";
                using var fake=JsonDocument.Parse(JsonSerializer.Serialize(new{targets,template,layer,bus="PROGRAM",data=JsonSerializer.Deserialize<object>(data,Json)},Json));await WriteMcpResult(c,id,HandleCgControllerCommand("play",fake));return;
            }
            if(name=="demo_load_and_test"){await WriteMcpResult(c,id,new DemoIntegrationService().SeedAndValidate());return;}
            if(name=="nrcs_list_rundowns"){await WriteMcpResult(c,id,new NrcsPlatformStore().ListRundowns());return;}
            if(name=="nrcs_list_stories"){var q=ArgString(args,"query");await WriteMcpResult(c,id,new NrcsPlatformStore().ListStoryBank(q));return;}
            if(name=="nrcs_list_assignments"){await WriteMcpResult(c,id,new NrcsPlatformStore().ListAssignments());return;}
            if(name=="nrcs_get_live"){var rid=ArgString(args,"rundownId");await WriteMcpResult(c,id,new NrcsPlatformStore().GetLiveState(rid));return;}
            if(name=="nrcs_set_live")
            {
                var rid=ArgString(args,"rundownId");var sid=ArgString(args,"storyId");var item=ArgString(args,"rundownItemId");var onAir=ArgBool(args,"onAir",true);var store=new NrcsPlatformStore();store.SetLiveStory(rid,sid,item,onAir);await WriteMcpResult(c,id,store.GetLiveState(rid));return;
            }
            if(name=="nrcs_publish_rundown")
            {
                var rid=ArgString(args,"rundownId");var publishMos=ArgBool(args,"publishMos",false);var store=new NrcsPlatformStore();var count=store.PublishToPlayout(rid);object? mos=null;if(publishMos){var r=await store.PublishMosAsync(rid,ct);mos=new{r.Path,r.Sent,r.Message};}await WriteMcpResult(c,id,new{rundownId=rid,playoutItems=count,mos});return;
            }
            if(name=="prompter_get_rundown")
            {
                var rid=ArgString(args,"rundownId");var store=new NrcsPlatformStore();await WriteMcpResult(c,id,new{rundown=store.ListRundowns().FirstOrDefault(x=>x.Id==rid),stories=store.ListStories(rid)});return;
            }
            if(name=="ha_get_state"){await WriteMcpResult(c,id,HaControlBus.ReadSnapshot());return;}
            if(name=="ha_force_active"||name=="ha_release")
            {
                var channelId=ArgString(args,"channelId");var nodeId=ArgString(args,"nodeId");var duration=ArgInt(args,"durationSeconds",600);var response=await HaControlBus.SubmitAndWaitAsync(new HaControlCommand{Action=name=="ha_force_active"?"force_active":"release",ChannelId=channelId,NodeId=nodeId,DurationSeconds=duration,Reason="MCP"},TimeSpan.FromSeconds(3),ct);await WriteMcpResult(c,id,response);return;
            }
        }
        await WriteJson(c,new{jsonrpc="2.0",id,error=new{code=-32601,message="Method/tool not found"}});
    }

    private static string ArgString(JsonElement args,string name)=>args.ValueKind==JsonValueKind.Object&&args.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString()??string.Empty:string.Empty;
    private static int ArgInt(JsonElement args,string name,int fallback)=>args.ValueKind==JsonValueKind.Object&&args.TryGetProperty(name,out var value)&&value.TryGetInt32(out var n)?n:fallback;
    private static bool ArgBool(JsonElement args,string name,bool fallback)=>args.ValueKind==JsonValueKind.Object&&args.TryGetProperty(name,out var value)&&value.ValueKind is JsonValueKind.True or JsonValueKind.False?value.GetBoolean():fallback;
    private static string[] ArgStrings(JsonElement args,string name)=>args.ValueKind==JsonValueKind.Object&&args.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.Array?value.EnumerateArray().Where(x=>x.ValueKind==JsonValueKind.String).Select(x=>x.GetString()??string.Empty).Where(x=>!string.IsNullOrWhiteSpace(x)).ToArray():Array.Empty<string>();
    private static Task WriteMcpResult(HttpListenerContext c,JsonElement id,object? value)=>WriteJson(c,new{jsonrpc="2.0",id,result=new{content=new[]{new{type="text",text=JsonSerializer.Serialize(value,Json)}}}});
    private static object Tool(string name,string description)=>new{name,description,inputSchema=new{type="object",additionalProperties=true}};
    private static DateTime? ReadNullableUtc(JsonElement value)
    {
        if(value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)return null;
        if(value.ValueKind!=JsonValueKind.String)return null;
        var text=value.GetString();
        return DateTime.TryParse(text,null,System.Globalization.DateTimeStyles.RoundtripKind,out var parsed)?parsed.ToUniversalTime():null;
    }
    private static async Task<JsonDocument> ReadJson(HttpListenerRequest r,CancellationToken ct)
    {
        // Windows PowerShell 5.1 can send a .NET string body using the process ANSI code page
        // unless the caller explicitly supplies UTF-8 bytes. JsonDocument's stream overload
        // expects UTF-8 JSON and an invalid byte sequence can surface as the otherwise opaque
        // "Cannot transcode invalid UTF-8 JSON text to UTF-16 string" failure. Read the request
        // once, then normalize it to valid UTF-8 JSON. This also keeps third-party MOS/NRCS
        // clients that correctly declare another charset interoperable with the REST gateway.
        using var buffer = new MemoryStream();
        await r.InputStream.CopyToAsync(buffer, ct).ConfigureAwait(false);
        var bytes = buffer.ToArray();
        if (bytes.Length == 0) return JsonDocument.Parse("{}");
        try { return JsonDocument.Parse(bytes); }
        catch (JsonException first)
        {
            Encoding encoding;
            try { encoding = r.ContentEncoding ?? Encoding.UTF8; }
            catch { encoding = Encoding.UTF8; }
            try
            {
                var text = encoding.GetString(bytes);
                return JsonDocument.Parse(text);
            }
            catch
            {
                // Last recovery path: replacement fallback guarantees a valid UTF-16 string,
                // while malformed JSON still reports INVALID_REQUEST instead of crashing HTTP.
                var safeUtf8 = new UTF8Encoding(false, false).GetString(bytes);
                try { return JsonDocument.Parse(safeUtf8); }
                catch { throw new JsonException("Request body is not valid JSON/UTF-8.", first); }
            }
        }
    }
    private static async Task WriteJson(HttpListenerContext c,object value){var b=JsonSerializer.SerializeToUtf8Bytes(value,Json);c.Response.ContentType="application/json; charset=utf-8";c.Response.ContentLength64=b.Length;await c.Response.OutputStream.WriteAsync(b);c.Response.Close();}
    private static async Task WriteHtml(HttpListenerContext c,string value){var b=Encoding.UTF8.GetBytes(value);c.Response.ContentType="text/html; charset=utf-8";c.Response.ContentLength64=b.Length;await c.Response.OutputStream.WriteAsync(b);c.Response.Close();}
    private string DashboardHtml()=>"""<!doctype html><html><head><meta charset='utf-8'><title>Kashtrix MCR</title><style>body{font-family:Segoe UI;background:#070a10;color:#eee;margin:30px}h1{color:#d86ac1}.card{border:1px solid #3e2854;background:#10151d;padding:16px;margin:10px 0;border-radius:8px}code{color:#dcb9ff}</style></head><body><h1>Kashtrix Web MCR / API Gateway</h1><p>REST <code>/api/v1</code> · WebSocket <code>/api/v1/events/ws</code> · MCP <code>/mcp</code></p><div id='x'></div><script>async function tick(){let r=await fetch('/api/v1/health');let h=await r.json();document.getElementById('x').innerHTML='<div class=card>Gateway OK · UTC '+h.utc+' · channels '+h.channels+'</div>';}tick();setInterval(tick,2000);</script></body></html>""";
    private void Write(string text)=>Log?.Invoke($"{DateTime.Now:HH:mm:ss}  {text}");
    private static GatewaySettings LoadSettings(){try{Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);if(File.Exists(SettingsPath))return JsonSerializer.Deserialize<GatewaySettings>(File.ReadAllText(SettingsPath),Json)??new();var s=new GatewaySettings();File.WriteAllText(SettingsPath,JsonSerializer.Serialize(s,new JsonSerializerOptions{WriteIndented=true}));return s;}catch{return new();}}
    public void Dispose()=>Stop();
}
