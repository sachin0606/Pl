using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using BroadcastPlayout.Models;

namespace BroadcastPlayout.Professional;

/// <summary>Represents a decoded or generated SCTE-35 / SCTE-104 digital program insertion cue.</summary>
public sealed class ScteSpliceCue
{
    public uint EventId { get; set; }
    public string SpliceType { get; set; } = "Start Normal";
    public bool IsCueIn { get; set; } = true;
    public bool IsCueOut { get; set; }
    public bool OutOfNetworkIndicator { get => IsCueIn; set { IsCueIn = value; IsCueOut = !value; } }
    public bool SpliceEventCancelIndicator { get; set; }
    public bool ProgramSpliceFlag { get; set; } = true;
    public bool SpliceImmediate { get; set; }
    public double DurationSeconds { get; set; } = 60.0;
    public bool AutoReturn { get; set; } = true;
    public long PtsTime90k { get; set; }
    public double PtsAdjustmentSeconds { get; set; }
    public ushort UniqueProgramId { get; set; } = 1;
    public byte AvailNum { get; set; }
    public byte AvailsExpected { get; set; }
    public string Source { get; set; } = "SCTE-35 TS";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string RawHex { get; set; } = string.Empty;

    public override string ToString() =>
        $"[{Source}] {SpliceType} · Event {EventId} · {(IsCueIn ? "CUE IN" : (IsCueOut ? "CUE OUT" : "SIGNAL"))} · Dur: {DurationSeconds}s · AutoReturn: {AutoReturn}";
}

/// <summary>Builds and parses SCTE-35 splice_insert() sections and compact MPEG-TS sidecars with PMT stream_type 0x86.</summary>
public static class Scte35Codec
{
    public static byte[] BuildSpliceInsert(uint eventId, string spliceType, TimeSpan? breakDuration = null, long pts90k = 0,
        bool autoReturn = true, ushort uniqueProgramId = 1, byte availNum = 0, byte availsExpected = 0, double ptsAdjustment = 0.0)
    {
        var type = (spliceType ?? "Start Normal").Trim().ToUpperInvariant();
        var cancel = type.Contains("CANCEL", StringComparison.Ordinal);
        var outOfNetwork = type.StartsWith("START", StringComparison.Ordinal);
        var immediate = type.Contains("IMMEDIATE", StringComparison.Ordinal);

        var command = new List<byte>();
        WriteUInt32(command, eventId);
        command.Add((byte)(cancel ? 0x80 : 0x00)); // splice_event_cancel_indicator + reserved bits
        if (!cancel)
        {
            var hasDuration = breakDuration.HasValue && breakDuration.Value > TimeSpan.Zero && outOfNetwork;
            var flags = 0x40; // program_splice_flag
            if (outOfNetwork) flags |= 0x80;
            if (hasDuration) flags |= 0x20;
            if (immediate) flags |= 0x10;
            command.Add((byte)flags);

            if (!immediate)
            {
                // splice_time(): time_specified_flag=1 + 33-bit PTS in the 90 kHz clock domain.
                var pts = Math.Max(0L, pts90k) & 0x1FFFFFFFFL;
                var ptsHighBit = (int)((pts >> 32) & 0x01L);
                command.Add((byte)(0xFE | ptsHighBit));
                command.Add((byte)(pts >> 24)); command.Add((byte)(pts >> 16)); command.Add((byte)(pts >> 8)); command.Add((byte)pts);
            }
            if (hasDuration)
            {
                var dur = Math.Max(0L, (long)Math.Round(breakDuration!.Value.TotalSeconds * 90000d)) & 0x1FFFFFFFFL;
                var durationHighBit = (int)((dur >> 32) & 0x01L);
                command.Add((byte)((autoReturn ? 0x80 : 0x00) | 0x7E | durationHighBit));
                command.Add((byte)(dur >> 24)); command.Add((byte)(dur >> 16)); command.Add((byte)(dur >> 8)); command.Add((byte)dur);
            }
            command.Add((byte)(uniqueProgramId >> 8)); command.Add((byte)uniqueProgramId);
            command.Add(availNum); command.Add(availsExpected);
        }

        var section = new List<byte> { 0xFC, 0x30, 0x00, 0x00 };
        // encryption/pts_adjustment (33-bit ticks)
        var adjTicks = Math.Max(0L, (long)Math.Round(ptsAdjustment * 90000d)) & 0x1FFFFFFFFL;
        section.Add((byte)((adjTicks >> 32) & 0x01));
        section.Add((byte)(adjTicks >> 24));
        section.Add((byte)(adjTicks >> 16));
        section.Add((byte)(adjTicks >> 8));
        section.Add((byte)adjTicks);

        section.Add(0x00); // cw_index
        var cmdLen = command.Count;
        section.Add(0xFF); // tier high bits
        section.Add((byte)(0xF0 | ((cmdLen >> 8) & 0x0F)));
        section.Add((byte)(cmdLen & 0xFF));
        section.Add(0x05); // splice_insert
        section.AddRange(command);
        section.Add(0x00); section.Add(0x00); // descriptor_loop_length
        var sectionLength = section.Count - 3 + 4;
        section[1] = (byte)(0x30 | ((sectionLength >> 8) & 0x0F)); section[2] = (byte)sectionLength;
        WriteUInt32(section, MpegCrc32(section.ToArray()));
        return section.ToArray();
    }

