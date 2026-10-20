using System.Net;
using System.Net.Sockets;
using BroadcastPlayout.Models;

namespace BroadcastPlayout.Professional;

/// <summary>
/// Test/acceptance ST 2110 sender. Video is RFC4175-shaped YCbCr 4:2:2 8-bit RTP,
/// audio is big-endian 24-bit PCM at 48 kHz, ANC is carried on a dedicated RTP flow.
/// ST 2022-7 duplicates identical RTP packets to A/B destinations.
/// </summary>
public sealed class St2110Runtime : IDisposable
{
    private readonly UdpClient _videoA=new(),_videoB=new(),_audioA=new(),_audioB=new(),_ancA=new(),_ancB=new();
    private ushort _vSeq,_aSeq,_ancSeq; private uint _vSsrc=0x4B545831,_aSsrc=0x4B545832,_ancSsrc=0x4B545833; private uint _audioTs;
    public string LastError { get; private set; }=string.Empty;
    public bool Enabled { get; set; }

    public void SubmitVideo(VideoFrameData frame, ProfessionalBroadcastSettings s)
    {
        if(!Enabled||!s.EnableSt2110)return;
        try{
            var a=Parse(s.St2110VideoDestinationA,s.St2110VideoPort); var b=Parse(s.St2110VideoDestinationB,s.St2110VideoPort);
            var ts=(uint)Math.Max(0,Math.Round(frame.PtsSeconds*90000)); var mtu=Math.Clamp(s.St2110Mtu,600,9000);
            var rowBytes=frame.Width*2; var uyvy=new byte[rowBytes];
            for(var y=0;y<frame.Height;y++){
                ConvertBgraRowToUyvy(frame,y,uyvy);
                var off=0;
                while(off<uyvy.Length){
                    var chunk=Math.Min(mtu-20,uyvy.Length-off); chunk-=chunk%4; if(chunk<=0)break;
                    var payload=new byte[8+chunk];
                    // Extended sequence + RFC4175 line header: length, line, offset.
                    payload[0]=(byte)(_vSeq>>8);payload[1]=(byte)_vSeq;
                    payload[2]=(byte)(chunk>>8);payload[3]=(byte)chunk;
                    payload[4]=(byte)(y>>8);payload[5]=(byte)y;
                    payload[6]=(byte)(off>>8);payload[7]=(byte)off;
                    Buffer.BlockCopy(uyvy,off,payload,8,chunk);
                    var marker=y==frame.Height-1&&off+chunk>=uyvy.Length;
                    var packet=Rtp((byte)s.St2110VideoPayloadType,_vSeq++,ts,_vSsrc,payload,marker);
                    Send(packet,a,_videoA); if(s.EnableSt2022_7)Send(packet,b,_videoB); off+=chunk;
                }
            } LastError=string.Empty;
        }catch(Exception ex){LastError=ex.Message;}
    }

    public void SubmitAudio(AudioChunk chunk, ProfessionalBroadcastSettings s)
    {
        if(!Enabled||!s.EnableSt2110||chunk.Pcm16Stereo48k.Length<4)return;
        try{
            var a=Parse(s.St2110AudioDestinationA,s.St2110AudioPort);var b=Parse(s.St2110AudioDestinationB,s.St2110AudioPort);
            var frames=chunk.Pcm16Stereo48k.Length/4; var payload=new byte[frames*6];
            for(var i=0;i<frames*2;i++){short v=(short)(chunk.Pcm16Stereo48k[i*2]|(chunk.Pcm16Stereo48k[i*2+1]<<8));int v24=v<<8;payload[i*3]=(byte)(v24>>16);payload[i*3+1]=(byte)(v24>>8);payload[i*3+2]=(byte)v24;}
            var packet=Rtp((byte)s.St2110AudioPayloadType,_aSeq++,_audioTs,_aSsrc,payload,true);_audioTs+=(uint)frames;
            Send(packet,a,_audioA);if(s.EnableSt2022_7)Send(packet,b,_audioB);LastError=string.Empty;
        }catch(Exception ex){LastError=ex.Message;}
    }

