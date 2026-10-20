using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using BroadcastPlayout.Models;

namespace BroadcastPlayout.Professional;

/// <summary>Win32 COM transport for common USB/PCI RS-422 adapters exposed as COM ports.</summary>
public sealed class Rs422Transport : IDisposable
{
    private IntPtr _handle = new(-1);
    public string LastError { get; private set; } = string.Empty;

    public bool Open(string port, int baud, bool oddParity = false)
    {
        Close();
        if (!OperatingSystem.IsWindows()) { LastError = "RS-422 COM transport requires Windows"; return false; }
        _handle = CreateFile("\\\\.\\" + port, 0xC0000000, 0, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (_handle == new IntPtr(-1)) { LastError = "CreateFile failed: " + Marshal.GetLastWin32Error(); return false; }
        var dcb = new DCB
        {
            DCBlength = (uint)Marshal.SizeOf<DCB>(), BaudRate = (uint)Math.Clamp(baud, 1200, 115200), ByteSize = 8,
            Parity = (byte)(oddParity ? 1 : 0), StopBits = 0, Flags = oddParity ? 3u : 1u
        };
        if (!SetCommState(_handle, ref dcb)) { LastError = "SetCommState failed: " + Marshal.GetLastWin32Error(); Close(); return false; }
        LastError = string.Empty; return true;
    }
    public bool Write(ReadOnlySpan<byte> bytes) { if (_handle == new IntPtr(-1)) return false; var b = bytes.ToArray(); return WriteFile(_handle, b, (uint)b.Length, out var n, IntPtr.Zero) && n == b.Length; }
    public bool WriteAscii(string text) => Write(Encoding.ASCII.GetBytes(text ?? string.Empty));
    public byte[] Read(int max = 256) { if (_handle == new IntPtr(-1)) return []; var b = new byte[Math.Clamp(max, 1, 4096)]; return ReadFile(_handle, b, (uint)b.Length, out var n, IntPtr.Zero) ? b.Take((int)n).ToArray() : []; }
    public void Close() { if (_handle != new IntPtr(-1)) { CloseHandle(_handle); _handle = new IntPtr(-1); } }
    public void Dispose() => Close();
    [StructLayout(LayoutKind.Sequential)] private struct DCB { public uint DCBlength, BaudRate, Flags; public ushort wReserved, XonLim, XoffLim; public byte ByteSize, Parity, StopBits; public sbyte XonChar, XoffChar, ErrorChar, EofChar, EvtChar; public ushort wReserved1; }
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr CreateFile(string n, uint a, uint s, IntPtr sa, uint c, uint f, IntPtr t);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetCommState(IntPtr h, ref DCB d);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteFile(IntPtr h, byte[] b, uint n, out uint w, IntPtr o);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadFile(IntPtr h, byte[] b, uint n, out uint r, IntPtr o);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
}

public sealed class GpioSerialController : IDisposable
{
    private IntPtr _handle = new(-1); public string LastError { get; private set; } = string.Empty;
    public bool Open(string port) { Close(); if (!OperatingSystem.IsWindows()) { LastError = "GPIO COM control requires Windows"; return false; } _handle = CreateFile("\\\\.\\" + port, 0xC0000000, 0, IntPtr.Zero, 3, 0, IntPtr.Zero); if (_handle == new IntPtr(-1)) { LastError = "GPIO open failed: " + Marshal.GetLastWin32Error(); return false; } LastError = string.Empty; return true; }
    /// <summary>Maps even outputs to RTS and odd outputs to DTR for two-line serial GPIO adapters.</summary>
    public bool SetOutput(int output, bool on) { if (_handle == new IntPtr(-1)) return false; var fn = output % 2 == 0 ? (on ? SETRTS : CLRRTS) : (on ? SETDTR : CLRDTR); return EscapeCommFunction(_handle, fn); }
    public async Task PulseAsync(int output, int milliseconds, bool activeHigh = true, CancellationToken ct = default) { SetOutput(output, activeHigh); try { await Task.Delay(Math.Clamp(milliseconds, 10, 60000), ct); } finally { SetOutput(output, !activeHigh); } }
    public (bool Cts, bool Dsr, bool Ring, bool Rlsd) ReadInputs() { if (_handle == new IntPtr(-1) || !GetCommModemStatus(_handle, out var s)) return default; return ((s & 0x10) != 0, (s & 0x20) != 0, (s & 0x40) != 0, (s & 0x80) != 0); }
    public void Close() { if (_handle != new IntPtr(-1)) { CloseHandle(_handle); _handle = new IntPtr(-1); } } public void Dispose() => Close();
    private const uint SETRTS = 3, CLRRTS = 4, SETDTR = 5, CLRDTR = 6;
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr CreateFile(string n, uint a, uint s, IntPtr sa, uint c, uint f, IntPtr t);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool EscapeCommFunction(IntPtr h, uint f);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetCommModemStatus(IntPtr h, out uint s);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
}

/// <summary>VDCP byte framing over RS-422. Implements the common STOP, PLAY, CUE and CUE WITH DATA commands plus raw mode.</summary>
public sealed class VdcpClient
{
    private readonly Rs422Transport _serial;
    public VdcpClient(Rs422Transport serial) => _serial = serial;

