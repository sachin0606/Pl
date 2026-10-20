using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace BroadcastPlayout.Services;

/// <summary>
/// Broadcast automation gateway for Kashtrix Playout.
/// - A line-oriented TCP control listener for simple automation/remote-control integrations.
/// - A legacy socket MOS listener on the MOS upper/running-order port (default 10541).
/// - MOS outbound/test traffic to the NCS lower/media-object port (default 10540).
///
/// MOS is intentionally implemented as a conservative interoperable core. Vendor-specific
/// profiles/extensions can be layered on top without coupling them to the playout engine.
/// </summary>
public sealed class AutomationGatewayService : IDisposable
{
    private CancellationTokenSource? _cts;
    private TcpListener? _commandListener;
    private TcpListener? _mosListener;
    private Task? _commandTask;
    private Task? _mosTask;
    private int _disposed;

    public AutomationGatewayProfile Profile { get; private set; } = AutomationGatewayProfileStore.Load();
    public int CommandPort { get; private set; } = 9101;
    /// <summary>MOS upper/running-order listener port. Legacy default: 10541.</summary>
    public int MosPort { get; private set; } = 10541;
    public int MosLowerPort => Profile.MosLowerPort;
    public int MosTopPort => MosPort + 1;
    public bool IsRunning => _cts is { IsCancellationRequested: false };
    public event Action<string>? CommandReceived;
    public event Action<MosGatewayMessage>? MosMessageReceived;
    public event Action<string>? Log;

    public void Start(int commandPort = 9101, int mosPort = 10541)
    {
        var p = Profile with { CommandPort = commandPort, MosUpperPort = mosPort };
        Start(p);
    }

    public void Start(AutomationGatewayProfile profile)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        Stop();
        Profile = profile.Normalize();
        AutomationGatewayProfileStore.Save(Profile);
        CommandPort = Profile.CommandPort;
        MosPort = Profile.MosUpperPort;

