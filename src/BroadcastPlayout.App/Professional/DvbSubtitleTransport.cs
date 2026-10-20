using System.Net;
using System.Net.Sockets;
using System.Text;
using BroadcastPlayout.Models;

namespace BroadcastPlayout.Professional;

/// <summary>
/// DVB subtitle MPEG-TS sender. Emits PAT/PMT with a DVB subtitling_descriptor and a PES display set
/// using EN 300 743 character-coded objects. Character-coded objects require the receiver/broadcaster
/// to share the character table/font agreement; bitmap authoring can be added as an alternate renderer.
/// </summary>
public sealed class DvbSubtitleTsSender : IDisposable
{
    private readonly UdpClient _udp = new(); private int _ccPat, _ccPmt, _ccSub; private byte _version;
    public string LastError { get; private set; } = string.Empty;

    public void SendText(ProfessionalBroadcastSettings s, PlaylistItem item, long pts90k)
    {
        if (!s.EnableDvbSubtitles) return;
        try
        {
            var ep = new IPEndPoint(IPAddress.Parse(s.Scte35TsDestination), s.Scte35TsPort);
            var pid = (ushort)s.DvbSubtitlePid; const ushort program = 1; const ushort pmtPid = 0x1200;
            foreach (var p in PacketizePsi(BuildPat(program, pmtPid), 0, ref _ccPat)) _udp.Send(p, p.Length, ep);
            foreach (var p in PacketizePsi(BuildPmt(program, pid, item), pmtPid, ref _ccPmt)) _udp.Send(p, p.Length, ep);
            var displaySet = BuildDisplaySet(item, _version++);
            var pes = BuildPes(displaySet, pts90k);
            foreach (var p in PacketizePes(pes, pid, ref _ccSub)) _udp.Send(p, p.Length, ep);
            LastError = string.Empty;
        }
        catch (Exception ex) { LastError = ex.Message; }
    }

    private static byte[] BuildDisplaySet(PlaylistItem item, byte version)
    {
        var pageId = (ushort)item.DvbCompositionPageId;
        var data = new List<byte> { 0x20, 0x00 }; // data_identifier, subtitle_stream_id
        data.AddRange(Segment(0x10, pageId, BuildPageComposition(item, version)));
        data.AddRange(Segment(0x11, pageId, BuildRegionComposition(version)));
        data.AddRange(Segment(0x13, pageId, BuildCharacterObject(item, version)));
        data.AddRange(Segment(0x80, pageId, [])); // end_of_display_set_segment
        data.Add(0xFF); // stuffing byte allowed at PES end
        return data.ToArray();
    }
    private static byte[] BuildPageComposition(PlaylistItem item, byte version)
    {
        var b = new List<byte> { (byte)item.DvbTimeoutSeconds, (byte)(((version & 0x0F) << 4) | 0x07), 0x01, 0xFF };
        const ushort x = 160, y = 850;
        b.Add((byte)(x >> 8)); b.Add((byte)(x & 0xFF));
        b.Add((byte)(y >> 8)); b.Add((byte)(y & 0xFF));
        return b.ToArray();
    }
    private static byte[] BuildRegionComposition(byte version)
    {
        const ushort width = 1600, height = 150, objectId = 1; const ushort x = 0, y = 0;
        var b = new List<byte> { 0x01, (byte)(((version & 0x0F) << 4) | 0x0F), (byte)(width >> 8), (byte)(width & 0xFF), (byte)(height >> 8), (byte)(height & 0xFF),
            0x4F, 0x00, 0x00, 0x00, (byte)(objectId >> 8), (byte)objectId,
            (byte)(0x40 | ((x >> 8) & 0x0F)), (byte)x, (byte)(0xF0 | ((y >> 8) & 0x0F)), (byte)y, 0x01, 0x00 };
        return b.ToArray();
    }
    private static byte[] BuildCharacterObject(PlaylistItem item, byte version)
    {
        const ushort objectId = 1; var chars = (item.CaptionText ?? string.Empty).Take(255).ToArray();
        var b = new List<byte> { (byte)(objectId >> 8), (byte)objectId, (byte)(((version & 0x0F) << 4) | 0x05), (byte)chars.Length };
        foreach (var ch in chars) { b.Add((byte)(ch >> 8)); b.Add((byte)ch); }
        return b.ToArray();
    }
    private static byte[] Segment(byte type, ushort pageId, byte[] body)
    {
        var b = new List<byte> { 0x0F, type, (byte)(pageId >> 8), (byte)pageId, (byte)(body.Length >> 8), (byte)body.Length }; b.AddRange(body); return b.ToArray();
    }

