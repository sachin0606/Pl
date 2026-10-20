using BroadcastPlayout.Models;

namespace BroadcastPlayout.Professional;

public sealed record AncillaryPacket(byte Did, byte Sdid, ushort Line, byte[] UserData, string Kind);

public static class AncillaryDataBuilder
{
    /// <summary>Build an SCTE-104 multiple_operation_message containing one splice_request_data operation (opID 0x0101).</summary>
    public static AncillaryPacket BuildScte104(ProfessionalBroadcastSettings s, PlaylistItem item, uint eventId, byte messageNumber)
    {
        var spliceType = Scte104SpliceType(item.ScteSpliceType);
        var data = new List<byte>
        {
            spliceType,
            (byte)(eventId >> 24), (byte)(eventId >> 16), (byte)(eventId >> 8), (byte)eventId,
            (byte)(item.ScteUniqueProgramId >> 8), (byte)item.ScteUniqueProgramId,
            (byte)(item.SctePreRollMilliseconds >> 8), (byte)item.SctePreRollMilliseconds
        };
        var tenths = Math.Clamp((int)Math.Round(item.ScteDurationSeconds * 10d), 0, 65535);
        data.Add((byte)(tenths >> 8)); data.Add((byte)tenths);
        data.Add((byte)item.ScteAvailNum); data.Add((byte)item.ScteAvailsExpected); data.Add((byte)(item.ScteAutoReturn ? 1 : 0));

        // multiple_operation_message(): reserved, size, protocol, AS, message, DPI PID index,
        // SCTE35 protocol, time_type=0 (immediate message processing), num_ops, opID, data_length, data.
        var msg = new List<byte> { 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, messageNumber, 0x00, 0x00, 0x00, 0x00, 0x01, 0x01, 0x01,
            (byte)(data.Count >> 8), (byte)data.Count };
        msg.AddRange(data);
        var size = msg.Count; msg[2] = (byte)(size >> 8); msg[3] = (byte)size;

        // SMPTE ST 2010 assigns SCTE-104 VANC DID/SDID 0x41/0x07.
        return new AncillaryPacket(0x41, 0x07, (ushort)Math.Clamp(s.Scte104VancLine, 1, 2047), msg.ToArray(), "SCTE-104");
    }

    /// <summary>Builds parity-correct CEA-608 command/text pairs for the selected channel and display mode.</summary>
    public static AncillaryPacket BuildCea608(ProfessionalBroadcastSettings s, PlaylistItem item)
    {
        var channelBase = item.Caption608Channel.ToUpperInvariant() switch { "CC2" => (byte)0x1C, "CC3" => (byte)0x15, "CC4" => (byte)0x1D, _ => (byte)0x14 };
        var modeCode = item.Caption608Mode.ToUpperInvariant() switch { "PAINT-ON" => (byte)0x29, "ROLL-UP 2" => (byte)0x25, "ROLL-UP 3" => (byte)0x26, "ROLL-UP 4" => (byte)0x27, _ => (byte)0x20 };
        var payload = new List<byte>();
        AddControlPair(payload, channelBase, modeCode);
        if (item.Caption608Mode.Equals("Pop-On", StringComparison.OrdinalIgnoreCase)) AddControlPair(payload, channelBase, 0x2E); // ENM
        AddPac(payload, channelBase, item.Caption608Row, item.Caption608Underline, item.Caption608Italics);

        foreach (var line in Wrap608(item.CaptionText))
        {
            var bytes = line.Select(ch => WithOddParity((byte)(ch >= 0x20 && ch <= 0x7F ? ch : '?'))).ToList();
            if ((bytes.Count & 1) != 0) bytes.Add(WithOddParity((byte)' '));
            payload.AddRange(bytes);
            if (item.Caption608Mode.StartsWith("Roll-Up", StringComparison.OrdinalIgnoreCase)) AddControlPair(payload, channelBase, 0x2D); // CR
        }
        if (item.Caption608Mode.Equals("Pop-On", StringComparison.OrdinalIgnoreCase)) AddControlPair(payload, channelBase, 0x2F); // EOC
        return new AncillaryPacket(0x61, 0x02, (ushort)Math.Clamp(s.CaptionVancLine, 1, 2047), payload.ToArray(), "CEA-608");
    }

    /// <summary>Builds an SMPTE-334 style CDP carrying cc_data. The selected 708 service/window is represented in DTVCC service data.</summary>
    public static AncillaryPacket BuildCea708Cdp(ProfessionalBroadcastSettings s, PlaylistItem item, double fps)
    {
        var dtvcc = BuildDtvccService(item);
        var ccTriplets = new List<byte>();
        for (var i = 0; i < dtvcc.Length; i += 2)
        {
            var a = dtvcc[i]; var b = i + 1 < dtvcc.Length ? dtvcc[i + 1] : (byte)0;
            // cc_valid=1. The first pair starts a DTVCC packet (cc_type=3); subsequent pairs continue it (cc_type=2).
            ccTriplets.Add(i == 0 ? (byte)0xFF : (byte)0xFE);
            ccTriplets.Add(a); ccTriplets.Add(b);
        }
        var count = Math.Min(31, ccTriplets.Count / 3); if (count * 3 < ccTriplets.Count) ccTriplets = ccTriplets.Take(count * 3).ToList();
        var cdp = new List<byte> { 0x96, 0x69, 0x00, MapCdpFrameRate(fps), 0x43, 0x00, 0x00, 0x72, (byte)(0xE0 | count) };
        cdp.AddRange(ccTriplets); cdp.Add(0x74); cdp.Add(0); cdp.Add(0); cdp[2] = (byte)(cdp.Count + 1); cdp.Add(CdpChecksum(cdp));
        return new AncillaryPacket(0x61, 0x01, (ushort)Math.Clamp(s.CaptionVancLine, 1, 2047), cdp.ToArray(), "CEA-708-CDP");
    }