    public static ScteSpliceCue? ParseSpliceSection(ReadOnlySpan<byte> data, string source = "SCTE-35 TS")
    {
        if (data.Length < 14) return null;
        var offset = 0;
        while (offset < data.Length && data[offset] != 0xFC) offset++;
        if (offset + 14 > data.Length) return null;

        var span = data.Slice(offset);
        if (span[0] != 0xFC) return null;
        var sectionLength = ((span[1] & 0x0F) << 8) | span[2];
        if (span.Length < Math.Min(sectionLength + 3, 20)) return null;

        var cmdType = span[12];
        if (cmdType == 0x05) // splice_insert
        {
            var p = 13;
            if (p + 4 > span.Length) return null;
            var eventId = ((uint)span[p] << 24) | ((uint)span[p + 1] << 16) | ((uint)span[p + 2] << 8) | span[p + 3];
            p += 4;
            var cancel = (span[p] & 0x80) != 0;
            p++;

            var cue = new ScteSpliceCue
            {
                EventId = eventId,
                SpliceEventCancelIndicator = cancel,
                Source = source,
                Timestamp = DateTime.UtcNow,
                RawHex = Convert.ToHexString(span.Slice(0, Math.Min(span.Length, sectionLength + 3)))
            };

            if (cancel)
            {
                cue.SpliceType = "Cancel";
                cue.IsCueIn = false;
                cue.IsCueOut = false;
                return cue;
            }

            if (p >= span.Length) return cue;
            var flags = span[p++];
            var outOfNetwork = (flags & 0x80) != 0;
            var programSplice = (flags & 0x40) != 0;
            var durationFlag = (flags & 0x20) != 0;
            var immediate = (flags & 0x10) != 0;

            cue.IsCueIn = outOfNetwork;
            cue.IsCueOut = !outOfNetwork;
            cue.ProgramSpliceFlag = programSplice;
            cue.SpliceImmediate = immediate;
            cue.SpliceType = outOfNetwork 
                ? (immediate ? "Start Immediate" : "Start Normal") 
                : (immediate ? "End Immediate" : "End Normal");

            if (!immediate && p + 5 <= span.Length)
            {
                var timeSpecified = (span[p] & 0x80) != 0;
                if (timeSpecified)
                {
                    var ptsHigh = (long)(span[p] & 0x01);
                    var ptsLow = ((long)span[p + 1] << 24) | ((long)span[p + 2] << 16) | ((long)span[p + 3] << 8) | span[p + 4];
                    cue.PtsTime90k = (ptsHigh << 32) | ptsLow;
                }
                p += 5;
            }

            if (durationFlag && p + 5 <= span.Length)
            {
                var autoRet = (span[p] & 0x80) != 0;
                var durHigh = (long)(span[p] & 0x01);
                var durLow = ((long)span[p + 1] << 24) | ((long)span[p + 2] << 16) | ((long)span[p + 3] << 8) | span[p + 4];
                var durTicks = (durHigh << 32) | durLow;
                cue.AutoReturn = autoRet;
                cue.DurationSeconds = Math.Round(durTicks / 90000.0, 1);
                p += 5;
            }

            if (p + 4 <= span.Length)
            {
                cue.UniqueProgramId = (ushort)(((ushort)span[p] << 8) | span[p + 1]);
                p += 2;
                cue.AvailNum = span[p++];
                cue.AvailsExpected = span[p++];
            }

            return cue;
        }

        return null;
    }