    private static byte[] BuildPat(ushort program, ushort pmtPid)
    {
        var b = new List<byte> { 0x00,0xB0,0x0D,0x00,0x01,0xC1,0x00,0x00,(byte)(program>>8),(byte)program,(byte)(0xE0|((pmtPid>>8)&0x1F)),(byte)pmtPid };
        AddCrc(b); return b.ToArray();
    }
    private static byte[] BuildPmt(ushort program, ushort subPid, PlaylistItem item)
    {
        var lang = Encoding.ASCII.GetBytes((item.DvbLanguage ?? "eng").PadRight(3).Substring(0, 3));
        var desc = new byte[] { 0x59, 0x08, lang[0], lang[1], lang[2], (byte)item.DvbSubtitlingType,
            (byte)(item.DvbCompositionPageId >> 8), (byte)item.DvbCompositionPageId, (byte)(item.DvbAncillaryPageId >> 8), (byte)item.DvbAncillaryPageId };
        var esInfoLen = desc.Length;
        var sectionLength = 9 + 5 + esInfoLen + 4;
        var b = new List<byte> { 0x02, (byte)(0xB0 | ((sectionLength >> 8) & 0x0F)), (byte)sectionLength, (byte)(program >> 8), (byte)program, 0xC1, 0, 0,
            (byte)(0xE0 | ((subPid >> 8) & 0x1F)), (byte)subPid, 0xF0, 0x00, 0x06, (byte)(0xE0 | ((subPid >> 8) & 0x1F)), (byte)subPid,
            (byte)(0xF0 | ((esInfoLen >> 8) & 0x0F)), (byte)esInfoLen };
        b.AddRange(desc); AddCrc(b); return b.ToArray();
    }
    private static void AddCrc(List<byte> b) { var crc = Scte35Codec.MpegCrc32(b.ToArray()); b.Add((byte)(crc >> 24)); b.Add((byte)(crc >> 16)); b.Add((byte)(crc >> 8)); b.Add((byte)crc); }

    private static byte[] BuildPes(byte[] payload, long pts)
    {
        var h = new List<byte> { 0x00,0x00,0x01,0xBD,0x00,0x00,0x80,0x80,0x05 }; var p = pts & 0x1FFFFFFFFL;
        var pts32To29 = (int)((p >> 29) & 0x0EL);
        var pts21To15 = (int)((p >> 14) & 0xFEL);
        var pts6To0 = (int)((p << 1) & 0xFEL);
        h.Add((byte)(0x21 | pts32To29)); h.Add((byte)(p >> 22));
        h.Add((byte)(0x01 | pts21To15)); h.Add((byte)(p >> 7));
        h.Add((byte)(0x01 | pts6To0)); h.AddRange(payload);
        var len = h.Count - 6; h[4] = (byte)(len >> 8); h[5] = (byte)len; return h.ToArray();
    }
    private static IReadOnlyList<byte[]> PacketizePsi(byte[] section, ushort pid, ref int cc)
    {
        var packets = new List<byte[]>(); var o = 0; var first = true;
        while (o < section.Length) { var p = Enumerable.Repeat((byte)0xff, 188).ToArray(); p[0] = 0x47; p[1] = (byte)((first ? 0x40 : 0) | ((pid >> 8) & 0x1f)); p[2] = (byte)pid; p[3] = (byte)(0x10 | (cc++ & 0xf)); var at = 4; if (first) p[at++] = 0; var n = Math.Min(188 - at, section.Length - o); Buffer.BlockCopy(section, o, p, at, n); o += n; first = false; packets.Add(p); } return packets;
    }
    private static IReadOnlyList<byte[]> PacketizePes(byte[] pes, ushort pid, ref int cc)
    {
        var packets = new List<byte[]>(); var o = 0; var first = true;
        while (o < pes.Length) { var p = Enumerable.Repeat((byte)0xff, 188).ToArray(); p[0] = 0x47; p[1] = (byte)((first ? 0x40 : 0) | ((pid >> 8) & 0x1f)); p[2] = (byte)pid; p[3] = (byte)(0x10 | (cc++ & 0xf)); var n = Math.Min(184, pes.Length - o); Buffer.BlockCopy(pes, o, p, 4, n); o += n; first = false; packets.Add(p); } return packets;
    }
    public void Dispose() => _udp.Dispose();
}