    public void SubmitAncillary(IEnumerable<AncillaryPacket> packets, ProfessionalBroadcastSettings s, uint timestamp90k)
    {
        if(!Enabled||!s.EnableSt2110)return;
        try{var a=Parse(s.St2110AncDestinationA,s.St2110AncPort);var b=Parse(s.St2110AncDestinationB,s.St2110AncPort);
            foreach(var p in packets){var payload=new byte[6+p.UserData.Length];payload[0]=p.Did;payload[1]=p.Sdid;payload[2]=(byte)(p.Line>>8);payload[3]=(byte)p.Line;payload[4]=(byte)(p.UserData.Length>>8);payload[5]=(byte)p.UserData.Length;Buffer.BlockCopy(p.UserData,0,payload,6,p.UserData.Length);var packet=Rtp((byte)s.St2110AncPayloadType,_ancSeq++,timestamp90k,_ancSsrc,payload,true);Send(packet,a,_ancA);if(s.EnableSt2022_7)Send(packet,b,_ancB);}LastError=string.Empty;
        }catch(Exception ex){LastError=ex.Message;}
    }

    public string BuildVideoSdp(ProfessionalBroadcastSettings s,int width,int height,double fps)=>$"v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=Kashtrix ST2110-20\r\nc=IN IP4 {s.St2110VideoDestinationA}/32\r\nt=0 0\r\nm=video {s.St2110VideoPort} RTP/AVP {s.St2110VideoPayloadType}\r\na=rtpmap:{s.St2110VideoPayloadType} raw/90000\r\na=fmtp:{s.St2110VideoPayloadType} sampling=YCbCr-4:2:2; width={width}; height={height}; exactframerate={fps:0.###}; depth=8; colorimetry=BT709; PM=2110GPM; SSN=ST2110-20:2017\r\n";

    private static IPEndPoint Parse(string ip,int port)=>new(IPAddress.Parse(ip),Math.Clamp(port,1,65535));
    private static void Send(byte[] b,IPEndPoint ep,UdpClient u)=>u.Send(b,b.Length,ep);
    private static byte[] Rtp(byte pt,ushort seq,uint ts,uint ssrc,byte[] payload,bool marker){var b=new byte[12+payload.Length];b[0]=0x80;b[1]=(byte)((marker?0x80:0)|(pt&0x7f));b[2]=(byte)(seq>>8);b[3]=(byte)seq;b[4]=(byte)(ts>>24);b[5]=(byte)(ts>>16);b[6]=(byte)(ts>>8);b[7]=(byte)ts;b[8]=(byte)(ssrc>>24);b[9]=(byte)(ssrc>>16);b[10]=(byte)(ssrc>>8);b[11]=(byte)ssrc;Buffer.BlockCopy(payload,0,b,12,payload.Length);return b;}
    private static void ConvertBgraRowToUyvy(VideoFrameData f,int y,byte[] dst){var s=y*f.Stride;for(var x=0;x<f.Width;x+=2){var i0=s+x*4;var i1=s+Math.Min(x+1,f.Width-1)*4;RgbToYuv(f.Bgra[i0+2],f.Bgra[i0+1],f.Bgra[i0],out var y0,out var u0,out var v0);RgbToYuv(f.Bgra[i1+2],f.Bgra[i1+1],f.Bgra[i1],out var y1,out var u1,out var v1);var o=x*2;dst[o]=(byte)((u0+u1)/2);dst[o+1]=y0;dst[o+2]=(byte)((v0+v1)/2);dst[o+3]=y1;}}
    private static void RgbToYuv(byte r,byte g,byte b,out byte y,out byte u,out byte v){var yy=(int)(16+0.183*r+0.614*g+0.062*b);var uu=(int)(128-0.101*r-0.339*g+0.439*b);var vv=(int)(128+0.439*r-0.399*g-0.040*b);y=(byte)Math.Clamp(yy,16,235);u=(byte)Math.Clamp(uu,16,240);v=(byte)Math.Clamp(vv,16,240);}
    public void Dispose(){_videoA.Dispose();_videoB.Dispose();_audioA.Dispose();_audioB.Dispose();_ancA.Dispose();_ancB.Dispose();}
}