    public static uint MpegCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data) { crc ^= (uint)b << 24; for (var i = 0; i < 8; i++) crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1; }
        return crc;
    }
    private static void WriteUInt32(List<byte> b, uint v) { b.Add((byte)(v >> 24)); b.Add((byte)(v >> 16)); b.Add((byte)(v >> 8)); b.Add((byte)v); }
}

/// <summary>Builds and decodes SCTE-104 automation protocol messages for VANC or IP/UDP.</summary>
public static class Scte104Codec
{
    public static byte[] BuildSpliceRequest(uint eventId, string spliceType, TimeSpan? breakDuration = null, ushort preRollMs = 4000, bool autoReturn = true)
    {
        var typeByte = (spliceType ?? "").ToUpperInvariant() switch
        {
            "START IMMEDIATE" => (byte)2,
            "END NORMAL" => (byte)3,
            "END IMMEDIATE" => (byte)4,
            "CANCEL" => (byte)5,
            _ => (byte)1 // Start Normal
        };
        var durTenths = (ushort)(breakDuration.HasValue ? Math.Round(breakDuration.Value.TotalSeconds * 10) : 0);

        var msg = new List<byte>
        {
            0xFF, 0xFF, 0x00, 0x14, // SCTE-104 framing prefix
            0x01, 0x01,             // opID 0x0101 splice_request_data
            0x00, 0x0E,             // length 14 bytes
            typeByte,
            (byte)(eventId >> 24), (byte)(eventId >> 16), (byte)(eventId >> 8), (byte)eventId,
            (byte)(preRollMs >> 8), (byte)preRollMs,
            (byte)(durTenths >> 8), (byte)durTenths,
            (byte)(autoReturn ? 1 : 0),
            0x00, 0x01,             // unique_program_id
            0x00, 0x00              // avail_num, avails_expected
        };
        return msg.ToArray();
    }

    public static ScteSpliceCue? ParseScte104(ReadOnlySpan<byte> data, string source = "SCTE-104 IP")
    {
        if (data.Length < 10) return null;
        for (int i = 0; i <= data.Length - 10; i++)
        {
            if (data[i] == 0x01 && data[i + 1] == 0x01) // opID 0x0101
            {
                var p = i + 4; // skip opID and length
                if (p + 8 > data.Length) break;
                var typeByte = data[p++];
                var eventId = ((uint)data[p] << 24) | ((uint)data[p + 1] << 16) | ((uint)data[p + 2] << 8) | data[p + 3];
                p += 4;
                var preRoll = ((ushort)data[p] << 8) | data[p + 1];
                p += 2;
                var durTenths = p + 2 <= data.Length ? (((ushort)data[p] << 8) | data[p + 1]) : 0;
                p += 2;
                var autoRet = p < data.Length && data[p] != 0;

                var isCueIn = typeByte == 1 || typeByte == 2;
                var spliceType = typeByte switch
                {
                    1 => "Start Normal",
                    2 => "Start Immediate",
                    3 => "End Normal",
                    4 => "End Immediate",
                    5 => "Cancel",
                    _ => "Start Normal"
                };

                return new ScteSpliceCue
                {
                    EventId = eventId,
                    SpliceType = spliceType,
                    IsCueIn = isCueIn,
                    IsCueOut = !isCueIn && typeByte != 5,
                    SpliceEventCancelIndicator = typeByte == 5,
                    DurationSeconds = durTenths / 10.0,
                    AutoReturn = autoRet,
                    Source = source,
                    Timestamp = DateTime.UtcNow,
                    RawHex = Convert.ToHexString(data)
                };
            }
        }
        return null;
    }
}

public sealed class Scte35TsSender : IDisposable
{
    private readonly UdpClient _udp = new(); private int _ccPat, _ccPmt, _ccScte;
    public string LastError { get; private set; } = string.Empty;