    public bool Send(PlaylistItem item)
    {
        var port = Math.Clamp(item.VdcpPort, 0, 15);
        var cmd = (item.VdcpCommand ?? "PLAY").Trim().ToUpperInvariant();
        return cmd switch
        {
            "STOP" => _serial.Write(BuildFrame(port, 0x1, 0x00, [])),
            "PLAY" => _serial.Write(BuildFrame(port, 0x1, 0x01, [])),
            "CUE" => _serial.Write(BuildFrame(port, 0x2, 0x24, ClipId(item.VdcpClipId))),
            "CUE WITH DATA" => _serial.Write(BuildFrame(port, 0x2, 0x25, ClipId(item.VdcpClipId).Concat(BcdTimecode(item.VdcpStartTimecode)).Concat(BcdTimecode(item.VdcpDurationTimecode)).ToArray())),
            "PORT STATUS" => _serial.Write(BuildFrame(port, 0x3, 0x05, [0xFF])),
            "RAW HEX" => SendRawHex(item.VdcpHex),
            _ => false
        };
    }

    public bool SendRawHex(string hex)
    {
        try { var clean = new string((hex ?? string.Empty).Where(Uri.IsHexDigit).ToArray()); if ((clean.Length & 1) != 0) return false; var b = Enumerable.Range(0, clean.Length / 2).Select(i => Convert.ToByte(clean.Substring(i * 2, 2), 16)).ToArray(); return _serial.Write(b); } catch { return false; }
    }

    public static byte[] BuildFrame(int port, byte commandClass, byte command, byte[] data)
    {
        // VDCP: STX, byte-count, TYPE/UA, CMD-2, DATA..., two's-complement checksum of TYPE..DATA.
        var body = new List<byte> { (byte)((commandClass << 4) | (port & 0x0F)), command };
        body.AddRange(data ?? []);
        var checksum = unchecked((byte)(0 - body.Sum(x => x)));
        var frame = new List<byte> { 0x02, (byte)body.Count }; frame.AddRange(body); frame.Add(checksum); return frame.ToArray();
    }
    private static byte[] ClipId(string value) => Encoding.ASCII.GetBytes((value ?? string.Empty).PadRight(8).Substring(0, 8));
    private static byte[] BcdTimecode(string text)
    {
        var p = (text ?? "00:00:00:00").Split(':'); if (p.Length != 4) p = ["00", "00", "00", "00"];
        return p.Select(x => int.TryParse(x, out var n) ? (byte)(((Math.Clamp(n, 0, 99) / 10) << 4) | (Math.Clamp(n, 0, 99) % 10)) : (byte)0).ToArray();
    }
}

public sealed class SnmpV2Agent : IDisposable
{
    private UdpClient? _agent; private CancellationTokenSource? _cts; private ProfessionalBroadcastSettings? _s; private Func<string>? _status;
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    public void Start(ProfessionalBroadcastSettings s, Func<string> status) { Stop(); if (!s.EnableSnmp) return; _s = s; _status = status; try { _agent = new UdpClient(s.SnmpAgentPort); _cts = new CancellationTokenSource(); _ = Task.Run(() => Loop(_cts.Token)); } catch { } }
    private async Task Loop(CancellationToken ct) { while (!ct.IsCancellationRequested) { try { var r = await _agent!.ReceiveAsync(ct); if (!TryParseGet(r.Buffer, out var requestId, out var oid, out var community)) continue; if (!community.Equals(_s!.SnmpReadCommunity, StringComparison.Ordinal)) continue; var value = oid switch { "1.3.6.1.4.1.55555.1.1.0" => _status?.Invoke() ?? "UNKNOWN", "1.3.6.1.4.1.55555.1.2.0" => Environment.MachineName, _ => "noSuchObject" }; var resp = BuildResponse(requestId, community, oid, value); await _agent.SendAsync(resp, r.RemoteEndPoint, ct); } catch (OperationCanceledException) { break; } catch (ObjectDisposedException) { break; } catch { } } }