    private static byte[] BuildDtvccService(PlaylistItem item)
    {
        var text = new string((item.CaptionText ?? string.Empty).Where(ch => ch >= 0x20 && ch <= 0x7E).Take(48).Select(ch => (char)ch).ToArray());
        var block = new List<byte>();
        var window = Math.Clamp(item.Caption708Window, 0, 7);
        var row = Math.Clamp(item.Caption708Row, 0, 14);
        var column = Math.Clamp(item.Caption708Column, 0, 31);

        // DefineWindowN (DF0..DF7): visible window, unlocked rows/columns, relative positioning disabled,
        // anchor at the requested caption cell, 4 rows x 32 columns, default pen/window styles.
        block.Add((byte)(0x98 + window));
        block.Add(0x20);                                      // priority=0, col_lock=0, row_lock=0, visible=1
        block.Add((byte)Math.Clamp(row * 5, 0, 74));          // anchor_vertical
        block.Add((byte)Math.Clamp(column * 3, 0, 209));     // relative=0 + anchor_horizontal
        block.Add(0x00);                                      // anchor_id=0, row_count=1 encoded as 0
        block.Add(0x1F);                                      // column_count=32 encoded as 31
        block.Add(0x00);                                      // pen_style=0, window_style=0

        block.Add((byte)(0x80 | window));                    // SetCurrentWindow
        block.Add(0x92); block.Add((byte)row); block.Add((byte)column); // SetPenLocation
        block.AddRange(System.Text.Encoding.ASCII.GetBytes(text));
        block.Add(0x89); block.Add((byte)(1 << window));     // DisplayWindows bitmap
        var service = Math.Clamp(item.Caption708ServiceNumber, 1, 63);
        var serviceData = new List<byte>();
        if (service <= 6) serviceData.Add((byte)((service << 5) | Math.Min(31, block.Count)));
        else { serviceData.Add((byte)(0xE0 | Math.Min(31, block.Count + 1))); serviceData.Add((byte)(service & 0x3F)); }
        serviceData.AddRange(block.Take(31));
        var packetSize = Math.Min(63, (serviceData.Count + 1) / 2); var dtvcc = new List<byte> { (byte)(packetSize & 0x3F) }; dtvcc.AddRange(serviceData); return dtvcc.ToArray();
    }

    private static IEnumerable<string> Wrap608(string text)
    {
        var normalized = (text ?? string.Empty).Replace("\r", string.Empty); foreach (var raw in normalized.Split('\n')) { var line = raw; if (line.Length == 0) { yield return string.Empty; continue; } while (line.Length > 32) { var take = line.LastIndexOf(' ', Math.Min(31, line.Length - 1)); if (take < 8) take = 32; yield return line[..take].TrimEnd(); line = line[take..].TrimStart(); } yield return line; }
    }
    private static void AddPac(List<byte> p, byte channelBase, int row, bool underline, bool italics)
    {
        // PAC row map for rows 1..15. Attributes use the common white/italics forms.
        byte[] rowCodes = [0x40,0x60,0x40,0x60,0x40,0x60,0x40,0x60,0x40,0x60,0x40,0x60,0x40,0x60,0x40];
        var first = (byte)((channelBase & 0x08) != 0 ? 0x18 : 0x10); first += (byte)(((Math.Clamp(row,1,15)-1)/2) & 0x07);
        var second = (byte)(rowCodes[Math.Clamp(row,1,15)-1] | (italics ? 0x0E : 0x00) | (underline ? 0x01 : 0x00));
        AddPair(p, first, second);
    }
    private static void AddPair(List<byte> p, byte a, byte b) { p.Add(WithOddParity(a)); p.Add(WithOddParity(b)); }
    private static void AddControlPair(List<byte> p, byte a, byte b)
    {
        // CEA-608 receivers de-duplicate repeated non-printing control pairs; send each command twice for reliability.
        AddPair(p, a, b); AddPair(p, a, b);
    }
    private static byte Scte104SpliceType(string text) => (text ?? string.Empty).ToUpperInvariant() switch { "START IMMEDIATE" => 2, "END NORMAL" => 3, "END IMMEDIATE" => 4, "CANCEL" => 5, _ => 1 };
    private static byte MapCdpFrameRate(double fps) => fps switch { >= 59 => 0x80, >= 49 => 0x70, >= 29.5 => 0x50, >= 25.5 => 0x40, >= 24.5 => 0x30, >= 23.5 => 0x20, _ => 0x10 };
    private static byte CdpChecksum(IEnumerable<byte> bytes) { var sum = bytes.Sum(x => (int)x) & 0xff; return (byte)((256 - sum) & 0xff); }
    private static byte WithOddParity(byte b) { b &= 0x7f; var ones = 0; for (var i = 0; i < 7; i++) ones += (b >> i) & 1; return (byte)(b | ((ones & 1) == 0 ? 0x80 : 0)); }
}

public sealed class AncillaryQueue
{
    private readonly object _gate = new(); private readonly Queue<AncillaryPacket> _q = new();
    public void Enqueue(AncillaryPacket p) { lock (_gate) { _q.Enqueue(p); while (_q.Count > 128) _q.Dequeue(); } }
    public AncillaryPacket[] Drain() { lock (_gate) { var a = _q.ToArray(); _q.Clear(); return a; } }
}