    public void SendCue(ProfessionalBroadcastSettings s, PlaylistItem item, uint eventId, long currentPts90k)
    {
        if (!s.EnableScte35Ts) return;
        try
        {
            var ep = new IPEndPoint(IPAddress.Parse(s.Scte35TsDestination), Math.Clamp(s.Scte35TsPort, 1, 65535));
            foreach (var packet in PacketizePsi(BuildPat((ushort)s.Scte35ProgramNumber, (ushort)s.Scte35PmtPid), 0x0000, ref _ccPat)) _udp.Send(packet, packet.Length, ep);
            foreach (var packet in PacketizePsi(BuildPmt((ushort)s.Scte35ProgramNumber, (ushort)s.Scte35Pid), (ushort)s.Scte35PmtPid, ref _ccPmt)) _udp.Send(packet, packet.Length, ep);
            var scheduledPts = item.SctePts90k > 0 ? item.SctePts90k : currentPts90k + (long)item.SctePreRollMilliseconds * 90L;
            TimeSpan? duration = item.ScteDurationSeconds > 0 ? (TimeSpan?)TimeSpan.FromSeconds(item.ScteDurationSeconds) : null;
            var section = Scte35Codec.BuildSpliceInsert(eventId, item.ScteSpliceType, duration, scheduledPts, item.ScteAutoReturn,
                (ushort)item.ScteUniqueProgramId, (byte)item.ScteAvailNum, (byte)item.ScteAvailsExpected, s.SctePtsAdjustmentSeconds);
            foreach (var packet in PacketizePsi(section, (ushort)s.Scte35Pid, ref _ccScte)) _udp.Send(packet, packet.Length, ep);
            LastError = string.Empty;
        }
        catch (Exception ex) { LastError = ex.Message; }
    }

    public void SendManualCue(ProfessionalBroadcastSettings s, ScteSpliceCue cue)
    {
        if (!s.EnableScte35Ts) return;
        try
        {
            var ep = new IPEndPoint(IPAddress.Parse(s.Scte35TsDestination), Math.Clamp(s.Scte35TsPort, 1, 65535));
            foreach (var packet in PacketizePsi(BuildPat((ushort)s.Scte35ProgramNumber, (ushort)s.Scte35PmtPid), 0x0000, ref _ccPat)) _udp.Send(packet, packet.Length, ep);
            foreach (var packet in PacketizePsi(BuildPmt((ushort)s.Scte35ProgramNumber, (ushort)s.Scte35Pid), (ushort)s.Scte35PmtPid, ref _ccPmt)) _udp.Send(packet, packet.Length, ep);
            TimeSpan? duration = cue.DurationSeconds > 0 ? (TimeSpan?)TimeSpan.FromSeconds(cue.DurationSeconds) : null;
            var section = Scte35Codec.BuildSpliceInsert(cue.EventId, cue.SpliceType, duration, cue.PtsTime90k, cue.AutoReturn,
                cue.UniqueProgramId, cue.AvailNum, cue.AvailsExpected, s.SctePtsAdjustmentSeconds);
            foreach (var packet in PacketizePsi(section, (ushort)s.Scte35Pid, ref _ccScte)) _udp.Send(packet, packet.Length, ep);
            LastError = string.Empty;
        }
        catch (Exception ex) { LastError = ex.Message; }
    }

    private static byte[] BuildPat(ushort program, ushort pmtPid)
    {
        var b = new List<byte> { 0x00,0xB0,0x0D,0x00,0x01,0xC1,0x00,0x00,(byte)(program>>8),(byte)program,(byte)(0xE0 | ((pmtPid>>8)&0x1F)),(byte)pmtPid };
        var crc = Scte35Codec.MpegCrc32(b.ToArray()); b.Add((byte)(crc >> 24)); b.Add((byte)(crc >> 16)); b.Add((byte)(crc >> 8)); b.Add((byte)crc); return b.ToArray();
    }
    private static byte[] BuildPmt(ushort program, ushort sctePid)
    {
        var b = new List<byte> { 0x02,0xB0,0x12,(byte)(program>>8),(byte)program,0xC1,0x00,0x00,
            (byte)(0xE0|((sctePid>>8)&0x1F)),(byte)sctePid,0xF0,0x00, 0x86,(byte)(0xE0|((sctePid>>8)&0x1F)),(byte)sctePid,0xF0,0x00 };
        var crc = Scte35Codec.MpegCrc32(b.ToArray()); b.Add((byte)(crc >> 24)); b.Add((byte)(crc >> 16)); b.Add((byte)(crc >> 8)); b.Add((byte)crc); return b.ToArray();
    }
    private static IReadOnlyList<byte[]> PacketizePsi(byte[] section, ushort pid, ref int cc)
    {
        var packets = new List<byte[]>(); var offset = 0; var first = true;
        while (offset < section.Length)
        {
            var packet = Enumerable.Repeat((byte)0xFF, 188).ToArray(); packet[0] = 0x47; packet[1] = (byte)((first ? 0x40 : 0) | ((pid >> 8) & 0x1F)); packet[2] = (byte)pid; packet[3] = (byte)(0x10 | (cc++ & 0x0F));
            var p = 4; if (first) packet[p++] = 0x00; var n = Math.Min(188 - p, section.Length - offset); Buffer.BlockCopy(section, offset, packet, p, n); offset += n; first = false; packets.Add(packet);
        }
        return packets;
    }
    public void Dispose() => _udp.Dispose();
}