    public Task SendTrapAsync(string text, CancellationToken ct = default)
    {
        if (_s is null) return Task.CompletedTask;
        return SendTrapAsync(_s.SnmpTrapHost, _s.SnmpTrapPort, _s.SnmpTrapCommunity, "1.3.6.1.4.1.55555.1.10.0", "1.3.6.1.4.1.55555.1.11.0", "String", text, ct);
    }
    public async Task SendTrapAsync(string host, int port, string community, string trapOid, string varbindOid, string valueType, string value, CancellationToken ct = default)
    {
        try
        {
            using var u = new UdpClient();
            var ticks = (uint)Math.Clamp((DateTime.UtcNow - _startedUtc).TotalMilliseconds / 10.0, 0, uint.MaxValue);
            var packet = BuildTrap(Environment.TickCount, community, ticks, trapOid, varbindOid, valueType, value);
            var addresses = await Dns.GetHostAddressesAsync(host, ct); var address = addresses.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork) ?? addresses.First();
            await u.SendAsync(packet, new IPEndPoint(address, Math.Clamp(port, 1, 65535)), ct);
        }
        catch { }
    }

    private static bool TryParseGet(byte[] b, out int id, out string oid, out string comm) { id = 0; oid = "1.3.6.1.4.1.55555.1.1.0"; comm = string.Empty; try { var p = 0; ReadTlv(b, ref p, out _, out var msg); var q = msg.start; ReadTlv(b, ref q, out _, out _); ReadTlv(b, ref q, out _, out var c); comm = Encoding.ASCII.GetString(b, c.start, c.len); ReadTlv(b, ref q, out var pdu, out var pd); if (pdu != 0xA0) return false; var x = pd.start; ReadTlv(b, ref x, out _, out var rid); id = ReadInt(b, rid.start, rid.len); return true; } catch { return false; } }
    private static byte[] BuildResponse(int id, string comm, string oid, string value) { var vb = Seq(Oid(oid), Tlv(0x04, Encoding.UTF8.GetBytes(value))); var pduBody = Cat(Int(id), Int(0), Int(0), Seq(vb)); return Seq(Int(1), Tlv(0x04, Encoding.ASCII.GetBytes(comm)), Tlv(0xA2, pduBody)); }
    private static byte[] BuildTrap(int requestId, string community, uint uptimeTicks, string trapOid, string varbindOid, string valueType, string value)
    {
        // RFC 3416: sysUpTime.0 and snmpTrapOID.0 are mandatory first/second varbinds.
        var uptime = Seq(Oid("1.3.6.1.2.1.1.3.0"), Tlv(0x43, Unsigned(uptimeTicks)));
        var trap = Seq(Oid("1.3.6.1.6.3.1.1.4.1.0"), Oid(trapOid));
        var custom = Seq(Oid(varbindOid), TypedValue(valueType, value));
        var pduBody = Cat(Int(requestId), Int(0), Int(0), Seq(uptime, trap, custom));
        return Seq(Int(1), Tlv(0x04, Encoding.ASCII.GetBytes(community)), Tlv(0xA7, pduBody));
    }
    private static byte[] TypedValue(string type, string value) => (type ?? "String").Trim().ToUpperInvariant() switch
    {
        "INTEGER" => Int(int.TryParse(value, out var n) ? n : 0),
        "OID" or "OBJECT ID" => Oid(value),
        "COUNTER32" => Tlv(0x41, Unsigned(uint.TryParse(value, out var n) ? n : 0)),
        "GAUGE32" => Tlv(0x42, Unsigned(uint.TryParse(value, out var n) ? n : 0)),
        "TIMETICKS" => Tlv(0x43, Unsigned(uint.TryParse(value, out var n) ? n : 0)),
        _ => Tlv(0x04, Encoding.UTF8.GetBytes(value ?? string.Empty))
    };
    private static byte[] Oid(string text) { var nums = text.Split('.').Where(x => x.Length > 0).Select(int.Parse).ToArray(); if (nums.Length < 2) nums = [1, 3, 6, 1]; var o = new List<byte> { (byte)(nums[0] * 40 + nums[1]) }; for (var i = 2; i < nums.Length; i++) { var n = nums[i]; var stack = new Stack<byte>(); stack.Push((byte)(n & 0x7f)); n >>= 7; while (n > 0) { stack.Push((byte)(0x80 | (n & 0x7f))); n >>= 7; } o.AddRange(stack); } return Tlv(0x06, o.ToArray()); }
    private static byte[] Unsigned(uint v) { var b = new List<byte>(); do { b.Insert(0, (byte)v); v >>= 8; } while (v > 0); if ((b[0] & 0x80) != 0) b.Insert(0, 0); return b.ToArray(); }
    private static byte[] Int(int v) { var raw = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(v)); var i = 0; while (i < raw.Length - 1 && raw[i] == 0 && (raw[i + 1] & 0x80) == 0) i++; return Tlv(0x02, raw[i..]); }
    private static int ReadInt(byte[] b, int s, int n) { var v = 0; for (var i = 0; i < n; i++) v = (v << 8) | b[s + i]; return v; }
    private static byte[] Seq(params byte[][] p) => Tlv(0x30, Cat(p)); private static byte[] Cat(params byte[][] p) { var n = p.Sum(x => x.Length); var b = new byte[n]; var o = 0; foreach (var x in p) { Buffer.BlockCopy(x, 0, b, o, x.Length); o += x.Length; } return b; }
    private static byte[] Tlv(byte tag, byte[] v) { if (v.Length < 128) return [tag, (byte)v.Length, .. v]; if (v.Length <= 255) return [tag, 0x81, (byte)v.Length, .. v]; return [tag, 0x82, (byte)(v.Length >> 8), (byte)v.Length, .. v]; }
    private static void ReadTlv(byte[] b, ref int p, out byte tag, out (int start, int len) v) { tag = b[p++]; var n = (int)b[p++]; if ((n & 0x80) != 0) { var c = n & 0x7f; n = 0; while (c-- > 0) n = (n << 8) | b[p++]; } v = (p, n); p += n; }
    public void Stop() { var c = Interlocked.Exchange(ref _cts, null); try { c?.Cancel(); } catch { } try { _agent?.Dispose(); } catch { } _agent = null; try { c?.Dispose(); } catch { } } public void Dispose() => Stop();
}

public interface IDolbyCodecAdapter : IDisposable { bool IsLicensed { get; } string Name { get; } byte[] Encode(ReadOnlySpan<byte> pcm48k, int channels); byte[] Decode(ReadOnlySpan<byte> frame); }
public sealed class DolbyIntegration : IDisposable
{
    public string Mode { get; private set; } = "Passthrough"; public string Status { get; private set; } = "PASSTHROUGH"; public IDolbyCodecAdapter? Adapter { get; private set; }
    public void Configure(ProfessionalBroadcastSettings s) { Mode = s.DolbyMode; if (Mode.Equals("LicensedAdapter", StringComparison.OrdinalIgnoreCase)) { Status = File.Exists(s.DolbyAdapterPath) ? "LICENSED ADAPTER PATH CONFIGURED · integration DLL required" : "LICENSED ADAPTER NOT INSTALLED"; } else Status = Mode.Equals("Off", StringComparison.OrdinalIgnoreCase) ? "OFF" : "PASSTHROUGH"; }
    public void Dispose() { Adapter?.Dispose(); Adapter = null; }
}