        _cts = new CancellationTokenSource();
        _commandListener = new TcpListener(IPAddress.Any, CommandPort);
        _mosListener = new TcpListener(IPAddress.Any, MosPort);
        _commandListener.Start();
        _mosListener.Start();
        var token = _cts.Token;
        _commandTask = Task.Run(() => AcceptCommandsAsync(token));
        _mosTask = Task.Run(() => AcceptMosAsync(token));
        Trace($"TCP control listening on {CommandPort}");
        Trace($"MOS upper/running-order listener on {MosPort} · lower/NCS target {Profile.NcsHost}:{Profile.MosLowerPort}");
        Trace($"MOS IDs · MOS={Profile.MosId} · NCS={Profile.NcsId}");
    }

    public void SaveProfile(AutomationGatewayProfile profile)
    {
        Profile = profile.Normalize();
        AutomationGatewayProfileStore.Save(Profile);
    }

    private void Trace(string message)
    {
        try { RuntimeDiagnosticsService.Write("AUTOMATION", message); } catch { }
        Log?.Invoke(message);
    }

    public void Stop()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        try { cts?.Cancel(); } catch { }
        try { _commandListener?.Stop(); } catch { }
        try { _mosListener?.Stop(); } catch { }
        _commandListener = null;
        _mosListener = null;
        try { cts?.Dispose(); } catch { }
        Trace("Automation gateway stopped");
    }

    private async Task AcceptCommandsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _commandListener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = Task.Run(() => HandleCommandClientAsync(client, ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { Trace("TCP: " + ex.Message); }
        }
    }

    private async Task HandleCommandClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        using (var stream = client.GetStream())
        using (var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, true))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true) { AutoFlush = true })
        {
            var remote = client.Client.RemoteEndPoint?.ToString() ?? "remote";
            Trace("TCP CONNECT · " + remote);
            await writer.WriteLineAsync("KASHTRIX READY").ConfigureAwait(false);
            while (!ct.IsCancellationRequested && client.Connected)
            {
                string? line;
                try { line = await reader.ReadLineAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                if (line is null) break;
                line = line.Trim();
                if (line.Length == 0) continue;
                Trace("TCP RX · " + line);
                try { CommandReceived?.Invoke(line); } catch { }
                await writer.WriteLineAsync("OK").ConfigureAwait(false);
            }
            Trace("TCP DISCONNECT · " + remote);
        }
    }

    private async Task AcceptMosAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _mosListener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = Task.Run(() => HandleMosClientAsync(client, ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { Trace("MOS: " + ex.Message); }
        }
    }

    private async Task HandleMosClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        using (var stream = client.GetStream())
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 16384, true) { AutoFlush = true })
        {
            var remote = client.Client.RemoteEndPoint?.ToString() ?? "remote";
            Trace("MOS CONNECT · " + remote);
            var buffer = new StringBuilder(32768);
            var chars = new char[8192];
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 16384, true);

            while (!ct.IsCancellationRequested && client.Connected)
            {
                int read;
                try { read = await reader.ReadAsync(chars.AsMemory(0, chars.Length), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                if (read <= 0) break;
                buffer.Append(chars, 0, read);

                while (TryTakeMosDocument(buffer, out var xml))
                {
                    var message = ParseMos(xml);
                    if (message is not null)
                    {
                        Trace($"MOS RX · {message.Action} · {message.ItemId} · {message.Title}");

                        // Parse multiple stories/items if present (e.g., standard roCreate / roStorySend)
                        try
                        {
                            var doc = XDocument.Parse(xml, LoadOptions.None);
                            var stories = doc.Descendants().Where(x => x.Name.LocalName.Equals("story", StringComparison.OrdinalIgnoreCase)).ToList();
                            if (stories.Count > 0)
                            {
                                foreach (var story in stories)
                                {
                                    var storyId = Desc(story, "storyID") ?? Guid.NewGuid().ToString("N");
                                    var storySlug = Desc(story, "storySlug") ?? "STORY";
                                    var items = story.Descendants().Where(x => x.Name.LocalName.Equals("item", StringComparison.OrdinalIgnoreCase)).ToList();
                                    if (items.Count > 0)
                                    {
                                        foreach (var itm in items)
                                        {
                                            var itmId = Desc(itm, "itemID") ?? Desc(itm, "objID") ?? storyId;
                                            var itmSlug = Desc(itm, "itemSlug") ?? Desc(itm, "objSlug") ?? storySlug;
                                            TimeSpan? d = null;
                                            var dt = Desc(itm, "objDur") ?? Desc(itm, "duration");
                                            if (int.TryParse(dt, out var fr) && fr > 0) d = TimeSpan.FromSeconds(fr / 25.0);
                                            try { MosMessageReceived?.Invoke(new MosGatewayMessage(message.Action, itmId, itmSlug, d, xml, message.MessageId)); } catch { }
                                        }
                                    }
                                    else
                                    {
                                        try { MosMessageReceived?.Invoke(new MosGatewayMessage(message.Action, storyId, storySlug, null, xml, message.MessageId)); } catch { }
                                    }
                                }
                            }
                            else
                            {
                                try { MosMessageReceived?.Invoke(message); } catch { }
                            }
                        }
                        catch
                        {
                            try { MosMessageReceived?.Invoke(message); } catch { }
                        }

                        await writer.WriteAsync(BuildAck(message)).ConfigureAwait(false);
                        await writer.FlushAsync(ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await writer.WriteAsync(BuildNack("Invalid MOS XML")).ConfigureAwait(false);
                        await writer.FlushAsync(ct).ConfigureAwait(false);
                    }
                }

                // Keep a bad/malformed sender from growing memory without bound.
                if (buffer.Length > 2_000_000)
                {
                    Trace("MOS RX buffer exceeded 2 MB without a complete </mos>; buffer reset.");
                    buffer.Clear();
                }
            }
            Trace("MOS DISCONNECT · " + remote);
        }
    }

    private static bool TryTakeMosDocument(StringBuilder buffer, out string xml)
    {
        xml = string.Empty;
        var snapshot = buffer.ToString();
        var start = snapshot.IndexOf("<mos", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return false;
        var end = snapshot.IndexOf("</mos>", start, StringComparison.OrdinalIgnoreCase);
        if (end < 0) return false;
        end += 6;
        xml = snapshot[start..end];
        buffer.Remove(0, end);
        return true;
    }

    private static MosGatewayMessage? ParseMos(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml, LoadOptions.None);
            var mos = doc.Root;
            if (mos is null || !mos.Name.LocalName.Equals("mos", StringComparison.OrdinalIgnoreCase)) return null;
            var messageId = mos.Element(mos.Name.Namespace + "messageID")?.Value?.Trim() ?? mos.Descendants().FirstOrDefault(x => x.Name.LocalName.Equals("messageID", StringComparison.OrdinalIgnoreCase))?.Value?.Trim();
            var body = mos.Elements().FirstOrDefault(e =>
                !e.Name.LocalName.Equals("mosID", StringComparison.OrdinalIgnoreCase) &&
                !e.Name.LocalName.Equals("ncsID", StringComparison.OrdinalIgnoreCase) &&
                !e.Name.LocalName.Equals("messageID", StringComparison.OrdinalIgnoreCase));
            if (body is null) return null;
            var action = body.Name.LocalName;

            // Heartbeat
            if (action.Equals("heartbeat", StringComparison.OrdinalIgnoreCase))
            {
                return new MosGatewayMessage("heartbeat", "heartbeat", "HEARTBEAT", null, xml, messageId);
            }

            // ReqAppInfo
            if (action.Equals("mosReqAppInfo", StringComparison.OrdinalIgnoreCase))
            {
                return new MosGatewayMessage("mosReqAppInfo", "appInfo", "KASHTRIX MOS APP INFO", null, xml, messageId);
            }

            var itemId = Desc(body, "itemID") ?? Desc(body, "objID") ?? Desc(body, "storyID") ?? Desc(body, "roID") ?? Guid.NewGuid().ToString("N");
            var title = Desc(body, "itemSlug") ?? Desc(body, "objSlug") ?? Desc(body, "storySlug") ?? Desc(body, "roSlug") ?? "MOS ITEM";
            TimeSpan? duration = null;
            var durText = Desc(body, "objDur") ?? Desc(body, "duration");
            if (int.TryParse(durText, out var frames) && frames > 0) duration = TimeSpan.FromSeconds(frames / 25.0);
            return new MosGatewayMessage(action, itemId, title, duration, xml, messageId);
        }
        catch { return null; }
    }

    private static string? Desc(XElement root, string name) => root.Descendants().FirstOrDefault(x => x.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value?.Trim();

    private string BuildAck(MosGatewayMessage message)
    {
        var safe = System.Security.SecurityElement.Escape(message.Action) ?? "MOS";
        var mosId = System.Security.SecurityElement.Escape(Profile.MosId) ?? "KASHTRIX.PLAYOUT";
        var ncsId = System.Security.SecurityElement.Escape(Profile.NcsId) ?? "KASHTRIX.NCS";
        var msgIdXml = string.IsNullOrWhiteSpace(message.MessageId) ? string.Empty : $"<messageID>{System.Security.SecurityElement.Escape(message.MessageId)}</messageID>";

        if (message.Action.Equals("heartbeat", StringComparison.OrdinalIgnoreCase))
        {
            return $"<mos><mosID>{mosId}</mosID><ncsID>{ncsId}</ncsID>{msgIdXml}<heartbeat/></mos>";
        }

        if (message.Action.Equals("mosReqAppInfo", StringComparison.OrdinalIgnoreCase))
        {
            return $"<mos><mosID>{mosId}</mosID><ncsID>{ncsId}</ncsID>{msgIdXml}<mosAppInfo><ncsID>{ncsId}</ncsID><ncsApp>Kashtrix Playout</ncsApp><ncsVersion>4.9H</ncsVersion><supportedProfiles><mosProfile number=\"0\"/><mosProfile number=\"1\"/><mosProfile number=\"2\"/></supportedProfiles></mosAppInfo></mos>";
        }

        return $"<mos><mosID>{mosId}</mosID><ncsID>{ncsId}</ncsID>{msgIdXml}<mosAck><status>ACK</status><statusDescription>{safe} accepted</statusDescription></mosAck></mos>";
    }

    private string BuildNack(string description, string? messageId = null)
    {
        var safe = System.Security.SecurityElement.Escape(description) ?? "Invalid MOS XML";
        var msgIdXml = string.IsNullOrWhiteSpace(messageId) ? string.Empty : $"<messageID>{System.Security.SecurityElement.Escape(messageId)}</messageID>";
        return $"<mos><mosID>{Profile.MosId}</mosID><ncsID>{Profile.NcsId}</ncsID>{msgIdXml}<mosAck><status>NACK</status><statusDescription>{safe}</statusDescription></mosAck></mos>";
    }

    public async Task<string> SendTcpAsync(string host, int port, string command, CancellationToken ct = default)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(host, Math.Clamp(port, 1, 65535), ct).ConfigureAwait(false);
        using var stream = client.GetStream();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, true);
        await writer.WriteLineAsync(command ?? string.Empty).ConfigureAwait(false);
        Trace($"TCP TX · {host}:{port} · {command}");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(1500);
        try
        {
            var response = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(response)) Trace("TCP RX REPLY · " + response);
            return response;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return string.Empty; }
    }

    public async Task SendMosAsync(string host, int port, string xml, CancellationToken ct = default)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(host, Math.Clamp(port, 1, 65535), ct).ConfigureAwait(false);
        using var writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false), 16384, true) { AutoFlush = true };
        await writer.WriteAsync(xml ?? string.Empty).ConfigureAwait(false);
        await writer.FlushAsync(ct).ConfigureAwait(false);
        Trace($"MOS TX · {host}:{port} · {SummarizeXml(xml)}");
    }

    private static string SummarizeXml(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return "(empty)";
        var one = string.Join(' ', xml.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return one.Length <= 180 ? one : one[..180] + "…";
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Stop();
    }
}