public sealed class Scte104IpSender : IDisposable
{
    private readonly UdpClient _udp = new();
    public void SendCue(ProfessionalBroadcastSettings s, PlaylistItem item, uint eventId)
    {
        if (!s.EnableScte104Ip) return;
        try
        {
            var ep = new IPEndPoint(IPAddress.Parse(s.Scte104IpDestination), Math.Clamp(s.Scte104IpPort, 1, 65535));
            var dur = item.ScteDurationSeconds > 0 ? (TimeSpan?)TimeSpan.FromSeconds(item.ScteDurationSeconds) : null;
            var packet = Scte104Codec.BuildSpliceRequest(eventId, item.ScteSpliceType, dur, (ushort)item.SctePreRollMilliseconds, item.ScteAutoReturn);
            _udp.Send(packet, packet.Length, ep);
        }
        catch { }
    }

    public void SendManualCue(ProfessionalBroadcastSettings s, ScteSpliceCue cue)
    {
        if (!s.EnableScte104Ip) return;
        try
        {
            var ep = new IPEndPoint(IPAddress.Parse(s.Scte104IpDestination), Math.Clamp(s.Scte104IpPort, 1, 65535));
            var dur = cue.DurationSeconds > 0 ? (TimeSpan?)TimeSpan.FromSeconds(cue.DurationSeconds) : null;
            var packet = Scte104Codec.BuildSpliceRequest(cue.EventId, cue.SpliceType, dur, 4000, cue.AutoReturn);
            _udp.Send(packet, packet.Length, ep);
        }
        catch { }
    }

    public void Dispose() => _udp.Dispose();
}

/// <summary>Active UDP receiver for detecting inbound SCTE-35 TS and SCTE-104 IP cues to trigger ad breaks in playout.</summary>
public sealed class ScteReceiverService : IDisposable
{
    public static ScteReceiverService Instance { get; } = new();

    private UdpClient? _scte35Client;
    private UdpClient? _scte104Client;
    private CancellationTokenSource? _cts;
    private bool _running;

    public event EventHandler<ScteSpliceCue>? SpliceReceived;

    public void Start(ProfessionalBroadcastSettings s)
    {
        Stop();
        _cts = new CancellationTokenSource();
        _running = true;

        if (s.EnableScte35Receiver)
        {
            try
            {
                _scte35Client = new UdpClient(s.Scte35ReceiverPort);
                _ = Task.Run(() => ListenLoopAsync(_scte35Client, "SCTE-35 UDP", s.Scte35Pid, _cts.Token));
            }
            catch { }
        }

        if (s.EnableScte104Receiver)
        {
            try
            {
                _scte104Client = new UdpClient(s.Scte104ReceiverPort);
                _ = Task.Run(() => ListenLoopAsync(_scte104Client, "SCTE-104 IP", 0, _cts.Token));
            }
            catch { }
        }
    }

    private async Task ListenLoopAsync(UdpClient client, string source, int pid, CancellationToken token)
    {
        while (!token.IsCancellationRequested && _running)
        {
            try
            {
                var result = await client.ReceiveAsync(token);
                var data = result.Buffer;
                if (data == null || data.Length == 0) continue;

                ScteSpliceCue? cue = null;
                if (source.StartsWith("SCTE-35", StringComparison.OrdinalIgnoreCase))
                {
                    cue = Scte35Codec.ParseSpliceSection(data, source);
                }
                else
                {
                    cue = Scte104Codec.ParseScte104(data, source);
                }

                if (cue != null)
                {
                    SpliceReceived?.Invoke(this, cue);
                }
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    public void Stop()
    {
        _running = false;
        _cts?.Cancel();
        _scte35Client?.Dispose();
        _scte35Client = null;
        _scte104Client?.Dispose();
        _scte104Client = null;
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Stop();
}