public sealed record MosGatewayMessage(string Action, string ItemId, string Title, TimeSpan? Duration, string RawXml, string? MessageId = null);

public sealed record AutomationGatewayProfile
{
    public bool AutoStart { get; init; } = true;
    public int CommandPort { get; init; } = 9101;
    public int MosLowerPort { get; init; } = 10540;
    public int MosUpperPort { get; init; } = 10541;
    public string NcsHost { get; init; } = "127.0.0.1";
    public string MosId { get; init; } = "KASHTRIX.PLAYOUT";
    public string NcsId { get; init; } = "KASHTRIX.NCS";
    public string TcpTargetHost { get; init; } = "127.0.0.1";
    public int TcpTargetPort { get; init; } = 9101;
    public string DtmfSequence { get; init; } = "1234#";
    public int DtmfToneMilliseconds { get; init; } = 120;
    public int DtmfGapMilliseconds { get; init; } = 55;
    public int DtmfLevelPercent { get; init; } = 72;
    public int DialToneMilliseconds { get; init; } = 2500;
    public int DialToneLevelPercent { get; init; } = 55;

    public AutomationGatewayProfile Normalize() => this with
    {
        CommandPort = Math.Clamp(CommandPort, 1024, 65535),
        MosLowerPort = Math.Clamp(MosLowerPort, 1024, 65535),
        MosUpperPort = Math.Clamp(MosUpperPort, 1024, 65535),
        TcpTargetPort = Math.Clamp(TcpTargetPort, 1, 65535),
        DtmfToneMilliseconds = Math.Clamp(DtmfToneMilliseconds, 45, 2000),
        DtmfGapMilliseconds = Math.Clamp(DtmfGapMilliseconds, 20, 2000),
        DtmfLevelPercent = Math.Clamp(DtmfLevelPercent, 1, 100),
        DialToneMilliseconds = Math.Clamp(DialToneMilliseconds, 100, 30000),
        DialToneLevelPercent = Math.Clamp(DialToneLevelPercent, 1, 100),
        NcsHost = string.IsNullOrWhiteSpace(NcsHost) ? "127.0.0.1" : NcsHost.Trim(),
        MosId = string.IsNullOrWhiteSpace(MosId) ? "KASHTRIX.PLAYOUT" : MosId.Trim(),
        NcsId = string.IsNullOrWhiteSpace(NcsId) ? "KASHTRIX.NCS" : NcsId.Trim(),
        TcpTargetHost = string.IsNullOrWhiteSpace(TcpTargetHost) ? "127.0.0.1" : TcpTargetHost.Trim(),
        DtmfSequence = string.IsNullOrWhiteSpace(DtmfSequence) ? "1234#" : DtmfSequence.Trim()
    };
}

public static class AutomationGatewayProfileStore
{
    private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KashtrixPlayout");
    private static string PathName => Path.Combine(Folder, "automation-gateway.json");

    public static AutomationGatewayProfile Load()
    {
        try
        {
            if (!File.Exists(PathName)) return new AutomationGatewayProfile();
            return (JsonSerializer.Deserialize<AutomationGatewayProfile>(File.ReadAllText(PathName)) ?? new AutomationGatewayProfile()).Normalize();
        }
        catch { return new AutomationGatewayProfile(); }
    }

    public static void Save(AutomationGatewayProfile profile)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(PathName, JsonSerializer.Serialize(profile.Normalize(), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
